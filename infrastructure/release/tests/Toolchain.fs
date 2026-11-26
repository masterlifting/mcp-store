module Release.Tests.Toolchain

open System
open System.IO
open System.Text
open Expecto
open SharedFailure

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

// Synthetic values keep the parser fixtures independent of the host's installed SDK and runtime.
let private fixtureSdk = "7.8.9"
let private fixtureFramework = "net7.8"
let private fixtureLanguage = "8.0"
let private fixtureRuntime = "7.8.6"

let private withTempRoot (action: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "mcp-release-toolchain-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore

    try
        action root
    finally
        try
            if Directory.Exists root then Directory.Delete(root, true)
        with _ ->
            ()

// The strict `global.json` shape, so a case varies only the field it targets.
let private globalJsonDocument version rollForward allowPrerelease =
    sprintf
        "{\n  \"sdk\": {\n    \"version\": \"%s\",\n    \"rollForward\": \"%s\",\n    \"allowPrerelease\": %s\n  }\n}\n"
        version
        rollForward
        (if allowPrerelease then "true" else "false")

let private parseGlobalJsonText (name: string) (contents: string) =
    ReleaseToolchain.parseGlobalJson (Path.Combine("fixture", name)) (Encoding.UTF8.GetBytes contents)

let private expectRejected description (fragment: string) result =
    match result with
    | Ok _ -> failtestf "%s was accepted" description
    | Error error ->
        Expect.stringContains
            (ReleaseError.message error)
            fragment
            (sprintf "%s is rejected with the named reason" description)

let private globalJsonReaderTests =
    testList
        "global.json strict reader"
        [ testCase "the canonical repository owner is accepted" (fun _ ->
              match ReleaseToolchain.readSdkVersion repoRoot with
              | Ok version -> Expect.isTrue (ReleaseToolchain.isVersionToken version) "the owner names a plain version token"
              | Error error -> failtestf "the canonical global.json was rejected: %s" (ReleaseError.message error))

          testCase "a strict global.json is accepted" (fun _ ->
              match parseGlobalJsonText "strict.json" (globalJsonDocument "11.0.100" "disable" false) with
              | Ok version -> Expect.equal version "11.0.100" "the declared version is returned"
              | Error error -> failtestf "a strict owner was rejected: %s" (ReleaseError.message error))

          testCase "a repeated key is rejected" (fun _ ->
              let contents =
                  "{ \"sdk\": { \"version\": \"11.0.100\", \"version\": \"11.0.100\", \"rollForward\": \"disable\", \"allowPrerelease\": false } }"

              parseGlobalJsonText "duplicate-key.json" contents |> expectRejected "a repeated key" "is repeated")

          testCase "an unknown root property is rejected" (fun _ ->
              let contents =
                  "{ \"sdk\": { \"version\": \"11.0.100\", \"rollForward\": \"disable\", \"allowPrerelease\": false }, \"msbuild\": {} }"

              parseGlobalJsonText "unknown-root.json" contents |> expectRejected "an unknown root property" "not allowed")

          testCase "an unknown sdk property is rejected" (fun _ ->
              let contents =
                  "{ \"sdk\": { \"version\": \"11.0.100\", \"rollForward\": \"disable\", \"allowPrerelease\": false, \"path\": \"x\" } }"

              parseGlobalJsonText "unknown-sdk.json" contents |> expectRejected "an unknown sdk property" "not allowed")

          testCase "a missing sdk property is rejected" (fun _ ->
              let contents = "{ \"sdk\": { \"version\": \"11.0.100\", \"allowPrerelease\": false } }"

              parseGlobalJsonText "missing-property.json" contents
              |> expectRejected "a missing sdk property" "is missing")

          testCase "a loose rollForward is rejected" (fun _ ->
              parseGlobalJsonText "loose-rollforward.json" (globalJsonDocument "11.0.100" "latestPatch" false)
              |> expectRejected "a loose rollForward" "rollForward")

          testCase "an allowPrerelease other than false is rejected" (fun _ ->
              parseGlobalJsonText "prerelease.json" (globalJsonDocument "11.0.100" "disable" true)
              |> expectRejected "allowPrerelease true" "allowPrerelease")

          testCase "a non-string version is rejected" (fun _ ->
              let contents =
                  "{ \"sdk\": { \"version\": 1100100, \"rollForward\": \"disable\", \"allowPrerelease\": false } }"

              parseGlobalJsonText "numeric-version.json" contents
              |> expectRejected "a numeric version" "must be a string")

          testCase "a version that is not a plain token is rejected" (fun _ ->
              parseGlobalJsonText "traversal.json" (globalJsonDocument "../outside" "disable" false)
              |> expectRejected "a traversal version" "plain version token")

          testCase "an oversized owner is rejected" (fun _ ->
              parseGlobalJsonText "oversized.json" (String('x', ReleaseToolchain.maxOwnerBytes + 1))
              |> expectRejected "an oversized owner" "bounded maximum")

          testCase "an embedded NUL byte is rejected" (fun _ ->
              parseGlobalJsonText "nul.json" (globalJsonDocument "11.0.100" "disable" false + "\u0000")
              |> expectRejected "an embedded NUL byte" "NUL byte")

          testCase "a document that is not strict JSON is rejected" (fun _ ->
              parseGlobalJsonText "trailing-comma.json" "{ \"sdk\": { \"version\": \"11.0.100\", }, }"
              |> expectRejected "a trailing comma" "not valid strict JSON")

          testCase "the root must be an object" (fun _ ->
              parseGlobalJsonText "array.json" "[ 1 ]" |> expectRejected "an array root" "must be an object")

          testCase "a missing owner fails closed" (fun _ ->
              withTempRoot (fun root ->
                  match ReleaseToolchain.readSdkVersion root with
                  | Ok version -> failtestf "a missing global.json produced the version '%s'" version
                  | Error(MissingArtifact path) -> Expect.stringContains path "global.json" "the missing owner is named"
                  | Error error -> failtestf "a missing owner produced the wrong failure: %s" (ReleaseError.message error)))

          testCase "the version token rejects every named malformed shape" (fun _ ->
              let rejected =
                  [ "..", "the parent directory"
                    "11/0/100", "a forward separator"
                    "11\\0\\100", "a back separator"
                    "11..100", "an empty component"
                    "11.0.100.", "a trailing dot"
                    "C:11.0.100", "a drive prefix"
                    "-rc.1", "a bare prerelease label"
                    "11.0.100\n", "a trailing newline"
                    "", "an empty value" ]

              for value, description in rejected do
                  Expect.isFalse (ReleaseToolchain.isVersionToken value) (sprintf "%s is not a version token" description)

              let accepted = [ "11.0.100"; "11"; "11.0"; "6.0.428"; "11.0.100-rc.1.26425.128" ]

              for value in accepted do
                  Expect.isTrue (ReleaseToolchain.isVersionToken value) (sprintf "'%s' is a version token" value)) ]

let private evaluationProperties sdk framework language bundled =
    [ "NETCoreSdkVersion", sdk
      "TargetFramework", framework
      "LangVersion", language
      "BundledNETCoreAppTargetFramework", bundled ]

let private propertyText (properties: (string * string) list) =
    properties
    |> List.map (fun (name, value) -> sprintf "\"%s\":\"%s\"" name value)
    |> String.concat ","

let private frameworkReference identity framework runtime =
    match runtime with
    | Some value ->
        sprintf
            "{\"Identity\":\"%s\",\"TargetFramework\":\"%s\",\"DefaultRuntimeFrameworkVersion\":\"%s\"}"
            identity
            framework
            value
    | None -> sprintf "{\"Identity\":\"%s\",\"TargetFramework\":\"%s\"}" identity framework

let private evaluationDocument (properties: (string * string) list) (references: string list) =
    sprintf
        "{\"Properties\":{%s},\"Items\":{\"KnownFrameworkReference\":[%s]}}"
        (propertyText properties)
        (String.concat "," references)

let private parseEvaluationText (name: string) (output: string) =
    ReleaseToolchain.parseEvaluation (Path.Combine("fixture", name)) output

let private evaluationTests =
    testList
        "native evaluation reader"
        [ testCase "exactly one matching framework reference is accepted" (fun _ ->
              let output =
                  evaluationDocument
                      (evaluationProperties fixtureSdk fixtureFramework fixtureLanguage fixtureFramework)
                      [ frameworkReference "Microsoft.NETCore.App" fixtureFramework (Some fixtureRuntime) ]

              match parseEvaluationText "evaluation.json" output with
              | Ok facts ->
                  Expect.equal facts.EvaluatedSdkVersion fixtureSdk "the evaluated SDK is returned"
                  Expect.equal facts.TargetFramework fixtureFramework "the evaluated framework is returned"
                  Expect.equal facts.LangVersion fixtureLanguage "the evaluated language version is returned"
                  Expect.equal facts.BundledTargetFramework fixtureFramework "the SDK's own framework is returned"
                  Expect.equal facts.DefaultRuntimeFrameworkVersion fixtureRuntime "the derived runtime is returned"
              | Error error -> failtestf "a matching reference was rejected: %s" (ReleaseError.message error))

          testCase "zero matching references fail closed" (fun _ ->
              let output =
                  evaluationDocument
                      (evaluationProperties fixtureSdk fixtureFramework fixtureLanguage fixtureFramework)
                      [ frameworkReference "Microsoft.NETCore.App" "net7.9" (Some fixtureRuntime) ]

              parseEvaluationText "no-match.json" output
              |> expectRejected "a reference for another framework" "no KnownFrameworkReference")

          testCase "two matching references fail closed" (fun _ ->
              let output =
                  evaluationDocument
                      (evaluationProperties fixtureSdk fixtureFramework fixtureLanguage fixtureFramework)
                      [ frameworkReference "Microsoft.NETCore.App" fixtureFramework (Some fixtureRuntime)
                        frameworkReference "Microsoft.NETCore.App" fixtureFramework (Some "7.8.7") ]

              parseEvaluationText "two-match.json" output
              |> expectRejected "two matching references" "more than one KnownFrameworkReference")

          testCase "a matching reference without DefaultRuntimeFrameworkVersion fails closed" (fun _ ->
              let output =
                  evaluationDocument
                      (evaluationProperties fixtureSdk fixtureFramework fixtureLanguage fixtureFramework)
                      [ frameworkReference "Microsoft.NETCore.App" fixtureFramework None ]

              parseEvaluationText "no-runtime.json" output
              |> expectRejected "a reference without the runtime attribute" "carries no DefaultRuntimeFrameworkVersion")

          testCase "a KnownRuntimePack is never mistaken for a KnownFrameworkReference" (fun _ ->
              let output =
                  sprintf
                      "{\"Properties\":{%s},\"Items\":{\"KnownRuntimePack\":[{\"Identity\":\"Microsoft.NETCore.App\",\"TargetFramework\":\"%s\",\"LatestRuntimeFrameworkVersion\":\"%s\"}]}}"
                      (propertyText (evaluationProperties fixtureSdk fixtureFramework fixtureLanguage fixtureFramework))
                      fixtureFramework
                      fixtureRuntime

              parseEvaluationText "runtime-pack.json" output
              |> expectRejected "a KnownRuntimePack only document" "no KnownFrameworkReference")

          testCase "the SDK's own framework must equal the evaluated framework" (fun _ ->
              let output =
                  evaluationDocument
                      (evaluationProperties fixtureSdk fixtureFramework fixtureLanguage "net7.9")
                      [ frameworkReference "Microsoft.NETCore.App" fixtureFramework (Some fixtureRuntime) ]

              parseEvaluationText "framework-drift.json" output
              |> expectRejected "a drifted bundled framework" "differs from the evaluated target framework")

          testCase "a runtime value that is not a plain token fails closed" (fun _ ->
              let output =
                  evaluationDocument
                      (evaluationProperties fixtureSdk fixtureFramework fixtureLanguage fixtureFramework)
                      [ frameworkReference "Microsoft.NETCore.App" fixtureFramework (Some "../7.8.6") ]

              parseEvaluationText "runtime-token.json" output
              |> expectRejected "a traversal runtime value" "plain version token")

          testCase "an oversized evaluation fails closed" (fun _ ->
              parseEvaluationText "oversized.json" (String('x', ReleaseToolchain.maxEvaluationBytes + 1))
              |> expectRejected "an oversized evaluation" "bounded maximum")

          testCase "a framework that is not a plain moniker fails closed" (fun _ ->
              let output =
                  evaluationDocument
                      (evaluationProperties fixtureSdk "net7" fixtureLanguage "net7")
                      [ frameworkReference "Microsoft.NETCore.App" "net7" (Some fixtureRuntime) ]

              parseEvaluationText "framework.json" output
              |> expectRejected "a framework without a minor version" "plain netX.Y moniker")

          testCase "a language version that is not a plain version fails closed" (fun _ ->
              let output =
                  evaluationDocument
                      (evaluationProperties fixtureSdk fixtureFramework "preview" fixtureFramework)
                      [ frameworkReference "Microsoft.NETCore.App" fixtureFramework (Some fixtureRuntime) ]

              parseEvaluationText "language.json" output
              |> expectRejected "a language name instead of a version" "plain X.Y version")

          testCase "a missing evaluated property fails closed" (fun _ ->
              let output =
                  evaluationDocument
                      [ "NETCoreSdkVersion", fixtureSdk
                        "TargetFramework", fixtureFramework
                        "LangVersion", fixtureLanguage ]
                      [ frameworkReference "Microsoft.NETCore.App" fixtureFramework (Some fixtureRuntime) ]

              parseEvaluationText "missing-property.json" output
              |> expectRejected "a missing evaluated property" "lacks NETCoreSdkVersion") ]

let private withVerifiedHandoff (sdk: string option) (runtime: string option) (action: unit -> unit) =
    let previousSdk = Environment.GetEnvironmentVariable ReleaseToolchain.verifiedSdkVariable
    let previousRuntime = Environment.GetEnvironmentVariable ReleaseToolchain.verifiedRuntimeVariable

    try
        Environment.SetEnvironmentVariable(ReleaseToolchain.verifiedSdkVariable, Option.toObj sdk)
        Environment.SetEnvironmentVariable(ReleaseToolchain.verifiedRuntimeVariable, Option.toObj runtime)
        action ()
    finally
        Environment.SetEnvironmentVariable(ReleaseToolchain.verifiedSdkVariable, previousSdk)
        Environment.SetEnvironmentVariable(ReleaseToolchain.verifiedRuntimeVariable, previousRuntime)

// Native evaluation refuses to start beside a publication credential, so the live case runs credential-free.
let private withoutPublicationAuthority (action: unit -> Async<unit>) =
    async {
        let names = [ "GITHUB_TOKEN"; "MCP_RELEASE_DO_UPLOAD" ]
        let previous = names |> List.map (fun name -> name, Environment.GetEnvironmentVariable name)

        try
            names |> List.iter (fun name -> Environment.SetEnvironmentVariable(name, null))
            do! action ()
        finally
            for name, value in previous do
                Environment.SetEnvironmentVariable(name, value)
    }

let private withVerifiedHandoffAsync (sdk: string option) (runtime: string option) (action: unit -> Async<unit>) =
    async {
        let previousSdk = Environment.GetEnvironmentVariable ReleaseToolchain.verifiedSdkVariable
        let previousRuntime = Environment.GetEnvironmentVariable ReleaseToolchain.verifiedRuntimeVariable

        try
            Environment.SetEnvironmentVariable(ReleaseToolchain.verifiedSdkVariable, Option.toObj sdk)
            Environment.SetEnvironmentVariable(ReleaseToolchain.verifiedRuntimeVariable, Option.toObj runtime)
            do! action ()
        finally
            Environment.SetEnvironmentVariable(ReleaseToolchain.verifiedSdkVariable, previousSdk)
            Environment.SetEnvironmentVariable(ReleaseToolchain.verifiedRuntimeVariable, previousRuntime)
    }

let private publicationHandoffTests =
    testList
        "publication handoff"
        [ testCase "the publication path refuses an absent, incomplete or invalid verified handoff" (fun _ ->
              withVerifiedHandoff None None (fun () ->
                  match ReleaseToolchain.resolvePublication repoRoot with
                  | Ok toolchain -> failtestf "a missing handoff produced '%s'" toolchain.RuntimeVersion
                  | Error error ->
                      Expect.stringContains
                          (ReleaseError.message error)
                          ReleaseToolchain.verifiedSdkVariable
                          "the missing SDK handoff variable is named")

              withVerifiedHandoff (Some fixtureSdk) None (fun () ->
                  match ReleaseToolchain.resolvePublication repoRoot with
                  | Ok toolchain -> failtestf "an incomplete handoff produced '%s'" toolchain.RuntimeVersion
                  | Error error ->
                      Expect.stringContains
                          (ReleaseError.message error)
                          ReleaseToolchain.verifiedRuntimeVariable
                          "the missing runtime handoff variable is named")

              withVerifiedHandoff (Some "relative-root") (Some fixtureRuntime) (fun () ->
                  match ReleaseToolchain.resolvePublication repoRoot with
                  | Ok toolchain -> failtestf "an invalid handoff produced '%s'" toolchain.RuntimeVersion
                  | Error error ->
                      Expect.stringContains
                          (ReleaseError.message error)
                          "plain version token"
                          "the handoff must be a plain version token"))

          testCase "the publication path validates the verified handoff against the authored owner" (fun _ ->
              let owner =
                  match ReleaseToolchain.readSdkVersion repoRoot with
                  | Ok value -> value
                  | Error error -> failwithf "the canonical global.json was rejected: %s" (ReleaseError.message error)

              withVerifiedHandoff (Some owner) (Some fixtureRuntime) (fun () ->
                  match ReleaseToolchain.resolvePublication repoRoot with
                  | Ok toolchain ->
                      Expect.equal toolchain.SdkVersion owner "the publication path uses the owner's SDK version"
                      Expect.equal toolchain.RuntimeVersion fixtureRuntime "the publication path uses the handed-over runtime"
                  | Error error -> failtestf "the verified handoff was rejected: %s" (ReleaseError.message error))

              withVerifiedHandoff (Some "0.0.1") (Some fixtureRuntime) (fun () ->
                  match ReleaseToolchain.resolvePublication repoRoot with
                  | Ok toolchain -> failtestf "a disagreeing handoff produced '%s'" toolchain.RuntimeVersion
                  | Error(IdentityMismatch(name, _, _)) ->
                      Expect.equal name ReleaseToolchain.verifiedSdkVariable "the disagreeing handoff variable is named"
                  | Error error -> failtestf "a disagreeing handoff produced the wrong failure: %s" (ReleaseError.message error))) ]

// The host's own installation is the live end of the same chain: the authored owner pins the SDK, and native evaluation
// supplies the unique framework reference's runtime. This is the only case that depends on the installed SDK.
let private liveResolutionTest =
    testCaseAsync "the installed pinned SDK resolves through native evaluation to the canonical toolchain" <|
        withoutPublicationAuthority (fun () ->
            withVerifiedHandoffAsync None None (fun () ->
                async {
                    match! ReleaseToolchain.resolveLocal repoRoot with
                    | Error error -> failtestf "the live toolchain resolution failed: %s" (ReleaseError.message error)
                    | Ok toolchain ->
                        match ReleaseToolchain.readSdkVersion repoRoot with
                        | Error error -> failtestf "the canonical global.json was rejected: %s" (ReleaseError.message error)
                        | Ok owner ->
                            Expect.equal toolchain.SdkVersion owner "the evaluated SDK is the authored owner's exact selection"
                            Expect.isTrue
                                (ReleaseToolchain.isVersionToken toolchain.RuntimeVersion)
                                "the evaluated runtime is a plain version token"
                }))

let tests =
    testList
        "release toolchain"
        [ globalJsonReaderTests
          evaluationTests
          publicationHandoffTests
          liveResolutionTest ]
