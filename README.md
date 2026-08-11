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

Publishes the binary to `%LOCALAPPDATA%\Programs\ws-search-mcp\ws-search-mcp.exe`. Flags:

| Flag | Purpose |
|------|---------|
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

## Uninstall

```powershell
./uninstall.ps1
```

Removes the install directory. For each AI tool, also remove the server's entry:

| Tool | Command |
|------|---------|
| Claude Code | `claude mcp remove ws-search --scope user` |
| VS Code | delete the entry from your `mcp.json` |
| Copilot CLI | delete the entry from `~/.copilot/mcp-config.json` |
| Cursor | delete the entry from `mcp.json` |

## Examples

Once registered, an AI assistant can call the `search` tool with arguments like:

| Goal | Argument shape |
|------|----------------|
| Find readme files under a directory | `text="readme", mode="filename", scope="C:\\Users\\me\\source"` |
| Files larger than 100 MB | `text="", filters="[{\"property\":\"System.Size\",\"op\":\">\",\"value\":\"104857600\",\"valueType\":\"number\"}]"` |
| PDFs touched this week, newest first | `filters="[{\"property\":\"System.ItemType\",\"op\":\"=\",\"value\":\".pdf\"},{\"property\":\"System.DateAccessed\",\"op\":\">=\",\"value\":\"2026-05-03\"}]", orderBy="System.DateAccessed", orderDir="DESC"` |
| Photos taken on a Canon EOS R5 in 2025 | `filters="[{\"property\":\"System.Photo.CameraModel\",\"op\":\"=\",\"value\":\"Canon EOS R5\"},{\"property\":\"System.Photo.DateTaken\",\"op\":\">=\",\"value\":\"2025-01-01\"}]", columns="System.Photo.DateTaken,System.Image.Dimensions"` |
| Music tagged with a specific artist | `filters="[{\"property\":\"System.Music.Artist\",\"op\":\"CONTAINS\",\"value\":\"Radiohead\"}]"` |
| "Which property holds the camera maker?" | `list_properties(filter:"camera")` |

### `search` parameter reference

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
