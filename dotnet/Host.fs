namespace Mcp.Verifier

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

module private McpHost =
    [<Literal>]
    let ProtocolVersion = "2024-11-05"

    [<Literal>]
    let AcceptedClientProtocolVersion = "2025-11-25"

    let private invalid message : Result<'a, string> = Error message
    let private node (value: 'T) : JsonNode = JsonValue.Create<'T>(value) :> JsonNode
    let private nullNode () = JsonNode.Parse("null")

    let private objectProperties label (element: JsonElement) allowed required =
        if element.ValueKind <> JsonValueKind.Object then invalid $"{label} must be an object"
        else
            let properties = element.EnumerateObject() |> Seq.toList
            let names = properties |> List.map _.Name
            let duplicates = names |> List.groupBy id |> List.tryFind (fun (_, values) -> values.Length > 1)

            match duplicates with
            | Some(name, _) -> invalid $"{label} contains duplicate property '{name}'"
            | None ->
                match names |> List.tryFind (fun name -> not (List.contains name allowed)) with
                | Some name -> invalid $"{label} contains unknown property '{name}'"
                | None ->
                    match required |> List.tryFind (fun name -> not (List.contains name names)) with
                    | Some name -> invalid $"{label} is missing property '{name}'"
                    | None -> Ok element

    let private property name (element: JsonElement) =
        match element.EnumerateObject() |> Seq.tryFind (fun item -> item.Name = name) with
        | Some item -> Some(item.Value.Clone())
        | None -> None

    let private requiredString label name element =
        match property name element with
        | None -> invalid $"{label} is missing property '{name}'"
        | Some value when value.ValueKind <> JsonValueKind.String -> invalid $"property '{name}' must be a string"
        | Some value ->
            let text = value.GetString()
            if isNull text then invalid $"property '{name}' must be a string" else Ok text

    let private optionalString name element =
        match property name element with
        | None -> Ok None
        | Some value when value.ValueKind <> JsonValueKind.String -> invalid $"property '{name}' must be a string"
        | Some value ->
            let text = value.GetString()
            if isNull text then invalid $"property '{name}' must be a string" else Ok(Some text)

    let private optionalBoolean name element defaultValue =
        match property name element with
        | None -> Ok defaultValue
        | Some value when value.ValueKind = JsonValueKind.True -> Ok(Some true)
        | Some value when value.ValueKind = JsonValueKind.False -> Ok(Some false)
        | Some _ -> invalid $"property '{name}' must be a boolean"

    let private optionalInteger name element =
        match property name element with
        | None -> Ok None
        | Some value when value.ValueKind <> JsonValueKind.Number -> invalid $"property '{name}' must be an integer"
        | Some value ->
            match value.TryGetInt32() with
            | true, number -> Ok(Some number)
            | false, _ -> invalid $"property '{name}' must be a 32-bit integer"

    let private optionalTimeout element =
        match optionalInteger "timeoutMs" element with
        | Error error -> Error error
        | Ok None -> Ok None
        | Ok(Some value) when value < 0 -> invalid "timeoutMs must be non-negative"
        | Ok(Some value) ->
            try
                Ok(Some(TimeSpan.FromMilliseconds(float value)))
            with :? OverflowException ->
                invalid "timeoutMs is outside the supported range"

    let private parseBuild arguments =
        result {
            let! arguments = objectProperties "verify_dotnet_build arguments" arguments [ "target"; "configuration"; "noRestore"; "timeoutMs" ] []
            let! target = optionalString "target" arguments
            let! configuration = optionalString "configuration" arguments
            let! noRestore = optionalBoolean "noRestore" arguments None
            let! timeout = optionalTimeout arguments
            return { Target = target; Configuration = configuration; NoRestore = noRestore; Timeout = timeout }
        }

    let private parseTest arguments =
        result {
            let! arguments = objectProperties "verify_dotnet_test arguments" arguments [ "target"; "configuration"; "filter"; "noBuild"; "timeoutMs" ] []
            let! target = optionalString "target" arguments
            let! configuration = optionalString "configuration" arguments
            let! filter = optionalString "filter" arguments
            let! noBuild = optionalBoolean "noBuild" arguments None
            let! timeout = optionalTimeout arguments
            return { Target = target; Configuration = configuration; Filter = filter; NoBuild = noBuild; Timeout = timeout }
        }

    let private parseDetailKind value =
        match value with
        | "errors" -> Ok DetailKind.Errors
        | "warnings" -> Ok DetailKind.Warnings
        | "failed-tests" -> Ok DetailKind.FailedTests
        | "output" -> Ok DetailKind.Output
        | _ -> invalid "kind must be one of: errors, warnings, failed-tests, output"

    let private parseDetails arguments =
        result {
            let! arguments = objectProperties "verification_details arguments" arguments [ "runId"; "kind"; "offset"; "limit" ] [ "runId"; "kind" ]
            let! runId = requiredString "verification_details arguments" "runId" arguments
            let! kindText = requiredString "verification_details arguments" "kind" arguments
            let! kind = parseDetailKind kindText
            let! offsetValue = optionalInteger "offset" arguments
            let! limit = optionalInteger "limit" arguments
            return { RunId = runId; Kind = kind; Offset = offsetValue |> Option.defaultValue 0; Limit = limit }
        }

    let private statusText status =
        match status with
        | VerificationStatus.Succeeded -> "succeeded"
        | VerificationStatus.Failed -> "failed"
        | VerificationStatus.TimedOut -> "timeout"
        | VerificationStatus.Cancelled -> "cancelled"
        | VerificationStatus.InfrastructureFailure -> "infrastructure-failure"

    let private errorCode error =
        match error with
        | VerificationError.InvalidInput _ -> "INVALID_INPUT"
        | VerificationError.UnauthorizedPath _ -> "UNAUTHORIZED_PATH"
        | VerificationError.UnknownRunId _ -> "UNKNOWN_RUN_ID"
        | VerificationError.InvalidPagination _ -> "INVALID_PAGINATION"
        | VerificationError.UnsupportedDetailKind _ -> "UNSUPPORTED_DETAIL_KIND"
        | VerificationError.UnavailableTestDetail _ -> "TEST_DETAIL_UNAVAILABLE"
        | VerificationError.MissingArtifact _ -> "MISSING_ARTIFACT"
        | VerificationError.ProcessStartFailure _ -> "PROCESS_START_FAILURE"
        | VerificationError.ArtifactQuotaExceeded _ -> "ARTIFACT_QUOTA_EXCEEDED"
        | VerificationError.ArtifactFailure _ -> "ARTIFACT_FAILURE"

    let private boundedMessage (message: string) =
        let value = if isNull message then "operation failed" else message
        if value.Length <= Budgets.Defaults.MessageMaxLength then value else value.Substring(0, Budgets.Defaults.MessageMaxLength)

    let private envelopeFor isError (payload: JsonNode) =
        let content = JsonObject()
        content["type"] <- node "text"
        content["text"] <- node (payload.ToJsonString())
        let contentArray = JsonArray()
        contentArray.Add content
        let envelope = JsonObject()
        envelope["content"] <- contentArray
        envelope["isError"] <- node isError
        envelope["structuredContent"] <- payload.DeepClone()
        envelope

    let private serializedSize (value: JsonNode) = Encoding.UTF8.GetByteCount(value.ToJsonString())

    let private resultEnvelope isError (payload: JsonNode) =
        let mutable candidate = envelopeFor isError payload
        let mutable changed = true

        // structuredContent is repeated as text in the MCP envelope, so the
        // item budgets alone do not guarantee the serialized result limit.
        while serializedSize candidate > Budgets.Defaults.TotalToolResultSize && changed do
            changed <- false

            for propertyName in [ "errors"; "warnings"; "failedTests"; "items" ] do
                if not changed then
                    match Option.ofObj (payload.AsObject()[propertyName]) with
                    | Some value when value.GetValueKind() = JsonValueKind.Array && value.AsArray().Count > 0 ->
                        let values = value.AsArray()
                        values.RemoveAt(values.Count - 1)
                        changed <- true
                        if propertyName = "items" then
                            payload["limit"] <- node values.Count
                            payload["hasMore"] <- node true
                    | _ -> ()

            if changed then candidate <- envelopeFor isError payload

        if serializedSize candidate <= Budgets.Defaults.TotalToolResultSize then candidate
        else
            let fallback = JsonObject()
            fallback["ok"] <- node false
            let error = JsonObject()
            error["code"] <- node "RESULT_BUDGET_EXCEEDED"
            error["message"] <- node "verification result exceeded the serialized tool-result budget"
            fallback["error"] <- error
            envelopeFor true fallback

    let private failure error =
        let payload = JsonObject()
        let detail = JsonObject()
        detail["code"] <- node (errorCode error)
        detail["message"] <- node (boundedMessage (VerificationError.message error))
        payload["ok"] <- node false
        payload["error"] <- detail
        resultEnvelope true payload

    let private optionalInt value =
        match value with
        | Some number -> node number
        | None -> nullNode ()

    let private buildResult (result: CompactBuildResult) =
        let payload = JsonObject()
        payload["ok"] <- node true
        payload["runId"] <- node result.RunId
        payload["operation"] <- node "build"
        payload["status"] <- node (statusText result.Status)
        payload["exitCode"] <- optionalInt result.ExitCode
        payload["durationMs"] <- node result.DurationMs
        payload["errorCount"] <- node result.ErrorCount
        payload["warningCount"] <- node result.WarningCount
        let errors = JsonArray()
        result.Errors |> List.iter (fun value -> errors.Add(node value))
        payload["errors"] <- errors
        let warnings = JsonArray()
        result.Warnings |> List.iter (fun value -> warnings.Add(node value))
        payload["warnings"] <- warnings
        payload["detailsAvailable"] <- node result.DetailsAvailable
        resultEnvelope false payload

    let private testResult (result: CompactTestResult) =
        let payload = JsonObject()
        payload["ok"] <- node true
        payload["runId"] <- node result.RunId
        payload["operation"] <- node "test"
        payload["status"] <- node (statusText result.Status)
        payload["exitCode"] <- optionalInt result.ExitCode
        payload["durationMs"] <- node result.DurationMs

        match result.Counts with
        | None -> payload["counts"] <- nullNode ()
        | Some counts ->
            let countsNode = JsonObject()
            countsNode["total"] <- node counts.Total
            countsNode["passed"] <- node counts.Passed
            countsNode["failed"] <- node counts.Failed
            countsNode["skipped"] <- node counts.Skipped
            payload["counts"] <- countsNode

        let failedTests = JsonArray()
        result.FailedTests |> List.iter (fun value -> failedTests.Add(node value))
        payload["failedTests"] <- failedTests
        payload["detailsAvailable"] <- node result.DetailsAvailable
        payload["trxAvailable"] <- node result.TrxAvailable
        match result.TrxUnavailableReason with
        | Some reason -> payload["trxUnavailableReason"] <- node (boundedMessage reason)
        | None -> payload["trxUnavailableReason"] <- nullNode ()
        resultEnvelope false payload

    let private detailsResult (result: DetailPage) =
        let payload = JsonObject()
        payload["ok"] <- node true
        payload["runId"] <- node result.RunId
        payload["kind"] <- node (match result.Kind with | DetailKind.Errors -> "errors" | DetailKind.Warnings -> "warnings" | DetailKind.FailedTests -> "failed-tests" | DetailKind.Output -> "output")
        payload["offset"] <- node result.Offset
        payload["limit"] <- node result.Limit
        payload["total"] <- node result.Total
        let items = JsonArray()
        result.Items |> List.iter (fun value -> items.Add(node (boundedMessage value)))
        payload["items"] <- items
        payload["hasMore"] <- node result.HasMore
        resultEnvelope false payload

    let private toolsJson =
        """[
              {"name":"verify_dotnet_build","description":"Run the capability-controlled dotnet build and return a bounded semantic result.","inputSchema":{"type":"object","additionalProperties":false,"properties":{"target":{"type":"string","description":"Workspace-relative .NET project or solution file."},"configuration":{"type":"string","description":"Safe configuration name; defaults to Release."},"noRestore":{"type":"boolean","description":"Skip restore; defaults to false."},"timeoutMs":{"type":"integer","minimum":100,"maximum":1800000,"description":"Timeout in milliseconds; defaults to five minutes."}}}},
              {"name":"verify_dotnet_test","description":"Run the capability-controlled dotnet test and return bounded counts and failures.","inputSchema":{"type":"object","additionalProperties":false,"properties":{"target":{"type":"string","description":"Workspace-relative .NET project or solution file."},"configuration":{"type":"string","description":"Safe configuration name; defaults to Release."},"filter":{"type":"string","description":"One controlled dotnet test filter value."},"noBuild":{"type":"boolean","description":"Skip build; defaults to false."},"timeoutMs":{"type":"integer","minimum":100,"maximum":1800000,"description":"Timeout in milliseconds; defaults to five minutes."}}}},
              {"name":"verification_details","description":"Read one bounded page of retained verifier evidence by opaque run ID.","inputSchema":{"type":"object","additionalProperties":false,"required":["runId","kind"],"properties":{"runId":{"type":"string"},"kind":{"type":"string","enum":["errors","warnings","failed-tests","output"]},"offset":{"type":"integer","minimum":0,"default":0},"limit":{"type":"integer","minimum":1,"maximum":128}}}}
            ]"""

    let private initializeResult =
        let result = JsonObject()
        result["protocolVersion"] <- node ProtocolVersion
        let capabilities = JsonObject()
        let toolsCapability = JsonObject()
        toolsCapability["listChanged"] <- node false
        capabilities["tools"] <- toolsCapability
        result["capabilities"] <- capabilities
        let serverInfo = JsonObject()
        serverInfo["name"] <- node "mcp-store-dotnet-verifier"
        serverInfo["version"] <- node "1"
        result["serverInfo"] <- serverInfo
        result

    let private response (id: string) (result: JsonNode) =
        let envelope = JsonObject()
        envelope["jsonrpc"] <- node "2.0"
        envelope["id"] <- JsonNode.Parse id
        envelope["result"] <- result
        envelope.ToJsonString()

    let private protocolError (id: string) code message =
        let error = JsonObject()
        error["code"] <- node code
        error["message"] <- node (boundedMessage message)
        let envelope = JsonObject()
        envelope["jsonrpc"] <- node "2.0"
        envelope["id"] <- JsonNode.Parse id
        envelope["error"] <- error
        envelope.ToJsonString()

    let private requestId (request: JsonElement) : Result<string option, string> =
        match property "id" request with
        | None -> Ok None
        | Some value when value.ValueKind = JsonValueKind.String || value.ValueKind = JsonValueKind.Number -> Ok(Some(value.GetRawText()))
        | Some value when value.ValueKind = JsonValueKind.Null -> Ok None
        | Some _ -> invalid "JSON-RPC request id must be a string, number, or null"

    let private parseRequest (line: string) =
        try
            use document = JsonDocument.Parse(line, JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false))
            let request = document.RootElement

            result {
                let! request = objectProperties "JSON-RPC request" request [ "jsonrpc"; "id"; "method"; "params" ] [ "jsonrpc"; "method" ]
                let! version = requiredString "JSON-RPC request" "jsonrpc" request
                if version <> "2.0" then return! invalid "JSON-RPC request jsonrpc must be exactly '2.0'"
                let! id = requestId request
                let! methodName = requiredString "JSON-RPC request" "method" request
                return id, methodName, property "params" request
            }
        with
        | :? JsonException -> invalid "invalid JSON request"
        | :? FormatException -> invalid "invalid JSON-RPC request"

    let private cancelledRequestId parameters =
        result {
            let! parameters = match parameters with | Some value -> Ok value | None -> invalid "notifications/cancelled requires params"
            let! parameters = objectProperties "notifications/cancelled params" parameters [ "requestId" ] [ "requestId" ]
            let! requestId =
                match property "requestId" parameters with
                | Some value when value.ValueKind = JsonValueKind.String || value.ValueKind = JsonValueKind.Number -> Ok(value.GetRawText())
                | _ -> invalid "notifications/cancelled requestId must be a string or number"
            return requestId
        }

    let private parseToolCall parameters =
        result {
            let! parameters = match parameters with | Some value -> Ok value | None -> invalid "tools/call requires params"
            let! parameters = objectProperties "tools/call params" parameters [ "name"; "arguments" ] [ "name" ]
            let! name = requiredString "tools/call params" "name" parameters
            let! arguments =
                match property "arguments" parameters with
                | None -> Ok(JsonDocument.Parse("{}").RootElement.Clone())
                | Some value when value.ValueKind <> JsonValueKind.Object -> invalid "tools/call arguments must be an object"
                | Some value -> Ok value
            return name, arguments
        }

    let private invokeTool (service: VerifierService) name arguments cancellationToken =
        task {
            try
                match name with
                | "verify_dotnet_build" ->
                    match parseBuild arguments with
                    | Error message -> return failure (VerificationError.InvalidInput message)
                    | Ok options ->
                        let! result = service.VerifyBuild(options, cancellationToken = cancellationToken)
                        match result with | Ok value -> return buildResult value | Error error -> return failure error
                | "verify_dotnet_test" ->
                    match parseTest arguments with
                    | Error message -> return failure (VerificationError.InvalidInput message)
                    | Ok options ->
                        let! result = service.VerifyTest(options, cancellationToken = cancellationToken)
                        match result with | Ok value -> return testResult value | Error error -> return failure error
                | "verification_details" ->
                    match parseDetails arguments with
                    | Error message -> return failure (VerificationError.InvalidInput message)
                    | Ok request ->
                        match service.Details request with | Ok value -> return detailsResult value | Error error -> return failure error
                | _ -> return failure (VerificationError.InvalidInput $"tool '{name}' is not registered")
            with _ -> return failure (VerificationError.ArtifactFailure "verifier operation failed")
        }

    let private handleImmediate id methodName parameters =
        match methodName with
        | "ping" -> response id (JsonObject())
        | "initialize" ->
            match parameters with
            | None -> protocolError id -32602 "initialize requires params"
            | Some values ->
                match objectProperties "initialize params" values [ "protocolVersion"; "capabilities"; "clientInfo" ] [ "protocolVersion"; "capabilities"; "clientInfo" ] with
                | Error message -> protocolError id -32602 message
                | Ok values ->
                    match requiredString "initialize params" "protocolVersion" values with
                    | Error message -> protocolError id -32602 message
                    | Ok version when version <> ProtocolVersion && version <> AcceptedClientProtocolVersion -> protocolError id -32602 $"unsupported protocol version '{version}'"
                    | Ok _ -> response id initializeResult
        | "tools/list" ->
            match parameters with
            | Some values ->
                match objectProperties "tools/list params" values [] [] with
                | Error message -> protocolError id -32602 message
                | Ok _ ->
                    let result = JsonObject()
                    result["tools"] <- JsonNode.Parse toolsJson
                    response id result
            | None ->
                let result = JsonObject()
                result["tools"] <- JsonNode.Parse toolsJson
                response id result
        | "shutdown" -> response id (JsonObject())
        | _ -> protocolError id -32601 $"method '{methodName}' is not supported"

    let run (service: VerifierService) =
        let outputGate = obj ()
        let logGate = obj ()
        let active = ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal)
        let running = ConcurrentBag<Task>()
        use shutdown = new CancellationTokenSource()
        let mutable accepting = true

        let write output = lock outputGate (fun () -> printfn "%s" output; Console.Out.Flush())
        let log message = lock logGate (fun () -> Console.Error.WriteLine(boundedMessage message))

        let dispatch id name arguments =
            let cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token)

            if not (active.TryAdd(id, cancellation)) then
                cancellation.Dispose()
                write (protocolError id -32600 "request id is already active")
            else
                let work =
                    task {
                        try
                            let! result = invokeTool service name arguments cancellation.Token
                            write (response id result)
                            let runId =
                                result.AsObject()["structuredContent"]
                                |> Option.ofObj
                                |> Option.bind (fun value -> Option.ofObj (value.AsObject()["runId"]))
                                |> Option.map (fun value -> value.GetValue<string>())
                                |> Option.defaultValue "n/a"
                            log $"{name} completed runId={runId}"
                        with error ->
                            write (response id (failure (VerificationError.ArtifactFailure "verifier operation failed")))
                            log $"{name} failed: {error.Message}"

                        let mutable removed: CancellationTokenSource = null
                        active.TryRemove(id, &removed) |> ignore
                        cancellation.Dispose()
                    }
                running.Add(work)

        while accepting do
            let line = Console.ReadLine()
            if isNull line then
                accepting <- false
                shutdown.Cancel()
            elif not (String.IsNullOrWhiteSpace line) then
                match parseRequest line with
                | Error message -> write (protocolError "null" -32600 message)
                | Ok(id, methodName, parameters) ->
                    match methodName, id with
                    | "notifications/initialized", _ -> ()
                    | "notifications/cancelled", _ ->
                        match cancelledRequestId parameters with
                        | Ok requestId ->
                            match active.TryGetValue requestId with
                            | true, cancellation ->
                                try cancellation.Cancel() with :? ObjectDisposedException -> ()
                            | false, _ -> ()
                        | Error message -> log $"ignored cancellation notification: {message}"
                    | "exit", _ ->
                        accepting <- false
                        shutdown.Cancel()
                    | "tools/call", Some requestId ->
                        match parseToolCall parameters with
                        | Error message -> write (protocolError requestId -32602 message)
                        | Ok(name, arguments) -> dispatch requestId name arguments
                    | _, Some requestId ->
                        let output = handleImmediate requestId methodName parameters
                        write output
                        if methodName = "shutdown" then
                            accepting <- false
                            shutdown.Cancel()
                    | _, None -> ()

        shutdown.Cancel()
        try Task.WaitAll(running.ToArray()) with :? AggregateException -> log "one or more verifier operations did not shut down cleanly"

module Program =
    [<EntryPoint>]
    let main args =
        let startupFailure message =
            Console.Error.WriteLine($"dotnet verifier startup failed: {message}")
            1

        match args with
        | [| "--dotnet-host"; injectedHost |] ->
            match AuthorizedInvocation.validateInjectedHost Environment.CurrentDirectory injectedHost with
            | Error error -> startupFailure (VerificationError.message error)
            | Ok validatedHost ->
                try
                    use service = new VerifierService(Environment.CurrentDirectory, dotnetHost = validatedHost)
                    McpHost.run service
                    0
                with error ->
                    let message =
                        if isNull error.Message then "verifier host could not start"
                        elif error.Message.Length <= Budgets.Defaults.MessageMaxLength then error.Message
                        else error.Message.Substring(0, Budgets.Defaults.MessageMaxLength)
                    startupFailure message
        | _ -> startupFailure "the verifier requires exactly one injected --dotnet-host absolute path"
