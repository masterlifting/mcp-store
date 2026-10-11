# role: entrypoint/command
# Runs the focused deterministic release regression suite non-publishing; a nonzero child fails the gate.
# Historical.fsx is the optional integration that needs locally downloaded archives, so it never gates CI.

[CmdletBinding()]
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"

# Read every child exit code explicitly so a native-command error preference cannot abort the loop early.
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

# Capture the workflow output target and its bytes so a child cannot redirect or append them.
function Get-OutputState([string]$Name) {
    $target = [System.Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrEmpty($target)) { return "<unset>" }
    if (-not (Test-Path -LiteralPath $target)) { return "target=$target;absent" }
    return "target=$target;content=$(Get-Content -LiteralPath $target -Raw -ErrorAction SilentlyContinue)"
}
$outputBefore = @{}
foreach ($name in @("GITHUB_ENV", "GITHUB_OUTPUT")) {
    $outputBefore[$name] = Get-OutputState $name
}

$tests = Join-Path $Root "infrastructure\release\tests"
if (-not (Test-Path -LiteralPath $tests)) {
    throw "release regression test directory not found: $tests"
}

# Only `test entrypoint` scripts are test cases; a `test helper` module must not run standalone.
$scripts = @(
    Get-ChildItem -LiteralPath $tests -Filter *.fsx |
        Where-Object {
            $_.Name -ne "Historical.fsx" -and
            (Get-Content -LiteralPath $_.FullName -TotalCount 1) -match '^//\s*role:\s*test entrypoint\s*$'
        } |
        Sort-Object Name
)

if ($scripts.Count -eq 0) {
    throw "no deterministic release regression tests found under $tests"
}

$failed = New-Object System.Collections.Generic.List[string]
Push-Location $Root
try {
    foreach ($script in $scripts) {
        Write-Host "::group::release regression $($script.Name)"
        & dotnet fsi --nologo $script.FullName
        $code = $LASTEXITCODE
        Write-Host "::endgroup::"
        if ($code -ne 0) {
            $failed.Add("$($script.Name) (exit $code)")
        }
    }
} finally {
    Pop-Location
}

foreach ($name in @("GITHUB_ENV", "GITHUB_OUTPUT")) {
    if ((Get-OutputState $name) -ne $outputBefore[$name]) {
        $failed.Add("$name was redirected or written during the suite")
    }
}

if ($failed.Count -gt 0) {
    Write-Host "::error::release regression suite failed: $($failed -join ', ')"
    exit 1
}

Write-Host "release regression suite passed: $($scripts.Count) deterministic script(s)"
exit 0
