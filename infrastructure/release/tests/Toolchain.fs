module Release.Tests.Toolchain

open System
open System.Diagnostics
open System.IO
open System.Text
open Expecto
open SharedFailure
open ReleaseTestProcess

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

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

let private withTempRootAsync (action: string -> Async<unit>) =
    async {
        let root = Path.Combine(Path.GetTempPath(), "mcp-release-toolchain-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore

        try
            do! action root
        finally
            try
                if Directory.Exists root then Directory.Delete(root, true)
            with _ ->
                ()
    }

let private writeFixture (root: string) (relativePath: string) (contents: string) =
    let path = Path.Combine(root, relativePath)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, contents)
    path

let private isOk result =
    match result with
    | Ok _ -> true
    | Error _ -> false

// The strict `global.json` shape, so a case varies only the field it targets.
let private globalJsonDocument version rollForward allowPrerelease =
    sprintf
        "{\n  \"sdk\": {\n    \"version\": \"%s\",\n    \"rollForward\": \"%s\",\n    \"allowPrerelease\": %s\n  }\n}\n"
        version
        rollForward
        (if allowPrerelease then "true" else "false")

let private parseGlobalJsonText (name: string) (contents: string) =
    ReleaseToolchain.parseGlobalJson (Path.Combine("fixture", name)) (Encoding.UTF8.GetBytes contents)

let private parseBuildPropsText (name: string) (contents: string) =
    ReleaseToolchain.parseBuildProps (Path.Combine("fixture", name)) (Encoding.UTF8.GetBytes contents)

let private parseBundledVersionsText (name: string) (targetFramework: string) (contents: string) =
    ReleaseToolchain.parseBundledVersions
        (Path.Combine("fixture", name))
        targetFramework
        (Encoding.UTF8.GetBytes contents)

let private expectRejected description (fragment: string) result =
    match result with
    | Ok _ -> failtestf "%s was accepted" description
    | Error error ->
        Expect.stringContains
            (ReleaseError.message error)
            fragment
            (sprintf "%s is rejected with the named reason" description)

let private buildPropsDocument framework language expecto =
    sprintf
        "<Project>\n  <PropertyGroup>\n    <TargetFramework>%s</TargetFramework>\n    <LangVersion>%s</LangVersion>\n    <ExpectoVersion>%s</ExpectoVersion>\n  </PropertyGroup>\n</Project>\n"
        framework
        language
        expecto

// A synthetic SDK props document. `KnownRuntimePack` carries the same `Include` and `TargetFramework` as the framework
// reference, so a filter that matched on `Include` alone would report two matches here.
let private bundledVersionsDocument targetFramework runtimeVersion =
    sprintf
        "<Project><ItemGroup><KnownFrameworkReference Include=\"Microsoft.NETCore.App\" TargetFramework=\"%s\" DefaultRuntimeFrameworkVersion=\"%s\" /><KnownRuntimePack Include=\"Microsoft.NETCore.App\" TargetFramework=\"%s\" LatestRuntimeFrameworkVersion=\"99.0.1\" /><BundledNETCoreAppTargetFramework>%s</BundledNETCoreAppTargetFramework></ItemGroup></Project>"
        targetFramework
        runtimeVersion
        targetFramework
        targetFramework

let private globalJsonReaderTests =
    testList
        "global.json strict reader"
        [ testCase "the canonical repository owner names the fixture's SDK version" (fun _ ->
              let path = ReleaseToolchain.globalJsonPath repoRoot

              match ReleaseToolchain.parseGlobalJson path (File.ReadAllBytes path) with
              | Ok version -> Expect.equal version Fixture.canonicalToolchain.SdkVersion "the owner and the resolved toolchain agree"
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

let private buildPropsTests =
    testList
        "Directory.Build.props strict reader"
        [ testCase "the canonical repository owner is accepted" (fun _ ->
              match ReleaseToolchain.readBuildProperties repoRoot with
              | Ok properties ->
                  Expect.equal properties.TargetFramework "net11.0" "the owner names the target framework"
                  Expect.equal properties.LangVersion "9.0" "the owner names the language version"
                  Expect.isTrue
                      (ReleaseToolchain.isVersionToken properties.ExpectoVersion)
                      "the owner names a plain Expecto version token"
              | Error error -> failtestf "the canonical Directory.Build.props was rejected: %s" (ReleaseError.message error))

          testCase "a strict document is accepted" (fun _ ->
              match parseBuildPropsText "strict.xml" (buildPropsDocument "net11.0" "9.0" "10.2.3") with
              | Ok properties -> Expect.equal properties.ExpectoVersion "10.2.3" "the declared Expecto version is returned"
              | Error error -> failtestf "a strict document was rejected: %s" (ReleaseError.message error))

          testCase "an unknown property is rejected" (fun _ ->
              let contents =
                  "<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework><LangVersion>9.0</LangVersion><ExpectoVersion>10.2.3</ExpectoVersion><Nullable>enable</Nullable></PropertyGroup></Project>"

              parseBuildPropsText "unknown.xml" contents |> expectRejected "an unknown property" "not allowed")

          testCase "a repeated property is rejected" (fun _ ->
              let contents =
                  "<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework><TargetFramework>net11.0</TargetFramework><LangVersion>9.0</LangVersion><ExpectoVersion>10.2.3</ExpectoVersion></PropertyGroup></Project>"

              parseBuildPropsText "repeated.xml" contents |> expectRejected "a repeated property" "is repeated")

          testCase "a missing property is rejected" (fun _ ->
              let contents =
                  "<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework><LangVersion>9.0</LangVersion></PropertyGroup></Project>"

              parseBuildPropsText "missing.xml" contents |> expectRejected "a missing property" "is missing")

          testCase "a value with surrounding whitespace is rejected" (fun _ ->
              let contents =
                  "<Project><PropertyGroup><TargetFramework> net11.0</TargetFramework><LangVersion>9.0</LangVersion><ExpectoVersion>10.2.3</ExpectoVersion></PropertyGroup></Project>"

              parseBuildPropsText "whitespace.xml" contents
              |> expectRejected "a padded value" "without surrounding whitespace")

          testCase "a second property group is rejected" (fun _ ->
              let contents =
                  "<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework><LangVersion>9.0</LangVersion><ExpectoVersion>10.2.3</ExpectoVersion></PropertyGroup><PropertyGroup /></Project>"

              parseBuildPropsText "two-groups.xml" contents
              |> expectRejected "a second property group" "exactly one")

          testCase "a value that is not a plain framework moniker is rejected" (fun _ ->
              parseBuildPropsText "framework.xml" (buildPropsDocument "net11" "9.0" "10.2.3")
              |> expectRejected "a framework without a minor version" "plain netX.Y moniker")

          testCase "a value that is not a plain language version is rejected" (fun _ ->
              parseBuildPropsText "language.xml" (buildPropsDocument "net11.0" "preview" "10.2.3")
              |> expectRejected "a language name instead of a version" "plain X.Y version")

          testCase "a missing owner fails closed" (fun _ ->
              withTempRoot (fun root ->
                  match ReleaseToolchain.readBuildProperties root with
                  | Ok properties -> failtestf "a missing Directory.Build.props produced '%s'" properties.TargetFramework
                  | Error(MissingArtifact path) -> Expect.stringContains path "Directory.Build.props" "the missing owner is named"
                  | Error error -> failtestf "a missing owner produced the wrong failure: %s" (ReleaseError.message error))) ]

let private bundledVersionsTests =
    testList
        "SDK bundled-versions reader"
        [ testCase "exactly one matching framework reference is accepted" (fun _ ->
              match parseBundledVersionsText "bundled.xml" "net11.0" (bundledVersionsDocument "net11.0" "11.0.42") with
              | Ok facts ->
                  Expect.equal facts.DefaultRuntimeFrameworkVersion "11.0.42" "the derived runtime is returned"
                  Expect.equal facts.BundledTargetFramework "net11.0" "the SDK's own framework is returned"
              | Error error -> failtestf "a matching reference was rejected: %s" (ReleaseError.message error))

          testCase "zero matching references fail closed" (fun _ ->
              parseBundledVersionsText "no-match.xml" "net11.0" (bundledVersionsDocument "net10.0" "10.0.42")
              |> expectRejected "a reference for another framework" "no KnownFrameworkReference")

          testCase "two matching references fail closed" (fun _ ->
              let contents =
                  "<Project><ItemGroup><KnownFrameworkReference Include=\"Microsoft.NETCore.App\" TargetFramework=\"net11.0\" DefaultRuntimeFrameworkVersion=\"11.0.42\" /><KnownFrameworkReference Include=\"Microsoft.NETCore.App\" TargetFramework=\"net11.0\" DefaultRuntimeFrameworkVersion=\"11.0.43\" /><BundledNETCoreAppTargetFramework>net11.0</BundledNETCoreAppTargetFramework></ItemGroup></Project>"

              parseBundledVersionsText "two-match.xml" "net11.0" contents
              |> expectRejected "two matching references" "more than one KnownFrameworkReference")

          testCase "a matching reference without DefaultRuntimeFrameworkVersion fails closed" (fun _ ->
              let contents =
                  "<Project><ItemGroup><KnownFrameworkReference Include=\"Microsoft.NETCore.App\" TargetFramework=\"net11.0\" /><BundledNETCoreAppTargetFramework>net11.0</BundledNETCoreAppTargetFramework></ItemGroup></Project>"

              parseBundledVersionsText "no-runtime.xml" "net11.0" contents
              |> expectRejected "a reference without the runtime attribute" "carries no DefaultRuntimeFrameworkVersion")

          testCase "a KnownRuntimePack is never mistaken for a KnownFrameworkReference" (fun _ ->
              let contents =
                  "<Project><ItemGroup><KnownRuntimePack Include=\"Microsoft.NETCore.App\" TargetFramework=\"net11.0\" LatestRuntimeFrameworkVersion=\"11.0.42\" /><BundledNETCoreAppTargetFramework>net11.0</BundledNETCoreAppTargetFramework></ItemGroup></Project>"

              parseBundledVersionsText "runtime-pack.xml" "net11.0" contents
              |> expectRejected "a KnownRuntimePack only document" "no KnownFrameworkReference")

          testCase "the SDK's own framework must equal the owned framework" (fun _ ->
              let contents =
                  "<Project><ItemGroup><KnownFrameworkReference Include=\"Microsoft.NETCore.App\" TargetFramework=\"net11.0\" DefaultRuntimeFrameworkVersion=\"11.0.42\" /><BundledNETCoreAppTargetFramework>net10.0</BundledNETCoreAppTargetFramework></ItemGroup></Project>"

              parseBundledVersionsText "framework-drift.xml" "net11.0" contents
              |> expectRejected "a drifted bundled framework" "differs from the owned target framework")

          testCase "more than one bundled framework fails closed" (fun _ ->
              let contents =
                  "<Project><ItemGroup><KnownFrameworkReference Include=\"Microsoft.NETCore.App\" TargetFramework=\"net11.0\" DefaultRuntimeFrameworkVersion=\"11.0.42\" /><BundledNETCoreAppTargetFramework>net11.0</BundledNETCoreAppTargetFramework><BundledNETCoreAppTargetFramework>net11.0</BundledNETCoreAppTargetFramework></ItemGroup></Project>"

              parseBundledVersionsText "two-bundled.xml" "net11.0" contents
              |> expectRejected "two bundled frameworks" "exactly one BundledNETCoreAppTargetFramework")

          testCase "a runtime value that is not a plain token fails closed" (fun _ ->
              parseBundledVersionsText "runtime-token.xml" "net11.0" (bundledVersionsDocument "net11.0" "../11.0.0")
              |> expectRejected "a traversal runtime value" "plain version token")

          testCase "an oversized document fails closed" (fun _ ->
              parseBundledVersionsText "oversized.xml" "net11.0" (String('x', ReleaseToolchain.maxBundledVersionsBytes + 1))
              |> expectRejected "an oversized bundled document" "bounded maximum") ]

let private seedSyntheticRoot (root: string) (sdkVersion: string) (targetFramework: string) (runtimeVersion: string) =
    writeFixture root "global.json" (globalJsonDocument sdkVersion "disable" false) |> ignore
    writeFixture root "Directory.Build.props" (buildPropsDocument targetFramework "9.0" "10.2.3") |> ignore

    writeFixture
        root
        (Path.Combine("sdk", sdkVersion, ReleaseToolchain.bundledVersionsName))
        (bundledVersionsDocument targetFramework runtimeVersion)
    |> ignore

let private sdkRootResolutionTests =
    testList
        "SDK root resolution"
        [ testCase "the pinned version resolves the runtime from the explicit root" (fun _ ->
              withTempRoot (fun root ->
                  seedSyntheticRoot root "11.0.100" "net11.0" "11.0.42"

                  match ReleaseToolchain.resolveWithRoot root (Path.Combine(root, "sdk")) with
                  | Ok toolchain ->
                      Expect.equal toolchain.SdkVersion "11.0.100" "the declared SDK version is selected"
                      Expect.equal toolchain.RuntimeVersion "11.0.42" "the runtime is derived from the SDK"
                  | Error error -> failtestf "an explicit rooted resolution failed: %s" (ReleaseError.message error)))

          testCase "a relative SDK root is rejected" (fun _ ->
              withTempRoot (fun root ->
                  seedSyntheticRoot root "11.0.100" "net11.0" "11.0.42"

                  match ReleaseToolchain.resolveWithRoot root "sdk" with
                  | Ok toolchain -> failtestf "a relative root produced '%s'" toolchain.RuntimeVersion
                  | Error error ->
                      Expect.stringContains (ReleaseError.message error) "must be an absolute path" "the root must be absolute"))

          testCase "a missing bundled document fails closed" (fun _ ->
              withTempRoot (fun root ->
                  writeFixture root "global.json" (globalJsonDocument "11.0.100" "disable" false) |> ignore
                  writeFixture root "Directory.Build.props" (buildPropsDocument "net11.0" "9.0" "10.2.3") |> ignore

                  match ReleaseToolchain.resolveWithRoot root (Path.Combine(root, "sdk")) with
                  | Ok toolchain -> failtestf "a missing bundled document produced '%s'" toolchain.RuntimeVersion
                  | Error(MissingArtifact path) -> Expect.stringContains path "BundledVersions" "the missing document is named"
                  | Error error -> failtestf "a missing document produced the wrong failure: %s" (ReleaseError.message error)))

          testCase "the TFM input is the owned framework, so a drifted framework finds no reference" (fun _ ->
              withTempRoot (fun root ->
                  writeFixture root "global.json" (globalJsonDocument "11.0.100" "disable" false) |> ignore
                  writeFixture root "Directory.Build.props" (buildPropsDocument "net10.0" "9.0" "10.2.3") |> ignore

                  writeFixture
                      root
                      (Path.Combine("sdk", "11.0.100", ReleaseToolchain.bundledVersionsName))
                      (bundledVersionsDocument "net11.0" "11.0.42")
                  |> ignore

                  match ReleaseToolchain.resolveWithRoot root (Path.Combine(root, "sdk")) with
                  | Ok toolchain -> failtestf "a drifted framework produced '%s'" toolchain.RuntimeVersion
                  | Error error ->
                      Expect.stringContains (ReleaseError.message error) "no KnownFrameworkReference" "the owned framework drives the lookup"))

          testCase "the pinned version resolves under exactly one root" (fun _ ->
              withTempRoot (fun root ->
                  let first = Path.Combine(root, "sdk-one")
                  let listing = sprintf "11.0.100 [%s]\n10.0.115 [%s]\n" first (Path.Combine(root, "sdk-two"))

                  match ReleaseToolchain.selectRoot "11.0.100" (ReleaseToolchain.parseSdkListing listing) with
                  | Ok selected -> Expect.equal selected (Path.GetFullPath first) "the single matching root is selected"
                  | Error error -> failtestf "a single matching root was rejected: %s" (ReleaseError.message error)))

          testCase "the pinned version under two roots fails closed" (fun _ ->
              withTempRoot (fun root ->
                  let listing =
                      sprintf
                          "11.0.100 [%s]\n11.0.100 [%s]\n"
                          (Path.Combine(root, "sdk-one"))
                          (Path.Combine(root, "sdk-two"))

                  match ReleaseToolchain.selectRoot "11.0.100" (ReleaseToolchain.parseSdkListing listing) with
                  | Ok selected -> failtestf "an ambiguous pin resolved to '%s'" selected
                  | Error error ->
                      Expect.stringContains (ReleaseError.message error) "more than one root" "the ambiguity is named"))

          testCase "the pinned version that is not installed fails closed" (fun _ ->
              withTempRoot (fun root ->
                  let listing = sprintf "10.0.115 [%s]\n" (Path.Combine(root, "sdk-one"))

                  match ReleaseToolchain.selectRoot "11.0.100" (ReleaseToolchain.parseSdkListing listing) with
                  | Ok selected -> failtestf "an absent pin resolved to '%s'" selected
                  | Error error ->
                      Expect.stringContains (ReleaseError.message error) "is not installed" "the absent pin is named"))

          testCase "the same root listed twice is one root" (fun _ ->
              withTempRoot (fun root ->
                  let first = Path.Combine(root, "sdk-one")

                  let listing =
                      sprintf "11.0.100 [%s]\n11.0.100 [%s%c]\n" first first Path.DirectorySeparatorChar

                  match ReleaseToolchain.selectRoot "11.0.100" (ReleaseToolchain.parseSdkListing listing) with
                  | Ok selected -> Expect.equal selected (Path.GetFullPath first) "one normalized root remains"
                  | Error error -> failtestf "a repeated identical root was rejected: %s" (ReleaseError.message error)))

          testCase "a line that is not an installation entry is ignored" (fun _ ->
              let listing =
                  "warning: something happened\n11.0.100 [C:\\Program Files\\dotnet\\sdk]\nnot a listing line\n"

              match ReleaseToolchain.parseSdkListing listing with
              | [ install ] ->
                  Expect.equal install.Version "11.0.100" "the version is parsed"
                  Expect.equal install.Root "C:\\Program Files\\dotnet\\sdk" "the bracketed root is parsed"
              | parsed -> failtestf "the listing produced %d entries" (List.length parsed))

          testCase "the local listing drives the same derivation as the explicit root" (fun _ ->
              withTempRoot (fun root ->
                  seedSyntheticRoot root "11.0.100" "net11.0" "11.0.42"
                  let sdkRoot = Path.Combine(root, "sdk")
                  let listing = sprintf "11.0.100 [%s]\n" sdkRoot

                  match ReleaseToolchain.resolveFromListing root listing None with
                  | Ok toolchain -> Expect.equal toolchain.RuntimeVersion "11.0.42" "the listing root derives the runtime"
                  | Error error -> failtestf "the listing resolution failed: %s" (ReleaseError.message error)

                  match ReleaseToolchain.resolveFromListing root listing (Some(sdkRoot + string Path.DirectorySeparatorChar)) with
                  | Ok toolchain -> Expect.equal toolchain.SdkVersion "11.0.100" "an agreeing handoff keeps the resolution"
                  | Error error -> failtestf "an agreeing handoff was rejected: %s" (ReleaseError.message error)

                  match ReleaseToolchain.resolveFromListing root listing (Some(Path.Combine(root, "sdk-other"))) with
                  | Ok toolchain -> failtestf "a disagreeing handoff produced '%s'" toolchain.RuntimeVersion
                  | Error(IdentityMismatch(name, _, _)) ->
                      Expect.equal name ReleaseToolchain.sdkRootVariable "the disagreeing handoff is named"
                  | Error error -> failtestf "a disagreeing handoff produced the wrong failure: %s" (ReleaseError.message error)))

          testCase "the publication path refuses without the explicit handoff" (fun _ ->
              let previous = Environment.GetEnvironmentVariable ReleaseToolchain.sdkRootVariable

              try
                  Environment.SetEnvironmentVariable(ReleaseToolchain.sdkRootVariable, null)

                  match ReleaseToolchain.resolvePublication repoRoot with
                  | Ok toolchain -> failtestf "a missing handoff produced '%s'" toolchain.RuntimeVersion
                  | Error error ->
                      Expect.stringContains
                          (ReleaseError.message error)
                          ReleaseToolchain.sdkRootVariable
                          "the missing handoff variable is named"

                  Environment.SetEnvironmentVariable(ReleaseToolchain.sdkRootVariable, "relative-root")

                  match ReleaseToolchain.resolvePublication repoRoot with
                  | Ok toolchain -> failtestf "a relative handoff produced '%s'" toolchain.RuntimeVersion
                  | Error error -> Expect.stringContains (ReleaseError.message error) "absolute path" "the handoff must be absolute"
              finally
                  Environment.SetEnvironmentVariable(ReleaseToolchain.sdkRootVariable, previous))

          testCase "the publication path derives the toolchain from the handed-over root" (fun _ ->
              let previous = Environment.GetEnvironmentVariable ReleaseToolchain.sdkRootVariable

              try
                  withTempRoot (fun root ->
                      let owner = Fixture.canonicalToolchain.SdkVersion

                      let properties =
                          match ReleaseToolchain.readBuildProperties repoRoot with
                          | Ok value -> value
                          | Error error -> failwithf "the canonical Directory.Build.props was rejected: %s" (ReleaseError.message error)

                      writeFixture
                          root
                          (Path.Combine("sdk", owner, ReleaseToolchain.bundledVersionsName))
                          (bundledVersionsDocument properties.TargetFramework "11.0.42")
                      |> ignore

                      let sdkRoot = Path.Combine(root, "sdk")
                      Environment.SetEnvironmentVariable(ReleaseToolchain.sdkRootVariable, sdkRoot)

                      match ReleaseToolchain.resolvePublication repoRoot with
                      | Ok toolchain ->
                          Expect.equal toolchain.SdkVersion owner "the publication path uses the owner's SDK version"
                          Expect.equal toolchain.RuntimeVersion "11.0.42" "the publication path derives the runtime"
                      | Error error -> failtestf "the handoff resolution failed: %s" (ReleaseError.message error))
              finally
                  Environment.SetEnvironmentVariable(ReleaseToolchain.sdkRootVariable, previous)) ]

// The host's own installation is the live end of the same chain the synthetic cases cover: listing, single root,
// bundled facts, derived runtime.
let private liveResolutionTest =
    testCaseAsync "the installed pinned SDK resolves to one root and the canonical toolchain" <| async {
        let! listing = ReleaseToolchain.discoverSdkListing repoRoot

        match listing with
        | Error error -> failtestf "the SDK listing failed: %s" (ReleaseError.message error)
        | Ok output ->
            let version =
                match ReleaseToolchain.readSdkVersion repoRoot with
                | Ok value -> value
                | Error error -> failwithf "the canonical global.json was rejected: %s" (ReleaseError.message error)

            match ReleaseToolchain.selectRoot version (ReleaseToolchain.parseSdkListing output) with
            | Error error -> failtestf "the pinned SDK did not resolve to one root: %s" (ReleaseError.message error)
            | Ok root ->
                match ReleaseToolchain.resolveWithRoot repoRoot root with
                | Ok toolchain ->
                    Expect.equal toolchain Fixture.canonicalToolchain "the live resolution is the canonical fixture toolchain"
                | Error error -> failtestf "the live resolution failed: %s" (ReleaseError.message error)
    }

// The PowerShell mirror `Toolchain.ps1` must accept and reject the same inputs as the compiled reader. Each fixture file
// is evaluated on both sides; only the verdict is compared, because the two carriers own their own message wording.
let private mirrorScript =
    """
param([Parameter(Mandatory = $true)][string]$Toolchain, [Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$TargetFramework)

. $Toolchain

function Write-Verdict {
    param([string]$Kind, [string]$Name, [bool]$Accepted)
    $state = if ($Accepted) { "ok" } else { "error" }
    "{0}/{1}`t{2}" -f $Kind, $Name, $state
}

foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $Root "global") -File | Sort-Object Name)) {
    $accepted = $true
    try { $null = ConvertFrom-GlobalJsonBytes -Path $file.FullName -Bytes ([System.IO.File]::ReadAllBytes($file.FullName)) } catch { $accepted = $false }
    Write-Verdict "global" $file.Name $accepted
}

foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $Root "props") -File | Sort-Object Name)) {
    $accepted = $true
    try { $null = ConvertFrom-BuildPropsBytes -Path $file.FullName -Bytes ([System.IO.File]::ReadAllBytes($file.FullName)) } catch { $accepted = $false }
    Write-Verdict "props" $file.Name $accepted
}

foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $Root "bundled") -File | Sort-Object Name)) {
    $accepted = $true
    try { $null = ConvertFrom-BundledVersionsBytes -Path $file.FullName -TargetFramework $TargetFramework -Bytes ([System.IO.File]::ReadAllBytes($file.FullName)) } catch { $accepted = $false }
    Write-Verdict "bundled" $file.Name $accepted
}

foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $Root "token") -File | Sort-Object Name)) {
    $accepted = Test-ToolchainVersionToken ([System.IO.File]::ReadAllText($file.FullName))
    Write-Verdict "token" $file.Name $accepted
}

foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $Root "listing") -File | Sort-Object Name)) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    $version = $lines[0].Substring("version=".Length)
    $accepted = $true
    try {
        $installs = @(ConvertFrom-SdkListing -Lines ($lines[1..($lines.Length - 1)]))
        $null = Select-ToolchainSdkRoot -Version $version -Installs $installs
    } catch { $accepted = $false }
    Write-Verdict "listing" $file.Name $accepted
}
"""

let private mirrorFixtures (root: string) : (string * string) list =
    let one = Path.Combine(root, "sdk-one")
    let two = Path.Combine(root, "sdk-two")

    [ "global/canonical.json", globalJsonDocument "11.0.100" "disable" false
      "global/duplicate-key.json",
      "{ \"sdk\": { \"version\": \"11.0.100\", \"version\": \"11.0.100\", \"rollForward\": \"disable\", \"allowPrerelease\": false } }"
      "global/unknown-property.json",
      "{ \"sdk\": { \"version\": \"11.0.100\", \"rollForward\": \"disable\", \"allowPrerelease\": false }, \"msbuild\": {} }"
      "global/unknown-sdk-property.json",
      "{ \"sdk\": { \"version\": \"11.0.100\", \"rollForward\": \"disable\", \"allowPrerelease\": false, \"path\": \"x\" } }"
      "global/missing-property.json", "{ \"sdk\": { \"version\": \"11.0.100\", \"allowPrerelease\": false } }"
      "global/loose-rollforward.json", globalJsonDocument "11.0.100" "latestPatch" false
      "global/prerelease.json", globalJsonDocument "11.0.100" "disable" true
      "global/traversal.json", globalJsonDocument "../outside" "disable" false
      "global/numeric-version.json",
      "{ \"sdk\": { \"version\": 1100100, \"rollForward\": \"disable\", \"allowPrerelease\": false } }"
      "global/trailing-comma.json", "{ \"sdk\": { \"version\": \"11.0.100\", }, }"
      "global/oversized.json", String('x', ReleaseToolchain.maxOwnerBytes + 1)
      "props/canonical.xml", buildPropsDocument "net11.0" "9.0" "10.2.3"
      "props/unknown-property.xml",
      "<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework><LangVersion>9.0</LangVersion><ExpectoVersion>10.2.3</ExpectoVersion><Nullable>enable</Nullable></PropertyGroup></Project>"
      "props/repeated-property.xml",
      "<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework><TargetFramework>net11.0</TargetFramework><LangVersion>9.0</LangVersion><ExpectoVersion>10.2.3</ExpectoVersion></PropertyGroup></Project>"
      "props/missing-property.xml",
      "<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework><LangVersion>9.0</LangVersion></PropertyGroup></Project>"
      "props/whitespace.xml",
      "<Project><PropertyGroup><TargetFramework> net11.0</TargetFramework><LangVersion>9.0</LangVersion><ExpectoVersion>10.2.3</ExpectoVersion></PropertyGroup></Project>"
      "props/two-groups.xml",
      "<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework><LangVersion>9.0</LangVersion><ExpectoVersion>10.2.3</ExpectoVersion></PropertyGroup><PropertyGroup /></Project>"
      "props/bad-framework.xml", buildPropsDocument "net11" "9.0" "10.2.3"
      "bundled/canonical.xml", bundledVersionsDocument "net11.0" "11.0.42"
      "bundled/no-match.xml", bundledVersionsDocument "net10.0" "10.0.42"
      "bundled/runtime-pack-only.xml",
      "<Project><ItemGroup><KnownRuntimePack Include=\"Microsoft.NETCore.App\" TargetFramework=\"net11.0\" LatestRuntimeFrameworkVersion=\"11.0.42\" /><BundledNETCoreAppTargetFramework>net11.0</BundledNETCoreAppTargetFramework></ItemGroup></Project>"
      "bundled/framework-drift.xml",
      "<Project><ItemGroup><KnownFrameworkReference Include=\"Microsoft.NETCore.App\" TargetFramework=\"net11.0\" DefaultRuntimeFrameworkVersion=\"11.0.42\" /><BundledNETCoreAppTargetFramework>net10.0</BundledNETCoreAppTargetFramework></ItemGroup></Project>"
      "bundled/two-matches.xml",
      "<Project><ItemGroup><KnownFrameworkReference Include=\"Microsoft.NETCore.App\" TargetFramework=\"net11.0\" DefaultRuntimeFrameworkVersion=\"11.0.42\" /><KnownFrameworkReference Include=\"Microsoft.NETCore.App\" TargetFramework=\"net11.0\" DefaultRuntimeFrameworkVersion=\"11.0.43\" /><BundledNETCoreAppTargetFramework>net11.0</BundledNETCoreAppTargetFramework></ItemGroup></Project>"
      "token/plain", "11.0.100"
      "token/prerelease", "11.0.100-rc.1.26425.128"
      "token/parent", ".."
      "token/forward-slash", "11/0/100"
      "token/back-slash", "11\\0\\100"
      "token/empty-component", "11..100"
      "token/trailing-dot", "11.0.100."
      "token/drive-prefix", "C:11.0.100"
      "token/bare-prerelease", "-rc.1"
      "token/trailing-newline", "11.0.100\n"
      "listing/single.txt", sprintf "version=11.0.100\n11.0.100 [%s]\n10.0.115 [%s]\n" one two
      "listing/absent.txt", sprintf "version=11.0.100\n10.0.115 [%s]\n" one
      "listing/two-roots.txt", sprintf "version=11.0.100\n11.0.100 [%s]\n11.0.100 [%s]\n" one two
      "listing/repeated-root.txt",
      sprintf "version=11.0.100\n11.0.100 [%s]\n11.0.100 [%s%c]\n" one one Path.DirectorySeparatorChar ]

let private compiledVerdict (root: string) (relativePath: string) : bool =
    let path = Path.Combine(root, relativePath)
    let bytes = File.ReadAllBytes path
    let parts = relativePath.Split('/')

    match parts[0] with
    | "global" -> ReleaseToolchain.parseGlobalJson path bytes |> isOk
    | "props" -> ReleaseToolchain.parseBuildProps path bytes |> isOk
    | "bundled" -> ReleaseToolchain.parseBundledVersions path "net11.0" bytes |> isOk
    | "token" -> ReleaseToolchain.isVersionToken (File.ReadAllText path)
    | "listing" ->
        let lines = File.ReadAllLines path
        let version = lines[0].Substring("version=".Length)
        let listing = String.concat "\n" (lines |> Array.skip 1)

        ReleaseToolchain.parseSdkListing listing
        |> ReleaseToolchain.selectRoot version
        |> isOk
    | kind -> failwithf "unknown mirror fixture kind '%s'" kind

let private parseVerdicts (stdout: string) : Map<string, bool> =
    stdout.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun line ->
        match line.Split('\t') with
        | [| name; verdict |] when not (String.IsNullOrWhiteSpace name) ->
            Some(name.Trim(), verdict.Trim() = "ok")
        | _ -> None)
    |> Map.ofArray

let private mirrorAgreementTests =
    testCaseAsync "the compiled reader and the PowerShell mirror agree on the same inputs" <| async {
        do!
            withTempRootAsync (fun root ->
                async {
                    let fixtures = mirrorFixtures root

                    for relativePath, contents in fixtures do
                        writeFixture root relativePath contents |> ignore

                    let scriptPath = writeFixture root "Mirror.ps1" mirrorScript

                    let info = ProcessStartInfo("pwsh")
                    info.WorkingDirectory <- repoRoot
                    info.UseShellExecute <- false
                    info.RedirectStandardOutput <- true
                    info.RedirectStandardError <- true

                    [ "-NoProfile"
                      "-File"
                      scriptPath
                      "-Toolchain"
                      Path.Combine(repoRoot, "infrastructure", "release", "Toolchain.ps1")
                      "-Root"
                      root
                      "-TargetFramework"
                      "net11.0" ]
                    |> List.iter info.ArgumentList.Add

                    match! ReleaseTestProcess.capture 60000 info with
                    | Error error -> failtestf "the PowerShell mirror could not run: %s" error
                    | Ok (code, _, stderr) when code <> 0 -> failtestf "the PowerShell mirror exited %d: %s" code stderr
                    | Ok (_, stdout, _) ->
                        let observed = parseVerdicts stdout

                        for relativePath, _ in fixtures do
                            let expected = compiledVerdict root relativePath

                            match Map.tryFind relativePath observed with
                            | None -> failtestf "the PowerShell mirror reported no verdict for '%s'" relativePath
                            | Some actual ->
                                Expect.equal actual expected (sprintf "compiled and PowerShell verdicts differ for '%s'" relativePath)

                        Expect.equal (Map.count observed) (List.length fixtures) "the mirror reported one verdict per case"
                        Expect.isGreaterThan (observed |> Map.toList |> List.filter snd |> List.length) 3 "the fixtures include accepted inputs"
                        Expect.isGreaterThan (observed |> Map.toList |> List.filter (snd >> not) |> List.length) 12 "the fixtures include rejected inputs"
                })
    }

let tests =
    testList
        "release toolchain"
        [ globalJsonReaderTests
          buildPropsTests
          bundledVersionsTests
          sdkRootResolutionTests
          liveResolutionTest
          mirrorAgreementTests ]
