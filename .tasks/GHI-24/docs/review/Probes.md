# Authority regression review

## Summary

The publish-pipeline authority gate regression now uses child-process probes that strip
publication credential names by name and only set sentinel env values that the parent
controls. The host process never reads real credential values, the child only sees the
dummy values the parent opts into, and the recorded canonical URL is verified against
what the production source actually constructs.

## Behaviour covered

The two registered `Expecto` cases do exactly the bounded matrix the gate is required
to enforce, no more:

- `authority gate rejects unauthorized candidates before any HTTP for both producers`
  runs three reject scenarios for each producer (six guarded probes total). Every probe
  must observe a transport factory call count of zero, an HTTP request count of zero
  and a process exit code of four.
- `authorized transport invokes the canonical /releases/tags/<producer> URL without a v
  prefix for both producers` runs one authorized probe per producer (two tag probes
  total). Every probe must invoke the transport factory at least once, observe at
  least one HTTP request whose URL contains the canonical
  `/repos/masterlifting/mcp-store/releases/tags/<producer>` pattern with no `v` prefix,
  see a non-POST method (the scripted transport returns HTTP 500 before any upload),
  and exit with code four.

Both producers (`dotnet`, `workflow`) follow the canonical repository owner and
repository name (`masterlifting/mcp-store`) the production runner uses for
`ReleaseLocator` construction.

## How the probes stay safe under the global no-real-credentials rule

The parent constructs a fresh `ProcessStartInfo` per probe and:

1. Strips every publication credential name by name through
   `info.EnvironmentVariables.Remove` without ever calling
   `Environment.GetEnvironmentVariable`, so the host's real credentials (if any) are
   never read into the test code or written to stdout.
2. Strips an additional set of host-side names that may carry credentials in CI the
   same way (`MCP_RELEASE_PROVENANCE_TOKENS`, `GH_HOST`, `GITHUB_API_TOKEN`,
   `ACTIONS_RUNTIME_TOKEN`, `MCP_RELEASE_NO_HTTPS`,
   `DOTNET_SYSTEM_NET_HTTP_ALLOW_UNENCRYPTED`).
3. Writes only the dummy sentinel values required by each scenario
   (`MCP_RELEASE_DO_UPLOAD` set to `"dummy"`, `"true"` or absent; `GITHUB_TOKEN` set
   to the constant `dummy-published-probe` for the authorized case). The sentinel
   values are not real credentials and live only in memory for the child's lifetime.
4. Captures the child via `ReleaseTestProcess.capture`, which uses
   `WaitForExitAsync` together with `ReadToEndAsync` and a bounded cancellation. The
   parent code does not call `Async.RunSynchronously`, `WaitForExit`,
   `GetAwaiter`, `.Result` or any other blocking bridge.
5. Discards the child process immediately after capture because the dummy values are
   not restored to the parent (the child is ephemeral).

The child probe runs in `Release.Tests.exe` through the normal Expecto
`runTestsWithCLIArgs` runner. The dispatch path through `Main.fs` invokes
`Authority.probeCase` for the given mode and producer, which executes
`runPublishPipelineWithTransportFactory` via reflection on the production type with
the typed `HttpTransport` factory the test supplies — the production
`defaultHttpTransport` factory is never reached.

## What the production source actually constructs

The fixed-tag positive probe asserts the canonical URL pattern the production source
builds. The relevant lines are:

- `Runner.fs`: `let locator = { Owner = repositoryOwner; Repository = repositoryName;
  Tag = policy.ComponentId }` — the tag is the canonical component id with no `v`
  prefix.
- `Publish.fs`: `let uri = apiUrl $"repos/{locator.Owner}/{locator.Repository}/releases/tags/{locator.Tag}"` —
  the URL uses the locator owner, repository and tag without any `v` prefix.

The probe handler records the first HTTP request it observes (the search-the-release
GET) and the parent asserts the URL contains the canonical path with no `v` prefix and
that the method is not `POST`, so the scripted HTTP 500 stops the pipeline before any
upload attempt.

## Test helper boundary

- `Authority.probeCase` is exposed as `internal` and consumed only by `Main.fs`. No
  production runtime API is added.
- `Authority.tests` is registered as a `testList` of two cases. The legacy
  `testSequenced` wrapper around `tests` in `Main.fs` was removed; it never protected
  host environment values and is no longer required because the parent process does
  not touch the environment at all.
- The `withIsolatedEnv` helper that read `Environment.GetEnvironmentVariable` values
  for `GITHUB_TOKEN` / `GH_TOKEN` (and other publication names) into local variables
  was removed entirely. The replacement never calls `GetEnvironmentVariable` for any
  credential name.
- The candidate fixture continues to flow through `ReleaseHandoff.readRawTransport`
  and `ReleaseHandoff.parsePreparationReceipt`, whose inner field types
  (`SchemaVersion`, `InnerArchiveSha256`, `InnerArchiveSize`, `ManifestSha256`,
  `ManifestSize`, `SourcePath`, `SourceSha256`) remain stable.

## Out-of-scope items

- Host environment variables are never restored because no environment write
  escapes the child's ephemeral process; the parent process environment is unchanged
  at the conclusion of every probe.
- No production source, public API, environment endpoint or auxiliary workflow path
  was overridden.
- No credential store, session store, real network call, source production check,
  PSBootstrap test, foreign checkout, commit or workflow state is touched.

The next build is expected to be performed by the engineering role that owns the
project build. The two registered cases are ready to execute; no test run was
triggered during this review.
