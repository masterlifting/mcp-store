# Channels

The `ReleaseRunner publish` entrypoint consumes three receipts on three
explicit, isolated channels. The role string on each channel is a
deliberately independent identity field; the publisher never binds them
together, because they describe different artifacts and different jobs.

## Channel A — Trusted publisher receipt

- **Source**: independently built trusted builder job (`Bootstrap.ps1 -Mode
  InstallRuntime -Role publisher`).
- **CLI flag**: `--trusted-receipt=<builder-emitted-receipt.json>`.
- **Authority**: only source of every observed identifier (repo,
  event SHA, run/attempt, observed SDK/runtime/platform, roster SHA-256,
  per-component Config.fsx SHA-256s, runtime paths + sizes + hashes).
- **Role value**: `publisher`. Enforced in `crossCheckReceipt` against
  the literal `publisher`; independent of the publish CLI `--role`.
- **Why independent**: the trusted builder installs the publisher
  runtime in a fresh isolated job against a platform artifact the
  candidate cannot author. Binding its `role` to the CLI `--role`
  would conflate the trusted channel with the candidate channel and
  reject a correctly emitted receipt.

## Channel B — Bootstrap FetchCandidate receipt

- **Source**: transport-only bootstrap that observed the platform
  artifact (`Bootstrap.ps1 -Mode FetchCandidate -Role candidate`).
- **CLI flag**: `--transport-evidence=<fetchcandidate-bootstrap-receipt.json>`.
- **Authority**: every observed platform metadata field (artifact ID,
  name, observed SHA-256, observed size, uploader job ID, conclusion,
  head SHA, run-attempt metadata).
- **Role value**: `candidate`. Enforced in `crossCheckBootstrapReceipt`
  against the CLI `--role` because the bootstrap receipt and the CLI
  describe the same fetched artifact; the match is real, not coincidental.
- **Strong existing checks** (preserved verbatim): `status=fetched`,
  `component` non-null and equal to CLI `--component`, artifact ID/name
  equality, observed SHA-256 equals CLI `--transport-sha256`, uploader
  job conclusion `success` and numeric job ID, run-attempt head SHA
  equals CLI `--event-sha`.

## Channel C — Publish CLI

- **Source**: the workflow-supplied `--key=value` flags on the
  `ReleaseRunner publish` command line.
- **Role value**: `candidate`. Enforced in `parsePublishCli` against
  the `allowedRoles = [ "candidate" ]` allowlist before any file read,
  token read, or HTTP transport is composed.
- **Why `candidate`-only**: the publisher never consumes
  Bootstrap.ps1 InstallRuntime outputs (status `installed`, role
  `publisher`/`runtime`, component `null`) as publish input; those are
  protected BOOTSTRAP installation evidence.

## What the publisher enforces vs what it does not

- Enforced across channels: repository, run/attempt, event SHA,
  observed SDK / runtime / platform, roster SHA-256, per-component
  Config.fsx SHA-256s, runtime triplet cross-consistency.
- NOT enforced across channels: role equality. The CLI `--role` is
  validated once against `allowedRoles`; the trusted receipt role is
  validated against `publisher`; the bootstrap receipt role is
  validated against the CLI role (because both describe the same fetch).
- Old "trusted receipt role" wording is INVALID forward-only; the new
  wording is `trustedReceipt.role=publisher`. No alias, no migration.