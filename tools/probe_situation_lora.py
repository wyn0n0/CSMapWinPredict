"""Two-step offline CUDA LoRA resource probe; never save a trained adapter."""
import argparse
from collections import Counter
from contextlib import nullcontext
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import traceback

from situation_token_measurement import sha_text
from prepare_situation_base import destination, reference, require, verify_base
from probe_situation_cuda import memory
from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json
from situation_lossless_table import canonical
from situation_probe_inputs import assistant_labels, choose_rows
from situation_dependency_profile import verify_installed_profile
from situation_training_memory import attention_evidence, projection_kwargs, sdpa_context


def smi():
    fields = 'index,uuid,name,driver_version,memory.total,memory.used,memory.free'
    result = subprocess.run(['nvidia-smi', '--query-gpu=' + fields, '--format=csv,noheader,nounits'],
                            capture_output=True, text=True, timeout=20)
    apps = subprocess.run(['nvidia-smi', '--query-compute-apps=pid,process_name,used_gpu_memory', '--format=csv'],
                          capture_output=True, text=True, timeout=20)
    value = dict(exitCode=result.returncode, gpu=result.stdout.strip(), applications=apps.stdout.strip())
    if result.returncode == 0:
        value['freeBytes'] = int(result.stdout.strip().splitlines()[0].split(',')[-1].strip()) * 1024**2
    return value


def prepare_inputs(root, cfg, report):
    run = safe_child(root, 'reports/' + cfg['budgetRun'])
    require(digest(run / 'token-budget-report.json') == cfg['budgetReportSha256'], 'budget-report-drift')
    budget = read_json(run / 'token-budget-report.json')
    require(budget['inputBudgetReady'] and budget['fullTrainValidated'], 'input-budget-not-ready')
    require(budget['policy']['selectedBudget'] == cfg['maxSequenceLength'], 'sequence-budget-mismatch')
    require(digest(REPO / 'situation-implementation/situation-token-budget-v2.json') == budget['policySha256'], 'policy-drift')
    require(digest(REPO / budget['policy']['representationConfig']) == budget['policy']['representationConfigSha256'], 'prompt-drift')
    for name, expected in budget['codeSha256'].items():
        require(digest(REPO / name) == expected, 'budget-code-drift')
    for name, expected in budget['representations']['full-train']['artifacts'].items():
        require(digest(run / name) == expected, 'budget-artifact-drift')
    source = next(d for d in budget['policy']['datasets'] if d['role'] == 'full-train')
    require(digest(safe_child(root, 'datasets/' + source['name'] + '/train.jsonl')) == source['trainSha256'], 'source-train-drift')
    rows = [json.loads(line) for line in (run / 'full-train-token-lengths.jsonl').read_text(encoding='utf-8').splitlines()]
    selected = choose_rows(rows, cfg['gradientAccumulationSteps'])
    wanted = {r['sampleId'] for r in selected}
    records = {}
    with (run / 'full-train-model-inputs.jsonl').open(encoding='utf-8') as handle:
        for line in handle:
            row = json.loads(line)
            if row['sampleId'] in wanted:
                records[row['sampleId']] = row
    require(set(records) == wanted, 'selected-input-missing')
    report['inputs'] = dict(budgetReportSha256=cfg['budgetReportSha256'], trainSha256=source['trainSha256'],
        representationVersion=budget['promptConfig']['representationVersion'], selectedRows=selected,
        selectedInputSha256=sha_text(canonical([records[r['sampleId']] for r in selected])),
        selection='max total/input/completion then greedy phase/tag coverage then longest; train-only',
        devTestRead=False, syntheticSequenceLength=cfg['maxSequenceLength'])
    return budget, selected, records


def probe(out, report, cfg, args):
    local = read_json(CONFIG)
    require(Path(sys.executable).resolve() == Path(local['python']).resolve(), 'wrong-interpreter')
    root = storage_root(local['storageRoot'])
    require(os.environ.get('SITUATION_STORAGE_ROOT') == str(root), 'use-run-situation-launcher')
    require(cfg['optimizerSteps'] == 2 and cfg['microBatchSize'] == 1 and cfg['gradientAccumulationSteps'] == 16, 'unsafe-probe-size')
    profile = verify_installed_profile()
    require(args.precision != 'nf4' or profile['profile'] == 'requirements-situation-win-cu126-qlora',
            'nf4-requires-approved-qlora-profile')
    installed = profile['installedPackages']
    report.update(lockSha256=profile['lockSha256'], dependencyProfile=profile,
                  installedPackages=installed, phase='verify-inputs')
    budget, selected, records = prepare_inputs(root, cfg, report)
    report['phase'] = 'verify-base'
    ref = reference()
    base = destination(root, ref)
    manifest = verify_base(base, ref)
    report.update(modelId=ref['modelId'], revision=ref['revision'], baseManifestSha256=digest(base / 'base-manifest.json'))
    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig
    from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training
    require(torch.cuda.is_available() and torch.cuda.is_bf16_supported(including_emulation=False), 'native-bf16-cuda-required')
    torch.cuda.set_device(0)
    torch.manual_seed(cfg['seed']); torch.cuda.manual_seed_all(cfg['seed'])
    torch.set_num_threads(4)
    torch.cuda.reset_peak_memory_stats(0)
    report.update(gpu=torch.cuda.get_device_name(0), torch=str(torch.__version__), cudaRuntime=torch.version.cuda,
                  memoryBefore=memory(torch), nvidiaSmiBefore=smi())
    require(report['nvidiaSmiBefore']['exitCode'] == 0, 'nvidia-smi-failed')
    tokenizer = AutoTokenizer.from_pretrained(str(base), local_files_only=True, trust_remote_code=False)
    require(sha_text(tokenizer.chat_template) == manifest['chatTemplateSha256'], 'template-drift')
    batches = []
    for row in selected:
        record = records[row['sampleId']]
        user, target = canonical(record['facts']), canonical(record['target'])
        messages = [dict(role='system', content=budget['promptConfig']['system']), dict(role='user', content=user)]
        prompt = tokenizer.apply_chat_template(messages, tokenize=False, add_generation_prompt=True, enable_thinking=False)
        full = tokenizer.apply_chat_template(messages + [dict(role='assistant', content=target)], tokenize=False,
                                             add_generation_prompt=False, enable_thinking=False)
        ids = tokenizer.encode(full, add_special_tokens=False)
        prefix = tokenizer.encode(prompt, add_special_tokens=False)
        require(sha_text(full) == row['trainingTextSha256'] and len(ids) == row['totalTokens'], 'probe-render-drift')
        labels = assistant_labels(ids, prefix, tokenizer.eos_token_id, cfg['maxSequenceLength'])
        batches.append((ids, labels, row['sampleId']))
    # Pure synthetic stress sequence, not a truncated or padded real example.
    text_ids = tokenizer.encode('这是合成资源测试的已知事实与中文摘要。', add_special_tokens=False)
    n = cfg['maxSequenceLength']
    synthetic = (text_ids * ((n // len(text_ids)) + 1))[:n - 1] + [tokenizer.eos_token_id]
    synth_labels = assistant_labels(synthetic, synthetic[:n - 512], tokenizer.eos_token_id, n)
    schedule = [(synthetic, synth_labels, 'synthetic-2560')] * 16 + batches
    report['phase'] = 'load-model'
    kwargs = dict(local_files_only=True, trust_remote_code=False, use_safetensors=True, dtype=torch.bfloat16,
                  device_map={'': 'cuda:0'}, attn_implementation=cfg['attentionImplementation'])
    if args.precision == 'nf4':
        kwargs['quantization_config'] = BitsAndBytesConfig(load_in_4bit=True, bnb_4bit_quant_type='nf4',
            bnb_4bit_use_double_quant=True, bnb_4bit_compute_dtype=torch.bfloat16)
    model, loading = AutoModelForCausalLM.from_pretrained(str(base), output_loading_info=True, **kwargs)
    require(not any(loading.get(k) for k in ('missing_keys', 'unexpected_keys', 'mismatched_keys', 'error_msgs')), 'checkpoint-load-incomplete')
    report['memoryAfterBaseLoad'] = memory(torch)
    if args.precision == 'nf4':
        model = prepare_model_for_kbit_training(model, use_gradient_checkpointing=True,
                                               gradient_checkpointing_kwargs={'use_reentrant': False})
        import bitsandbytes as bnb
        modules = [(name, m) for name, m in model.named_modules() if isinstance(m, bnb.nn.Linear4bit)]
        require(bool(modules), 'no-4bit-modules')
        require(all(m.weight.quant_type == 'nf4' and m.weight.compress_statistics and
                    m.compute_dtype == torch.bfloat16 and m.weight.quant_state is not None and
                    m.weight.quant_state.nested and m.weight.quant_state.absmax.device.type == 'cuda'
                    for _, m in modules), 'nf4-double-quant-not-active')
        report['quantization'] = dict(type='nf4', doubleQuant=True, computeDtype='torch.bfloat16',
                                      linear4bitModules=len(modules), bitsandbytes=installed['bitsandbytes'])
    model.config.use_cache = False
    model = get_peft_model(model, LoraConfig(task_type='CAUSAL_LM', r=args.rank, lora_alpha=cfg['alpha'],
        lora_dropout=cfg['dropout'], target_modules=cfg['targetModules'], bias='none'))
    model.gradient_checkpointing_enable(gradient_checkpointing_kwargs={'use_reentrant': False})
    model.train()
    trainable = [(name, p) for name, p in model.named_parameters() if p.requires_grad]
    require(trainable and all('lora_' in name for name, _ in trainable), 'non-adapter-trainable')
    require(all(p.device.type == 'cuda' for p in model.parameters()), 'cpu-offload-or-meta-not-allowed')
    require(model.is_gradient_checkpointing and model.config.use_cache is False, 'checkpointing-or-cache-mismatch')
    if args.precision == 'bf16':
        require(all(p.dtype == torch.bfloat16 for p in model.parameters() if not p.requires_grad), 'base-not-bf16')
    params = [p for _, p in trainable]
    optimizer = torch.optim.AdamW(params, lr=cfg['learningRate'], betas=(0.9, 0.999), eps=1e-8,
                                 weight_decay=cfg['weightDecay'], foreach=cfg['optimizerForeach'])
    require({id(p) for g in optimizer.param_groups for p in g['params']} == {id(p) for p in params}, 'optimizer-parameters-mismatch')
    before = {name: p.detach().cpu().clone() for name, p in trainable}
    report.update(trainableParameters=sum(p.numel() for p in params), totalParameterNumel=sum(p.numel() for p in model.parameters()),
                  trainableDtypes=dict(Counter(str(p.dtype) for p in params)), adapterModules=len(trainable),
                  attentionImplementation=model.config._attn_implementation, memoryBeforeTraining=memory(torch),
                  sdpaBackend=cfg['sdpaBackend'], lossImplementation=cfg['lossImplementation'],
                  attentionVerification=[],
                  optimizer=dict(name=cfg['optimizer'], foreach=cfg['optimizerForeach'], learningRate=cfg['learningRate'],
                                 betas=[0.9, 0.999], eps=1e-8, weightDecay=cfg['weightDecay']), microSteps=[], optimizerSteps=[])
    optimizer.zero_grad(set_to_none=True)
    started = time.perf_counter()
    for i, (ids, labels, sample_id) in enumerate(schedule):
        report['phase'] = 'synthetic' if i < 16 else 'real-train'
        batch = {k: torch.tensor([v], dtype=torch.long, device='cuda:0') for k, v in
                 (('input_ids', ids), ('attention_mask', [1]*len(ids)), ('labels', labels))}
        require(bool((batch['labels'] != -100).any()) and bool((batch['labels'] == -100).any()), 'empty-or-unmasked-supervision')
        torch.cuda.synchronize()
        tick = time.perf_counter()
        projection = projection_kwargs(batch['labels'], cfg['lossImplementation'])
        projected_tokens = (projection['logits_to_keep'].numel() if projection else len(ids))
        profiling = (torch.profiler.profile(activities=[torch.profiler.ProfilerActivity.CPU])
                     if i in (0, 16) else nullcontext())
        # The backend context includes backward so checkpoint recomputation cannot
        # silently switch back to the default math implementation.
        with sdpa_context(cfg['sdpaBackend']), profiling as profiler:
            with torch.autocast(device_type='cuda', dtype=torch.bfloat16):
                output = model(**batch, **projection, use_cache=False)
                loss = output.loss
            require(output.logits.shape[:2] == (1, projected_tokens), 'logits-projection-shape-mismatch')
            require(bool(torch.isfinite(loss).item()), 'nonfinite-loss')
            loss_value = loss.item()
            forward_memory = memory(torch)
            del output
            (loss / cfg['gradientAccumulationSteps']).backward()
            torch.cuda.synchronize()
        if profiler is not None:
            report['attentionVerification'].append(dict(microStep=i + 1, phase=report['phase'],
                **attention_evidence(profiler, cfg['sdpaBackend'])))
        del loss, batch, projection
        torch.cuda.synchronize()
        event = dict(microStep=i + 1, phase=report['phase'], sampleId=sample_id, tokens=len(ids),
                     supervisedTokens=sum(x != -100 for x in labels), loss=loss_value,
                     projectedTokens=projected_tokens, memoryAfterForward=forward_memory,
                     seconds=time.perf_counter() - tick, memory=memory(torch),
                     inactiveSplitBytes=torch.cuda.memory_stats(0).get('inactive_split_bytes.all.current', 0))
        report['microSteps'].append(event)
        with (out / 'events.jsonl').open('a', encoding='utf-8') as handle:
            handle.write(json.dumps(event) + '\n')
        print(json.dumps({k: event[k] for k in ('microStep', 'phase', 'tokens', 'loss', 'seconds')}), flush=True)
        if (i + 1) % cfg['gradientAccumulationSteps'] == 0:
            require(all(p.grad is not None and bool(torch.isfinite(p.grad).all().item()) for p in params), 'missing-or-nonfinite-gradient')
            norm = torch.nn.utils.clip_grad_norm_(params, cfg['maxGradNorm'], error_if_nonfinite=True)
            require(norm.item() > 0, 'zero-gradient-norm')
            require(all(p.grad is None for p in model.parameters() if not p.requires_grad), 'base-received-gradients')
            optimizer.step()
            torch.cuda.synchronize()
            require(all(bool(torch.isfinite(p).all().item()) for p in params), 'nonfinite-adapter')
            states = [v for state in optimizer.state.values() for key, v in state.items() if key != 'step' and torch.is_tensor(v)]
            require(states and all(v.device.type == 'cuda' and bool(torch.isfinite(v).all().item()) for v in states), 'optimizer-state-invalid')
            changed = sum(not torch.equal(p.detach().cpu(), before[name]) for name, p in trainable)
            require(changed > 0, 'adapter-not-updated')
            for name, p in trainable:
                before[name] = p.detach().cpu().clone()
            report['optimizerSteps'].append(dict(step=len(report['optimizerSteps']) + 1, gradientNorm=norm.item(),
                changedAdapterTensors=changed, stateDtypes=dict(Counter(str(v.dtype) for v in states)),
                memory=memory(torch), nvidiaSmi=smi()))
            optimizer.zero_grad(set_to_none=True)
            print('Optimizer step completed: ' + str(len(report['optimizerSteps'])), flush=True)
    torch.cuda.synchronize()
    elapsed = time.perf_counter() - started
    report.update(elapsedTrainingSeconds=elapsed, totalTokens=sum(x['tokens'] for x in report['microSteps']),
                  tokensPerSecond=sum(x['tokens'] for x in report['microSteps']) / elapsed,
                  memoryAfter=memory(torch), nvidiaSmiAfter=smi())
    # CUDA free can differ from driver free on WDDM. Keep both, and use the more
    # conservative baseline-minus-incremental-peak estimate for the headroom gate.
    baseline = report['memoryBefore']
    increment = max(0, report['memoryAfter']['peakReservedBytes'] - baseline['reservedBytes'])
    headroom = min(baseline['freeBytes'], report['nvidiaSmiBefore']['freeBytes']) - increment
    sampled_free = min([s['memory']['freeBytes'] for s in report['optimizerSteps']] +
                       [s['memory']['freeBytes'] for s in report['microSteps']] +
                       [report['memoryAfter']['freeBytes'], report['nvidiaSmiAfter']['freeBytes']])
    headroom = min(headroom, sampled_free)
    fraction = headroom / baseline['totalBytes']
    require(len(report['optimizerSteps']) == 2 and len(report['microSteps']) == 32, 'probe-incomplete')
    require(verify_base(base, ref) == manifest, 'base-files-changed')
    require(digest(REPO / report['configPath']) == report['configSha256'], 'probe-config-changed')
    for name, expected_hash in report['codeSha256'].items():
        require(digest(REPO / name) == expected_hash, 'probe-code-changed')
    report.update(operationsPassed=True, conservativeHeadroomBytes=headroom, headroomFraction=fraction,
                  resourceReady=fraction >= cfg['minimumHeadroomFraction'],
                  status='passed' if fraction >= cfg['minimumHeadroomFraction'] else 'insufficient-headroom',
                  exitCode=0 if fraction >= cfg['minimumHeadroomFraction'] else 2,
                  qlora='passed' if args.precision == 'nf4' else 'not-required/not-tested', phase='complete')
    if report['resourceReady']:
        write_new_json(out / 'selected-runtime.json', dict(schemaVersion='situation-selected-runtime-v2',
            resourceReady=True, trainingReady=False, precision=args.precision, rank=args.rank, config=cfg,
            dependencyLockSha256=report['lockSha256'], baseManifestSha256=report['baseManifestSha256'],
            budgetReportSha256=cfg['budgetReportSha256'], probeCodeSha256=report['codeSha256'],
            attentionVerification=report['attentionVerification'],
            scope='two-step resource probe only; same attention/optimizer/checkpointing/loss path required',
            runtimeFrozenBaseOnGpu=True, optimizer=report['optimizer'], quantization=report.get('quantization'),
            headroomFraction=fraction, qlora=report['qlora']))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-id', required=True)
    parser.add_argument('--precision', choices=['bf16', 'nf4'], default='bf16')
    parser.add_argument('--rank', choices=[16, 8], type=int, default=16)
    parser.add_argument('--loss-implementation', choices=['full', 'assistant-positions'])
    parser.add_argument('--sdpa-backend', choices=['auto', 'cudnn'])
    args = parser.parse_args()
    require(bool(re.fullmatch(r'[a-z0-9-]{1,96}', args.run_id)), 'invalid-run-id')
    cfg_path = REPO / 'situation-implementation/situation-lora-probe-v2.json'
    cfg = read_json(cfg_path)
    cfg['rank'] = args.rank
    if args.loss_implementation is not None:
        cfg['lossImplementation'] = args.loss_implementation
    if args.sdpa_backend is not None:
        cfg['sdpaBackend'] = args.sdpa_backend
    out = safe_child(storage_root(read_json(CONFIG)['storageRoot']), 'reports/' + args.run_id)
    out.mkdir(parents=True, exist_ok=False)
    report = dict(schemaVersion='situation-lora-memory-probe-v2', status='incomplete', resourceReady=False,
        operationsPassed=False, trainingReady=False, startedUtc=datetime.now(timezone.utc).isoformat(),
        pid=os.getpid(), config=cfg, configSha256=digest(cfg_path), precision=args.precision, rank=args.rank,
        configPath=cfg_path.relative_to(REPO).as_posix(),
        codeSha256={name: digest(REPO / name) for name in ('tools/probe_situation_lora.py', 'tools/situation_evidence.py', 'tools/situation_token_measurement.py',
            'tools/situation_probe_inputs.py', 'tools/situation_training_memory.py', 'tools/situation_dependency_profile.py')},
        command=[sys.executable, *sys.argv], phase='environment', exitCode=1, adapterSaved=False)
    write_new_json(out / 'started.json', report)
    os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1', TOKENIZERS_PARALLELISM='false')
    try:
        probe(out, report, cfg, args)
    except Exception as exc:
        torch = sys.modules.get('torch')
        oom = torch is not None and isinstance(exc, torch.OutOfMemoryError)
        report.update(status='oom' if oom else 'failed', resourceReady=False, exitCode=3 if oom else 1,
                      error=dict(type=type(exc).__name__, message=str(exc), traceback=traceback.format_exc()))
        if torch is not None and torch.cuda.is_initialized():
            report['memoryAtFailure'] = memory(torch)
        report['nvidiaSmiAtFailure'] = smi()
    report['finishedUtc'] = datetime.now(timezone.utc).isoformat()
    write_new_json(out / 'memory-probe.json', report)
    print(json.dumps(dict(status=report['status'], resourceReady=report['resourceReady'],
                          report=str(out / 'memory-probe.json'), error=report.get('error')), ensure_ascii=True), flush=True)
    return report['exitCode']


if __name__ == '__main__':
    sys.exit(main())
