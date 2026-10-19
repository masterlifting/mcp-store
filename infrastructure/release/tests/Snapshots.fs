// role: reusable module/helper
// WHY: source-SHA reading lives in a temporary owned helper so it re-reads the bytes from the
//   canonical Config.json without inheriting the HarnessRunner private factories or the shared
//   ReleaseError rendering. The helper reads the bytes from disk only; the policy
//   parser applies the strict JSON grammar over exactly those bytes and returns the rendered
//   SHA-256 plus the parsed projection so a single source observation covers both ProjectSHA
//   and the Config-shape regression without storing anything across tests.
module Release.Tests.FixtureHelpers

open System
open System.IO
open System.Security.Cryptography

type ConfigSnapshot =
    { ComponentId: string
      ConfigPath: string
      ConfigBytes: byte[]
      ConfigSha256: string
      Parsed: ReleasePolicy.PolicyProjection }

let private sha256Hex (bytes: byte[]) =
    SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()

let private canonicalConfigPath (root: string) (componentId: string) =
    Path.Combine(root, componentId, "release", "Config.json")

let readCanonicalConfig (root: string) (componentId: string) =
    let path = canonicalConfigPath root componentId
    let bytes = File.ReadAllBytes path
    match ReleasePolicy.parseBytes path bytes with
    | Ok projection ->
        { ComponentId = componentId
          ConfigPath = path
          ConfigBytes = bytes
          ConfigSha256 = sha256Hex bytes
          Parsed = projection }
    | Error error -> failwithf "%s canonical Config.json failed: %s" componentId (SharedFailure.ReleaseError.message error)

let readConfigFile (path: string) =
    let bytes = File.ReadAllBytes path
    match ReleasePolicy.parseBytes path bytes with
    | Ok projection ->
        { ComponentId = projection.ComponentId
          ConfigPath = path
          ConfigBytes = bytes
          ConfigSha256 = sha256Hex bytes
          Parsed = projection }
    | Error error -> failwithf "%s Config.json failed: %s" path (SharedFailure.ReleaseError.message error)

let parseConfigBytes (sourcePath: string) (bytes: byte[]) =
    match ReleasePolicy.parseBytes sourcePath bytes with
    | Ok projection -> Ok projection
    | Error error -> Error(SharedFailure.ReleaseError.message error)