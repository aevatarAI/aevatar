---
name: aevatar-agent-profile-management
description: Use when a caller needs to create or manage an owner Profile draft, exact skill bindings, or release readiness.
---

# Aevatar Agent Profile Management

Treat the owner Profile read model as the canonical management view. The caller owner is implicit. Every `agent_profiles` call uses `action` and `profile_slug`; pass the Profile slug, not an owner-qualified name.

Follow this sequence exactly:

1. Read the owner Profile and retain its strong ETag:

   ```json
   {
     "action": "get",
     "profile_slug": "<profile-slug>"
   }
   ```

   Preserve the returned ETag verbatim, including its surrounding quotes. In JSON arguments, escape those inner quotes exactly as shown below; the example versions are illustrative.

2. Search Ornn for a candidate with `ornn_search_skills`.
3. Inspect and retain the stable GUID, literal major.minor version, canonical name, and publisher id as `skill_guid`, `literal_version`, `expected_name`, and `expected_publisher_id`.
4. Call `agent_profiles` action `upsert_skill` with this exact argument shape. The `skill` object contains exactly the four inspected fields:

   ```json
   {
     "action": "upsert_skill",
     "profile_slug": "<profile-slug>",
     "etag": "\"agent-profile-v23\"",
     "binding_id": "<binding-id>",
     "activation_mode": "ROUTED",
     "skill": {
       "skill_guid": "<stable-guid>",
       "literal_version": "<literal-major.minor>",
       "expected_name": "<canonical-name>",
       "expected_publisher_id": "<publisher-id>"
     }
   }
   ```

5. Reread with the step 1 `get` shape and use the new ETag for every later mutation.
6. Call `agent_profiles` action `validate` for the complete draft. Validation takes no ETag:

   ```json
   {
     "action": "validate",
     "profile_slug": "<profile-slug>"
   }
   ```

7. Call `agent_profiles` action `publish` only when validation returns a valid report, using the latest reread strong ETag:

   ```json
   {
     "action": "publish",
     "profile_slug": "<profile-slug>",
     "etag": "\"agent-profile-v24\""
   }
   ```

8. Reread with the step 1 `get` shape until canonical state reconciles the accepted operation and digest.

The argument contract is closed. Do not use `operation`, `profile`, or `owner_profile`. Do not use `exact_ornn_skill_reference`; the exact reference belongs in `skill`. Do not invent `if_match`, `validation_id`, or other fields.

Never substitute a name-only reference or a `latest` version for the four exact facts. Never provide inline skill content, sealed content, credentials, another owner, or another scope. This tool cannot manage `system/*`, channel binding, or any authority outside the caller context.

A `202 Accepted` receipt means dispatch was accepted, not committed success. Report completion only after the canonical read model reconciles the accepted operation or digest.
