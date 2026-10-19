// role: reusable module/helper
// Lowercase SHA-256 over bytes, shared by every resolver trust boundary.
module ResolverDigest

open System
open System.Security.Cryptography

let sha256 (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
