# CLAUDE.md

Claude Code–specific guidance for this repository. For general project guidance (build, test, architecture, invariants), see [AGENTS.md](AGENTS.md).

## Using the MCP tools in this session

The ws-search MCP tools are deferred — load schemas before calling:

```
ToolSearch(query: "select:mcp__ws-search__find_files,mcp__ws-search__list_properties,mcp__ws-search__list_properties_by_family")
```

## Calling the find_files tool

`text` is required by the schema; all other params have defaults. Example:

```
mcp__ws-search__find_files(text="Erin Dawn", scope="")
```

- `text`: search term (full-text + filename match). Pass `""` if you only want filter-based results.
- `scope`: **where to search — three cases.** Omit it or pass `"<UNSET>"` → user profile folder (`%USERPROFILE%`) and subfolders (the default). Pass `""` (empty string) → the **entire** index, all indexed locations and drives. Pass an absolute folder path (e.g. `"C:\\Users\\me\\Documents"`) → only that folder and its subfolders. To search the whole PC, use `""`, not `"<UNSET>"`.
- `filters`: a JSON string like `'[{"property":"System.Kind","op":"=","value":"picture"}]'`. Pass `""` for no filters.
- `columns`: comma-separated extra `System.*` columns to include in results, e.g. `"System.DateModified,System.Size"`.

Example searching entire index:

```
mcp__ws-search__find_files(text="contract", scope="", maxResults=20)
```

## Schema design (load-bearing)

The tool's parameters are intentionally all **flat strings or integers** — no `string?` (nullable type unions) and no nested object array params. Earlier revisions used `string?` and `SearchFilter[]?`; Anthropic Claude models (Opus 4.7, Sonnet 4.6) then silently emitted `{}` for the entire `tool_use.input`, producing the terse error `An error occurred invoking '<toolname>'` with no detail. The same schema worked fine with OpenAI/Copilot, which is why this is a Claude-specific concern.

If you add parameters to this tool, keep them as plain `string` / `int` / `bool` with literal defaults. Use sentinel strings (e.g. `""`, `"<UNSET>"`, `"ASC"`) and decode them inside the method body. Do **not** reintroduce `string?` or typed array params in the public surface.
