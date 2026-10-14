"""Run the isolated interpreter with project-local cache settings.

Example: python tools/run_situation.py -m pip --version
The parent shell and machine/user environment are not modified.
"""
import subprocess
import sys
from pathlib import Path

from situation_environment import CONFIG, REPO, VENV, child_environment, read_json, safe_child, storage_root


def main():
    config=read_json(CONFIG)
    root=storage_root(config['storageRoot'])
    python=safe_child(REPO,'.venv/situation/Scripts/python.exe')
    if Path(config['repository']).resolve()!=REPO or Path(config['python']).resolve()!=python.resolve():
        raise ValueError('local-config-environment-mismatch')
    if not python.is_file() or not VENV.is_dir():
        raise ValueError('situation-environment-missing')
    args=sys.argv[1:]
    if not args:
        raise SystemExit('Usage: python tools/run_situation.py <Python arguments>')
    return subprocess.call([str(python),*args],env=child_environment(root),cwd=REPO)


if __name__=='__main__':
    raise SystemExit(main())
