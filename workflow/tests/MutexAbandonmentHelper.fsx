// Test helper for the abandoned-owner recovery process-boundary test. It opens
// the same OS-named runtime coordination mutex the packaged Workflow producer
// uses for a given sidecar path, acquires it, signals readiness, and then blocks
// forever. The parent test kills this process while it owns the mutex, which
// makes the mutex abandoned for the next packaged Workflow process to acquire.

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading

// Mirrors Workflow.coordinationName exactly: canonical full path, uppercase on
// Windows, SHA-256 hex, then the per-platform mutex namespace prefix.
let coordinationName (path: string) =
    let canonical =
        let fullPath = Path.GetFullPath path
        if OperatingSystem.IsWindows() then fullPath.ToUpperInvariant() else fullPath

    let digest = SHA256.HashData(Encoding.UTF8.GetBytes canonical) |> Convert.ToHexString
    if OperatingSystem.IsWindows() then $"Local\\Mcp.Workflow.Runtime.{digest}" else $"Mcp.Workflow.Runtime.{digest}"

let arguments = fsi.CommandLineArgs |> Array.skip 1

if arguments.Length <> 2 then
    Console.Error.WriteLine "usage: MutexAbandonmentHelper.fsx <sidecar-path> <ready-marker-path>"
    exit 2

let sidecarPath = arguments.[0]
let readyMarker = arguments.[1]

// Deliberately not disposed: the parent test kills this process while it owns
// the mutex so the next waiter observes abandonment.
let mutex = new Mutex(false, coordinationName sidecarPath)
mutex.WaitOne() |> ignore
File.WriteAllText(readyMarker, "held")
printfn "held:%s" (coordinationName sidecarPath)
Console.Out.Flush()
Thread.Sleep Timeout.Infinite
