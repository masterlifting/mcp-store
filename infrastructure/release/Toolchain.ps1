# role: reusable module/helper
# Pre-SDK PowerShell mirror of the compiled `ReleaseToolchain` reader (`Toolchain.fs`). Both apply the same rules to the same
# owners: the SDK version in the root `global.json`, the framework, language and Expecto versions in the root
# `Directory.Build.props`, and the runtime derived from the pinned SDK's `Microsoft.NETCoreSdk.BundledVersions.props`.
# Check.ps1 and Update.ps1 dot-source this file, so the closed property set below is authored once for both scripts.
# Nothing here discovers an SDK: `Select-ToolchainSdkRoot` works on an explicit listing, and only Update.ps1 -Verify runs
# the muxer listing, after the SDK is installed and before any credential step.

# The closed `Directory.Build.props` property set; any new property is a coordinated edit of every reader.
$script:ToolchainBuildProperties = @("TargetFramework"; "LangVersion"; "ExpectoVersion")
$script:ToolchainMaxOwnerBytes = 16 * 1024
$script:ToolchainMaxBundledBytes = 4 * 1024 * 1024
$script:ToolchainBundledVersionsName = "Microsoft.NETCoreSdk.BundledVersions.props"
# The handoff channel for the SDK root; the publication entry point reads this variable only.
$script:ToolchainSdkRootVariable = "MCP_RELEASE_SDK_ROOT"
# Digits separated by dots with an optional dot-separated alphanumeric prerelease label (\z, so a trailing newline never matches).
$script:ToolchainVersionPattern = '\A[0-9]+(\.[0-9]+)*(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?\z'

function Test-ToolchainVersionToken {
    param([AllowNull()][string]$Value)

    return ($null -ne $Value) -and ($Value -cmatch $script:ToolchainVersionPattern)
}

function Read-ToolchainBytes {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][int]$Limit)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "required toolchain owner not found: $Path" }
    $length = (Get-Item -LiteralPath $Path).Length
    if ($length -gt $Limit) { throw "toolchain file '$Path' exceeds the bounded maximum of $Limit bytes (got $length)" }
    return [System.IO.File]::ReadAllBytes($Path)
}

function Assert-ToolchainBytes {
    param([string]$Path, [byte[]]$Bytes, [int]$Limit)

    if ($null -eq $Bytes) { throw "toolchain file '$Path' has null bytes" }
    if ($Bytes.Length -gt $Limit) { throw "toolchain file '$Path' exceeds the bounded maximum of $Limit bytes (got $($Bytes.Length))" }
    if ($Bytes -contains 0) { throw "toolchain file '$Path' contains an embedded NUL byte" }
}

# Strict `global.json`: one `sdk` object with exactly version, rollForward and allowPrerelease; no repeated key (parsed with
# System.Text.Json because ConvertFrom-Json does not detect one), no unknown property, size bound and the strict values.
function ConvertFrom-GlobalJsonBytes {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes)

    Assert-ToolchainBytes -Path $Path -Bytes $Bytes -Limit $script:ToolchainMaxOwnerBytes

    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
    $options.MaxDepth = 4

    $document = $null
    try {
        $text = [System.Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
        $document = [System.Text.Json.JsonDocument]::Parse($text, $options)
    } catch {
        throw "toolchain file '$Path' is not valid strict JSON: $($_.Exception.Message)"
    }

    try {
        function Get-JsonMembers {
            param($Element, [string]$Where)
            $members = [ordered]@{}
            foreach ($property in $Element.EnumerateObject()) {
                if ($members.Contains($property.Name)) { throw "toolchain file '$Path' repeats the key '$($property.Name)' in $Where" }
                $members[$property.Name] = $property.Value
            }
            return $members
        }

        $root = $document.RootElement
        if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw "toolchain file '$Path' root must be an object" }
        $rootMembers = Get-JsonMembers -Element $root -Where "the root"
        foreach ($name in $rootMembers.Keys) {
            if ($name -cne "sdk") { throw "toolchain file '$Path' carries the unknown property '$name'" }
        }
        if (-not $rootMembers.Contains("sdk")) { throw "toolchain file '$Path' has no 'sdk' object" }
        $sdk = $rootMembers["sdk"]
        if ($sdk.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw "toolchain file '$Path' 'sdk' member must be an object" }
        $sdkMembers = Get-JsonMembers -Element $sdk -Where "'sdk'"
        $expected = @("version", "rollForward", "allowPrerelease")
        foreach ($name in $sdkMembers.Keys) {
            if ($expected -cnotcontains $name) { throw "toolchain file '$Path' carries the unknown 'sdk' property '$name'" }
        }
        foreach ($name in $expected) {
            if (-not $sdkMembers.Contains($name)) { throw "toolchain file '$Path' 'sdk' is missing '$name'" }
        }

        $version = $sdkMembers["version"]
        if ($version.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { throw "toolchain file '$Path' 'sdk.version' must be a string" }
        $versionText = $version.GetString()
        if (-not (Test-ToolchainVersionToken $versionText)) { throw "toolchain file '$Path' 'sdk.version' value '$versionText' is not a plain version token" }
        $rollForward = $sdkMembers["rollForward"]
        if ($rollForward.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or $rollForward.GetString() -cne "disable") {
            throw "toolchain file '$Path' 'sdk.rollForward' must be exactly `"disable`""
        }
        if ($sdkMembers["allowPrerelease"].ValueKind -ne [System.Text.Json.JsonValueKind]::False) {
            throw "toolchain file '$Path' 'sdk.allowPrerelease' must be exactly false"
        }
        return $versionText
    } finally {
        $document.Dispose()
    }
}

function Read-ToolchainXml {
    param([string]$Path, [byte[]]$Bytes)

    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.IgnoreComments = $true
    $settings.IgnoreProcessingInstructions = $true
    $settings.MaxCharactersInDocument = 8000000
    $stream = [System.IO.MemoryStream]::new($Bytes, $false)
    try {
        $reader = [System.Xml.XmlReader]::Create($stream, $settings)
        try {
            return [System.Xml.Linq.XDocument]::Load($reader)
        } catch {
            throw "toolchain file '$Path' is not well-formed XML: $($_.Exception.Message)"
        } finally {
            $reader.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

# Strict `Directory.Build.props`: exactly one attribute-free PropertyGroup of plain text elements over the closed set.
function ConvertFrom-BuildPropsBytes {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes)

    Assert-ToolchainBytes -Path $Path -Bytes $Bytes -Limit $script:ToolchainMaxOwnerBytes
    $document = Read-ToolchainXml -Path $Path -Bytes $Bytes
    $root = $document.Root
    if ($null -eq $root -or $root.Name.LocalName -cne "Project" -or $root.Name.NamespaceName -ne "") {
        throw "toolchain file '$Path' root element must be an un-namespaced <Project>"
    }
    if ($root.HasAttributes) { throw "toolchain file '$Path' <Project> must carry no attribute" }
    $groups = @($root.Elements())
    if ($groups.Count -ne 1 -or $groups[0].Name.LocalName -cne "PropertyGroup" -or $groups[0].HasAttributes) {
        throw "toolchain file '$Path' <Project> must contain exactly one attribute-free <PropertyGroup>"
    }

    $values = [ordered]@{}
    foreach ($property in @($groups[0].Elements())) {
        $name = $property.Name.LocalName
        if ($property.Name.NamespaceName -ne "" -or $script:ToolchainBuildProperties -cnotcontains $name) { throw "toolchain file '$Path' carries the unknown property '$name'" }
        if ($values.Contains($name)) { throw "toolchain file '$Path' repeats the property '$name'" }
        if ($property.HasAttributes -or $property.HasElements) { throw "toolchain file '$Path' property '$name' must be plain text" }
        $value = $property.Value
        if ($value.Length -eq 0 -or $value -cne $value.Trim()) { throw "toolchain file '$Path' property '$name' must be a non-empty value without surrounding whitespace" }
        $values[$name] = $value
    }
    foreach ($name in $script:ToolchainBuildProperties) {
        if (-not $values.Contains($name)) { throw "toolchain file '$Path' is missing the property '$name'" }
    }
    if ($values["TargetFramework"] -cnotmatch '\Anet[0-9]+\.[0-9]+\z') { throw "toolchain file '$Path' TargetFramework '$($values["TargetFramework"])' is not a plain netX.Y moniker" }
    if ($values["LangVersion"] -cnotmatch '\A[0-9]+\.[0-9]+\z') { throw "toolchain file '$Path' LangVersion '$($values["LangVersion"])' is not a plain X.Y version" }
    if (-not (Test-ToolchainVersionToken $values["ExpectoVersion"])) { throw "toolchain file '$Path' ExpectoVersion '$($values["ExpectoVersion"])' is not a plain version token" }

    return [pscustomobject]@{
        TargetFramework = $values["TargetFramework"]
        LangVersion     = $values["LangVersion"]
        ExpectoVersion  = $values["ExpectoVersion"]
    }
}

# The SDK-bundled facts: the single `KnownFrameworkReference` (exact element name, never `KnownRuntimePack`) for
# Microsoft.NETCore.App and the owned framework, its `DefaultRuntimeFrameworkVersion`, and the SDK's own
# `BundledNETCoreAppTargetFramework`, which must equal the owned framework.
function ConvertFrom-BundledVersionsBytes {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$TargetFramework,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes
    )

    Assert-ToolchainBytes -Path $Path -Bytes $Bytes -Limit $script:ToolchainMaxBundledBytes
    $document = Read-ToolchainXml -Path $Path -Bytes $Bytes
    $includeName = [System.Xml.Linq.XName]::Get("Include")
    $frameworkName = [System.Xml.Linq.XName]::Get("TargetFramework")
    $runtimeName = [System.Xml.Linq.XName]::Get("DefaultRuntimeFrameworkVersion")

    $references = @($document.Descendants() | Where-Object {
            $_.Name.LocalName -ceq "KnownFrameworkReference" -and
            $_.Attribute($includeName)?.Value -ceq "Microsoft.NETCore.App" -and
            $_.Attribute($frameworkName)?.Value -ceq $TargetFramework
        })
    if ($references.Count -eq 0) { throw "toolchain file '$Path' has no KnownFrameworkReference for Microsoft.NETCore.App and '$TargetFramework'" }
    if ($references.Count -gt 1) { throw "toolchain file '$Path' has more than one KnownFrameworkReference for Microsoft.NETCore.App and '$TargetFramework'" }

    $bundled = @($document.Descendants() | Where-Object { $_.Name.LocalName -ceq "BundledNETCoreAppTargetFramework" })
    if ($bundled.Count -ne 1) { throw "toolchain file '$Path' must declare exactly one BundledNETCoreAppTargetFramework" }

    $attribute = $references[0].Attribute($runtimeName)
    if ($null -eq $attribute) { throw "toolchain file '$Path' matching KnownFrameworkReference carries no DefaultRuntimeFrameworkVersion" }
    if (-not (Test-ToolchainVersionToken $attribute.Value)) { throw "toolchain file '$Path' DefaultRuntimeFrameworkVersion '$($attribute.Value)' is not a plain version token" }
    if ($bundled[0].Value -cne $TargetFramework) {
        throw "toolchain file '$Path' BundledNETCoreAppTargetFramework '$($bundled[0].Value)' differs from the owned target framework '$TargetFramework'"
    }

    return [pscustomobject]@{ BundledTargetFramework = $bundled[0].Value; DefaultRuntimeFrameworkVersion = $attribute.Value }
}

function Get-OwnedSdkVersion {
    param([Parameter(Mandatory = $true)][string]$Root)

    $path = Join-Path $Root "global.json"
    return ConvertFrom-GlobalJsonBytes -Path $path -Bytes (Read-ToolchainBytes -Path $path -Limit $script:ToolchainMaxOwnerBytes)
}

function Get-OwnedBuildProperties {
    param([Parameter(Mandatory = $true)][string]$Root)

    $path = Join-Path $Root "Directory.Build.props"
    return ConvertFrom-BuildPropsBytes -Path $path -Bytes (Read-ToolchainBytes -Path $path -Limit $script:ToolchainMaxOwnerBytes)
}

function ConvertFrom-SdkListing {
    param([AllowNull()][string[]]$Lines)

    foreach ($line in @($Lines)) {
        if ("$line".Trim() -match '^(?<version>\S+)\s+\[(?<root>.+)\]\s*$') {
            [pscustomobject]@{ Version = $Matches['version']; Root = $Matches['root'] }
        }
    }
}

function Format-ToolchainRoot {
    param([string]$Root)

    return [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
}

function Test-ToolchainSameRoot {
    param([string]$Left, [string]$Right)

    $comparison = if ($IsWindows -or $PSVersionTable.PSVersion.Major -lt 6) { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }
    return [string]::Equals((Format-ToolchainRoot $Left), (Format-ToolchainRoot $Right), $comparison)
}

# The pinned version must resolve to exactly one root; none or more than one distinct root fails closed.
function Select-ToolchainSdkRoot {
    param([Parameter(Mandatory = $true)][string]$Version, [AllowNull()][object[]]$Installs)

    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($install in @($Installs)) {
        if ($install.Version -cne $Version) { continue }
        $known = $false
        foreach ($root in $roots) { if (Test-ToolchainSameRoot $root $install.Root) { $known = $true } }
        if (-not $known) { $roots.Add($install.Root) }
    }
    if ($roots.Count -eq 0) { throw "the pinned SDK '$Version' is not installed" }
    if ($roots.Count -gt 1) { throw "the pinned SDK '$Version' is installed under more than one root [$($roots -join '; ')]; refusing to choose one" }
    return (Format-ToolchainRoot $roots[0])
}

# The derived runtime: the pinned SDK's DefaultRuntimeFrameworkVersion for the owned framework, read under one explicit root.
function Get-DerivedRuntimeVersion {
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$SdkRoot)

    $sdkVersion = Get-OwnedSdkVersion -Root $Root
    $properties = Get-OwnedBuildProperties -Root $Root
    # The version token was validated by the global.json reader, so this join cannot leave the SDK root.
    $path = Join-Path (Join-Path $SdkRoot $sdkVersion) $script:ToolchainBundledVersionsName
    $facts = ConvertFrom-BundledVersionsBytes -Path $path -TargetFramework $properties.TargetFramework -Bytes (Read-ToolchainBytes -Path $path -Limit $script:ToolchainMaxBundledBytes)
    return $facts.DefaultRuntimeFrameworkVersion
}
