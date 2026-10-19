// role: reusable module/helper
// CLI workflows over the resolver library: resolve snapshots a verified installation, run
// holds its lease for the child lifetime and reports the child's own outcome.
module ResolverRun

open SharedFailure

let private root (overrideRoot: string option) : Result<string, ReleaseError> =
    match overrideRoot with
    | Some value -> Ok value
    | None -> ResolverStorage.canonicalRoot ()

let private read (path: string) : Result<DistributionPin.Pin, ReleaseError> =
    match ResolverInput.pin path with
    | Error error -> Error error
    | Ok text -> DistributionPin.parse text

let private resolveTarget
    (transport: ResolverTransport.Transport)
    (pinPath: string)
    (overrideRoot: string option)
    : Async<Result<DistributionPin.Pin * ResolverInstall.Resolution, ReleaseError>> =
    async {
        match root overrideRoot with
        | Error error -> return Error error
        | Ok storageRoot ->
            match read pinPath with
            | Error error -> return Error error
            | Ok pin ->
                match! ResolverInstall.resolve transport storageRoot pin with
                | Error error -> return Error error
                | Ok resolution -> return Ok(pin, resolution)
    }

// A resolve caller launches nothing of its own, so the lease ends with the snapshot.
let resolve
    (transport: ResolverTransport.Transport)
    (target: ResolverCli.Target)
    : Async<Result<string, ReleaseError>> =
    async {
        match! resolveTarget transport target.Pin target.Root with
        | Error error -> return Error error
        | Ok(pin, resolution) ->
            let snapshot = ResolverReport.render pin resolution
            resolution.Lease.Release()
            return Ok snapshot
    }

let run
    (transport: ResolverTransport.Transport)
    (options: ResolverCli.Run)
    : Async<Result<ResolverChild.Outcome, ReleaseError>> =
    async {
        match root options.Root with
        | Error error -> return Error error
        | Ok storageRoot ->
            match ResolverInput.directory storageRoot "workspace" options.Workspace with
            | Error error -> return Error error
            | Ok () ->
                match ResolverInput.file storageRoot "host" options.Host with
                | Error error -> return Error error
                | Ok () ->
                    match! resolveTarget transport options.Pin options.Root with
                    | Error error -> return Error error
                    | Ok(_, resolution) ->
                        let descriptor = resolution.Manifest.Launch

                        let prepared =
                            match ResolverLaunch.compose descriptor options.Inputs with
                            | Error error -> Error error
                            | Ok arguments ->
                                match ResolverInput.inputs storageRoot descriptor options.Inputs with
                                | Error error -> Error error
                                | Ok () -> Ok arguments

                        match prepared with
                        | Error error ->
                            resolution.Lease.Release()
                            return Error error
                        | Ok arguments ->
                            // The lease outlives the child, so a repair cannot replace the
                            // payload a running process is executing while the claim is held.
                            try
                                match ResolverChild.start options.Host options.Workspace resolution.Entrypoint arguments with
                                | Error error -> return Error error
                                | Ok started ->
                                    use child = started
                                    let! outcome = ResolverChild.await child
                                    return Ok outcome
                            finally
                                resolution.Lease.Release()
    }
