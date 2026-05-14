# CLAUDE.md

Claude Code–specific guidance for this repository. For general project guidance (build, test, architecture, invariants), see [AGENTS.md](AGENTS.md).

## Using the MCP tools in this session

The ws-search MCP tools (`mcp__ws-search__query`, `mcp__ws-search__list_properties`, `mcp__ws-search__list_properties_by_family`) are deferred — their schemas are not loaded automatically. Before calling any of them, run:

```
ToolSearch(query: "select:mcp__ws-search__query,mcp__ws-search__list_properties,mcp__ws-search__list_properties_by_family")
```

Calling a deferred tool without loading its schema first will fail with an input validation error.
