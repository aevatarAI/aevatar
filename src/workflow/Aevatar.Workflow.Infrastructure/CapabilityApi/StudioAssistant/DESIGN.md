# DESIGN.md: NyxID Chat workflow transcript

## Source

- URL: https://nyx-chat-wf.surge.sh/?lang=zh#uc1a
- Capture date: 2026-08-06
- Evidence: Firecrawl branding/images/markdown scrape, full-page screenshot, live browser DOM and interaction inspection

## Reference Screenshot

![NyxID Chat workflow transcript](./.firecrawl/nyx-chat-wf-screenshot.png)

Use the screenshot as the visual source of truth for layout, hierarchy, density, and interaction emphasis. The Admin implementation keeps Aevatar branding and real production data contracts; it does not copy NyxID trademarks or demo-only playback controls.

## Design Summary

The page is a quiet operational transcript with confident workspace scale. A substantial session sidebar supports navigation, while a generous conversation column carries user messages, plan state, approvals, connection journeys, tool progress, verification, and final delivery in one chronological stream. Purple is reserved for the current action; green, amber, and red communicate proven outcomes.

## Design Tokens

### Colors

- Background: `#FAFAFA` (observed)
- Panel: `#FFFFFF` (observed)
- Primary action: `#5A2AF1` (observed from rendered controls)
- Secondary purple: `#A672FB` (observed)
- Text: `#18181B` / `#52525B` (observed)
- Border: `#E4E4E7` (observed)
- Success: `#059669` (Firecrawl branding evidence)
- Warning/error: amber and red semantic accents (inferred from cards)

### Typography

- Body and headings: Mona Sans
- Identifiers and wire facts: JetBrains Mono
- Admin implementation body: 15px; operational metadata: 11-13px; compact card headings: 14-15px; empty-state title: 28px
- Letter spacing: `0`; weights: 400, 500, 600, 700

### Spacing And Layout

- Reference main container: `1022px` (measured evidence, not an Admin implementation constraint)
- Admin workspace: up to `1400px` with 24px responsive gutters
- Admin grid: `264px` sidebar, `36px` gap, fluid conversation column
- Conversation and composer: up to `900px`; assistant cards: up to `860px`
- Base spacing: 4px; common gaps: 8, 12, 16, 24, 32, 36px
- Card radius: 8-12px; high-frequency controls: 36-40px; composer: 60px minimum height
- Borders are preferred over shadows; fixed composer uses a translucent background and blur

## Components

- Top bar: brand, current conversation, services, run details, connection, and account controls; hidden inside the Admin shell
- Session sidebar: current and recent transcripts without a second task-lifecycle display; wide enough for unambiguous session scanning
- Message row: 28px avatar, readable 15px copy, right-aligned user bubble
- Plan card: revision, current step, typed source, effect evidence, retry/skip controls
- Pending input: question in the transcript; answer through the shared composer
- Approval card: action, target, actor, reversibility, expiry, approve/reject controls
- Connect card: NyxID-owned browser journey, explicit continuation report, actor postcondition proof
- Tool activity: ordered calls and receipts with collapsed detail
- Composer: stable 900px input with 60px minimum height, optional answer choices, attachment/services, send/steer/stop modes
- Inspector: off-canvas run facts and raw events so the transcript width never changes

## Page Patterns

- Desktop uses the available Admin canvas instead of shrinking to the reference capture width; session navigation stays visible and the conversation remains centered while run details open in a drawer.
- Mobile collapses navigation and inspector into drawers, keeps all controls within viewport width, and stacks pending-input choices above the composer.
- The composer command changes with authoritative actor state: new `text`, pending `input.resolve`, active `task.steer`, or explicit `task.stop`.
- Task completion is displayed only from committed actor/current-state facts. A browser journey or transport ACK is never presented as verified success.

## Content Style

- Chinese operational copy is short, literal, and status-first.
- Explanations describe the current fact or required decision, not product features or tutorials.
- IDs and protocol details are available in the inspector, not in the primary reading flow.

## Agent Build Instructions

- Preserve the single `/api/chat` and actor current-state contracts.
- Do not infer task, approval, connection, or effect success from local browser state.
- Keep task and step status inside the chronological transcript; do not add a parallel lifecycle rail.
- Send clarification as one typed `input.resolve` from the shared composer.
- Keep `task.steer`, `task.stop`, `step.retry`, and `step.skip` explicit and identity/version guarded.
- Render final verification only when actor-owned postcondition or terminal facts prove it.
- Keep the `1400px / 264px / 900px` Admin scale system and verify both `1280x720` desktop and `390x844` mobile without horizontal overflow.

## Rerun Inputs

```text
workflow: firecrawl-website-design-clone
source_url: https://nyx-chat-wf.surge.sh/?lang=zh#uc1a
target_stack: embedded HTML/CSS/JavaScript in Aevatar Workflow Host Admin
output: StudioAssistant/DESIGN.md
```
