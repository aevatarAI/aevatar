---
name: product-iteration-loop
description: Use when the user wants Codex to take an existing repository and keep iterating on weak product logic, missing UX flows, rough interaction design, or under-specified features. Codex should inspect the repo, identify product gaps, draft structured product-design handoff prompts for ChatGPT, draft visual/style handoff prompts for Stitch, implement the approved or default direction in code, verify the result, and prepare the next iteration.
---

# Product Iteration Loop

Use this skill when the user wants a semi-automatic product improvement loop around an existing codebase.

The operating model is:

- Codex owns repository inspection, gap analysis, implementation, testing, and iteration bookkeeping.
- ChatGPT is treated as a product-design copilot for product reasoning, feature shaping, flow critique, copy, edge cases, and tradeoff analysis.
- Stitch is treated as a visual-design copilot for style direction, layout exploration, UI polish, and component-level design proposals.

The preferred mode is real connectivity, not silent fallback.

If ChatGPT or Stitch can be reached through a registered plugin, connector, MCP server, app bridge, or approved local integration, use that route first.

For ChatGPT, a logged-in `chatgpt.com` session in the Codex in-app browser is an approved local integration when the user does not want to configure an API key. Use it before asking for `OPENAI_API_KEY`.

If ChatGPT or Stitch cannot be called directly from the current environment:

- do not pretend they were used
- explicitly report the missing connection as a blocker to the full intended workflow
- identify which capability is missing: `ChatGPT product reasoning`, `Stitch visual design`, or both
- produce a concrete connection checklist for the missing capability
- only then provide a temporary local fallback by generating a handoff prompt and continuing with the best local implementation

The fallback is a temporary degraded mode, not the target architecture.

## Default Outcome

One invocation should usually complete one iteration cycle:

1. Inspect the existing repository and product surface.
2. Find the highest-leverage product logic or UX weakness.
3. Produce a product brief for ChatGPT.
4. Produce a style brief for Stitch if UI changes are involved.
5. Decide a concrete implementation slice.
6. Make the code changes.
7. Run the smallest meaningful verification.
8. Report what changed, what remains weak, and what the next iteration should target.

Do not create an infinite loop or internal polling process. Finish one clear iteration per user request unless the user explicitly asks for more rounds in the same turn and the work remains bounded.

## Iteration Contract

Treat each run as a disciplined product-improvement loop, not a vague brainstorming session.

Each loop should leave behind these concrete artifacts, either in the response or in changed code:

- a clearly named product problem
- a scoped implementation decision
- actual ChatGPT / Stitch outputs when connected, or explicit blocker notes plus handoff briefs when not connected
- code changes or a justified stop condition
- verification evidence
- one recommended next iteration

If the user asks to "keep improving" the same repository over time, continue choosing the next highest-leverage bounded slice rather than reopening the full audit every time. Re-audit only the affected surfaces plus nearby dependencies unless the product direction has changed materially.

When external-design connectivity is missing and the user wants it fixed, the next iteration should prioritize enabling that integration before additional product polish work.

## Phase 0: Connectivity Check

Before starting product work, check whether the intended external design collaborators are actually reachable in the current environment.

Check for:

- a registered plugin for ChatGPT-oriented product consultation
- a logged-in ChatGPT web session in the Codex in-app browser
- a registered plugin for Stitch-oriented style consultation
- a local MCP server or app bridge that exposes those capabilities
- project-local integration files, plugin manifests, or connector configuration

For each required external capability, classify the state as one of:

- `Connected`
- `Declared but not configured`
- `Not present`

Classify ChatGPT web as `Connected` only after Codex can send a short prompt on `chatgpt.com` and read the reply. If login is required, open the page and let the user complete passwords, 2FA, passkeys, or authorization confirmations; do not perform sensitive login steps yourself.

If either ChatGPT or Stitch is not `Connected`, say so clearly in the work summary and treat integration enablement as an explicit concern of the iteration.

## Phase 1: Build Context

Before suggesting or changing anything:

- Read the local `AGENTS.md`, README, feature docs, and app entry points that define the product surface.
- Identify the actual user-facing surface area: routes, screens, API endpoints, workflows, commands, forms, dashboards, onboarding, empty states, failure states, and settings.
- Infer the current product model from code and docs, not from optimistic assumptions.
- Prefer concrete evidence: existing copy, component props, state transitions, tests, analytics hooks, reducers, workflows, and API contracts.

When the repository contains multiple apps or services, narrow to the surface area most related to the user's request. If the user did not specify one, choose the most user-facing application path first.

## Phase 2: Product Audit

Look for product issues in this order:

1. Broken core task flow: the user cannot finish the main job cleanly.
2. Missing decision support: the UI exposes actions but not enough context to choose well.
3. Mismatched mental model: naming, hierarchy, or interaction order does not match user intent.
4. Missing empty, loading, success, and error states.
5. Unclear data semantics: a field, card, tab, or action is overloaded with multiple meanings.
6. Weak trust signals: no status, no progress, no receipts, no confirmation, no recovery path.
7. Visual inconsistency or low-information layout.

For each candidate issue, write a short internal note with:

- `Observed behavior`
- `Why it is weak`
- `User cost`
- `Proposed improvement`
- `Implementation scope`

Then pick the highest-leverage issue that is still feasible within the current turn.

Prefer this scoring heuristic when multiple issues compete:

- `Severity`: how badly the issue harms task completion
- `Reach`: how many users or sessions likely encounter it
- `Clarity gain`: how much confusion the fix removes
- `Implementation fit`: how feasible it is within one iteration

Choose the item with the best combined leverage, not necessarily the most visually obvious flaw.

## Phase 3: ChatGPT Product Brief

When product reasoning would benefit from external brainstorming, first attempt the connected ChatGPT route if one exists. Prefer the ChatGPT web route described in [references/chatgpt-web-product-channel.md](references/chatgpt-web-product-channel.md) when the user prefers login over API keys. Otherwise, read [references/chatgpt-product-brief.md](references/chatgpt-product-brief.md) and fill it using the current repository context.

Use ChatGPT for:

- product strategy within a feature
- UX flow alternatives
- naming and copy refinement
- edge cases and failure-state design
- prioritization of confusing or missing logic

Do not offload implementation decisions that are already obvious from the codebase. The brief should be grounded in the actual repository, not generic startup advice.

If ChatGPT is unavailable, state that the product-reasoning integration is currently missing, produce the filled brief in your working notes or response, and continue only as a fallback. Treat the brief as a forcing function for sharper thinking, not as proof that the desired integration exists.

Do not call the project-local `chatgpt-product` MCP server as if it were a live ChatGPT route unless it is configured with an API key. In browser mode, use the MCP server only as a prompt builder if needed, then send the prompt through the actual ChatGPT webpage.

## Phase 4: Stitch Style Brief

When the work touches UI structure or presentation, first attempt the connected Stitch route if one exists. Otherwise, read [references/stitch-style-brief.md](references/stitch-style-brief.md) and fill it with the current product context.

Use Stitch for:

- layout direction
- visual hierarchy
- composition of cards, panels, tables, and forms
- tone, density, spacing, and visual contrast
- component-level polish for existing flows

Do not ask Stitch to redesign the whole product unless the user explicitly wants a broad re-theme. Prefer scoped visual briefs tied to one flow, one screen, or one component family.

If Stitch is unavailable, state that the visual-design integration is currently missing, still produce the filled brief, and then implement a strong local design direction only as a fallback that follows the repo's existing design system unless the user asked for a more radical redesign.

## Phase 5: Decide the Build Slice

Turn the audit and briefs into one implementation slice that is:

- user-visible
- testable
- mergeable
- small enough for one turn

Good slices:

- improve onboarding flow for a single feature
- fix a broken create/edit/publish flow
- redesign one dashboard panel with clearer information hierarchy
- add explicit loading/error/empty states to one critical path
- split one overloaded form into clearer steps

Avoid vague slices such as:

- "make the product better"
- "redesign everything"
- "fix UX across the app"

When possible, phrase the slice as:

`Improve [user goal] by changing [screen/flow/component] so users can [better outcome].`

## Phase 6: Implement

While implementing:

- preserve architectural rules in the current repository
- keep product semantics explicit in names, fields, and copy
- avoid fake data and placeholder logic unless the user explicitly asked for mockups
- update or add tests when behavior changes
- remove dead or duplicated UI/logic rather than layering another workaround on top

For frontend changes, aim for intentional product design, not generic AI-produced scaffolding. Respect the existing design system when one already exists. If the current UI is weak and the user wants stronger design, improve typography, hierarchy, spacing, and state design in a cohesive way.

## Phase 7: Verify

Run the smallest meaningful checks available for the touched surface:

- targeted tests
- build
- lint
- typecheck
- screenshot or manual flow inspection when relevant

If verification could not be run, say so explicitly and explain why.

## Phase 8: Return Useful Iteration Output

In the final response, include:

- what product issue you targeted
- what you changed
- whether ChatGPT and Stitch were actually connected, and if not, what connection was missing
- how ChatGPT and Stitch were used, or whether you generated fallback handoff briefs instead
- what you verified
- what the next iteration should likely tackle

Keep the summary concise, but make the product reasoning legible.

If the user appears to want an ongoing autonomous workflow, recommend using this skill repeatedly on the same repo with a short operator prompt such as:

`Use product-iteration-loop on this repository. Audit the current user-facing product, choose one high-leverage issue, generate ChatGPT and Stitch handoff briefs if useful, implement the best bounded fix, verify it, and end with the next iteration recommendation.`

## Output Shape

When useful, structure the working output in this order:

1. `Product audit`
2. `Chosen iteration`
3. `ChatGPT brief`
4. `Stitch brief`
5. `Implementation`
6. `Verification`
7. `Next iteration`

Do not always dump every section to the user. Use this structure mainly to keep your own work disciplined. The user-facing answer should stay concise unless they ask for the full audit.

## Guardrails

- Do not claim external-tool output that you did not actually obtain.
- Do not silently downgrade from intended integrations to local-only behavior.
- Do not treat a generated ChatGPT prompt as ChatGPT output; only a sent-and-read `chatgpt.com` or API response counts.
- Do not produce generic product advice detached from the repository.
- Do not broaden scope just because many issues are visible.
- Do not keep compatibility layers that preserve obviously bad product logic when the repo rules prefer deletion and simplification.
- Do not stop at analysis if the user clearly wants code changes.
- Do not ask the user to manually restate what is already discoverable from the repository.

## Escalation

Pause and ask the user only when one of these is true:

- there are multiple plausible product directions with materially different business consequences
- the repo lacks enough context to determine the intended user or core job
- the requested iteration would require a wide redesign across many screens or services

Otherwise, make a reasonable product assumption, state it after the work, and keep the iteration moving.
