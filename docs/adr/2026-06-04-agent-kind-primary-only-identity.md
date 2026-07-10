---
title: "Agent Kind Primary-Only Identity"
status: accepted
owner: eanzhao
supersedes:
  - "0019-stable-agent-kind-identity"
---

# Agent Kind Primary-Only Identity

## Decision

Agent identity is primary-kind only. Runtime identity is read from
`RuntimeActorIdentity.Kind`; CLR type names are diagnostic metadata and are not
business identity, activation input, or recovery input.

Every concrete production `IAgent` implementation must declare one primary
`[GAgent("<module>.<entity>")]` kind and be registered through
`IAgentKindRegistry`. Legacy kind aliases and CLR-name identity resolution are
not part of the runtime contract.

Workflow roles are runtime targets for any registered primary GAgent kind.
Omitting `roles.agent_kind` defaults to `workflow.role-agent`; the LLM role
agent is the default implementation, not a privileged type.

## Consequences

- `AgentKindRegistry` resolves only primary `[GAgent]` declarations.
- `[LegacyAgentKind]`, `ILegacyAgentClrTypeResolver`, CLR-name activation, and
  `AgentTypeName` fallback are removed from identity flow.
- `RuntimeActorGrainState.AgentTypeName` may remain only as a reserved Orleans
  state slot and must not be read or written for activation.
- `[LegacyClrTypeName]` remains valid only for protobuf/payload codec
  compatibility at explicit adapter boundaries.
- Moving or renaming a class keeps the same primary kind. If the kind changes,
  that is an actor identity change and must follow the actor evolution canon.

## Validation

`tools/ci/agent_kind_naming_guard.sh` enforces primary kind token format,
concrete production agent decoration, and absence of removed legacy identity
symbols in production source.
