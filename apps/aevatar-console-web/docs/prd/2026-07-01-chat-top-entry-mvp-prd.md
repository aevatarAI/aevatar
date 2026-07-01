# Chat Top Entry MVP PRD

Date: 2026-07-01
Status: Revised for product/design review
Target surface: `apps/aevatar-console-web`
Related prototype: `../prototypes/2026-07-01-chat-top-entry-prototype.html`
Related PNG: `../prototypes/2026-07-01-chat-top-entry-prototype.png`

## Product Thesis

`Chat` becomes the first top-level entry in Aevatar Console. The first usable
version is intentionally narrow: use the backend `POST /api/chat` stream
directly to validate that a user can create a Team, add or create Members, and
bind/create Workflow behavior through a chat flow. Conversation history is local
to the browser in v1 so the team can validate the creation loop before building
durable backend chat history.

## Confirmed Decisions

- Top-level navigation order is `Chat`, `My Teams`, then Platform items, then
  `Settings`.
- User mental model is simply `Chat`.
- MVP backend integration uses `POST /api/chat` directly.
- `POST /api/chat` is an SSE endpoint. The frontend reads `data:` frames and
  updates the assistant message, progress state, token/usage details, and run
  handoff.
- Default create flow is `Plan -> Confirm -> Create`.
- Button confirmation is the primary path.
- Natural-language confirmation is also supported, including phrases such as
  `confirm`, `create it`, `go ahead`, `确认`, `确认创建`, and `开始创建`.
- If the user explicitly asks to create directly, Chat sends the direct-create
  request to `/api/chat` without waiting for the confirmation button.
- MVP supports both creating a Team from scratch and adding a Member to an
  existing Team.
- Conversation history is saved in frontend local storage for the first version.
- The default P0 result shape follows the current backend demo: assistant text,
  usage/tokens when provided, and an Observatory/run CTA when a run id or URL is
  available.
- Rich Team/Member/Workflow resource cards are conditional enhancements. They
  render only when `/api/chat` or tool results provide structured identifiers.

## Product Direction

Working title: `Chat`

One-line positioning: A Chat-first creation surface for Aevatar resources,
backed by `/api/chat`, with local conversation history and result handoff to
existing Console pages.

Design stance: operational and console-native. This should feel like the
existing Aevatar Chat page and ProLayout shell, not a marketing assistant or a
separate demo app.

Primary promise: a builder can type what they want, review a plan, confirm, and
land on created Team / Member / Workflow resources without manually switching
between setup pages.

## Target Users

- New builders who know the outcome they want but do not yet know the Aevatar
  object model.
- Operators who know Team, Member, and Workflow concepts but want a faster
  creation path than moving across multiple pages.
- Demo and sales users who need to create a usable AI Team from a prompt and
  then inspect it in the existing console.

## Problem

The console already has pages for Teams, Members, Workflows, Invocations, and
Runs. That is powerful after a user understands the model, but first-time
creation still feels fragmented. A user asking `create a customer support team
with refund and order lookup members` should not have to decide which page owns
each object. Chat should translate intent into a plan, call `/api/chat`, display
streamed progress, and expose next actions when resources are created or
accepted.

## Goals

- Make Chat the obvious first action in the console.
- Validate the complete chat creation loop through the existing backend
  `POST /api/chat` endpoint.
- Let users describe Team creation, Member addition, and Workflow creation in
  natural language.
- Give users a readable creation plan before writes by default.
- Make direct creation available when the prompt explicitly asks for it.
- Persist first-version history locally so users can revisit recent attempts
  during validation.
- Turn created resource identifiers into useful links when those identifiers are
  returned structurally.
- Preserve existing Team and Workflow pages as the source of detailed editing,
  testing, invoking, and run inspection.

## Non-Goals

- Do not build backend-backed chat history in the first version.
- Do not introduce a separate provisioning endpoint for the Chat UI.
- Do not call `POST /api/scopes/{scopeId}/provision-workflow` from the Chat UI
  for this MVP.
- Do not rebuild the Team detail page inside Chat.
- Do not rebuild the Workflow editor inside Chat.
- Do not make Chat a generic LLM playground.
- Do not fabricate resource links or resource cards when identifiers are
  missing.
- Do not parse arbitrary assistant prose as proof that a Team, Member, or
  Workflow exists. Natural-language output can be shown as text, but links and
  cards require structured identifiers.
- Do not label accepted or streamed intermediate states as fully complete until
  the stream or linked read model proves the final state.

## Backend Contract

### Primary Endpoint

Use:

```http
POST /api/chat
Accept: text/event-stream
Content-Type: application/json
```

The endpoint supports `application/json` and `multipart/form-data`. The Chat
Top Entry MVP only requires JSON.

Minimum JSON request shape:

```json
{
  "prompt": "Create a customer support team with refund and order lookup members.",
  "sessionId": "local-chat-session-id",
  "scopeId": "scope-a"
}
```

Relevant optional fields from the backend contract:

- `prompt`: user message for this run.
- `sessionId`: frontend-generated local conversation id for correlation.
- `scopeId`: current workspace/scope when available.
- `workflow`: registered workflow lookup. The prototype assumes the default
  backend routing can handle creation prompts; implementation can supply a
  product-approved workflow value only if backend requires it.
- `workflowYamls`: inline workflow YAML bundle; not required for the Chat Top
  Entry MVP UI.
- `source`: typed source for advanced run targeting; out of scope for the first
  creation UI.

### Streaming Response

`POST /api/chat` returns SSE frames. The frontend should:

- Parse `data:` lines.
- Ignore SSE comments/heartbeats.
- Append assistant text as frames arrive.
- Surface step/tool/progress frames as a compact progress card when possible.
- Treat `runFinished` as terminal success for the current request.
- Treat `runError` or HTTP errors as recoverable error states.
- Extract resource identifiers conservatively from structured frames, tool
  results, or a final JSON object. Do not infer identifiers from arbitrary
  assistant prose.

Useful event concepts already visible in backend examples:

- `runFinished`
- `runError`
- `usage`
- `stepStarted`
- `stepFinished`
- `custom.name = "aevatar.run.context"`
- `custom.name = "aevatar.step.request"`
- `custom.name = "aevatar.step.completed"`
- `custom.name = "aevatar.raw.observed"`

The frontend should not depend on every frame type being present. The minimum
acceptable behavior is: stream assistant output, show the run as active while
the response is open, then show success/error when the stream terminates with a
known final frame or failure.

### Current Rendering Capability

Current frontend/backend capability supports a backend-native Chat result:

- Plain assistant messages rendered as sanitized markdown-ish text.
- Runtime step/tool progress when corresponding SSE frames are present.
- Token/usage display when the stream provides usage fields.
- Observatory/run handoff when the stream exposes `runId`, `observatoryUrl`, or
  enough run context to build an existing Observatory link.
- Approval and intervention cards already supported by the existing Chat runtime
  model.

The current project does not have a stable first-class `createdResources`
contract for rich Team/Member/Workflow cards. The frontend can render those
cards once it receives reliable structured data, but it should not infer them
from a sentence such as `已创建团队`.

### Conditional Resource Result Model

The backend may later return created resource identifiers in structured frames,
tool results, or a final JSON object. The frontend result extractor should
recognize these fields when present:

- `scopeId`
- `teamId`
- `memberId`
- `workflowId`
- `studioUrl`
- `observatoryUrl`
- `bindingRunId`
- `scheduleId`
- `runId`
- `serviceId`

No identifier means no generated resource card. Show the assistant message,
token/usage line if present, and Observatory CTA if run context exists.

If identifiers are returned only in free-form assistant prose, treat the
response as text-only for P0. This avoids fake links and avoids implying that a
resource is visible in Team pages before the read model proves it.

## Local History

First version history is frontend-only.

Recommended local storage key:

```text
aevatar.chat.localHistory.v1:{scopeId}
```

Each local conversation record should include:

- `id`: local conversation/session id, reused as `/api/chat` `sessionId`.
- `scopeId`
- `title`: derived from first user message.
- `createdAt`
- `updatedAt`
- `messages`: user and assistant messages needed to redraw the thread.
- `lastStatus`: `draft | streaming | plan_ready | accepted | error`.
- `createdResources`: conservative list of extracted resources and links; empty
  for text-only backend responses.

Local history behavior:

- `New Chat` creates a new local conversation id.
- Sending a prompt appends a user message and a streaming assistant placeholder.
- Streamed frames update the active assistant message and local record.
- Confirmation button sends a normal user message, such as `确认创建`, through
  `/api/chat` with the same `sessionId`.
- Local history can be cleared by browser storage cleanup; no cross-device or
  server persistence is promised in MVP.

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

- Existing Aevatar console shell and light ProLayout side navigation.
- Chat container based on the current `/chat` page structure:
  - top internal `Console` bar with `New Chat` and `Tools`.
  - left local history rail.
  - main chat rail with messages, starter prompts, plan cards, progress cards,
    result cards, and composer.
- No permanent right context rail in MVP. Context appears inside the chat stage
  as compact status chips or summary cards.

## Core User Flows

### Flow 1: Create a Team From Scratch

1. User opens `Chat`.
2. Empty state asks what Aevatar should create.
3. User enters: `Create a customer support team with refund and order lookup members.`
4. Frontend creates or reuses a local `sessionId`.
5. Frontend calls `POST /api/chat` with `prompt`, `sessionId`, and `scopeId`.
6. Chat streams a plan and does not write yet by default.
7. User clicks `Confirm Create` or replies `confirm`.
8. Frontend sends the confirmation through `POST /api/chat` with the same
   `sessionId`.
9. Chat streams creation progress.
10. Chat shows the backend-native result: assistant text, usage/tokens when
    present, and an Observatory CTA when run context is available.
11. Resource cards link to existing pages only when identifiers are available:
    - `Open Team`
    - `Edit Workflow`
    - `Invoke`
    - `Runs`

### Flow 2: Add a Member to an Existing Team

1. User enters: `Add a QA reviewer member to Customer Support.`
2. Frontend sends the prompt to `/api/chat`.
3. Backend/assistant identifies the target Team or asks the user to clarify.
4. Chat presents a plan for the new Member and optional Workflow.
5. User confirms by button or natural language.
6. Chat streams creation/binding progress.
7. Chat first shows assistant text and run handoff; result cards appear only
   when IDs are reliable.

### Flow 3: Direct Create

1. User enters: `Directly create a demo FAQ team, no confirmation needed.`
2. Frontend sends the prompt to `/api/chat`.
3. Chat shows `Direct create requested` and starts displaying streamed progress.
4. Chat shows streamed text, usage, run handoff, and conditional resource cards
   only if structured IDs are present.

### Flow 4: Resume Local History

1. User returns to Chat in the same browser.
2. Left rail loads local conversations from `localStorage`.
3. User selects a previous conversation.
4. Messages and extracted resource cards are restored locally.
5. If the previous run is not active, the composer can continue the same
   `sessionId`; backend continuity depends on `/api/chat` session behavior.

## Functional Requirements

### P0

- `Chat` appears as the first top-level navigation item.
- `My Teams` appears immediately after `Chat`.
- Chat empty state presents creation-oriented starter actions:
  - `Create a team`
  - `Add member to existing team`
  - `Create workflow member`
  - `Direct create demo team`
- Sending a message calls `POST /api/chat`.
- Request uses JSON with at least `prompt`, local `sessionId`, and current
  `scopeId` when available.
- Frontend parses SSE `data:` frames from `/api/chat`.
- Frontend saves conversations locally in the browser.
- Chat supports a plan card with:
  - Team summary
  - Member list
  - Workflow summary
  - `Confirm Create`
  - `Edit Plan`
- `Confirm Create` sends a confirmation message through `/api/chat` using the
  same local `sessionId`.
- Natural-language confirmation uses the same send path as the button.
- Direct-create starter sends a prompt that clearly asks the backend to skip
  confirmation.
- Chat renders the backend-native response shape by default:
  - assistant text
  - token/usage row when present
  - `View in Observatory` or equivalent run CTA when run context is present
- Chat renders structured resource result cards only when reliable identifiers
  are present.
- Chat renders a plain assistant message when no reliable identifiers are
  present.
- Result cards use existing routes for details and editing.
- Active and accepted states are labeled honestly.

### P1

- Preserve target Team context from recent navigation or query params and
  include it in the prompt context only when product-approved.
- Add a small local-history management menu: rename conversation, delete
  conversation, clear local history.
- Show raw stream/debug details behind `Tools`, not on the default path.
- Poll linked read models after creation only when the result includes enough
  identifiers and the user opens a result action.
- Group result cards by Team, Member, Workflow, Schedule, and Run/Observatory.
- Ask backend for a stable `createdResources` or equivalent custom SSE payload
  if rich cards become a P0 requirement.

### P2

- Replace local history with backend-backed history only after the creation loop
  is validated.
- Provide reusable prompt templates.
- Show a diff between prior plan and revised plan.
- Support multi-Team creation plans.

## Result Link Rules

| Available fields | Card | Action |
| --- | --- | --- |
| `scopeId + teamId` | Team | `/scopes/{scopeId}/teams/{teamId}` |
| `scopeId + teamId + memberId` | Member Workflow | `/scopes/{scopeId}/teams/{teamId}/members/{memberId}/workflow` |
| `scopeId + teamId + memberId` | Invoke | `/scopes/{scopeId}/teams/{teamId}/members/{memberId}/invoke` |
| `scopeId + teamId + memberId` | Runs | `/scopes/{scopeId}/teams/{teamId}/members/{memberId}/runs` |
| `studioUrl` | Workflow Studio | Use returned `studioUrl` |
| `observatoryUrl` | Observatory | Use returned `observatoryUrl` |
| `runId` | Observatory | Build or open the existing Observatory run route |
| `bindingRunId` | Binding status | Show `Binding accepted`; polling is not P0 |
| `scheduleId` | Schedule status | Show `Schedule accepted` |
| Text + usage/run context | Backend-native result | Show assistant text, usage/tokens, and Observatory CTA |
| Text only | Assistant message | Do not generate a fake card |

## UI States

### Empty

The first screen should communicate creation immediately:

> Tell Aevatar what you want to create. Chat sends your request to `/api/chat`
> and drafts a plan before creating resources.

### Streaming

After send:

- Disable duplicate send for the same composer.
- Show assistant placeholder with streaming indicator.
- Show compact progress when SSE frames provide steps/tools.
- Save every visible update to local history.

### Plan Ready

Show a creation plan card with:

- planned Team
- planned Members
- planned Workflow
- `Confirm Create`
- `Edit Plan`

### Direct Create

When the user explicitly asks to skip confirmation, show:

> Direct create requested. Streaming creation from `/api/chat`.

### Creating

Show progress as streamed milestones:

- Team create accepted or created
- Member shell accepted or created
- Workflow binding accepted or created
- Optional run/schedule accepted

### Accepted / Created

Show the assistant's final text, usage/tokens if present, and Observatory/run
CTA when available. Show resource cards and next actions only when structured
resource identifiers are available. Avoid saying everything is fully ready if
the stream only proves an accepted stage.

### Error

Show the failing step, preserve any already extracted resources, and offer:

- `Retry`
- `Edit prompt`
- `Start new chat`
- relevant resource links if available

## Copy

Primary empty state:

> What do you want Aevatar to create?

Starter prompts:

- `Create a customer support team with refund and order lookup members.`
- `Add a QA reviewer member to an existing team.`
- `Create a workflow member that summarizes failed runs every morning.`
- `Directly create a demo FAQ team, no confirmation needed.`

Streaming state:

> Sending to `/api/chat`...

Accepted state:

> Creation accepted. Some resources may take a moment to appear in Team pages.

Text-only successful state:

> Creation request completed. Review this run in the Observatory for execution
> details.

Local history note:

> History is saved in this browser for the MVP.

## Metrics

- Chat starter prompt click-through rate.
- `/api/chat` send success rate.
- SSE stream completion rate.
- Plan confirmation rate.
- Direct create usage rate.
- Successful accepted/created resource rate.
- Open Team / Edit Workflow / Invoke / Runs click-through after result cards.
- Observatory CTA click-through for text-only backend responses.
- Time from initial prompt to first result card.
- User fallback rate to manual Team creation.

## Risks And Open Assumptions

- `/api/chat` may stream useful text before it streams structured identifiers.
  The UI must handle text-only success.
- The exact created-resource fields may appear in tool output, custom frames, or
  a final JSON object. The extractor must be conservative and must not treat
  arbitrary assistant prose as a resource contract.
- Backend session continuity for repeated `sessionId` should be verified. The
  frontend can reuse `sessionId`, but backend semantics own whether that is a
  true multi-turn memory boundary.
- Local history can be lost if the user clears browser storage.
- Direct create depends on backend prompt behavior; the frontend can only pass a
  clear direct-create prompt.
- Accepted and read-model-visible are different states. The UI should label
  them honestly.

## Validation Plan

1. 3-second comprehension test: user can tell Chat creates Team / Member /
   Workflow resources.
2. First-task test: user creates a simple Team from a prompt and sees a plan.
3. Confirmation test: button confirmation and `确认创建` both continue the same
   `/api/chat` flow.
4. Direct-create test: direct prompt skips the plan wait and streams progress.
5. Existing-Team test: prompt adds a Member to a named existing Team.
6. Text-only result test: backend-demo-style response renders assistant text,
   usage/tokens, and Observatory CTA without resource cards.
7. Result-link test: result cards only appear when IDs are present and route to
   existing pages.
8. Local-history test: refresh page and see the recent conversation restored
   from local storage.
9. Error test: failed `/api/chat` request preserves the user prompt and allows
   retry.

## Implementation Handoff

Implement the Chat Top Entry MVP in `apps/aevatar-console-web` only.

1. Make `Chat` visible as the first top-level navigation item.
2. Keep `My Teams` second and Platform items after it.
3. Use the existing Chat page visual structure and controls.
4. Update the Chat empty state to present creation-oriented starter prompts.
5. On send, call `POST /api/chat` with JSON:
   - `prompt`
   - frontend-generated `sessionId`
   - current `scopeId` when available
6. Read the SSE response from `response.body`.
7. Parse `data:` frames and ignore heartbeat comments.
8. Update assistant message/progress from stream frames.
9. Store conversation state in local storage by scope.
10. Add a plan card presentation with `Confirm Create` and `Edit Plan`.
11. Wire `Confirm Create` to send a confirmation message through `/api/chat`.
12. Treat natural-language confirmation as a normal prompt path.
13. Render backend-demo-style text results, token/usage details, and Observatory
    CTA from run context.
14. Add conservative result extraction for known resource fields.
15. Render resource result cards only when identifiers are reliable.
16. Use existing routes for Team, Member Workflow Studio, Invoke, Runs, and
    Observatory actions.
17. Label stream/accepted states honestly.
18. Add focused tests for navigation order, starter prompts, `/api/chat` send
    payload, SSE frame parsing, local history persistence, confirmation button,
    direct create starter text, text-only result rendering, Observatory CTA, and
    result link generation.

## Acceptance Criteria

- `Chat` is the first visible navigation item.
- `My Teams` is the second visible navigation item.
- Chat empty state clearly supports creating Teams, Members, and Workflows.
- Sending a prompt calls `POST /api/chat`.
- The request includes `prompt`, local `sessionId`, and available `scopeId`.
- SSE frames update the visible assistant/progress state.
- Backend-demo-style text responses render without fabricated resource cards.
- Token/usage details render when the stream provides usage fields.
- Observatory/run CTA renders when run context is available.
- Conversations persist in browser local storage.
- Default starter flow asks for a plan before creation.
- Direct create starter explicitly asks to skip confirmation.
- `Confirm Create` is available on a plan card.
- `Confirm Create` sends a confirmation message to `/api/chat`.
- Natural-language confirmation remains possible via the composer.
- Result cards link to existing pages when required IDs are available.
- Result cards do not fabricate links when IDs are missing.
- Accepted or streamed states are not mislabeled as completed.
