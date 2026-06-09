---
name: product-semantics-evolution-loop
description: Use when product meaning is unclear or drifting across UI, copy, API contracts, data models, tests, docs, or user expectations. Trigger for questions like "why is this here?", "this feels unlike n8n/Linear/GitHub", "this field name is misleading", "optional control is too prominent", "backend concept leaked into UI", "should this be a trigger/input/prompt/setting?", "make the product semantics self-evolve", or any task that should update implementation plus tests/docs so the corrected product semantics persist across future changes.
---

# Product Semantics Evolution Loop

Use this skill to turn a local product-confusion moment into durable product semantics. The loop is not page-specific: apply it to any surface where user-facing meaning, internal contracts, and actual runtime behavior disagree.

## Core Rule

Do not merely rename the confusing thing. Identify the authority for the concept, choose the correct product owner for the concept, move or hide the UI if the concept has the wrong prominence, then lock the new meaning with tests and, when useful, docs or skill updates.

## Loop

1. **Name the mismatch**
   - Write one sentence in plain product language: "The UI implies X, but the system/user expects Y."
   - Classify the mismatch:
     - `label`: the name/copy is wrong.
     - `placement`: the control has the wrong visual or workflow priority.
     - `ownership`: the concept belongs to another node, step, page, actor, trigger, or settings surface.
     - `contract`: API/type/schema names encode the wrong semantics.
     - `runtime`: the UI describes a path different from what execution actually does.
     - `mental-model`: comparable products or user expectations imply a different model.

2. **Recover the actual facts**
   - Read the local UI component, hook/state, API wrapper, backend endpoint or application service, tests, and relevant docs.
   - Verify what path actually runs. Do not infer from button text.
   - If the comparison target is current or product-specific and facts may have changed, browse or inspect primary sources. Otherwise use the comparison as a mental-model heuristic, not as authority.

3. **Choose the semantic owner**
   - Assign each concept to exactly one product owner.
   - Examples:
     - Prompt belongs to an LLM role/node/message authoring surface.
     - Input belongs to a trigger, start node, manual test payload, or invocation contract.
     - Execution status/logs belong to a run console/history surface.
     - Durable facts belong to read models or actor-owned contracts, not transient UI state.
   - History, run list, audit, or activity surfaces must have an explicit owner/scope before listing facts; do not query global runtime records and filter by weak display labels as a substitute for ownership.
   - If no owner exists, prefer adding or naming the missing owner over exposing a technical field globally.

4. **Set prominence by importance**
   - Primary surfaces are for the main workflow: command, state, result, error, trace, selection, or required setup.
   - Optional controls must be collapsed, secondary, contextual, or moved to a menu/drawer.
   - Optional manual-test or invocation payloads belong to a trigger/test context or a docked inspector/drawer; they must not occupy the global primary action area or float over the work canvas.
   - Placeholder panels for future capability should be removed or clearly owned by an existing contract; do not show empty input/output/result surfaces that imply missing runtime data is already modeled.
   - User-facing labels and default user-visible resource names should use product vocabulary, not backend/runtime identifiers. Contract IDs such as step types, execution IDs, service IDs, type URLs, raw frame counts, or payload keys belong in typed contracts or explicit diagnostics, not primary UI labels or generated editable names.
   - One-way publish, bind, promote, submit, or deploy actions should be command buttons with a separate status badge; do not use binary switches or toggles unless both on and off transitions are supported and honest.
   - Dangerous, irreversible, or costly actions must be explicit and confirmable.
   - Diagnostic details should not displace the user's next action unless the diagnostic is the next action.

5. **Pick the smallest durable change**
   - Change UI copy and layout only when the runtime semantics are already correct.
   - Change state/type names when code identifiers would keep reintroducing the confusion.
   - Change API/schema contracts only when the boundary itself is wrong and the migration can be made honestly.
   - If the backend field is a historical transport name but changing it would be broad, map it at the UI/application boundary and leave a clear typed follow-up only if needed.

6. **Implement as a semantic migration**
   - Keep unrelated refactors out.
   - Rename local variables/props so future code reads with the corrected product meaning.
   - Update tests to assert the new behavior and prominence, not just text snapshots.
   - Prefer user-observable tests: absent by default, appears after explicit action, button disabled for the right reason, correct payload sent, correct error shown.

7. **Verify the semantic contract**
   - Run targeted tests and type checks.
   - For frontend changes, inspect or screenshot the surface when possible.
   - Search for stale terms in the changed product area.
   - Confirm old misleading terms do not remain as user-facing labels unless intentionally preserved for an external protocol.

8. **Evolve the loop when a reusable rule appears**
   - If the fix reveals a general product heuristic not already covered here, update this skill.
   - Add only reusable rules, not one-off page facts.
   - Good update: "optional test payloads should not occupy primary run-console space."
   - Bad update: "the workflow studio cache node should show X."

## Decision Checks

Before editing, answer these privately:

- What would a user think this control means?
- What does the runtime actually do?
- Where would this concept live in a mature product model?
- Is the issue naming, placement, ownership, contract, runtime, or mental model?
- Is this concept required for the common path?
- What test would fail if someone reintroduces the old confusion?

## Output Expectations

When reporting back:

- State the semantic decision first.
- Mention the implementation surface changed.
- Mention tests or checks run.
- Call out any remaining deeper contract debt separately from the UI fix.

## Guardrails

- Do not create a new global framework for a local copy problem.
- Do not preserve misleading UI just because the backend field has that name.
- Do not change external API/protobuf/contracts casually; map at the boundary when the product fix is local.
- Do not make optional inputs prominent to explain technical capability.
- Do not use competitor behavior as law. Use it to understand user mental models, then reconcile with this product's architecture.
- Do not update this skill with page-specific rules.
