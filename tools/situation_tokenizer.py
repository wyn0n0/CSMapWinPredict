"""Offline tokenizer loading and isolated evaluation receipts shared by CLIs."""
import json
import os
import re
import subprocess
from prepare_situation_base import reference, destination, verify_base, REFERENCE
from situation_environment import read_json, safe_child
from situation_evidence import binding, require
from situation_stage6_budget import QwenTokenizerAdapter, REVISION

def tokenizer(store, *, full_verification):
    os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1')
    ref = reference()
    base = destination(store.storage, ref)
    manifest = verify_base(base, ref) if full_verification else read_json(base / 'base-manifest.json')
    require(manifest['revision'] == REVISION and manifest['status'] == 'complete', 'base-identity-invalid')
    bindings = [binding(store, base / 'base-manifest.json'), binding(store, REFERENCE)]
    for name, expected in manifest['tokenizerFiles'].items():
        b = binding(store, safe_child(base, name))
        require(b['sha256'] == expected, 'tokenizer-file-drift')
        bindings.append(b)
    from transformers import AutoTokenizer
    loaded = AutoTokenizer.from_pretrained(str(base), local_files_only=True, trust_remote_code=False)
    return QwenTokenizerAdapter(loaded, revision=REVISION, chat_template_sha256=manifest['chatTemplateSha256']), bindings


def consume_test_receipt(process, store, logical_path):
    """A passed word alone cannot open the gate: require a real report digest."""
    try:
        result = json.loads(process.stdout)
        require(isinstance(result, dict) and set(result) == {'status', 'sha256'}
                and result['status'] in ('passed', 'failed')
                and process.returncode == (0 if result['status'] == 'passed' else 2), 'isolated-test-result-invalid')
        value = result['sha256']
        require(value is None or isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value), 'isolated-test-hash-invalid')
        require(result['status'] != 'passed' or value is not None, 'isolated-test-passed-without-hash')
        test_binding = None
        if value is not None:
            test_binding = dict(logicalPath=logical_path, sha256=value, scope='isolated-test', split='test', purpose='final-evaluation')
            store.verify(test_binding)
        return result, test_binding
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        return dict(status='failed', sha256=None), None
