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
- **Arguments** (optional): the existing string payload passed to the tool.
  It opens in a beginner-oriented **Fields** mode and also provides a **JSON**
  mode for pasting or editing the complete payload.

Fields mode parses the current JSON object into recursive, manually editable
rows. Each property has a name input, a value-type select, a value control that
matches the selected type, and a remove action. Object and array values remain
structured and provide their own add actions. Empty arguments start as an
empty object. The supported value types are string, number, boolean, object,
array, and null.

JSON mode exposes the complete text payload and supports pasting. A valid edit
rebuilds Fields mode. An invalid edit remains visible in JSON mode with a clear
error; the inspector must not switch to Fields or replace that text with stale
structured data. Edits made in Fields mode immediately update JSON mode.

Fields mode rejects duplicate property names within the same object instead of
silently collapsing one row during serialization. Numbers that would change
when parsed and serialized by JavaScript, including unsafe integers,
underflowing or overflowing exponents, and high-precision decimals, remain
byte-for-byte intact in JSON mode. They cannot enter Fields mode because doing
so could change their value. Users can keep those values in JSON mode or
represent them as strings.

The arguments editor deliberately serializes its object back to JSON text and
preserves the final parameter as a string. It must not write an object into the
workflow document because the current contract stores `parameters.arguments`
as a string.

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
- Fields and JSON modes in the tool arguments editor share one local draft and
  synchronize only from valid values.
- Duplicate property names block apply until the rejected edit is corrected or
  discarded; unsafe integers are editable only as raw JSON or strings.
- Editing Advanced JSON for the complete step continues to synchronize the
  arguments editor through the existing configuration draft.
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
- nested argument values render as fields with type-appropriate controls;
- adding, removing, and changing argument value types update JSON mode;
- valid JSON edits update Fields mode while invalid JSON remains intact;
- duplicate property names never discard visible values, and unsafe integers
  remain exact in JSON mode;
- no workflow capability discovery/readiness client is required;
- raw JSON errors and discard behavior still work;
- existing workflow editor integration accepts the unchanged
  `parametersText` callback contract.

Only affected frontend tests and changed-file static checks run locally. The
full frontend suite, typecheck, and production build are delegated to GitHub CI.
