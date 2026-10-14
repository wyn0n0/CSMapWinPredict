"""Fetch a pinned official Qwen3 text base; verify before publishing a new directory."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import time
import traceback

from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json

REFERENCE = REPO / 'situation-implementation/situation-qwen3-base-reference-v1.json'
LOCK = REPO / 'requirements-situation-win-cu126.lock.txt'


def require(ok, reason):
    if not ok:
        raise ValueError(reason)


def reference():
    ref = read_json(REFERENCE)
    require(ref['modelId'] == 'Qwen/Qwen3-1.7B', 'wrong-model-id')
    require(bool(re.fullmatch('[a-f0-9]{40}', ref['revision'])), 'revision-must-be-exact-commit')
    require(ref['dependencyLockSha256'] == digest(LOCK), 'dependency-lock-drift')
    return ref


def destination(root, ref):
    return safe_child(root, 'bases/Qwen3-1.7B-' + ref['revision'])


def direct_download(ref, entry, path):
    """Official HTTP fallback with bounded reconnects and checked resume offsets."""
    import requests
    url = 'https://huggingface.co/' + ref['modelId'] + '/resolve/' + ref['revision'] + '/' + entry['path'] + '?download=true'
    total = entry['bytes']
    path.touch(exist_ok=False)
    for attempt in range(1, 9):
        offset = path.stat().st_size
        if offset == total:
            return
        try:
            headers = {'Range': f'bytes={offset}-', 'Accept-Encoding': 'identity'}
            with requests.get(url, headers=headers, stream=True, timeout=(15, 45)) as response:
                response.raise_for_status()
                require(response.status_code == 206 or (response.status_code == 200 and offset == 0), 'invalid-resume-status')
                if response.status_code == 206:
                    require(response.headers.get('Content-Range', '').startswith(f'bytes {offset}-') and
                            response.headers['Content-Range'].endswith('/' + str(total)), 'invalid-resume-range')
                last_print = time.monotonic()
                with path.open('ab') as stream:
                    for chunk in response.iter_content(1024 * 1024):
                        require(offset + len(chunk) <= total, 'download-exceeds-expected-size')
                        stream.write(chunk)
                        offset += len(chunk)
                        if time.monotonic() - last_print >= 20:
                            print(f'{entry["path"]}: {offset}/{total} bytes', flush=True)
                            last_print = time.monotonic()
            if path.stat().st_size == total:
                return
        except requests.RequestException as exc:
            print(f'HTTP attempt {attempt}: {type(exc).__name__}; retained {path.stat().st_size} bytes', flush=True)
        time.sleep(2)
    raise RuntimeError('official-http-download-incomplete-after-eight-attempts')


def validate_files(base, ref):
    names = [f['path'] for f in ref['files']]
    require(len(set(names)) == len(names), 'duplicate-reference-file')
    allowed = {'LICENSE', 'README.md', 'config.json', 'generation_config.json',
               'merges.txt', 'vocab.json', 'tokenizer.json', 'tokenizer_config.json',
               'model.safetensors.index.json', 'chat_template.jinja'}
    entries = []
    for entry in ref['files']:
        name = entry['path']
        require(name in allowed or bool(re.fullmatch(r'model-\d{5}-of-\d{5}\.safetensors', name)), 'unexpected-file-type')
        path = safe_child(base, name)
        require(path.stat().st_size == entry['bytes'], 'size-mismatch: ' + name)
        sha = digest(path)
        if entry.get('sha256'):
            require(sha == entry['sha256'], 'upstream-sha256-mismatch: ' + name)
        else:
            blob = hashlib.sha1(b'blob ' + str(entry['bytes']).encode() + b'\0' + path.read_bytes()).hexdigest()
            require(blob == entry['gitBlobSha1'], 'upstream-git-blob-mismatch: ' + name)
        entries.append(dict(path=name, bytes=entry['bytes'], sha256=sha))
    actual = {p.relative_to(base).as_posix() for p in base.rglob('*') if p.is_file()}
    require(actual - {'base-manifest.json'} == set(names), 'base-file-set-mismatch')
    config = read_json(base / 'config.json')
    require(config.get('model_type') == 'qwen3' and not config.get('vision_config'), 'not-text-qwen3')
    require(config.get('architectures') == ['Qwen3ForCausalLM'], 'unexpected-architecture')
    index = read_json(base / 'model.safetensors.index.json')
    shards = {name for name in names if name.endswith('.safetensors')}
    require(set(index['weight_map'].values()) == shards, 'weight-index-shards-mismatch')
    license_text = (base / 'LICENSE').read_text(encoding='utf-8')
    require('Apache License' in license_text and 'Version 2.0, January 2004' in license_text, 'license-not-apache-2.0')
    tokenizer = read_json(base / 'tokenizer_config.json')
    template = tokenizer.get('chat_template')
    require(isinstance(template, str) and bool(template.strip()), 'chat-template-missing')
    return entries, hashlib.sha256(template.encode('utf-8')).hexdigest()


def verify_base(base, ref=None):
    ref = reference() if ref is None else ref
    manifest = read_json(safe_child(base, 'base-manifest.json'))
    require(manifest['status'] == 'complete' and manifest['modelId'] == ref['modelId'] and
            manifest['revision'] == ref['revision'], 'base-manifest-identity-mismatch')
    require(manifest['referenceSha256'] == digest(REFERENCE), 'base-reference-drift')
    require(manifest['dependencyLockSha256'] == digest(LOCK), 'base-lock-drift')
    files, template_hash = validate_files(base, ref)
    require(manifest['files'] == files, 'base-manifest-file-hash-mismatch')
    require(manifest['chatTemplateSha256'] == template_hash, 'chat-template-hash-mismatch')
    hashes = {f['path']: f['sha256'] for f in files}
    require(manifest['license'] == dict(id='Apache-2.0', path='LICENSE', sha256=hashes['LICENSE']), 'license-manifest-mismatch')
    require(manifest['tokenizerFiles'] == {name: value for name, value in hashes.items()
            if name in ('tokenizer.json', 'tokenizer_config.json', 'merges.txt', 'vocab.json')}, 'tokenizer-manifest-mismatch')
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['fetch', 'verify'])
    parser.add_argument('--direct-http', action='store_true', help='Use official download=true HTTP for large weight shards')
    parser.add_argument('--run-id', default='base-' + datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ'))
    args = parser.parse_args()
    require(bool(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,95}', args.run_id)), 'invalid-run-id')
    config = read_json(CONFIG)
    require(Path(sys.executable).resolve() == Path(config['python']).resolve(), 'wrong-interpreter')
    root = storage_root(config['storageRoot'])
    require(os.environ.get('SITUATION_STORAGE_ROOT') == str(root), 'use-run-situation-launcher')
    ref = reference()
    target = destination(root, ref)
    out = safe_child(root, 'reports/' + args.run_id)
    out.mkdir(parents=True, exist_ok=False)
    report = dict(schemaVersion='situation-base-acquisition-v1', status='incomplete',
                  modelId=ref['modelId'], revision=ref['revision'], action=args.action,
                  startedUtc=datetime.now(timezone.utc).isoformat(), command=[sys.executable, *sys.argv],
                  scriptSha256=digest(__file__), referenceSha256=digest(REFERENCE), baseDirectory=str(target))
    write_new_json(out / 'started.json', report)
    try:
        if target.exists():
            verify_base(target, ref)
            report['reused'] = True
        else:
            require(args.action == 'fetch', 'base-not-found')
            total = sum(f['bytes'] for f in ref['files'])
            free = shutil.disk_usage(root).free
            report['disk'] = dict(freeBytes=free, downloadBytes=total, requiredBytes=total * 2 + 2**30)
            require(free >= report['disk']['requiredBytes'], 'insufficient-storage-for-cache-and-base')
            staging = safe_child(root, 'tmp/' + args.run_id)
            staging.mkdir(exist_ok=False)
            base = staging / 'model'
            base.mkdir()
            write_new_json(staging / 'incomplete.json', report)
            from huggingface_hub import hf_hub_download
            for entry in ref['files']:
                name = entry['path']
                print('Downloading ' + name, flush=True)
                if args.direct_http and name.endswith('.safetensors'):
                    direct_download(ref, entry, safe_child(base, name))
                    continue
                cached = hf_hub_download(ref['modelId'], name, revision=ref['revision'], token=False)
                # Never alter the Hub cache; keep a separate verified, self-contained base.
                path = safe_child(base, name)
                with Path(cached).open('rb') as source, path.open('xb') as dest:
                    shutil.copyfileobj(source, dest, 1024 * 1024)
            entries, template_hash = validate_files(base, ref)
            hashes = {f['path']: f['sha256'] for f in entries}
            manifest = dict(schemaVersion='situation-base-manifest-v1', status='complete',
                            modelId=ref['modelId'], revision=ref['revision'],
                            acquiredUtc=datetime.now(timezone.utc).isoformat(),
                            referenceSha256=digest(REFERENCE), dependencyLockSha256=digest(LOCK),
                            files=entries, chatTemplateSha256=template_hash,
                            chatTemplateSource='tokenizer_config.json:chat_template (UTF-8 value)',
                            tokenizerFiles={n: h for n, h in hashes.items() if n in
                                            ('tokenizer.json', 'tokenizer_config.json', 'merges.txt', 'vocab.json')},
                            license=dict(id='Apache-2.0', path='LICENSE', sha256=hashes['LICENSE']))
            write_new_json(base / 'base-manifest.json', manifest)
            verify_base(base, ref)
            require(not target.exists(), 'target-created-concurrently')
            # Same-volume rename after all checks; no overwrite or repair in place.
            base.rename(target)
            write_new_json(staging / 'complete.json', dict(status='promoted', baseDirectory=str(target)))
            report['reused'] = False
        report.update(status='complete', manifestSha256=digest(target / 'base-manifest.json'), exitCode=0)
    except Exception as exc:
        report.update(status='incomplete', exitCode=1, error=dict(type=type(exc).__name__, message=str(exc), traceback=traceback.format_exc()))
    report['finishedUtc'] = datetime.now(timezone.utc).isoformat()
    write_new_json(out / 'result.json', report)
    print(json.dumps(report, ensure_ascii=False), flush=True)
    return report['exitCode']


if __name__ == '__main__':
    sys.exit(main())
