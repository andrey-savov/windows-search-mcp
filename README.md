# windows-search-mcp

An [MCP](https://modelcontextprotocol.io) server that exposes the **Windows Search index** to AI coding assistants and agents. Ask Claude / Copilot / Cursor questions like *"find every PDF I downloaded last week"* or *"list photos taken on my Canon R5"* and they call into Windows Search directly.

Built on `ADODB.Connection` + `Search.CollatorDSO` (the same query engine the Start menu and File Explorer use), so every property indexed by Windows is queryable — dates, sizes, EXIF metadata, music tags, document properties, GPS coords, etc.

## Tools

- **`find_files`** — full-text + filename matching with structured filters on any indexed `System.*` property, optional directory scope, ordering, and result cap.
- **`list_properties_by_family`** — enumerate properties in a family (`Photo`, `Music`, `Video`, `Document`, `GPS`, `Message`, `Contact`, …).
- **`list_properties`** — substring search across all ~600 properties registered on the machine (canonical name + display name).

## Requirements

- Windows 10 / 11
- Windows Search service running (default)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) — `install.ps1` will offer to install it via `winget` if missing.

## Install

```powershell
git clone https://github.com/andrey-savov/windows-search-mcp.git
cd windows-search-mcp
./install.ps1
```

Publishes three executables side by side into `%LOCALAPPDATA%\Programs\ws-search-mcp\`:

| Executable | Purpose |
|------------|---------|
| `ws-search-mcp.exe` | The MCP server for **local** clients over stdio. Unchanged; this is what the per-tool registrations below use. |
| `ws-search-mcp-http.exe` | Optional authenticated **HTTPS** MCP server for network clients. See [Network hosting](#network-hosting-https). |
| `ws-search-mcp-admin.exe` | Local tool that mints/revokes network clients and manages the HTTPS certificate. |

Flags:

| Flag | Purpose |
|------|---------|
| `-RegisterHttpTask` | Also register a per-user Scheduled Task that starts `ws-search-mcp-http.exe` at logon (and restarts it on reinstall). |
| `-SelfContained` | Bundle the .NET runtime into the binary (~70 MB, no runtime needed on the machine). |
| `-InstallDir <path>` | Install somewhere other than `%LOCALAPPDATA%\Programs\ws-search-mcp`. |
| `-Yes` | Skip the prerequisite-install confirmation prompt (unattended use). |

Then register the server with your AI tool below, using the path printed by `install.ps1`.

### Claude Code

```powershell
claude mcp add ws-search "$env:LOCALAPPDATA\Programs\ws-search-mcp\ws-search-mcp.exe" --scope user
```

`--scope user` makes the server available in every project. Drop the flag for project-only scope.

### VS Code (GitHub Copilot, 1.99+)

Command palette → **`MCP: Open User Configuration`** (or **`MCP: Open Workspace Folder Configuration`** for a per-project `.vscode/mcp.json`). Add:

```json
{
  "servers": {
    "ws-search": {
      "type": "stdio",
      "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\ws-search-mcp\\ws-search-mcp.exe"
    }
  }
}
```

Note: VS Code uses `servers` — *not* `mcpServers`.

### GitHub Copilot CLI

Edit `~/.copilot/mcp-config.json` (Windows: `C:\Users\<you>\.copilot\mcp-config.json`):

```json
{
  "mcpServers": {
    "ws-search": {
      "type": "local",
      "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\ws-search-mcp\\ws-search-mcp.exe",
      "tools": ["*"]
    }
  }
}
```

Or, inside a Copilot CLI session, run `/mcp add` and follow the prompts.

### Cursor

Edit `~/.cursor/mcp.json` (user-scope) or `<project>/.cursor/mcp.json` (project-scope):

```json
{
  "mcpServers": {
    "ws-search": {
      "type": "stdio",
      "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\ws-search-mcp\\ws-search-mcp.exe"
    }
  }
}
```

> **Windows JSON gotcha:** double-escape backslashes (`\\`) inside JSON strings, or use forward slashes. VS Code uses `servers`; Cursor and Copilot CLI use `mcpServers` — the key name is the most common copy-paste mistake.

## Network hosting (HTTPS)

`ws-search-mcp-http.exe` serves the same three tools to remote MCP clients over **HTTPS** (MCP Streamable HTTP, stateless). It is opt-in: the stdio server above is unaffected. Every request must carry `Authorization: Bearer <token>`; there is no plaintext HTTP listener and no remote credential-minting endpoint.

### Quick start

Run these as the Windows user whose search index you want to serve (see [constraints](#constraints-and-security-boundaries)):

```powershell
$bin = "$env:LOCALAPPDATA\Programs\ws-search-mcp"

# 1. Create the server's TLS certificate (self-signed; add names clients will use to reach this machine)
& "$bin\ws-search-mcp-admin.exe" cert init --san mybox.example.lan
& "$bin\ws-search-mcp-admin.exe" cert export "$HOME\ws-search-server.pem"   # public cert only

# 2. Mint a client (the token is printed ONCE, to stdout)
& "$bin\ws-search-mcp-admin.exe" client mint "Work VS Code"

# 3. Run the server (or install with -RegisterHttpTask to start it at every logon)
& "$bin\ws-search-mcp-http.exe"                 # https on all interfaces, port 8765
& "$bin\ws-search-mcp-http.exe" --port 9000 --bind 192.168.1.20
```

Copy `ws-search-server.pem` to each client machine and make the client trust it (below). Allow the port through the firewall for the networks you intend, e.g.:

```powershell
New-NetFirewallRule -DisplayName "ws-search-mcp-http" -Direction Inbound -Protocol TCP -LocalPort 8765 `
    -Action Allow -Profile Private -RemoteAddress LocalSubnet
```

The server listens on **all interfaces** by default; use the firewall (or `--bind`) to control reachability.

### `ws-search-mcp-admin`

| Command | Purpose |
|---------|---------|
| `client mint <name> [--expires 365d\|never]` | Create a client. Prints its token once (progress goes to stderr, so `mint > token.txt` captures only the token). |
| `client list [--json]` | List **current** (unrevoked) clients: name, created, expires (`EXPIRED` if past), last used, key fingerprint. |
| `client revoke <name>` | Revoke a client; it is rejected within seconds, **no server restart needed**. |
| `cert init [--san <dns-or-ip>]... [--force]` | Create the self-signed certificate. Default names: `localhost`, machine name, FQDN, `127.0.0.1`, `::1`, current IPs. |
| `cert show` / `cert export <path.pem\|.cer>` | Inspect, or write the **public** certificate. |
| `cert renew [--san ...]` | Replace the certificate (keeps its names unless `--san` is given). Restart the server and re-trust on clients. |
| `cert use <thumbprint>` | Use a CA-issued certificate you imported into `CurrentUser\My` instead. |

Client names are the human-friendly identifier: 1–64 characters (letters, digits, spaces, `-`, `_`, `.`), unique ignoring case.

### How credentials work

`client mint` generates a fresh P-256 key pair, signs a token (an ES256 JWT carrying the client name, audience and expiry; 365 days by default) with the private key, then **discards the private key**. Only the *public* key is kept, as a public-only certificate in `CurrentUser\WsSearchMcp.Clients` (inspect with `certmgr.msc`). The server therefore can verify tokens but can never forge one, and reading the server's storage yields nothing a client can present. Rotation = mint a new client, then revoke the old one. Treat the token itself as a password on the client side (an environment variable, not a checked-in file).

The server's TLS private key is non-exportable and stays in `CurrentUser\My`. Configuration (`config.json`) and last-used timestamps (`usage.json`) live in `%APPDATA%\ws-search-mcp`, ACL'd to your user and SYSTEM. Set `WS_SEARCH_HOME` to relocate them.

### Connecting clients

Clients must (a) trust the certificate and (b) connect using a name in its list (`cert show`). Replace `mybox` and keep the token in `WS_SEARCH_TOKEN`.

| Client | Status | How it trusts the self-signed cert |
|--------|--------|------------------------------------|
| Claude Code | **Tested** | `NODE_EXTRA_CA_CERTS=<path to .pem>` |
| opencode | **Tested** | `NODE_EXTRA_CA_CERTS=<path to .pem>` — set where its *background service* sees it (see below) |
| VS Code / Copilot | Not tested | Uses the Windows certificate store (import the cert, below) |
| Copilot CLI, Cursor | Not tested | Try `NODE_EXTRA_CA_CERTS`, or the Windows store |
| Claude.ai / Claude Desktop remote connectors | **Unsupported** | They require OAuth and a publicly trusted certificate |
| Any local client | n/a | Keep using stdio (`ws-search-mcp.exe`) |

**Claude Code**
```powershell
$env:NODE_EXTRA_CA_CERTS = "$HOME\ws-search-server.pem"     # persist with setx for future sessions
$env:WS_SEARCH_TOKEN = "<token>"
claude mcp add-json ws-search-net '{"type":"http","url":"https://mybox:8765/","headers":{"Authorization":"Bearer ${WS_SEARCH_TOKEN}"}}' --scope user
```

**opencode** (`opencode.jsonc`; servers live under `mcp.servers`)
```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "ws-search": {
        "type": "remote",
        "url": "https://mybox:8765/",
        "oauth": false,
        "headers": { "Authorization": "Bearer {env:WS_SEARCH_TOKEN}" }
      }
    }
  }
}
```
- `"oauth": false` is **required**; opencode otherwise treats the server as OAuth and shows "needs authentication".
- An unset `{env:...}` variable becomes an empty string and fails with a silent 401. Check it, or use `{file:~/path/to/token}`.
- opencode runs MCP connections inside a shared **background service** that does not see environment variables set on a one-off command. Set `NODE_EXTRA_CA_CERTS` as a persistent user variable (`setx NODE_EXTRA_CA_CERTS "%USERPROFILE%\ws-search-server.pem"`), then run `opencode service restart`. (Verified with `opencode run --standalone`, whose private server inherits the shell environment; the persistent-variable route follows from that but was not separately exercised.) A failure to trust the cert shows as `UNABLE_TO_VERIFY_LEAF_SIGNATURE`. Do not "fix" it with `NODE_TLS_REJECT_UNAUTHORIZED=0`; that disables certificate checking entirely.

**VS Code** (`mcp.json`, not tested)
```json
{
  "inputs": [{ "type": "promptString", "id": "ws-search-token", "description": "ws-search token", "password": true }],
  "servers": {
    "ws-search-net": { "type": "http", "url": "https://mybox:8765/",
      "headers": { "Authorization": "Bearer ${input:ws-search-token}" } }
  }
}
```

**Trusting the certificate in the Windows store** (for clients that use it; Windows shows a confirmation prompt):
```powershell
Import-Certificate -FilePath "$HOME\ws-search-server.pem" -CertStoreLocation Cert:\CurrentUser\Root
```

**Using your own CA instead:** have your CA issue a server certificate (with the names clients use, `serverAuth` usage), import it with its private key into `CurrentUser\My`, then `ws-search-mcp-admin cert use <thumbprint>`. Clients that already trust your CA need no further steps.

### Constraints and security boundaries

- **Run as the user whose index you want.** Windows Search security-trims results to the identity of the server process, so `ws-search-mcp-http.exe` runs as the signed-in user (a logon task, not a Windows service). It stops at sign-out. A service/SYSTEM account would see a different, or no, per-user scope; that is not supported.
- **`ws-search-mcp-admin` must run as the same user** as the server (they share that user's certificate stores and `%APPDATA%`). Anyone who can run code as that user can mint clients; the Windows account is the trust boundary.
- **Anyone with a valid token can search your whole index** (everything your user can see, including file names and indexed content). Scope tokens per device, set `--expires`, and revoke promptly.
- The server rejects requests carrying a browser `Origin` header (allow-list in `config.json` → `allowedOrigins`, empty by default), limits bodies to 1 MB, and logs failed authentication with the caller's IP and a reason — never the token.
- `install.ps1` stops running `ws-search-mcp*` processes before publishing, including a manually started HTTP server; with `-RegisterHttpTask` (or an existing task) it restarts the task afterwards.
- The task shows a console window at logon. Windows Firewall will prompt on first listen unless you created a rule.

## Uninstall

```powershell
./uninstall.ps1               # removes binaries and the logon task; keeps your clients and certificate
./uninstall.ps1 -RemoveData   # also deletes client keys, the server certificate and %APPDATA%\ws-search-mcp
```

Removes the install directory. For each AI tool, also remove the server's entry:

| Tool | Command |
|------|---------|
| Claude Code | `claude mcp remove ws-search --scope user` |
| VS Code | delete the entry from your `mcp.json` |
| Copilot CLI | delete the entry from `~/.copilot/mcp-config.json` |
| Cursor | delete the entry from `mcp.json` |

## Examples

Once registered, an AI assistant can call the `find_files` tool with arguments like:

| Goal | Argument shape |
|------|----------------|
| Find readme files under a directory | `text="readme", mode="filename", scope="C:\\Users\\me\\source"` |
| Files larger than 100 MB | `text="", filters="[{\"property\":\"System.Size\",\"op\":\">\",\"value\":\"104857600\",\"valueType\":\"number\"}]"` |
| PDFs touched this week, newest first | `filters="[{\"property\":\"System.ItemType\",\"op\":\"=\",\"value\":\".pdf\"},{\"property\":\"System.DateAccessed\",\"op\":\">=\",\"value\":\"2026-05-03\"}]", orderBy="System.DateAccessed", orderDir="DESC"` |
| Photos taken on a Canon EOS R5 in 2025 | `filters="[{\"property\":\"System.Photo.CameraModel\",\"op\":\"=\",\"value\":\"Canon EOS R5\"},{\"property\":\"System.Photo.DateTaken\",\"op\":\">=\",\"value\":\"2025-01-01\"}]", columns="System.Photo.DateTaken,System.Image.Dimensions"` |
| Music tagged with a specific artist | `filters="[{\"property\":\"System.Music.Artist\",\"op\":\"CONTAINS\",\"value\":\"Radiohead\"}]"` |
| "Which property holds the camera maker?" | `list_properties(filter:"camera")` |

### `find_files` parameter reference

| Parameter | Purpose |
|-----------|---------|
| `text` | Search text. Pass `""` to search by `filters` only. Combines with `filters` via `AND`. |
| `mode` | `contains` (default — full-text *or* filename), `filename` (filename only), `raw` (a verbatim Windows Search SQL `WHERE` clause, such as `System.Size > 104857600`; AQS such as `size:>100mb` is not supported here). |
| `scope` | Directory to limit to. Defaults to the entire index. Pass an absolute path to search only that folder and its subfolders. |
| `maxResults` | Cap on returned rows. Clamped to `[1, 1000]`. Default 50. |
| `columns` | Comma-separated `System.*` columns to return alongside `System.ItemPathDisplay`. |
| `filters` | A JSON string containing an array of `{property, op, value, valueType}`. `value` must be a string. Ops: `=`, `!=`, `<>`, `<`, `<=`, `>`, `>=`, `LIKE`, `NOT LIKE`, `IS NULL`, `IS NOT NULL`, `CONTAINS`. `valueType` is `text` (default; quoted, also for ISO 8601 dates), `number` (bare), or `bool` (`1`/`0`). HTML-escaped comparison operators such as `&gt;` are accepted. |
| `orderBy` / `orderDir` | Sort column and direction (`ASC` / `DESC`). |

## Build from source

```powershell
dotnet build -c Release
dotnet test
```

Layout: `src/WsSearchMcp.Core` (tools + security library), `ws-search-mcp.csproj` (stdio exe), `src/WsSearchMcp.Http`, `src/WsSearchMcp.Admin`, `tests/WsSearchMcp.Tests`.
