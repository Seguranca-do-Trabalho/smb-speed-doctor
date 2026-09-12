<#
.SYNOPSIS
    Complete repository verification, running LOCALLY.

.DESCRIPTION
    Runs local validation matching CI checks:
      Build       dotnet build -warnaserror
      Tests       dotnet test
      PS 5.1      parse .ps1 scripts in Windows PowerShell 5.1
      BOM         ensure UTF-8 BOM on .ps1 files (required for PS 5.1)
      Secrets     scan complete history for credentials

.PARAMETER Quick
    Skips full rebuild and history scan.

.PARAMETER Only
    Runs a single group: build | test | scripts | secrets

.EXAMPLE
    ./scripts/verify.ps1
    ./scripts/verify.ps1 -Quick
    ./scripts/verify.ps1 -Only scripts

.AUTHOR
    Created by forg3
#>
[CmdletBinding()]
param(
    [switch]$Quick,
    [ValidateSet('build', 'test', 'scripts', 'secrets')]
    [string]$Only
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$script:failures = @()
$script:warnings = @()

function Title($t) {
    Write-Host ""
    Write-Host ("=" * 68) -ForegroundColor DarkGray
    Write-Host "  $t" -ForegroundColor Cyan
    Write-Host ("=" * 68) -ForegroundColor DarkGray
}
function Ok($m)     { Write-Host "  [OK]    $m" -ForegroundColor Green }
function Failure($m){ Write-Host "  [FAIL]  $m" -ForegroundColor Red;    $script:failures += $m }
function Warn($m)   { Write-Host "  [WARN]  $m" -ForegroundColor Yellow; $script:warnings += $m }

function RunStep($group) { return (-not $Only) -or ($Only -eq $group) }

# --------------------------------------------------------------- Build -----
if (RunStep 'build') {
    Title "BUILD (warnings = error)"
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Warn "dotnet not found in PATH; skipping build and tests"
    }
    else {
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $args = @('build', 'SmbSpeedDoctor.sln', '-c', 'Release', '--nologo',
                  '-v', 'minimal', '-warnaserror')
        if (-not $Quick) { $args += '--no-incremental' }
        $output = & dotnet @args 2>&1
        $errors = @($output | Select-String ' error ')
        if ($LASTEXITCODE -eq 0 -and $errors.Count -eq 0) {
            Ok "clean build, zero warnings"
        }
        else {
            Failure "build failed ($($errors.Count) error(s))"
            $errors | Select-Object -First 8 | ForEach-Object { Write-Host "          $_" -ForegroundColor DarkRed }
        }
    }
}

# -------------------------------------------------------------- Tests -----
if (RunStep 'test') {
    Title "TESTS"
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Warn "dotnet missing; tests skipped"
    }
    else {
        $output = & dotnet test SmbSpeedDoctor.sln -c Release --nologo -v quiet 2>&1
        $line = $output | Select-String 'Passed!|Failed!' | Select-Object -First 1
        if ($LASTEXITCODE -eq 0) { Ok ($line -replace '\s+', ' ').Trim() }
        else {
            Failure "tests failed"
            $output | Select-String '\[FAIL\]' | Select-Object -First 10 |
                ForEach-Object { Write-Host "          $_" -ForegroundColor DarkRed }
        }
    }
}

# ------------------------------------------------------------- Scripts -----
if (RunStep 'scripts') {
    Title "POWERSHELL SCRIPTS"

    $ps1 = @(Get-ChildItem scripts -Recurse -Filter *.ps1 -ErrorAction SilentlyContinue)
    if ($ps1.Count -eq 0) { Warn "no .ps1 found in scripts/" }

    # -- UTF-8 BOM --------------------------------------------------------
    $missingBom = @()
    foreach ($f in $ps1) {
        $b = [System.IO.File]::ReadAllBytes($f.FullName)
        if ($b.Length -lt 3 -or $b[0] -ne 0xEF -or $b[1] -ne 0xBB -or $b[2] -ne 0xBF) {
            $missingBom += $f.Name
        }
    }
    if ($missingBom.Count -eq 0) { Ok "all .ps1 have UTF-8 BOM" }
    else { Failure "missing UTF-8 BOM (breaks in PowerShell 5.1): $($missingBom -join ', ')" }

    # -- parse in REAL Windows PowerShell 5.1 ------------------------------
    $ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path $ps51)) {
        Warn "powershell.exe 5.1 not found; skipping 5.1 parse"
    }
    else {
        $tmp = Join-Path $env:TEMP "verify-parse51-$PID.ps1"
        @'
$failCount = 0
Get-ChildItem (Join-Path $args[0] 'scripts') -Recurse -Filter *.ps1 | ForEach-Object {
  $errors = $null
  [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$null, [ref]$errors)
  if ($errors -and $errors.Count) { Write-Output "FAIL|$($_.Name)|$($errors[0].Message)"; $failCount++ }
  else { Write-Output "OK|$($_.Name)|" }
}
exit $failCount
'@ | Set-Content -Path $tmp -Encoding UTF8
        $res = & $ps51 -NoProfile -ExecutionPolicy Bypass -File $tmp $root 2>&1
        $bad = @($res | Where-Object { $_ -like 'FAIL|*' })
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        if ($bad.Count -eq 0) { Ok "$($ps1.Count) script(s) compile in Windows PowerShell 5.1" }
        else {
            Failure "$($bad.Count) script(s) do NOT compile in PowerShell 5.1"
            $bad | ForEach-Object {
                $p = $_ -split '\|'
                Write-Host "          $($p[1]): $($p[2])" -ForegroundColor DarkRed
            }
        }
    }
}

# ------------------------------------------------------------- Secrets -----
if (RunStep 'secrets') {
    Title "SECRETS"

    $patterns = @(
        @{ name = 'GitHub PAT (ghp_)';       re = 'ghp_[A-Za-z0-9]{30,}' },
        @{ name = 'Fine-grained PAT (github_pat_)'; re = 'github_pat_[A-Za-z0-9_]{30,}' },
        @{ name = 'OAuth token (gho_)';     re = 'gho_[A-Za-z0-9]{30,}' },
        @{ name = 'Private key';            re = 'BEGIN [A-Z ]*PRIVATE KEY' },
        @{ name = 'Embedded password';      re = '(?i)(password|passwd)\s*[:=]\s*["\x27][^"\x27]{3,}' }
    )

    # working tree
    $found = @()
    foreach ($p in $patterns) {
        $r = & git grep -nEI $p.re -- . 2>$null
        if ($r) { $found += "$($p.name): $($r | Select-Object -First 3)" }
    }
    if ($found.Count -eq 0) { Ok "no secrets in working tree" }
    else { $found | ForEach-Object { Failure "working tree: $_" } }

    # full history
    if ($Quick) {
        Warn "-Quick: history scan skipped"
    }
    elseif (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Warn "git missing; history scan skipped"
    }
    else {
        $hist = @()
        foreach ($p in $patterns) {
            $r = & git log -p --all -G $p.re --oneline 2>$null | Select-Object -First 1
            if ($r) { $hist += "$($p.name) in $r" }
        }
        if ($hist.Count -eq 0) { Ok "no secrets in complete history" }
        else { $hist | ForEach-Object { Failure "history: $_" } }
    }
}

# -------------------------------------------------------------- Summary -----
Title "SUMMARY"
if ($warnings.Count) {
    Write-Host "  warnings ($($warnings.Count)):" -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host "    - $_" -ForegroundColor Yellow }
}
if ($failures.Count -eq 0) {
    Write-Host ""
    Write-Host "  ALL GREEN" -ForegroundColor Green
    Write-Host ""
    exit 0
}
Write-Host ""
Write-Host "  $($failures.Count) FAILURE(S):" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
Write-Host ""
exit 1
