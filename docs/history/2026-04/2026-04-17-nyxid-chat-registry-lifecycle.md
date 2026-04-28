# 2026-04-17 NyxId Chat Registry Lifecycle

## Decision

NyxId chat conversation lifecycle depends on the registry command/admission ports
defined by [GAgent Registry Ownership](../../canon/gagent-registry-ownership.md)
being available.
The system does not support a degraded mode where a conversation can be created or
continued without being persisted in the registry.

## Required behavior

- `POST /api/scopes/{scopeId}/nyxid-chat/conversations` is fail-fast on registry persistence failure.
- Relay webhook conversation registration follows the same rule and must not silently continue when registry command/admission fails.
- Conversation deletion deletes chat history first and removes the registry entry second, so a history delete failure does not orphan history behind a missing registry entry.

## Rationale

The conversation list is registry-backed. Allowing create or relay to continue after
registry failure would leave the system in a mixed contract where some chat sessions
exist only in runtime state and cannot be explained or retried consistently from the
registry-backed UI path.
