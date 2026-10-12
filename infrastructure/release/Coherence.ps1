# role: entrypoint/command
# Deterministic cross-stage guard: the workflow matrix, the bootstrap toolchain projection, the CLI entry path and the roster must describe the same participants.
# Marker checks are anchored and fail closed, so a hand-edited list or a renamed output can never pass silently.

[CmdletBinding()]
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Workflow = "",
    [string]$OutputFile = ""
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Roster.ps1")

$roster = Get-ReleaseRoster -Root $Root
$componentIds = @($roster | ForEach-Object { $_.componentId })
$workflowPath = if ($Workflow) { $Workflow } else { Join-Path $Root ".github\workflows\release.yml" }

if (-not (Test-Path -LiteralPath $workflowPath)) {
    throw "release workflow not found: $workflowPath"
}

$text = Get-Content -LiteralPath $workflowPath -Raw
$checked = 0

function Assert-Count {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][int]$Expected
    )

    $script:checked = $script:checked + 1
    $found = [regex]::Matches($text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline).Count

    if ($found -ne $Expected) {
        throw "cross-stage coherence failed [$Name]: expected $Expected match(es) of '$Pattern' in $workflowPath; found $found"
    }
}

# Participation must flow through the roster projection in every stage; literals in these slots are drift.
Assert-Count -Name "bootstrap components output" -Pattern '^ {6}components: \$\{\{ steps\.project\.outputs\.components \}\}\s*$' -Expected 1
Assert-Count -Name "release matrix from roster projection" -Pattern '^ {8}component: \$\{\{ fromJSON\(needs\.bootstrap\.outputs\.components\) \}\}\s*$' -Expected 1
Assert-Count -Name "tests job waits for the projection" -Pattern '^ {4}needs: bootstrap\s*$' -Expected 1
Assert-Count -Name "fixture step consumes the projection" -Pattern '^ {10}RELEASE_COMPONENTS: \$\{\{ needs\.bootstrap\.outputs\.components \}\}\s*$' -Expected 1
Assert-Count -Name "bootstrap verification is roster-wide" -Pattern 'Update\.ps1 -Verify\s*$' -Expected 1
Assert-Count -Name "publication verification uses the matrix participant" -Pattern 'Update\.ps1 -Verify -Component \$\{\{ matrix\.component \}\}\s*$' -Expected 1
Assert-Count -Name "publication verification names no participant literal" -Pattern 'Update\.ps1 -Verify -Component (?!\$\{\{)' -Expected 0
Assert-Count -Name "transaction CLI path derives from the matrix participant" -Pattern 'orchestrate "\$\{\{ matrix\.component \}\}/release/Config\.fsx"' -Expected 1
Assert-Count -Name "transaction CLI path has no participant literal" -Pattern 'orchestrate "(dotnet|workflow)/release/Config\.fsx"' -Expected 0
Assert-Count -Name "no hardcoded participant list" -Pattern '^\s*-\s*(dotnet|workflow)\s*$' -Expected 0
Assert-Count -Name "SDK installs never name a literal version" -Pattern 'dotnet-version: (?!\$\{\{)' -Expected 0

# Every setup-dotnet use must be fed by the canonical projection, not by a copied value.
$usesCount = [regex]::Matches($text, 'uses: actions/setup-dotnet@').Count
$projectedVersionCount = [regex]::Matches($text, 'dotnet-version: \$\{\{ (?:steps\.project|needs\.bootstrap)\.outputs\.sdk \}\}').Count
$checked = $checked + 1

if ($usesCount -lt 1 -or $usesCount -ne $projectedVersionCount) {
    throw "cross-stage coherence failed [SDK install projection]: $usesCount setup-dotnet use(s) but $projectedVersionCount projected dotnet-version value(s) in $workflowPath"
}

# The CLI receives `<componentId>/release/Config.fsx`; the roster must register exactly that path per participant.
foreach ($entry in $roster) {
    $checked = $checked + 1
    $derived = "$($entry.componentId)/release/Config.fsx"

    if ($derived -ne $entry.configPath) {
        throw "cross-stage coherence failed [CLI entry registration]: participant '$($entry.componentId)' registers '$($entry.configPath)' but the workflow CLI path derives '$derived'"
    }
}

# When the projection output exists, the emitted participant list must equal the roster.
if ($OutputFile) {
    $checked = $checked + 1

    if (-not (Test-Path -LiteralPath $OutputFile)) {
        throw "cross-stage coherence failed [projection output]: $OutputFile does not exist"
    }

    $line = @(Get-Content -LiteralPath $OutputFile | Where-Object { $_ -match '^components=' }) | Select-Object -Last 1
    if (-not $line) {
        throw "cross-stage coherence failed [projection output]: no 'components=' entry in $OutputFile"
    }

    $projected = @(ConvertFrom-Json ($line -replace '^components=', ''))
    $expected = @($componentIds | Sort-Object)
    $actual = @($projected | Sort-Object)

    if (($expected -join ',') -ne ($actual -join ',')) {
        throw "cross-stage coherence failed [projection output]: projected participants [$($actual -join ', ')] do not equal the roster [$($expected -join ', ')]"
    }
}

Write-Host "cross-stage coherence ok: $checked assertions; roster=[$($componentIds -join ', ')]"
exit 0
