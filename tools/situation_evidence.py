"""Strict SHA-256 evidence verification shared by preparation and budget tools."""
import copy
import json
import re
import subprocess
import sys
from pathlib import Path

try:
    from .situation_environment import digest, read_json, safe_child
except ImportError:
    from situation_environment import digest, read_json, safe_child

SHA256 = re.compile(r"[0-9a-f]{64}\Z")

class EvidenceError(ValueError):
    pass


def require(value, reason):
    if not value:
        raise EvidenceError(reason)


class EvidenceStore:
    def __init__(self, repository, storage, *, isolated=False):
        self.repository = Path(repository).resolve()
        self.storage = Path(storage).resolve()
        self.isolated = isolated
        self.checked = {}

    def path(self, logical):
        require(isinstance(logical, str) and ":" in logical, "binding-path-invalid")
        scheme, relative = logical.split(":", 1)
        require(scheme in ("repo", "storage"), "binding-root-invalid")
        require(relative and "\\" not in relative, "binding-path-invalid")
        return safe_child(self.repository if scheme == "repo" else self.storage, relative)

    def is_test_payload(self, binding):
        path = self.path(binding.get("logicalPath"))
        # Repository test source is public code, not held-out corpus content.
        storage_path = path.is_relative_to(self.storage)
        return (binding.get("split") == "test" or binding.get("scope") == "isolated-test"
                or storage_path and (path.name in {'label-stats.json', 'statistics.json', 'overlays.json',
                    'duplicate-audit-private.json', 'review-inputs.jsonl', 'review-stats.json', 'selection-differences-isolated.json'}
                    or any(part.startswith(('test', 'situation-stage4-review-frozen-')) or part == "isolated-test" for part in path.parts)))

    def verify(self, binding):
        require(isinstance(binding, dict), "binding-invalid")
        logical, expected = binding.get("logicalPath"), binding.get("sha256")
        require(isinstance(expected, str) and SHA256.fullmatch(expected), "binding-hash-invalid")
        path = self.path(logical)
        # File names are checked as well so a missing split flag cannot turn a
        # test-file binding into ordinary parent-process data access.
        private_test = self.is_test_payload(binding)
        if private_test and not self.isolated:
            command = [sys.executable, "-B", str(Path(__file__).resolve()),
                       "--isolated-verify-binding", logical, expected,
                       "--repository", str(self.repository), "--storage-root", str(self.storage)]
            process = subprocess.run(command, capture_output=True, text=True,
                                     encoding="utf-8", timeout=600)
            require(process.returncode == 0, "isolated-test-binding-failed")
            try:
                result = json.loads(process.stdout)
            except ValueError as exc:
                raise EvidenceError("isolated-test-result-invalid") from exc
            require(set(result) == {"status", "sha256"} and result["status"] == "passed"
                    and result["sha256"] == expected, "isolated-test-result-invalid")
            actual = result["sha256"]
        else:
            actual = digest(path)
        require(actual == expected, "binding-hash-mismatch")
        self.checked[logical] = actual
        return dict(binding, actualSha256=actual, observedSha256=actual, verified=True)

    def report(self, binding):
        self.verify(binding)
        require(not self.is_test_payload(binding), "test-detail-report-forbidden")
        value = read_json(self.path(binding["logicalPath"]))
        require(isinstance(value, dict), "report-invalid")
        bindings = value.get("evidenceBindings")
        require(isinstance(bindings, list) and bool(bindings), "report-bindings-missing")
        result = copy.deepcopy(value)
        result["evidenceBindings"] = [self.verify(item) for item in bindings]
        result["hashesVerified"] = True
        result["_artifactBinding"] = dict(binding)
        if result.get('checkId') == 'corpus-pairing':
            marker_path = self.path(binding['logicalPath']).parent / 'complete.json'
            marker = read_json(marker_path)
            require(marker.get('complete') is True and marker.get('frozen') is True
                    and marker.get('reportSha256') == binding['sha256'], 'corpus-completion-marker-invalid')
            manifest_path = marker_path.parent / 'manifest.json'
            logical_manifest = binding['logicalPath'].rsplit('/', 1)[0] + '/manifest.json'
            # A root-level fixture is valid as well as the production subdirectory.
            if '/' not in binding['logicalPath']:
                logical_manifest = binding['logicalPath'].split(':', 1)[0] + ':manifest.json'
            declared = next((b for b in bindings if b['logicalPath'] == logical_manifest), None)
            require(declared is not None and marker.get('manifestSha256') == declared['sha256']
                    and digest(manifest_path) == declared['sha256'], 'corpus-completion-manifest-invalid')
            self.checked[binding['logicalPath'].rsplit(':', 1)[0] + ':' + marker_path.relative_to(self.storage if marker_path.is_relative_to(self.storage) else self.repository).as_posix()] = digest(marker_path)
        return result


def binding(store, path, **extra):
    path = Path(path).resolve()
    scheme, root = ('repo', store.repository) if path.is_relative_to(store.repository) else ('storage', store.storage)
    return dict(logicalPath=scheme + ':' + path.relative_to(root).as_posix(), sha256=digest(path), **extra)



def main():
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--isolated-verify-binding', nargs=2, required=True)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--storage-root', required=True)
    args = parser.parse_args()
    try:
        logical, expected = args.isolated_verify_binding
        EvidenceStore(args.repository, args.storage_root, isolated=True).verify(
            dict(logicalPath=logical, sha256=expected, split='test'))
        print(json.dumps(dict(status='passed', sha256=expected)))
        return 0
    except (OSError, ValueError, TypeError, KeyError):
        print(json.dumps(dict(status='failed', sha256=None)))
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
