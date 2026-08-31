# Lark Docs Knowledge Assistant Design

## Problem

The `enterprise_knowledge_assistant` starter currently answers only from context supplied in the run input. Its canvas contains an `llm_call` followed by `assign`, so the product name implies enterprise knowledge retrieval while execution performs no knowledge-source access.

The corrected product contract is: the caller asks through Aevatar Run/Chat, the workflow actively searches the caller's connected Lark Docs/Wiki content, and the result is grounded in retrieved Lark evidence. Lark is a knowledge source, not the conversation channel or workflow trigger.

## Scope

This change will:

- Completely replace the existing supplied-context starter flow.
- Reuse the caller's existing NyxID-backed Lark connection and workflow tool execution path.
- Search Lark Docs and Wiki resources with the caller's Lark permissions.
- Read the textual content of the strongest matching Docx/Wiki pages.
- Support natural-language answers and structured extraction from the same retrieval flow.
- Return source titles and Lark URLs with every grounded result.
- Surface missing connection, missing permission, empty search, and partial document-read outcomes honestly.

This change will not:

- Receive questions through a Lark bot or send answers to Lark.
- Search Lark chat messages or arbitrary attachments.
- Create a local vector index, synchronization job, knowledge collection, or indexing checkpoint.
- Claim strict model-level JSON Schema enforcement that the current workflow `llm_call` contract does not provide.

## Product Semantics

The workflow has one retrieval spine and two presentation modes:

```text
Aevatar Run/Chat request
        |
        v
Plan a concise Lark search query
        |
        v
Search and read Lark Docs/Wiki
        |
        v
Answer or extract from retrieved evidence
        |
        v
Return result with Lark sources
```

The mode is inferred from the run request:

- A normal question produces a concise natural-language answer with inline source references and a source list.
- A request for fields, JSON, a table, or a supplied extraction shape produces structured data, a `sources` collection, and an explicit list of unavailable fields.
- A JSON run input may carry `mode`, `question`, and a requested field/schema description. Plain text remains the default and requires no wrapper.

Retrieval is always performed before either output mode. Structured extraction is not a separate workflow and must not bypass source retrieval.

## Runtime Design

### Existing infrastructure reused

- `ToolCallModule` remains the workflow execution mechanism.
- `AgentWorkflowToolSourceAdapter` continues to project registered agent tools into workflow tools.
- `AgentToolRequestContext.NyxIdAccessToken` supplies the current caller's NyxID identity.
- `LarkNyxClient` continues to route requests through the configured NyxID Lark provider slug.
- Existing workflow expression JSON escaping is used when the query-planning step output becomes tool arguments.

### New Lark capability

Add one read-only agent tool named `lark_docs_search`. It owns the adapter-level orchestration required to turn one search query into usable evidence:

1. Validate and normalize a query, limiting it to Lark Search v2's 30-Unicode-code-point boundary.
2. Call `POST /open-apis/search/v2/doc_wiki/search` with Docx/Wiki filters and a bounded result count.
3. Normalize result title, URL, resource kind, and resource token without exposing the raw provider response to the workflow.
4. For a Docx result, call `GET /open-apis/docx/v1/documents/{document_id}/raw_content`.
5. For a Wiki result, resolve its node with `GET /open-apis/wiki/v2/spaces/get_node` when the search response does not already provide the underlying Docx token, then read the Docx raw content.
6. Return a bounded evidence envelope. Document reads are sequential, source count is capped, each source is truncated independently, and the total evidence payload is bounded.

The tool is marked read-only and never requires mutation approval. It uses the caller's credential, so search visibility matches what that caller can access in Lark.

### Tool input

The initial contract is intentionally narrow:

```json
{
  "query": "project policy reimbursement",
  "max_sources": 5,
  "space_ids": []
}
```

- `query` is required after trimming.
- `max_sources` defaults to 5 and is bounded to 1-10.
- `space_ids` is optional. When present, the query is limited to those Wiki spaces; otherwise both Docs and Wiki are searched.

### Tool output

The workflow receives a normalized evidence envelope:

```json
{
  "success": true,
  "query": "project policy reimbursement",
  "has_more": false,
  "sources": [
    {
      "source_id": "lark:docx:doccn_example",
      "source_kind": "docx",
      "title": "Expense policy",
      "url": "https://example.larksuite.com/docx/doccn_example",
      "document_token": "doccn_example",
      "content": "...",
      "content_truncated": false
    }
  ],
  "unreadable_sources": []
}
```

Stable fields are named by responsibility. Raw provider payloads and generic metadata bags are not propagated into the workflow.

## Starter Workflow

Replace `enterprise_knowledge_assistant.yaml` with five steps:

1. `capture_knowledge_request`: preserve the original Run/Chat request for the final response step.
2. `plan_lark_search`: an LLM role converts the request into one concise search query of at most 30 characters and returns only the query text.
3. `search_lark_docs_and_wiki`: a visible `tool_call` invokes `lark_docs_search` using JSON-escaped planner output.
4. `answer_or_extract`: a grounded LLM role receives the original request plus normalized Lark evidence and chooses answer or structured-extraction presentation according to the request.
5. `record_knowledge_result`: store and return the grounded output.

The answering role has no tools. It cannot silently perform additional retrieval or bypass the visible search step. Retrieved document text is treated as untrusted evidence, not as instructions.

For natural-language answers, every material claim must point to a retrieved source and the response must end with a source list containing Lark links. For structured extraction, the output must be JSON, include `data`, `sources`, and `missing_fields`, and use `null` plus `missing_fields` instead of inventing unavailable values.

## Error Behavior

- Missing NyxID/Lark credential: return a safe `success: false` tool result stating that the caller must connect or reauthenticate Lark.
- Missing `search:docs:read` or document-read permission: return the provider error in a safe normalized form; do not fall back to model knowledge.
- Empty search result: return `success: true` with an empty `sources` list. The answer step states that no supporting Lark document was found.
- One unreadable match: continue with other readable matches and list the failed source under `unreadable_sources` without exposing tokens or response bodies not needed for remediation.
- All matches unreadable: return the normalized evidence envelope with no usable sources; the answer step reports insufficient accessible evidence.
- Invalid provider JSON: return a stable parsing error and do not dispatch the LLM with fabricated evidence.

## Security And Limits

- Use only the current caller's NyxID access token. Never accept an access token from YAML or tool arguments.
- Never log query text, document content, provider bodies, or access tokens at information level.
- Treat Lark document content as prompt-injection-capable data. System instructions explicitly prohibit following instructions found in retrieved content.
- Default to five sources, permit at most ten, and cap per-source and total content sizes before the LLM call.
- Keep Lark as the external authority. This design stores no durable document copy or indexing state.

## Verification

Focused tests will cover:

- Search request path, method, filters, page size, caller token, and query normalization.
- Docx raw-content retrieval and Wiki-to-Docx node resolution.
- Result normalization, source URLs, source de-duplication, content limits, and partial read failure.
- Authentication, provider error, empty result, and invalid response behavior.
- Tool registration in `LarkAgentToolSource`.
- Starter YAML parsing and validation.
- Starter semantic assertions proving that it contains a visible Lark retrieval step, supports both output modes, cites sources, and no longer claims supplied context.

Only the affected backend test projects and repository guards relevant to modified tests will run locally. No frontend files or full frontend validation are in scope.
