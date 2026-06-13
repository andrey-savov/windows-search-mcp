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

### Stripped-environment guard (`EnsureSearchComEnvironment`)

The Windows Search OLE DB provider (`Search.CollatorDSO`) resolves the OLE DB **core components** (`%CommonProgramFiles%\System\Ole DB\oledb32.dll`) during `conn.Execute`. Some MCP hosts — notably the **MSIX build of Claude Desktop** — launch the server with a drastically stripped environment (~15 variables) that **omits `%CommonProgramFiles%`**. Without it, `conn.Open` still succeeds but `conn.Execute` fails with `REGDB_E_CLASSNOTREG` (0x80040154). (Property enumeration via `propsys.dll` is unaffected, so `list_properties*` keep working regardless.)

This was diagnosed empirically: the *same* binary works when launched from a normal shell/Claude Code but fails under Claude Desktop, with identical user/session/integrity/bitness and **no** package identity. The only difference was the environment block; adding back `CommonProgramFiles` (or `CommonProgramW6432`) alone fixes it.

`Search()` calls `EnsureSearchComEnvironment()` before touching COM. It repopulates `CommonProgramFiles`, `CommonProgramFiles(x86)`, and `CommonProgramW6432` from `Environment.GetFolderPath(SpecialFolder.CommonProgramFiles*)` — the known-folder paths come from the shell-folders registry and do **not** depend on the (stripped) environment block. If `REGDB_E_CLASSNOTREG` still occurs, `Search()` rethrows it as an actionable `McpException` instead of the opaque "Class not registered".

Set `WS_SEARCH_DIAG=1` to enable diagnostic logging (`Diag`) to `ws-search-diag.log` next to the executable; it is off by default.

### Windows property enumeration (WindowsPropertySystem.cs)

P/Invokes `PSEnumeratePropertyDescriptions` from `propsys.dll` to walk ~600 registered `System.*` property descriptors via COM interfaces. The full list is lazy-initialized once and cached in a static `Lazy<>`. `ByFamily` and `Search` are pure LINQ filters over that cache.

### Key invariants

- **`scope` has three cases**: omitted / `"<UNSET>"` (sentinel) → `%USERPROFILE%` and subfolders; `""` (empty) → the entire index (including non-`C:` drives); an absolute path → that folder and its subfolders. The public tool description spells all three out explicitly so callers don't confuse "default" with "whole PC".
- **`contains` mode** produces `(CONTAINS('"term"') OR System.FileName LIKE '%term%')` — both full-text and filename are always tried.
- **Quote handling**: double quotes would break the `CONTAINS('"…"')` phrase wrapper, so `SanitizeFullText` collapses them to spaces before building the phrase (they are word-breakers in full-text search anyway). The same applies to the `CONTAINS` filter operator value.
- **Apostrophe canonicalization in `LIKE`**: `BuildLikePattern` maps the apostrophe/quote family (`'` `‘` `’` `ʼ` `` ` `` `´`) to the `LIKE` single-character wildcard `_`, so a query typed with a straight apostrophe still matches a filename stored with a typographic one (files are commonly named `Name’s …` with U+2019 while users type U+0027). Canonicalizing only the query to a straight quote does **not** work, because `LIKE` compares the indexed filename literally and the index keeps the original character. Double quotes (illegal in Windows filenames) are dropped to spaces.
- **SQL injection prevention**: property names are validated against `^[A-Za-z_][A-Za-z0-9_.]*$`; string values are single-quote–escaped via `EscapeSqlLiteral`.
- **`DP://` entries**: some index entries have an empty `System.ItemPathDisplay` and a `DP://{SID}/X:/path` in `System.ItemUrl`. The `Search` method silently fetches `ItemUrl` and recovers the path from it, stripping it from the returned row if the caller didn't ask for it.

### Test scope

`tests/WsSearchMcp.Tests/WindowsSearchToolTests.cs` tests `BuildSql()` and `ListProperties*()` only — pure logic with no COM or ADODB calls. There are no integration tests against the live Windows Search index.
