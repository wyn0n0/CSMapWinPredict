"""Bounded CUDA BF16/NF4 checks of the actual loss helper and checkpointed SDPA."""
import argparse
from datetime import datetime, timezone
import json
import re
import sys
import traceback

from situation_dependency_profile import verify_installed_profile
from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json
from situation_training_memory import attention_evidence, projection_kwargs, sdpa_context


def check_precision(precision):
    import torch
    from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training
    from transformers import Qwen3Config, Qwen3ForCausalLM
    torch.manual_seed(42)
    torch.cuda.manual_seed_all(42)
    config = Qwen3Config(vocab_size=257, hidden_size=256, intermediate_size=512,
        num_hidden_layers=2, num_attention_heads=2, num_key_value_heads=1,
        head_dim=128, tie_word_embeddings=True, use_cache=False,
        attention_dropout=0.0, eos_token_id=2)
    config._attn_implementation = 'sdpa'
    model = Qwen3ForCausalLM(config).to(dtype=torch.bfloat16)
    quantized = 0
    if precision == 'nf4':
        import bitsandbytes as bnb
        head = model.get_output_embeddings()
        for name, layer in list(model.named_modules()):
            if isinstance(layer, torch.nn.Linear) and layer is not head:
                replacement = bnb.nn.Linear4bit(layer.in_features, layer.out_features,
                    bias=False, compute_dtype=torch.bfloat16, quant_type='nf4', compress_statistics=True)
                replacement.weight = bnb.nn.Params4bit(layer.weight.detach().clone(), requires_grad=False,
                    quant_type='nf4', compress_statistics=True)
                parent, _, child = name.rpartition('.')
                setattr(model.get_submodule(parent), child, replacement)
                quantized += 1
        model.is_loaded_in_4bit = True
    model = model.to('cuda')
    if precision == 'nf4':
        model = prepare_model_for_kbit_training(model, use_gradient_checkpointing=True,
            gradient_checkpointing_kwargs={'use_reentrant': False})
        assert quantized == 14
        assert all(m.weight.quant_state.nested and m.weight.quant_type == 'nf4'
                   for m in model.modules() if isinstance(m, bnb.nn.Linear4bit))
    model = get_peft_model(model, LoraConfig(task_type='CAUSAL_LM', r=16, lora_alpha=32,
        lora_dropout=0.05, target_modules='all-linear', bias='none'))
    model.gradient_checkpointing_enable(gradient_checkpointing_kwargs={'use_reentrant': False})
    with torch.no_grad():
        for name, parameter in model.named_parameters():
            if 'lora_B' in name:
                parameter.normal_(0, 0.01)
    model.train()
    ids = torch.randint(3, 257, (1, 257), device='cuda')
    ids[0, -2:] = torch.tensor([2, 7], device='cuda')  # EOS plus template suffix.
    labels = ids.clone()
    labels[:, :190] = -100
    batch = dict(input_ids=ids, labels=labels, attention_mask=torch.ones_like(ids))
    original = {k: v.clone() for k, v in batch.items()}
    cpu_rng, gpu_rng = torch.get_rng_state(), torch.cuda.get_rng_state()
    runs = []
    references = {}
    for mode, backend in [('full', 'auto'), ('assistant-positions', 'auto'), ('assistant-positions', 'cudnn')]:
        model.zero_grad(set_to_none=True)
        torch.set_rng_state(cpu_rng)
        torch.cuda.set_rng_state(gpu_rng)
        kwargs = projection_kwargs(labels, mode)
        with sdpa_context(backend), torch.profiler.profile(activities=[torch.profiler.ProfilerActivity.CPU]) as profiler:
            with torch.autocast('cuda', dtype=torch.bfloat16):
                output = model(**batch, **kwargs, use_cache=False)
            value = output.loss.item()
            output.loss.backward()
            torch.cuda.synchronize()
        assert bool(torch.isfinite(output.loss))
        grads = {}
        for name, parameter in model.named_parameters():
            if parameter.requires_grad:
                assert parameter.grad is not None and bool(torch.isfinite(parameter.grad).all())
                grads[name] = parameter.grad.detach().float().cpu().clone()
            else:
                assert parameter.grad is None
        assert all(torch.equal(batch[k], original[k]) for k in batch)
        row = dict(lossImplementation=mode, sdpaBackend=backend, loss=value,
            projectedTokens=output.logits.shape[1], totalTokens=ids.shape[1],
            supervisedTokens=int((labels != -100).sum()), adapterGradientTensors=len(grads),
            inputUnchanged=True, baseGradientsAbsent=True, finite=True,
            attention=attention_evidence(profiler, backend))
        if mode == 'full':
            references['full'] = (value, grads)
        else:
            reference = references['full'] if backend == 'auto' else references['assistant-auto']
            row['lossAbsoluteDifference'] = abs(value - reference[0])
            row['maxAdapterGradientRelativeL2Error'] = max(
                (grads[n] - reference[1][n]).norm().item() / max(reference[1][n].norm().item(), 1e-10)
                for n in grads)
            row['lossRelativeTolerance'] = 0.003
            row['gradientRelativeL2Tolerance'] = 0.02 if backend == 'auto' else 0.04
            assert row['lossAbsoluteDifference'] <= 0.003 * abs(reference[0])
            assert row['maxAdapterGradientRelativeL2Error'] <= row['gradientRelativeL2Tolerance']
            if backend == 'auto':
                references['assistant-auto'] = (value, grads)
        runs.append(row)
        del output, kwargs
    del model, batch, original, references, grads
    torch.cuda.empty_cache()
    return dict(precision=precision, quantizedLinearModules=quantized, status='passed', runs=runs)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-id', required=True)
    args = parser.parse_args()
    if not re.fullmatch(r'[a-z0-9-]{1,96}', args.run_id):
        raise ValueError('invalid-run-id')
    root = storage_root(read_json(CONFIG)['storageRoot'])
    out = safe_child(root, 'reports/' + args.run_id)
    out.mkdir(parents=True, exist_ok=False)
    report = dict(schemaVersion='situation-training-memory-verification-v1', status='incomplete',
        trainingReady=False, resourceReady=False, adapterSaved=False,
        scope='tiny random CUDA models; equivalence/backend checks only, no pretrained base or dev/test',
        startedUtc=datetime.now(timezone.utc).isoformat(), dependencyProfile=verify_installed_profile(),
        codeSha256={p: digest(REPO / p) for p in ('tools/verify_situation_training_memory.py', 'tools/situation_training_memory.py')},
        precisions=[])
    write_new_json(out / 'started.json', report)
    try:
        import torch
        if not torch.cuda.is_available() or not torch.cuda.is_bf16_supported(including_emulation=False):
            raise ValueError('native-bf16-cuda-required')
        torch.set_num_threads(2)
        report.update(torch=str(torch.__version__), gpu=torch.cuda.get_device_name(0))
        for precision in ('bf16', 'nf4'):
            report['precisions'].append(check_precision(precision))
        report.update(status='passed', exitCode=0)
    except Exception as exc:
        report.update(status='failed', exitCode=1, error=dict(type=type(exc).__name__,
            message=str(exc), traceback=traceback.format_exc()))
    report['finishedUtc'] = datetime.now(timezone.utc).isoformat()
    write_new_json(out / 'verification.json', report)
    print(json.dumps(dict(status=report['status'], precisions=report['precisions'], error=report.get('error')), indent=2))
    return report['exitCode']


if __name__ == '__main__':
    sys.exit(main())
