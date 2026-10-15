# role: entrypoint/command
# Deterministic cross-stage guard: the three workflow jobs, the action pins, the artifact-id bindings, the CLI paths
# and the enrollment roster must all describe the same participants. Marker checks are anchored and fail closed, so a
# hand-edited list, a renamed output, a re-enabled credential or a retired CLI path can never pass silently.

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

# A job body runs from its two-space key to the next two-space key, so a nested `steps:`/`concurrency:` key is never
# mistaken for a job boundary.
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

function Assert-Before {
    param(
        [Parameter(Mandatory = $true)][string]$Job,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$First,
        [Parameter(Mandatory = $true)][string]$Second
    )

    $script:checked = $script:checked + 1
    $body = Get-JobBlock -Name $Job
    $firstIndex = $body.IndexOf($First, [System.StringComparison]::Ordinal)
    $secondIndex = $body.IndexOf($Second, [System.StringComparison]::Ordinal)

    if ($firstIndex -lt 0 -or $secondIndex -lt 0) {
        throw "cross-stage coherence failed [$Name] in job '$Job': '$First' or '$Second' is absent"
    }
    if ($firstIndex -gt $secondIndex) {
        throw "cross-stage coherence failed [$Name] in job '$Job': '$First' must precede '$Second'"
    }
}

# The workflow trigger stays push-to-main only: a path filter or a manual dispatch would let an unevaluated revision release.
Assert-Count -Name "push to main is the only trigger" -Pattern '^on:\r?\n  push:\r?\n    branches:\r?\n      - main\s*$' -Expected 1
Assert-Count -Name "no path filter can skip packaged bytes" -Pattern '^ {4}paths:' -Expected 0
Assert-Count -Name "no manual dispatch bypasses the push gate" -Pattern 'workflow_dispatch' -Expected 0

# Exactly the three frozen jobs exist; no bootstrap, builder or legacy test/prepare/publish job survives.
Assert-Count -Name "the publisher job exists" -Pattern '^ {2}publisher:\s*$' -Expected 1
Assert-Count -Name "the preparation job exists" -Pattern '^ {2}preparation:\s*$' -Expected 1
Assert-Count -Name "the publication job exists" -Pattern '^ {2}publication:\s*$' -Expected 1
Assert-Count -Name "no retired job survives" -Pattern '^ {2}(bootstrap|builder|tests|prepare|publish):\s*$' -Expected 0
Assert-Count -Name "the three jobs are the only runners" -Pattern '^ {4}runs-on: windows-latest\s*$' -Expected 3
Assert-Count -Name "only the publication job depends on both other jobs" -Pattern '^ {4}needs:\s*$' -Expected 1

# Participation flows through the projection in every stage: a matrix leg or a literal participant list would drift.
Assert-Count -Name "no matrix leg survives" -Pattern 'matrix\.' -Expected 0
Assert-Count -Name "no matrix strategy survives" -Pattern '^ {4}strategy:' -Expected 0
Assert-Count -Name "no cross-job participation fallback" -Pattern 'fromJSON\(needs\.' -Expected 0
Assert-Count -Name "no hardcoded participant list" -Pattern '^\s*-\s*(dotnet|workflow)\s*$' -Expected 0
Assert-Count -Name "no participant literal in a CLI or policy path" -Pattern '(dotnet|workflow)/release/Config\.json' -Expected 0
Assert-Count -Name "both jobs address the policy by the looped participant" -Pattern '\$component/release/Config\.json' -Expected 2

# Every job projects the canonical toolchain before the SDK exists and verifies the executing toolchain afterwards.
Assert-Count -Name "each job projects the canonical toolchain" -Pattern '^ {8}run: pwsh infrastructure/release/Update\.ps1 -Project\s*$' -Expected 3
Assert-Count -Name "each job verifies the executing toolchain" -Pattern '^ {8}run: pwsh infrastructure/release/Update\.ps1 -Verify\s*$' -Expected 3
Assert-JobCount -Job "publisher" -Name "publisher runs the coherence guard exactly once" -Pattern 'Coherence\.ps1 -Components \$env:PROJECTED_COMPONENTS\s*$' -Expected 1
Assert-Count -Name "the coherence guard receives the projection value" -Pattern '^ {10}PROJECTED_COMPONENTS: \$\{\{ steps\.project\.outputs\.components \}\}\s*$' -Expected 1
Assert-Count -Name "the coherence guard reads no step-local output file" -Pattern 'Coherence\.ps1 -OutputFile' -Expected 0
Assert-Count -Name "every SDK install is fed by the canonical projection" -Pattern '^ {10}dotnet-version: \$\{\{ steps\.project\.outputs\.sdk \}\}\s*$' -Expected 3
Assert-Count -Name "SDK installs never name a literal version" -Pattern 'dotnet-version: (?!\$\{\{)' -Expected 0
Assert-JobCount -Job "publisher" -Name "publisher reads the projected components" -Pattern 'ConvertFrom-Json \$env:RELEASE_COMPONENTS' -Expected 1
Assert-JobCount -Job "preparation" -Name "preparation reads the projected components" -Pattern 'ConvertFrom-Json \$env:RELEASE_COMPONENTS' -Expected 2
Assert-JobCount -Job "publication" -Name "publication reads the projected components" -Pattern 'ConvertFrom-Json \$env:RELEASE_COMPONENTS' -Expected 2

# Every action reference must be a reviewed 40-hex commit; a floating tag would let an upstream change reach the publication boundary.
Assert-Count -Name "all action references are pinned commits" -Pattern 'uses: [A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@[0-9a-f]{40} ' -Expected 13
Assert-Count -Name "no floating or short action reference" -Pattern 'uses: [A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@(?![0-9a-f]{40})' -Expected 0
Assert-Count -Name "checkout uses the reviewed pinned commit" -Pattern 'uses: actions/checkout@11d5960a326750d5838078e36cf38b85af677262' -Expected 3
Assert-Count -Name "SDK setup uses the reviewed pinned commit" -Pattern 'uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9' -Expected 3
Assert-Count -Name "the platform upload action is the reviewed pinned commit" -Pattern 'uses: actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02' -Expected 4
Assert-Count -Name "the platform download action is the reviewed pinned commit" -Pattern 'uses: actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093' -Expected 3
Assert-Count -Name "no floating artifact download reference exists" -Pattern 'uses: actions/download-artifact@(?![0-9a-f]{40} )' -Expected 0
Assert-Count -Name "no artifact is selected by a zip wildcard" -Pattern '\*\.zip' -Expected 0
Assert-Count -Name "no arbitrary artifact name lookup exists" -Pattern 'artifact-name' -Expected 0
# A transfer input other than the exact artifact id and its isolated destination would reintroduce a name, pattern,
# cross-run or merge lookup the frozen contract removed.
Assert-Count -Name "no artifact transfer selects by name, pattern, run id, repository, token or merge" -Pattern '^ {10}(pattern|run-id|repository|github-token|merge-multiple):' -Expected 0
Assert-Count -Name "the publisher runtime is transferred by the pinned action" -Pattern '^ {8}id: downloadPublisher\s*$' -Expected 1
Assert-Count -Name "the runtime transfer is bound to the publisher job's own artifact id" -Pattern '^ {10}artifact-ids: \$\{\{ needs\.publisher\.outputs\.runtimeArtifactId \}\}\s*$' -Expected 1
Assert-Count -Name "the runtime transfer lands in its own isolated root" -Pattern '^ {10}path: \$\{\{ runner\.temp \}\}/runtime\s*$' -Expected 1

# No repository secret is read: the platform workflow token is the only credential and it stays inside the one publication step.
Assert-Count -Name "no repository secret is read by the release workflow" -Pattern 'secrets\.' -Expected 0
Assert-Count -Name "the platform workflow token is the only credential value" -Pattern 'github\.token' -Expected 1
Assert-JobCount -Job "publisher" -Name "the publisher job holds no credential" -Pattern 'github\.token' -Expected 0
Assert-JobCount -Job "preparation" -Name "the preparation job holds no credential" -Pattern 'github\.token' -Expected 0
Assert-JobCount -Job "publication" -Name "only the publication job holds the credential" -Pattern 'github\.token' -Expected 1
Assert-Count -Name "write authority stays in the publication scope" -Pattern '^ {6}contents: write\s*$' -Expected 1
Assert-Count -Name "write authority is not granted at workflow scope" -Pattern '^permissions:\r?\n {2}contents: read\s*$' -Expected 1
Assert-Count -Name "artifact read authority stays in the publication job" -Pattern '^ {6}actions: read\s*$' -Expected 1
Assert-Count -Name "queue serialization is retained" -Pattern '^ {2}queue: max\s*$' -Expected 1
Assert-Count -Name "queue cancellation stays disabled" -Pattern '^ {2}cancel-in-progress: false\s*$' -Expected 1
Assert-Count -Name "the publication group serializes the component publications" -Pattern '^ {6}group: release-publication\s*$' -Expected 1
Assert-Count -Name "the publication group queues instead of replacing a pending run" -Pattern '^ {6}queue: max\s*$' -Expected 1
Assert-Count -Name "the publication group never cancels a pending run" -Pattern '^ {6}cancel-in-progress: false\s*$' -Expected 1

# The retired custom layers are deleted, not aliased: no bootstrap, build, resolve, closure, receipt or envelope path survives here.
Assert-Count -Name "the custom transport bootstrap is gone" -Pattern 'Bootstrap\.(ps1|fsx)' -Expected 0
Assert-Count -Name "the custom builder is gone" -Pattern 'Build\.ps1' -Expected 0
Assert-Count -Name "the uploader-job resolver is gone" -Pattern 'Resolve\.ps1' -Expected 0
Assert-Count -Name "the authored source closure is gone" -Pattern 'Sources\.json' -Expected 0
Assert-Count -Name "the publisher closure is gone" -Pattern 'Publisher\.(json|Closure)' -Expected 0
Assert-Count -Name "the F# policy config is gone" -Pattern 'Config\.fsx' -Expected 0
Assert-Count -Name "no preparation sidecar is transported" -Pattern 'preparation\.json' -Expected 0
Assert-Count -Name "no receipt document or receipt path survives" -Pattern '(?i)(trusted-receipt|receipt\.json|receiptPath|receiptRoot|InstallReceipt)' -Expected 0
Assert-Count -Name "no raw platform transport mode survives" -Pattern '(InstallRuntime|FetchCandidate|RawTransportPath|-Mode )' -Expected 0
Assert-Count -Name "the retired orchestration mode is gone" -Pattern 'orchestrate' -Expected 0
Assert-Count -Name "no retired publisher flag survives" -Pattern '(--role=|--component=|--trigger=|--builder-|--artifact-name|--transport-evidence)' -Expected 0
Assert-Count -Name "no attempt-scoped job lookup survives" -Pattern 'check_run_id' -Expected 0

# Publisher job: it builds the trusted runtime from the project's own compile list and runs the release regression gates.
Assert-JobCount -Job "publisher" -Name "publisher job is read-only" -Pattern '^ {6}contents: read\s*$' -Expected 1
Assert-JobCount -Job "publisher" -Name "publisher checkout disables credential persistence" -Pattern 'persist-credentials: false\s*$' -Expected 1
Assert-JobCount -Job "publisher" -Name "publisher uploads the runtime transport" -Pattern '^ {10}name: release-publisher\s*$' -Expected 1
Assert-JobCount -Job "publisher" -Name "publisher declares the runtime artifact id from its own upload" -Pattern '^ {6}runtimeArtifactId: \$\{\{ steps\.upload\.outputs\.artifact-id \}\}\s*$' -Expected 1
Assert-JobCount -Job "publisher" -Name "publisher declares the runtime platform digest from its own upload" -Pattern '^ {6}runtimeArtifactDigest: \$\{\{ steps\.upload\.outputs\.artifact-digest \}\}\s*$' -Expected 1
Assert-JobCount -Job "publisher" -Name "publisher declares the runtime inventory as a job output" -Pattern '^ {6}runtimeInventory: \$\{\{ steps\.inventory\.outputs\.inventory \}\}\s*$' -Expected 1
Assert-JobCount -Job "publisher" -Name "publisher emits the complete file and SHA-256 inventory" -Pattern '^ {10}"inventory=\$inventoryJson" >> \$env:GITHUB_OUTPUT\s*$' -Expected 1
Assert-JobCount -Job "publisher" -Name "publisher stages from the project's explicit compile items" -Pattern 'SelectNodes\("//Compile\[@Include\]"\)' -Expected 1
Assert-JobCount -Job "publisher" -Name "publisher publishes the staged project in isolation" -Pattern 'dotnet publish \$stageProject' -Expected 1
Assert-JobCount -Job "publisher" -Name "publisher suppresses repository-wide build imports" -Pattern 'p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false' -Expected 2
Assert-JobCount -Job "publisher" -Name "publisher restores from the cleared isolated source list" -Pattern 'dotnet restore \$stageProject --configfile' -Expected 1
Assert-JobCount -Job "publisher" -Name "twelve non-packaging Workflow entrypoints" -Pattern '^\s+"workflow/tests/[A-Za-z]+\.fsx",?\s*$' -Expected 12
Assert-JobCount -Job "publisher" -Name "publisher runs the producer Expecto gate" -Pattern 'dotnet run --project dotnet/tests/Mcp\.Dotnet\.Tests' -Expected 1
Assert-JobCount -Job "publisher" -Name "publisher builds the compiled release regression project" -Pattern 'dotnet build infrastructure/release/tests/Tests\.fsproj' -Expected 1
Assert-JobCount -Job "publisher" -Name "publisher consumes the pinned fixture descriptor" -Pattern 'infrastructure/release/tests/Fixtures\.json' -Expected 1
Assert-JobCount -Job "publisher" -Name "publisher runs the complete compiled release gate" -Pattern '^ {8}run: pwsh infrastructure/release/Suite\.ps1\s*$' -Expected 1
Assert-JobCount -Job "publisher" -Name "publisher typechecks the retained release scripts" -Pattern 'Typecheck\.ps1 -SdkVersion' -Expected 1

# Preparation job: both producers' package gates pass before either candidate transport is uploaded.
Assert-JobCount -Job "preparation" -Name "preparation job is read-only" -Pattern '^ {6}contents: read\s*$' -Expected 1
Assert-JobCount -Job "preparation" -Name "preparation checkout disables credential persistence" -Pattern 'persist-credentials: false\s*$' -Expected 1
Assert-JobCount -Job "preparation" -Name "preparation runs each producer package gate" -Pattern '^ {12}\$entrypoint = "\$component/tests/DistributionTests\.fsx"\s*$' -Expected 1
Assert-JobCount -Job "preparation" -Name "preparation runs the Workflow packaged coordination gate" -Pattern 'workflow/tests/PackagedRuntimeCoordinationTests\.fsx' -Expected 1
Assert-JobCount -Job "preparation" -Name "preparation builds the release runner from the checkout" -Pattern 'dotnet build infrastructure/release/Release\.fsproj -c Release' -Expected 1
Assert-JobCount -Job "preparation" -Name "preparation prepares each enrolled candidate through the canonical CLI" -Pattern '& \$runner prepare \$configPath \$candidate\s*\|' -Expected 1
Assert-JobCount -Job "preparation" -Name "preparation writes each component's own archive key" -Pattern '^ {12}"archiveName_\$component=\$archiveName" >> \$env:GITHUB_OUTPUT\s*$' -Expected 1
Assert-JobCount -Job "preparation" -Name "preparation publishes each component's archive name in the job environment" -Pattern 'CANDIDATE_ARCHIVE_NAME_\$\(\(\$component\)\.ToUpperInvariant\(\)\)' -Expected 1
Assert-JobCount -Job "preparation" -Name "preparation accepts only the exact two-member candidate directory" -Pattern 'where exactly the archive' -Expected 1
# Publication job: it installs the canonical SDK, verifies every downloaded member and publishes without building anything.
Assert-JobCount -Job "publication" -Name "publish job alone holds write authority" -Pattern '^ {6}contents: write\s*$' -Expected 1
Assert-JobCount -Job "publication" -Name "publish job keeps platform artifact read" -Pattern '^ {6}actions: read\s*$' -Expected 1
Assert-JobCount -Job "publication" -Name "publication installs the canonical SDK exactly once" -Pattern 'uses: actions/setup-dotnet@' -Expected 1
Assert-JobCount -Job "publication" -Name "publication never builds" -Pattern 'dotnet build' -Expected 0
Assert-JobCount -Job "publication" -Name "publication never restores" -Pattern 'dotnet restore' -Expected 0
Assert-JobCount -Job "publication" -Name "publication never evaluates F#" -Pattern 'dotnet fsi' -Expected 0
Assert-JobCount -Job "publication" -Name "publication never runs tests" -Pattern 'dotnet test' -Expected 0
Assert-JobCount -Job "publication" -Name "publication never runs a project" -Pattern 'dotnet run' -Expected 0
Assert-JobCount -Job "publication" -Name "publication rechecks the control plane" -Pattern 'git rev-parse HEAD' -Expected 2
Assert-JobCount -Job "publication" -Name "publication proves the ancestry through the API" -Pattern 'gh api "repos/\$repo' -Expected 2
# The pinned action treats a blank `artifact-ids` value as "every artifact in this run", so the three declared ids are
# proven non-empty, positive decimal ids before any transfer action is entered.
Assert-JobCount -Job "publication" -Name "publication validates the declared artifact ids exactly once" -Pattern '^ {8}id: validateArtifacts\s*$' -Expected 1
Assert-JobCount -Job "publication" -Name "publication guards all three declared artifact ids" -Pattern '^ {10}Assert-ArtifactId ' -Expected 3
Assert-JobCount -Job "publication" -Name "the artifact id guard embeds no workflow expression in its own body" -Pattern 'Assert-ArtifactId [^\r\n]*\$\{\{' -Expected 0
Assert-JobCount -Job "publication" -Name "the runtime id guard reads the publisher job's own declared output" -Pattern '^ {10}RUNTIME_ARTIFACT_ID: \$\{\{ needs\.publisher\.outputs\.runtimeArtifactId \}\}\s*$' -Expected 1
Assert-JobCount -Job "publication" -Name "the dotnet id guard reads the preparation job's own declared output" -Pattern '^ {10}CANDIDATE_ARTIFACT_ID_DOTNET: \$\{\{ needs\.preparation\.outputs\.candidateArtifactId_dotnet \}\}\s*$' -Expected 1
Assert-JobCount -Job "publication" -Name "the workflow id guard reads the preparation job's own declared output" -Pattern '^ {10}CANDIDATE_ARTIFACT_ID_WORKFLOW: \$\{\{ needs\.preparation\.outputs\.candidateArtifactId_workflow \}\}\s*$' -Expected 1
Assert-JobCount -Job "publication" -Name "publication transfers every transport through the pinned official action" -Pattern '^ {8}uses: actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093 # v4\s*$' -Expected 3
Assert-JobCount -Job "publication" -Name "publication addresses every transfer by an exact declared artifact id" -Pattern '^ {10}artifact-ids: \$\{\{ needs\.' -Expected 3
Assert-JobCount -Job "publication" -Name "publication restates no artifact identity beyond the pinned action" -Pattern '^ {10}(pattern|run-id|repository|github-token|merge-multiple):' -Expected 0
Assert-JobCount -Job "publication" -Name "no publication step selects an artifact by name" -Pattern '^ {10}name: ' -Expected 1
Assert-JobCount -Job "publication" -Name "publication ships no hand-written artifact client" -Pattern '(Invoke-WebRequest|Invoke-RestMethod|Expand-Archive|Save-ArtifactTransport|Get-FileHash|api\.github\.com)' -Expected 0
Assert-JobCount -Job "publication" -Name "publication binds the runtime artifact id" -Pattern 'Read-DeclaredOutput \$publisher "runtimeArtifactId"' -Expected 1
Assert-JobCount -Job "publication" -Name "publication binds the runtime platform digest" -Pattern 'Read-DeclaredOutput \$publisher "runtimeArtifactDigest"' -Expected 1
Assert-JobCount -Job "publication" -Name "publication binds the independent runtime inventory" -Pattern 'Read-DeclaredOutput \$publisher "runtimeInventory"' -Expected 1
Assert-JobCount -Job "publication" -Name "publication binds only its own component keys" -Pattern 'Read-DeclaredOutput \$candidates "candidateArtifactId_\$component"' -Expected 1
Assert-JobCount -Job "publication" -Name "publication binds its own component digest" -Pattern 'Read-DeclaredOutput \$candidates "candidateArtifactDigest_\$component"' -Expected 1
Assert-JobCount -Job "publication" -Name "publication binds its own component archive name" -Pattern 'Read-DeclaredOutput \$candidates "candidateArchiveName_\$component"' -Expected 1
Assert-JobCount -Job "publication" -Name "publication verifies the complete runtime inventory" -Pattern '& pwsh -NoProfile -File \$verifier -Root \$runtimeRoot -InventoryJson \$runtimeInventory' -Expected 1
Assert-JobCount -Job "publication" -Name "publication verifies the exact candidate members" -Pattern '& pwsh -NoProfile -File \$verifier -Root \$candidateRoot -MembersJson \(ConvertTo-Json -Compress @\(\$archiveName, "distribution\.json"\)\)' -Expected 1
Assert-JobCount -Job "publication" -Name "publication requires both write authorities in the invoking step alone" -Pattern '^ {10}MCP_RELEASE_DO_UPLOAD: "true"\s*$' -Expected 1
Assert-JobCount -Job "publication" -Name "publication invokes the verified publisher once" -Pattern '& \$runner publish \$configPath \$candidateRoot "--workspace-root=\$workspace" "--event-sha=\$eventSha" "--repo=\$repo"' -Expected 1
Assert-JobCount -Job "publication" -Name "publication retains the runner log per component" -Pattern 'Tee-Object -FilePath \$statusLog' -Expected 1
Assert-JobCount -Job "publication" -Name "publication preserves the native component exit" -Pattern '^ {10}exit \$firstFailure\s*$' -Expected 1
Assert-JobCount -Job "publication" -Name "publication records its results unconditionally" -Pattern '^ {8}if: always\(\)\s*$' -Expected 2
Assert-JobCount -Job "publication" -Name "publication uploads one metadata evidence artifact" -Pattern '^ {10}name: release-metadata\s*$' -Expected 1

# Ordering: every transport arrives through the pinned action, and the downloaded member set is proven against the
# independent declaration before anything from it executes.
Assert-Before -Job "publication" -Name "the declared artifact ids are validated before the first transfer" -First 'id: validateArtifacts' -Second 'uses: actions/download-artifact@'
Assert-Before -Job "publication" -Name "the runtime transport is transferred before it is verified" -First 'uses: actions/download-artifact@' -Second '& pwsh -NoProfile -File $verifier'
Assert-Before -Job "publication" -Name "the runtime inventory is verified before execution" -First '-InventoryJson $runtimeInventory' -Second '& $runner publish $configPath $candidateRoot'
Assert-Before -Job "publication" -Name "the candidate members are verified before the native call" -First '-MembersJson (ConvertTo-Json -Compress @($archiveName, "distribution.json"))' -Second '& $runner publish $configPath $candidateRoot'

# Static per-component output keys: a participant added to the roster without its declared upload and key fails here.
foreach ($entry in $roster) {
    $componentId = $entry.componentId
    $upperId = $componentId.ToUpperInvariant()

    $id = [regex]::Escape($componentId)
    $bindings = @(
        @{ Key = "candidateArtifactId_$componentId"; Pattern = '^ {6}candidateArtifactId_' + $id + ': \$\{\{ steps\.upload_' + $id + '\.outputs\.artifact-id \}\}\s*$' },
        @{ Key = "candidateArtifactDigest_$componentId"; Pattern = '^ {6}candidateArtifactDigest_' + $id + ': \$\{\{ steps\.upload_' + $id + '\.outputs\.artifact-digest \}\}\s*$' },
        @{ Key = "candidateArchiveName_$componentId"; Pattern = '^ {6}candidateArchiveName_' + $id + ': \$\{\{ steps\.prepare\.outputs\.archiveName_' + $id + ' \}\}\s*$' }
    )
    foreach ($binding in $bindings) {
        $script:checked = $script:checked + 1
        $declared = [regex]::Matches($text, $binding.Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline).Count

        if ($declared -ne 1) {
            throw "cross-stage coherence failed [static candidate output key]: expected exactly one declared output '$($binding.Key)'; found $declared"
        }
    }

    # The candidate transport is transferred by the pinned official action from this participant's own declared id.
    foreach ($transfer in @(
        @{ Key = "candidateArtifactId_$componentId transfer"; Pattern = '^ {10}artifact-ids: \$\{\{ needs\.preparation\.outputs\.candidateArtifactId_' + $id + ' \}\}\s*$' },
        @{ Key = "candidate-$componentId transfer root"; Pattern = '^ {10}path: \$\{\{ runner\.temp \}\}/candidate-' + $id + '\s*$' }
    )) {
        $script:checked = $script:checked + 1
        $declared = [regex]::Matches($text, $transfer.Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline).Count

        if ($declared -ne 1) {
            throw "cross-stage coherence failed [candidate transport binding]: expected exactly one '$($transfer.Key)'; found $declared"
        }
    }

    $script:checked = $script:checked + 1
    $capitalized = $componentId.Substring(0, 1).ToUpperInvariant() + $componentId.Substring(1)
    $transferSteps = [regex]::Matches($text, ('^ {8}id: downloadCandidate' + $capitalized + '\s*$'), [System.Text.RegularExpressions.RegexOptions]::Multiline).Count
    if ($transferSteps -ne 1) {
        throw "cross-stage coherence failed [candidate transfer step]: expected exactly one 'downloadCandidate$capitalized' step; found $transferSteps"
    }

    $script:checked = $script:checked + 1
    $uploadSteps = [regex]::Matches($text, ('^ {8}id: upload_' + $id + '\s*$'), [System.Text.RegularExpressions.RegexOptions]::Multiline).Count
    if ($uploadSteps -ne 1) {
        throw "cross-stage coherence failed [candidate upload step]: expected exactly one upload step for '$componentId'; found $uploadSteps"
    }

    $script:checked = $script:checked + 1
    $artifactNames = [regex]::Matches($text, ('^ {10}name: release-candidate-' + $id + '\s*$'), [System.Text.RegularExpressions.RegexOptions]::Multiline).Count
    if ($artifactNames -ne 1) {
        throw "cross-stage coherence failed [candidate artifact name]: expected exactly one 'release-candidate-$componentId' upload; found $artifactNames"
    }

    # Each candidate upload carries exactly two direct members: the policy-derived archive name and the distribution manifest.
    $archiveMember = '${{ env.CANDIDATE_ROOT }}/' + $componentId + '/${{ env.CANDIDATE_ARCHIVE_NAME_' + $upperId + ' }}'
    $manifestMember = '${{ env.CANDIDATE_ROOT }}/' + $componentId + '/distribution.json'
    foreach ($member in @($archiveMember, $manifestMember)) {
        $script:checked = $script:checked + 1
        $memberCount = [regex]::Matches($text, ('^ {12}' + [regex]::Escape($member) + '\s*$'), [System.Text.RegularExpressions.RegexOptions]::Multiline).Count
        if ($memberCount -ne 1) {
            throw "cross-stage coherence failed [candidate transport member]: expected exactly one upload path '$member'; found $memberCount"
        }
    }
}

# No participant may own a static candidate output key or upload that the roster does not declare.
foreach ($prefix in @("candidateArtifactId_", "candidateArtifactDigest_", "candidateArchiveName_")) {
    $script:checked = $script:checked + 1
    $declared = [regex]::Matches($text, ('^ {6}' + $prefix), [System.Text.RegularExpressions.RegexOptions]::Multiline).Count

    if ($declared -ne $componentIds.Count) {
        throw "cross-stage coherence failed [static candidate output inventory]: the workflow declares $declared '$prefix' outputs for $($componentIds.Count) roster participant(s)"
    }
}

$script:checked = $script:checked + 1
$candidateUploads = [regex]::Matches($text, '^ {10}name: release-candidate-', [System.Text.RegularExpressions.RegexOptions]::Multiline).Count
if ($candidateUploads -ne $componentIds.Count) {
    throw "cross-stage coherence failed [candidate upload inventory]: the workflow uploads $candidateUploads candidate transport(s) for $($componentIds.Count) roster participant(s)"
}

$script:checked = $script:checked + 1
$candidateTransfers = [regex]::Matches($text, '^ {8}id: downloadCandidate', [System.Text.RegularExpressions.RegexOptions]::Multiline).Count
if ($candidateTransfers -ne $componentIds.Count) {
    throw "cross-stage coherence failed [candidate transfer inventory]: the workflow transfers $candidateTransfers candidate transport(s) for $($componentIds.Count) roster participant(s)"
}

# The CLI receives `<componentId>/release/Config.json`; the roster must register exactly that path per participant.
foreach ($entry in $roster) {
    $checked = $checked + 1
    $derived = "$($entry.componentId)/release/Config.json"

    if ($derived -ne $entry.configPath) {
        throw "cross-stage coherence failed [CLI entry registration]: participant '$($entry.componentId)' registers '$($entry.configPath)' but the workflow CLI path derives '$derived'"
    }
}

# The workflow passes the projection step's actual value; a step-local GITHUB_OUTPUT file belongs to a different step.
if ([string]::IsNullOrWhiteSpace($Components)) {
    throw "cross-stage coherence failed [projected components]: -Components must carry the projection value"
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
