---
name: aevatar-agent-profile-management
description: Use when a caller needs to create or manage an owner Profile draft, exact skill bindings, or release readiness.
---

# Aevatar Agent Profile Management

Treat the owner Profile read model as the canonical management view. Follow this sequence exactly:

1. Read the owner Profile with `agent_profiles` action `get`, and retain its strong ETag.
2. Search Ornn for a candidate with `ornn_search_skills`.
3. Inspect and retain the stable GUID, literal major.minor version, canonical name, and publisher id as `skill_guid`, `literal_version`, `expected_name`, and `expected_publisher_id`.
4. Call `agent_profiles` action `upsert_skill` with only that `ExactOrnnSkillReference` and the retained ETag.
5. Reread the owner Profile and use the new ETag for every later mutation.
6. Call `agent_profiles` action `validate` for the complete draft.
7. Call `agent_profiles` action `publish` only when validation returns a valid report, using the latest reread ETag.
8. Reread until canonical state reconciles the accepted operation and digest.

Never substitute a name-only reference or a `latest` version for the four exact facts. Never provide inline skill content, sealed content, credentials, another owner, or another scope. This tool cannot manage `system/*`, channel binding, or any authority outside the caller context.

A `202 Accepted` receipt means dispatch was accepted, not committed success. Report completion only after the canonical read model reconciles the accepted operation or digest.
