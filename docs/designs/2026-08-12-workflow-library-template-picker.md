---
title: "New Workflow Template Modal Picker Design"
status: review-ready
owner: potter-sun
last_updated: 2026-08-13
base_branch: feat/2026-08-04_workflow-activity-vnext
---

# New Workflow Template Modal Picker Design

This revision updates `docs/designs/aevatar-template-picker.excalidraw` after
re-checking three product facts:

1. the current implementation on `feat/2026-08-04_workflow-activity-vnext`
   already has a `New workflow` page with `Describe`, `Import YAML`, and
   `Use template`;
2. the expected interaction is: click `Use template`, open a modal, choose a
   template inside that modal, then create/open a draft; and
3. Calvin's feedback says either picker can work, but he is leaning toward
   frame 2 for the picker, which maps to the category-first “pick the kind of
   work” direction.

The previous design over-weighted `/runtime/workflows` as the primary template
selection surface. This version corrects that product boundary. The primary
template picker belongs in a modal launched from the vNext `New workflow` page.
The existing runtime Workflow Library can remain useful for advanced catalog
inspection and run handoff, but it should not be the main route a user takes
when they choose `Use template`.

## Product decision

`Use template` should be a modal action on the vNext `New workflow` page.

The durable flow is:

1. the user opens
   `/scopes/:scopeId/workflow-activity-vnext/workflows/new`;
2. the user clicks the `Use template` creation card;
3. a `Use template` modal opens;
4. the modal defaults to `Pick by work type`, following Calvin's frame 2
   preference;
5. the user can switch to `Search all templates` if they know the template name
   or want a direct comparison table;
6. the user previews the selected template inside the modal;
7. `Use template and open` validates the template YAML, creates a workspace
   draft, observes materialization, and opens the vNext editor.

This keeps the current implementation's correct save path while improving the
selection UI. It also keeps identity boundaries clear:

- a template is an inventory item or bundled starter;
- a draft `workflowId` exists only after copying/creating the template into the
  workspace;
- a published workflow or service identity is not implied by picking a
  template;
- vNext `Workflows` shows owned workspace workflows, not raw template inventory.

## Current implementation facts checked

The design is grounded in these implementation facts:

- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.tsx`
  - renders `Describe`, `Import YAML`, and `Use template`;
  - currently exposes `Use template` as an inline bundled-template `Select`;
  - validates template YAML through `parseYaml`;
  - saves through `studioApi.createWorkflowDraft`;
  - observes draft materialization;
  - navigates to the vNext workflow editor.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/workflowCreation.ts`
  - contains `BUNDLED_WORKFLOW_TEMPLATES`;
  - currently ships `Incident triage` as the seed starter.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`
  - uses `scopesApi.queryWorkflowCatalogue`;
  - owns Draft / Published / Archived workflow rows and actions;
  - should not show templates until a template has been copied into a workspace
    draft.
- `apps/aevatar-console-web/src/pages/workflows/index.tsx`
  - renders `/runtime/workflows` as a runtime Workflow Library table and drawer;
  - supports catalog inspection, `Run`, and `Open workflow editor`;
  - remains a secondary or advanced catalog surface in this design.
- `apps/aevatar-console-web/src/shared/studio/navigation.ts`
  - supports `focus=template:<name>` and `focus=workflow:<id>`;
  - can still support Studio template focus, but that route is not the primary
    `Use template` modal flow.

## Excalidraw frame guide

The revised Excalidraw has 9 frames:

1. **Current New workflow entry — keep the page, change the template
   affordance**  
   Shows that the existing `New workflow` page is the correct entry point. The
   design correction is that `Use template` opens a modal instead of expanding
   into a small inline selector.
2. **Use template modal — pick by work type**  
   The default modal state follows Calvin's frame 2 preference. It has
   category cards for Finance, Documents and contracts, HR and recruiting,
   Support and inbox, Market intelligence, and Meetings and projects.
3. **Category drilldown inside the modal**  
   Choosing a work type narrows templates inside the same modal. The user can
   switch category or search without leaving `New workflow`.
4. **Search all templates mode — second picker, same modal**  
   Keeps the table/list picker as a secondary mode for users who know the
   template name or need direct comparison.
5. **Template preview and CTA boundary**  
   Preview explains what the template reads, what it needs, what it does, and
   what gets created. The primary CTA is `Use template and open`.
6. **Create draft handoff — current implementation stays true**  
   Documents the current save path: selected template YAML, `parseYaml`,
   `createWorkflowDraft`, materialization observation, then editor navigation.
7. **Product boundary with existing pages**  
   Separates the `New workflow` page, `Use template` modal, vNext owned
   `Workflows` list, `/runtime/workflows` Library, and Studio/editor.
8. **States, responsive behavior, and accessibility**  
   Covers loading, no matches, catalog error, preview loading/error,
   validation findings, materialization delay, responsive layout, and keyboard
   behavior.
9. **Review questions for Louis and Calvin**  
   Makes the product and backend contract questions explicit.

## Recommended interaction

The recommended implementation direction is category-first modal selection:

- `Use template` opens a modal immediately.
- The default tab or segmented mode is `Pick by work type`.
- Category cards use the frame 2 information model:
  - category name;
  - example work keywords;
  - template count;
  - whether templates require external accounts.
- A selected category drills down to template rows/cards within the same modal.
- `Search all templates` remains available as the second picker.
- Preview is mandatory before creating a draft when template details or YAML
  are loaded asynchronously.
- `Use template and open` creates a workspace draft and opens the editor.

This gives the user a lightweight creation flow while preserving the richer
decision support from the original Excalidraw sketch.

## What should not change

- Do not turn `/runtime/workflows` into the primary route for picking a template
  from the `New workflow` page.
- Do not create a separate top-level Template Center unless product explicitly
  wants a new information architecture.
- Do not show raw templates in
  `/scopes/:scopeId/workflow-activity-vnext/workflows`; that page should show
  workspace-owned workflows after creation/copying.
- Do not make picking a template imply execution, connector binding, publishing,
  service identity, or member identity.
- Do not infer user-facing template safety from primitive names or weak labels
  once the UI needs to expose reads, needs, writes, or account posture.

## Questions for Louis and Calvin

The design intentionally raises these questions instead of hiding them inside
mock UI:

- Should the modal default be Calvin's frame 2 category-first picker, with
  `Search all templates` as the secondary picker?
- Should the first implementation source templates from bundled frontend
  starters, `/api/workflow-catalog`, or a new template-specific backend
  contract?
- Can backend expose typed template fields for category, keywords, reads,
  required accounts, external effects, output kind, ranking, popularity, and
  safety posture?
- After a user chooses a template, should `Use template and open` always create
  a workspace draft immediately, or should some templates open in Studio
  preview first?
- Should `/runtime/workflows` remain an advanced inspect/run catalog while
  `New workflow` owns the common template creation path?
- If the remote template catalog fails, should the modal fall back to bundled
  starters or block template creation with retry?

## Local validation

This PR changes design artifacts only. Validation is:

- parse `docs/designs/aevatar-template-picker.excalidraw` as JSON;
- verify the Excalidraw frame list and required semantic anchors;
- verify the PR diff contains only `docs/designs/*` files.

No frontend source, tests, styles, configuration, dependencies, or backend code
are changed by this design PR.
