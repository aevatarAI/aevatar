---
title: "GAgent Registry Uses AgentKind As Business Key"
status: accepted
owner: eanzhao
supersedes:
  - "2026-06-04-agent-kind-primary-only-identity"
---

# GAgent Registry Uses AgentKind As Business Key

Date: 2026-06-04

Status: Accepted

## Context

GAgent draft-run and registry/admission paths still used CLR implementation type names as the business key for registry membership. That made local runtime shape part of the durable business fact and meant class rename, namespace move, or proxy implementation changes could change the registry/admission identity for the same GAgent kind.

The stable identity contract is canonical `AgentKind`. CLR type names are useful diagnostics, but they are not ownership facts.

## Decision

Registry state, registry commands/events, admission checks, draft-run targets, HTTP GAgent catalog endpoints, binding tools, Aevatar invocation tools, NyxID chat integration, streaming proxy integration, Studio projection, and console runtime APIs use canonical `AgentKind`/`agent_kind` as the sole GAgent business key.

`ImplementationClrTypeName` and `actorTypeName` may remain only as diagnostic display fields at explicit boundaries. New request/tool contracts reject identity aliases including `actorTypeName`, `gagentType`, `gagent_type`, and Aevatar invocation `actor_name`.

Legacy registry rows keyed by CLR type names are migrated by the registry authority only. `GAgentRegistryGAgent` probes the actor-owned kind contract, commits one `ActorRegistrationKeyCanonicalizedEvent` per actor when the canonical kind is known, and quarantines unmappable rows. No application, read model, or adapter may use CLR-name mapping fallback for admission.

The Studio-facing scope GAgent catalog route is `/api/scopes/gagent-types`, but the route returns a strongly typed `AgentKind` catalog. The route name is a UI capability surface, not permission to use CLR type names as registry identity.

## Consequences

- Registry/admission identity is stable across implementation class changes.
- Read models and frontend catalog state expose `agentKind`; diagnostic CLR type names are labeled as diagnostics.
- Tool schemas require `agent_kind` for GAgent identity while preserving `actor_id` direct addressing where supported.
- Proto evolution keeps tag compatibility by renaming fields in place and reserving old field names.
- `tools/ci/gagent_registry_kind_guard.sh` enforces the boundary against reintroducing legacy aliases.
