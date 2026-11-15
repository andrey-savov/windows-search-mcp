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
.\bin\Release\net10.0-windows\ws-search-mcp.exe --tool find_files --input '{"text":"readme","scope":"","maxResults":5}'
.\bin\Release\net10.0-windows\ws-search-mcp.exe --tool list_properties --input '{"filter":"camera"}'
```

## Using the ws-search MCP tools (Claude Code)

The ws-search MCP tools are deferred in Claude Code — load schemas before calling:

```
ToolSearch(query: "select:mcp__ws-search__find_files,mcp__ws-search__list_properties,mcp__ws-search__list_properties_by_family")
```

### Calling the find_files tool

`text` is required by the schema; all other params have defaults. Example:

```
mcp__ws-search__find_files(text="Erin Dawn", scope="")
```

- `text`: search term (full-text + filename match). Pass `""` if you only want filter-based results.
- `scope`: **where to search — three cases.** Omit it or pass `"<UNSET>"` → the **entire** index, all indexed locations and drives (the default). Pass `""` (empty string) → the entire index. Pass an absolute folder path (e.g. `"C:\\Users\\me\\Documents"`) → only that folder and its subfolders.
- `filters`: a JSON string like `'[{"property":"System.Kind","op":"=","value":"picture"}]'`. Pass `""` for no filters.
- `columns`: comma-separated extra `System.*` columns to include in results, e.g. `"System.DateModified,System.Size"`.

Example searching entire index:

```
mcp__ws-search__find_files(text="contract", scope="", maxResults=20)
```

## Architecture

### Projects and runtime modes

| Project | Output | Role |
|---------|--------|------|
| `src/WsSearchMcp.Core` | library | The tools (`WindowsSearchTool`, `WindowsPropertySystem`, `Diag`) and the `WsSearchMcp.Security` namespace (tokens, client stores, certificates, paths). No ASP.NET dependency. |
| `ws-search-mcp.csproj` (repo root) | `ws-search-mcp.exe` | stdio MCP server + `--tool` debug mode. **Name, path and behaviour must not change** — local clients register this exe. |
| `src/WsSearchMcp.Http` | `ws-search-mcp-http.exe` | Authenticated HTTPS MCP server (ASP.NET Core/Kestrel, `ModelContextProtocol.AspNetCore`). |
| `src/WsSearchMcp.Admin` | `ws-search-mcp-admin.exe` | Local CLI: `client mint/list/revoke`, `cert init/show/export/renew/use`. Logic is in the testable `AdminCli` class. |

The root `ws-search-mcp.csproj` must `Compile Remove="src\**\*.cs"` (and `tests\**`) or it will compile the sibling projects' sources. Tools are discovered with `WithToolsFromAssembly(typeof(WindowsSearchTool).Assembly)` because they no longer live in the entry assembly.

`Program.cs` (stdio exe) checks for `--tool` on `args` at startup. If present it dispatches directly to the static tool methods and exits; otherwise it starts the MCP stdio server via `Microsoft.Extensions.Hosting` + `ModelContextProtocol`.

### HTTPS hosting and client credentials (Http + Core.Security)

- `McpHttpServer.Build(options)` is the single entry point (used by `Program.cs` and the integration tests). HTTPS only, `Stateless = true` (no `Mcp-Session-Id`), `ListenAnyIP` by default, 1 MB body limit. The host builder is given an **empty** args array so CLI flags never leak into configuration.
- A middleware ahead of `MapMcp()` rejects browser `Origin`s (unless allow-listed) with 403, then requires `Authorization: Bearer <token>` (401 + `WWW-Authenticate: Bearer`). Failure reasons are fixed strings; the token is never logged.
- **Token = ES256 JWS** (`ClientTokens`). `Mint` generates a P-256 key, signs the token, stores only a **public-key-only self-signed certificate** (subject `CN=<name>, OU=<kid>`; `NotBefore` = created, `NotAfter` = expiry, or +100y meaning "never"), and drops the private key. `Validate` pins `alg` to `ES256`, checks signature (IEEE P1363), `aud`, `exp`, and the client's own expiry. Never accept other algorithms.
- `IClientStore`: `WindowsCertClientStore` (`CurrentUser\WsSearchMcp.Clients`), `InMemoryClientStore` (tests), `CachingClientStore` (3 s TTL so out-of-process revocation takes effect without restart). Revoked clients are *removed*, so `client list` never shows them.
- Server TLS cert: ECDSA P-256 with a persisted, **non-exportable** CNG key in `CurrentUser\My`; thumbprint in `%APPDATA%\ws-search-mcp\config.json` (override dir with `WS_SEARCH_HOME`; directory ACL = user + SYSTEM). `usage.json` holds throttled per-client last-used times.
- The server and admin tool must run as the same Windows user (shared per-user stores) and the server must run as the signed-in user so Windows Search returns that user's index.

### Three MCP tools (src/WsSearchMcp.Core/WindowsSearchTool.cs)

All three are `public static` methods on `WindowsSearchTool` decorated with `[McpServerTool]`. The SDK discovers them via `WithToolsFromAssembly(typeof(WindowsSearchTool).Assembly)` (both the stdio and HTTP hosts).

- **`find_files`** (C# method `WindowsSearchTool.Search`) — builds a `SELECT … FROM SYSTEMINDEX WHERE …` query and executes it over COM (`ADODB.Connection` + `Search.CollatorDSO` provider). Returns a JSON array where every row always includes `System.ItemPathDisplay`.
- **`list_properties`** — delegates to `WindowsPropertySystem.Search()`.
- **`list_properties_by_family`** — delegates to `WindowsPropertySystem.ByFamily()`.

SQL generation is isolated in `BuildSql()` (marked `internal` so tests can call it directly without touching COM). The execution path opens/closes the ADODB connection inline.

### Stripped-environment guard (`EnsureSearchComEnvironment`)

The Windows Search OLE DB provider (`Search.CollatorDSO`) resolves the OLE DB **core components** (`%CommonProgramFiles%\System\Ole DB\oledb32.dll`) during `conn.Execute`. Some MCP hosts — notably the **MSIX build of Claude Desktop** — launch the server with a drastically stripped environment (~15 variables) that **omits `%CommonProgramFiles%`**. Without it, `conn.Open` still succeeds but `conn.Execute` fails with `REGDB_E_CLASSNOTREG` (0x80040154). (Property enumeration via `propsys.dll` is unaffected, so `list_properties*` keep working regardless.)

This was diagnosed empirically: the *same* binary works when launched from a normal shell/Claude Code but fails under Claude Desktop, with identical user/session/integrity/bitness and **no** package identity. The only difference was the environment block; adding back `CommonProgramFiles` (or `CommonProgramW6432`) alone fixes it.

`Search()` calls `EnsureSearchComEnvironment()` before touching COM. It repopulates `CommonProgramFiles`, `CommonProgramFiles(x86)`, and `CommonProgramW6432` from `Environment.GetFolderPath(SpecialFolder.CommonProgramFiles*)` — the known-folder paths come from the shell-folders registry and do **not** depend on the (stripped) environment block. If `REGDB_E_CLASSNOTREG` still occurs, `Search()` rethrows it as an actionable `McpException` instead of the opaque "Class not registered".

Set `WS_SEARCH_DIAG=1` to enable diagnostic logging (`Diag`) to `ws-search-diag.log` next to the executable; it is off by default.

### Windows property enumeration (src/WsSearchMcp.Core/WindowsPropertySystem.cs)

P/Invokes `PSEnumeratePropertyDescriptions` from `propsys.dll` to walk ~600 registered `System.*` property descriptors via COM interfaces. The full list is lazy-initialized once and cached in a static `Lazy<>`. `ByFamily` and `Search` are pure LINQ filters over that cache.

### Schema design (load-bearing)

The tool's parameters are intentionally all **flat strings or integers** — no `string?` (nullable type unions) and no nested object array params. Earlier revisions used `string?` and `SearchFilter[]?`; Anthropic Claude models (Opus 4.7, Sonnet 4.6) then silently emitted `{}` for the entire `tool_use.input`, producing the terse error `An error occurred invoking '<toolname>'` with no detail. The same schema worked fine with OpenAI/Copilot, which is why this is a Claude-specific concern.

If you add parameters to this tool, keep them as plain `string` / `int` / `bool` with literal defaults. Use sentinel strings (e.g. `""`, `"<UNSET>"`, `"ASC"`) and decode them inside the method body. Do **not** reintroduce `string?` or typed array params in the public surface.

### Key invariants

- **`scope` has three cases**: omitted / `"<UNSET>"` (sentinel) → the entire index (including non-`C:` drives); `""` (empty) → the entire index; an absolute path → that folder and its subfolders.
- **`contains` mode** produces `(CONTAINS('"term"') OR System.FileName LIKE '%term%')` — both full-text and filename are always tried.
- **Quote handling**: double quotes would break the `CONTAINS('"…"')` phrase wrapper, so `SanitizeFullText` collapses them to spaces before building the phrase (they are word-breakers in full-text search anyway). The same applies to the `CONTAINS` filter operator value.
- **Apostrophe canonicalization in `LIKE`**: `BuildLikePattern` maps the apostrophe/quote family (`'` `‘` `’` `ʼ` `` ` `` `´`) to the `LIKE` single-character wildcard `_`, so a query typed with a straight apostrophe still matches a filename stored with a typographic one (files are commonly named `Name’s …` with U+2019 while users type U+0027). Canonicalizing only the query to a straight quote does **not** work, because `LIKE` compares the indexed filename literally and the index keeps the original character. Double quotes (illegal in Windows filenames) are dropped to spaces.
- **SQL injection prevention**: property names are validated against `^[A-Za-z_][A-Za-z0-9_.]*$`; string values are single-quote–escaped via `EscapeSqlLiteral`.
- **`DP://` entries**: some index entries have an empty `System.ItemPathDisplay` and a `DP://{SID}/X:/path` in `System.ItemUrl`. The `Search` method silently fetches `ItemUrl` and recovers the path from it, stripping it from the returned row if the caller didn't ask for it.

### Test scope

`tests/WsSearchMcp.Tests/WindowsSearchToolTests.cs` tests `BuildSql()` and `ListProperties*()` only — pure logic with no COM or ADODB calls. There are no integration tests against the live Windows Search index.

The security/HTTP tests also avoid COM: `HttpServerTests` run `McpHttpServer` in-process on an ephemeral port with a throwaway certificate and an `InMemoryClientStore`, and dispatch only `list_properties`. `WindowsCertClientStoreTests` use a uniquely named throwaway certificate store (deleted on dispose) — never the real `WsSearchMcp.Clients` store or the user's `CurrentUser\My`. The test project needs the `Microsoft.AspNetCore.App` framework reference.
