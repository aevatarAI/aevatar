---
title: Responses Agent Tool actor id scheme
status: accepted
owner: refactor/2026-05-18_readable-responses-actor-id
---

# Context

`ResponsesAgentToolStateGAgent` actor ids were built as `responses-agent-tools-` plus the first 128 bits of a SHA-256 hash over the normalized scope and owner subject.

That made the actor id stable, bounded, and safe for arbitrary identity-provider input, but it also made the id opaque. Operators could not identify the owning scope or subject from logs, projection documents, actor runtime tools, or forensic traces without recomputing the hash from candidate inputs. The hash was a one-way trapdoor we did not need because `ResponsesAgentToolStateRecord` already stores `scope_id` and `owner_subject` as authoritative audit fields.

# Decision

Introduce a feature-flagged readable actor id scheme:

```text
responses-agent-tools/scope:{percent_encoded_scope_id}/owner:{percent_encoded_owner_subject}
```

The feature flag is `FeatureFlags:AevatarResponsesAgentToolReadableIds`, surfaced as `ResponsesAgentToolStateIdOptions.AevatarResponsesAgentToolReadableIds`, and defaults to `false`. With the flag off, `BuildActorId(scopeId, ownerSubject)` continues to return the legacy SHA-256-based id byte-for-byte. With the flag on, it returns the structured readable id.

Scope and owner segments are percent-encoded using the RFC 3986 unreserved subset used by this actor id scheme: ASCII alphanumerics plus `-_.` are left as-is; all other UTF-8 bytes are encoded as `%HH`. Any id that fits within the 512-character cap can be decoded back to the original scope and owner values.

If the encoded id exceeds 512 characters, the longer encoded segment is truncated deterministically and receives a `~{16 lowercase hex chars}` SHA-256 tail derived from `scopeId + "|" + ownerSubject`. If both segments are too large for the cap, both are shortened and the longer original segment still receives the tail. The truncated id remains stable and bounded, but it is not intended to be fully decodable.

During rollout, readers try the readable id first and fall back to the legacy hash id. Writers with the flag enabled reuse an existing legacy actor when one exists, so tool state does not split between old and new actor ids during the dual-read window. The legacy hash fallback is temporary and should be removed after 30 days.

# Consequences

Readable ids improve operability: logs, projection documents, and actor runtime inspection now reveal the state owner without a side calculation.

The rollout remains reversible while the flag defaults to `false`, and the dual-read/write fallback allows deployments to enable the new scheme without losing access to legacy actor state.

The length cap creates an edge case for very long identity values. Those ids are deterministic and disambiguated by a short hash tail, but truncated segments cannot round-trip back to the full original values. The authoritative scope and owner remain available in `ResponsesAgentToolStateRecord`.
