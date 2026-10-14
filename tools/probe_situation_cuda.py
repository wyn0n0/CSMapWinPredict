"""Step four: run once in a fresh process; never download a model or change packages."""
import argparse
from datetime import datetime, timezone
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import time
import traceback

from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json
from situation_dependency_profile import verify_installed_profile


class ProbeFailure(RuntimeError):
    pass


def require(condition, code):
    if not condition:
        raise ProbeFailure(code)


def normalized(name):
    return re.sub(r'[-_.]+', '-', name).lower()


def memory(torch):
    free, total = torch.cuda.mem_get_info(0)
    return dict(freeBytes=free, totalBytes=total,
                allocatedBytes=torch.cuda.memory_allocated(0),
                reservedBytes=torch.cuda.memory_reserved(0),
                peakAllocatedBytes=torch.cuda.max_memory_allocated(0),
                peakReservedBytes=torch.cuda.max_memory_reserved(0))


def probe(report, config):
    report['phase'] = 'environment'
    require(Path(sys.executable).resolve() == Path(config['python']).resolve(), 'wrong-interpreter')
    require(sys.version_info[:2] == (3, 11), 'python-version-mismatch')
    require(Path(config['repository']).resolve() == REPO, 'repository-mismatch')
    profile = verify_installed_profile()
    report['dependencyProfile'] = profile
    report['lockSha256'] = profile['lockSha256']
    report['sourcesSha256'] = profile['sourcesSha256']
    report['installedPackages'] = profile['installedPackages']
    import torch
    report['torch'] = str(torch.__version__)
    report['cudaRuntime'] = torch.version.cuda
    require(torch.version.cuda == '12.6', 'cuda-runtime-mismatch')
    report['cudaAvailable'] = torch.cuda.is_available()
    require(report['cudaAvailable'], 'cuda-unavailable-check-driver-and-wheel')
    report['phase'] = 'device'
    smi_command = ['nvidia-smi', '--query-gpu=index,uuid,name,driver_version,memory.total,memory.used,memory.free,compute_cap', '--format=csv,noheader']
    smi = subprocess.run(smi_command, capture_output=True, text=True, timeout=20)
    report['nvidiaSmi'] = dict(command=smi_command, exitCode=smi.returncode,
                             stdout=smi.stdout.strip(), stderr=smi.stderr.strip())
    require(smi.returncode == 0 and bool(smi.stdout.strip()), 'driver-query-failed')
    torch.cuda.set_device(0)
    props = torch.cuda.get_device_properties(0)
    report['device'] = dict(index=0, name=props.name, uuid=str(props.uuid),
                            capability=list(torch.cuda.get_device_capability(0)),
                            totalBytes=props.total_memory)
    require(report['device']['capability'] == [8, 9], 'gpu-architecture-mismatch-expected-sm89')
    report['bf16Reported'] = torch.cuda.is_bf16_supported()
    report['bf16NativeReported'] = torch.cuda.is_bf16_supported(including_emulation=False)
    require(report['bf16NativeReported'], 'native-bf16-unsupported')
    torch.cuda.synchronize(0)
    torch.cuda.reset_peak_memory_stats(0)
    report['memoryBefore'] = memory(torch)

    report['phase'] = 'bf16-forward-backward'
    seed, size = 20260927, 256
    generator = torch.Generator(device='cpu').manual_seed(seed)
    # Round inputs to BF16 first so the FP32 reference uses exactly the same values.
    a_cpu = (torch.randn(size, size, generator=generator) / size**0.5).bfloat16().float()
    b_cpu = (torch.randn(size, size, generator=generator) / size**0.5).bfloat16().float()
    a = a_cpu.to(device='cuda:0', dtype=torch.bfloat16).requires_grad_()
    b = b_cpu.to(device='cuda:0', dtype=torch.bfloat16).requires_grad_()
    torch.cuda.synchronize(0)
    started = time.perf_counter()
    output = a @ b
    # FP32 scalar reduction is deliberate; matmul and its leaf gradients are BF16.
    loss = output.float().square().mean()
    loss.backward()
    torch.cuda.synchronize(0)
    elapsed = time.perf_counter() - started
    report['memoryAfter'] = memory(torch)
    tensors = {'a': a, 'b': b, 'output': output, 'loss': loss,
               'aGradient': a.grad, 'bGradient': b.grad}
    checks = {}
    for name, tensor in tensors.items():
        checks[name + 'Present'] = tensor is not None
        require(tensor is not None, 'missing-tensor-' + name)
        checks[name + 'OnGpu'] = tensor.device == torch.device('cuda:0')
        checks[name + 'Finite'] = bool(torch.isfinite(tensor).all().item())
        checks[name + 'Dtype'] = tensor.dtype == (torch.float32 if name == 'loss' else torch.bfloat16)
    checks['positiveLoss'] = loss.item() > 0
    checks['aGradientNonzero'] = bool(torch.count_nonzero(a.grad).item())
    checks['bGradientNonzero'] = bool(torch.count_nonzero(b.grad).item())
    reference = a_cpu @ b_cpu
    relative_error = ((output.detach().float().cpu() - reference).norm() / reference.norm()).item()
    # A norm tolerance avoids unstable per-element relative errors near zero.
    checks['forwardReferenceWithinTolerance'] = relative_error <= 0.02
    report['operation'] = dict(seed=seed, shape=[size, size], expression='output=a@b; loss=mean(output.float()**2)',
                               loss=loss.item(), synchronizedSeconds=elapsed,
                               forwardRelativeL2Error=relative_error, referenceTolerance=0.02,
                               aGradientNorm=a.grad.float().norm().item(),
                               bGradientNorm=b.grad.float().norm().item(),
                               tensors={k: dict(dtype=str(v.dtype), device=str(v.device)) for k, v in tensors.items()})
    report['checks'] = checks
    require(all(checks.values()), 'tensor-validation-failed')
    report['capabilities']['bf16MatmulForwardBackward'] = 'passed'
    report['cudaBf16Ready'] = True
    report['phase'] = 'complete'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-id', default='cuda-probe-' + datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ'))
    args = parser.parse_args()
    require(bool(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,95}', args.run_id)), 'invalid-run-id')
    config = read_json(CONFIG)
    root = storage_root(config['storageRoot'])
    require(os.environ.get('SITUATION_STORAGE_ROOT') == str(root), 'use-tools-run-situation-launcher')
    out = safe_child(root, 'reports/' + args.run_id)
    out.mkdir(parents=True, exist_ok=False)
    report = dict(schemaVersion='situation-cuda-probe-v1', startedUtc=datetime.now(timezone.utc).isoformat(),
                  python=platform.python_version(), executable=sys.executable, pid=os.getpid(),
                  command=[sys.executable, *sys.argv],
                  lockSha256=digest(REPO / 'requirements-situation-win-cu126.lock.txt'),
                  sourcesSha256=digest(REPO / 'requirements-situation-win-cu126.sources.json'),
                  probeSha256=digest(__file__), cudaBf16Ready=False, status='running',
                  capabilities=dict(bf16MatmulForwardBackward='not-tested',
                                    qwen3BaseLoad='not-tested', loraMemoryProbe='not-tested',
                                    qlora='not-tested', flashAttention='not-tested', torchCompile='not-tested'))
    write_new_json(out / 'started.json', report)
    exit_code = 1
    try:
        probe(report, config)
        report['status'] = 'passed'
        exit_code = 0
    except Exception as exc:
        # Never interpret arbitrary runtime/driver failures as OOM or enable QLoRA.
        report['status'] = 'failed'
        report['cudaBf16Ready'] = False
        report['error'] = dict(type=type(exc).__name__, message=str(exc), traceback=traceback.format_exc())
        if report.get('phase') == 'bf16-forward-backward':
            report['capabilities']['bf16MatmulForwardBackward'] = 'failed'
    report['finishedUtc'] = datetime.now(timezone.utc).isoformat()
    report['exitCode'] = exit_code
    write_new_json(out / 'cuda-probe.json', report)
    print(json.dumps(dict(status=report['status'], cudaBf16Ready=report['cudaBf16Ready'],
                          report=str(out / 'cuda-probe.json'), error=report.get('error')), ensure_ascii=False))
    return exit_code


if __name__ == '__main__':
    sys.exit(main())
