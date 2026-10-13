#requires -Version 7.0
# role: trusted builder transport glue
# Stage validates the audited closures, stages the Sources.json closure outside the checkout and restores it with an authored project-local import; Emit hashes the compiled runtime into the manifest, inner envelope, outer transport and trusted publisher receipt.
# It holds no producer policy, transaction, upload, producer-script or FSI logic, and never parses a producer Config.fsx.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Stage", "Emit")]
    [string]$Phase,

    [Parameter(Mandatory = $true)]
    [string]$Root,

    [Parameter(Mandatory = $true)]
    [string]$StageRoot,

    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,

    [Parameter(Mandatory = $true)]
    [string]$Repository,

    [string]$EventSha = "",
    [string]$RunId = "",
    [string]$RunAttempt = "",
    [string]$Trigger = "push",
    [string]$BuilderSdk = "",
    [string]$BuilderRuntime = "",
    [string]$BuilderPlatform = "",

    [string]$SourcesPath = "",
    [string]$PublisherClosure = "",
    [string]$RosterPath = "infrastructure/release/Participants.json",
    [string]$RuntimeProject = "infrastructure/release/Release.fsproj",
    [string]$Configuration = "Release",
    [string]$EnvelopeName = "publisher-runtime.zip",
    [string]$ManifestName = "runtime-manifest.json",
    [string]$OuterName = "publisher-transport.zip",
    [string]$RequiredRuntimeFile = "ReleaseRunner.exe"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:ClosureSchemaVersion = 1
$script:ManifestSchemaVersion = 1
$script:ReceiptSchemaVersion = 1
$script:PublisherClosureSchemaVersion = 1
$script:Separator = [System.IO.Path]::DirectorySeparatorChar
# A fixed archive timestamp keeps identical inputs byte-identical, so the builder envelope hash is reproducible and the platform outer digest is the only upload-dependent value.
$script:EnvelopeTimestamp = [System.DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
$script:ClosureFieldNames = @(
    "schemaVersion", "role", "description", "project", "stageRoot", "paths", "imports", "excluded", "nuget", "forbiddenFields")
# Directory.Build/Directory.Packages and MSBuild user imports stay off so only the staged project and the trusted SDK MSBuild decide the compile.
$script:ImportSuppression = @("-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false")
$script:PublisherClosureFieldNames = @("schemaVersion", "repository", "rosterPath")

function Assert-Outside {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Anchor,
        [Parameter(Mandatory = $true)][string]$Code
    )

    $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd($script:Separator)
    $anchorFull = [System.IO.Path]::GetFullPath($Anchor).TrimEnd($script:Separator)
    # Anything inside the checkout would let repository build hooks, producer scripts or candidate bytes reach the trusted compile.
    if ($candidateFull -ceq $anchorFull -or $candidateFull.StartsWith($anchorFull + $script:Separator, [System.StringComparison]::Ordinal)) {
        throw ("{0}: '{1}' must stay outside '{2}'" -f $Code, $candidateFull, $anchorFull)
    }
}

function Assert-RootSeparation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RootFull,
        [Parameter(Mandatory = $true)][string]$StageFull,
        [Parameter(Mandatory = $true)][string]$OutputFull
    )

    Assert-Outside -Candidate $StageFull -Anchor $RootFull -Code "stage-root-inside-checkout"
    Assert-Outside -Candidate $OutputFull -Anchor $RootFull -Code "output-root-inside-checkout"
    Assert-Outside -Candidate $OutputFull -Anchor $StageFull -Code "output-root-overlaps-stage"
    Assert-Outside -Candidate $StageFull -Anchor $OutputFull -Code "stage-root-overlaps-output"
}

function Read-ObjectDocument {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$AllowedFields
    )

    if (-not (Test-Path -LiteralPath $Path)) { throw "the {0} is missing: {1}" -f $Label, $Path }

    try { $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -Depth 16 -ErrorAction Stop }
    catch { throw ("the {0} is not valid JSON: {1}" -f $Label, $Path) }

    if ($document -isnot [System.Collections.IDictionary]) { throw ("the {0} is not a JSON object: {1}" -f $Label, $Path) }

    # Strict shape: an unknown authored field is drift, and a policy or digest value in an authored document is a forbidden duplicate.
    foreach ($key in @($document.Keys)) {
        if ($AllowedFields -cnotcontains "$key") { throw ("the {0} carries the unknown top-level field '{1}'" -f $Label, $key) }
    }

    return $document
}

function Get-FileSha256Hex {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function New-DeterministicZip {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Entries
    )

    $stream = [System.IO.File]::Open($Destination, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($name in @($Entries.Keys)) {
                $entry = $archive.CreateEntry("$name", [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $script:EnvelopeTimestamp
                $entryStream = $entry.Open()
                try {
                    $sourceStream = [System.IO.File]::OpenRead("$($Entries[$name])")
                    try { $sourceStream.CopyTo($entryStream) } finally { $sourceStream.Dispose() }
                } finally { $entryStream.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}

function Write-OutputLine {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Key, [Parameter(Mandatory = $true)][string]$Value)

    if ($env:GITHUB_OUTPUT) { Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "$Key=$Value" }
    Write-Host "$Key=$Value"
}

$rootFull = [System.IO.Path]::GetFullPath($Root)
$stageFull = [System.IO.Path]::GetFullPath($StageRoot)
$outputFull = [System.IO.Path]::GetFullPath($OutputRoot)
if (-not (Test-Path -LiteralPath $rootFull)) { throw "the checkout root does not exist: $rootFull" }
if ([string]::IsNullOrWhiteSpace($EnvelopeName) -or $EnvelopeName.Contains("/") -or $EnvelopeName.Contains("\")) { throw "the envelope name must be a single relative name: '$EnvelopeName'" }
if ([string]::IsNullOrWhiteSpace($ManifestName) -or $ManifestName.Contains("/") -or $ManifestName.Contains("\")) { throw "the manifest name must be a single relative name: '$ManifestName'" }
if ($EnvelopeName -ceq $ManifestName) { throw "the envelope and manifest names must differ" }

Assert-RootSeparation -RootFull $rootFull -StageFull $stageFull -OutputFull $outputFull

$stagedProjectFull = Join-Path $stageFull ($RuntimeProject.Replace("/", $script:Separator))
$publishRootFull = Join-Path $stageFull "publish"

if ($Phase -ceq "Stage") {
    if (Test-Path -LiteralPath $stageFull) { throw "the stage root already exists; the builder never reuses a staging directory: $StageRoot" }
    if ([string]::IsNullOrWhiteSpace($BuilderPlatform)) { throw "-BuilderPlatform is required to restore the self-contained runtime" }

    $sourcesFull =
        if ($SourcesPath) {
            # A relative override resolves against the checkout root, never against the step's working directory.
            if ([System.IO.Path]::IsPathRooted($SourcesPath)) { $SourcesPath } else { Join-Path $rootFull ($SourcesPath.Replace("/", $script:Separator)) }
        } else {
            Join-Path $rootFull "infrastructure/release/Sources.json"
        }
    $closure = Read-ObjectDocument -Path $sourcesFull -Label "audited builder closure" -AllowedFields $script:ClosureFieldNames
    if ("$($closure['schemaVersion'])" -ne "$($script:ClosureSchemaVersion)") { throw "unsupported audited builder closure schemaVersion '$($closure['schemaVersion'])'" }

    $paths = @($closure['paths'] | ForEach-Object { "$_" })
    if ($paths.Count -eq 0) { throw "the audited builder closure declares no paths" }
    if (($paths | Sort-Object -Unique).Count -ne $paths.Count) { throw "the audited builder closure repeats a path" }
    if ($paths -cnotcontains $RuntimeProject) { throw "the audited builder closure does not list the runtime project '$RuntimeProject'" }
    foreach ($path in $paths) {
        if ([string]::IsNullOrWhiteSpace($path) -or $path.Contains("..")) { throw "the audited builder closure declares an unsafe path '$path'" }
        if (-not (Test-Path -LiteralPath (Join-Path $rootFull ($path.Replace("/", $script:Separator))))) { throw "the audited builder closure lists a missing path '$path'" }
    }

    # The publish-time closure is validated here as well: the builder confirms the audited document the publisher will read, without duplicating any policy value.
    $publisherFull = if ($PublisherClosure) { Join-Path $rootFull ($PublisherClosure.Replace("/", $script:Separator)) } else { Join-Path $rootFull "infrastructure/release/Publisher.json" }
    $publisherDocument = Read-ObjectDocument -Path $publisherFull -Label "audited publisher closure" -AllowedFields $script:PublisherClosureFieldNames
    if ("$($publisherDocument['schemaVersion'])" -ne "$($script:PublisherClosureSchemaVersion)") { throw "unsupported publisher closure schemaVersion '$($publisherDocument['schemaVersion'])'" }
    if ("$($publisherDocument['repository'])" -cne $Repository) { throw ("the publisher closure repository '{0}' does not equal the event repository '{1}'" -f "$($publisherDocument['repository'])", $Repository) }
    if ([string]::IsNullOrWhiteSpace("$($publisherDocument['rosterPath'])")) { throw "the publisher closure declares no rosterPath" }

    [System.IO.Directory]::CreateDirectory($stageFull) | Out-Null

    # Staging outside the checkout with repo-relative layout keeps ../../infrastructure/Provenance.fs resolvable while excluding every repository build hook.
    foreach ($path in $paths) {
        $relative = $path.Replace("/", $script:Separator)
        $destination = Join-Path $stageFull $relative
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $rootFull $relative) -Destination $destination -Force
    }

    # The staged NuGet.config clears inherited sources so the restore resolves only the audited feed and cannot inherit a developer or machine-level import.
    $nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@
    Set-Content -LiteralPath (Join-Path $stageFull "NuGet.config") -Value $nugetConfig -Encoding utf8

    $env:NUGET_PACKAGES = Join-Path $stageFull "packages"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:DOTNET_NOLOGO = "1"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

    Write-Host ("builder stage: restore {0} for {1}/{2}" -f $RuntimeProject, $BuilderPlatform, $Configuration)
    & dotnet restore $stagedProjectFull --configfile (Join-Path $stageFull "NuGet.config") -r $BuilderPlatform @($script:ImportSuppression)
    if ($LASTEXITCODE -ne 0) { throw "the trusted publisher restore failed (exit $LASTEXITCODE)" }

    Write-OutputLine -Key "stagedProject" -Value $stagedProjectFull
    Write-OutputLine -Key "publishRoot" -Value $publishRootFull
    Write-OutputLine -Key "publisherClosureSha256" -Value (Get-FileSha256Hex -Path $publisherFull)
    exit 0
}

if ($EventSha -cnotmatch '^[0-9a-f]{40}$') { throw "-EventSha is not a lowercase 40-character commit SHA" }
if ($RunId -cnotmatch '^[0-9]+$') { throw "-RunId is not a numeric run id" }
if ($RunAttempt -cnotmatch '^[0-9]+$') { throw "-RunAttempt is not a numeric run attempt" }
if ([string]::IsNullOrWhiteSpace($BuilderSdk)) { throw "-BuilderSdk is required for the trusted receipt" }
if ([string]::IsNullOrWhiteSpace($BuilderRuntime)) { throw "-BuilderRuntime is required for the trusted receipt" }
if ([string]::IsNullOrWhiteSpace($BuilderPlatform)) { throw "-BuilderPlatform is required for the trusted receipt" }

if (-not (Test-Path -LiteralPath $publishRootFull)) { throw "the staged runtime was never compiled: $publishRootFull" }
if (-not (Test-Path -LiteralPath (Join-Path $publishRootFull $RequiredRuntimeFile))) { throw "the published trusted runtime is missing the required file '$RequiredRuntimeFile'" }

[System.IO.Directory]::CreateDirectory($outputFull) | Out-Null

$runtimeFiles = @(
    [System.IO.Directory]::EnumerateFiles($publishRootFull, "*", [System.IO.SearchOption]::AllDirectories) |
        ForEach-Object { [System.IO.Path]::GetRelativePath($publishRootFull, $_).Replace("\", "/") } |
        Sort-Object)
if ($runtimeFiles.Count -eq 0) { throw "the published trusted runtime is empty" }

$runtimeDigests = [ordered]@{}
$runtimeSizes = [ordered]@{}
$manifestEntries = New-Object System.Collections.Generic.List[object]
foreach ($name in $runtimeFiles) {
    $full = Join-Path $publishRootFull ($name.Replace("/", $script:Separator))
    $sha = Get-FileSha256Hex -Path $full
    $bytes = [long]([System.IO.FileInfo]::new($full).Length)
    $runtimeDigests[$name] = $sha
    $runtimeSizes[$name] = $bytes
    $manifestEntries.Add([ordered]@{ path = $name; sha256 = $sha; bytes = $bytes })
}

$manifestPath = Join-Path $outputFull $ManifestName
# ToArray, not @(): the New-Object list carries a [psobject] wrapper that the array-subexpression binder rejects with 'Argument types do not match' on PowerShell 7.5/7.6.
Set-Content -LiteralPath $manifestPath -Value ([ordered]@{ schemaVersion = $script:ManifestSchemaVersion; files = $manifestEntries.ToArray() } | ConvertTo-Json -Depth 8) -Encoding utf8

# The inner envelope is authored here; the outer platform zip is created later by actions/upload-artifact, so the two digests stay in separate namespaces.
$envelopeEntries = [ordered]@{}
foreach ($name in $runtimeFiles) { $envelopeEntries[$name] = Join-Path $publishRootFull ($name.Replace("/", $script:Separator)) }
$envelopePath = Join-Path $outputFull $EnvelopeName
New-DeterministicZip -Destination $envelopePath -Entries $envelopeEntries

$outerEntries = [ordered]@{}
$outerEntries[$EnvelopeName] = $envelopePath
$outerEntries[$ManifestName] = $manifestPath
$outerPath = Join-Path $outputFull $OuterName
New-DeterministicZip -Destination $outerPath -Entries $outerEntries

$envelopeSha = Get-FileSha256Hex -Path $envelopePath
$manifestSha = Get-FileSha256Hex -Path $manifestPath

# Roster and per-component config bytes are hashed from the checked-out event SHA so the publisher can re-derive policy independently later.
$rosterFull = Join-Path $rootFull ($RosterPath.Replace("/", $script:Separator))
if (-not (Test-Path -LiteralPath $rosterFull)) { throw "the canonical roster is missing: $RosterPath" }
$rosterSha = Get-FileSha256Hex -Path $rosterFull

$rosterDocument = Get-Content -LiteralPath $rosterFull -Raw | ConvertFrom-Json -ErrorAction Stop
if ($null -eq $rosterDocument.participants -or @($rosterDocument.participants).Count -eq 0) { throw "the canonical roster declares no participants" }

$componentConfigPaths = [ordered]@{}
$componentConfigShas = [ordered]@{}
foreach ($participant in @($rosterDocument.participants)) {
    $componentId = "$($participant.componentId)"
    $configPath = "$($participant.configPath)"
    if ([string]::IsNullOrWhiteSpace($componentId) -or [string]::IsNullOrWhiteSpace($configPath)) { throw "the canonical roster carries an empty participant" }
    $configFull = Join-Path $rootFull ($configPath.Replace("/", $script:Separator))
    if (-not (Test-Path -LiteralPath $configFull)) { throw "the canonical config is missing: $configPath" }
    $componentConfigPaths[$componentId] = $configPath
    $componentConfigShas[$componentId] = Get-FileSha256Hex -Path $configFull
}

# The receipt carries the exact strict field set the publisher parses; every value is computed from observed bytes, never a placeholder.
$receipt = [ordered]@{
    schemaVersion        = $script:ReceiptSchemaVersion
    repository           = $Repository
    eventRevision        = $EventSha
    runId                = "$RunId"
    attempt              = "$RunAttempt"
    trigger              = $Trigger
    # The trusted publisher receipt MUST declare the publisher role: the candidate role belongs only to the preparation/candidate artifact transport and cannot authorise the trusted publisher.
    role                 = "publisher"
    rosterPath           = $RosterPath
    rosterSha256         = $rosterSha
    componentConfigPaths = $componentConfigPaths
    componentConfigShas  = $componentConfigShas
    runtimeFilePaths     = @($runtimeFiles)
    runtimeFileDigests   = $runtimeDigests
    runtimeFileSizes     = $runtimeSizes
    builderSdk           = $BuilderSdk
    builderRuntime       = $BuilderRuntime
    builderPlatform      = $BuilderPlatform
}
$receiptPath = Join-Path $outputFull "receipt.json"
Set-Content -LiteralPath $receiptPath -Value ($receipt | ConvertTo-Json -Depth 8) -Encoding utf8

Write-OutputLine -Key "envelopeSha256" -Value $envelopeSha
Write-OutputLine -Key "manifestSha256" -Value $manifestSha
Write-OutputLine -Key "expectedOuterEntries" -Value (ConvertTo-Json -Compress -InputObject @($EnvelopeName, $ManifestName))
Write-OutputLine -Key "outerTransportPath" -Value $outerPath

Write-Host ("builder emit ok: runtimeFiles={0} envelopeSha256={1} manifestSha256={2}" -f $runtimeFiles.Count, $envelopeSha, $manifestSha)
Write-Host ("builder outputs: outer={0} manifest={1} receipt={2}" -f $outerPath, $manifestPath, $receiptPath)
exit 0
