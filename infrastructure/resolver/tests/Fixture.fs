// Canonical resolver fixtures come from the shared distribution renderer and the strict ZIP entry shape the resolver accepts, so no test invents a competing schema or archive layout.
module Resolver.Tests.Fixture

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open SharedFailure
open DistributionContract
open DistributionPin

type Spec =
    { Component: string
      Version: string
      Revision: string
      ArchiveName: string
      Entrypoint: string
      RuntimeVersion: string }

let repository = "https://github.com/masterlifting/mcp-store"

let dotnetSpec =
    { Component = "dotnet"
      Version = "1.0.5"
      Revision = "fc0aefa9db0fb7262ee6ca1b54bd6bbb752b8b01"
      ArchiveName = "v1.0.5.zip"
      Entrypoint = "Mcp.Dotnet.dll"
      RuntimeVersion = "11.0.0" }

let workflowSpec =
    { Component = "workflow"
      Version = "1.1.0"
      Revision = "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
      ArchiveName = "v1.1.0.zip"
      Entrypoint = "Mcp.Workflow.dll"
      RuntimeVersion = "11.0.0" }

let assetUri (spec: Spec) : string =
    $"{repository}/releases/download/{spec.Component}/{spec.ArchiveName}"

let sha256 (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let utf8 (value: string) : byte[] = Encoding.UTF8.GetBytes value

// The packaged runtime configuration is the authoritative prerequisite, so the fixture bytes declare the framework and version the manifest derives rather than assuming one.
let private runtimeConfigNode (version: string) : JsonObject =
    let framework = JsonObject()
    framework["name"] <- JsonValue.Create "Microsoft.NETCore.App"
    framework["version"] <- JsonValue.Create version
    let options = JsonObject()
    options["framework"] <- framework
    let root = JsonObject()
    root["runtimeOptions"] <- options
    root

let runtimeConfig (version: string) : byte[] =
    utf8 ((runtimeConfigNode version).ToJsonString())

let bodies (spec: Spec) : (string * byte[]) list =
    [ spec.Entrypoint, utf8 $"entrypoint:{spec.Component}"
      Path.ChangeExtension(spec.Entrypoint, ".deps.json"),
      utf8 "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v11.0\"}}"
      runtimeConfigNameFor spec.Entrypoint, runtimeConfig spec.RuntimeVersion
      noticeEntryName, utf8 $"notice:{spec.Component}" ]

let manifest (spec: Spec) (revision: string) (members: (string * byte[]) list) : DistributionManifest =
    { SchemaVersion = DistributionContract.schemaVersion
      Id = spec.Component
      Version = spec.Version
      Revision = revision
      SourceRepository = repository
      Producer =
        { Sdk = "11.0.100"
          Runtime = spec.RuntimeVersion
          Platform = "win-x64" }
      Archive = spec.ArchiveName
      Launch =
        { Kind = launchKindDotnetExec
          Entrypoint = spec.Entrypoint
          Arguments =
            [ { Flag = "--dotnet-host"; Input = "dotnetHost"; Type = AbsoluteFile }
              { Flag = "--artifact-root"; Input = "artifactRoot"; Type = AbsoluteDirectory } ] }
      Runtime =
        { Framework = runtimeFramework
          Version = spec.RuntimeVersion
          Config = runtimeConfigNameFor spec.Entrypoint }
      Files =
        members
        |> List.map (fun (path, bytes) -> { Path = path; Sha256 = sha256 bytes })
        |> List.sortWith (fun left right -> String.CompareOrdinal(left.Path, right.Path)) }

let manifestBytes (spec: Spec) (revision: string) (members: (string * byte[]) list) : byte[] =
    utf8 (DistributionContract.render (manifest spec revision members))

let private writeArchiveBytes
    (level: CompressionLevel)
    (manifestContent: byte[])
    (members: (string * byte[]) list)
    : byte[] =
    use output = new MemoryStream()

    let write () =
        use archive = new ZipArchive(output, ZipArchiveMode.Create, true)

        let add (name: string) (payload: byte[]) =
            let entry = archive.CreateEntry(name, level)
            // A fixed timestamp keeps the fixture bytes deterministic across runs.
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use stream = entry.Open()
            stream.Write(payload, 0, payload.Length)

        for name, payload in members do
            add name payload

        add manifestEntryName manifestContent

    write ()
    output.ToArray()

// The writer records its own platform in the creator byte, so the fixture carries the canonical creator metadata the released archive carries.
let archiveBytesWith
    (level: CompressionLevel)
    (manifestContent: byte[])
    (members: (string * byte[]) list)
    : byte[] =
    match DistributionArchive.canonicalizeWriterBytes "fixture-archive.zip" (writeArchiveBytes level manifestContent members) with
    | Ok bytes -> bytes
    | Error error -> failtestf "the fixture archive is not a canonical archive: %s" (ReleaseError.message error)

// A fixture that must carry a defect the canonicalizer refuses is written without it, so the reader's own refusal is what the case observes.
let rawArchiveBytesWith
    (level: CompressionLevel)
    (manifestContent: byte[])
    (members: (string * byte[]) list)
    : byte[] =
    writeArchiveBytes level manifestContent members

let archiveBytes (manifestContent: byte[]) (members: (string * byte[]) list) : byte[] =
    archiveBytesWith CompressionLevel.Optimal manifestContent members

let pin (spec: Spec) (manifestContent: byte[]) (archive: byte[]) : Pin =
    { SchemaVersion = DistributionPin.schemaVersion
      ComponentId = spec.Component
      Version = spec.Version
      AssetUri = assetUri spec
      ArchiveName = spec.ArchiveName
      ArchiveSha256 = sha256 archive
      ManifestSha256 = sha256 manifestContent
      PublishedRevision = spec.Revision
      ProducerSdk = None
      ProducerRuntime = None
      ProducerPlatform = None }

// One consistent fixture: the pin is derived from the exact bytes that carry the same identity.
let fixture (spec: Spec) : Pin * byte[] =
    let members = bodies spec
    let content = manifestBytes spec spec.Revision members
    let archive = archiveBytes content members
    pin spec content archive, archive

let tamper (bytes: byte[]) : byte[] =
    let copy = Array.copy bytes
    copy[1] <- copy[1] ^^^ 0xFFuy
    copy

type private ServingHandler(bytes: byte[], delayMs: int) =
    inherit HttpMessageHandler()

    override _.SendAsync(_, cancellationToken) =
        task {
            if delayMs > 0 then do! Task.Delay(delayMs, cancellationToken)
            let response = new HttpResponseMessage(HttpStatusCode.OK)
            response.Content <- new ByteArrayContent(bytes)
            return response
        }

type private FileHandler(path: string, delayMs: int) =
    inherit HttpMessageHandler()

    override _.SendAsync(_, cancellationToken) =
        task {
            if delayMs > 0 then do! Task.Delay(delayMs, cancellationToken)
            let response = new HttpResponseMessage(HttpStatusCode.OK)
            response.Content <- new ByteArrayContent(File.ReadAllBytes path)
            return response
        }

type private RefusingHandler() =
    inherit HttpMessageHandler()

    override _.SendAsync(_, _) =
        task {
            let response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            response.Content <- new StringContent "the transport is disabled"
            return response
        }

let serving (bytes: byte[]) (downloads: int ref) : ResolverTransport.Transport =
    { CreateClient =
        fun () ->
            downloads.Value <- downloads.Value + 1
            new HttpClient(new ServingHandler(bytes, 0)) }

let servingFile (path: string) (delayMs: int) (downloads: int ref) : ResolverTransport.Transport =
    { CreateClient =
        fun () ->
            downloads.Value <- downloads.Value + 1
            new HttpClient(new FileHandler(path, delayMs)) }

// Transport-disabled: a caller that reaches the network is refused explicitly, so a resolve that still succeeds proves the cache was used instead of a silent fallback.
let offline (downloads: int ref) : ResolverTransport.Transport =
    { CreateClient =
        fun () ->
            downloads.Value <- downloads.Value + 1
            new HttpClient(new RefusingHandler()) }

// A temp base reached through an alias such as /tmp or /var is itself a link, and the resolver refuses a linked chain, so each component is walked to the entry it names.
let private canonicalDirectory (path: string) : string =
    let absolute = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)
    let root = Path.GetPathRoot absolute

    let rec step (current: string) (remaining: string list) =
        match remaining with
        | [] -> current
        | segment :: rest ->
            let next = Path.Combine(current, segment)
            let info = DirectoryInfo next

            let target =
                if isNull info.LinkTarget then
                    next
                else
                    match info.ResolveLinkTarget true with
                    | null -> next
                    | resolved -> resolved.FullName

            step target rest

    if String.IsNullOrEmpty root then
        absolute
    else
        let relative =
            absolute.Substring(root.Length)
            |> fun rest -> rest.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

        let segments = relative |> Array.filter (fun segment -> segment <> "") |> Array.toList
        step root segments

let tempDirectory = canonicalDirectory (Path.GetTempPath())

let private baseDirectory = Path.Combine(tempDirectory, "mcp-store-resolver-tests")

let root (label: string) : string =
    let path = Path.Combine(baseDirectory, $"{label}-{Guid.NewGuid():N}")
    Directory.CreateDirectory path |> ignore
    path

// Every link these fixtures plant targets a directory inside the same fixture root, so a recursive delete cannot reach content the resolver or the operator owns.
let discard (path: string) : unit =
    try
        Directory.Delete(path, true)
    with _ ->
        ()

let scoped (label: string) (body: string -> unit) : unit =
    let path = root label

    try
        body path
        discard path
    with _ ->
        eprintfn "preserved resolver fixture after failure: %s" path
        reraise ()

let scopedAsync (label: string) (body: string -> Async<unit>) : Async<unit> =
    async {
        let path = root label

        try
            do! body path
            discard path
        with error ->
            eprintfn "preserved resolver fixture after failure: %s" path
            return raise error
    }

let okAsync (work: Async<Result<'a, ReleaseError>>) : Async<'a> =
    async {
        match! work with
        | Ok value -> return value
        | Error error -> return failtestf "expected success, got %s" (ReleaseError.message error)
    }

let writePin (directory: string) (value: Pin) : string =
    let path = Path.Combine(directory, "pin.json")
    File.WriteAllText(path, DistributionPin.render value)
    path

let writeArchive (path: string) (bytes: byte[]) : string =
    File.WriteAllBytes(path, bytes)
    path

// On Windows a file symlink needs a privilege a junction does not, so the fixture reports why it could not plant one rather than letting a case claim a link it never created.
let fileSymlink (link: string) (target: string) : Result<unit, string> =
    try
        File.CreateSymbolicLink(link, target) |> ignore
        Ok()
    with error -> Error $"{error.GetType().Name}: {error.Message}"

// A directory link must be creatable without an elevated privilege everywhere, so Windows uses the junction command and Unix uses the supported symbolic-link API.
let directoryLinkAsync (link: string) (target: string) : Async<bool> =
    async {
        if not (OperatingSystem.IsWindows()) then
            try
                Directory.CreateSymbolicLink(link, target) |> ignore
                return Directory.Exists link
            with _ ->
                return false
        else
            let info = ProcessStartInfo("cmd")
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            info.CreateNoWindow <- true

            for argument in [ "/c"; "mklink"; "/J"; link; target ] do
                info.ArgumentList.Add argument

            use child = new Process(StartInfo = info)

            if not (child.Start()) then
                return false
            else
                do! child.WaitForExitAsync() |> Async.AwaitTask
                return child.ExitCode = 0 && Directory.Exists link
    }
