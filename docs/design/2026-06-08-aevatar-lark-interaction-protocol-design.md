---
title: "Aevatar Lark Interaction Protocol Design"
status: active
owner: eanzhao
---

# Aevatar Lark Interaction Protocol Design

Workflow interaction requests use the Foundation-owned `InteractionSpec` proto as their stable contract. The contract is channel-neutral: it describes title/body text, actions, fields, cards, and delivery disposition without exposing Lark JSON, `MessageContent`, or composer-specific types to Workflow Core.

## Contract Flow

1. Workflow YAML may define `interaction_spec` at step root, under `presentation.interaction_spec`, or as inline `presentation` fields.
2. `WorkflowParser` lifts that structure into `StepPresentation.InteractionSpec` during parsing.
3. `WorkflowExecutionKernel` evaluates workflow expressions in visible text/value fields and writes the result to `StepRequestEvent.StepParameters.InteractionSpec`.
4. Channel boundary code maps `InteractionSpec` to existing `MessageContent` through `InteractionSpecMapper`.

## Boundary Rules

- `Aevatar.Workflow.Core` must not reference `MessageContent`, `LarkMessageComposer`, or raw Lark card JSON.
- Workflow step parameters remain for primitive configuration, not for stable interaction semantics.
- Channel adapters and composers continue to consume `MessageContent`; the only typed-contract bridge is `InteractionSpecMapper`.
