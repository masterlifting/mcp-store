# role: entrypoint/command
# Deterministic cross-stage guard: the workflow matrix, the bootstrap toolchain projection, the fresh prepare/builder/publish job CLI paths and the roster must describe the same participants.
# Marker checks are anchored and fail closed, so a hand-edited list, a renamed output, a re-enabled publication job or a retired CLI path can never pass silently.

[CmdletBinding()]
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Workflow = "",
    [string]$Components = ""
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

# A job body runs from its two-space key to the next two-space key, so a nested `steps:`/`matrix:` key can never be mistaken for a job boundary.
function Get-JobBlock {
    param([Parameter(Mandatory = $true)][string]$Name)

    $match = [regex]::Match($text, ('(?ms)^  ' + [regex]::Escape($Name) + ':\r?\n(?<body>.*?)(?=^  \S|\z)'))
    if (-not $match.Success) {
        throw "cross-stage coherence failed [job block]: the workflow declares no '$Name' job"
    }

    return $match.Groups['body'].Value
}

# Per-job assertions keep a rule scoped to the job that must obey it; a global count cannot prove that.
function Assert-JobCount {
    param(
        [Parameter(Mandatory = $true)][string]$Job,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][int]$Expected
    )

    $script:checked = $script:checked + 1
    $body = Get-JobBlock -Name $Job
    $found = [regex]::Matches($body, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline).Count

    if ($found -ne $Expected) {
        throw "cross-stage coherence failed [$Name] in job '$Job': expected $Expected match(es) of '$Pattern'; found $found"
    }
}

# Participation must flow through the roster projection in every stage; literals in these slots are drift.
Assert-Count -Name "bootstrap components output" -Pattern '^ {6}components: \$\{\{ steps\.project\.outputs\.components \}\}\s*$' -Expected 1
Assert-Count -Name "release matrix from roster projection" -Pattern '^ {8}component: \$\{\{ fromJSON\(needs\.bootstrap\.outputs\.components\) \}\}\s*$' -Expected 1
Assert-Count -Name "tests job waits for the projection" -Pattern '^ {4}needs: bootstrap\s*$' -Expected 1
Assert-Count -Name "fixture step consumes the projection" -Pattern '^ {10}RELEASE_COMPONENTS: \$\{\{ needs\.bootstrap\.outputs\.components \}\}\s*$' -Expected 1
# The guard receives the projection as a value, because a step's own GITHUB_OUTPUT file holds only that step's writes.
Assert-Count -Name "coherence guard projection input" -Pattern '^ {10}PROJECTED_COMPONENTS: \$\{\{ steps\.project\.outputs\.components \}\}\s*$' -Expected 1
Assert-Count -Name "coherence guard consumes the projected components" -Pattern 'Coherence\.ps1 -Components \$env:PROJECTED_COMPONENTS\s*$' -Expected 1
Assert-Count -Name "coherence guard reads no step-local output file" -Pattern 'Coherence\.ps1 -OutputFile' -Expected 0
# Every job that installs an SDK verifies the executing toolchain roster-wide before it builds anything.
Assert-Count -Name "toolchain verification is roster-wide" -Pattern 'Update\.ps1 -Verify\s*$' -Expected 3
# Toolchain verification appears only in the jobs that install an SDK and build.
Assert-Count -Name "toolchain verification stays in building jobs" -Pattern 'Update\.ps1' -Expected 4
Assert-Count -Name "publication verification names no participant literal" -Pattern 'Update\.ps1 -Verify -Component' -Expected 0
# The retired credential-bearing orchestration mode is removed, not aliased.
Assert-Count -Name "retired orchestration mode is gone" -Pattern 'orchestrate' -Expected 0
Assert-Count -Name "no hardcoded participant list" -Pattern '^\s*-\s*(dotnet|workflow)\s*$' -Expected 0
Assert-Count -Name "SDK installs never name a literal version" -Pattern 'dotnet-version: (?!\$\{\{)' -Expected 0

# Every action reference must be a reviewed 40-hex commit; a floating tag would let an upstream change reach the publication boundary.
Assert-Count -Name "all action references are pinned commits" -Pattern 'uses: [A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@[0-9a-f]{40} ' -Expected 12
Assert-Count -Name "no floating or short action reference" -Pattern 'uses: [A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@(?![0-9a-f]{40})' -Expected 0
Assert-Count -Name "checkout uses the reviewed pinned commit" -Pattern 'uses: actions/checkout@11d5960a326750d5838078e36cf38b85af677262' -Expected 4

# The publication token and write authority are absent until the trusted-receipt path lands; both stay disabled.
Assert-Count -Name "publication token appears in no job" -Pattern 'secrets\.GITHUB_TOKEN' -Expected 0
Assert-Count -Name "write authority stays in the publication scope" -Pattern '^ {6}contents: write\s*$' -Expected 1
Assert-Count -Name "write authority is not granted at workflow scope" -Pattern '^permissions:\r?\n {2}contents: read\s*$' -Expected 1
Assert-Count -Name "queue serialization is retained" -Pattern '^ {2}queue: max\s*$' -Expected 1
Assert-Count -Name "queue cancellation stays disabled" -Pattern '^ {2}cancel-in-progress: false\s*$' -Expected 1

# Job-scoped invariants: fresh hosted read-only preparation and builder, and a publication job that compiles nothing.
Assert-JobCount -Job "bootstrap" -Name "bootstrap projects the canonical toolchain" -Pattern 'Update\.ps1 -Project\s*$' -Expected 1
Assert-JobCount -Job "bootstrap" -Name "bootstrap checkout disables credential persistence" -Pattern 'persist-credentials: false\s*$' -Expected 1
Assert-JobCount -Job "tests" -Name "tests job is read-only" -Pattern 'contents: read\s*$' -Expected 1
Assert-JobCount -Job "tests" -Name "ten non-packaging Workflow entrypoints" -Pattern '^\s+"workflow/tests/[A-Za-z]+\.fsx",?\s*$' -Expected 10
Assert-JobCount -Job "prepare" -Name "prepare job is read-only" -Pattern 'contents: read\s*$' -Expected 1
Assert-JobCount -Job "prepare" -Name "prepare CLI path derives from the matrix participant" -Pattern 'prepare "\$\{\{ matrix\.component \}\}/release/Config\.fsx"' -Expected 1
Assert-JobCount -Job "prepare" -Name "prepare CLI path has no participant literal" -Pattern 'prepare "(dotnet|workflow)/release/Config\.fsx"' -Expected 0
Assert-JobCount -Job "prepare" -Name "prepare job holds no publication token" -Pattern 'secrets\.GITHUB_TOKEN' -Expected 0
Assert-JobCount -Job "prepare" -Name "prepare uploads the exact candidate transport" -Pattern 'name: release-candidate-\$\{\{ matrix\.component \}\}\s*$' -Expected 1
Assert-JobCount -Job "builder" -Name "builder job is read-only" -Pattern 'contents: read\s*$' -Expected 1
Assert-JobCount -Job "builder" -Name "builder stages the audited closure" -Pattern '-SourcesPath "infrastructure/release/Sources\.json"' -Expected 1
Assert-JobCount -Job "builder" -Name "builder job holds no publication token" -Pattern 'secrets\.GITHUB_TOKEN' -Expected 0
Assert-JobCount -Job "builder" -Name "builder uploads the runtime transport" -Pattern 'name: release-publisher\s*$' -Expected 1
Assert-JobCount -Job "builder" -Name "builder uploads the trusted receipt separately" -Pattern 'name: trusted-publisher-receipt\s*$' -Expected 1
Assert-JobCount -Job "publish" -Name "publish job alone holds write authority" -Pattern 'contents: write\s*$' -Expected 1
Assert-JobCount -Job "publish" -Name "publish job keeps platform artifact read" -Pattern 'actions: read\s*$' -Expected 1
Assert-JobCount -Job "publish" -Name "publish job never installs an SDK" -Pattern 'setup-dotnet' -Expected 0
Assert-JobCount -Job "publish" -Name "publish job never builds" -Pattern 'dotnet build' -Expected 0
Assert-JobCount -Job "publish" -Name "publish job never evaluates F#" -Pattern 'dotnet fsi' -Expected 0
Assert-JobCount -Job "publish" -Name "publish job never runs tests" -Pattern 'dotnet test' -Expected 0
Assert-JobCount -Job "publish" -Name "publish job never verifies a toolchain" -Pattern 'Update\.ps1' -Expected 0
Assert-JobCount -Job "publish" -Name "publication stays disabled until the trusted-receipt path lands" -Pattern '^ {4}if: false\s*$' -Expected 1
Assert-JobCount -Job "publish" -Name "publication reports the blocked wiring marker" -Pattern 'publisher-wiring-not-enabled' -Expected 1

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

# The workflow passes the project step's actual projection value; a step-local GITHUB_OUTPUT file belongs to a different step.
if ([string]::IsNullOrWhiteSpace($Components)) {
    throw "cross-stage coherence failed [projected components]: -Components must carry the bootstrap projection value"
}

$checked = $checked + 1
$projected = @(ConvertFrom-Json $Components)
$expected = @($componentIds | Sort-Object)
$actual = @($projected | Sort-Object)

if (($expected -join ',') -ne ($actual -join ',')) {
    throw "cross-stage coherence failed [projection output]: projected participants [$($actual -join ', ')] do not equal the roster [$($expected -join ', ')]"
}

Write-Host "cross-stage coherence ok: $checked assertions; roster=[$($componentIds -join ', ')]"
exit 0
