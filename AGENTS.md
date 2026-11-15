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
| `src/WsSearchMcp.Core` | library | The tools (`WindowsSearchTool`, `WindowsPropertySystem`, `Diag`), the `WsSearchMcp.Security` namespace (tokens, client stores, permissions, certificates, paths) and the `WsSearchMcp.Files` namespace (`FileAccessPolicy` deny list, `IndexedFileResolver`, `NativeFilePath`). No ASP.NET dependency. |
| `ws-search-mcp.csproj` (repo root) | `ws-search-mcp.exe` | stdio MCP server + `--tool` debug mode. **Name, path and behaviour must not change** — local clients register this exe. |
| `src/WsSearchMcp.Http` | `ws-search-mcp-http.exe` | Authenticated HTTPS MCP server (ASP.NET Core/Kestrel, `ModelContextProtocol.AspNetCore`). Also owns the HTTPS-only `get_file` tool (`FileTool`, `FileChunking`, `AcceptList`, `FileClassifier`, `ContentNegotiator`, `Conversion/`). |
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

### `get_file` (HTTPS only) — invariants

`FileTool` (in `src/WsSearchMcp.Http`, registered with `.WithTools<FileTool>()`) returns file contents. It must **never** be registered by the stdio exe or live in Core: `WithToolsFromAssembly(Core)` would then expose it over stdio (`StdioToolSetTests` guards this).

- **Permission is per client and server-side.** `ClientRecord.Permissions` (currently only `files`) is stored in the client certificate subject as `OU=perm:files`; the token format carries no permissions. Unknown permission names in a certificate are ignored, never granted. Clients minted without `--files`, and all pre-existing clients, have none. The auth middleware puts the validated `ClientRecord` in `HttpContext.Items[McpHttpServer.ClientItemKey]`; the tool reads it via `IHttpContextAccessor` (works because `Stateless = true`; do not enable `PerSessionExecutionContext`).
- **Order of checks:** permission → argument validation → `FileAccessPolicy.CheckPathText` (before the index is touched, so denied paths never reveal existence) → `IndexedFileResolver` (the index is the allowlist and supplies the canonical path that is opened) → open → `CheckAttributes` + `NativeFilePath.GetFinalPath` (`GetFinalPathNameByHandle`) → `Check(finalPath, stream)` (text, attributes, private-key content sniff). Never open the caller's string; never skip the post-open check — it is what stops a junction/symlink in a *parent* folder.
- **The deny list is absolute and structural** (`FileAccessPolicy`): any dot-prefixed component at any depth; any Hidden/System component up the path (the volume/share root is skipped — `C:\` is Hidden+System; compare without the trailing `\` that `Path.GetDirectoryName` leaves on it); protected roots (every profile's AppData, ProgramData, Windows, `ServerPaths.Root`); credential-style names; `.pem`/`.key` containing `PRIVATE KEY`. It never consults the index. Client-facing errors carry only `PolicyDecision.Category`; the path/detail go to the server log.
- `IndexedFileResolver` reuses `WindowsSearchTool.Search` through a delegate (no new SQL; tests inject a fake). It falls back to a `System.FileName` + `DP://` query because `find_files` repairs empty `ItemPathDisplay` values from `DP://` URLs.
- Chunking is character-safe (`FileChunking`): UTF-8 and UTF-16 (BOM) text never split a character, `nextOffset` always lands on a boundary, and encoding is judged from the file head so every chunk of a file is treated alike. UTF-16 text legitimately contains NULs — only apply the "NUL means binary" rule to UTF-8.
- Per-call cap: `McpHttpServerOptions.MaxFileBytes` (default 200 MiB), `--max-file-mb` / `config.json` `maxFileBytes`. The blob URI label uses the index's canonical path (`new Uri(@"\?\C:\...")` throws).
- **Content negotiation (`accept`).** MCP has no client MIME capability and the transport is stateless, so the client states what it can read in the flat-string `accept` argument (default `text/*`; text is always implicitly accepted; the tool description carries the "retry with `accept=\"text/*\"`" fallback because the harness only rejects an attachment *after* the reply). `FileClassifier` judges the type from magic bytes (PDF, PNG/JPEG/GIF/WebP, OLE2, zip → OOXML via `_rels/.rels` + `[Content_Types].xml`), never the extension, and known binaries are never treated as text even if their first 4 KiB is ASCII. `ContentNegotiator.Decide` order for `format=auto`: text → native if `accept` covers the MIME type (image block, or binary resource) → **for a PDF, rendered page images when an image type is accepted (PNG preferred, then JPEG, WebP)** → extracted text for PDF/docx/xlsx/pptx → `McpException`. So a client that accepts images never gets a PDF as text; it omits image types from `accept` to get text. Images are delivered whole (no `offset`/`length`; the per-call cap is the only limit, there is no separate image cap). `format=text|base64` bypasses negotiation.
- **Conversion (`Conversion/`) happens strictly after every policy check**, on bytes copied from the already-validated handle (input ≤ `ConversionOptions.MaxInputBytes`, so a shared background conversion never touches a closed file). Never trust zip header sizes: every part is read through `CountingLimitStream` against one shared `ConversionBudget` (real decompressed bytes, all entries together). OOXML structure comes from relationships (xlsx sheet order and `r:id` → part via `workbook.xml.rels`; pptx slide order via `presentation.xml`; docx main part via `_rels/.rels`), never from file names. XML is read with `DtdProcessing.Prohibit`. PdfPig has no cancellation token, so conversions run under a wall-clock timeout (a truly stuck parser cannot be killed in-process). `ConversionService` caches the UTF-8 result by (path, size, last-write) in a byte-bounded LRU, shares one conversion between concurrent requests, and never caches failures. For converted files `size`/`offset`/`nextOffset` are over the converted text. **PDF rendering** (`IPdfRenderer`/`PdfiumRenderer`, PDFium through `PDFtoImage` 5.x, which also pulls SkiaSharp and per-platform native assets into the output) is native, non-thread-safe and cannot be cancelled: calls are serialised by a lock and by `ConversionService`'s render gate, which waits at most the conversion timeout and then fails fast ("renderer is busy") so one hung render cannot queue everyone behind it. Pages are drawn on demand (not cached), one image block per page; for rendered PDFs `offset`/`length`/`nextOffset` count pages, `size` is the page count, and the per-call page limit and byte cap (`MaxFileBytes`) both apply. Page size/dpi limits are `ConversionOptions.RenderMaxDimension` (1568 px) and `RenderMaxDpi` (150). Limits come from `McpHttpServerOptions.Conversion` and `config.json` keys `maxConvertBytes` (input cap, default 50 MiB) and `convertTimeoutSeconds` (default 60); PdfPig is the only added dependency (OOXML uses `System.IO.Compression` + `System.Xml`).
- Tests build fixtures under `AppData\Local\Temp`, which the real policy would deny, so they use `FileAccessPolicy`'s internal constructor with an *exempt base* (`TempTree.Policy()`); never loosen the production policy for tests. `HttpServerFixture` injects a `FakeIndex` resolver and that policy via `McpHttpServerOptions`.

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

The security/HTTP tests also avoid COM: `HttpServerTests` run `McpHttpServer` in-process on an ephemeral port with a throwaway certificate and an `InMemoryClientStore`, and dispatch only `list_properties`. `WindowsCertClientStoreTests` use a uniquely named throwaway certificate store (deleted on dispose) — never the real `WsSearchMcp.Clients` store or the user's `CurrentUser\My`. `FileToolTests` / `HttpFileToolTests` / `FileAccessPolicyTests` use real temp files (incl. a `mklink /J` junction) with a `FakeIndex` that "indexes" whatever exists on disk, so `get_file` is tested end to end without COM. `ContentNegotiationTests` build valid PDF/docx/xlsx/pptx fixtures in memory (`DocumentFixtures`, PDFs via PdfPig's `PdfDocumentBuilder`; the xlsx/pptx fixtures deliberately use non-ordinal part names and a hidden sheet) and cover accept parsing, the negotiation matrix, converters, zip-expansion limits, the conversion cache and real PDFium page rendering (plus a fake `IPdfRenderer` for timeout/gate behaviour). The test project needs the `Microsoft.AspNetCore.App` framework reference.
