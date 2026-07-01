# Chat Top Entry MVP PRD

Date: 2026-07-01
Status: Draft for product/design review
Target surface: `apps/aevatar-console-web`
Related prototype: `../prototypes/2026-07-01-chat-top-entry-prototype.html`

## Product Thesis

Chat becomes the first top-level entry in Aevatar Console. It is not a hidden
debug chat or a generic LLM playground. It is the primary natural-language
creation surface for planning, confirming, creating, and iterating Aevatar
Teams, Members, and Workflows while handing off detailed management to the
existing Team, Member Workflow Studio, Invoke, Runs, and Observatory surfaces.

## Confirmed Decisions

- Top-level navigation order is `Chat`, `My Teams`, then Platform items, then
  `Settings`.
- User mental model is simply `Chat`.
- Default create flow is `Plan -> Confirm -> Create`.
- Button confirmation is the primary path.
- Natural-language confirmation is also supported, including phrases such as
  `confirm`, `create it`, `go ahead`, `确认`, `确认创建`, and `开始创建`.
- If the user explicitly asks to create directly, Chat skips the confirmation
  step and starts provisioning.
- MVP supports both creating a new Team from scratch and adding a Member to an
  existing Team.
- Chat result cards must connect to existing pages instead of recreating Team or
  Workflow management inside Chat.

## Target Users

- New builders who know the outcome they want, but do not yet know the Aevatar
  object model.
- Operators who know Team, Member, and Workflow concepts, but want a faster
  creation path than jumping between multiple pages.
- Demo and sales users who need to create a usable AI Team from a prompt and
  then inspect it in the existing console.

## Problem

The current console already has separate surfaces for Teams, Members, Workflows,
Invocations, and Runs. That is powerful after the user understands the model,
but it makes first-time creation feel fragmented. A user asking "create a
support team with refund and order lookup members" should not need to decide
which page or endpoint owns each object. Chat should translate intent into a
plan, get confirmation, then create the required Aevatar resources and expose
clear next actions.

## Goals

- Make Chat the obvious first action in the console.
- Let users describe a Team or Member addition in natural language.
- Give users a readable creation plan before writes by default.
- Make direct creation available when the user explicitly asks for it.
- Surface backend async semantics honestly: `accepted` is not the same as
  fully materialized.
- Turn created resource identities into useful links.
- Preserve existing Team and Workflow pages as the source of detailed editing,
  testing, invoking, and run inspection.

## Non-Goals

- Do not rebuild the Team detail page inside Chat.
- Do not rebuild the Workflow editor inside Chat.
- Do not make Chat a general LLM playground.
- Do not require users to understand raw actor, command, binding run, or schedule
  identifiers for normal success.
- Do not imply strong consistency from a weak or asynchronous acknowledgement.

## Backend Contract Notes

These notes come from the `feature/integrate` backend branch and should guide the
frontend result model.

### Team

Endpoint shape:

- `POST /api/scopes/{scopeId}/teams`
- `GET /api/scopes/{scopeId}/teams`
- `GET /api/scopes/{scopeId}/teams/{teamId}`

Useful response fields:

- `teamId`
- `scopeId`
- `displayName`
- `description`
- `lifecycleStage`
- `memberCount`
- `entryMemberId`
- `createdAt`
- `updatedAt`

### Member

Endpoint shape:

- `POST /api/scopes/{scopeId}/members`
- `GET /api/scopes/{scopeId}/members`
- `GET /api/scopes/{scopeId}/members/{memberId}`
- `PUT /api/scopes/{scopeId}/members/{memberId}/binding`

Useful response fields:

- `memberId`
- `scopeId`
- `displayName`
- `description`
- `implementationKind`
- `lifecycleStage`
- `publishedServiceId`
- `lastBoundRevisionId`
- `teamId`
- `implementationRef.workflowId`
- `implementationRef.workflowRevision`

Important constraint:

- Member creation should generally create the shell first. Workflow or script
  implementation details are bound through the binding route rather than being
  treated as inline creation metadata.

### Workflow Provisioning

Feature branch introduces a one-call provisioning path:

- HTTP: `POST /api/scopes/{scopeId}/provision-workflow`
- Tool: `aevatar_provision_workflow_schedule`

Provisioning composes:

- Create workflow member shell.
- Bind inline workflow YAML.
- Create schedule or near-future demo fire.

Useful tool/result fields:

- `status` / `bindingStatus`
- `memberId`
- `scopeId`
- `scheduleId`
- `bindingRunId`
- `observatoryUrl`
- `studioUrl`

Important async semantics:

- Provisioning returns accepted state.
- It does not return a synchronous `runId`.
- Binding and run materialization can lag behind the acknowledgement.
- The user should be sent to Observatory, Team detail, or Workflow Studio instead
  of being told everything is fully ready immediately.

## Information Architecture

Top-level navigation:

1. `Chat`
2. `My Teams`
3. Platform items:
   - `Event Stream`
   - `Services`
   - `Governance`
   - `Deployments`
   - `Topology`
4. `Settings`

Chat page layout:

- Left rail: conversation history and `New Chat`.
- Main rail: chat messages, empty starters, plan cards, progress cards, result
  cards, and composer.
- Right rail: current workspace, selected target Team, pending plan summary,
  created items, and next actions.

The right rail can collapse on narrow screens.

## User Journeys

### Journey 1: Create a Team From Scratch

1. User opens `Chat`.
2. Empty state asks what Aevatar should create.
3. User enters: `Create a customer support team with refund and order lookup members.`
4. Chat produces a plan and does not write yet.
5. User clicks `Confirm Create` or replies `confirm`.
6. Chat provisions the Team, Members, Workflow bindings, and optional demo run.
7. Result cards show accepted resources and next actions:
   - `Open Team`
   - `Edit Workflow`
   - `Test Team`
   - `View Runs`

### Journey 2: Add a Member to an Existing Team

1. User enters: `Add a QA reviewer member to Customer Support.`
2. Chat identifies or asks to confirm the target Team.
3. Chat produces a plan for the new Member and optional Workflow.
4. User confirms by button or natural language.
5. Chat provisions the Member and binding.
6. Result card links to Team detail and Member Workflow Studio.

### Journey 3: Direct Create

1. User enters: `Directly create a demo FAQ team, no confirmation needed.`
2. Chat marks direct create requested.
3. Chat starts provisioning immediately.
4. Result cards show accepted resources and next actions.

## Functional Requirements

### P0

- `Chat` appears as the first top-level navigation item.
- `My Teams` appears immediately after `Chat`.
- Chat empty state presents creation-oriented starter actions:
  - `Create a team`
  - `Add member to existing team`
  - `Create workflow member`
  - `Direct create demo team`
- Chat supports a plan card with:
  - Team summary
  - Member list
  - Workflow summary
  - `Confirm Create`
  - `Edit Plan`
- `Confirm Create` sends a confirmation action through the chat flow.
- Natural-language confirmation uses the same backend flow as the button.
- Direct create prompt starter sends a prompt that clearly asks the backend to
  skip confirmation.
- Chat renders structured resource result cards when a tool result or assistant
  message includes known identifiers.
- Chat renders a plain message when no reliable identifiers are present.
- Result cards use existing routes for details and editing.
- Async accepted states are labeled as accepted or pending, not complete.

### P1

- Preserve target Team context from recent navigation or query params.
- Poll binding or read model status after `bindingRunId`.
- Show Observatory status when `observatoryUrl` is available.
- Allow a plan card to be revised in-place through a follow-up prompt.
- Group result cards by Team, Member, Workflow, Schedule, and Run/Observatory.

### P2

- Provide a compact "creation timeline" across conversations.
- Offer reusable prompt templates.
- Show a diff between prior plan and revised plan.
- Support multi-Team creation plans.

## Result Link Rules

| Available fields | Card | Action |
| --- | --- | --- |
| `scopeId + teamId` | Team | `/scopes/{scopeId}/teams/{teamId}` |
| `scopeId + teamId + memberId` | Member | `/scopes/{scopeId}/teams/{teamId}/members/{memberId}/workflow` |
| `scopeId + teamId + memberId` | Invoke | `/scopes/{scopeId}/teams/{teamId}/members/{memberId}/invoke` |
| `scopeId + teamId + memberId` | Runs | `/scopes/{scopeId}/teams/{teamId}/members/{memberId}/runs` |
| `studioUrl` | Workflow Studio | Use returned `studioUrl` |
| `observatoryUrl` | Observatory | Use returned `observatoryUrl` |
| `bindingRunId` | Binding status | Show `Binding accepted`; polling is P1 |
| `scheduleId` | Schedule status | Show `Schedule accepted` |
| Text only | Assistant message | Do not generate a fake card |

## UI States

### Empty

The first screen should communicate creation immediately:

> Tell Aevatar what you want to create. Chat drafts a plan first, then creates
> Teams, Members, and Workflows after you confirm.

### Plan Ready

Show a creation plan card with a clear primary action:

- `Confirm Create`
- `Edit Plan`

### Direct Create

When the user explicitly asks to skip confirmation, show:

> Direct create requested. Aevatar will start provisioning now.

### Creating

Show progress as accepted milestones:

- Team accepted
- Member shell accepted
- Workflow binding accepted
- Demo run scheduled

### Accepted

Show result cards and next actions. Avoid "fully ready" language until the
existing read models or binding status prove it.

### Error

Show the failing step, preserve any accepted resources, and offer a next action:

- `Open Team`
- `Retry binding`
- `Edit plan`
- `Start new chat`

## Copy

Primary empty state:

> What do you want Aevatar to create?

Starter prompts:

- `Create a customer support team with refund and order lookup members.`
- `Add a QA reviewer member to an existing team.`
- `Create a workflow member that summarizes failed runs every morning.`
- `Directly create a demo FAQ team, no confirmation needed.`

Accepted state:

> Provisioning accepted. Some resources may take a moment to appear in Team
> pages.

## Metrics

- Chat starter prompt click-through rate.
- Plan confirmation rate.
- Direct create usage rate.
- Successful accepted provisioning rate.
- Open Team / Edit Workflow / Invoke action click-through after result cards.
- Time from initial prompt to first accepted resource.
- User fallback rate to manual Team creation.

## Risks And Open Assumptions

- The chat stream may return structured identifiers only inside tool result JSON.
  The frontend should parse conservatively and never invent missing IDs.
- The one-call workflow provisioning path can return `studioUrl` only when the
  member is assigned to a Team. If no Team is available, Chat should fall back to
  Observatory or My Teams.
- A plan/confirm protocol may need backend prompt alignment. The frontend can
  send confirmation prompts, but reliable action gating depends on the backend
  assistant respecting plan-first behavior.
- Binding and schedule status are asynchronous. The MVP should show accepted
  status honestly and leave polling to P1.

## Implementation Handoff Prompt

Implement the Chat Top Entry MVP in `apps/aevatar-console-web` only.

1. Make `Chat` visible as the first top-level navigation item.
2. Keep `My Teams` second and Platform items after it.
3. Update the Chat empty state to present creation-oriented starter prompts.
4. Add a plan card presentation with `Confirm Create` and `Edit Plan` actions.
5. Wire `Confirm Create` to send a confirmation message through the existing
   chat stream.
6. Treat natural-language confirmation as a supported user prompt path.
7. Add conservative result extraction for known resource fields:
   `scopeId`, `teamId`, `memberId`, `studioUrl`, `observatoryUrl`,
   `bindingRunId`, `scheduleId`, `workflowId`, `runId`, and `serviceId`.
8. Render resource result cards only when identifiers are reliable.
9. Use existing routes for Team, Member Workflow Studio, Invoke, Runs, and
   Observatory actions.
10. Label async states as accepted or pending.
11. Add focused tests for navigation order, empty starter prompts,
   confirmation button behavior, direct create starter text, and result link
   generation.

## Acceptance Criteria

- `Chat` is the first visible navigation item.
- `My Teams` is the second visible navigation item.
- Chat empty state clearly supports creating Teams, Members, and Workflows.
- Default starter flow asks for a plan before creation.
- Direct create starter explicitly asks to skip confirmation.
- `Confirm Create` is available on a plan card.
- Natural-language confirmation remains possible via the composer.
- Result cards link to existing pages when the required IDs are available.
- Result cards do not fabricate links when IDs are missing.
- Accepted async states are not mislabeled as completed.
