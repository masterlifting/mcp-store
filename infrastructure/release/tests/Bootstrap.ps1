#requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
$bootstrapPath = Join-Path $repositoryRoot "infrastructure/release/Bootstrap.ps1"
$releaseProjectPath = Join-Path $repositoryRoot "infrastructure/release/Release.fsproj"
$sourcesPath = Join-Path $repositoryRoot "infrastructure/release/Sources.json"
$tempBase = Join-Path $env:LOCALAPPDATA "Temp/opencode"
[System.IO.Directory]::CreateDirectory($tempBase) | Out-Null
$suiteRoot = Join-Path $tempBase ("mcp-store-bootstrap-tests-" + [guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($suiteRoot) | Out-Null

$script:AssertionCount = 0
$script:PassedScenarioCount = 0
$script:FailedScenarios = [System.Collections.Generic.List[string]]::new()

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $script:AssertionCount++
    if (-not $Condition) { throw $Message }
}

function ConvertTo-PSLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)
    return [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Write-ZipArchive {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object[]]$Entries
    )

    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $archive = $null
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        foreach ($definition in $Entries) {
            $entry = $archive.CreateEntry([string]$definition.Name, [System.IO.Compression.CompressionLevel]::Optimal)
            if ($null -ne $definition.Attributes) { $entry.ExternalAttributes = [int]$definition.Attributes }
            $entryStream = $entry.Open()
            try {
                [byte[]]$content = $definition.Bytes
                $entryStream.Write($content, 0, $content.Length)
            } finally {
                $entryStream.Dispose()
            }
        }
    } finally {
        if ($null -ne $archive) { $archive.Dispose() }
        $stream.Dispose()
    }
}

function New-ZipEntry {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [int]$Attributes = 0
    )

    return [pscustomobject]@{ Name = $Name; Bytes = $Bytes; Attributes = $Attributes }
}

function New-RuntimeTransport {
    param([Parameter(Mandatory = $true)][string]$Directory)

    $runnerBytes = [System.Text.Encoding]::UTF8.GetBytes("verified runner fixture")
    $supportBytes = [System.Text.Encoding]::UTF8.GetBytes("runtime support fixture")
    $innerPath = Join-Path $Directory "runtime-envelope.zip"
    $manifestPath = Join-Path $Directory "runtime-manifest.json"
    $outerPath = Join-Path $Directory "platform-artifact.zip"
    # The third outer member is a minimal trusted publisher receipt: the fixture declares the
    # publisher role and the same event/run/attempt the test transport serves, so the bootstrap
    # authenticates the bytes against the independently expected SHA-256 without parsing any
    # policy out of them. The manifest stays files-only and never names this member, so the
    # receipt cannot authenticate itself through the runtime.
    $trustedReceiptBytes = [System.Text.Encoding]::UTF8.GetBytes('{"schemaVersion":1,"role":"publisher","eventRevision":"0123456789abcdef0123456789abcdef01234567","runId":"5001","attempt":"2"}')
    $trustedReceiptPath = Join-Path $Directory "receipt.json"

    Write-ZipArchive -Path $innerPath -Entries @(
        (New-ZipEntry -Name "ReleaseRunner.exe" -Bytes $runnerBytes),
        (New-ZipEntry -Name "runtime/support.dat" -Bytes $supportBytes)
    )
    $innerBytes = [System.IO.File]::ReadAllBytes($innerPath)
    $manifest = [ordered]@{
        schemaVersion = 1
        files = @(
            [ordered]@{ path = "ReleaseRunner.exe"; sha256 = Get-Sha256Hex $runnerBytes; bytes = $runnerBytes.Length },
            [ordered]@{ path = "runtime/support.dat"; sha256 = Get-Sha256Hex $supportBytes; bytes = $supportBytes.Length }
        )
    }
    $manifestBytes = [System.Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 6 -Compress))
    [System.IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
    [System.IO.File]::WriteAllBytes($trustedReceiptPath, $trustedReceiptBytes)
    Write-ZipArchive -Path $outerPath -Entries @(
        (New-ZipEntry -Name "runtime-envelope.zip" -Bytes $innerBytes),
        (New-ZipEntry -Name "runtime-manifest.json" -Bytes $manifestBytes),
        (New-ZipEntry -Name "receipt.json" -Bytes $trustedReceiptBytes)
    )

    return [pscustomobject]@{
        Path = $outerPath
        InnerPath = $innerPath
        InnerSha256 = Get-Sha256Hex $innerBytes
        ManifestSha256 = Get-Sha256Hex $manifestBytes
        RunnerSha256 = Get-Sha256Hex $runnerBytes
        RunnerBytes = $runnerBytes.Length
        SupportSha256 = Get-Sha256Hex $supportBytes
        SupportBytes = $supportBytes.Length
        TrustedReceiptSha256 = Get-Sha256Hex $trustedReceiptBytes
        TrustedReceiptBytes = $trustedReceiptBytes.Length
        TrustedReceiptPath = $trustedReceiptPath
        TrustedReceiptBytesContent = $trustedReceiptBytes
        ExpectedNames = @("runtime-envelope.zip", "runtime-manifest.json", "receipt.json")
        EnvelopeName = "runtime-envelope.zip"
        ManifestName = "runtime-manifest.json"
        TrustedReceiptName = "receipt.json"
    }
}

function New-CandidateTransport {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][object]$Scenario
    )

    $outerPath = Join-Path $Directory "platform-artifact.zip"
    if ($null -ne $Scenario.ArchiveEntries) {
        $entries = @($Scenario.ArchiveEntries)
    } else {
        $entries = @(
            (New-ZipEntry -Name "candidate.zip" -Bytes ([System.Text.Encoding]::UTF8.GetBytes("candidate archive fixture"))),
            (New-ZipEntry -Name "distribution.json" -Bytes ([System.Text.Encoding]::UTF8.GetBytes('{"schemaVersion":1}'))),
            (New-ZipEntry -Name "receipt.json" -Bytes ([System.Text.Encoding]::UTF8.GetBytes('{"schemaVersion":1,"fixture":true}')))
        )
    }
    Write-ZipArchive -Path $outerPath -Entries $entries
    return [pscustomobject]@{ Path = $outerPath }
}

function New-PlatformFixture {
    param(
        [Parameter(Mandatory = $true)][object]$Scenario,
        [Parameter(Mandatory = $true)][string]$TransportSha256,
        [Parameter(Mandatory = $true)][long]$TransportBytes
    )

    $artifact = [ordered]@{
        id = "7001"
        name = [string]$Scenario.ArtifactName
        expired = $false
        digest = if ($Scenario.ApiDigestForm -eq "bare") { $TransportSha256 } else { "sha256:$TransportSha256" }
        size_in_bytes = $TransportBytes
        workflow_run = [ordered]@{ id = "5001"; head_sha = "0123456789abcdef0123456789abcdef01234567" }
    }
    $run = [ordered]@{
        id = "5001"
        run_attempt = "2"
        head_sha = "0123456789abcdef0123456789abcdef01234567"
        event = "push"
        repository = [ordered]@{ id = "12345"; full_name = "masterlifting/mcp-store" }
    }
    $job = [ordered]@{
        id = "9001"
        run_id = "5001"
        head_sha = "0123456789abcdef0123456789abcdef01234567"
        name = [string]$Scenario.UploaderJobName
        status = "completed"
        conclusion = "success"
    }
    return [ordered]@{
        artifact = $artifact
        run = $run
        jobsByPage = [ordered]@{
            "1" = [ordered]@{ total_count = 1; jobs = @($job) }
        }
    }
}

function New-ScenarioSource {
    param(
        [Parameter(Mandatory = $true)][string]$FixtureJsonBase64,
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$TracePath,
        [Parameter(Mandatory = $true)][object]$Scenario,
        [Parameter(Mandatory = $true)][object]$Paths
    )

    $source = @'
$fixtureJson = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String(%%FIXTURE%%))
$archiveSource = %%ARCHIVE%%
$tracePath = %%TRACE%%
$fixture = $fixtureJson | ConvertFrom-Json -AsHashtable -Depth 16
$metadataJson = {
        param([string]$Uri, [string]$Token)
        if ($Token -cne 'bootstrap-test-dummy') { throw 'metadata stub received an unexpected token value' }
        [System.IO.File]::AppendAllText($tracePath, $Uri + [System.Environment]::NewLine)
        if ($Uri.Contains('/actions/artifacts/')) {
            return ($fixture.artifact | ConvertTo-Json -Depth 16 -Compress)
        }
        if ($Uri.Contains('/attempts/2/jobs')) {
            $pageMatch = [System.Text.RegularExpressions.Regex]::Match($Uri, '(?:[?&])page=([0-9]+)')
            $page = if ($pageMatch.Success) { $pageMatch.Groups[1].Value } else { '1' }
            return ($fixture.jobsByPage[$page] | ConvertTo-Json -Depth 16 -Compress)
        }
        if ($Uri.Contains('/attempts/2')) {
            return ($fixture.run | ConvertTo-Json -Depth 16 -Compress)
        }
        throw ('metadata stub received an unknown endpoint: ' + $Uri)
}.GetNewClosure()
$downloadRaw = {
        param([string]$DownloadUri, [string]$Token, [string]$DestinationPath, [string[]]$AllowedHosts, [long]$MaxBytes)
        if ($Token -cne 'bootstrap-test-dummy') { throw 'download stub received an unexpected token value' }
        [System.IO.File]::Copy($archiveSource, $DestinationPath)
        $bytes = [System.IO.File]::ReadAllBytes($DestinationPath)
        $sha = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $reportedSha = %%REPORTED_SHA%%
        $reportedBytes = %%REPORTED_BYTES%%
        $reportedHost = %%REDIRECT_HOST%%
        if (%%OMIT_DOWNLOAD%%) { [System.IO.File]::Delete($DestinationPath) }
        return [pscustomobject]@{ Sha256 = $reportedSha; Bytes = $reportedBytes; RedirectHost = $reportedHost }
}.GetNewClosure()
$transport = @{ MetadataJson = $metadataJson; DownloadRaw = $downloadRaw }
'@
    $source = $source.Replace("%%FIXTURE%%", (ConvertTo-PSLiteral $FixtureJsonBase64))
    $source = $source.Replace("%%ARCHIVE%%", (ConvertTo-PSLiteral $ArchivePath))
    $source = $source.Replace("%%TRACE%%", (ConvertTo-PSLiteral $TracePath))
    $source = $source.Replace("%%REPORTED_SHA%%", (ConvertTo-PSLiteral $Paths.ReportedSha256))
    $source = $source.Replace("%%REPORTED_BYTES%%", [string]$Paths.ReportedBytes)
    $source = $source.Replace("%%REDIRECT_HOST%%", (ConvertTo-PSLiteral $Paths.RedirectHost))
    $omitToken = if ([bool]$Paths.OmitDownload) { '$true' } else { '$false' }
    $source = $source.Replace("%%OMIT_DOWNLOAD%%", $omitToken)

    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.Add("& " + (ConvertTo-PSLiteral $bootstrapPath))
    $arguments.Add("-Mode " + (ConvertTo-PSLiteral $Scenario.Mode))
    $arguments.Add("-Repository 'masterlifting/mcp-store' -RepositoryId '12345' -RunId '5001' -RunAttempt '2'")
    $arguments.Add("-EventSha '0123456789abcdef0123456789abcdef01234567'")
    $arguments.Add("-Role " + (ConvertTo-PSLiteral $Scenario.Role))
    $arguments.Add("-ArtifactName " + (ConvertTo-PSLiteral $Scenario.ArtifactName))
    $arguments.Add("-UploaderJobId '9001' -RoleMapJson " + (ConvertTo-PSLiteral $Scenario.RoleMapJson))
    $arguments.Add("-ArtifactId '7001' -PlatformDigest " + (ConvertTo-PSLiteral $Paths.PlatformDigest))
    $arguments.Add("-ExpectedOuterEntries @(" + (($Scenario.ExpectedNames | ForEach-Object { ConvertTo-PSLiteral ([string]$_) }) -join ",") + ")")
    $arguments.Add("-InstallRoot " + (ConvertTo-PSLiteral $Paths.InstallRoot))
    $arguments.Add("-ReceiptPath " + (ConvertTo-PSLiteral $Paths.ReceiptPath))
    $arguments.Add("-CheckoutRoot " + (ConvertTo-PSLiteral $Paths.CheckoutRoot))
    $arguments.Add("-ApiBase " + (ConvertTo-PSLiteral $Scenario.ApiBase))
    $arguments.Add("-AllowedRedirectHosts @('*.blob.core.windows.net')")
    $arguments.Add("-ExpectedEvent 'push'")
    if ($Scenario.Mode -eq "InstallRuntime") {
        $arguments.Add("-EnvelopeSha256 " + (ConvertTo-PSLiteral $Paths.EnvelopeSha256))
        $arguments.Add("-ManifestSha256 " + (ConvertTo-PSLiteral $Paths.ManifestSha256))
        $arguments.Add("-ExpectedEnvelopeName 'runtime-envelope.zip' -ExpectedManifestName 'runtime-manifest.json'")
        # The trusted receipt is the third outer member: its name is the canonical
        # "receipt.json" member emitted by the production builder, its SHA-256 is the
        # independent expectation the bootstrap authenticates the uploaded bytes against,
        # and its destination is a fresh wrapper-created leaf outside checkout, install,
        # candidate, the bootstrap receipt, and the raw transport leaf.
        $arguments.Add("-TrustedReceiptName 'receipt.json'")
        $arguments.Add("-TrustedReceiptSha256 " + (ConvertTo-PSLiteral $Paths.TrustedReceiptSha256))
        $arguments.Add("-TrustedReceiptPath " + (ConvertTo-PSLiteral $Paths.TrustedReceiptPath))
    }
    if ($Scenario.Mode -eq "FetchCandidate") {
        # Every FetchCandidate scenario supplies an explicit -RawTransportPath so the
        # bootstrap retains the verified streamed outer ZIP after every platform check
        # passes. The leaf is fresh (never created by this wrapper), the parent
        # ($caseDirectory) is created by the wrapper before the child is invoked, and
        # the path sits outside checkout, install, candidate, and receipt so the
        # destination guard accepts it on success and rejects it on misuse.
        $arguments.Add("-RawTransportPath " + (ConvertTo-PSLiteral $Paths.RawTransportPath))
        if (-not $Scenario.OmitOptionalCandidateRoot) {
            # -CandidateRoot is supplied alongside -RawTransportPath because the
            # production Assert-RawTransportDestination builds `@($InstallRoot,
            # $ReceiptPath, $CheckoutRoot, $CandidateRoot)` and PowerShell 7 cannot
            # bind an empty-string element to a Mandatory `[string[]]` parameter.
            # The wrapper-emitted path is the dedicated candidate-boundary
            # directory under caseDirectory; it never overlaps checkout, install,
            # receipt, or the raw transport leaf.
            $arguments.Add("-CandidateRoot " + (ConvertTo-PSLiteral $Paths.CandidateRoot))
        }
    }
    if (-not [string]::IsNullOrEmpty([string]$Scenario.Component)) {
        $arguments.Add("-Component " + (ConvertTo-PSLiteral $Scenario.Component))
    }
    if (-not [string]::IsNullOrEmpty([string]$Scenario.CandidateRoot) -and -not $Scenario.OmitOptionalCandidateRoot) {
        $arguments.Add("-CandidateRoot " + (ConvertTo-PSLiteral $Scenario.CandidateRoot))
    }
    if ($Scenario.MaxEntryCount -gt 0) { $arguments.Add("-MaxEntryCount $($Scenario.MaxEntryCount)") }
    if ($Scenario.MaxTotalUncompressedBytes -gt 0) { $arguments.Add("-MaxTotalUncompressedBytes $($Scenario.MaxTotalUncompressedBytes)") }
    if ($Scenario.MaxEntryUncompressedBytes -gt 0) { $arguments.Add("-MaxEntryUncompressedBytes $($Scenario.MaxEntryUncompressedBytes)") }
    if ($Scenario.MaxCompressionRatio -gt 0) { $arguments.Add("-MaxCompressionRatio $($Scenario.MaxCompressionRatio)") }
    if ($Scenario.MaxTransportBytes -gt 0) { $arguments.Add("-MaxTransportBytes $($Scenario.MaxTransportBytes)") }
    if ($Scenario.IncludeTestTransport) { $arguments.Add("-TestTransport `$transport") }
    $source += "`n" + ($arguments -join " ") + "; exit `$LASTEXITCODE`n"
    return $source
}

function Invoke-ChildBootstrap {
    param([Parameter(Mandatory = $true)][string]$ScriptPath, [Parameter(Mandatory = $true)][string]$TempPath)

    $powershellPath = Join-Path $PSHOME "pwsh.exe"
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $powershellPath
    $startInfo.ArgumentList.Add("-NoLogo")
    $startInfo.ArgumentList.Add("-NoProfile")
    $startInfo.ArgumentList.Add("-NonInteractive")
    $startInfo.ArgumentList.Add("-File")
    $startInfo.ArgumentList.Add($ScriptPath)
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment.Clear()
    foreach ($name in @("SystemRoot", "WINDIR")) {
        $value = [System.Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrEmpty($value)) { $startInfo.Environment[$name] = $value }
    }
    $startInfo.Environment["TEMP"] = $TempPath
    $startInfo.Environment["TMP"] = $TempPath
    $startInfo.Environment["GITHUB_TOKEN"] = "bootstrap-test-dummy"

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "The isolated PowerShell child process could not start." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(60000)) {
        $process.Kill($true)
        $process.WaitForExit()
        return [pscustomobject]@{ ExitCode = -999; Stdout = $stdoutTask.GetAwaiter().GetResult(); Stderr = $stderrTask.GetAwaiter().GetResult() + "`nChild process exceeded 60 seconds and was terminated." }
    }
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Stdout = $stdoutTask.GetAwaiter().GetResult()
        Stderr = $stderrTask.GetAwaiter().GetResult()
    }
}

function New-Scenario {
    param([string]$Name, [string]$Mode = "InstallRuntime", [string]$Role = "publisher", [string]$ArtifactName = "release-publisher", [string]$UploaderJobName = "build-publisher")
    return @{
        Name = $Name
        Mode = $Mode
        Role = $Role
        ArtifactName = $ArtifactName
        UploaderJobName = $UploaderJobName
        Component = ""
        RoleMapJson = '{"publisher":{"artifactName":"release-publisher","uploaderJobName":"build-publisher","component":null},"runtime":{"artifactName":"release-runtime","uploaderJobName":"build-runtime","component":null},"candidate":{"artifactName":"release-candidate-dotnet","uploaderJobName":"prepare-dotnet","component":"dotnet"}}'
        ApiBase = "https://api.test.invalid"
        ApiDigestForm = "prefix"
        PlatformDigestForm = "prefix"
        IncludeTestTransport = $true
        ExpectedExitCode = 0
        ExpectedStatus = if ($Mode -eq "InstallRuntime") { "installed" } else { "fetched" }
        ExpectedFailureCode = ""
        ExpectedNames = if ($Mode -eq "InstallRuntime") { @("runtime-envelope.zip", "runtime-manifest.json", "receipt.json") } else { @("candidate.zip", "distribution.json", "receipt.json") }
        ArchiveEntries = $null
        Mutation = $null
        CandidateRoot = ""
        MaxEntryCount = 4096
        MaxTotalUncompressedBytes = 268435456L
        MaxEntryUncompressedBytes = 268435456L
        MaxCompressionRatio = 200L
        MaxTransportBytes = 268435456L
        ReportedSha256 = ""
        ReportedBytes = -1L
        RedirectHost = "artifact.blob.core.windows.net"
        OmitDownload = $false
        ExistingInstall = $false
        OverlapCheckout = $false
        ReceiptInsideCandidate = $false
        ExpectReceiptAbsent = $false
        PaginateJobs = $false
        ExpectInstallRootAbsent = $true
        ExpectCandidateRootAbsent = $false
        # When $true, New-ScenarioSource suppresses -CandidateRoot entirely from the
        # generated child argv (instead of emitting -CandidateRoot '') and the wrapper
        # post-write-checks the child source does not carry the flag, so a genuine
        # FetchCandidate caller who never supplies the optional root still works.
        OmitOptionalCandidateRoot = $false
        # When $true, Invoke-Scenario writes a known fixture at the raw transport leaf
        # before the child runs and then asserts the bytes are unchanged after the
        # rejection; the bootstrap's existing pre-existing-file guard must reject the
        # run without deleting or rewriting the wrapper-owned file.
        PreserveExistingRawFile = $false
        # When non-empty, the wrapper substitutes this SHA-256 for the fixture's authentic
        # trusted-receipt SHA-256 in the generated -TrustedReceiptSha256, so the bootstrap's
        # trusted-receipt-digest-mismatch guard fires and the install root is not created.
        TrustedReceiptSha256Override = ""
        # When $true, Invoke-Scenario writes a known wrapper-owned file at the trusted
        # receipt leaf before the child runs and then asserts the bytes are unchanged after
        # the rejection; the bootstrap's existing pre-existing-file guard must reject the
        # run without deleting or rewriting the wrapper-owned user file.
        PreserveExistingTrustedReceipt = $false
    }
}

function Invoke-Scenario {
    param([Parameter(Mandatory = $true)][hashtable]$Scenario)

    $caseDirectory = Join-Path $suiteRoot ("case-" + $Scenario.Name)
    [System.IO.Directory]::CreateDirectory($caseDirectory) | Out-Null
    $checkoutRoot = Join-Path $caseDirectory "checkout"
    $candidateRoot = Join-Path $caseDirectory "candidate-boundary"
    $installRoot = Join-Path $caseDirectory "install"
    $receiptPath = Join-Path $caseDirectory "bootstrap-receipt.json"
    $tracePath = Join-Path $caseDirectory "metadata-uris.log"
    # The retained raw transport leaf for FetchCandidate scenarios sits directly
    # under the wrapper-created caseDirectory: the parent is created by this
    # wrapper before the child is invoked, the leaf is never created by this
    # wrapper (it is emitted only by the bootstrap's atomic retention), and the
    # path is outside checkout, install, candidate, and receipt so the
    # destination guard accepts it on success and never overlaps a protected
    # root on misuse.
    $rawTransportPath = Join-Path $caseDirectory "raw-transport.zip"
    # The sealed trusted receipt leaf for InstallRuntime scenarios sits directly
    # under the wrapper-created caseDirectory: the parent exists before the
    # child is invoked, the leaf is never created by this wrapper on success
    # (it is emitted only by the bootstrap's atomic copy), and the path is
    # outside checkout, install, candidate, the bootstrap receipt, and the raw
    # transport leaf so the destination guard accepts it on success and never
    # overlaps a protected root on misuse.
    $trustedReceiptPath = Join-Path $caseDirectory "trusted-receipt.json"
    if ($Scenario.OverlapCheckout) { $checkoutRoot = $caseDirectory }
    if (-not [string]::IsNullOrEmpty($Scenario.CandidateRoot)) { $candidateRoot = $Scenario.CandidateRoot }
    if ($Scenario.ReceiptInsideCandidate) {
        $Scenario.CandidateRoot = $candidateRoot
        $receiptPath = Join-Path $candidateRoot "bootstrap-receipt.json"
    }

    if ($Scenario.ExistingInstall) {
        [System.IO.Directory]::CreateDirectory($installRoot) | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $installRoot "manual.txt"), "manual contents")
    }

    if ($Scenario.Mode -eq "InstallRuntime") {
        $package = New-RuntimeTransport -Directory $caseDirectory
    } else {
        $package = New-CandidateTransport -Directory $caseDirectory -Scenario $Scenario
    }
    if ($Scenario.Mode -eq "FetchCandidate" -and $Scenario.PreserveExistingRawFile) {
        # Place a known wrapper-owned fixture at the raw transport leaf before the
        # child runs so the bootstrap's pre-existing-file guard rejects a file the
        # suite did not create in this run; the wrapper asserts those exact bytes
        # remain untouched after the rejection.
        $preexistingBytes = [System.Text.Encoding]::UTF8.GetBytes("preexisting raw transport fixture bytes that must remain untouched")
        [System.IO.File]::WriteAllBytes($rawTransportPath, $preexistingBytes)
    }
    if ($Scenario.Mode -eq "InstallRuntime" -and $Scenario.PreserveExistingTrustedReceipt) {
        # Place a known wrapper-owned file at the trusted receipt leaf before the
        # child runs so the bootstrap's pre-existing-file guard rejects a file the
        # suite did not create in this run; the wrapper asserts those exact bytes
        # remain untouched after the rejection.
        $preexistingReceiptBytes = [System.Text.Encoding]::UTF8.GetBytes("preexisting trusted receipt fixture bytes that must remain untouched")
        [System.IO.File]::WriteAllBytes($trustedReceiptPath, $preexistingReceiptBytes)
    }
    $transportBytes = [System.IO.File]::ReadAllBytes($package.Path)
    $transportSha256 = Get-Sha256Hex $transportBytes
    $fixture = New-PlatformFixture -Scenario $Scenario -TransportSha256 $transportSha256 -TransportBytes $transportBytes.LongLength
    if ($Scenario.ApiDigestForm -eq "missing") { $fixture.artifact.Remove("digest") | Out-Null }
    if ($Scenario.ApiDigestForm -eq "malformed") { $fixture.artifact.digest = "sha512:$transportSha256" }
    if ($Scenario.PaginateJobs) {
        $pageOne = [System.Collections.Generic.List[object]]::new()
        for ($index = 0; $index -lt 100; $index++) {
            $pageOne.Add([ordered]@{ id = "job-$index"; run_id = "5001"; head_sha = "0123456789abcdef0123456789abcdef01234567"; name = "other-job-$index"; status = "completed"; conclusion = "success" })
        }
        $fixture.jobsByPage["1"] = [ordered]@{ total_count = 101; jobs = @($pageOne) }
        $fixture.jobsByPage["2"] = [ordered]@{ total_count = 101; jobs = @([ordered]@{ id = "9001"; run_id = "5001"; head_sha = "0123456789abcdef0123456789abcdef01234567"; name = $Scenario.UploaderJobName; status = "completed"; conclusion = "success" }) }
    }
    if ($null -ne $Scenario.Mutation) { & $Scenario.Mutation $fixture }

    $platformDigest = if ($Scenario.PlatformDigestForm -eq "bare") { $transportSha256 } else { "sha256:$transportSha256" }
    $reportedSha = if ([string]::IsNullOrEmpty($Scenario.ReportedSha256)) { $transportSha256 } else { $Scenario.ReportedSha256 }
    $reportedBytes = if ($Scenario.ReportedBytes -lt 0) { $transportBytes.LongLength } else { $Scenario.ReportedBytes }
    $paths = @{
        InstallRoot = $installRoot
        ReceiptPath = $receiptPath
        CheckoutRoot = $checkoutRoot
        CandidateRoot = $candidateRoot
        EnvelopeSha256 = if ($Scenario.Mode -eq "InstallRuntime") { $package.InnerSha256 } else { "" }
        ManifestSha256 = if ($Scenario.Mode -eq "InstallRuntime") { $package.ManifestSha256 } else { "" }
        # The trusted-receipt SHA is the fixture's authentic digest by default; a test
        # that wants the bootstrap's digest-mismatch guard to fire overrides it via
        # TrustedReceiptSha256Override. The path is the wrapper-created fresh leaf the
        # bootstrap copies the receipt into through its atomic temp + rename.
        TrustedReceiptSha256 = $(
            if ($Scenario.Mode -eq "InstallRuntime") {
                if (-not [string]::IsNullOrEmpty($Scenario.TrustedReceiptSha256Override)) { $Scenario.TrustedReceiptSha256Override } else { $package.TrustedReceiptSha256 }
            } else { "" }
        )
        TrustedReceiptPath = if ($Scenario.Mode -eq "InstallRuntime") { $trustedReceiptPath } else { "" }
        PlatformDigest = $platformDigest
        ReportedSha256 = $reportedSha
        ReportedBytes = $reportedBytes
        RedirectHost = $Scenario.RedirectHost
        OmitDownload = $Scenario.OmitDownload
        RawTransportPath = $rawTransportPath
    }
    $fixtureJson = $fixture | ConvertTo-Json -Depth 20 -Compress
    $fixtureBase64 = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($fixtureJson))
    $source = New-ScenarioSource -FixtureJsonBase64 $fixtureBase64 -ArchivePath $package.Path -TracePath $tracePath -Scenario $Scenario -Paths $paths
    [System.IO.File]::WriteAllText((Join-Path $caseDirectory "child-command.ps1"), $source)
    if ($Scenario.OmitOptionalCandidateRoot) {
        $generatedSource = [System.IO.File]::ReadAllText((Join-Path $caseDirectory "child-command.ps1"))
        if ($generatedSource -match '(?<![\w-])-CandidateRoot\b') {
            $problems.Add("OmitOptionalCandidateRoot did not suppress the -CandidateRoot argument in the generated child source")
        }
    }
    $result = Invoke-ChildBootstrap -ScriptPath (Join-Path $caseDirectory "child-command.ps1") -TempPath $caseDirectory

    $problems = [System.Collections.Generic.List[string]]::new()
    if ($result.ExitCode -ne $Scenario.ExpectedExitCode) { $problems.Add("expected exit $($Scenario.ExpectedExitCode), received $($result.ExitCode)") }

    $receipt = $null
    if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
        try { $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -AsHashtable -Depth 20 }
        catch { $problems.Add("bootstrap receipt is not valid JSON: $($_.Exception.Message)") }
    }
    if ($Scenario.ExpectedExitCode -eq 0) {
        if ($null -eq $receipt) { $problems.Add("successful execution did not write its receipt") }
        elseif ($receipt.status -cne $Scenario.ExpectedStatus) { $problems.Add("expected receipt status '$($Scenario.ExpectedStatus)', received '$($receipt.status)'") }
        elseif ($receipt.transport -cne "test-stub") { $problems.Add("successful stubbed execution did not mark transport=test-stub") }
    } elseif (-not [string]::IsNullOrEmpty($Scenario.ExpectedFailureCode)) {
        if ($Scenario.ExpectReceiptAbsent) {
            if (Test-Path -LiteralPath $receiptPath -PathType Leaf) { $problems.Add("failed execution unexpectedly wrote its bootstrap receipt") }
            $failureOutput = $result.Stderr + "`n" + $result.Stdout
            if (-not $failureOutput.Contains($Scenario.ExpectedFailureCode)) {
                $problems.Add("expected typed diagnostic '$($Scenario.ExpectedFailureCode)' in child output")
            }
        } else {
            if ($null -eq $receipt) { $problems.Add("failed execution did not write its bootstrap receipt") }
            elseif ($receipt.failures.Count -eq 0 -or $receipt.failures[0].code -cne $Scenario.ExpectedFailureCode) {
                $actual = if ($receipt.failures.Count -gt 0) { $receipt.failures[0].code } else { "<no failure code>" }
                $problems.Add("expected receipt failure '$($Scenario.ExpectedFailureCode)', received '$actual'")
            }
        }
    }

    if ($Scenario.ExistingInstall) {
        if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) { $problems.Add("pre-existing install directory was removed") }
        elseif ([System.IO.File]::ReadAllText((Join-Path $installRoot "manual.txt")) -cne "manual contents") { $problems.Add("pre-existing install contents were changed") }
    } elseif ($Scenario.ExpectedExitCode -ne 0 -and $Scenario.ExpectInstallRootAbsent -and (Test-Path -LiteralPath $installRoot)) {
        $problems.Add("failed install did not remove its fresh install root")
    } elseif ($Scenario.ExpectedExitCode -eq 0 -and -not (Test-Path -LiteralPath $installRoot -PathType Container)) {
        $problems.Add("successful execution did not leave its install root")
    }

    if ($Scenario.ExpectCandidateRootAbsent -and (Test-Path -LiteralPath $candidateRoot)) {
        $problems.Add("rejected candidate-boundary path was created")
    }

    if ($Scenario.ExpectedExitCode -eq 0 -and $Scenario.Mode -eq "InstallRuntime" -and $null -ne $receipt) {
        $runtimeRoot = if ($null -ne $receipt.runtime) { $receipt.runtime.installRoot } else { $null }
        if ([string]::IsNullOrEmpty([string]$runtimeRoot)) { $problems.Add("successful runtime receipt did not name the installed runtime root") }
        else {
            foreach ($fileCheck in @(
                @{ Path = "ReleaseRunner.exe"; Sha = $package.RunnerSha256; Bytes = $package.RunnerBytes },
                @{ Path = "runtime/support.dat"; Sha = $package.SupportSha256; Bytes = $package.SupportBytes }
            )) {
                $runtimeFile = Join-Path $runtimeRoot $fileCheck.Path
                if (-not (Test-Path -LiteralPath $runtimeFile -PathType Leaf)) { $problems.Add("installed runtime file '$($fileCheck.Path)' is missing"); continue }
                $bytes = [System.IO.File]::ReadAllBytes($runtimeFile)
                if ((Get-Sha256Hex $bytes) -cne $fileCheck.Sha) { $problems.Add("installed runtime file '$($fileCheck.Path)' has a different SHA-256") }
                if ($bytes.LongLength -ne $fileCheck.Bytes) { $problems.Add("installed runtime file '$($fileCheck.Path)' has a different exact size") }
            }
        }
        if ($receipt.artifact.sizeInBytes -ne $transportBytes.LongLength) { $problems.Add("receipt outer artifact size did not match exact transport bytes") }
        if ($null -eq $receipt.download) { $problems.Add("successful runtime receipt omitted download observations") }
        elseif ($receipt.download.observedSha256 -cne $transportSha256) { $problems.Add("receipt outer digest did not match exact transport bytes") }
        if ($null -eq $receipt.envelope) { $problems.Add("successful runtime receipt omitted inner envelope observations") }
        elseif ($receipt.envelope.observedSha256 -cne $package.InnerSha256) { $problems.Add("receipt inner envelope digest did not match exact envelope bytes") }
        if ($null -ne $receipt.download -and $null -ne $receipt.envelope -and $receipt.download.observedSha256 -ceq $receipt.envelope.observedSha256) { $problems.Add("outer transport and inner envelope digest namespaces were not distinct") }
        # Receipt copy proof: the sealed trusted receipt is copied last into a fresh
        # wrapper-created leaf through a same-directory temp + same-volume rename, so
        # the file at the destination is the exact verified outer member bytes (not
        # the inner envelope, not the manifest, and never a pre-existing user file).
        if ($null -eq $receipt.envelope) { $problems.Add("successful runtime receipt omitted inner envelope observations needed for the receipt copy proof") }
        elseif ($null -eq $receipt.envelope.trustedReceiptObservedSha256) { $problems.Add("successful runtime receipt did not record the observed trusted-receipt SHA-256") }
        elseif ($receipt.envelope.trustedReceiptObservedSha256 -cne $package.TrustedReceiptSha256) { $problems.Add("receipt observed trusted-receipt SHA-256 did not match the fixture's authentic receipt bytes") }
        if (-not (Test-Path -LiteralPath $trustedReceiptPath -PathType Leaf)) {
            $problems.Add("InstallRuntime success did not seal the trusted receipt at '$trustedReceiptPath'")
        } else {
            $sealedBytes = [System.IO.File]::ReadAllBytes($trustedReceiptPath)
            $sealedSha = Get-Sha256Hex $sealedBytes
            $sealedLength = $sealedBytes.LongLength
            if ($sealedSha -cne $package.TrustedReceiptSha256) {
                $problems.Add("sealed trusted receipt SHA-256 '$sealedSha' did not equal the fixture's authentic receipt SHA-256 '$($package.TrustedReceiptSha256)'")
            }
            if ($sealedLength -ne $package.TrustedReceiptBytes) {
                $problems.Add("sealed trusted receipt length $sealedLength did not equal the fixture's authentic receipt length $($package.TrustedReceiptBytes)")
            }
            if ($sealedLength -eq $package.TrustedReceiptBytes) {
                $byteArrayEqual = $true
                $fixtureReceiptBytes = $package.TrustedReceiptBytesContent
                for ($index = 0; $index -lt $sealedLength; $index++) {
                    if ($sealedBytes[$index] -ne $fixtureReceiptBytes[$index]) { $byteArrayEqual = $false; break }
                }
                if (-not $byteArrayEqual) { $problems.Add("sealed trusted receipt bytes did not match the fixture receipt bytes element-wise") }
            }
            if ($null -ne $receipt -and $null -ne $receipt.envelope -and $sealedSha -cne $receipt.envelope.trustedReceiptObservedSha256) {
                $problems.Add("sealed trusted receipt SHA-256 did not match the receipt's envelope.trustedReceiptObservedSha256")
            }
        }
    }
    if ($Scenario.ExpectedExitCode -eq 0 -and $Scenario.Mode -eq "FetchCandidate") {
        $candidatePayload = Join-Path $installRoot "candidate"
        foreach ($name in $Scenario.ExpectedNames) {
            if (-not (Test-Path -LiteralPath (Join-Path $candidatePayload $name) -PathType Leaf)) { $problems.Add("fetched candidate entry '$name' is missing") }
        }
        # Retention proof: the streamed outer ZIP is atomically copied into the
        # fresh wrapper-created leaf after every platform, transport and outer
        # archive check passes, so the retained file is the exact verified
        # bytes the platform served (and never the inner envelope, an
        # extracted candidate entry, or the install-root download temp).
        if (-not (Test-Path -LiteralPath $rawTransportPath -PathType Leaf)) {
            $problems.Add("FetchCandidate success did not retain the streamed raw transport at '$rawTransportPath'")
        } else {
            $retainedBytes = [System.IO.File]::ReadAllBytes($rawTransportPath)
            $retainedSha = Get-Sha256Hex $retainedBytes
            $retainedLength = $retainedBytes.LongLength
            if ($retainedSha -cne $transportSha256) {
                $problems.Add("retained raw transport SHA-256 '$retainedSha' did not equal the platform outer transport SHA-256 '$transportSha256'")
            }
            if ($retainedLength -ne $transportBytes.LongLength) {
                $problems.Add("retained raw transport length $retainedLength did not equal the outer transport length $($transportBytes.LongLength)")
            }
            if ($retainedLength -eq $transportBytes.LongLength) {
                $byteArrayEqual = $true
                for ($index = 0; $index -lt $retainedLength; $index++) {
                    if ($retainedBytes[$index] -ne $transportBytes[$index]) { $byteArrayEqual = $false; break }
                }
                if (-not $byteArrayEqual) { $problems.Add("retained raw transport bytes did not match the original fixture transport bytes element-wise") }
            }
            if ($null -ne $receipt -and $null -ne $receipt.download -and $retainedSha -cne $receipt.download.observedSha256) {
                $problems.Add("retained raw transport SHA-256 did not match the receipt's download.observedSha256")
            }
            if ($null -ne $receipt -and $null -ne $receipt.artifact -and $retainedLength -ne $receipt.artifact.sizeInBytes) {
                $problems.Add("retained raw transport length did not match the receipt's artifact.sizeInBytes")
            }
        }
    } elseif ($Scenario.Mode -eq "FetchCandidate" -and $Scenario.ExpectedExitCode -ne 0) {
        if ($Scenario.PreserveExistingRawFile) {
            # The wrapper-owned pre-existing file is the input under test, not the
            # bootstrap's creation; the rejection must leave the bytes untouched and
            # never delete or rewrite the file. The generic "no raw transport on
            # failure" check is intentionally skipped here because the file existed
            # before the bootstrap ran; it is replaced by an explicit byte-level
            # preservation assertion instead of a blanket absence assertion.
            $expectedBytes = [System.Text.Encoding]::UTF8.GetBytes("preexisting raw transport fixture bytes that must remain untouched")
            $expectedSha = Get-Sha256Hex $expectedBytes
            $expectedLength = $expectedBytes.LongLength
            if (-not (Test-Path -LiteralPath $rawTransportPath -PathType Leaf)) {
                $problems.Add("FetchCandidate failure deleted the wrapper-owned pre-existing raw transport at '$rawTransportPath'")
            } else {
                $observedBytes = [System.IO.File]::ReadAllBytes($rawTransportPath)
                $observedSha = Get-Sha256Hex $observedBytes
                $observedLength = $observedBytes.LongLength
                if ($observedSha -cne $expectedSha) {
                    $problems.Add("FetchCandidate failure rewrote the pre-existing raw transport; expected SHA '$expectedSha', observed '$observedSha'")
                }
                if ($observedLength -ne $expectedLength) {
                    $problems.Add("FetchCandidate failure changed the pre-existing raw transport length; expected $expectedLength, observed $observedLength")
                }
                if ($observedLength -eq $expectedLength) {
                    $byteArrayEqual = $true
                    for ($index = 0; $index -lt $observedLength; $index++) {
                        if ($observedBytes[$index] -ne $expectedBytes[$index]) { $byteArrayEqual = $false; break }
                    }
                    if (-not $byteArrayEqual) {
                        $problems.Add("FetchCandidate failure modified the pre-existing raw transport bytes element-wise")
                    }
                }
            }
        } else {
            # Every existing FetchCandidate failure (the role-component mismatch and
            # the archive-* set) is detected before the bootstrap reaches its
            # atomic retention copy, so the catch's `$createdRawTransport = false`
            # branch leaves no retained raw transport file behind at the wrapper
            # leaf. A later failure that fires post-retention is exercised by the
            # success path's byte equality check above.
            if (Test-Path -LiteralPath $rawTransportPath -PathType Leaf) {
                $problems.Add("FetchCandidate failure left an unexpected raw transport file at '$rawTransportPath'; retention must not run when a check rejects the run")
            }
        }
    } elseif ($Scenario.Mode -eq "InstallRuntime" -and $Scenario.ExpectedExitCode -ne 0) {
        # InstallRuntime negative coverage: a rejected run must never seal the
        # trusted receipt into the wrapper-created leaf (the catch's
        # `$createdTrustedReceipt = false` branch guards that invariant), and a
        # wrapper-owned pre-existing file at the destination must survive the
        # rejection exactly as the raw-transport pre-existing-file guard does.
        # InstallRuntime never receives -RawTransportPath, so no file at the
        # raw transport wrapper leaf may be produced either.
        if (Test-Path -LiteralPath $rawTransportPath -PathType Leaf) {
            $problems.Add("InstallRuntime unexpectedly produced a raw transport file at '$rawTransportPath'")
        }
        if ($Scenario.PreserveExistingTrustedReceipt) {
            $expectedReceiptBytes = [System.Text.Encoding]::UTF8.GetBytes("preexisting trusted receipt fixture bytes that must remain untouched")
            $expectedReceiptSha = Get-Sha256Hex $expectedReceiptBytes
            $expectedReceiptLength = $expectedReceiptBytes.LongLength
            if (-not (Test-Path -LiteralPath $trustedReceiptPath -PathType Leaf)) {
                $problems.Add("InstallRuntime failure deleted the wrapper-owned pre-existing trusted receipt at '$trustedReceiptPath'")
            } else {
                $observedReceiptBytes = [System.IO.File]::ReadAllBytes($trustedReceiptPath)
                $observedReceiptSha = Get-Sha256Hex $observedReceiptBytes
                $observedReceiptLength = $observedReceiptBytes.LongLength
                if ($observedReceiptSha -cne $expectedReceiptSha) {
                    $problems.Add("InstallRuntime failure rewrote the pre-existing trusted receipt; expected SHA '$expectedReceiptSha', observed '$observedReceiptSha'")
                }
                if ($observedReceiptLength -ne $expectedReceiptLength) {
                    $problems.Add("InstallRuntime failure changed the pre-existing trusted receipt length; expected $expectedReceiptLength, observed $observedReceiptLength")
                }
                if ($observedReceiptLength -eq $expectedReceiptLength) {
                    $byteArrayEqual = $true
                    for ($index = 0; $index -lt $observedReceiptLength; $index++) {
                        if ($observedReceiptBytes[$index] -ne $expectedReceiptBytes[$index]) { $byteArrayEqual = $false; break }
                    }
                    if (-not $byteArrayEqual) {
                        $problems.Add("InstallRuntime failure modified the pre-existing trusted receipt bytes element-wise")
                    }
                }
            }
        } else {
            # Every InstallRuntime failure (existing-install, source-overlap, the
            # new digest-mismatch, and the new destination-overlap) is detected
            # before the bootstrap reaches its atomic receipt copy, so the
            # catch's `$createdTrustedReceipt = false` branch leaves no sealed
            # receipt behind at the wrapper leaf.
            if (Test-Path -LiteralPath $trustedReceiptPath -PathType Leaf) {
                $problems.Add("InstallRuntime failure left an unexpected trusted receipt file at '$trustedReceiptPath'; the copy must not run when a check rejects the run")
            }
        }
    } elseif ($Scenario.Mode -eq "InstallRuntime" -and $Scenario.ExpectedExitCode -eq 0) {
        # InstallRuntime success: -RawTransportPath is rejected by the production
        # bootstrap in this mode, so no file at the wrapper leaf may be produced.
        if (Test-Path -LiteralPath $rawTransportPath -PathType Leaf) {
            $problems.Add("InstallRuntime unexpectedly produced a raw transport file at '$rawTransportPath'")
        }
        # The trusted receipt copy proof is exercised by the positive InstallRuntime
        # block above (the file at $trustedReceiptPath must be byte-equal to the
        # fixture receipt); the same block already fails on its absence.
    }
    if ($Scenario.ExpectedExitCode -eq 0 -and $Scenario.PaginateJobs) {
        if (-not (Test-Path -LiteralPath $tracePath -PathType Leaf)) { $problems.Add("metadata request trace was not written") }
        else {
            $uriLog = [System.IO.File]::ReadAllText($tracePath)
            if (-not $uriLog.Contains("/actions/runs/5001/attempts/2/jobs?per_page=100&page=2")) { $problems.Add("attempt-scoped job page 2 was not requested") }
        }
    }

    if ($problems.Count -eq 0) {
        $script:PassedScenarioCount++
        return
    }

    $inputEvidence = [ordered]@{
        Mode = $Scenario.Mode
        Repository = "masterlifting/mcp-store"
        RepositoryId = "12345"
        RunId = "5001"
        RunAttempt = "2"
        EventSha = "0123456789abcdef0123456789abcdef01234567"
        Role = $Scenario.Role
        Component = $Scenario.Component
        ArtifactName = $Scenario.ArtifactName
        UploaderJobId = "9001"
        PlatformDigest = $platformDigest
        ExpectedOuterEntries = @($Scenario.ExpectedNames)
        ApiBase = $Scenario.ApiBase
        InstallRoot = $installRoot
        ReceiptPath = $receiptPath
        CheckoutRoot = $checkoutRoot
        CandidateRoot = $candidateRoot
        TestTransport = if ($Scenario.IncludeTestTransport) { "MetadataJson + DownloadRaw script blocks; dummy token supplied via child environment" } else { "absent" }
        MetadataFixture = $fixture
    }
    $evidence = [ordered]@{
        case = $Scenario.Name
        problems = @($problems)
        originalInputs = $inputEvidence
        exitCode = $result.ExitCode
        stdout = $result.Stdout
        stderr = $result.Stderr
        receipt = $receipt
        expectedExitCode = $Scenario.ExpectedExitCode
        expectedReceiptFailure = $Scenario.ExpectedFailureCode
    }
    $jsonEvidence = $evidence | ConvertTo-Json -Depth 24
    $script:FailedScenarios.Add($jsonEvidence)
    $evidencePath = Join-Path $caseDirectory "failure-evidence.json"
    [System.IO.File]::WriteAllText($evidencePath, $jsonEvidence)
    [Console]::Error.WriteLine("FAIL $($Scenario.Name): $($problems -join '; ')")
    [Console]::Error.WriteLine("Evidence: $evidencePath")
}

function New-FailureScenario {
    param([string]$Name, [string]$Code, [scriptblock]$Mutation, [string]$Mode = "InstallRuntime")
    $scenario = New-Scenario -Name $Name -Mode $Mode -Role $(if ($Mode -eq "FetchCandidate") { "candidate" } else { "publisher" }) -ArtifactName $(if ($Mode -eq "FetchCandidate") { "release-candidate-dotnet" } else { "release-publisher" }) -UploaderJobName $(if ($Mode -eq "FetchCandidate") { "prepare-dotnet" } else { "build-publisher" })
    $scenario.ExpectedExitCode = 1
    $scenario.ExpectedStatus = ""
    $scenario.ExpectedFailureCode = $Code
    $scenario.Mutation = $Mutation
    if ($Mode -eq "FetchCandidate") { $scenario.Component = "dotnet" }
    return $scenario
}

function Test-StaticContracts {
    $bootstrapText = [System.IO.File]::ReadAllText($bootstrapPath)
    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($bootstrapPath, [ref]$tokens, [ref]$parseErrors) | Out-Null
    Assert-Condition ($parseErrors.Count -eq 0) "production Bootstrap.ps1 has PowerShell parse errors"

    $metadataStart = $bootstrapText.IndexOf("function Get-MetadataDocument", [System.StringComparison]::Ordinal)
    $downloadStart = $bootstrapText.IndexOf("function Get-ArtifactRawArchive", [System.StringComparison]::Ordinal)
    $manifestStart = $bootstrapText.IndexOf("function Read-RuntimeManifest", [System.StringComparison]::Ordinal)
    Assert-Condition ($metadataStart -ge 0 -and $downloadStart -gt $metadataStart -and $manifestStart -gt $downloadStart) "production transport functions could not be delimited"
    $metadataSource = $bootstrapText.Substring($metadataStart, $downloadStart - $metadataStart)
    $downloadSource = $bootstrapText.Substring($downloadStart, $manifestStart - $downloadStart)
    Assert-Condition ($metadataSource.Contains("`$handler.AllowAutoRedirect = `$false") -and $metadataSource.Contains('metadata-redirect-rejected')) "metadata transport source no longer disables redirects and rejects 3xx"
    Assert-Condition ($metadataSource.Contains('TryAddWithoutValidation("Authorization", "Bearer $Token")')) "metadata request source no longer binds its bearer header to the metadata request"
    $secondRequest = $downloadSource.IndexOf("`$second = [System.Net.Http.HttpRequestMessage]", [System.StringComparison]::Ordinal)
    $signedRequest = $downloadSource.IndexOf("`$signed = `$client.SendAsync(`$second", [System.StringComparison]::Ordinal)
    Assert-Condition ($downloadSource.Contains("`$handler.AllowAutoRedirect = `$false") -and $secondRequest -ge 0 -and $signedRequest -gt $secondRequest) "signed-download source no longer uses explicit redirect handling"
    if ($secondRequest -ge 0 -and $signedRequest -gt $secondRequest) {
        $signedRequestSource = $downloadSource.Substring($secondRequest, $signedRequest - $secondRequest)
        Assert-Condition (-not $signedRequestSource.Contains('TryAddWithoutValidation("Authorization"')) "signed URL request source forwards an Authorization header"
    }

    $sources = Get-Content -LiteralPath $sourcesPath -Raw | ConvertFrom-Json -Depth 20
    [xml]$project = Get-Content -LiteralPath $releaseProjectPath -Raw
    $compilePaths = [System.Collections.Generic.List[string]]::new()
    foreach ($compile in $project.Project.ItemGroup.Compile) {
        $absolute = [System.IO.Path]::GetFullPath((Join-Path (Split-Path $releaseProjectPath -Parent) ([string]$compile.Include)))
        $relative = [System.IO.Path]::GetRelativePath($repositoryRoot, $absolute).Replace("\", "/")
        $compilePaths.Add($relative)
    }
    $sourcePaths = @($sources.paths | Select-Object -Skip 1)
    Assert-Condition (@($sourcePaths).Count -eq $compilePaths.Count) "Sources.json source-path count differs from the project compile count"
    Assert-Condition ((@($sourcePaths) -join "`n") -ceq ($compilePaths.ToArray() -join "`n")) "Sources.json source paths do not match Release.fsproj compile order"
    $importPaths = @($sources.imports | ForEach-Object { [string]$_.to })
    Assert-Condition ((@($importPaths) -join "`n") -ceq ($compilePaths.ToArray() -join "`n")) "Sources.json imports do not match Release.fsproj compile order"
    Assert-Condition ($sources.paths[0] -ceq "infrastructure/release/Release.fsproj" -and $sources.schemaVersion -eq 1) "Sources.json project/schema identity differs from the expected closure contract"
    foreach ($path in $sources.paths) {
        $absolute = Join-Path $repositoryRoot ([string]$path)
        Assert-Condition (Test-Path -LiteralPath $absolute -PathType Leaf) "Sources.json closure path is missing: $path"
    }
    Assert-Condition (-not ($sources.paths -contains "infrastructure/release/Bootstrap.ps1")) "transport bootstrap is incorrectly in the publisher compile closure"
}

try {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Test-StaticContracts

    $scenarios = [System.Collections.Generic.List[hashtable]]::new()
    $scenarios.Add((New-Scenario -Name "runtime-install-two-level-envelope"))
    $candidateSuccess = New-Scenario -Name "candidate-fetch-exact-entries" -Mode "FetchCandidate" -Role "candidate" -ArtifactName "release-candidate-dotnet" -UploaderJobName "prepare-dotnet"
    $candidateSuccess.Component = "dotnet"
    $scenarios.Add($candidateSuccess)

    foreach ($forms in @(
        @{ Name = "digest-prefix-prefix"; Api = "prefix"; Platform = "prefix" },
        @{ Name = "digest-prefix-bare"; Api = "prefix"; Platform = "bare" },
        @{ Name = "digest-bare-prefix"; Api = "bare"; Platform = "prefix" },
        @{ Name = "digest-bare-bare"; Api = "bare"; Platform = "bare" }
    )) {
        $scenario = New-Scenario -Name $forms.Name
        $scenario.ApiDigestForm = $forms.Api
        $scenario.PlatformDigestForm = $forms.Platform
        $scenarios.Add($scenario)
    }

    $scenarios.Add((New-FailureScenario -Name "wrong-repository-id" -Code "run-repository-mismatch" -Mutation { param($fixture) $fixture.run.repository.id = "99999" }))
    $scenarios.Add((New-FailureScenario -Name "wrong-repository-fullname" -Code "run-repository-name-mismatch" -Mutation { param($fixture) $fixture.run.repository.full_name = "attacker/mcp-store" }))
    $scenarios.Add((New-FailureScenario -Name "wrong-run-event-sha" -Code "run-event-sha-mismatch" -Mutation { param($fixture) $fixture.run.head_sha = "ffffffffffffffffffffffffffffffffffffffff" }))
    $scenarios.Add((New-FailureScenario -Name "wrong-run-attempt" -Code "run-attempt-mismatch" -Mutation { param($fixture) $fixture.run.run_attempt = "1" }))
    $scenarios.Add((New-FailureScenario -Name "wrong-event-name" -Code "run-event-mismatch" -Mutation { param($fixture) $fixture.run.event = "Pull_Request" }))
    $scenarios.Add((New-FailureScenario -Name "wrong-uploader-job-id" -Code "uploader-job-not-in-attempt" -Mutation { param($fixture) $fixture.jobsByPage["1"].jobs[0].id = "9002" }))
    $scenarios.Add((New-FailureScenario -Name "wrong-uploader-job-name" -Code "uploader-job-name-mismatch" -Mutation { param($fixture) $fixture.jobsByPage["1"].jobs[0].name = "other-job" }))
    $scenarios.Add((New-FailureScenario -Name "uploader-job-not-completed" -Code "uploader-job-not-completed" -Mutation { param($fixture) $fixture.jobsByPage["1"].jobs[0].status = "in_progress" }))
    $scenarios.Add((New-FailureScenario -Name "uploader-job-not-successful" -Code "uploader-job-not-successful" -Mutation { param($fixture) $fixture.jobsByPage["1"].jobs[0].conclusion = "failure" }))
    $scenarios.Add((New-FailureScenario -Name "contradictory-optional-job-attempt" -Code "uploader-job-attempt-mismatch" -Mutation { param($fixture) $fixture.jobsByPage["1"].jobs[0].run_attempt = "1" }))
    $scenarios.Add((New-FailureScenario -Name "wrong-artifact-id" -Code "artifact-id-mismatch" -Mutation { param($fixture) $fixture.artifact.id = "7002" }))
    $scenarios.Add((New-FailureScenario -Name "wrong-artifact-name" -Code "artifact-name-mismatch" -Mutation { param($fixture) $fixture.artifact.name = "release-runtime" }))
    $scenarios.Add((New-FailureScenario -Name "artifact-workflow-run-event-sha" -Code "artifact-event-sha-mismatch" -Mutation { param($fixture) $fixture.artifact.workflow_run.head_sha = "ffffffffffffffffffffffffffffffffffffffff" }))
    $scenarios.Add((New-FailureScenario -Name "artifact-fork-repository-id" -Code "artifact-repository-mismatch" -Mutation { param($fixture) $fixture.artifact.workflow_run.repository_id = "54321" }))
    $scenarios.Add((New-FailureScenario -Name "fork-head-repository-id" -Code "artifact-head-repository-mismatch" -Mutation { param($fixture) $fixture.artifact.workflow_run.head_repository_id = "54321" }))
    $scenarios.Add((New-FailureScenario -Name "fork-head-repository-name" -Code "run-head-repository-mismatch" -Mutation { param($fixture) $fixture.run.head_repository = [ordered]@{ full_name = "fork-owner/mcp-store" } }))
    $scenarios.Add((New-FailureScenario -Name "role-map-artifact-name-mismatch" -Code "role-map-artifact-name-mismatch" -Mutation { param($fixture) $fixture.artifact.name = "unused" } | ForEach-Object { $_.ArtifactName = "unexpected-artifact"; $_ }))
    $componentMismatch = New-FailureScenario -Name "role-component-map-mismatch" -Code "role-component-mismatch" -Mode "FetchCandidate" -Mutation { param($fixture) }
    $componentMismatch.Component = "workflow"
    $scenarios.Add($componentMismatch)
    $scenarios.Add((New-FailureScenario -Name "api-digest-mismatch" -Code "platform-digest-mismatch" -Mutation { param($fixture) $fixture.artifact.digest = "sha256:" + ("0" * 64) }))
    $scenarios.Add((New-FailureScenario -Name "api-digest-missing" -Code "missing-platform-field" -Mutation { param($fixture) $fixture.artifact.Remove("digest") | Out-Null }))
    $malformedDigest = New-Scenario -Name "api-digest-malformed"
    $malformedDigest.ExpectedExitCode = 1
    $malformedDigest.ExpectedStatus = ""
    $malformedDigest.ExpectedFailureCode = "malformed-platform-field"
    $malformedDigest.ApiDigestForm = "malformed"
    $scenarios.Add($malformedDigest)
    $sizeMismatch = New-FailureScenario -Name "platform-size-mismatch" -Code "transport-platform-size-mismatch" -Mutation { param($fixture) $fixture.artifact.size_in_bytes = [long]$fixture.artifact.size_in_bytes + 1 }
    $scenarios.Add($sizeMismatch)
    $badStubHash = New-Scenario -Name "stub-reported-digest-mismatch"
    $badStubHash.ExpectedExitCode = 1
    $badStubHash.ExpectedStatus = ""
    $badStubHash.ExpectedFailureCode = "transport-digest-mismatch"
    $badStubHash.ReportedSha256 = "0000000000000000000000000000000000000000000000000000000000000000"
    $scenarios.Add($badStubHash)
    $badStubSize = New-Scenario -Name "stub-reported-size-mismatch"
    $badStubSize.ExpectedExitCode = 1
    $badStubSize.ExpectedStatus = ""
    $badStubSize.ExpectedFailureCode = "transport-platform-size-mismatch"
    $badStubSize.ReportedBytes = 1
    $scenarios.Add($badStubSize)
    $missingStubFile = New-Scenario -Name "stub-download-file-missing"
    $missingStubFile.ExpectedExitCode = 1
    $missingStubFile.ExpectedStatus = ""
    $missingStubFile.ExpectedFailureCode = "invalid-test-transport"
    $missingStubFile.OmitDownload = $true
    $scenarios.Add($missingStubFile)
    $badRedirectHost = New-Scenario -Name "stub-redirect-host-rejected"
    $badRedirectHost.ExpectedExitCode = 1
    $badRedirectHost.ExpectedStatus = ""
    $badRedirectHost.ExpectedFailureCode = "redirect-host-rejected"
    $badRedirectHost.RedirectHost = "not-allowed.example"
    $scenarios.Add($badRedirectHost)

    $paginated = New-Scenario -Name "attempt-scoped-job-listing-pagination"
    $paginated.PaginateJobs = $true
    $scenarios.Add($paginated)
    $prodOrigin = New-Scenario -Name "production-origin-refuses-test-transport"
    $prodOrigin.ExpectedExitCode = 1
    $prodOrigin.ExpectedStatus = ""
    $prodOrigin.ExpectedFailureCode = "test-transport-on-production-origin"
    $prodOrigin.ApiBase = "https://api.github.com"
    $scenarios.Add($prodOrigin)
    $noSeam = New-Scenario -Name "non-production-origin-requires-test-transport"
    $noSeam.ExpectedExitCode = 1
    $noSeam.ExpectedStatus = ""
    $noSeam.ExpectedFailureCode = "non-production-api-base"
    $noSeam.IncludeTestTransport = $false
    $scenarios.Add($noSeam)

    $existingInstall = New-FailureScenario -Name "manual-install-directory-is-not-overwritten" -Code "install-root-exists" -Mutation { param($fixture) }
    $existingInstall.ExistingInstall = $true
    $existingInstall.ExpectInstallRootAbsent = $false
    $scenarios.Add($existingInstall)
    $sourceOverlap = New-FailureScenario -Name "install-source-overlap" -Code "install-overlaps-checkout" -Mutation { param($fixture) }
    $sourceOverlap.OverlapCheckout = $true
    $sourceOverlap.ExpectInstallRootAbsent = $false
    $scenarios.Add($sourceOverlap)
    $candidateOverlap = New-FailureScenario -Name "receipt-candidate-overlap" -Code "receipt-overlaps-candidate" -Mutation { param($fixture) }
    $candidateOverlap.ReceiptInsideCandidate = $true
    $candidateOverlap.ExpectReceiptAbsent = $true
    $candidateOverlap.ExpectCandidateRootAbsent = $true
    $scenarios.Add($candidateOverlap)

    # A genuine FetchCandidate caller who never supplies the optional -CandidateRoot
    # still has to work because production's Assert-RawTransportDestination filters
    # the empty default out of its protected-root set; this scenario omits the flag
    # entirely from the generated child argv (not -CandidateRoot ''), verifies the
    # omission with a post-write regex, and exercises the success retention proof on
    # the same fresh leaf the other FetchCandidate scenario uses.
    $omitCandidateRoot = New-Scenario -Name "fetch-candidate-omit-optional-candidate-root" -Mode "FetchCandidate" -Role "candidate" -ArtifactName "release-candidate-dotnet" -UploaderJobName "prepare-dotnet"
    $omitCandidateRoot.Component = "dotnet"
    $omitCandidateRoot.OmitOptionalCandidateRoot = $true
    $omitCandidateRoot.ExpectCandidateRootAbsent = $true
    $scenarios.Add($omitCandidateRoot)

    # A pre-existing raw transport file at the wrapper leaf must be rejected with
    # raw-transport-exists and the bootstrap must not delete or overwrite the file
    # the suite owns, so the wrapper writes a known fixture, asserts the failure
    # code, then verifies the wrapper-owned bytes survived the rejection exactly.
    $preexistingRawTransport = New-FailureScenario -Name "fetch-candidate-preexisting-raw-transport" -Code "raw-transport-exists" -Mutation { param($fixture) } -Mode "FetchCandidate"
    $preexistingRawTransport.PreserveExistingRawFile = $true
    $preexistingRawTransport.Component = "dotnet"
    $preexistingRawTransport.ExpectCandidateRootAbsent = $true
    $scenarios.Add($preexistingRawTransport)

    # A tampered -TrustedReceiptSha256 must reject the run with
    # trusted-receipt-digest-mismatch before any install root, runtime tree, or
    # sealed trusted receipt is produced; the typed diagnostic must reach the
    # caller and the catch must leave every fresh destination absent so an
    # attacker-controlled digest never reaches a publisher invocation.
    $trustedReceiptHashMismatch = New-FailureScenario -Name "trusted-receipt-hash-mismatch" -Code "trusted-receipt-digest-mismatch" -Mutation { param($fixture) }
    $trustedReceiptHashMismatch.TrustedReceiptSha256Override = "1111111111111111111111111111111111111111111111111111111111111111"
    $trustedReceiptHashMismatch.ExpectInstallRootAbsent = $true
    $scenarios.Add($trustedReceiptHashMismatch)

    # A pre-existing file at the trusted receipt destination must be rejected
    # with trusted-receipt-exists; the catch leaves the wrapper-owned file
    # untouched (no delete, no overwrite, no rename) so a user-controlled
    # receipt is never replaced by bootstrap output, mirroring the
    # raw-transport pre-existing-file guard.
    $trustedReceiptOverlap = New-FailureScenario -Name "trusted-receipt-destination-overlap" -Code "trusted-receipt-exists" -Mutation { param($fixture) }
    $trustedReceiptOverlap.PreserveExistingTrustedReceipt = $true
    $trustedReceiptOverlap.ExpectInstallRootAbsent = $true
    $scenarios.Add($trustedReceiptOverlap)

    # Positive copy proof for the sealed trusted receipt: the bootstrap's
    # success path is the only one that copies the receipt into a fresh
    # wrapper-created leaf, so this case focuses on the byte-equal copy and
    # the SHA-256 alignment with the source member without duplicating the
    # broader envelope/manifest/install-tree checks the existing positive
    # already covers.
    $correctRuntimeReceipt = New-Scenario -Name "correct-runtime-receipt-copied"
    $scenarios.Add($correctRuntimeReceipt)

    $archiveScenarios = @(
        # Each archive-* case declares exactly 3 ExpectedNames and emits exactly 3 actual
        # outer members so the production outer-entry count check passes; the loop-level
        # archive rule under test then fires before the SetEquals check, and the third
        # member is always a benign small leaf that is itself safe by every rule.
        @{ Name = "archive-duplicate-entry"; Expected = @("payload.txt", "payload-twin.txt", "extra.txt"); Actual = @((New-ZipEntry "payload.txt" ([byte[]]@(1))), (New-ZipEntry "payload.txt" ([byte[]]@(2))), (New-ZipEntry "extra.txt" ([byte[]]@(0)))); Code = "archive-duplicate-entry" },
        @{ Name = "archive-case-collision"; Expected = @("payload.txt", "payload-twin.txt", "extra.txt"); Actual = @((New-ZipEntry "payload.txt" ([byte[]]@(1))), (New-ZipEntry "PAYLOAD.TXT" ([byte[]]@(2))), (New-ZipEntry "extra.txt" ([byte[]]@(0)))); Code = "archive-case-collision" },
        @{ Name = "archive-traversal-entry"; Expected = @("payload.txt", "second.txt", "extra.txt"); Actual = @((New-ZipEntry "../escape.txt" ([byte[]]@(1))), (New-ZipEntry "payload.txt" ([byte[]]@(1))), (New-ZipEntry "extra.txt" ([byte[]]@(0)))); Code = "archive-unsafe-entry-traversal-entry-name" },
        @{ Name = "archive-absolute-entry"; Expected = @("payload.txt", "second.txt", "extra.txt"); Actual = @((New-ZipEntry "/absolute.txt" ([byte[]]@(1))), (New-ZipEntry "payload.txt" ([byte[]]@(1))), (New-ZipEntry "extra.txt" ([byte[]]@(0)))); Code = "archive-unsafe-entry-rooted-entry-name" },
        @{ Name = "archive-unc-entry"; Expected = @("payload.txt", "second.txt", "extra.txt"); Actual = @((New-ZipEntry "//server/share.txt" ([byte[]]@(1))), (New-ZipEntry "payload.txt" ([byte[]]@(1))), (New-ZipEntry "extra.txt" ([byte[]]@(0)))); Code = "archive-unsafe-entry-rooted-entry-name" },
        @{ Name = "archive-drive-entry"; Expected = @("payload.txt", "second.txt", "extra.txt"); Actual = @((New-ZipEntry "C:/drive.txt" ([byte[]]@(1))), (New-ZipEntry "payload.txt" ([byte[]]@(1))), (New-ZipEntry "extra.txt" ([byte[]]@(0)))); Code = "archive-unsafe-entry-colon-entry-name" },
        @{ Name = "archive-alternate-stream-entry"; Expected = @("payload.txt", "second.txt", "extra.txt"); Actual = @((New-ZipEntry "payload.txt:stream" ([byte[]]@(1))), (New-ZipEntry "payload.txt" ([byte[]]@(1))), (New-ZipEntry "extra.txt" ([byte[]]@(0)))); Code = "archive-unsafe-entry-colon-entry-name" },
        @{ Name = "archive-backslash-entry"; Expected = @("payload.txt", "second.txt", "extra.txt"); Actual = @((New-ZipEntry "dir\payload.txt" ([byte[]]@(1))), (New-ZipEntry "payload.txt" ([byte[]]@(1))), (New-ZipEntry "extra.txt" ([byte[]]@(0)))); Code = "archive-unsafe-entry-backslash-entry-name" },
        @{ Name = "archive-symlink-entry"; Expected = @("payload.txt", "second.txt", "extra.txt"); Actual = @((New-ZipEntry "link" ([byte[]]@(1)) ([int]::Parse("-1610612736"))), (New-ZipEntry "payload.txt" ([byte[]]@(1))), (New-ZipEntry "extra.txt" ([byte[]]@(0)))); Code = "archive-symbolic-link" },
        @{ Name = "archive-per-entry-expansion-limit"; Expected = @("small.txt", "second.txt", "extra.txt"); Actual = @((New-ZipEntry "payload.txt" ([byte[]]([System.Text.Encoding]::UTF8.GetBytes(("A" * 4096))))), (New-ZipEntry "small.txt" ([byte[]]@(0))), (New-ZipEntry "extra.txt" ([byte[]]@(0)))); Code = "archive-entry-too-large"; MaxEntry = 128; MaxTotal = 8192; Ratio = 100000 },
        @{ Name = "archive-total-expansion-limit"; Expected = @("one.txt", "two.txt", "extra.txt"); Actual = @((New-ZipEntry "one.txt" ([byte[]]([System.Text.Encoding]::UTF8.GetBytes(("A" * 100))))), (New-ZipEntry "two.txt" ([byte[]]([System.Text.Encoding]::UTF8.GetBytes(("B" * 100))))), (New-ZipEntry "extra.txt" ([byte[]]@(0)))); Code = "archive-total-too-large"; MaxEntry = 256; MaxTotal = 128; Ratio = 100000 },
        @{ Name = "archive-entry-count-limit"; Expected = @("one.txt", "two.txt", "extra.txt"); Actual = @((New-ZipEntry "one.txt" ([byte[]]@(1))), (New-ZipEntry "two.txt" ([byte[]]@(2))), (New-ZipEntry "extra.txt" ([byte[]]@(3)))); Code = "archive-entry-count-exceeded"; MaxCount = 1 }
    )
    foreach ($definition in $archiveScenarios) {
        $scenario = New-FailureScenario -Name $definition.Name -Code $definition.Code -Mutation { param($fixture) } -Mode "FetchCandidate"
        $scenario.ExpectedNames = @($definition.Expected)
        $scenario.ArchiveEntries = @($definition.Actual)
        if ($definition.ContainsKey("MaxEntry")) { $scenario.MaxEntryUncompressedBytes = $definition.MaxEntry }
        if ($definition.ContainsKey("MaxTotal")) { $scenario.MaxTotalUncompressedBytes = $definition.MaxTotal }
        if ($definition.ContainsKey("MaxCount")) { $scenario.MaxEntryCount = $definition.MaxCount }
        if ($definition.ContainsKey("Ratio")) { $scenario.MaxCompressionRatio = $definition.Ratio }
        $scenario.ExpectCandidateRootAbsent = $true
        $scenarios.Add($scenario)
    }

    foreach ($scenario in $scenarios) { Invoke-Scenario -Scenario $scenario }
} catch {
    $fatalEvidence = $_ | Out-String
    [Console]::Error.WriteLine("Bootstrap test launcher failed before completing the suite:")
    [Console]::Error.WriteLine($fatalEvidence)
    [Console]::Error.WriteLine("Artifacts retained at $suiteRoot")
    exit 2
}

$scenarioCount = $scenarios.Count
if ($script:FailedScenarios.Count -gt 0) {
    [Console]::Error.WriteLine("Bootstrap standalone verification: $($script:PassedScenarioCount)/$scenarioCount scenarios passed; $($script:AssertionCount) source-contract assertions evaluated; $($script:FailedScenarios.Count) scenario(s) failed.")
    [Console]::Error.WriteLine("Failure artifacts retained at $suiteRoot")
    exit 1
}

Remove-Item -LiteralPath $suiteRoot -Recurse -Force
Write-Output "Bootstrap standalone verification: $($script:PassedScenarioCount)/$scenarioCount scenarios passed; $($script:AssertionCount) source-contract assertions passed."
