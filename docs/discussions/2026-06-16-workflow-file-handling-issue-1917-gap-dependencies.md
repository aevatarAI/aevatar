---
title: "Workflow File Handling Issue 1917 Gap Dependencies"
status: draft
owner: eanzhao
last_updated: 2026-06-17
---

# Workflow File Handling Issue 1917 Gap Dependencies

## Issue 2199 Outcome

Multipart chat upload handling accepts an ordered list of files from the configured same-name form field. The HTTP boundary validates payload and every file before accepted workflow execution semantics begin. Any malformed payload, mismatched file field, invalid file, or file ingress failure rejects the whole request and does not produce a workflow run with partial file refs.

Accepted workflow runs use existing typed `WorkflowFileRef` descriptors as the file identity contract. `WorkflowMultipartChatRequestParser` appends descriptor-only `ChatInputContentPart.FileRef` entries in original form-file order and does not expose bytes, base64, multipart bodies, or provider raw responses to actor-facing input.

For workflow execution, per-file fan-out is expressed through `foreach` with `items_source=input_file_refs`. The module creates one child step per `WorkflowFileRef`, preserves the matching file ref through backpressure queue state, and aggregates per-item completion facts in actor-owned protobuf state. The parent `StepCompletedEvent` carries ordered descriptor-only `WorkflowFileItemResultSet` entries containing item index, file descriptor, success, output, and error.

`parallel` remains worker fan-out for the same input. File-dimensional concurrency composes as `foreach(items_source=input_file_refs) -> parallel(worker)` instead of introducing a second file-parallel primitive or a public batch proto.

## Dependency Notes

No new public `WorkflowFileBatch*` execution proto was introduced. Durable execution facts were added to `workflow_state.proto`: foreach item index/file/error, foreach parent item file refs/source, and backpressure queued input file refs. The stable completion contract for per-file foreach aggregation is the typed `WorkflowFileItemResultSet` field on `StepCompletedEvent`.

Ingress remains a Host/Infrastructure concern. Once file descriptors enter the run, partial success and failure are represented by actor/module-owned execution state and completion events, not by request-time fallback queries or process-local registries.

## Context

Issue 1917 left one dependency gap in the workflow file submit path: `WorkflowConnectedServiceFileSubmitOptions.Targets` already existed as the typed policy shape, but Workflow/Mainnet composition did not bind a stable configuration section or fail fast when a generic connected-service endpoint policy was malformed.

## Current Boundary

- `WorkflowFileSubmitToolSource` remains the only workflow submit runtime. It resolves Lark adapter targets and Host-configured generic connected-service targets into the same `workflow_file_submit` tool.
- Generic connected-service submit targets are bound from `WorkflowConnectedServiceFileSubmit:Targets` by shared Workflow capability composition.
- Endpoint policy is Host-owned. Workflow arguments can select a registered target and provide target-specific allowed arguments, but cannot override service slug, downstream path, HTTP method, headers, body fields, or multipart file-field name.
- Malformed generic endpoint policy fails during options validation. Host startup therefore rejects invalid deployment configuration before a workflow run can use it.

## Deployment Dependency

Real non-Lark target values must be supplied by verified Host deployment configuration, ConfigMap, or secret. The repository must not invent checked-in NyxID targets, use `.refactor-loop/host.env` as production configuration, or move generic target ownership into a provider adapter registry.
