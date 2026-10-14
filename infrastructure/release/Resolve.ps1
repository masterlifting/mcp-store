#requires -Version 7.0
# Transport-only uploader-job resolver: it turns an attempt-scoped, exact-rendered job name into the platform job id that Bootstrap.ps1 already validates as its -UploaderJobId input.
# It never parses Config.fsx, a publisher receipt or an artifact digest, never derives policy and never drives a transaction; the job context's check_run_id is unusable without service containers, so the id is observed instead of inferred.
# Authentication is a runtime environment lookup; the bearer credential is never printed and is never forwarded across a redirect.

[CmdletBinding()]
param(
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

    # The same independently authored role expectation map Bootstrap.ps1 consumes: the expected job name and the candidate component come from the caller's map, never from a platform response.
    [Parameter(Mandatory = $true)]
    [string]$RoleMapJson,

    [string]$Component = "",

    # Written only when the caller names a path; stdout always carries the same JSON document.
    [string]$OutputPath = "",

    [string]$ApiBase = "https://api.github.com",
    [string]$TokenEnvironmentVariable = "GITHUB_TOKEN",

    # Test-only transport seam, mirroring Bootstrap.ps1: refused together with the production origin and required by any other -ApiBase.
    [System.Collections.IDictionary]$TestTransport = $null
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:ApiVersion = "2022-11-28"
$script:ProductionApiOrigin = "https://api.github.com"
$script:MaxMetadataBytes = 4194304
$script:PageSize = 100
$script:MaxJobListingPages = 10
$script:MaxJobListingEntries = 1000
$script:RoleKeys = @("publisher", "runtime", "candidate")
$script:TestTransport = $TestTransport

function Fail {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Code, [Parameter(Mandatory = $true)][string]$Detail)

    throw ("{0}|{1}" -f $Code, $Detail)
}

function Get-JsonField {
    [CmdletBinding()]
    param($Object, [Parameter(Mandatory = $true)][string]$Name)

    if ($null -eq $Object) { return $null }
    if ($Object -isnot [System.Management.Automation.PSCustomObject] -and $Object -isnot [System.Collections.IDictionary]) { return $null }
    if ($Object -is [System.Collections.IDictionary]) {
        if (-not $Object.Contains($Name)) { return $null }
        return $Object[$Name]
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# Every platform identifier comparison states its own case policy; only repository owner/name are case-insensitive.
function Assert-JsonString {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Document,
        [Parameter(Mandatory = $true)][string]$Field,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][string]$Subject,
        [bool]$CaseSensitive = $true
    )

    $value = "$(Get-JsonField $Document $Field)"
    $equal = if ($CaseSensitive) { $value -ceq $Expected } else { $value -ieq $Expected }
    if (-not $equal) {
        Fail $Code ("{0} reports {1} '{2}', not the bound '{3}'" -f $Subject, $Field, $value, $Expected)
    }

    return $value
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
        Fail "test-transport-on-production-origin" "the test transport seam must not be combined with the production platform origin"
    }
    if ($origin -cne $script:ProductionApiOrigin -and $null -eq $script:TestTransport) {
        Fail "non-production-api-base" ("-ApiBase '{0}' is not the production platform origin; a non-production API base is only accepted together with the explicit test transport seam" -f $origin)
    }
    if ($null -ne $script:TestTransport) {
        $keys = @($script:TestTransport.Keys | ForEach-Object { "$_" })
        if ($keys.Count -ne 1 -or $keys[0] -cne "MetadataJson") { Fail "invalid-test-transport" "the test transport seam must carry exactly the 'MetadataJson' script block" }
        if ($script:TestTransport['MetadataJson'] -isnot [scriptblock]) { Fail "invalid-test-transport" "the test transport seam entry 'MetadataJson' is not a script block" }
    }

    return $origin
}

function Read-BoundedStream {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Stream, [Parameter(Mandatory = $true)][long]$MaxBytes, [Parameter(Mandatory = $true)][string]$Code, [Parameter(Mandatory = $true)][string]$Subject)

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

# Bounded, non-redirecting metadata read: a 3xx answer ends the call instead of replaying the bearer credential at another origin.
function Get-MetadataDocument {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Uri, [Parameter(Mandatory = $true)][string]$Token)

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
                    Fail "metadata-redirect-rejected" ("the platform metadata endpoint '{0}' answered HTTP {1} with a redirect; the bearer credential is never forwarded" -f $Uri, $status)
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

# The expected rendered job name and the candidate component come from the caller's independent map, so no second expectation can drift from Bootstrap.ps1's binding.
function Resolve-ExpectedJobName {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Json, [Parameter(Mandatory = $true)][string]$RoleName, [string]$ComponentValue)

    if ([string]::IsNullOrWhiteSpace($Json)) { Fail "missing-input" "-RoleMapJson must carry the role expectation map" }

    foreach ($key in $script:RoleKeys) {
        if ([System.Text.RegularExpressions.Regex]::Matches($Json, ('"' + $key + '"\s*:')).Count -gt 1) {
            Fail "duplicate-role-map-key" ("the role map declares the key '{0}' more than once" -f $key)
        }
    }

    $map = $null
    try {
        $map = $Json | ConvertFrom-Json -AsHashtable -Depth 8 -ErrorAction Stop
    } catch {
        Fail "malformed-role-map" ("-RoleMapJson is not valid JSON: {0}" -f $_.Exception.Message)
    }
    if ($map -isnot [System.Collections.IDictionary] -or $map.Count -eq 0) { Fail "malformed-role-map" "-RoleMapJson must be a non-empty JSON object keyed by role" }

    foreach ($key in @($map.Keys | ForEach-Object { "$_" })) {
        if ($key -cnotin $script:RoleKeys) { Fail "malformed-role-map" ("the role map carries the unknown role key '{0}'" -f $key) }
    }
    if (@($map.Keys | ForEach-Object { "$_" }) -cnotcontains $RoleName) { Fail "role-not-in-map" ("the role map declares no expectation for the role '{0}'" -f $RoleName) }

    $entry = $map[$RoleName]
    if ($entry -isnot [System.Collections.IDictionary]) { Fail "malformed-role-map" ("the role map entry for '{0}' is not a JSON object" -f $RoleName) }
    $entryKeys = @($entry.Keys | ForEach-Object { "$_" })
    foreach ($key in $entryKeys) {
        if ($key -cnotin @("artifactName", "uploaderJobName", "component")) { Fail "malformed-role-map" ("the role map entry for '{0}' carries the unknown field '{1}'" -f $RoleName, $key) }
    }
    foreach ($required in @("artifactName", "uploaderJobName")) {
        if ($entryKeys -cnotcontains $required) { Fail "malformed-role-map" ("the role map entry for '{0}' is missing '{1}'" -f $RoleName, $required) }
    }

    $jobName = "$($entry['uploaderJobName'])"
    if ([string]::IsNullOrWhiteSpace($jobName)) { Fail "malformed-role-map" ("the role map entry for '{0}' declares an empty uploaderJobName" -f $RoleName) }

    $observedComponent = if ([string]::IsNullOrWhiteSpace($ComponentValue)) { $null } else { $ComponentValue }
    if ($RoleName -ceq "candidate") {
        if ($null -eq $observedComponent) { Fail "role-component-mismatch" "-Component is required for the candidate role" }
        $mappedComponent = "$($entry['component'])"
        if ([string]::IsNullOrWhiteSpace($mappedComponent)) { Fail "role-component-mismatch" "the candidate role requires an explicit component in the role map" }
        if ($mappedComponent -cne $observedComponent) { Fail "role-component-mismatch" ("-Component '{0}' does not equal the independently supplied candidate component '{1}'" -f $observedComponent, $mappedComponent) }
    } elseif ($null -ne $observedComponent) {
        Fail "role-component-mismatch" ("-Component must be empty for the role '{0}'" -f $RoleName)
    }

    return [pscustomobject]@{ UploaderJobName = $jobName; ArtifactName = "$($entry['artifactName'])"; Component = $observedComponent }
}

function Write-ResultDocument {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary]$Document, [Parameter(Mandatory = $true)][int]$ExitCode)

    $payload = $Document | ConvertTo-Json -Depth 8
    Write-Output $payload
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $parent = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($OutputPath))
        if (-not [string]::IsNullOrEmpty($parent)) { [System.IO.Directory]::CreateDirectory($parent) | Out-Null }
        Set-Content -LiteralPath $OutputPath -Value $payload -Encoding utf8
    }
    exit $ExitCode
}

$componentValue = if ([string]::IsNullOrWhiteSpace($Component)) { $null } else { $Component }
$result = [ordered]@{
    status         = "failed"
    reason         = "unhandled"
    detail         = ""
    role           = $Role
    component      = $componentValue
    repository     = $Repository
    repositoryId   = $RepositoryId
    runId          = $RunId
    runAttempt     = $RunAttempt
    eventSha       = $EventSha
    expectedJobName = $null
    uploaderJobId  = $null
    apiOrigin      = $null
    apiVersion     = $script:ApiVersion
    observedJobs   = 0
    pagesRead      = 0
}

try {
    $apiOrigin = Resolve-ApiOrigin -Value $ApiBase
    $result.apiOrigin = $apiOrigin

    $binding = Resolve-ExpectedJobName -Json $RoleMapJson -RoleName $Role -ComponentValue $Component
    $result.expectedJobName = $binding.UploaderJobName

    $token = [System.Environment]::GetEnvironmentVariable($TokenEnvironmentVariable)
    if ([string]::IsNullOrWhiteSpace($token)) {
        Fail "missing-token" ("environment variable '{0}' is not set; the resolver never accepts a token value on the command line" -f $TokenEnvironmentVariable)
    }

    # The attempt segment fixes the attempt inside the request URL; the run endpoint without it would answer with the latest attempt.
    $jobsBaseUri = "{0}/repos/{1}/actions/runs/{2}/attempts/{3}/jobs" -f $apiOrigin, $Repository, $RunId, $RunAttempt

    $matched = @()
    $observed = 0
    $page = 1
    while ($true) {
        $listing = Get-MetadataDocument -Uri ("{0}?per_page={1}&page={2}" -f $jobsBaseUri, $script:PageSize, $page) -Token $token
        $result.pagesRead = $page

        $declared = "$(Get-JsonField $listing 'total_count')"
        if ($declared -cnotmatch '^[0-9]+$') { Fail "malformed-platform-field" ("the attempt-scoped job listing declared total_count '{0}', which is not a non-negative integer" -f $declared) }
        if ([long]$declared -gt $script:MaxJobListingEntries) {
            Fail "uploader-job-listing-too-large" ("the run attempt declares {0} jobs, above the bounded {1}-job listing limit" -f $declared, $script:MaxJobListingEntries)
        }

        $listed = @(Get-JsonField $listing 'jobs')
        foreach ($job in $listed) {
            $observed = $observed + 1
            # Every exact-name occurrence is collected so a duplicated rendered name fails instead of silently resolving to one of them.
            if ("$(Get-JsonField $job 'name')" -ceq $binding.UploaderJobName) { $matched += $job }
        }
        $result.observedJobs = $observed

        if ($matched.Count -gt 1) {
            Fail "uploader-job-name-not-unique" ("the run attempt '{0}' declares {1} jobs named '{2}'; the uploader job id is ambiguous" -f $RunAttempt, $matched.Count, $binding.UploaderJobName)
        }
        if ($listed.Count -eq 0) { break }
        if ($observed -ge [long]$declared) { break }
        $page = $page + 1
        if ($page -gt $script:MaxJobListingPages) {
            Fail "uploader-job-listing-too-large" ("the run attempt job listing exceeded the bounded {0}-page read" -f $script:MaxJobListingPages)
        }
    }

    if ($matched.Count -eq 0) {
        Fail "uploader-job-not-found" ("the run attempt '{0}' declares no job named '{1}'; no latest or last-resort job is substituted" -f $RunAttempt, $binding.UploaderJobName)
    }

    $job = $matched[0]
    $null = Assert-JsonString -Document $job -Field "run_id" -Expected $RunId -Code "uploader-job-run-mismatch" -Subject "the resolved uploader job"
    $null = Assert-JsonString -Document $job -Field "head_sha" -Expected $EventSha -Code "uploader-job-event-sha-mismatch" -Subject "the resolved uploader job"
    $null = Assert-JsonString -Document $job -Field "status" -Expected "completed" -Code "uploader-job-not-completed" -Subject "the resolved uploader job"
    $null = Assert-JsonString -Document $job -Field "conclusion" -Expected "success" -Code "uploader-job-not-successful" -Subject "the resolved uploader job"
    $jobAttempt = "$(Get-JsonField $job 'run_attempt')"
    if (-not [string]::IsNullOrWhiteSpace($jobAttempt) -and $jobAttempt -cne $RunAttempt) {
        Fail "uploader-job-attempt-mismatch" ("the resolved uploader job reports run_attempt '{0}', not the bound attempt '{1}'" -f $jobAttempt, $RunAttempt)
    }

    $jobId = "$(Get-JsonField $job 'id')"
    if ($jobId -cnotmatch '^[0-9]+$') { Fail "malformed-platform-field" ("the resolved uploader job id '{0}' is not a numeric platform job id" -f $jobId) }

    $result.status = "resolved"
    $result.reason = "the attempt-scoped successful job with the exact rendered uploader name is unique"
    $result.detail = ""
    $result.uploaderJobId = $jobId
    $result.jobsUri = $jobsBaseUri
    $result.artifactLinkLimit = "the platform exposes no artifact-to-job link; this attempt-scoped successful job is the strongest available binding"
    Write-ResultDocument -Document $result -ExitCode 0
} catch {
    $message = $_.Exception.Message
    $parts = $message -split '\|', 2
    $result.status = "failed"
    if ($parts.Count -eq 2) { $result.reason = $parts[0]; $result.detail = $parts[1] } else { $result.reason = "unhandled-failure"; $result.detail = $message }
    Write-Host ("::error::uploader-job-resolve failed [{0}] {1}" -f $result.reason, $result.detail)
    Write-ResultDocument -Document $result -ExitCode 1
}
