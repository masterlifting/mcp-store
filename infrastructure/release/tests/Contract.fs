// role: test module
// The shared contract only holds when the policy-derived trusted shape meets real archive bytes, so these assertions run compiled in the suite rather than reviewed by inspection.

module Release.Tests.Contract

open Expecto
open Release.Tests

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open BuildProvenance
open DistributionContract

let mutable passed = 0
let mutable failed = 0
let failures = ResizeArray<string>()

let assertTrue name condition =
    if condition then passed <- passed + 1
    else
        failed <- failed + 1
        failures.Add(name)

let assertEqual name expected actual =
    if expected = actual then passed <- passed + 1
    else
        failed <- failed + 1
        failures.Add(sprintf "%s: expected %A got %A" name expected actual)

let private recordFailure name detail =
    failed <- failed + 1
    failures.Add(sprintf "%s: %s" name detail)

let private assertOk name (result: Result<'a, ReleaseError>) =
    match result with
    | Ok value ->
        passed <- passed + 1
        value
    | Error error ->
        recordFailure name (sprintf "expected Ok got '%s'" (ReleaseError.message error))
        Unchecked.defaultof<'a>

let private assertDeclared name (result: Result<'a, ReleaseError>) (detail: string) =
    match result with
    | Ok _ -> recordFailure name (sprintf "expected Error containing '%s' got Ok" detail)
    | Error error ->
        let message = ReleaseError.message error

        if message.Contains(detail, StringComparison.Ordinal) then
            passed <- passed + 1
        else
            recordFailure name (sprintf "expected '%s' got '%s'" detail message)

let private assertIdentityMismatch name (field: string) (result: Result<unit, ReleaseError>) =
    match result with
    | Error(IdentityMismatch(actualField, _, _)) when actualField = field -> passed <- passed + 1
    | Error error -> recordFailure name (sprintf "expected IdentityMismatch '%s' got '%s'" field (ReleaseError.message error))
    | Ok () -> recordFailure name (sprintf "expected IdentityMismatch '%s' got Ok" field)

// Mutations ride the shared renderer, so a case varies only the manifest field it targets.
let private manifestText (mutate: JsonObject -> unit) =
    let node =
        JsonNode
            .Parse(Fixture.buildManifestText Fixture.canonicalEvaluatedRevision Fixture.canonicalSdk (Fixture.canonicalPayloadHashes ()))
            .AsObject()

    mutate node
    node.ToJsonString()

let private canonicalManifestWithoutNotice () =
    let manifest =
        Fixture.canonicalManifest Fixture.canonicalEvaluatedRevision Fixture.canonicalSdk (Fixture.canonicalPayloadHashes ())

    { manifest with
        Files = manifest.Files |> List.filter (fun file -> file.Path <> noticeEntryName) }

let private isInsideRoot (root: string) (path: string) =
    path.StartsWith(Path.GetFullPath root + string Path.DirectorySeparatorChar, StringComparison.Ordinal)

let private resolveRootWithSpaces () =
    let outer = Fixture.newTempDir "mcp-contract-root"
    let root = Path.Combine(outer, "nested root with spaces")
    Directory.CreateDirectory root |> ignore
    root

let private unsafeMembers =
    [ "../escape.dll"
      "nested/../escape.dll"
      "/absolute.dll"
      "C:/drive.dll"
      "back\\slash.dll"
      "trailing/"
      "" ]

let arbitraryRootResolution () =
    let root = resolveRootWithSpaces ()
    let otherRootOuter = Fixture.newTempDir "mcp-contract-other"

    try
        let otherRoot = Path.Combine(otherRootOuter, "second root with spaces")
        Directory.CreateDirectory otherRoot |> ignore
        let entry = Fixture.canonicalEntrypoint

        match resolveUnderRoot root entry with
        | Error error -> recordFailure "arbitrary root resolves the canonical entrypoint" (ReleaseError.message error)
        | Ok resolved ->
            assertEqual "arbitrary root keeps the member leaf name" entry (Path.GetFileName resolved)
            assertTrue "arbitrary root composes beneath the supplied root" (isInsideRoot root resolved)
            assertTrue "the resolved entrypoint is not an installed or harness location" (not (resolved.Contains("opencode", StringComparison.OrdinalIgnoreCase)))

        match resolveUnderRoot otherRoot entry, resolveUnderRoot root entry with
        | Ok other, Ok first ->
            assertTrue "the same member resolves under an unrelated root" (other <> first)
            assertTrue "each resolution stays inside its own root" (other.Contains(otherRoot, StringComparison.Ordinal))
            assertTrue "no resolution embeds the other root" (not (other.Contains(root, StringComparison.Ordinal)))
        | Error error, _ -> recordFailure "unrelated root resolves the canonical entrypoint" (ReleaseError.message error)
        | _, Error error -> recordFailure "spaced root resolves the canonical entrypoint" (ReleaseError.message error)

        for unsafeMember in unsafeMembers do
            assertDeclared
                (sprintf "unsafe member '%s' is rejected" unsafeMember)
                (resolveUnderRoot root unsafeMember)
                "not a safe slash-separated relative path"

        assertDeclared "an empty extraction root is rejected" (resolveUnderRoot "" entry) "extraction root is empty"
    finally
        Fixture.safeDeleteDir root
        Fixture.safeDeleteDir (Path.GetDirectoryName root)
        Fixture.safeDeleteDir otherRootOuter

let bothProducerPolicies () =
    for relative in [ "dotnet/release/Config.json"; "workflow/release/Config.json" ] do
        let policy = assertOk (sprintf "%s projects a policy" relative) (ReleasePolicy.parse (Path.Combine(Fixture.repoRoot, relative)))

        let contract =
            assertOk (sprintf "%s projects a trusted contract" relative) (ReleaseCompare.contractFromPolicy policy policy.ArchiveName)

        assertEqual (sprintf "%s component identity" relative) policy.ComponentId contract.ComponentId
        assertEqual (sprintf "%s version identity" relative) policy.Version contract.Version
        assertEqual (sprintf "%s archive identity" relative) policy.ArchiveName contract.ArchiveName
        assertEqual (sprintf "%s source repository" relative) Fixture.canonicalSourceRepository contract.SourceRepository

        assertEqual
            (sprintf "%s launch descriptor" relative)
            { Kind = launchKindDotnetExec
              Entrypoint = policy.EntryDll
              Arguments = policy.LaunchArguments }
            contract.Launch

        assertEqual
            (sprintf "%s producer provenance" relative)
            { Sdk = policy.SdkVersion
              Runtime = policy.RuntimeVersion
              Platform = policy.Platform }
            contract.Producer

        assertEqual
            (sprintf "%s archive allowlist" relative)
            (trustedArchiveMemberNames policy.PublishedFiles)
            contract.EntryAllowlist

        assertEqual
            (sprintf "%s allowlist adds NOTICE and the manifest exactly once" relative)
            (trustedArchiveMemberNames policy.PublishedFiles).Length
            (contract.EntryAllowlist |> List.distinct).Length

        assertTrue
            (sprintf "%s trusted members carry no absolute, drive-qualified, or harness path" relative)
            (contract.EntryAllowlist
             |> List.forall (fun entry ->
                 isSafeRelativePath entry
                 && not (entry.Contains("opencode", StringComparison.OrdinalIgnoreCase))))

let exactFailureClasses () =
    async {
        let root = Fixture.newTempDir "mcp-contract-failures"

        try
            let identityArchive = Path.Combine(root, "identity.zip")
            Fixture.writeManifestArchive identityArchive (manifestText (fun node -> node["id"] <- JsonValue.Create "rogue"))

            match! ReleaseCompare.observe identityArchive with
            | Error error -> recordFailure "identity mismatch observation" (ReleaseError.message error)
            | Ok observation ->
                assertIdentityMismatch
                    "a manifest id that is not the trusted identity fails closed"
                    "id"
                    (ReleaseCompare.validate Fixture.canonicalContract observation "candidate")

            let hashArchive = Path.Combine(root, "hash.zip")

            Fixture.writeManifestArchive
                hashArchive
                (manifestText (fun node ->
                    for value in node["files"].AsArray() do
                        let file = value.AsObject()

                        if file["path"].GetValue<string>() = Fixture.canonicalEntrypoint then
                            file["sha256"] <- JsonValue.Create(String('a', 64))))

            match! ReleaseCompare.observe hashArchive with
            | Ok _ -> recordFailure "declared hash mismatch" "observation unexpectedly accepted"
            | Error error ->
                assertTrue
                    "a declared payload hash that is not the archived payload fails closed"
                    ((ReleaseError.message error).Contains("does not match the archived payload", StringComparison.Ordinal))

            let runtimeArchive = Path.Combine(root, "runtime.zip")

            Fixture.writeManifestArchive
                runtimeArchive
                (manifestText (fun node ->
                    let runtime = node["runtime"].AsObject()
                    runtime["version"] <- JsonValue.Create "9.9.9.9"))

            match! ReleaseCompare.observe runtimeArchive with
            | Ok _ -> recordFailure "runtime cross-check" "observation unexpectedly accepted"
            | Error(IdentityMismatch("runtime", _, _)) -> passed <- passed + 1
            | Error error -> recordFailure "runtime cross-check" (ReleaseError.message error)

            let publishedHead = String('a', 40)
            let candidateHead = String('b', 40)
            let evaluatedHead = String('c', 40)
            let publishedArchive = Path.Combine(root, "published.zip")
            let candidateArchive = Path.Combine(root, "candidate.zip")
            let publishedManifest = Path.Combine(root, "published.json")
            let candidateManifest = Path.Combine(root, "candidate.json")

            Fixture.writeRevisionVariantArchive publishedArchive publishedManifest publishedHead
            Fixture.writeRevisionVariantArchive candidateArchive candidateManifest candidateHead

            let! publishedResult = ReleaseCompare.observe publishedArchive
            let! candidateResult = ReleaseCompare.observe candidateArchive

            match publishedResult, candidateResult with
            | Ok published, Ok candidate ->
                match! ReleaseCompare.compare Fixture.canonicalContract published candidate evaluatedHead with
                | Error(RevisionMismatch(expected, actual)) ->
                    assertEqual "a candidate revision that is not the evaluated HEAD names the HEAD" evaluatedHead expected
                    assertEqual "a candidate revision that is not the evaluated HEAD names the candidate" candidateHead actual
                | Error error -> recordFailure "revision mismatch" (ReleaseError.message error)
                | Ok decision -> recordFailure "revision mismatch" (sprintf "expected RevisionMismatch got %A" decision)
            | Error error, _ -> recordFailure "published revision-variant observation" (ReleaseError.message error)
            | _, Error error -> recordFailure "candidate revision-variant observation" (ReleaseError.message error)

            assertDeclared
                "a manifest without the NOTICE member fails closed"
                (validate (canonicalManifestWithoutNotice ()))
                "NOTICE.txt"
        finally
            Fixture.safeDeleteDir root
    }

// A duplicate key anywhere in the packaged runtime configuration would make the derived prerequisite order-dependent.
let private assertDuplicateRuntimeConfigKey name key (text: string) =
    let expected = sprintf "duplicate JSON key '%s'" key

    match runtimeRequirementFromConfig Fixture.canonicalRuntimeConfig (Encoding.UTF8.GetBytes text) with
    | Error(MalformedArtifact(path, detail)) when path = Fixture.canonicalRuntimeConfig && detail.Contains(expected, StringComparison.Ordinal) ->
        passed <- passed + 1
    | Error error -> recordFailure name (sprintf "expected '%s' got '%s'" expected (ReleaseError.message error))
    | Ok requirement -> recordFailure name (sprintf "expected '%s' got %A" expected requirement)

let runtimeRequirementDerivation () =
    match
        runtimeRequirementFromConfig Fixture.canonicalRuntimeConfig (Fixture.runtimeConfigPayloadFor Fixture.canonicalRuntime)
    with
    | Ok requirement ->
        assertEqual "a valid packaged runtime configuration derives the framework" runtimeFramework requirement.Framework
        assertEqual "a valid packaged runtime configuration derives the version" Fixture.canonicalRuntime requirement.Version
        assertEqual "a derived prerequisite keeps the packaged configuration name" Fixture.canonicalRuntimeConfig requirement.Config
    | Error error -> recordFailure "a valid packaged runtime configuration derives its prerequisite" (ReleaseError.message error)

    assertDuplicateRuntimeConfigKey
        "a duplicate runtimeOptions key is rejected"
        "runtimeOptions"
        """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"9.9.9"}},"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"11.0.0"}}}"""

    assertDuplicateRuntimeConfigKey
        "a duplicate framework key is rejected"
        "framework"
        """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"9.9.9"},"framework":{"name":"Microsoft.NETCore.App","version":"11.0.0"}}}"""

    assertDuplicateRuntimeConfigKey
        "a duplicate framework name key is rejected"
        "name"
        """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","name":"Microsoft.NETCore.App","version":"11.0.0"}}}"""

    assertDuplicateRuntimeConfigKey
        "a duplicate framework version key is rejected"
        "version"
        """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"9.9.9","version":"11.0.0"}}}"""

    assertDuplicateRuntimeConfigKey
        "a duplicate key outside the derived path is rejected"
        "configProperties"
        """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"11.0.0"}},"configProperties":{"a":1},"configProperties":{"b":2}}"""

    let expectRuntimeDiagnostic (name: string) (detail: string) (text: string) =
        match runtimeRequirementFromConfig Fixture.canonicalRuntimeConfig (Encoding.UTF8.GetBytes text) with
        | Error(MalformedArtifact(_, actual)) when actual = detail -> passed <- passed + 1
        | Error error -> recordFailure name (sprintf "expected '%s' got '%s'" detail (ReleaseError.message error))
        | Ok requirement -> recordFailure name (sprintf "expected '%s' got %A" detail requirement)

    expectRuntimeDiagnostic
        "a non-object packaged runtime configuration keeps its diagnostic"
        "the packaged runtime configuration is not a JSON object"
        "[]"

    expectRuntimeDiagnostic
        "a packaged runtime configuration without runtimeOptions keeps its diagnostic"
        "the packaged runtime configuration declares no 'runtimeOptions' object"
        """{"configProperties":{}}"""

let tests =
    testCaseAsync "shared distribution contract assertions" <| async {
        arbitraryRootResolution ()
        bothProducerPolicies ()
        runtimeRequirementDerivation ()
        do! exactFailureClasses ()
        printfn "contract-tests summary: passed=%d failed=%d" passed failed
        Expect.equal 0 failed (String.concat Environment.NewLine failures)
    }
