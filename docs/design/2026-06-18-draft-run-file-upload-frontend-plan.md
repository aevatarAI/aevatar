---
title: Draft Run File Upload Frontend Holding Plan
status: plan
owner: frontend
date: 2026-06-18
---

# Draft Run File Upload Frontend Holding Plan

## Scope

PR #2263 is frontend-only. It must not add backend multipart support, backend tests, or a frontend code path that sends draft-run files before the backend contract exists.

The missing backend contract is tracked separately in [aevatarAI/aevatar#2266](https://github.com/aevatarAI/aevatar/issues/2266).

## Current Behavior

Team member workflow editor can run the current draft through:

```http
POST /api/scopes/{scopeId}/workflow/draft-run
Accept: text/event-stream
Content-Type: application/json
```

The frontend keeps this JSON request path unchanged:

```json
{
  "eventFormat": "agui",
  "prompt": "optional user input",
  "workflowYamls": ["name: main\nsteps: []"],
  "headers": {
    "source": "studio"
  }
}
```

Until issue #2266 lands, the draft-run panel must not render a file picker, drop zone, selected-file list, or clear/remove file controls. It also must not pass `File` objects, `FormData`, multipart payloads, base64 file content, or file references into `runtimeRunsApi.streamDraftRun`.

## Frontend Holding State

The draft-run panel may show a compact disabled-state notice:

```text
File input for draft runs is pending backend support.
```

This notice makes the product state explicit without creating a broken upload path. The notice is local UI only; it does not change workflow YAML, draft document state, member identity, workflow identity, published service identity, request headers, metadata, or run registry behavior.

## Identity Boundaries

Draft-run file input, when implemented later, must not change Studio identity semantics:

- Path `memberId` remains Team member authority.
- Query `workflowId`, when present, remains a draft workflow identity hint.
- `publishedServiceId` remains callable service runtime identity.
- Files must not participate in identity conversion, owner calculation, workflow serialization, or member binding.

## Deferred Backend Contract

Issue #2266 should define and implement the backend multipart boundary. The expected shape is:

```http
POST /api/scopes/{scopeId}/workflow/draft-run
Accept: text/event-stream
Content-Type: multipart/form-data
```

Expected form parts:

| Field | Type | Notes |
|---|---|---|
| `payload` | JSON | Existing draft-run JSON request body. |
| `file` | file | Repeated once for each selected file. |

The frontend must wait for that backend contract before enabling upload controls or sending multipart requests.

## Future Frontend Work

After #2266 is available, a follow-up frontend PR can add:

- File picker and drag/drop controls in the `Draft run` panel.
- Local `File[]` UI state scoped to the open draft-run panel.
- Duplicate-file handling based on browser file identity fields.
- Selected-file list with accessible remove and clear actions.
- Multipart transport in `runtimeRunsApi.streamDraftRun`, while preserving the no-file JSON path.
- Tests for JSON no-file behavior, multipart with repeated `file` parts, UI selection/removal, and cleanup on close.

The follow-up implementation must still keep files out of workflow YAML, headers, metadata, and identity models.

## Verification

For this frontend-only PR, verify:

```bash
pnpm --dir apps/aevatar-console-web tsc
pnpm --dir apps/aevatar-console-web test --runInBand src/pages/team-member-workflow-studio/index.test.tsx src/shared/api/runtimeRunsApi.test.ts
bash tools/ci/test_stability_guards.sh
```

The PR diff should remain limited to frontend files and frontend documentation. Backend source and backend tests belong in the backend issue or a separate backend PR.
