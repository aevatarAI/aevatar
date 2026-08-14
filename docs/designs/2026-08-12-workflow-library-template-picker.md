---
title: "Workflow Template Browse and Detail Design"
status: approved
owner: potter-sun
last_updated: 2026-08-14
base_branch: feat/2026-08-04_workflow-activity-vnext
---

# Workflow Template Browse and Detail Design

## Semantic correction

The previous design implied that choosing `Use template` on the `New workflow`
page opened a template-picker modal. The intended product model is different:

- `Use template` enters a full-page template browsing surface inside the
  existing New workflow creation flow;
- `View` is the action that opens a template detail modal; and
- `Use template` on a template row creates a workflow directly, without making
  detail preview a required intermediate step.

The browser, detail modal, design document, and future implementation must keep
those action meanings consistent.

## Product decision

The template experience has two canonical visual states.

### 1. Browse templates - full page

After the user chooses `Use template` from the existing `New workflow` method
selection page, the method cards are replaced by a full-page template browser.
This is a page-level creation surface, not content inside a modal, drawer, or
popover.

The page contains:

- a clear `Start from a template` heading and a way back to the New workflow
  method selection;
- search and sort controls;
- optional filters that narrow the current template list without creating a
  separate category-first journey;
- one scan-friendly list of templates; and
- pagination or result count when the catalog requires it.

Each template row aligns four decision areas:

1. template identity: name and short outcome-oriented description;
2. `Reads`: the input or source material the template consumes;
3. `Connection`: any account or external connection requirement; and
4. `Does`: the template's external-effect posture or output behavior.

Every row exposes exactly two primary workflow actions:

- `View` opens the detail modal for that template and creates nothing;
- `Use template` immediately starts workflow creation from that template.

`Use template` must not be hidden behind `View`, and preview must not be a
mandatory gate before workflow creation.

### 2. View template - detail modal

The detail modal is opened only by `View`. It overlays the template browser so
closing it returns the user to the same search, filters, sort, and scroll
position.

The modal contains:

- template name, summary, source, usage, and update facts;
- an `Overview` tab for the template contract and expected outcome;
- a `The 6 steps` tab that lists the workflow steps in execution order;
- read, connection, and external-effect disclosures; and
- a `Use this template` action that invokes the same creation command as the
  row-level `Use template` action.

The modal is read-only. It does not become an editor, binding surface, run
console, or publication workflow.

## Action contract

The actions have one owner and one meaning each.

| Action | Owner | Result |
| --- | --- | --- |
| New workflow `Use template` | creation-method selection | Show the full-page template browser. |
| Template-row `View` | template browser | Open the read-only detail modal. |
| Template-row `Use template` | template browser | Create a workflow directly from the selected template. |
| Modal `Use this template` | template detail modal | Invoke the same direct workflow-creation path as the row action. |

Both creation actions reuse the current vNext creation semantics:

1. take the selected template YAML;
2. validate it through `parseYaml`;
3. create the workspace draft through `createWorkflowDraft`;
4. observe draft materialization; and
5. navigate to the vNext workflow editor.

The action is disabled while its creation request is pending so repeated clicks
cannot create duplicate drafts. Validation, creation, and materialization
failures remain recoverable without losing the user's current template-browser
context.

## Identity boundaries

- A template is catalog inventory, not a workspace-owned workflow.
- A draft `workflowId` exists only after a template has been copied into the
  workspace creation path.
- Viewing a template does not create a draft or imply publication.
- Creating from a template does not imply connector binding, execution,
  publication, member identity, or published service identity.
- The vNext Workflows catalog continues to list workspace-owned workflows, not
  raw template inventory.

## Excalidraw scope

`docs/designs/aevatar-template-picker.excalidraw` contains exactly two frames:

1. `01 - Browse templates - full page`
2. `02 - View template - detail modal`

The file must not retain the previous category-first picker, category drilldown,
handoff diagram, product-boundary diagram, state inventory, or review-question
frames. Loading, empty, error, responsive, and accessibility behavior stays in
this document instead of adding more Excalidraw frames.

## PNG deliverables

The design PR includes three PNG artifacts derived from the two canonical
states:

- `workflow-template-browser-page.png`: the full-page browser by itself;
- `workflow-template-detail-modal.png`: a readable close view of the detail
  modal; and
- `workflow-template-picker-flow.png`: a presentation image showing the browse
  page and the View-to-modal relationship together.

The flow PNG is an export artifact, not a third Excalidraw frame.

## Accessibility and responsive behavior

- Search, sort, filters, `View`, and `Use template` are keyboard reachable.
- Row actions use explicit accessible names that include the template name.
- Modal focus moves to the dialog when opened, remains trapped inside it, and
  returns to the originating `View` action when closed.
- `Escape` closes the detail modal but never leaves the template browser.
- Narrow layouts preserve the two actions and stack the aligned facts instead
  of hiding them.
- Long names and descriptions wrap without moving or overlapping the row
  actions.
- Pending and error states do not resize the list or obscure the current
  selection context.

## Validation

This PR is design-only. It does not change frontend runtime code, tests, styles,
configuration, dependencies, or backend code.

Validation must confirm:

- the Excalidraw file parses as JSON;
- it contains exactly two frames with the canonical names;
- required action and semantic anchors exist in the Excalidraw text;
- the three PNG files exist, decode successfully, and match their intended
  visual states; and
- the PR diff remains limited to `docs/designs/*`.
