# Scheduled Invocation Authorization

`ScheduledInvocationAuthorizationPlan` is the single contract shared by Team automation and
scheduled-agent authoring. It keeps the Aevatar invocation target separate from NyxID grants:

- `invocation_target` is a typed `oneof`: Studio keeps scope/team/member, workflow revision, and
  published service identities together; Lark keeps scheduled-agent, conversation, and skill
  identities together. Callers cannot populate both target kinds or flatten their identities.
- `nyx_id_service_grants` identifies exact NyxID `UserService` and primary/fallback node ids.
- `credential_policy` fixes `read proxy`, both allow-all flags to false, expiry, and policy version.
- `authority` records the authoritative read-model versions and catalog freshness evidence.
- `disclosure` carries stable product facts about dedicated credentials, secret custody, deletion,
  pause, resume, and browser exposure.

The catalog query port reads an owner-scoped current-state replica keyed by authority, owner kind,
and exact owner subject. A missing, stale, or owner-mismatched snapshot fails closed. Query and
preflight code never refreshes the projection, reads the event store, calls NyxID, or receives a
bearer. Authenticated host lifecycle code owns catalog refresh and invalidation; bearer material
exists only in that adapter call stack and is never written to an event, state, read model, key, or
digest.

Every planner request carries an `AuthenticatedNyxIdOwnerContext`. The owner descriptor is paired
with the authenticated surface subject and the verified binding that established it. A missing
binding, an incomplete subject, or organization authority that the adapter cannot prove fails
closed before catalog lookup or mutation.

The planner sorts service and node identities using ordinal comparison and hashes deterministic
Protobuf bytes with the digest field cleared. The digest covers invocation identity, owner,
service/node grants, policy, expiry, disclosure, and authority/freshness evidence. Any change to
those facts changes the digest.

Create and reauthorize always plan again from current read models. A digest mismatch returns the
typed `authorization_plan_changed` result before key, vault, or schedule side effects. On a match,
the credential issuer copies `allowed_service_ids` and `allowed_node_ids` directly from the plan;
the browser cannot submit either allowlist.

Team scheduling exposes three operations. `preflight` only resolves current read models and returns
the non-secret plan. `create` and `reauthorize` accept only the confirmed digest, re-resolve the
member target, and re-run the planner before schedule mutation. A workflow with no NyxID outbound
surface records the typed `service_grants_not_required` policy fact; an empty allowlist is never
interpreted implicitly.
