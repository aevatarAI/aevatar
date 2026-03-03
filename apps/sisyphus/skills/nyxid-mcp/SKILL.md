---
name: nyxid-mcp
description: Use NyxID MCP tools to discover, connect, and call downstream service tools on behalf of the user
---

# NyxID MCP

You have access to a NyxID MCP server with 4 meta-tools. These are the ONLY way to access external service tools.

## Meta-Tools

| Tool | Purpose |
|------|---------|
| `nyx__search_tools` | Search connected tools by keyword. Returns tool names, descriptions, and `inputSchema`. |
| `nyx__discover_services` | List available services (including ones not yet connected). |
| `nyx__connect_service` | Connect/activate a service so its tools become searchable. |
| `nyx__call_tool` | Execute a connected tool by its full name. |

## Required Setup Sequence (First Time Only)

You MUST follow these steps IN ORDER the first time you need to use a service's tools. Do NOT skip any step.

### Step 1: Search for tools
```json
nyx__search_tools({ "query": "chrono-graph" })
```

### Step 2: If search returns 0 results → Discover and Connect

**This is expected on first use.** Services must be activated before their tools appear in search.

```json
nyx__discover_services({})
```
This returns a list of available services with their `service_id`. Find the service you need (e.g. `chrono-graph-service`).

Then connect it:
```json
nyx__connect_service({ "service_id": "<service_id_from_discovery>" })
```
For services with `requires_credential: false`, connect directly without asking. For services requiring credentials, ask the user first.

### Step 3: Search again
After connecting, search again to get the tool names and schemas:
```json
nyx__search_tools({ "query": "chrono-graph" })
```
Now the tools will appear. Note the exact tool names — you need them for `nyx__call_tool`.

### Step 4: Call tools

**CRITICAL: Always pass `arguments_json` to `nyx__call_tool`.** This is a **JSON string** containing all required parameters for the tool (path parameters like `graphId`, `nodeId`, plus any body parameters). Check the tool's `inputSchema` from `nyx__search_tools` results to see what parameters are required.

```json
nyx__call_tool({
  "tool_name": "chrono-graph-service__get_api_graphs_by_graphid_snapshot",
  "arguments_json": "{\"graphId\": \"dbeef00f-f2c7-4447-9686-3a6deba65a72\"}"
})
```

**If you omit `arguments_json`, the tool call WILL FAIL** because path parameters like `{graphId}` won't be substituted in the URL. Always pass `arguments_json` even if the tool takes no parameters — use `"{}"` in that case.

## CRITICAL: Do NOT give up if search returns empty

If `nyx__search_tools` returns 0 results, this does NOT mean the tools don't exist. It means the service is not yet activated. You MUST proceed to `nyx__discover_services` → `nyx__connect_service` → search again. **Never stop or ask the user to activate services manually.**

## After Setup

Once a service is connected in a session, its tools stay available. On subsequent turns:
- Skip discover/connect — go directly to `nyx__call_tool`
- Only re-search if you need to find a new tool name

## Rules

- **Never guess tool names.** Always get them from `nyx__search_tools` results.
- **Tool name format:** `{service_slug}__{endpoint_name}` (double underscore).
- **Always use `nyx__call_tool`** to execute tools. Do not call service tools directly.
- **Always pass `arguments_json`** (a JSON string) to `nyx__call_tool`. Check the tool's `inputSchema` for required parameters. Path parameters (e.g., `graphId`, `nodeId`) are ALWAYS required. Example: `"arguments_json": "{\"graphId\": \"uuid-here\"}"`.
- **Maximum 20 activated services per session.** Only connect services you actually need.
