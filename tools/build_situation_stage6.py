"""One shared .NET build, with immutable source and actual binary bindings."""
import argparse
import os
from pathlib import Path
import subprocess
from situation_environment import REPO, digest, write_new_json


def build_input_paths():
    paths = set()
    for folder in ('apps/api', 'apps/cli', 'tests/CsDemoMap.Api.Tests', 'tests/situation-review-ui', 'schemas/situation'):
        for path in (REPO / folder).rglob('*'):
            if path.is_file() and not {'bin', 'obj'} & set(path.relative_to(REPO).parts) and path.suffix in ('.cs', '.csproj', '.json', '.mjs'):
                paths.add(path)
    for name in ('package.json', 'package-lock.json', 'tools/build_situation_stage6.py'):
        paths.add(REPO / name)
    for name in ('Directory.Build.props', 'Directory.Build.targets', 'global.json', 'NuGet.Config'):
        if (REPO / name).is_file(): paths.add(REPO / name)
    for folder in ('apps/api', 'apps/cli', 'tests/CsDemoMap.Api.Tests'):
        paths.add(REPO / folder / 'obj/project.assets.json')
    return sorted(paths)


def binding(path):
    return dict(logicalPath='repo:' + path.relative_to(REPO).as_posix(), sha256=digest(path))


def verify(bindings):
    for b in bindings:
        if digest(REPO / b['logicalPath'].removeprefix('repo:')) != b['sha256']:
            raise ValueError('build-input-or-binary-drift')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence', required=True)
    args = parser.parse_args()
    if Path(args.evidence).exists(): raise ValueError('evidence-already-exists')
    sources = [binding(p) for p in build_input_paths()]
    command = ['dotnet', 'build', 'tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj', '-c', 'Release', '--no-restore', '-p:NuGetAudit=false']
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_GENERATE_ASPNET_CERTIFICATE='false')
    result = subprocess.run(command, cwd=REPO, env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')
    verify(sources)
    binaries = []
    for folder in ('apps/api', 'apps/cli', 'tests/CsDemoMap.Api.Tests'):
        binaries += [binding(p) for p in sorted((REPO / folder / 'bin/Release/net10.0').rglob('*')) if p.is_file()]
    write_new_json(args.evidence, dict(command=command, exitCode=result.returncode, sourceBindings=sources,
                                     binaryBindings=binaries, stdout=result.stdout, stderr=result.stderr))
    print(result.stdout)
    print(result.stderr)
    return result.returncode


if __name__ == '__main__':
    raise SystemExit(main())
