"""Validate installed versions against either explicitly pinned dependency profile."""
import importlib.metadata
import re

from situation_environment import REPO, digest, read_json


def verify_installed_profile():
    normalize = lambda s: re.sub(r'[-_.]+', '-', s).lower()
    installed = {normalize(d.metadata['Name']): d.version for d in importlib.metadata.distributions()}
    for stem in ('requirements-situation-win-cu126', 'requirements-situation-win-cu126-qlora'):
        path = REPO / (stem + '.sources.json')
        lock = REPO / (stem + '.lock.txt')
        if not path.is_file() or not lock.is_file():
            continue
        source = read_json(path)
        if {normalize(p['name']): p['version'] for p in source['packages']} != installed:
            continue
        if digest(lock) != source['lockSha256']:
            raise ValueError('dependency-lock-drift')
        return dict(profile=stem, lockSha256=digest(lock), sourcesSha256=digest(path), installedPackages=installed)
    raise ValueError('installed-packages-differ-from-known-locks')
