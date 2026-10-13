#requires -Version 7.0
# role: transport-only bootstrap
# Never parses Config.fsx, never derives policy, never invokes the SDK or runs the transaction.
# The attempt is observed via the attempt-specific run endpoint and uploader-job listing, never via a latest-attempt response.
# Platform metadata is fetched with redirects disabled so a redirecting endpoint cannot inherit the bearer credential.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("InstallRuntime", "FetchCandidate")]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+$')]
    [string]$RepositoryId,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+$')]
    [string]$RunId,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+$')]
    [string]$RunAttempt,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$EventSha,

    [Parameter(Mandatory = $true)]
    [ValidateSet("publisher", "runtime", "candidate")]
    [string]$Role,

    [Parameter(Mandatory = $true)]
    [string]$ArtifactName,

    # The workflow's upload step supplies `job.check_run_id`; its response is the attempt-specific success evidence.
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+$')]
    [string]$UploaderJobId,

    # Independently supplied role expectation map (exact JSON shape in README.md), never echoed from a platform response.
    [Parameter(Mandatory = $true)]
    [string]$RoleMapJson,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+$')]
    [string]$ArtifactId,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(?:sha256:)?[0-9a-f]{64}$')]
    [string]$PlatformDigest,

    [string]$Component = "",
    [string]$EnvelopeSha256 = "",
    [string]$ManifestSha256 = "",
    [string]$ExpectedEnvelopeName = "",
    [string]$ExpectedManifestName = "runtime-manifest.json",
    [string]$RequiredRuntimeFile = "ReleaseRunner.exe",

    [Parameter(Mandatory = $true)]
    [string[]]$ExpectedOuterEntries,

    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,

    [Parameter(Mandatory = $true)]
    [string]$ReceiptPath,

    [Parameter(Mandatory = $true)]
    [string]$CheckoutRoot,

    [string]$CandidateRoot = "",

    [string]$ApiBase = "https://api.github.com",
    [string]$TokenEnvironmentVariable = "GITHUB_TOKEN",
    [string[]]$AllowedRedirectHosts = @("*.blob.core.windows.net"),
    [string]$ExpectedEvent = "push",

    [int]$MaxEntryCount = 4096,
    [long]$MaxTotalUncompressedBytes = 268435456,
    [long]$MaxEntryUncompressedBytes = 268435456,
    [long]$MaxCompressionRatio = 200,
    [long]$MaxTransportBytes = 268435456,
    [long]$MaxMetadataBytes = 4194304,

    # Test-only transport seam: refused together with the production origin and required by any other -ApiBase, so no production invocation can bypass the bounded transport.
    [System.Collections.IDictionary]$TestTransport = $null
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:ApiVersion = "2022-11-28"
$script:RuntimeManifestSchemaVersion = 1
$script:Separator = [System.IO.Path]::DirectorySeparatorChar
$script:ProductionApiOrigin = "https://api.github.com"
$script:MaxMetadataBytes = $MaxMetadataBytes
$script:TestTransport = $TestTransport
$script:ReceiptSchemaVersion = 2
$script:RoleKeys = @("publisher", "runtime", "candidate")
$script:MaxJobListingPages = 10
$script:MaxJobListingEntries = 1000



function Fail {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][string]$Detail
    )
    throw [System.Exception]::new(("{0}|{1}" -f $Code, $Detail))
}



function Get-CanonicalPath {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    return $full.TrimEnd([char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar))
}

function Test-PathOverlap {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$A,
        [Parameter(Mandatory = $true)][string]$B
    )
    $a = Get-CanonicalPath $A
    $b = Get-CanonicalPath $B
    if ($a -eq $b) { return $true }
    $comparison = [System.StringComparison]::OrdinalIgnoreCase
    return ($a.StartsWith($b + $script:Separator, $comparison) -or $b.StartsWith($a + $script:Separator, $comparison))
}



function Get-JsonField {
    [CmdletBinding()]
    param($Object, [Parameter(Mandatory = $true)][string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}



# Every identifier comparison states its own case policy instead of inheriting PowerShell's case-insensitive -eq: only repository owner/name are case-insensitive.
function Test-ExactString {
    [CmdletBinding()]
    param(
        [string]$Observed,
        [string]$Expected,
        [bool]$CaseSensitive = $true
    )
    $comparison = if ($CaseSensitive) { [System.StringComparison]::Ordinal } else { [System.StringComparison]::OrdinalIgnoreCase }
    return [string]::Equals($Observed, $Expected, $comparison)
}

function Assert-JsonString {
    [CmdletBinding()]
    param(
        $Document,
        [Parameter(Mandatory = $true)][string]$Field,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][string]$Subject,
        [bool]$CaseSensitive = $true,
        [string]$CasePhrase = "the exact value"
    )
    $observed = "$(Get-JsonField $Document $Field)"
    if (-not (Test-ExactString -Observed $observed -Expected $Expected -CaseSensitive $CaseSensitive)) {
        Fail $Code ("{0} {1} '{2}' does not equal the expected {3} '{4}'" -f $Subject, $Field, $observed, $CasePhrase, $Expected)
    }
    return $observed
}

# `-Value` is not Mandatory so a missing digest reaches the typed `missing-platform-field` failure instead of an unhandled parameter-binding error; malformed digests fail closed before any install can act on them.
function ConvertFrom-PlatformDigest {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Value,
        [Parameter(Mandatory = $true)][string]$FieldName
    )
    if ([string]::IsNullOrWhiteSpace($Value)) {
        Fail "missing-platform-field" ("{0} is absent; the outer platform digest cannot be verified and the bootstrap refuses to continue" -f $FieldName)
    }
    if ($Value -cmatch '^sha256:([0-9a-f]{64})$') { return $Matches[1] }
    if ($Value -cmatch '^[0-9a-f]{64}$') { return $Value }
    Fail "malformed-platform-field" ("{0} value '{1}' is not an accepted outer platform digest form ('sha256:<64 lowercase hex>' or '<64 lowercase hex>')" -f $FieldName, $Value)
}



function Resolve-ApiOrigin {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Value)

    $parsed = $null
    if (-not [System.Uri]::TryCreate($Value, [System.UriKind]::Absolute, [ref]$parsed)) {
        Fail "invalid-api-base" ("-ApiBase '{0}' is not an absolute URI" -f $Value)
    }
    if ($parsed.Scheme -cne "https") { Fail "invalid-api-base" ("-ApiBase scheme '{0}' is not https" -f $parsed.Scheme) }
    if (-not [string]::IsNullOrEmpty($parsed.UserInfo)) { Fail "invalid-api-base" "-ApiBase must not carry user information" }
    if (-not [string]::IsNullOrEmpty($parsed.Query) -or -not [string]::IsNullOrEmpty($parsed.Fragment)) {
        Fail "invalid-api-base" "-ApiBase must not carry a query string or fragment"
    }

    $origin = $parsed.AbsoluteUri.TrimEnd('/')
    if ($origin -ceq $script:ProductionApiOrigin -and $null -ne $script:TestTransport) {
        Fail "test-transport-on-production-origin" "the test transport seam must not be combined with the production platform origin; no stubbed transport may run under the production API base"
    }
    if ($origin -cne $script:ProductionApiOrigin -and $null -eq $script:TestTransport) {
        Fail "non-production-api-base" ("-ApiBase '{0}' is not the production platform origin; a non-production API base is only accepted together with the explicit test transport seam" -f $origin)
    }
    return $origin
}

function Assert-TestTransport {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary]$Value)

    foreach ($key in @($Value.Keys | ForEach-Object { "$_" })) {
        if ($key -cnotin @("MetadataJson", "DownloadRaw")) { Fail "invalid-test-transport" ("the test transport seam carries the unknown key '{0}'" -f $key) }
    }
    foreach ($required in @("MetadataJson", "DownloadRaw")) {
        if (-not (@($Value.Keys | ForEach-Object { "$_" }) -contains $required)) {
            Fail "invalid-test-transport" ("the test transport seam is missing the required '{0}' script block" -f $required)
        }
        if ($Value[$required] -isnot [scriptblock]) {
            Fail "invalid-test-transport" ("the test transport seam entry '{0}' is not a script block" -f $required)
        }
    }
}

function Read-BoundedStream {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Stream,
        [Parameter(Mandatory = $true)][long]$MaxBytes,
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][string]$Subject
    )
    $buffer = [byte[]]::new(65536)
    $memory = [System.IO.MemoryStream]::new()
    try {
        [long]$total = 0
        while (($read = $Stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $total = $total + $read
            if ($total -gt $MaxBytes) { Fail $Code ("{0} exceeded the bounded {1}-byte limit" -f $Subject, $MaxBytes) }
            $memory.Write($buffer, 0, $read)
        }
        return $memory.ToArray()
    } finally {
        $memory.Dispose()
    }
}



# Role, component, artifact name and uploader job name are never taken from the platform response: they are validated against this independently supplied map, and the platform metadata must then agree with it.
function Resolve-RoleBinding {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Json,
        [Parameter(Mandatory = $true)][string]$Role,
        [Parameter(Mandatory = $true)][string]$ModeValue,
        [string]$Component,
        [Parameter(Mandatory = $true)][string]$ArtifactName
    )

    if ([string]::IsNullOrWhiteSpace($Json)) { Fail "missing-input" "-RoleMapJson must carry the role expectation map" }

    foreach ($key in $script:RoleKeys) {
        $occurrences = [System.Text.RegularExpressions.Regex]::Matches($Json, ('"' + $key + '"\s*:')).Count
        if ($occurrences -gt 1) { Fail "duplicate-role-map-key" ("the role map declares the key '{0}' more than once" -f $key) }
    }

    $map = $null
    try {
        $map = $Json | ConvertFrom-Json -AsHashtable -Depth 8 -ErrorAction Stop
    } catch {
        Fail "malformed-role-map" ("-RoleMapJson is not valid JSON: {0}" -f $_.Exception.Message)
    }
    if ($map -isnot [System.Collections.IDictionary] -or $map.Count -eq 0) { Fail "malformed-role-map" "-RoleMapJson must be a non-empty JSON object keyed by role" }

    $keys = @($map.Keys | ForEach-Object { "$_" })
    foreach ($key in $keys) {
        if ($key -cnotin $script:RoleKeys) { Fail "malformed-role-map" ("the role map carries the unknown role key '{0}'" -f $key) }
    }
    if ($keys -cnotcontains $Role) { Fail "role-not-in-map" ("the role map declares no expectation for the role '{0}'" -f $Role) }

    $entry = $map[$Role]
    if ($entry -isnot [System.Collections.IDictionary]) { Fail "malformed-role-map" ("the role map entry for '{0}' is not a JSON object" -f $Role) }

    $entryKeys = @($entry.Keys | ForEach-Object { "$_" })
    foreach ($key in $entryKeys) {
        if ($key -cnotin @("artifactName", "uploaderJobName", "component")) { Fail "malformed-role-map" ("the role map entry for '{0}' carries the unknown field '{1}'" -f $Role, $key) }
    }
    foreach ($required in @("artifactName", "uploaderJobName")) {
        if ($entryKeys -cnotcontains $required) { Fail "malformed-role-map" ("the role map entry for '{0}' is missing '{1}'" -f $Role, $required) }
    }

    $mappedArtifactName = "$($entry['artifactName'])"
    $mappedUploaderJobName = "$($entry['uploaderJobName'])"
    if ([string]::IsNullOrWhiteSpace($mappedArtifactName)) { Fail "malformed-role-map" ("the role map entry for '{0}' declares an empty artifactName" -f $Role) }
    if ([string]::IsNullOrWhiteSpace($mappedUploaderJobName)) { Fail "malformed-role-map" ("the role map entry for '{0}' declares an empty uploaderJobName" -f $Role) }

    $mappedComponent = $null
    if ($entryKeys -ccontains "component" -and $null -ne $entry['component']) {
        $mappedComponent = "$($entry['component'])"
        if ([string]::IsNullOrWhiteSpace($mappedComponent)) { Fail "malformed-role-map" ("the role map entry for '{0}' declares an empty component" -f $Role) }
    }

    if (-not (Test-ExactString -Observed $ArtifactName -Expected $mappedArtifactName -CaseSensitive $true)) {
        Fail "role-map-artifact-name-mismatch" ("-ArtifactName '{0}' does not equal the independently supplied expectation '{1}' for the role '{2}'" -f $ArtifactName, $mappedArtifactName, $Role)
    }

    $observedComponent = if ([string]::IsNullOrWhiteSpace($Component)) { $null } else { $Component }
    if ($Role -ceq "candidate") {
        if ($null -eq $mappedComponent) { Fail "role-component-mismatch" "the candidate role requires an explicit component in the role map" }
        if ($null -eq $observedComponent) { Fail "role-component-mismatch" "-Component is required for the candidate role" }
        if (-not (Test-ExactString -Observed $observedComponent -Expected $mappedComponent -CaseSensitive $true)) {
            Fail "role-component-mismatch" ("-Component '{0}' does not equal the independently supplied expectation '{1}' for the candidate role" -f $observedComponent, $mappedComponent)
        }
        if ($ModeValue -cne "FetchCandidate") { Fail "role-mode-mismatch" ("the candidate role is only valid in the FetchCandidate mode, not '{0}'" -f $ModeValue) }
    } else {
        if ($null -ne $mappedComponent) { Fail "role-component-mismatch" ("the role map entry for '{0}' must declare a null component" -f $Role) }
        if ($null -ne $observedComponent) { Fail "role-component-mismatch" ("-Component must be empty for the role '{0}'" -f $Role) }
        if ($ModeValue -cne "InstallRuntime") { Fail "role-mode-mismatch" ("the {0} role is only valid in the InstallRuntime mode, not '{1}'" -f $Role, $ModeValue) }
    }

    return [pscustomobject]@{
        ArtifactName    = $mappedArtifactName
        UploaderJobName = $mappedUploaderJobName
        Component       = $mappedComponent
    }
}



# Returns $null when the entry name is safe, otherwise a stable reason code; the compiled ReleaseArchive.parse enforces the same transport categories plus deeper zip-format checks.
function Test-ArchiveEntryName {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Name)
    if ([string]::IsNullOrEmpty($Name)) { return "empty-entry-name" }
    if ($Name.EndsWith("/")) { return "directory-entry" }
    foreach ($ch in $Name.ToCharArray()) {
        $code = [int][char]$ch
        if ($code -lt 0x20 -or $code -gt 0x7e) { return "non-ascii-entry-name" }
    }
    if ($Name.Contains("\")) { return "backslash-entry-name" }
    if ($Name.Contains(":")) { return "colon-entry-name" }
    if ($Name.StartsWith("/")) { return "rooted-entry-name" }
    if ($Name.StartsWith("//")) { return "unc-entry-name" }
    foreach ($segment in $Name.Split("/")) {
        if ($segment -eq "..") { return "traversal-entry-name" }
        if ($segment -eq ".") { return "dot-entry-name" }
        if ($segment -eq "") { return "empty-segment-entry-name" }
    }
    return $null
}

# Validates the outer (platform) and inner (builder envelope) archives by the same bounded transport rules without materializing any entry.
function Assert-ZipArchiveSafety {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string[]]$ExpectedNames,
        [Parameter(Mandatory = $true)][int]$MaxCount,
        [Parameter(Mandatory = $true)][long]$MaxTotal,
        [Parameter(Mandatory = $true)][long]$MaxEntry,
        [Parameter(Mandatory = $true)][long]$MaxRatio
    )

    $expectedSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($name in $ExpectedNames) {
        $reason = Test-ArchiveEntryName $name
        if ($null -ne $reason) { Fail "malformed-expected-entry" ("expected entry name '{0}' is itself unsafe: {1}" -f $name, $reason) }
        $null = $expectedSet.Add($name)
    }

    if (-not (Test-Path -LiteralPath $ArchivePath)) { Fail "missing-archive" ("archive not found: {0}" -f $ArchivePath) }

    $archive = $null
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    } catch {
        Fail "archive-open-failed" ("the archive could not be opened as a ZIP: {0}" -f $_.Exception.Message)
    }

    try {
        $names = New-Object System.Collections.Generic.List[string]
        $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        $seenCaseInsensitive = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        [long]$total = 0
        [int]$count = 0

        foreach ($entry in $archive.Entries) {
            $count = $count + 1
            if ($count -gt $MaxCount) {
                Fail "archive-entry-count-exceeded" ("the archive declares more than {0} entries" -f $MaxCount)
            }

            $name = $entry.FullName
            $reason = Test-ArchiveEntryName $name
            if ($null -ne $reason) { Fail ("archive-unsafe-entry-" + $reason) ("entry '{0}' failed the transport rule: {1}" -f $name, $reason) }
            if (-not $seen.Add($name)) { Fail "archive-duplicate-entry" ("entry '{0}' appears more than once" -f $name) }
            if (-not $seenCaseInsensitive.Add($name)) { Fail "archive-case-collision" ("entry '{0}' collides with another entry under case-insensitive comparison" -f $name) }

            [long]$length = $entry.Length
            [long]$compressed = $entry.CompressedLength
            if ($length -gt $MaxEntry) { Fail "archive-entry-too-large" ("entry '{0}' expands to {1} bytes, above the {2}-byte per-entry limit" -f $name, $length, $MaxEntry) }
            $total = $total + $length
            if ($total -gt $MaxTotal) { Fail "archive-total-too-large" ("the archive expands beyond the {0}-byte total limit" -f $MaxTotal) }
            if ($length -gt 0) {
                if ($compressed -le 0) { Fail "archive-nonpositive-compressed-length" ("entry '{0}' declares {1} compressed bytes for {2} uncompressed bytes" -f $name, $compressed, $length) }
                if (([double]$length / [double]$compressed) -gt [double]$MaxRatio) {
                    Fail "archive-compression-ratio-exceeded" ("entry '{0}' exceeds the {1}:1 expansion limit" -f $name, $MaxRatio)
                }
            }

            [int]$external = $entry.ExternalAttributes
            if (($external -band 0x400) -ne 0) { Fail "archive-reparse-point" ("entry '{0}' carries a DOS reparse-point attribute" -f $name) }
            [int]$unixMode = ($external -shr 16) -band 0xFFFF
            if (($unixMode -band 0xF000) -eq 0xA000) { Fail "archive-symbolic-link" ("entry '{0}' is a symbolic link" -f $name) }

            $names.Add($name)
        }

        $actualSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$names, [System.StringComparer]::Ordinal)
        if (-not $expectedSet.SetEquals($actualSet)) {
            $missingNames = @($ExpectedNames | Where-Object { -not $actualSet.Contains($_) }) -join ","
            $extraNames = @($names | Where-Object { -not $expectedSet.Contains($_) }) -join ","
            Fail "archive-entry-set-mismatch" ("the archive entry set is not the exact expected set (missing: {0}; unexpected: {1})" -f $missingNames, $extraNames)
        }

        return [pscustomobject]@{
            EntryCount             = $count
            TotalUncompressedBytes = $total
            Names                  = $names
        }
    } finally {
        if ($null -ne $archive) { $archive.Dispose() }
    }
}

# Entry-by-entry extraction instead of ExtractToDirectory, which cannot enforce the transport path rules.
function Expand-SafeArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][long]$MaxTotal
    )

    if (Test-Path -LiteralPath $Destination) { Fail "extract-destination-exists" ("extraction destination already exists: {0}" -f $Destination) }
    $destinationFull = [System.IO.Path]::GetFullPath($Destination)
    [System.IO.Directory]::CreateDirectory($destinationFull) | Out-Null

    $archive = $null
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    } catch {
        Fail "archive-open-failed" ("the archive could not be opened as a ZIP: {0}" -f $_.Exception.Message)
    }

    try {
        [long]$written = 0
        foreach ($entry in $archive.Entries) {
            $target = [System.IO.Path]::GetFullPath((Join-Path $destinationFull $entry.FullName))
            if (-not $target.StartsWith($destinationFull + $script:Separator, [System.StringComparison]::OrdinalIgnoreCase)) {
                Fail "extract-escape" ("entry '{0}' resolves outside the extraction destination" -f $entry.FullName)
            }
            $parent = [System.IO.Path]::GetDirectoryName($target)
            if (-not [string]::IsNullOrEmpty($parent)) { [System.IO.Directory]::CreateDirectory($parent) | Out-Null }

            $input = $entry.Open()
            $output = [System.IO.File]::Create($target)
            try {
                $buffer = [byte[]]::new(1048576)
                while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $written = $written + $read
                    if ($written -gt $MaxTotal) { Fail "extract-total-too-large" ("extraction exceeded the {0}-byte total limit" -f $MaxTotal) }
                    $output.Write($buffer, 0, $read)
                }
            } finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
        return $written
    } finally {
        if ($null -ne $archive) { $archive.Dispose() }
    }
}



function Get-FileSha256Hex {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        return [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
    } finally {
        $stream.Dispose()
    }
}



function Test-RedirectHostAllowed {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$HostName,
        [Parameter(Mandatory = $true)][string[]]$AllowedHosts
    )
    $candidate = $HostName.ToLowerInvariant()
    foreach ($pattern in $AllowedHosts) {
        $normalized = $pattern.ToLowerInvariant()
        if ($normalized.StartsWith("*.")) {
            $suffix = $normalized.Substring(1)
            if ($candidate.EndsWith($suffix, [System.StringComparison]::Ordinal)) { return $true }
        } elseif ($candidate -eq $normalized) {
            return $true
        }
    }
    return $false
}

# Metadata is never followed through a redirect while the bearer credential is attached, so any 3xx is a typed failure rather than a silent hop.
function Get-MetadataDocument {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Token
    )

    [byte[]]$bytes = $null
    if ($null -ne $script:TestTransport) {
        $text = $null
        try {
            $text = [string](& $script:TestTransport['MetadataJson'] $Uri $Token)
        } catch {
            Fail "metadata-request-failed" ("the test transport seam failed for '{0}': {1}" -f $Uri, $_.Exception.Message)
        }
        if ($null -eq $text) { Fail "metadata-request-failed" ("the test transport seam returned no document for '{0}'" -f $Uri) }
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
        if ($bytes.LongLength -gt $script:MaxMetadataBytes) {
            Fail "metadata-response-too-large" ("the test transport document for '{0}' exceeded the bounded {1}-byte limit" -f $Uri, $script:MaxMetadataBytes)
        }
    } else {
        $handler = [System.Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $handler.UseCookies = $false
        $handler.AutomaticDecompression = [System.Net.DecompressionMethods]::None
        $client = [System.Net.Http.HttpClient]::new($handler)
        $client.Timeout = [System.TimeSpan]::FromMinutes(5)
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Uri)
        $null = $request.Headers.TryAddWithoutValidation("Authorization", "Bearer $Token")
        $null = $request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json")
        $null = $request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", $script:ApiVersion)
        try {
            $response = $null
            try {
                $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            } catch {
                Fail "metadata-request-failed" ("the platform metadata request to '{0}' failed: {1}" -f $Uri, $_.Exception.Message)
            }
            try {
                $status = [int]$response.StatusCode
                if ($status -ge 300 -and $status -lt 400) {
                    Fail "metadata-redirect-rejected" ("the platform metadata endpoint '{0}' answered HTTP {1} with a redirect; platform metadata is never followed through a redirect with the bearer credential attached" -f $Uri, $status)
                }
                if ($status -ne 200) { Fail "metadata-request-failed" ("the platform metadata endpoint '{0}' returned HTTP {1}" -f $Uri, $status) }
                $stream = $response.Content.ReadAsStream()
                $bytes = Read-BoundedStream -Stream $stream -MaxBytes $script:MaxMetadataBytes -Code "metadata-response-too-large" -Subject ("the platform metadata response from '{0}'" -f $Uri)
            } finally {
                $response.Dispose()
            }
        } finally {
            $request.Dispose()
            $client.Dispose()
        }
    }

    $text = [System.Text.Encoding]::UTF8.GetString([byte[]]$bytes)
    try {
        return ($text | ConvertFrom-Json -ErrorAction Stop)
    } catch {
        Fail "malformed-platform-response" ("the platform metadata response from '{0}' is not valid JSON: {1}" -f $Uri, $_.Exception.Message)
    }
}

function Get-ArtifactRawArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$DownloadUri,
        [Parameter(Mandatory = $true)][string]$Token,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string[]]$AllowedHosts,
        [Parameter(Mandatory = $true)][long]$MaxBytes
    )

    $handler = [System.Net.Http.HttpClientHandler]::new()
    # The signed redirect is followed by hand so a transforming hop (decompression, cookie replay) can never change the hashed bytes.
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.AutomaticDecompression = [System.Net.DecompressionMethods]::None
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [System.TimeSpan]::FromMinutes(15)

    if ($null -ne $script:TestTransport) {
        $stub = $null
        try {
            $stub = & $script:TestTransport['DownloadRaw'] $DownloadUri $Token $DestinationPath $AllowedHosts $MaxBytes
        } catch {
            Fail "download-not-redirected" ("the test transport seam failed for '{0}': {1}" -f $DownloadUri, $_.Exception.Message)
        }
        if ($null -eq $stub) { Fail "download-not-redirected" ("the test transport seam returned no download result for '{0}'" -f $DownloadUri) }
        $stubSha = "$(Get-JsonField $stub 'Sha256')"
        $stubBytes = "$(Get-JsonField $stub 'Bytes')"
        $stubHost = "$(Get-JsonField $stub 'RedirectHost')"
        if ($stubSha -cnotmatch '^[0-9a-f]{64}$') { Fail "invalid-test-transport" "the test transport seam must return a lowercase SHA-256 for the bytes it wrote" }
        if ($stubBytes -cnotmatch '^[0-9]+$') { Fail "invalid-test-transport" "the test transport seam must return a non-negative byte count" }
        if (-not (Test-RedirectHostAllowed $stubHost $AllowedHosts)) {
            Fail "redirect-host-rejected" ("the test transport redirect host '{0}' is not in the permitted artifact host set" -f $stubHost)
        }
        if (-not (Test-Path -LiteralPath $DestinationPath)) { Fail "invalid-test-transport" ("the test transport seam did not write the bounded archive to '{0}'" -f $DestinationPath) }
        return [pscustomobject]@{
            Sha256       = $stubSha
            Bytes        = [long]$stubBytes
            RedirectHost = $stubHost.ToLowerInvariant()
            RedirectCode = 0
        }
    }

    $first = $null
    $second = $null
    try {
        $parsed = $null
        [int]$redirectCode = 0
        $first = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $DownloadUri)
        $null = $first.Headers.TryAddWithoutValidation("Authorization", "Bearer $Token")
        $null = $first.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json")
        $null = $first.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", $script:ApiVersion)
        $response = $client.SendAsync($first, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            $status = [int]$response.StatusCode
            if ($status -lt 300 -or $status -ge 400) {
                Fail "download-not-redirected" ("the artifact download endpoint returned HTTP {0}; a signed redirect was expected" -f $status)
            }
            $redirectCode = $status
            $location = $response.Headers.Location
            if ($null -eq $location) {
                Fail "missing-redirect-location" "the artifact download response carried no Location header; the platform contract cannot be satisfied"
            }
            $parsed = [System.Uri]::new(([System.Uri]$location).AbsoluteUri)
            if ($parsed.Scheme -ne "https") {
                Fail "redirect-scheme-rejected" ("the signed redirect scheme '{0}' is not https" -f $parsed.Scheme)
            }
            if (-not (Test-RedirectHostAllowed $parsed.Host $AllowedHosts)) {
                Fail "redirect-host-rejected" ("the signed redirect host '{0}' is not in the permitted artifact host set; the live platform contract must be confirmed" -f $parsed.Host)
            }
        } finally {
            $response.Dispose()
        }

        $second = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $parsed)
        $signed = $client.SendAsync($second, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        [string]$digest = ""
        [long]$total = 0
        try {
            if ([int]$signed.StatusCode -ne 200) {
                Fail "signed-download-status" ("the signed artifact download returned HTTP {0}" -f [int]$signed.StatusCode)
            }
            $sha = [System.Security.Cryptography.SHA256]::Create()
            $input = $signed.Content.ReadAsStream()
            $output = [System.IO.File]::Create($DestinationPath)
            $buffer = [byte[]]::new(1048576)
            try {
                while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $total = $total + $read
                    if ($total -gt $MaxBytes) { Fail "transport-size-exceeded" ("the downloaded archive exceeded the bounded transport limit of {0} bytes" -f $MaxBytes) }
                    $output.Write($buffer, 0, $read)
                    $null = $sha.TransformBlock($buffer, 0, $read, $null, 0)
                }
                $null = $sha.TransformFinalBlock([byte[]]::new(0), 0, 0)
                $digest = [System.Convert]::ToHexString($sha.Hash).ToLowerInvariant()
            } finally {
                $output.Dispose()
                $input.Dispose()
                $sha.Dispose()
            }
        } finally {
            $signed.Dispose()
        }

        return [pscustomobject]@{
            Sha256       = $digest
            Bytes        = $total
            RedirectHost = $parsed.Host.ToLowerInvariant()
            RedirectCode = $redirectCode
        }
    } finally {
        if ($null -ne $first) { $first.Dispose() }
        if ($null -ne $second) { $second.Dispose() }
        $client.Dispose()
    }
}



function Read-RuntimeManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { Fail "missing-runtime-manifest" ("the builder runtime manifest is absent: {0}" -f $Path) }
    $text = [System.IO.File]::ReadAllText($Path)
    try {
        $document = $text | ConvertFrom-Json -ErrorAction Stop
    } catch {
        Fail "malformed-runtime-manifest" ("the builder runtime manifest is not valid JSON: {0}" -f $_.Exception.Message)
    }

    $version = Get-JsonField $document "schemaVersion"
    if ("$version" -ne "$script:RuntimeManifestSchemaVersion") {
        Fail "unsupported-runtime-manifest-schema" ("the builder runtime manifest declared schemaVersion '{0}'; expected {1}" -f "$version", $script:RuntimeManifestSchemaVersion)
    }

    $files = @(Get-JsonField $document "files")
    if ($files.Count -eq 0) { Fail "empty-runtime-manifest" "the builder runtime manifest declares no files" }

    $entries = New-Object System.Collections.Generic.List[object]
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $seenCaseInsensitive = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($file in $files) {
        $path = "$(Get-JsonField $file 'path')"
        $hash = "$(Get-JsonField $file 'sha256')"
        $bytes = Get-JsonField $file "bytes"
        $reason = Test-ArchiveEntryName $path
        if ($null -ne $reason) { Fail "malformed-runtime-manifest-entry" ("manifest path '{0}' is unsafe: {1}" -f $path, $reason) }
        if ($hash -cnotmatch '^[0-9a-f]{64}$') { Fail "malformed-runtime-manifest-entry" ("manifest sha256 for '{0}' is not a lowercase SHA-256" -f $path) }
        if ($null -eq $bytes -or "$bytes" -notmatch '^[0-9]+$') { Fail "malformed-runtime-manifest-entry" ("manifest bytes for '{0}' is not a non-negative integer" -f $path) }
        if (-not $seen.Add($path)) { Fail "duplicate-runtime-manifest-entry" ("manifest path '{0}' appears more than once" -f $path) }
        if (-not $seenCaseInsensitive.Add($path)) { Fail "case-colliding-runtime-manifest-entry" ("manifest path '{0}' collides under case-insensitive comparison" -f $path) }
        $entries.Add([pscustomobject]@{ Path = $path; Sha256 = $hash; Bytes = [long]$bytes })
    }

    return $entries
}

function Assert-ManifestMatchesDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$ManifestEntries,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $rootFull)) { Fail "missing-runtime-directory" ("the extracted runtime directory is absent: {0}" -f $rootFull) }

    $actual = New-Object System.Collections.Generic.List[string]
    foreach ($file in [System.IO.Directory]::EnumerateFiles($rootFull, "*", [System.IO.SearchOption]::AllDirectories)) {
        $relative = [System.IO.Path]::GetRelativePath($rootFull, $file).Replace("\", "/")
        $actual.Add($relative)
    }

    $expectedSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($entry in $ManifestEntries) { $null = $expectedSet.Add($entry.Path) }
    $actualSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$actual, [System.StringComparer]::Ordinal)

    if (-not $expectedSet.SetEquals($actualSet)) {
        $missingNames = @($ManifestEntries | Where-Object { -not $actualSet.Contains($_.Path) } | ForEach-Object { $_.Path }) -join ","
        $extraNames = @($actual | Where-Object { -not $expectedSet.Contains($_) }) -join ","
        Fail "runtime-manifest-set-mismatch" ("the extracted runtime file set differs from the builder manifest (missing: {0}; unexpected: {1})" -f $missingNames, $extraNames)
    }

    foreach ($entry in $ManifestEntries) {
        $target = Join-Path $rootFull ($entry.Path.Replace("/", $script:Separator))
        $observedSha = Get-FileSha256Hex $target
        if ($observedSha -ne $entry.Sha256) {
            Fail "runtime-manifest-hash-mismatch" ("extracted '{0}' hashed to '{1}' but the builder manifest expects '{2}'" -f $entry.Path, $observedSha, $entry.Sha256)
        }
        $observedBytes = [long]([System.IO.FileInfo]::new($target).Length)
        if ($observedBytes -ne $entry.Bytes) {
            Fail "runtime-manifest-size-mismatch" ("extracted '{0}' is {1} bytes but the builder manifest expects {2}" -f $entry.Path, $observedBytes, $entry.Bytes)
        }
    }
}



function Write-BootstrapReceipt {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Receipt,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $full = [System.IO.Path]::GetFullPath($Path)
    $parent = [System.IO.Path]::GetDirectoryName($full)
    if (-not [string]::IsNullOrEmpty($parent)) { [System.IO.Directory]::CreateDirectory($parent) | Out-Null }
    $temporary = "{0}.tmp-{1}" -f $full, ([Guid]::NewGuid().ToString("N"))
    ($Receipt | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $full -Force
}



if ($null -ne $script:TestTransport) { Assert-TestTransport -Value $TestTransport }

$componentValue = $null
if (-not [string]::IsNullOrWhiteSpace($Component)) { $componentValue = $Component }

$receipt = [ordered]@{
    schemaVersion         = $script:ReceiptSchemaVersion
    mode                  = $Mode
    status                = "failed"
    transport             = "github-api"
    apiOrigin             = $null
    repository            = $Repository
    repositoryId          = $RepositoryId
    runId                 = $RunId
    runAttempt            = $RunAttempt
    eventSha              = $EventSha
    observedEvent         = $null
    role                  = $Role
    component             = $componentValue
    artifact              = $null
    runAttemptObservation = $null
    uploaderJob           = $null
    download              = $null
    outerArchive          = $null
    envelope              = $null
    runtime               = $null
    failures              = @()
}

$createdInstallRoot = $false
# Stays false until every receipt-destination guard passes, so a rejected or overlapping receipt target is never created by the run that rejected it.
$receiptDestinationValidated = $false
try {
    if ($EventSha -cnotmatch '^[0-9a-f]{40}$') { Fail "malformed-input" "-EventSha is not a lowercase 40-character commit SHA" }
    if ($RepositoryId -cnotmatch '^[0-9]+$') { Fail "malformed-input" "-RepositoryId is not a numeric platform repository id" }
    if ($UploaderJobId -cnotmatch '^[0-9]+$') { Fail "malformed-input" "-UploaderJobId is not a numeric platform job id" }

    # Receipt-destination safety is decided before any platform call, credential read or install action, so the failure path knows that destination is safe wherever a later failure occurs.
    if (Test-PathOverlap $InstallRoot $ReceiptPath) { Fail "install-overlaps-receipt" "the install root must not overlap the receipt path" }
    # The bootstrap receipt is a different document from the candidate `receipt.json` the compiled publisher reads, so it must never be written into the candidate payload directory.
    if (-not [string]::IsNullOrWhiteSpace($CandidateRoot) -and (Test-PathOverlap $ReceiptPath $CandidateRoot)) {
        Fail "receipt-overlaps-candidate" "the bootstrap receipt must not be written into the candidate payload directory"
    }
    $receiptDestinationValidated = $true

    $apiOrigin = Resolve-ApiOrigin -Value $ApiBase
    $receipt.apiOrigin = $apiOrigin
    if ($null -ne $script:TestTransport) { $receipt.transport = "test-stub" }

    $binding = Resolve-RoleBinding -Json $RoleMapJson -Role $Role -ModeValue $Mode -Component $Component -ArtifactName $ArtifactName

    if ($Mode -eq "FetchCandidate" -and [string]::IsNullOrWhiteSpace($Component)) {
        Fail "missing-input" "-Component is required for the FetchCandidate mode"
    }
    if ($Mode -eq "InstallRuntime") {
        if ([string]::IsNullOrWhiteSpace($EnvelopeSha256)) { Fail "missing-input" "-EnvelopeSha256 is required for the InstallRuntime mode" }
        if ($EnvelopeSha256 -cnotmatch '^[0-9a-f]{64}$') { Fail "malformed-input" "-EnvelopeSha256 is not a lowercase SHA-256" }
        if ($ManifestSha256 -cnotmatch '^[0-9a-f]{64}$') { Fail "malformed-input" "-ManifestSha256 is not a lowercase SHA-256" }
        if ([string]::IsNullOrWhiteSpace($ExpectedEnvelopeName)) { Fail "missing-input" "-ExpectedEnvelopeName is required for the InstallRuntime mode" }
        if ($ExpectedOuterEntries -notcontains $ExpectedEnvelopeName) { Fail "malformed-input" "-ExpectedEnvelopeName must be one of the exact outer entries" }
        if ($ExpectedOuterEntries -notcontains $ExpectedManifestName) { Fail "malformed-input" "-ExpectedManifestName must be one of the exact outer entries" }
    }

    # The normalized platform digest is decoded once here and is what every later comparison uses; inner builder hashes never enter this value.
    $platformDigestExpected = ConvertFrom-PlatformDigest -Value $PlatformDigest -FieldName "the -PlatformDigest trusted upload output"

    if (Test-PathOverlap $InstallRoot $CheckoutRoot) { Fail "install-overlaps-checkout" "the install root must not overlap the checkout" }
    if (-not [string]::IsNullOrWhiteSpace($CandidateRoot) -and (Test-PathOverlap $InstallRoot $CandidateRoot)) { Fail "install-overlaps-candidate" "the install root must not overlap the candidate root" }
    if (Test-Path -LiteralPath $InstallRoot) { Fail "install-root-exists" ("the install root already exists; the bootstrap never overwrites a user directory: {0}" -f $InstallRoot) }

    [System.IO.Directory]::CreateDirectory($InstallRoot) | Out-Null
    $createdInstallRoot = $true

    $token = [System.Environment]::GetEnvironmentVariable($TokenEnvironmentVariable)
    if ([string]::IsNullOrWhiteSpace($token)) {
        Fail "missing-token" ("environment variable '{0}' is not set; the bootstrap never accepts a token value on the command line" -f $TokenEnvironmentVariable)
    }

    Write-Host ("bootstrap {0}: artifact {1} '{2}' (run {3} attempt {4})" -f $Mode, $ArtifactId, $ArtifactName, $RunId, $RunAttempt)

    $artifact = Get-MetadataDocument -Uri ("{0}/repos/{1}/actions/artifacts/{2}" -f $apiOrigin, $Repository, $ArtifactId) -Token $token
    $null = Assert-JsonString -Document $artifact -Field "id" -Expected $ArtifactId -Code "artifact-id-mismatch" -Subject "the platform artifact" -CaseSensitive $true
    $null = Assert-JsonString -Document $artifact -Field "name" -Expected $ArtifactName -Code "artifact-name-mismatch" -Subject "the platform artifact" -CaseSensitive $true
    if ([bool](Get-JsonField $artifact "expired")) { Fail "artifact-expired" "the platform reports the artifact as expired" }

    $artifactRun = Get-JsonField $artifact "workflow_run"
    if ($null -eq $artifactRun) { Fail "missing-platform-field" "the artifact response carried no workflow_run object; the run/event SHA cannot be bound" }
    $null = Assert-JsonString -Document $artifactRun -Field "id" -Expected $RunId -Code "artifact-run-mismatch" -Subject "the artifact workflow_run" -CaseSensitive $true
    $null = Assert-JsonString -Document $artifactRun -Field "head_sha" -Expected $EventSha -Code "artifact-event-sha-mismatch" -Subject "the artifact workflow_run" -CaseSensitive $true
    # These optional artifact fields are rejected only when they contradict the bound repository, without inventing a required field the platform may not emit.
    $artifactRepositoryId = "$(Get-JsonField $artifactRun 'repository_id')"
    if (-not [string]::IsNullOrWhiteSpace($artifactRepositoryId) -and $artifactRepositoryId -cne $RepositoryId) {
        Fail "artifact-repository-mismatch" ("the artifact workflow_run names repository id '{0}', not the bound repository '{1}'" -f $artifactRepositoryId, $RepositoryId)
    }
    $artifactHeadRepositoryId = "$(Get-JsonField $artifactRun 'head_repository_id')"
    if (-not [string]::IsNullOrWhiteSpace($artifactHeadRepositoryId) -and $artifactHeadRepositoryId -cne $RepositoryId) {
        Fail "artifact-head-repository-mismatch" ("the artifact workflow_run names head repository id '{0}', not the bound repository '{1}'" -f $artifactHeadRepositoryId, $RepositoryId)
    }

    # The run endpoint without an attempt segment answers with the latest attempt, so this bootstrap never calls it.
    $attemptUri = "{0}/repos/{1}/actions/runs/{2}/attempts/{3}" -f $apiOrigin, $Repository, $RunId, $RunAttempt
    $run = Get-MetadataDocument -Uri $attemptUri -Token $token
    $null = Assert-JsonString -Document $run -Field "id" -Expected $RunId -Code "run-id-mismatch" -Subject "the attempt-specific workflow run" -CaseSensitive $true
    $null = Assert-JsonString -Document $run -Field "run_attempt" -Expected $RunAttempt -Code "run-attempt-mismatch" -Subject "the attempt-specific workflow run" -CaseSensitive $true
    $null = Assert-JsonString -Document $run -Field "head_sha" -Expected $EventSha -Code "run-event-sha-mismatch" -Subject "the attempt-specific workflow run" -CaseSensitive $true
    $observedEvent = "$(Get-JsonField $run 'event')"
    if (-not [string]::IsNullOrWhiteSpace($ExpectedEvent)) {
        $null = Assert-JsonString -Document $run -Field "event" -Expected $ExpectedEvent -Code "run-event-mismatch" -Subject "the attempt-specific workflow run" -CaseSensitive $true
    }

    $runRepository = Get-JsonField $run "repository"
    if ($null -eq $runRepository) { Fail "missing-platform-field" "the attempt-specific run response carried no repository object; the repository binding cannot be completed" }
    $null = Assert-JsonString -Document $runRepository -Field "id" -Expected $RepositoryId -Code "run-repository-mismatch" -Subject "the observed repository" -CaseSensitive $true
    # Owner/name are case-insensitive platform identifiers; the observed canonical full_name is recorded.
    $observedFullName = Assert-JsonString -Document $runRepository -Field "full_name" -Expected $Repository -Code "run-repository-name-mismatch" -Subject "the observed repository" -CaseSensitive $false -CasePhrase "case-insensitive repository name"
    $headRepository = Get-JsonField $run "head_repository"
    if ($null -ne $headRepository) {
        $null = Assert-JsonString -Document $headRepository -Field "full_name" -Expected $Repository -Code "run-head-repository-mismatch" -Subject "the head repository of the attempt-specific run" -CaseSensitive $false -CasePhrase "case-insensitive repository name"
    }

    # The artifact object carries no attempt field and the single-job response does not require one, so the uploader job is found in the attempt-scoped listing (the attempt is fixed by the request URL) and then required to be completed and successful.
    $jobsBaseUri = "{0}/repos/{1}/actions/runs/{2}/attempts/{3}/jobs" -f $apiOrigin, $Repository, $RunId, $RunAttempt
    $job = $null
    [int]$jobPage = 1
    [long]$enumeratedJobs = 0
    while ($null -eq $job) {
        $listing = Get-MetadataDocument -Uri ("{0}?per_page=100&page={1}" -f $jobsBaseUri, $jobPage) -Token $token
        $declaredJobs = "$(Get-JsonField $listing 'total_count')"
        if ($declaredJobs -cnotmatch '^[0-9]+$') { Fail "malformed-platform-field" ("the attempt-scoped job listing declared total_count '{0}', which is not a non-negative integer" -f $declaredJobs) }
        [long]$declaredJobCount = [long]$declaredJobs
        if ($declaredJobCount -gt $script:MaxJobListingEntries) {
            Fail "uploader-job-listing-too-large" ("the run attempt declares {0} jobs, above the bounded {1}-job listing limit" -f $declaredJobCount, $script:MaxJobListingEntries)
        }
        $listedJobs = @(Get-JsonField $listing "jobs")
        if ($listedJobs.Count -eq 0) { break }
        foreach ($candidateJob in $listedJobs) {
            $enumeratedJobs = $enumeratedJobs + 1
            if ("$(Get-JsonField $candidateJob 'id')" -ceq $UploaderJobId) { $job = $candidateJob; break }
        }
        if ($null -ne $job) { break }
        if ($enumeratedJobs -ge $declaredJobCount) { break }
        $jobPage = $jobPage + 1
        if ($jobPage -gt $script:MaxJobListingPages) {
            Fail "uploader-job-listing-too-large" ("the run attempt job listing exceeded the bounded {0}-page read" -f $script:MaxJobListingPages)
        }
    }
    if ($null -eq $job) {
        Fail "uploader-job-not-in-attempt" ("the run attempt '{0}' declares no job with the id '{1}'; attempt-scoped successful job evidence is required" -f $RunAttempt, $UploaderJobId)
    }

    $null = Assert-JsonString -Document $job -Field "id" -Expected $UploaderJobId -Code "uploader-job-id-mismatch" -Subject "the platform uploader job" -CaseSensitive $true
    $null = Assert-JsonString -Document $job -Field "run_id" -Expected $RunId -Code "uploader-job-run-mismatch" -Subject "the platform uploader job" -CaseSensitive $true
    $null = Assert-JsonString -Document $job -Field "head_sha" -Expected $EventSha -Code "uploader-job-event-sha-mismatch" -Subject "the platform uploader job" -CaseSensitive $true
    $null = Assert-JsonString -Document $job -Field "name" -Expected $binding.UploaderJobName -Code "uploader-job-name-mismatch" -Subject "the platform uploader job" -CaseSensitive $true
    $null = Assert-JsonString -Document $job -Field "status" -Expected "completed" -Code "uploader-job-not-completed" -Subject "the platform uploader job" -CaseSensitive $true
    $null = Assert-JsonString -Document $job -Field "conclusion" -Expected "success" -Code "uploader-job-not-successful" -Subject "the platform uploader job" -CaseSensitive $true
    # run_attempt is optional in the documented job schema, so it is rejected only on contradiction; the binding comes from the attempt-scoped listing URL.
    $jobAttempt = "$(Get-JsonField $job 'run_attempt')"
    if (-not [string]::IsNullOrWhiteSpace($jobAttempt) -and $jobAttempt -cne $RunAttempt) {
        Fail "uploader-job-attempt-mismatch" ("the uploader job reports run_attempt '{0}', not the bound attempt '{1}'" -f $jobAttempt, $RunAttempt)
    }

    $receipt.runAttemptObservation = [ordered]@{
        uri                = $attemptUri
        id                 = "$RunId"
        runAttempt         = "$RunAttempt"
        headSha            = $EventSha
        event              = $observedEvent
        repositoryId       = $RepositoryId
        repositoryFullName = $observedFullName
    }
    $receipt.uploaderJob = [ordered]@{
        id                = "$UploaderJobId"
        name              = $binding.UploaderJobName
        runId             = "$RunId"
        runAttempt        = "$RunAttempt"
        headSha           = $EventSha
        status            = "completed"
        conclusion        = "success"
        listingUri        = $jobsBaseUri
        artifactLinkLimit = "the platform exposes no artifact-to-job link; the artifact id is accepted from the uploader job output and this attempt-scoped successful job is the strongest available binding"
    }

    $apiDigestRaw = "$(Get-JsonField $artifact 'digest')"
    $apiDigest = ConvertFrom-PlatformDigest -Value $apiDigestRaw -FieldName "the platform artifact digest"
    if ($apiDigest -cne $platformDigestExpected) {
        Fail "platform-digest-mismatch" ("the platform artifact digest '{0}' does not equal the trusted upload output '{1}'" -f $apiDigest, $platformDigestExpected)
    }

    $platformSize = "$(Get-JsonField $artifact 'size_in_bytes')"
    if ($platformSize -cnotmatch '^[0-9]+$') { Fail "malformed-platform-field" ("the platform artifact size_in_bytes '{0}' is not a non-negative integer" -f $platformSize) }
    [long]$platformSizeBytes = [long]$platformSize

    $receipt.artifact = [ordered]@{
        id             = $ArtifactId
        name           = "$(Get-JsonField $artifact 'name')"
        role           = $Role
        component      = $componentValue
        sizeInBytes    = $platformSizeBytes
        expired        = [bool](Get-JsonField $artifact "expired")
        apiDigestRaw   = $apiDigestRaw
        apiDigest      = $apiDigest
        platformDigest = $platformDigestExpected
        digestSource   = "actions/upload-artifact artifact-digest (outer platform zip) and actions/artifacts API digest, both normalized to bare lowercase sha256 hex"
    }

    $rawPath = Join-Path $InstallRoot "artifact-download.zip"
    $download = Get-ArtifactRawArchive `
        -DownloadUri ("{0}/repos/{1}/actions/artifacts/{2}/zip" -f $apiOrigin, $Repository, $ArtifactId) `
        -Token $token `
        -DestinationPath $rawPath `
        -AllowedHosts $AllowedRedirectHosts `
        -MaxBytes $MaxTransportBytes

    if ($download.Sha256 -cne $platformDigestExpected) { Fail "transport-digest-mismatch" ("the observed SHA-256 of the downloaded bytes '{0}' does not equal the trusted platform digest '{1}'" -f $download.Sha256, $platformDigestExpected) }
    if ($download.Sha256 -cne $apiDigest) { Fail "transport-api-digest-mismatch" ("the observed SHA-256 of the downloaded bytes '{0}' does not equal the platform API digest '{1}'" -f $download.Sha256, $apiDigest) }
    if ($download.Bytes -ne $platformSizeBytes) { Fail "transport-platform-size-mismatch" ("the streamed artifact is {0} bytes but the platform metadata declares {1} bytes" -f $download.Bytes, $platformSizeBytes) }

    $receipt.download = [ordered]@{
        redirectHost   = $download.RedirectHost
        redirectCode   = $download.RedirectCode
        bytesRead      = $download.Bytes
        platformBytes  = $platformSizeBytes
        observedSha256 = $download.Sha256
    }

    $outer = Assert-ZipArchiveSafety `
        -ArchivePath $rawPath `
        -ExpectedNames $ExpectedOuterEntries `
        -MaxCount $MaxEntryCount `
        -MaxTotal $MaxTotalUncompressedBytes `
        -MaxEntry $MaxEntryUncompressedBytes `
        -MaxRatio $MaxCompressionRatio

    $receipt.outerArchive = [ordered]@{
        entryCount             = $outer.EntryCount
        totalUncompressedBytes = $outer.TotalUncompressedBytes
        entries                = @($outer.Names)
    }

    if ($Mode -eq "FetchCandidate") {
        $candidateDirectory = Join-Path $InstallRoot "candidate"
        $null = Expand-SafeArchive -ArchivePath $rawPath -Destination $candidateDirectory -MaxTotal $MaxTotalUncompressedBytes
        $receipt.runtime = [ordered]@{ candidateDirectory = $candidateDirectory; role = $Role; component = $binding.Component }
        $receipt.status = "fetched"
        Write-Host ("bootstrap fetched candidate '{0}' into an isolated directory" -f $binding.Component)
    } else {
        $stageDirectory = Join-Path $InstallRoot "stage"
        $null = Expand-SafeArchive -ArchivePath $rawPath -Destination $stageDirectory -MaxTotal $MaxTotalUncompressedBytes

        # Builder-reported inner hashes stay a namespace separate from the outer platform digest.
        $envelopePath = Join-Path $stageDirectory $ExpectedEnvelopeName
        $envelopeSha = Get-FileSha256Hex $envelopePath
        if ($envelopeSha -cne $EnvelopeSha256) { Fail "envelope-digest-mismatch" ("the extracted builder envelope hashed to '{0}' but the trusted builder reported '{1}'" -f $envelopeSha, $EnvelopeSha256) }

        $manifestPath = Join-Path $stageDirectory $ExpectedManifestName
        if (-not [string]::IsNullOrWhiteSpace($ManifestSha256)) {
            $manifestSha = Get-FileSha256Hex $manifestPath
            if ($manifestSha -cne $ManifestSha256) { Fail "manifest-digest-mismatch" ("the extracted builder manifest hashed to '{0}' but the trusted builder reported '{1}'" -f $manifestSha, $ManifestSha256) }
        }

        $manifestEntries = @(Read-RuntimeManifest -Path $manifestPath)
        $innerNames = @($manifestEntries | ForEach-Object { $_.Path })

        $inner = Assert-ZipArchiveSafety `
            -ArchivePath $envelopePath `
            -ExpectedNames $innerNames `
            -MaxCount $MaxEntryCount `
            -MaxTotal $MaxTotalUncompressedBytes `
            -MaxEntry $MaxEntryUncompressedBytes `
            -MaxRatio $MaxCompressionRatio

        $receipt.envelope = [ordered]@{
            name           = $ExpectedEnvelopeName
            builderSha256  = $EnvelopeSha256
            observedSha256 = $envelopeSha
            manifestName   = $ExpectedManifestName
            manifestSha256 = $ManifestSha256
            entryCount     = $inner.EntryCount
            digestSource   = "builder-reported inner envelope/manifest hashes; never conflated with the outer platform digest"
        }

        if (-not [string]::IsNullOrWhiteSpace($RequiredRuntimeFile) -and -not ($innerNames -contains $RequiredRuntimeFile)) {
            Fail "missing-runtime-entry" ("the builder manifest does not declare the required runtime entry '{0}'" -f $RequiredRuntimeFile)
        }

        $runtimePartial = Join-Path $InstallRoot "runtime.partial"
        $runtimeFinal = Join-Path $InstallRoot "runtime"
        $null = Expand-SafeArchive -ArchivePath $envelopePath -Destination $runtimePartial -MaxTotal $MaxTotalUncompressedBytes
        Assert-ManifestMatchesDirectory -ManifestEntries $manifestEntries -Root $runtimePartial
        Move-Item -LiteralPath $runtimePartial -Destination $runtimeFinal

        $receipt.runtime = [ordered]@{
            installRoot   = $runtimeFinal
            fileCount     = $manifestEntries.Count
            requiredEntry = $RequiredRuntimeFile
        }
        $receipt.status = "installed"
        Write-Host ("bootstrap installed {0} verified runtime file(s) into an isolated directory" -f $manifestEntries.Count)
    }
} catch {
    $message = $_.Exception.Message
    $parts = $message -split '\|', 2
    if ($parts.Count -eq 2) { $code = $parts[0]; $detail = $parts[1] } else { $code = "unhandled-failure"; $detail = $message }
    $receipt.status = "failed"
    $receipt.failures = @([ordered]@{ code = $code; detail = $detail })
    if ($createdInstallRoot -and (Test-Path -LiteralPath $InstallRoot)) {
        try { Remove-Item -LiteralPath $InstallRoot -Recurse -Force -ErrorAction Stop } catch { }
    }
    Write-Error -Message ("bootstrap failed [{0}] {1}" -f $code, $detail) -ErrorAction Continue
    if ($receiptDestinationValidated) {
        Write-BootstrapReceipt -Receipt $receipt -Path $ReceiptPath
    } else {
        Write-Error -Message ("bootstrap failure receipt suppressed [{0}]: the receipt destination '{1}' was not validated as a safe, non-overlapping path, so no failure receipt was written anywhere" -f $code, $ReceiptPath) -ErrorAction Continue
    }
    exit 1
}

Write-BootstrapReceipt -Receipt $receipt -Path $ReceiptPath
Write-Host ("bootstrap {0} complete; receipt written" -f $receipt.status)
exit 0
