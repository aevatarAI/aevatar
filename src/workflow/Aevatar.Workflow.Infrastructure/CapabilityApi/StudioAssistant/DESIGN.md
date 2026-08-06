# DESIGN.md: NyxID Chat workflow transcript

## Source

- URL: https://nyx-chat-wf.surge.sh/?lang=zh#uc1a
- Capture date: 2026-08-06
- Evidence: Firecrawl branding/images/markdown scrape, full-page screenshot, live browser DOM and interaction inspection

## Reference Screenshot

![NyxID Chat workflow transcript](./.firecrawl/nyx-chat-wf-screenshot.png)

Use the screenshot as the visual source of truth for layout, hierarchy, density, and interaction emphasis. The Admin implementation keeps Aevatar branding and real production data contracts; it does not copy NyxID trademarks or demo-only playback controls.

## Design Summary

The page is a quiet, dense operational transcript. A compact session sidebar supports navigation, while a fixed-width conversation column carries user messages, plan state, approvals, connection journeys, tool progress, verification, and final delivery in one chronological stream. Purple is reserved for the current action; green, amber, and red communicate proven outcomes.

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
- Body: 11-13px; compact card headings: 12-13px; page title: 15-19px
- Letter spacing: `0`; weights: 400, 500, 600, 700

### Spacing And Layout

- Main container: `1022px` (measured)
- Grid: `236px 28px 758px` (measured)
- Composer content: `728px` (measured)
- Base spacing: 4px; common gaps: 8, 12, 16, 24, 28px
- Card radius: approximately 14px; controls: 8-12px
- Borders are preferred over shadows; fixed composer uses a translucent background and blur

## Components

- Top bar: brand, current conversation, services, run details, connection, and account controls
- Session sidebar: current and recent transcripts without a second task-lifecycle display
- Message row: small avatar, compact copy, right-aligned user bubble
- Plan card: revision, current step, typed source, effect evidence, retry/skip controls
- Pending input: question in the transcript; answer through the shared composer
- Approval card: action, target, actor, reversibility, expiry, approve/reject controls
- Connect card: NyxID-owned browser journey, explicit continuation report, actor postcondition proof
- Tool activity: ordered calls and receipts with collapsed detail
- Composer: stable 728px input, optional answer choices, attachment/services, send/steer/stop modes
- Inspector: off-canvas run facts and raw events so the transcript width never changes

## Page Patterns

- Desktop keeps session navigation and the conversation centered; run details open in a drawer.
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
- Keep the 1022px desktop grid and verify 390px mobile without horizontal overflow.

## Rerun Inputs

```text
workflow: firecrawl-website-design-clone
source_url: https://nyx-chat-wf.surge.sh/?lang=zh#uc1a
target_stack: embedded HTML/CSS/JavaScript in Aevatar Workflow Host Admin
output: StudioAssistant/DESIGN.md
```
