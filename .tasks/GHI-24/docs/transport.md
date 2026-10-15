# GHI-24 W4 transport seam — HttpTransport injection + relative-URI fix

## Status: production correction applied; W4 unblocker record
              (`tests.md`) re-runnable end-to-end with the new
              absolute-URI construction. Comment cleanup applied to
              the new/changed release-module files per the comment
              rules. Builds clean. Regression suite (tester) and
              hosted end-to-end remain blocked on the post-commit
              hosted run. No regression edits; no `.git` mutation;
              no actual remote write; no task-runtime changes. The
              previously-frozen `Release.fsproj` `Bootstrap.fsx`
              Compile-item exclusion (Option B from `debug.md`) is
              preserved.

Authority: `docs/GHI-24/original.md` (verbatim),
`docs/GHI-24/solution.md` (frozen), `docs/GHI-24/tests.md` (W4
unblocker that surfaced this defect), `docs/GHI-24/debug.md`
(root-cause + repair), the `capabilities/software/knowledge/{comments,architecture}.md`
comment rules.

## 1. Production correction (root cause and fix)

The W4 unblocker record (`tests.md` §1) noted that the real injected
HTTP tests exposed an `assertHostAllowed` failure on relative URIs.
The defect: the production callers constructed
`HttpRequestMessage(HttpMethod.Get, "repos/owner/repo/releases/tags/tag")`
with a *relative* path string, and `assertHostAllowed` reads
`request.RequestUri.Host` to validate it. A relative URI's
`Host` is empty, so the guard failed closed on every call and
the mock handler never saw a request.

The smallest fix: the production code now constructs the request
URI as an absolute URL at the production boundary, before the host
guard runs.

```fsharp
let private apiBaseUri   = Uri "https://api.github.com/"
let private uploadBaseUri = Uri "https://uploads.github.com/"

let private apiUrl   (relativePath: string) : Uri = Uri(apiBaseUri, relativePath)
let private uploadUrl (relativePath: string) : Uri = Uri(uploadBaseUri, relativePath)
```

`gitHubObserver` / `uploadCandidate` / `uploadMetadata` now call
`new HttpRequestMessage(HttpMethod.Get, apiUrl releasePath)` (or
`uploadUrl …`) so `request.RequestUri` carries the absolute host
before the host guard runs. The allowlist check is preserved exactly;
no step is skipped, no test mock is bypassed. The `followIfSafe`
helper was already producing absolute URIs, so it is unchanged.

This is the entire fix: one helper pair, four call-site edits, no
behaviour change for a correctly configured production transport,
and the W4 `RecordingHandler`-based tests can now assert against
absolute URLs.

## 2. Comment cleanup summary

Applied per the comment rules to all changed files in this slice
plus the touched producer `Config.fsx` and producer `Release.fsx`
scripts. The pre-existing `Compare.fsx` / `Decide.fsx` / `Transaction.fsx` /
`Metadata.fsx` were not touched in this slice, so no comments there
were rewritten.

| file | change |
| --- | --- |
| `infrastructure/release/Publish.fsx` | file header trimmed to one role line + three content points; 50+ line "Two seam contracts" narrative replaced with a single one-line `HttpTransport` purpose statement; removed "W3 contract" / "idempotent recovery" / "W3 dev mode" narrations; preserved the `assertHostAllowed` "why" (test author can wire permissive handler); "W3 dev mode" string kept verbatim in the typed error message because the test asserts on it |
| `infrastructure/release/Runner.fs` | file header trimmed to one `.fs` extension rationale; removed the "W3 contract" list and the exit-code table; removed "the published candidate hash is the only digest that the runner treats as authoritative" (restated code) and similar what-narratives; preserved one why-only "no `null` aliases for the published values" rule and the matching `assertHostAllowed`-adjacent rule |
| `infrastructure/release/Release.fsproj` | the 13-line XML comment about `Bootstrap.fsx` was trimmed to one line that records the constraint and the consequence (`Main` hangs on net11.0 RC); the underlying fix (excluding `Bootstrap.fsx` from `<Compile>` items) is unchanged |
| `dotnet/release/Config.fsx`, `workflow/release/Config.fsx` | file header trimmed to one role line + one content point; the 8-line "1.0.3 -> 1.0.4" rationale trimmed to a single one-line why (the binary differs, the revision-only exception cannot apply); the ticket-IDs / migration-history lines were removed; the 6-line "Canonical toolchain selection" comment was removed (it restated the code); the 3-line archive-name comment was removed (restated the code) |
| `dotnet/release/Release.fsx`, `workflow/release/Release.fsx` | file header trimmed to one role line + one content point; the exit-code table was removed from the script header (CLI exits belong in docs, not banners) |

The exit-code contract now lives in §8 below; it is not repeated in
any script header. The Runner JSON output shape is recorded in
`docs/release/Requirements.md` (canonical) and the per-component
summary renderer in the producer `Release.fsx` scripts is unchanged
because the JSON keys it reads are stable.

## 3. Surface authored in this slice

| path | role | notes |
| --- | --- | --- |
| `infrastructure/release/Publish.fsx` | reusable module/helper | `HttpTransport` record, `defaultHttpTransport ()`, `apiBaseUri`/`uploadBaseUri` constants, `apiUrl`/`uploadUrl` helpers, `apiBytesRequestAsync` for the binary download path; `gitHubObserver` / `uploadCandidate` / `uploadMetadata` take `transport: HttpTransport` and build absolute URIs at the production boundary |
| `infrastructure/release/Runner.fs` | compiled entrypoint | `defaultHttpTransport ()` constructed once per producer run; passes the transport to the three production entrypoints |
| `infrastructure/release/Release.fsproj` | build file | `Bootstrap.fsx` exclusion XML comment trimmed to one line |
| `dotnet/release/Config.fsx`, `workflow/release/Config.fsx` | reusable module/helper | file header and per-line comments trimmed; rationale kept as a one-line why |
| `dotnet/release/Release.fsx`, `workflow/release/Release.fsx` | release/build entrypoint | file header trimmed; exit-code table removed (moved to §8) |
| `docs/GHI-24/transport.md` | evidence | this file |

`Bootstrap.fsx`, `Update.ps1`, `.github/workflows/release.yml`,
`Provenance.fsx`, `Compare.fsx`, `Decide.fsx`, `Transaction.fsx`,
`Metadata.fsx`, the existing test files in
`infrastructure/release/tests/`, and every other frozen source
file are untouched.

## 4. Exact injected API signatures

```fsharp
type HttpTransport =
    { CreateApiClient:      unit -> HttpClient
      CreateUploadClient:   unit -> HttpClient
      CreateRedirectClient: unit -> HttpClient }

let defaultHttpTransport () : HttpTransport
val gitHubObserver : AuthToken -> downloadDir:string -> HttpTransport -> RemoteObserver
val uploadCandidate : AuthToken -> ReleaseLocator -> assetName:string -> candidatePath:string -> expectedDigest:string -> HttpTransport -> Async<Result<string, ReleaseError>>
val uploadMetadata   : AuthToken -> ReleaseLocator -> ReleaseMetadata.ProducerMetadataProjection -> HttpTransport -> Async<Result<string, ReleaseError>>
```

A `HttpMessageHandler`-based test transport is the natural shape:
a test author subclasses `HttpMessageHandler`, programs a queue of
canned responses, and constructs an `HttpTransport` whose three
factories all return `new HttpClient(handler)`. The factories are
independent so a test can install different handlers per transport,
but a single shared handler is the common case.

The runner constructs one transport per producer run:

```fsharp
let transport = ReleasePublish.defaultHttpTransport ()
let ghObserver = ReleasePublish.gitHubObserver auth.Value downloadDir transport
let archiveResult = ReleasePublish.uploadCandidate
                       authValue locator contract.ArchiveName archivePath
                       candidateArchiveSha256 transport
                       |> Async.RunSynchronously
let metadataResult = ReleasePublish.uploadMetadata
                        authValue locator projection transport
                        |> Async.RunSynchronously
```

No global mutable state, no per-test environment flag, no parallel
upload implementation.

## 5. Production host allowlist preserved

The default transport pins each `HttpClient` either to
`https://api.github.com/` (API host, with `BaseAddress` set) or to a
bare `HttpClient` taking an absolute URL (upload and redirect
hosts). `assertHostAllowed` runs before every HTTP call: a request
whose host is not in the `api.github.com` / `uploads.github.com`
allowlist fails closed with a typed `host` error, even when a test
author wires a permissive `HttpMessageHandler`. The guard reads
`request.RequestUri.Host` directly, and after the relative-URI fix
the URI is always absolute so the guard sees a real host.

The safe-redirect policy (`followIfSafe`) still refuses redirects
to any host other than `api.github.com`/`github.com` and refuses
redirects with a missing `Location` header. The bearer token is
re-applied to the redirected request only when the redirect target
is in the allowlist.

The `MCP_RELEASE_DO_UPLOAD` dev-mode gate is unchanged. The dev
behaviour (refuse an actual remote write without the flag) is
preserved. The typed error message keeps the historical
`"W3 dev mode: refusing to upload …"` text verbatim because the
W4 test asserts on it; the surrounding code comments no longer
narrate that label.

## 6. Tests the seam enables (tester-owned, not authored here)

The seam makes every production HTTP path observable to a
deterministic test. Specific cases the seam enables, each of which
exercises the absolute-URI construction and the host guard:

1. Idempotent recovery on existing asset with matching digest.
2. No-overwrite refusal on existing asset with different digest.
3. Immutable refusal (`isImmutable: true` in the lookup response).
4. Missing release (no `id` in the lookup response).
5. Safe redirect to a `github.com` host (handler records the
   absolute follow-up URL).
6. Unsafe redirect to a non-`github.com` host (handler sees only
   the first request; the typed `host`/`redirect` error fires
   before the second call).
7. Upload POST timeout / 5xx → typed failure, no retry (handler
   records exactly one POST).
8. Upload POST digest mismatch → typed failure.
9. Defensive host guard: hand the production function a transport
   whose handler would route every request to a synthetic host;
   the typed `host` error fires before the handler is invoked.

## 7. Build evidence (`dotnet_build` MCP, Release config, 0/0)

| target | runId | exit | notes |
| --- | --- | --- | --- |
| `infrastructure/release/Release.fsproj` | `8kvDQcunv8v-y5l8q7PVhIntN2l22UheCmogZMgCTfs` | 0 | first relative-URI fix draft |
| `infrastructure/release/Release.fsproj` | `MCKOzglHGEgffq7bh7WXgbk2bRTF8iBZPg_xu-eunQg` | 0 | after Runner.fs comment cleanup |
| `infrastructure/release/Release.fsproj` | `r-e_UEWjJoIRNH0mgAZz9anBa4g_-xAfOwsIk_6C7-Y` | 0 | after Release.fsproj XML comment trim |
| `infrastructure/release/Release.fsproj` | `Lynwh4v4GXZqthhjtsndFpinq8jEkv5_BtvIuOMZjy0` | 0 | final after Config.fsx and producer Release.fsx cleanup |
| `infrastructure/scripts/Validation.fsproj` | `xYdALuJpX7JSLMfXzZW9pseWzGlYGY-cueYKImNjiis` | 0 | analyzer rebuilt; 0 errors / 0 warnings |
| `dotnet/Mcp.Dotnet.fsproj` | `CDQAuOOga6avhTaQVLQuuY-m25geXBc2KBa9OLSt3CA` | 0 | producer rebuilt; 0 errors / 0 warnings |
| `workflow/Mcp.Workflow.fsproj` | `n6CMbUdDLy-AlnxE8qJU811-DuMccm8-hQaqIZyvjPY` | 0 | producer rebuilt; 0 errors / 0 warnings |

`Validation validate`: `OK 47 scripts validated` (the W4 unblocker
added two test files; the count is up from the pre-W4 38).
`Validation typecheck`: `OK reusable and test-helper roots typechecked`.

## 8. Runner contract — exit codes and JSON shape

Exit codes (moved here from the per-component `Release.fsx` script
header per the comment rules):

- `0` — `published` or `unchanged`; the upload either completed
  (production transport with the upload flag) or was not required
  (the existing archive matches under the strict comparison).
- `1` — `failed`; validation, build, pin, test, or transport error.
- `2` — `bump-required` or `publish-pending`; the strict comparison
  said the published archive differs beyond the narrow
  revision-only exception, or the asset was absent on the remote
  release and the upload was not authorised.
- `4` — clean-tree / HEAD / identity guard failure.

The runner emits a single JSON object on stdout. Field names are
stable and match the canonical contract recorded in
`docs/release/Requirements.md` (canonical). The per-component
summary renderer in the producer `Release.fsx` scripts reads
those keys verbatim.

## 9. Still unverified (ambiguous-upload paths)

After the relative-URI fix, the seam's behaviour is the same
real logic as the production code. The seam does **not** test:

- The exact `api.github.com` request envelope the live API expects
  (a `MockHttpMessageHandler` returns canned responses; it does
  not replay against a recording of the live API).
- The exact `uploads.github.com` POST envelope the live API expects
  (same reason).
- A live `queue: max` behaviour on the hosted runner.
- A real `git config` / `GITHUB_TOKEN` round trip.
- **A direct second round trip inside `uploadMetadata` itself
  after a successful POST**. The current production code treats
  the response digest as authoritative and does not re-fetch.
  The runner's archive-then-metadata sequence (Runner.fs
  `PublishPending` branch) provides the
  archive-success-then-metadata-failure recovery through the
  metadata upload's release-lookup GET. Tests can now verify
  that round trip because the relative-URI fix makes the lookup
  reach the mock handler. A direct second round trip *inside*
  `uploadMetadata` is an optional future hardening, not a
  current production gap.
- The "upload timeout followed by observation" sequence from the
  original issue: the current `uploadCandidate` returns
  `Error(timeout)` on `OperationCanceledException`; the next
  evaluation observes the remote through the metadata upload's
  GET. Tests can inject a mock that returns 5xx then a valid
  payload on the next call and assert that the production code
  fails the first call, recovers the asset on the second
  evaluation's GET, and either completes or fails closed.

The W4 `tests.md` §3 enumeration of "unverified hosted" requirements
(rows 15, 18, 19, 20, 22, 24, 37, 38, 39, 40, 44, 46, 48, 50, 54,
55, 63, 70) remains the authoritative list of seams that require
hosted observation, not the unit-test seam this slice added.

## 10. Original baseline remains

`git rev-parse HEAD` is still `3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1`.
`git reflog` is unchanged. `.git/info/exclude` is the committed
template. The frozen `Release.fsproj` `Bootstrap.fsx` Compile-item
exclusion (Option B from `debug.md`) is intact; the runner
binary's empty-argv exit is unchanged.

## 11. Files in this slice (working tree, uncommitted)

```
M  infrastructure/release/Publish.fsx          (relative-URI fix; comment cleanup; apiBaseUri/uploadBaseUri + apiUrl/uploadUrl helpers)
M  infrastructure/release/Runner.fs             (defaultHttpTransport () wired; comment cleanup)
M  infrastructure/release/Release.fsproj        (XML comment trim)
M  dotnet/release/Config.fsx                    (header + version-bump rationale + toolchain/archivename comments trimmed)
M  workflow/release/Config.fsx                  (same)
M  dotnet/release/Release.fsx                   (header + exit-code table trimmed)
M  workflow/release/Release.fsx                 (same)
M  docs/GHI-24/transport.md                     (this file)
```

Untracked from earlier slices that this slice keeps (none modified):
```
?? .github/workflows/release.yml
?? docs/GHI-24/{core,debug,devops,implementation,metadata,original-issue,packages,research,solution,tests}.md
?? infrastructure/release/{Bootstrap,Compare,Decide,Metadata,Publish,Transaction}.fsx
?? infrastructure/release/Release.fsproj
?? infrastructure/release/Runner.fs
?? infrastructure/release/tests/
?? {dotnet,workflow}/release/Release.fsx
```

No commits, branch operations, worktree mutations, or remote writes
were performed against the source repo during this slice.
