---
title: "Workflow File Handling Issue 1917 Gap Dependencies"
status: draft
owner: eanzhao
last_updated: 2026-06-17
---

# Workflow File Handling Issue 1917 Gap Dependencies

## Context

Issue 1917 left one dependency gap in the workflow file submit path: `WorkflowConnectedServiceFileSubmitOptions.Targets` already existed as the typed policy shape, but Workflow/Mainnet composition did not bind a stable configuration section or fail fast when a generic connected-service endpoint policy was malformed.

## Current Boundary

- `WorkflowFileSubmitToolSource` remains the only workflow submit runtime. It resolves Lark adapter targets and Host-configured generic connected-service targets into the same `workflow_file_submit` tool.
- Generic connected-service submit targets are bound from `WorkflowConnectedServiceFileSubmit:Targets` by shared Workflow capability composition.
- Endpoint policy is Host-owned. Workflow arguments can select a registered target and provide target-specific allowed arguments, but cannot override service slug, downstream path, HTTP method, headers, body fields, or multipart file-field name.
- Malformed generic endpoint policy fails during options validation. Host startup therefore rejects invalid deployment configuration before a workflow run can use it.

## Deployment Dependency

Real non-Lark target values must be supplied by verified Host deployment configuration, ConfigMap, or secret. The repository must not invent checked-in NyxID targets, use `.refactor-loop/host.env` as production configuration, or move generic target ownership into a provider adapter registry.
