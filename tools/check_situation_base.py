"""Verify the frozen base, then load and generate on CUDA in a network-blocked process."""
import argparse
from datetime import datetime, timezone
import importlib.metadata
import json
import os
from pathlib import Path
import re
import sys
import time
import traceback

from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json
from prepare_situation_base import destination, reference, require, verify_base
from situation_dependency_profile import verify_installed_profile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-id', default='base-offline-' + datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ'))
    args = parser.parse_args()
    require(bool(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,95}', args.run_id)), 'invalid-run-id')
    config = read_json(CONFIG)
    require(Path(sys.executable).resolve() == Path(config['python']).resolve(), 'wrong-interpreter')
    root = storage_root(config['storageRoot'])
    require(os.environ.get('SITUATION_STORAGE_ROOT') == str(root), 'use-run-situation-launcher')
    out = safe_child(root, 'reports/' + args.run_id)
    out.mkdir(parents=True, exist_ok=False)
    report = dict(schemaVersion='situation-base-offline-probe-v1', status='incomplete', baseLoadReady=False,
                  loraMemoryProbe='not-tested', trainingReady=False, pid=os.getpid(),
                  startedUtc=datetime.now(timezone.utc).isoformat(), command=[sys.executable, *sys.argv],
                  scriptSha256=digest(__file__), phase='verify-base')
    write_new_json(out / 'started.json', report)
    network_attempts = []
    os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1')

    def deny_network(event, args):
        if event in ('socket.connect', 'socket.getaddrinfo', 'socket.gethostbyname'):
            network_attempts.append(event)
            raise RuntimeError('network-disabled-for-offline-probe')

    # Enforce Python socket denial in addition to local_files_only / offline flags.
    # This is process-scoped, not an OS-wide firewall change.
    sys.addaudithook(deny_network)
    try:
        ref = reference()
        base = destination(root, ref)
        manifest = verify_base(base, ref)
        report.update(modelId=ref['modelId'], revision=ref['revision'], baseDirectory=str(base),
                      manifestSha256=digest(base / 'base-manifest.json'), filesVerified=len(manifest['files']))
        report['runtimeDependencyProfile'] = verify_installed_profile()
        import torch
        from transformers import AutoModelForCausalLM, AutoTokenizer, GenerationConfig
        from probe_situation_cuda import memory
        require(torch.cuda.is_available(), 'cuda-unavailable')
        require(torch.cuda.get_device_capability(0) == (8, 9), 'expected-sm89')
        torch.cuda.set_device(0)
        require(torch.cuda.is_bf16_supported(including_emulation=False), 'native-bf16-unavailable')
        report.update(torch=str(torch.__version__), cudaRuntime=torch.version.cuda,
                      gpu=torch.cuda.get_device_name(0), baseAcquisitionDependencyLockSha256=manifest['dependencyLockSha256'],
                      dependencyLockSha256=report['runtimeDependencyProfile']['lockSha256'])
        torch.cuda.reset_peak_memory_stats(0)
        report['memoryBefore'] = memory(torch)
        report['phase'] = 'load-pretrained'
        started = time.perf_counter()
        tokenizer = AutoTokenizer.from_pretrained(str(base), local_files_only=True, trust_remote_code=False)
        model, loading = AutoModelForCausalLM.from_pretrained(
            str(base), local_files_only=True, trust_remote_code=False, use_safetensors=True,
            dtype=torch.bfloat16, device_map={'': 'cuda:0'}, attn_implementation='sdpa', output_loading_info=True)
        model.eval()
        torch.cuda.synchronize(0)
        report['loadSeconds'] = time.perf_counter() - started
        report['loadingInfo'] = loading
        require(not any(loading.get(k) for k in ('missing_keys', 'unexpected_keys', 'mismatched_keys', 'error_msgs')),
                'checkpoint-not-fully-loaded')
        devices = sorted({str(p.device) for p in model.parameters()})
        dtypes = sorted({str(p.dtype) for p in model.parameters()})
        require(devices == ['cuda:0'], 'cpu-offload-or-meta-parameters-not-allowed')
        require(dtypes == ['torch.bfloat16'], 'model-parameters-not-bf16')
        report.update(parameterCount=sum(p.numel() for p in model.parameters()), devices=devices, dtypes=dtypes,
                      attentionImplementation=model.config._attn_implementation)
        report['memoryAfterLoad'] = memory(torch)
        messages = [{'role': 'user', 'content': '请用一句简短的中文说明什么是语言模型。'}]
        prompt = tokenizer.apply_chat_template(messages, tokenize=False, add_generation_prompt=True, enable_thinking=False)
        inputs = tokenizer(prompt, return_tensors='pt', add_special_tokens=False).to('cuda:0')
        report['phase'] = 'generate'
        torch.manual_seed(20260927)
        started = time.perf_counter()
        with torch.inference_mode():
            forward = model(**inputs)
            finite_logits = bool(torch.isfinite(forward.logits).all().item())
            del forward
            require(finite_logits, 'non-finite-logits')
            generation = GenerationConfig(max_new_tokens=48, do_sample=False,
                                          bos_token_id=model.generation_config.bos_token_id,
                                          eos_token_id=model.generation_config.eos_token_id,
                                          pad_token_id=tokenizer.pad_token_id)
            generated = model.generate(**inputs, generation_config=generation, use_model_defaults=False)
        torch.cuda.synchronize(0)
        elapsed = time.perf_counter() - started
        ids = generated[0, inputs['input_ids'].shape[1]:].tolist()
        answer = tokenizer.decode(ids, skip_special_tokens=True)
        require(bool(ids) and bool(answer.strip()), 'empty-generation')
        require(not network_attempts, 'offline-load-attempted-network')
        report['generation'] = dict(messages=messages, renderedPrompt=prompt, enableThinking=False,
                                    inputTokens=inputs['input_ids'].shape[1], outputTokens=len(ids),
                                    outputTokenIds=ids, outputText=answer, maxNewTokens=48, doSample=False,
                                    useModelDefaults=False, generationConfig=generation.to_dict(),
                                    seed=20260927, finiteLogits=finite_logits, synchronizedSeconds=elapsed)
        report['memoryAfterGeneration'] = memory(torch)
        report.update(status='passed', phase='complete', baseLoadReady=True, exitCode=0)
    except Exception as exc:
        report.update(status='failed', baseLoadReady=False, exitCode=1,
                      error=dict(type=type(exc).__name__, message=str(exc), traceback=traceback.format_exc()))
    report['network'] = dict(hubOffline=True, transformersOffline=True, pythonSocketConnectBlocked=True,
                             attemptedNetworkEvents=network_attempts, osFirewallChanged=False)
    report['finishedUtc'] = datetime.now(timezone.utc).isoformat()
    write_new_json(out / 'base-offline-probe.json', report)
    print(json.dumps(dict(status=report['status'], baseLoadReady=report['baseLoadReady'],
                          report=str(out / 'base-offline-probe.json'), error=report.get('error')), ensure_ascii=False))
    return report['exitCode']


if __name__ == '__main__':
    sys.exit(main())
