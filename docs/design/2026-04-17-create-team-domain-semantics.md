# Create Team Domain Semantics

## Goal

Clarify the real meaning of `behavior definition`, `team`, and `workflow` in the current Create Team / Studio / Teams flows before changing code again.

This document separates:

1. The semantics that the codebase currently implements.
2. The canonical semantics we should converge to.

---

## Current Code Reality

### 1. `workflow` is currently an overloaded term

In the current codebase, `workflow` refers to at least three different things:

1. A live scope workflow capability.
2. A stored YAML draft under scoped Studio storage.
3. A team-entry draft workflow used during `Create Team`.

These different things are flattened into the same frontend shape:

- `WorkflowSummary`
- `WorkflowFileResponse`

That is why the UI can easily confuse them.

### 2. `behavior definition` is currently a UI label, not a backend type

The left-side list in Studio is labeled `行为定义`, but its data source is the merged `visibleWorkflowSummaries` list.

That merged list is built from:

1. Runtime scope workflows.
2. Stored scoped workflow drafts.

So today, `行为定义` is not a first-class backend object with a stable type discriminator. It is a frontend label applied to a mixed workflow list.

### 3. `team` is not a workflow file

In current code, a `team` is the homepage-facing scope entry that has been bound/published into the scope.

That means:

- `Save Draft` does not create a team.
- `Publish Team Entry` is what turns the current draft into the scope-facing team entry.

### 4. `Create Team` stores a local pointer, not a full domain object

The current local resume model is `TeamCreateDraftPointer`, which stores only:

- `teamName`
- `entryName`
- `teamDraftWorkflowId`
- `teamDraftWorkflowName`
- `updatedAt`

It does **not** store:

- selected behavior definition id
- selected behavior definition name
- whether the draft was copied from an existing behavior definition
- whether the current draft has diverged from the original behavior definition

So `Create Team` cannot truthfully show "which behavior definition this team draft now represents".

---

## Confirmed Current Flows

### A. Studio `Save Draft`

In `teamMode=create`, `Save Draft` saves the current YAML as a scoped workflow draft and then updates the local team-create draft pointer.

It does **not** publish a team entry.

So after save, the durable thing we have is:

- a stored workflow draft
- plus a local `Create Team` resume pointer

### B. Studio `Publish Team Entry`

`Publish Team Entry` builds a workflow bundle and calls scope binding with:

- `displayName = entryName`
- `workflowYamls = current bundle`

That operation updates the scope binding and makes the scope serve that bundle as the entry.

So publishing is the transition from:

- draft workflow asset

to:

- live scope team entry

### C. Teams home page

Teams home treats "draft-only workflows" and "homepage team entry" as different things.

Its own text already encodes this distinction:

- behavior definitions may exist
- but they may not yet have formed the homepage team entry

So the homepage semantics are already closer to the correct mental model than the Create Team page is.

---

## Canonical Semantics We Should Use

### 1. Behavior Definition

`Behavior Definition` should mean:

> A reusable workflow definition asset that expresses an AI behavior pattern and can be used as a starting point or implementation source for one or more team entries.

Properties:

- reusable
- not tied to one team name
- not tied to one entry name
- can exist without being the active scope entry
- can be selected as a template/source during team creation

Examples:

- `hello-chat`
- `test03`
- `reply-bot`

### 2. Team Draft

`Team Draft` should mean:

> A draft of a concrete team entry being created for one team flow.

Properties:

- belongs to Create Team
- carries `teamName`
- carries `entryName`
- may start from a behavior definition
- may diverge from that behavior definition after edits
- is resumable
- is not yet the live team entry

Examples:

- `订单测试`
- `测试`

These should not be shown as generic behavior definitions.

### 3. Team

`Team` should mean:

> The scope-facing, published team entry currently bound into the scope and shown on Teams home.

Properties:

- published
- bound to the scope
- operational
- shown as the homepage team entry

This is not equivalent to "any saved workflow".

### 4. Workflow

`Workflow` should be treated as the implementation medium, not the top-level business concept.

In other words:

- a behavior definition has a workflow implementation
- a team draft has a workflow implementation
- a team entry is published from a workflow bundle

But `workflow` itself should not be the user-facing domain noun that replaces all three.

---

## Relationship Model

### Domain relationship

1. A `Behavior Definition` can seed a `Team Draft`.
2. A `Team Draft` owns its current editable workflow content.
3. A `Team Draft` may remember its source `Behavior Definition`, but it is not the same object anymore once editing begins.
4. Publishing a `Team Draft` creates or updates the scope `Team`.

### Practical interpretation

If a user selects `hello-chat` while creating `订单测试`:

1. `hello-chat` is the source behavior definition.
2. `订单测试` is the team draft.
3. After edits and save, the saved object in Create Team should still be understood as `订单测试` team draft.
4. The UI may optionally show that it was derived from `hello-chat`, but it must not pretend that `订单测试` itself is still the behavior definition.

---

## Where The Current UI Goes Wrong

### Problem 1. One list is pretending to be one concept, but it contains multiple concepts

The Studio left list labeled `行为定义` is fed by a merged workflow list that contains:

- live scope workflows
- stored drafts
- team-entry drafts in some paths

So the label is narrower than the data.

### Problem 2. Create Team saved draft card shows team-draft identity, not behavior-definition identity

The `Create Team` saved draft card is currently backed by `TeamCreateDraftPointer`, which only knows:

- team draft workflow id/name
- team name
- entry name

So when the user says "I edited the behavior definition", the Create Team page has no domain slot to display that source behavior definition.

### Problem 3. Save semantics and display semantics are mismatched

During `Create Team`, the editable canvas may be sourced from a selected behavior definition, but `Save Draft` persists the team-entry draft workflow.

That means:

- what is edited visually may feel like "behavior definition"
- what is saved semantically is "team draft"

If the UI does not say this clearly, the user will naturally think save should update the chosen behavior definition.

---

## Product Semantics We Should Lock In

### Rule 1

The left list in `Create Team` Studio should contain only reusable `Behavior Definitions`.

It should not contain current team drafts like:

- `测试`
- `订单测试`

### Rule 2

When a behavior definition is selected during `Create Team`, it is the **source** of the current team draft, not the identity of the saved draft.

### Rule 3

`Save Draft` in `Create Team` saves the current `Team Draft`, not the source behavior definition.

### Rule 4

`Create Team` resume cards should primarily describe the `Team Draft`.

If useful, they may additionally show:

- source behavior definition
- last selected behavior definition

But those must be explicitly labeled as source/reference, not shown as if they were the draft identity.

### Rule 5

`Publish Team Entry` turns the current `Team Draft` into the live `Team`.

It does not publish "a behavior definition library item".

---

## Expected User Mental Model

The correct user mental model should be:

1. I pick a `Behavior Definition` as a starting point.
2. I edit a `Team Draft` for my target team and entry.
3. I save that `Team Draft`.
4. I later resume that same `Team Draft`.
5. When ready, I publish it as the live `Team`.

Under this model:

- changing the selected behavior definition does not redefine what the saved team draft *is*
- saving a team draft should not overwrite unrelated behavior definitions
- Create Team should not claim that the saved card is "the behavior definition" unless it truly is

---

## Implementation Direction For The Next Step

No code change is proposed in this document, but the next implementation should follow these boundaries:

1. Separate `Behavior Definition Summary` from `Team Draft Summary` at the data-model level.
2. Stop using one flattened `WorkflowSummary` list as the sole source for both concepts.
3. Extend the `Create Team` draft pointer or replace it with a typed draft summary that can express:
   - `teamDraftWorkflowId`
   - `teamDraftWorkflowName`
   - `teamName`
   - `entryName`
   - `sourceBehaviorDefinitionId`
   - `sourceBehaviorDefinitionName`
   - `updatedAt`
4. Make the Studio `Create Team` page explicit about:
   - current team draft
   - selected behavior definition source
5. Keep Teams home focused on the published team entry, with drafts clearly presented as pre-entry assets.

---

## Bottom Line

The current bug is not just a save bug.

It is primarily a domain-semantics bug:

- the codebase currently uses `workflow` as a transport word for several different business objects
- the UI uses `行为定义` as if it were a clean type
- `Create Team` saves a `team draft`, but the user is encouraged to think they are still directly editing a `behavior definition`

That mismatch is why the saved result on `Create Team` does not match the user's expectation after editing a selected behavior definition.
