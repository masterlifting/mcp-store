# MCP store

The clean-slate producer set contains exactly these MCP producers:

| Producer | Source boundary | Release/runtime identity |
| --- | --- | --- |
| `Mcp.Dotnet` | `dotnet/` | `dotnet` distribution, `Mcp.Dotnet.dll` |
| `Mcp.Workflow` | `workflow/` | `workflow` distribution, `Mcp.Workflow.dll` |
| `Telegram` | `telegram/` | `telegram-mcp` Python package, `telegram` MCP server |

Dotnet and Workflow publish deterministic framework-dependent distributions.
Telegram is the Python producer and is tested from its `telegram/` boundary with
`uv`.
