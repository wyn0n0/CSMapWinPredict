param(
    [Parameter(Mandatory = $true)][string]$StorageRoot,
    [Parameter(Mandatory = $true)][string]$Python311,
    [switch]$Plan
)
$ErrorActionPreference = 'Stop'
$setupArgs = @((Join-Path $PSScriptRoot 'setup_situation_env.py'), '--storage-root', $StorageRoot, '--python311', $Python311)
if ($Plan) { $setupArgs += '--plan' }
& $Python311 @setupArgs
if ($LASTEXITCODE -ne 0) { throw "Situation environment setup failed (exit $LASTEXITCODE)." }
