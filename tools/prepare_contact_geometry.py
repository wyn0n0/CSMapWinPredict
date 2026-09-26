"""Prepare only the pinned Mirage collision mesh; no nav assets or dependencies.

python tools/prepare_contact_geometry.py [--archive path/to/geometry.zip]
Game assets remain Valve's property. Source: pnxenopoulos/awpy-data.
"""
import argparse
import hashlib
from pathlib import Path
import tempfile
import urllib.request
import zipfile

URL = "https://github.com/pnxenopoulos/awpy-data/releases/download/2000917/geometry.zip"
ARCHIVE_SHA256 = "1bc559fa9b1048b078a47be972c0546e71b1d1d958dcbfcc94e17eeb2e454971"
MESH_SHA256 = "d5e3aabe17583f07ca899236d015ecc46f7e542fa093ada4782944941b41e679"


def prepare(archive: Path | None) -> None:
    target = Path(__file__).resolve().parents[1] / "data/geometry/de_mirage.mesh"
    if target.exists():
        if hashlib.sha256(target.read_bytes()).hexdigest() != MESH_SHA256:
            raise ValueError("Existing mesh differs; preserve it and investigate before replacing.")
        print(f"Pinned Mirage mesh verified: {target}")
        return
    with tempfile.TemporaryDirectory(prefix="contact-geometry-") as temporary:
        if archive is None:
            archive = Path(temporary) / "geometry.zip"
            request = urllib.request.Request(URL, headers={"User-Agent": "cs-demo-map-geometry"})
            with urllib.request.urlopen(request, timeout=120) as source, archive.open("xb") as output:
                while block := source.read(1024 * 1024):
                    output.write(block)
        with archive.open("rb") as source:
            if hashlib.file_digest(source, "sha256").hexdigest() != ARCHIVE_SHA256:
                raise ValueError("Collision archive hash mismatch")
        with zipfile.ZipFile(archive) as source:
            mesh = source.read("de_mirage.mesh")
        if hashlib.sha256(mesh).hexdigest() != MESH_SHA256:
            raise ValueError("Mirage mesh hash mismatch")
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as output:
            output.write(mesh)
    print(f"Prepared pinned Mirage mesh: {target}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path)
    prepare(parser.parse_args().archive)
