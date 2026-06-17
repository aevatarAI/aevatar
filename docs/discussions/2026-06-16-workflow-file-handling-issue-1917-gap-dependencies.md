---
title: Workflow File Handling Issue 1917 Gap Dependencies
status: discussion
owner: workflow
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
