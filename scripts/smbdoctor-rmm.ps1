# SMB Speed Doctor — wrapper for RMM (Action1, NinjaOne, Datto RMM, etc.)
# License: MIT
# Created by forg3
#
# Typical workflow:
#   1) Deploy smbdoctor-cli.exe binary via RMM software deployment.
#   2) Run this script (PowerShell) on each managed endpoint.
#   3) Read JSON output in RMM log; exit code indicates bottleneck (0=ok, 2=critical).
#
# Note: this wrapper is public/MIT code.

[CmdletBinding()]
param(
    # Share to measure. Without it there is NO test copy and therefore no
    # throughput measurement: diagnosis cannot conclude about signing,
    # multichannel, disk, or CPU. For RMM usage, point to a real share.
    [Parameter(Mandatory = $false)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'

# Binary is published win-x64. Search both Program Files and PATH
$candidates = @(
    (Join-Path ${env:ProgramFiles} 'SMB Speed Doctor\smbdoctor-cli.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'SMB Speed Doctor\smbdoctor-cli.exe')
) | Where-Object { $_ -and (Test-Path $_) }

$exe = $candidates | Select-Object -First 1
if (-not $exe) {
    $onPath = Get-Command 'smbdoctor-cli.exe' -ErrorAction SilentlyContinue
    if ($onPath) { $exe = $onPath.Source }
}

if (-not $exe) {
    Write-Host '{"error":"binary not installed","searched":["%ProgramFiles%","%ProgramFiles(x86)%","PATH"]}'
    exit 1
}

$smbArgs = @('scan', '--json', '--quiet')
if ($Path) { $smbArgs += @('--path', $Path) }

$out = & $exe @smbArgs 2>&1
$exitFromExe = $LASTEXITCODE

try {
    $json = $out | ConvertFrom-Json
}
catch {
    # Non-JSON output indicates pre-diagnosis failure. Propagate as error
    $raw = ($out | Out-String).Trim()
    Write-Host (@{ error = 'non-JSON output from binary'; raw = $raw } | ConvertTo-Json -Compress)
    exit 1
}

Write-Host ($json | ConvertTo-Json -Depth 10)

# Prefer binary exit code; fall back to JSON if unavailable.
if ($null -ne $exitFromExe) { exit $exitFromExe }
exit $json.exitCode
