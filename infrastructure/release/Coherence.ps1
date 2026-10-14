# role: entrypoint/command
# Deterministic cross-stage guard: the workflow matrix, the bootstrap toolchain projection, the fresh prepare/builder/publish job CLI paths and the roster must describe the same participants.
# Marker checks are anchored and fail closed, so a hand-edited list, a renamed output, a re-enabled producer token or a retired CLI path can never pass silently.

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
# Preparation and publication both take their participant list from the projection, so one shared key can never be overridden per leg.
Assert-Count -Name "release matrix from roster projection" -Pattern '^ {8}component: \$\{\{ fromJSON\(needs\.bootstrap\.outputs\.components\) \}\}\s*$' -Expected 2
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
# The retired credential-bearing orchestration mode and its superseded preparation flag are removed, not aliased.
Assert-Count -Name "retired orchestration mode is gone" -Pattern 'orchestrate' -Expected 0
Assert-Count -Name "the superseded preparation receipt flag is gone" -Pattern 'preparation-receipt' -Expected 0
Assert-Count -Name "no hardcoded participant list" -Pattern '^\s*-\s*(dotnet|workflow)\s*$' -Expected 0
Assert-Count -Name "SDK installs never name a literal version" -Pattern 'dotnet-version: (?!\$\{\{)' -Expected 0

# Every action reference must be a reviewed 40-hex commit; a floating tag would let an upstream change reach the publication boundary.
Assert-Count -Name "all action references are pinned commits" -Pattern 'uses: [A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@[0-9a-f]{40} ' -Expected 12
Assert-Count -Name "no floating or short action reference" -Pattern 'uses: [A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@(?![0-9a-f]{40})' -Expected 0
Assert-Count -Name "checkout uses the reviewed pinned commit" -Pattern 'uses: actions/checkout@11d5960a326750d5838078e36cf38b85af677262' -Expected 5
Assert-Count -Name "the platform upload action is the reviewed pinned commit" -Pattern 'uses: actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02' -Expected 3

# No repository secret is read: the platform workflow token is the only credential, it stays a runtime environment value, and the anonymous fixture fetch binds none of it.
Assert-Count -Name "no repository secret is read by the release workflow" -Pattern 'secrets\.' -Expected 0
Assert-Count -Name "the platform workflow token is the only credential value" -Pattern 'github\.token' -Expected 9
Assert-Count -Name "write authority stays in the publication scope" -Pattern '^ {6}contents: write\s*$' -Expected 1
Assert-Count -Name "write authority is not granted at workflow scope" -Pattern '^permissions:\r?\n {2}contents: read\s*$' -Expected 1
Assert-Count -Name "queue serialization is retained" -Pattern '^ {2}queue: max\s*$' -Expected 1
Assert-Count -Name "queue cancellation stays disabled" -Pattern '^ {2}cancel-in-progress: false\s*$' -Expected 1
# Both component legs share one publication group, so the two producers never publish concurrently and a pending leg is never replaced.
Assert-Count -Name "the publication group serializes both component legs" -Pattern '^ {6}group: release-publication\s*$' -Expected 1
Assert-Count -Name "the publication group queues instead of replacing a pending leg" -Pattern '^ {6}queue: max\s*$' -Expected 1

# The two transports are three direct members each, never a nested wrapper and never a separate courier artifact.
Assert-Count -Name "the runtime transport is not a nested wrapper" -Pattern 'publisher-transport\.zip' -Expected 0
Assert-Count -Name "the trusted receipt has no separate courier artifact" -Pattern 'name: trusted-publisher-receipt' -Expected 0
Assert-Count -Name "the preparation metadata has no separate courier artifact" -Pattern 'name: candidate-preparation-' -Expected 0
Assert-Count -Name "no artifact is selected by a floating download pattern" -Pattern 'actions/download-artifact' -Expected 0
Assert-Count -Name "no candidate upload relies on a zip wildcard" -Pattern '\*\.zip' -Expected 0

# Job-scoped invariants: fresh hosted read-only preparation and builder, and a publication job that compiles nothing.
Assert-JobCount -Job "bootstrap" -Name "bootstrap projects the canonical toolchain" -Pattern 'Update\.ps1 -Project\s*$' -Expected 1
Assert-JobCount -Job "bootstrap" -Name "bootstrap checkout disables credential persistence" -Pattern 'persist-credentials: false\s*$' -Expected 1
Assert-JobCount -Job "tests" -Name "tests job is read-only" -Pattern 'contents: read\s*$' -Expected 1
Assert-JobCount -Job "tests" -Name "twelve non-packaging Workflow entrypoints" -Pattern '^\s+"workflow/tests/[A-Za-z]+\.fsx",?\s*$' -Expected 12
Assert-JobCount -Job "prepare" -Name "prepare job is read-only" -Pattern 'contents: read\s*$' -Expected 1
Assert-JobCount -Job "prepare" -Name "prepare CLI path derives from the matrix participant" -Pattern 'prepare "\$\{\{ matrix\.component \}\}/release/Config\.fsx"' -Expected 1
Assert-JobCount -Job "prepare" -Name "prepare CLI path has no participant literal" -Pattern 'prepare "(dotnet|workflow)/release/Config\.fsx"' -Expected 0
Assert-JobCount -Job "prepare" -Name "prepare job holds no publication token" -Pattern 'github\.token' -Expected 0
Assert-JobCount -Job "prepare" -Name "prepare job never disguises a missing archive name" -Pattern 'candidateArchiveName_\$component' -Expected 2
Assert-JobCount -Job "prepare" -Name "prepare uploads the exact candidate transport" -Pattern 'name: release-candidate-\$\{\{ matrix\.component \}\}\s*$' -Expected 1
Assert-JobCount -Job "prepare" -Name "prepare uploads exactly the three direct candidate members" -Pattern '^\s+\$\{\{ runner\.temp \}\}/candidate/' -Expected 3
Assert-JobCount -Job "prepare" -Name "prepare reads only its own upload outputs" -Pattern 'UPLOAD_OUTPUTS: \$\{\{ toJSON\(steps\.upload\.outputs\) \}\}' -Expected 1
Assert-JobCount -Job "builder" -Name "builder job is read-only" -Pattern 'contents: read\s*$' -Expected 1
Assert-JobCount -Job "builder" -Name "builder stages the audited closure" -Pattern '-SourcesPath "infrastructure/release/Sources\.json"' -Expected 1
Assert-JobCount -Job "builder" -Name "builder job holds no publication token" -Pattern 'github\.token' -Expected 0
Assert-JobCount -Job "builder" -Name "builder uploads the runtime transport" -Pattern 'name: release-publisher\s*$' -Expected 1
Assert-JobCount -Job "builder" -Name "builder uploads exactly the three direct runtime members" -Pattern '^\s+\$\{\{ steps\.build\.outputs\.(envelopePath|manifestPath|trustedReceiptPath) \}\}\s*$' -Expected 3
Assert-JobCount -Job "builder" -Name "builder exposes the independent receipt hash" -Pattern '^ {6}receiptSha256: \$\{\{ steps\.build\.outputs\.receiptSha256 \}\}\s*$' -Expected 1
Assert-JobCount -Job "builder" -Name "builder exposes the exact outer entry set" -Pattern '^ {6}expectedOuterEntries: \$\{\{ steps\.build\.outputs\.expectedOuterEntries \}\}\s*$' -Expected 1
Assert-JobCount -Job "publish" -Name "publish job alone holds write authority" -Pattern 'contents: write\s*$' -Expected 1
Assert-JobCount -Job "publish" -Name "publish job keeps platform artifact read" -Pattern 'actions: read\s*$' -Expected 1
Assert-JobCount -Job "publish" -Name "publish job never installs an SDK" -Pattern 'setup-dotnet' -Expected 0
Assert-JobCount -Job "publish" -Name "publish job never builds" -Pattern 'dotnet build' -Expected 0
Assert-JobCount -Job "publish" -Name "publish job never evaluates F#" -Pattern 'dotnet fsi' -Expected 0
Assert-JobCount -Job "publish" -Name "publish job never runs tests" -Pattern 'dotnet test' -Expected 0
Assert-JobCount -Job "publish" -Name "publish job never verifies a toolchain" -Pattern 'Update\.ps1' -Expected 0
Assert-JobCount -Job "publish" -Name "publication is enabled" -Pattern '^ {4}if: false\s*$' -Expected 0
Assert-JobCount -Job "publish" -Name "publication installs the runtime in InstallRuntime mode exactly once" -Pattern '-Mode InstallRuntime' -Expected 1
Assert-JobCount -Job "publish" -Name "publication authenticates the sealed receipt expectation exactly once" -Pattern '-TrustedReceiptPath \$sealedReceipt' -Expected 1
Assert-JobCount -Job "publish" -Name "publication never passes a raw transport to the runtime install" -Pattern '\s-RawTransportPath \$rawTransport' -Expected 1
Assert-JobCount -Job "publish" -Name "publication fetches the candidate transport exactly once" -Pattern '-Mode FetchCandidate' -Expected 1
Assert-JobCount -Job "publish" -Name "publication selects its own component keys" -Pattern '"(candidateArtifactId|candidateArtifactDigest|candidateArchiveName)_\$component"' -Expected 3
Assert-JobCount -Job "publish" -Name "publication binds only its own declared static outputs" -Pattern 'Read-DeclaredOutput \$candidate "candidateArtifact(Id|Digest)_\$component"' -Expected 2
Assert-JobCount -Job "publish" -Name "publication resolves both uploader job ids through the bounded transport resolver" -Pattern 'infrastructure/release/Resolve\.ps1' -Expected 2
Assert-JobCount -Job "publish" -Name "publication rechecks the exact event SHA and safe main ancestry before the native invocation" -Pattern 'git rev-parse HEAD' -Expected 2
Assert-JobCount -Job "publish" -Name "publication invokes the installed runtime by its exact reviewed name" -Pattern '& \./ReleaseRunner\.exe publish' -Expected 1
Assert-JobCount -Job "publish" -Name "publication supplies the trusted receipt on its own sealed path" -Pattern '--trusted-receipt=\$env:PUBLISHER_SEALED_RECEIPT' -Expected 1
Assert-JobCount -Job "publish" -Name "publication supplies the candidate transport evidence on its own path" -Pattern '--transport-evidence=\$env:FETCH_RECEIPT_PATH' -Expected 1
Assert-JobCount -Job "publish" -Name "publication keeps the required candidate role" -Pattern '--role=candidate' -Expected 1
Assert-JobCount -Job "publish" -Name "publication requires both write authorities in the invoking step alone" -Pattern 'MCP_RELEASE_DO_UPLOAD: "true"' -Expected 1
Assert-JobCount -Job "publish" -Name "publication retains the runner log for the always-run result step" -Pattern 'Tee-Object -FilePath \$statusLog' -Expected 1
Assert-JobCount -Job "publish" -Name "publication preserves the native component exit" -Pattern '^ {10}exit \$code\s*$' -Expected 1
Assert-JobCount -Job "publish" -Name "publication records the result unconditionally" -Pattern '^ {8}if: always\(\)\s*$' -Expected 2
Assert-JobCount -Job "publish" -Name "publication uploads one verified metadata artifact" -Pattern 'name: release-metadata-\$\{\{ matrix\.component \}\}\s*$' -Expected 1

# The static per-component output keys are derived from the roster: a participant added to the roster without its declared key fails here.
foreach ($entry in $roster) {
    $componentId = $entry.componentId
    foreach ($prefix in @("candidateArtifactId_", "candidateArtifactDigest_", "candidateArchiveName_")) {
        $script:checked = $script:checked + 1
        $key = "$prefix$componentId"
        $pattern = '^ {6}' + [regex]::Escape($key) + ': \$\{\{ steps\.identity\.outputs\.' + [regex]::Escape($key) + ' \}\}\s*$'
        $declared = [regex]::Matches($text, $pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline).Count

        if ($declared -ne 1) {
            throw "cross-stage coherence failed [static candidate output key]: expected exactly one declared output '$key'; found $declared"
        }
    }
}

# No producer may own a static candidate output key the roster does not declare.
foreach ($prefix in @("candidateArtifactId_", "candidateArtifactDigest_", "candidateArchiveName_")) {
    $script:checked = $script:checked + 1
    $declared = [regex]::Matches($text, ('^ {6}' + $prefix), [System.Text.RegularExpressions.RegexOptions]::Multiline).Count

    if ($declared -ne $componentIds.Count) {
        throw "cross-stage coherence failed [static candidate output inventory]: the workflow declares $declared '$prefix' outputs for $($componentIds.Count) roster participant(s)"
    }
}

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
