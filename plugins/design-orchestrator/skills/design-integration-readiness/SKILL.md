---
name: design-integration-readiness
description: Use when the user wants to verify or enable real connectivity from Codex to external product-design or visual-design tools such as ChatGPT and Stitch. Check whether plugin manifests, MCP servers, secrets, and launch commands are actually configured, report blockers clearly, and prepare the next concrete connection step.
---

# Design Integration Readiness

Use this skill to verify whether external design collaborators are truly reachable.

## What To Check

For each target integration such as ChatGPT or Stitch, verify:

- plugin manifest exists
- marketplace entry exists when needed
- MCP server entry exists
- command is specified
- required secrets are configured outside the repository
- base URL or service endpoint is configured when required
- there is a clear contract for what the integration returns

## Required Output

Return a concise readiness table with:

- `Integration`
- `State`
- `Missing pieces`
- `Next concrete step`

Use these states only:

- `Connected`
- `Declared but not configured`
- `Not present`

## Guardrails

- Do not call an integration "connected" unless it is actually callable.
- Do not treat placeholder values as configuration.
- Prefer unblocking one real connection over inventing a broad abstraction that nobody can run.
