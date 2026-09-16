# GitHub MCP Server

This package provides a portable Windows installer for the official GitHub MCP
server. It installs the server with Go and leaves OpenCode configuration and
authentication unchanged.

## Prerequisites

- Windows PowerShell 5.1 or PowerShell 7+
- A supported Go installation with the `go` executable available through PATH
- Permission to install Go binaries into the configured Go binary directory
- An MCP-compatible OpenCode installation for consuming the server

The installer does not read, write, or configure credentials. Any
authentication required by the server remains the responsibility of the
consuming OpenCode setup.

## Pin evidence

The installer uses the official GitHub MCP server module at the immutable
version reference `v1.12.2`:

```text
github.com/github/github-mcp-server/cmd/github-mcp-server@v1.12.2
```

The pin is evidenced by the official upstream repository:

- Tag: [`v1.12.2`](https://github.com/github/github-mcp-server/releases/tag/v1.12.2)
- Annotated-tag object: `c4e03a622ff9d9e62b251130c89899d4f1081240`
- Target commit: [`85598ba6e1256f7ebf4867b95d63b833c4549264`](https://github.com/github/github-mcp-server/commit/85598ba6e1256f7ebf4867b95d63b833c4549264)

## Installation

From this package directory, run:

```powershell
.\install.ps1
```

The installer runs:

```powershell
go install github.com/github/github-mcp-server/cmd/github-mcp-server@v1.12.2
```

It reports the Go binary directory used by the installation and then requires
`Get-Command github-mcp-server` to resolve `github-mcp-server.exe` from that
directory through PATH. The installer never changes PATH. If the command is
missing or resolves to a conflicting executable elsewhere, it fails with
instructions instead of reporting a false success.

## PATH configuration

If the installer reports that `github-mcp-server` is missing from PATH or a
conflicting command resolves first, add the reported Go binary directory
manually:

1. Open **System Properties** → **Advanced** → **Environment Variables**.
2. Under **User variables**, select `Path`, choose **Edit**, choose **New**,
   and enter the reported directory.
3. Confirm the dialogs and open a new PowerShell session.

When `GOBIN` is configured, its value is the Go binary directory. Otherwise,
the installer uses the first `GOPATH` entry followed by `bin` (normally
`%USERPROFILE%\go\bin`). Inspect the values with:

```powershell
go env GOBIN
go env GOPATH
```

## Verification

In a new PowerShell session, verify that the executable resolves through PATH:

```powershell
Get-Command -Name github-mcp-server -CommandType Application
```

The command should show the installed executable under the Go binary directory.

## OpenCode invocation

Use this invocation unchanged:

```text
github-mcp-server stdio --toolsets=context,repos,pull_requests,issues
```

This package does not change OpenCode configuration, toolsets, or
authorization semantics.
