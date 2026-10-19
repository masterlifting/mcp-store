// Transport policy is asserted through the real download path with a scripted handler, so the URI admission rules, redirect policy, body bound, and per-hop client lifetime are proven offline.
module Resolver.Tests.Transport

open System
open System.IO
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open SharedFailure
open DistributionArchive
open Resolver.Tests.Fixture

// A response is only disposed through the body it owns, so the body records how much of itself was consumed and whether that happened before the first disposal.
type private ITracked =
    abstract Disposals: int
    abstract Consumed: int64
    abstract DisposedAfterRead: bool

type private TrackedStream(payload: byte[]) =
    inherit MemoryStream(payload, 0, payload.Length, false, true)

    let mutable disposals = 0
    let mutable consumed = 0L
    let mutable disposedAfterRead = false

    interface ITracked with
        member _.Disposals = disposals
        member _.Consumed = consumed
        member _.DisposedAfterRead = disposedAfterRead

    override _.Read(buffer: byte[], offset: int, count: int) =
        let read = base.Read(buffer, offset, count)
        consumed <- consumed + int64 read
        read

    override this.Dispose(disposing: bool) =
        if disposing then
            disposals <- disposals + 1

            if disposals = 1 then
                disposedAfterRead <- consumed = int64 payload.Length

        base.Dispose disposing

// A body that declares no length can only be bounded while reading, so its bytes are generated.
type private VirtualStream(total: int64) =
    inherit Stream()

    let mutable remaining = total
    let mutable served = 0L
    let mutable disposals = 0
    let mutable disposedAfterRead = false

    interface ITracked with
        member _.Disposals = disposals
        member _.Consumed = served
        member _.DisposedAfterRead = disposedAfterRead

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = total

    override this.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()

    override _.Read(_: byte[], _: int, count: int) =
        if remaining <= 0L then
            0
        else
            let read = int (min remaining (int64 count))
            remaining <- remaining - int64 read
            served <- served + int64 read
            read

    override _.Seek(_: int64, _: SeekOrigin) = 0L
    override _.SetLength(_: int64) = ()
    override _.Write(_: byte[], _: int, _: int) = failwith "a response body is read-only"

    override this.Dispose(disposing: bool) =
        if disposing then
            disposals <- disposals + 1

            if disposals = 1 then
                disposedAfterRead <- remaining <= 0L

        base.Dispose disposing

type private Hop =
    { Status: HttpStatusCode
      Location: string option
      Content: HttpContent option }

type private CountingHandler(script: Uri -> Hop) =
    inherit HttpMessageHandler()

    let mutable disposals = 0

    member _.Disposals = disposals

    override _.SendAsync(request: HttpRequestMessage, _: CancellationToken) : Task<HttpResponseMessage> =
        task {
            let hop = script request.RequestUri
            let response = new HttpResponseMessage(hop.Status)

            match hop.Location with
            | Some location -> response.Headers.Location <- Uri(location, UriKind.Absolute)
            | None -> ()

            match hop.Content with
            | Some content -> response.Content <- content
            | None -> ()

            return response
        }

    override this.Dispose(disposing: bool) =
        if disposing then
            disposals <- disposals + 1

        base.Dispose disposing

type private Log() =
    let requests = ResizeArray<string>()
    let clients = ResizeArray<CountingHandler>()
    let bodies = ResizeArray<ITracked>()

    member _.Requests = List.ofSeq requests
    member _.Clients = clients.Count
    member _.DisposedClients = clients |> Seq.sumBy (fun client -> client.Disposals)
    member _.Bodies = List.ofSeq bodies
    member _.AddRequest(uri: Uri) = requests.Add uri.AbsoluteUri
    member _.AddClient(client: CountingHandler) = clients.Add client
    member _.Track(body: ITracked) = bodies.Add body

let private scripted (plan: int -> Uri -> Hop) (log: Log) : ResolverTransport.Transport =
    let mutable hop = 0

    { CreateClient =
        fun () ->
            let handler =
                new CountingHandler(fun uri ->
                    let index = hop
                    hop <- hop + 1
                    log.AddRequest uri
                    plan index uri)

            log.AddClient handler
            new HttpClient(handler) }

let private serving (log: Log) (payload: byte[]) : Hop =
    let body = new TrackedStream(payload)
    log.Track body

    { Status = HttpStatusCode.OK
      Location = None
      Content = Some(new StreamContent(body)) }

let private redirect (location: string) : Hop =
    { Status = HttpStatusCode.Found
      Location = Some location
      Content = None }

let private locationless : Hop =
    { Status = HttpStatusCode.Found
      Location = None
      Content = None }

let private declaredOversize (log: Log) : Hop =
    let body = new TrackedStream(utf8 "small body behind an oversized declaration")
    log.Track body
    let content = new StreamContent(body)
    content.Headers.ContentLength <- Nullable(maxArchiveBytes + 1L)

    { Status = HttpStatusCode.OK
      Location = None
      Content = Some content }

let private streamedOversize (log: Log) : Hop =
    let body = new VirtualStream(maxArchiveBytes + 1L)
    log.Track body

    { Status = HttpStatusCode.OK
      Location = None
      Content = Some(new StreamContent(body)) }

let private refused (error: ReleaseError) (phrase: string) : unit =
    match error with
    | TransportRejected (_, detail) ->
        Expect.stringContains detail phrase $"the refusal names '{phrase}'"
    | other -> failtestf "expected a transport refusal, got %s" (ReleaseError.message other)

let private expectedRefusal (reason: string) : 'a = failtestf "the download succeeded where it must fail: %s" reason

let tests =
    testList
        "transport"
        [ testCase "the canonical release asset URI is the only admitted origin"
          <| fun () ->
              let pinValue, _ = fixture dotnetSpec

              match ResolverTransport.assetUri pinValue with
              | Error error -> failtestf "the canonical fixture asset URI was refused: %s" (ReleaseError.message error)
              | Ok uri ->
                  Expect.equal uri.Scheme Uri.UriSchemeHttps "the asset URI is HTTPS"
                  Expect.equal uri.Host "github.com" "the asset URI names the release host"
                  Expect.isTrue uri.IsDefaultPort "the asset URI carries no explicit port"
                  Expect.isTrue (String.IsNullOrEmpty uri.UserInfo) "the asset URI carries no userinfo"
                  Expect.isTrue (String.IsNullOrEmpty uri.Query) "the asset URI carries no query string"
                  Expect.isTrue (String.IsNullOrEmpty uri.Fragment) "the asset URI carries no fragment"
                  Expect.equal
                      uri.AbsolutePath
                      (sprintf "/masterlifting/mcp-store/releases/download/dotnet/%s" dotnetSpec.ArchiveName)
                      "the asset URI path is the canonical release download"

          testCase "an off-origin, decorated, or mistyped asset URI is refused"
          <| fun () ->
              let pinValue, _ = fixture dotnetSpec
              let path = sprintf "/masterlifting/mcp-store/releases/download/dotnet/%s" dotnetSpec.ArchiveName

              let rejected (assetUri: string) =
                  match ResolverTransport.assetUri { pinValue with AssetUri = assetUri } with
                  | Ok _ -> failtestf "the asset URI '%s' must be refused" assetUri
                  | Error(TransportRejected _) -> ()
                  | Error error ->
                      failtestf "expected a transport refusal for '%s', got %s" assetUri (ReleaseError.message error)

              rejected (sprintf "http://github.com%s" path)
              rejected (sprintf "https://github.com.evil.test%s" path)
              rejected (sprintf "https://evil-github.test%s" path)
              rejected (sprintf "https://github.com:8443%s" path)
              rejected (sprintf "https://user@github.com%s" path)
              rejected (sprintf "https://github.com%s?token=abc" path)
              rejected (sprintf "https://github.com%s#fragment" path)
              rejected "https://github.com/masterlifting/mcp-store/releases/download/other/v1.0.5.zip"
              rejected "https://github.com/masterlifting/mcp-store/releases/download/dotnet/other.zip"
              rejected "releases/download/dotnet/v1.0.5.zip"

          testCaseAsync "an approved redirect is followed with its issued query string intact"
          <| async {
              let pinValue, archive = fixture dotnetSpec
              let log = Log()

              let signed = "https://release-assets.githubusercontent.com/mcp-store/v1.0.5.zip?Signature=issued"

              let transport = scripted (fun index _ -> if index = 0 then redirect signed else serving log archive) log

              match! ResolverTransport.download transport pinValue with
              | Ok bytes ->
                  Expect.equal bytes archive "the verified payload is returned from the redirect target"
                  Expect.equal log.Requests [ pinValue.AssetUri; signed ] "the issued Location is followed verbatim"
                  Expect.equal log.DisposedClients log.Clients "every hop's client is disposed"
              | Error error -> return expectedRefusal (ReleaseError.message error)
          }

          testCaseAsync "a redirect to an unapproved host is refused before it is requested"
          <| async {
              let pinValue, archive = fixture dotnetSpec
              let log = Log()

              let transport =
                scripted
                    (fun index _ ->
                        if index = 0 then
                            redirect "https://cdn.evil.test/mcp-store/v1.0.5.zip"
                        else
                            serving log archive)
                    log

              match! ResolverTransport.download transport pinValue with
              | Ok _ -> return expectedRefusal "an unapproved redirect target was served"
              | Error error ->
                  refused error "not an approved HTTPS GitHub host"
                  Expect.equal log.Requests [ pinValue.AssetUri ] "no request is issued to the unapproved host"
          }

          testCaseAsync "a redirect without a location and a chain past the hop bound are both refused"
          <| async {
              let pinValue, archive = fixture dotnetSpec

              let missing = Log()

              let withoutLocation =
                scripted (fun index _ -> if index = 0 then locationless else serving missing archive) missing

              match! ResolverTransport.download withoutLocation pinValue with
              | Ok _ -> return expectedRefusal "a redirect that carried no Location was served"
              | Error error ->
                  refused error "did not supply a Location header"
                  Expect.equal missing.Clients 1 "the refused hop is not retried"

              let chained = Log()

              let unbounded =
                scripted
                    (fun index _ -> redirect (sprintf "https://objects.githubusercontent.com/hop/%d.zip" index))
                    chained

              match! ResolverTransport.download unbounded pinValue with
              | Ok _ -> return expectedRefusal "an unbounded redirect chain was served"
              | Error error ->
                  refused error "exceeded the bounded limit"
                  // The hop count is the transport's private bound; the chain stops at six requests.
                  Expect.equal chained.Clients 6 "the chain stops at the bounded hop count"
                  Expect.equal chained.DisposedClients chained.Clients "every refused hop's client is disposed"
          }

          testCaseAsync "a declared body length over the archive bound is refused unread"
          <| async {
              let pinValue, _ = fixture dotnetSpec
              let log = Log()
              let transport = scripted (fun _ _ -> declaredOversize log) log

              match! ResolverTransport.download transport pinValue with
              | Ok _ -> return expectedRefusal "an oversized declared body was served"
              | Error error ->
                  refused error "declares more than"

                  for body in log.Bodies do
                      Expect.equal body.Consumed 0L "the oversized body is never read"
                      Expect.isTrue (body.Disposals >= 1) "the oversized body is disposed"
          }

          testCaseAsync "a body that outgrows the archive bound while streaming is refused"
          <| async {
              let pinValue, _ = fixture dotnetSpec
              let log = Log()
              let transport = scripted (fun _ _ -> streamedOversize log) log

              match! ResolverTransport.download transport pinValue with
              | Ok _ -> return expectedRefusal "an unbounded streamed body was served"
              | Error error ->
                  refused error "archive bound"

                  for body in log.Bodies do
                      Expect.isTrue (body.Disposals >= 1) "the refused body is disposed"
          }

          testCaseAsync "the final body is consumed before its response and client are disposed"
          <| async {
              let pinValue, archive = fixture dotnetSpec
              let log = Log()

              let transport =
                scripted
                    (fun index _ ->
                        if index = 0 then
                            redirect "https://objects.githubusercontent.com/mcp-store/v1.0.5.zip"
                        else
                            serving log archive)
                    log

              match! ResolverTransport.download transport pinValue with
              | Ok bytes ->
                  Expect.equal bytes archive "the complete body is returned"
                  Expect.equal log.Clients 2 "one client is created per hop"
                  Expect.equal log.DisposedClients log.Clients "every hop's client is disposed exactly once"
                  Expect.equal log.Bodies.Length 1 "only the final response carries a body"
                  Expect.equal
                      log.Bodies.Head.Consumed
                      (int64 archive.Length)
                      "the body was fully read"

                  Expect.isTrue log.Bodies.Head.DisposedAfterRead "the body was read before the response disposed it"
              | Error error -> return expectedRefusal (ReleaseError.message error)
          } ]