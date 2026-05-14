# AGENTS.md

This file provides guidance to coding agents working with code in this repository.

## Build and test

```powershell
dotnet build -c Release
dotnet test
dotnet test --filter "FullyQualifiedName~BuildSql_ClampsMaxResults"  # single test
```

Target: `net10.0-windows` (Windows only). Requires the Windows Search service running for the COM path; unit tests run without it.

## Debug / manual tool invocation

The binary has a built-in debug mode that bypasses the MCP stdio loop:

```powershell
.\bin\Release\net10.0-windows\ws-search-mcp.exe --tool query --input '{"query":"readme","scope":"","maxResults":5}'
.\bin\Release\net10.0-windows\ws-search-mcp.exe --tool list_properties --input '{"filter":"camera"}'
```

## Architecture

### Two runtime modes (Program.cs)

`Program.cs` checks for `--tool` on `args` at startup. If present it dispatches directly to the static tool methods and exits; otherwise it starts the MCP stdio server via `Microsoft.Extensions.Hosting` + `ModelContextProtocol`.

### Three MCP tools (WindowsSearchTool.cs)

All three are `public static` methods on `WindowsSearchTool` decorated with `[McpServerTool]`. The SDK discovers them via `WithToolsFromAssembly()`.

- **`query`** — builds a `SELECT … FROM SYSTEMINDEX WHERE …` query and executes it over COM (`ADODB.Connection` + `Search.CollatorDSO` provider). Returns a JSON array where every row always includes `System.ItemPathDisplay`.
- **`list_properties`** — delegates to `WindowsPropertySystem.Search()`.
- **`list_properties_by_family`** — delegates to `WindowsPropertySystem.ByFamily()`.

SQL generation is isolated in `BuildSql()` (marked `internal` so tests can call it directly without touching COM). The execution path opens/closes the ADODB connection inline.

### Windows property enumeration (WindowsPropertySystem.cs)

P/Invokes `PSEnumeratePropertyDescriptions` from `propsys.dll` to walk ~600 registered `System.*` property descriptors via COM interfaces. The full list is lazy-initialized once and cached in a static `Lazy<>`. `ByFamily` and `Search` are pure LINQ filters over that cache.

### Key invariants

- **`scope` defaults to `%USERPROFILE%`** when `null`; pass `""` to search the entire index (including non-`C:` drives).
- **`contains` mode** produces `(CONTAINS('"term"') OR System.FileName LIKE '%term%')` — both full-text and filename are always tried.
- **SQL injection prevention**: property names are validated against `^[A-Za-z_][A-Za-z0-9_.]*$`; string values are single-quote–escaped via `EscapeSqlLiteral`.
- **`DP://` entries**: some index entries have an empty `System.ItemPathDisplay` and a `DP://{SID}/X:/path` in `System.ItemUrl`. The `Search` method silently fetches `ItemUrl` and recovers the path from it, stripping it from the returned row if the caller didn't ask for it.

### Test scope

`tests/WsSearchMcp.Tests/WindowsSearchToolTests.cs` tests `BuildSql()` and `ListProperties*()` only — pure logic with no COM or ADODB calls. There are no integration tests against the live Windows Search index.
