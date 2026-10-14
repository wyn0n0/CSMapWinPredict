"""Local storage and environment helpers; standard library only."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat

REPO = Path(__file__).resolve().parents[1]
CONFIG = REPO / '.venv/situation.local.json'
VENV = REPO / '.venv/situation'
LAYOUT = ('bases', 'cache/huggingface/hub', 'cache/huggingface/xet',
          'cache/huggingface/assets', 'cache/torch', 'cache/pip', 'datasets',
          'runs', 'reports', 'tmp')


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def write_new_json(path, value):
    with Path(path).open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write('\n')


def storage_root(path, repo=REPO):
    requested = Path(path)
    if not requested.is_absolute():
        raise ValueError('storage-root-must-be-absolute')
    root, repo = requested.resolve(), Path(repo).resolve()
    if root == Path(root.anchor) or root.is_relative_to(repo) or repo.is_relative_to(root):
        raise ValueError('storage-root-overlaps-repository-or-drive-root')
    return root


def safe_child(root, relative):
    root = Path(root).resolve()
    relative = Path(relative)
    if relative.is_absolute() or '..' in relative.parts or ':' in str(relative):
        raise ValueError('unsafe-relative-path')
    candidate = root / relative
    if not candidate.resolve().is_relative_to(root):
        raise ValueError('path-escapes-root')
    current = candidate
    while current != root:
        if current.exists() or current.is_symlink():
            info = current.lstat()
            if current.is_symlink() or getattr(info, 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise ValueError('linked-child-not-allowed')
        current = current.parent
    return candidate


def child_environment(root, parent=None):
    root = storage_root(root)
    for relative in LAYOUT:
        safe_child(root, relative)
    env = dict(os.environ if parent is None else parent)
    for name in ('PYTHONHOME', 'PYTHONPATH'):
        env.pop(name, None)
    env.update({
        'SITUATION_STORAGE_ROOT': str(root),
        'HF_HOME': str(root/'cache/huggingface'),
        'HF_HUB_CACHE': str(root/'cache/huggingface/hub'),
        'HF_XET_CACHE': str(root/'cache/huggingface/xet'),
        'HF_ASSETS_CACHE': str(root/'cache/huggingface/assets'),
        'TORCH_HOME': str(root/'cache/torch'), 'PIP_CACHE_DIR': str(root/'cache/pip'),
        'TEMP': str(root/'tmp'), 'TMP': str(root/'tmp'), 'TMPDIR': str(root/'tmp'),
        'PYTHONNOUSERSITE': '1',
    })
    return env


def dataset_inventory(source, expected_manifest_hash=None):
    source = Path(source).resolve()
    manifest_path = safe_child(source, 'manifest.json')
    manifest_hash = digest(manifest_path)
    if expected_manifest_hash and manifest_hash != expected_manifest_hash:
        raise ValueError('source-manifest-drift')
    manifest = read_json(manifest_path)
    if manifest.get('status') != 'complete':
        raise ValueError('source-not-complete')
    entries = []
    names = set()
    for item in manifest['files']:
        path = safe_child(source, item['path'])
        name = path.relative_to(source).as_posix()
        if name.casefold() in names or name == 'manifest.json':
            raise ValueError('duplicate-manifest-file')
        names.add(name.casefold())
        if path.stat().st_size != item['bytes'] or digest(path) != item['sha256']:
            raise ValueError('source-file-integrity-mismatch: '+name)
        entries.append(dict(path=name, bytes=item['bytes'], sha256=item['sha256']))
    entries.append(dict(path='manifest.json', bytes=manifest_path.stat().st_size, sha256=manifest_hash))
    actual = {p.relative_to(source).as_posix() for p in source.rglob('*') if p.is_file()}
    if actual != {x['path'] for x in entries}:
        raise ValueError('dataset-file-set-mismatch')
    return entries


def copy_verified(source, target, inventory):
    """Never overwrite. Keep failed destination for diagnosis; no automatic deletion."""
    source, target = Path(source), Path(target)
    if target.exists():
        actual = {p.relative_to(target).as_posix() for p in target.rglob('*') if p.is_file()}
        if actual != {x['path'] for x in inventory}:
            raise ValueError('existing-copy-file-set-mismatch')
        reused = True
    else:
        target.mkdir(parents=True, exist_ok=False)
        reused = False
    for item in inventory:
        src = safe_child(source, item['path'])
        dst = safe_child(target, item['path'])
        if not reused:
            dst.parent.mkdir(parents=True, exist_ok=True)
            with src.open('rb') as inp, dst.open('xb') as out:
                shutil.copyfileobj(inp, out, 1024*1024)
        if dst.stat().st_size != item['bytes'] or digest(dst) != item['sha256']:
            raise ValueError('copy-integrity-mismatch: '+item['path'])
        if src.stat().st_size != item['bytes'] or digest(src) != item['sha256']:
            raise ValueError('source-changed-during-copy: '+item['path'])
    return dict(reused=reused, files=len(inventory), bytes=sum(x['bytes'] for x in inventory))


def nearest_existing(path):
    path = Path(path)
    while not path.exists():
        path = path.parent
    return path
