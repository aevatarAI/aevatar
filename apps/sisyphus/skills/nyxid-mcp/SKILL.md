---
name: nyxid-mcp
description: Use NyxID MCP tools to discover, connect, and call downstream service tools on behalf of the user
---

# NyxID MCP

## CRITICAL: Fully Automated — No User Approval

This runs inside an automated workflow with NO human in the loop. **NEVER ask for user confirmation or approval.** Once you determine a tool call is needed, execute it immediately. Do NOT say "if you agree" or "with your permission" — just call the tool. Every pause waiting for approval is a workflow stall.

## Overview

You have access to a NyxID MCP server with 4 meta-tools. These are the ONLY way to access external service tools.

## Meta-Tools

| Tool | Purpose |
|------|---------|
| `nyx__search_tools` | Search connected tools by keyword. Returns tool names, descriptions, and `inputSchema`. |
| `nyx__discover_services` | List services NOT yet connected. |
| `nyx__connect_service` | Connect/activate a service so its tools become searchable. |
| `nyx__call_tool` | Execute a connected tool by its full name. |

## How to Find and Use Tools

### Step 1: Search for tools

Use a **short keyword** as the query — just the service name, NOT full tool names.

```json
nyx__search_tools({ "query": "chrono-graph" })
```

**IMPORTANT search query rules:**
- Use SHORT keywords: `"chrono-graph"`, `"storage"`, `"sandbox"` — just 1-2 words
- NEVER put full tool names in the query (e.g., do NOT search for `"post_api_graphs_by_graphid_nodes"`)
- NEVER concatenate multiple terms (e.g., do NOT search for `"chrono-graph nodes edges"`)

`nyx__search_tools` auto-activates matching services. If the response contains a non-empty `matches` array, skip directly to **Step 3**.

### Step 2: If Step 1 returned 0 matches — Discover and Connect

**Only do this if Step 1 returned `"matches": [], "count": 0`.**

First, check `services_activated` and `note` in the Step 1 response:
- If `"note": "Tools were already activated."` and `"services_activated": 0` → the service is connected but your query didn't match. **Try a shorter/different query** before going to discover:
  ```json
  nyx__search_tools({ "query": "graph" })
  ```
- If still 0 matches, try a broader search:
  ```json
  nyx__search_tools({ "query": "chrono" })
  ```

If all search attempts return 0 matches, discover available services:
```json
nyx__discover_services({})
```
**IMPORTANT:** Do NOT pass `query` or `category` to `nyx__discover_services` — call it with empty args `{}` to see ALL available services. The `query` filter is very strict and will hide results.

Find the service you need, then connect it:
```json
nyx__connect_service({ "service_id": "<service_id_from_discovery>" })
```

After connecting, search again with a short keyword:
```json
nyx__search_tools({ "query": "chrono-graph" })
```

### Step 3: Call tools

Use `nyx__call_tool` with the exact tool name from search results and an `arguments_json` string.

**CRITICAL: Always pass `arguments_json`.** This is a **JSON string** containing all required parameters. Check the tool's `inputSchema` from search results to see what parameters are required.

```json
nyx__call_tool({
  "tool_name": "chrono-graph-service__post_api_graphs_by_graphid_nodes",
  "arguments_json": "{\"graphId\": \"<your-graph-id>\", \"nodes\": [{\"type\": \"definition\", \"properties\": {\"abstract\": \"Plain text here\"}}]}"
})
```

**If you omit `arguments_json`, the call WILL FAIL.** Path parameters like `graphId` won't be substituted. Always pass `arguments_json` even if the tool takes no parameters — use `"{}"`.

## Chrono-Graph Service Reference

The `chrono-graph-service` provides knowledge graph read/write tools. Key tools:

| Tool Name | Purpose |
|-----------|---------|
| `chrono-graph-service__get_api_graphs_by_graphid_snapshot` | Get full graph snapshot (nodes + edges) |
| `chrono-graph-service__post_api_graphs_by_graphid_nodes` | Create nodes (batch) |
| `chrono-graph-service__post_api_graphs_by_graphid_edges` | Create edges (batch) |

### Creating nodes (batch)
The API expects a `nodes` ARRAY — even for a single node, wrap it in `[]`.
Store ONLY plain-text properties — NO raw TeX content.
```json
nyx__call_tool({
  "tool_name": "chrono-graph-service__post_api_graphs_by_graphid_nodes",
  "arguments_json": "{\"graphId\": \"<Graph ID>\", \"nodes\": [{\"type\": \"definition\", \"properties\": {\"confidence\": \"0.9\", \"source\": \"research\", \"sourceref\": \"session-id\", \"abstract\": \"Plain text abstract.\"}}]}"
})
```
The response returns created nodes with `id` (UUID). Map each `temp_id → real UUID` by array order.

### Creating edges (batch)
Use real node UUIDs from the node creation response.
```json
nyx__call_tool({
  "tool_name": "chrono-graph-service__post_api_graphs_by_graphid_edges",
  "arguments_json": "{\"graphId\": \"<Graph ID>\", \"edges\": [{\"type\": \"extends\", \"source\": \"<source UUID>\", \"target\": \"<target UUID>\"}]}"
})
```

## Do NOT Give Up If Search Returns Empty

If `nyx__search_tools` returns 0 results, this does NOT mean the tools don't exist. Try these steps in order:
1. **Retry with a shorter query** — e.g., `"graph"` or `"chrono"`
2. **Call `nyx__discover_services({})` with empty args** — see all available services
3. **Connect the service** — `nyx__connect_service({ "service_id": "..." })`
4. **Search again** — `nyx__search_tools({ "query": "chrono-graph" })`

**Never stop or ask the user to activate services manually.**

## After Setup

Once a service is connected, its tools stay available for the session:
- Skip discover/connect — go directly to `nyx__call_tool`
- Only re-search if you need to find a new tool name

## Rules

- **Never guess tool names.** Always get them from `nyx__search_tools` results.
- **Tool name format:** `{service_slug}__{endpoint_name}` (double underscore).
- **Always use `nyx__call_tool`** to execute tools. Do not call service tools directly.
- **Always pass `arguments_json`** (a JSON string) to `nyx__call_tool`. Path parameters (e.g., `graphId`, `nodeId`) are ALWAYS required.
- **Maximum 20 activated services per session.** Only connect services you actually need.
