# Workflow Configuration Guidance Design

## Goal

Make the workflow node Configuration inspector understandable to a first-time
user without adding or changing any backend API, runtime contract, or server
implementation.

## Boundary

- The change is frontend-only under `apps/aevatar-console-web/` plus this
  design documentation.
- The frontend must use the workflow document and API contracts that already
  exist on `feat/2026-08-04_workflow-activity-vnext`.
- The browser must not infer NyxID capability availability, credentials,
  readiness, risk, operation schemas, or remediation actions.
- Aevatar service catalogue identities must not be presented as NyxID
  UserService identities. They are different resources.
- Existing step fields outside `parameters` remain preserved by the document
  update path, but this inspector does not create or edit them.

## Recommended Experience

The inspector uses one quiet, task-focused hierarchy:

1. The header names the step type and keeps the stable step ID out of the main
   task title.
2. A one-sentence purpose tells the user what the selected step does.
3. Settings are the primary surface. Every field says whether it is required
   or optional and shows either explanatory help or a persistent example.
4. Technical details and raw JSON remain collapsed for advanced inspection.
5. The footer uses `Cancel` and `Apply step`, and clearly indicates when edits
   still need to be applied before the workflow can be saved.

This hierarchy improves comprehension without inventing server-owned facts.

## Tool Call

`tool_call` stays on the existing parameter contract:

```yaml
parameters:
  tool: nyxid_proxy
  arguments: '{"query":{"request":"$input"}}'
```

The Settings surface exposes:

- **Tool name** (required): the exact registered runtime tool name, with an
  explicit explanation and a concrete example.
- **Arguments JSON** (optional): the existing string payload passed to the
  tool, with guidance that its property names come from that tool's contract.

The arguments editor deliberately preserves a string value. It must not parse
the text and write an object because the current workflow contract stores
`parameters.arguments` as a string.

The inspector does not offer a connected-action picker. Producing a truthful
picker would require caller-visible NyxID capability discovery, and no existing
frontend contract exposes that data. It also does not display fake readiness,
risk, or setup status.

## Validation And Draft Semantics

- Required values block `Apply step` only when structurally invalid.
- Optional values can remain empty and are removed through the existing
  configuration normalizer.
- Invalid raw JSON displays a localized summary while technical parser detail
  stays collapsed.
- Switching fields and editing Advanced JSON continue to share one local draft.
- Cancel and close retain the existing discard confirmation for unapplied edits.

## Accessibility And Responsive Behavior

- Existing field labels remain accessible names for inputs.
- Required/optional text is visible and is not communicated by color alone.
- The close action remains an icon button with an accessible label and tooltip.
- The inspector remains an overlay at narrower canvas widths and a stable side
  panel on wider screens; content scrolls without moving footer actions.

## Verification

Focused tests must prove:

- a user sees step purpose, field requirement state, and persistent guidance;
- tool arguments remain a string after editing and applying;
- no workflow capability discovery/readiness client is required;
- raw JSON errors and discard behavior still work;
- existing workflow editor integration accepts the unchanged
  `parametersText` callback contract.

Only affected frontend tests and changed-file static checks run locally. The
full frontend suite, typecheck, and production build are delegated to GitHub CI.
