---
title: "Scheduled Agent Key Runtime Integrity"
status: approved
owner: eanzhao
---

# Scheduled Agent Key Runtime Integrity

## Goal

Make the durable execution path authoritative end to end:

```text
scheduled task
  -> Vault Agent Key reference
  -> NyxID constrained key
  -> exact owner LLM UserService route and model
  -> workflow execution with verified caller binding
```

The key grant, runtime LLM target, and caller authority must be one integrity-bound
decision. A successful run must not depend on a matching host default.

## Verified Gaps

The reviewed branch correctly authorizes the exact owner `UserService.id`, stores only
an Agent Key reference, and persists verified caller authority for newly-created
automations. Three gaps remain:

1. Workflow Agent Key dispatch accepts a missing caller authority and can invoke without
   a verified binding.
2. The authorization plan grants the exact owner LLM service, but the scheduled
   `ChatRequestEvent` carries no route or model. Runtime therefore falls back to host
   defaults.
3. An absent typed UserConfig selection is manufactured into a compatibility Gateway
   route by some query/runtime consumers.

The mutation digest also changed from v1 to v2. A v1 pending operation cannot be
silently accepted as v2 because v1 did not bind caller authority.

## Semantic Decision

Owner LLM selection has exactly three states:

- `Unspecified`: no durable owner selection exists. Runtime may retain its existing
  caller/host default, but authorization code must not claim that Gateway was selected.
- `Gateway`: the owner explicitly selected the canonical Gateway route.
- `NyxIdUserService`: the owner explicitly selected an exact inventory-backed
  `UserService.id`, its canonical route, and its slug snapshot.

Only the latter two states are durable selections. An owner-LLM-dependent scheduled
workflow requires a valid durable selection and a non-empty canonical model. It fails
closed when the selection is unspecified or malformed.

## Chosen Architecture

### Plan-bound invocation selection

Add a typed `ScheduledInvocationOwnerLLMSelection` to the authorization plan with:

- route kind;
- canonical route value;
- exact NyxID UserService ID when the kind is `NyxIdUserService`;
- service slug snapshot when the kind is `NyxIdUserService`;
- canonical model.

The owner LLM query port builds this evidence only from the committed UserConfig read
model. It does not call NyxID, infer an ID from a slug, or manufacture Gateway for a
missing selection.

The planner validates the selection, adds the exact UserService to the required grant
set, stores the selection in the plan, and includes it in the existing protobuf-based
permission digest.

### Durable authorization fact

Copy the validated owner LLM selection into the persisted scheduled authorization fact.
The fact is the durable runtime contract; it is not reconstructed from a current
UserConfig query when the schedule fires.

The Studio schedule adapter derives `ChatRequestEvent.LlmControl` exclusively from the
validated plan/fact:

- `model_override` is the selected model;
- `nyx_id_route_preference` is the selected canonical route.

Create, reauthorize, and update all use a freshly validated plan. The payload never
reads the host default to fill these fields.

### Runtime cross-check

Before workflow invocation, the scheduled service dispatch port verifies that:

- a scheduled Agent Key workflow has a complete caller authority, including binding ID;
- the authorization fact has a valid owner LLM selection;
- the persisted chat payload route/model exactly match that selection;
- a `NyxIdUserService` selection references a service present in the authorization
  fact's exact service grants.

Any mismatch throws a typed scheduled authorization failure before invocation. The
existing schedule actor handling then moves a Team automation to `NeedsAuthorization`
and cancels future fire leases.

### Query semantics and canary evidence

Projection/query defaults preserve `LlmSelection = null` and an empty compatibility
route for a missing document. Runtime consumers branch on `LlmSelection.Kind`; they do
not promote `PreferredLlmRoute` into an explicit Gateway choice.

The scheduled current-state read model and Studio automation view expose the persisted
owner LLM route kind, route, exact UserService ID, and model. This read model exists for
one concrete consumer: Studio automation inspection and production acceptance. It is a
replica of the schedule actor's committed fact/payload, not a second calculation.

## Rejected Alternatives

### Query UserConfig when the schedule fires

Rejected because the current selection may differ from the grant used to provision the
Agent Key. It would reintroduce a query-time authorization decision and allow key/runtime
drift.

### Enrich only the chat payload after planning

Rejected because route/model would not be covered by the permission digest and could
change between planning and persistence.

### Accept v1 mutation digests as v2

Rejected because v1 did not bind caller authority. Treating it as compatible would allow
a different verified binding to claim the old operation.

## Rollout

The selected rollout strategy is an operational drain, not digest compatibility:

1. Before deployment, verify no Team automation is in `ProvisioningPending` or
   `ReplacementPending` under the v1 binary.
2. Verify no active Agent Key schedule lacks caller authority. Pause and reauthorize any
   such schedule before enabling fires on the new binary.
3. Deploy the updated plan, fact, state, projector, and reader together.
4. Reselect the owner's exact UserService so the typed UserConfig state is committed and
   projected.

The read-only production audit on 2026-07-23 found zero Team automations, so the current
rollout satisfies the drain requirement. The audit must be repeated immediately before
deployment.

## Production Acceptance

Use non-secret output only:

1. Verify NyxID `POST /api/v1/api-keys/scope-plan` returns the exact selected
   UserService, both mutation-revalidation declarations, and a present digest.
2. Reselect the production UserService by exact ID and model.
3. Observe typed UserConfig route kind, route, UserService ID, slug, and model.
4. Create distinct temporary Team, member, workflow, published service, and schedule
   identities.
5. Refresh the authorization catalog and verify both `allow_all_services` and
   `allow_all_nodes` are false.
6. Create the automation and observe:
   - credential source `scheduled_invocation_agent_key`;
   - persisted owner LLM route kind, route, exact UserService ID, and model;
   - complete caller authority with the verified binding ID.
7. Run now and verify a successful `simple_qa` completion.
8. Delete the automation and verify the exact NyxID Agent Key and Vault secret are both
   revoked.
9. Retire/delete/archive all temporary resources and verify the automation list is empty.

Never print bearer tokens, raw Agent Keys, refresh tokens, or Vault ciphertext.

## Test Contract

- Missing or incomplete caller authority fails before invocation and yields the stable
  caller-authority authorization code.
- Missing typed UserConfig remains `Unspecified` through query, settings view, and
  runtime consumers.
- Gateway and exact UserService selections round-trip through evidence, plan, digest,
  schedule fact, actor state, projection, and API view.
- Changing route, model, UserService ID, or slug changes the plan permission digest.
- A schedule whose selected service differs from the configured host default persists
  and invokes the selected route/model.
- Payload/fact route or model mismatch fails before invocation.
- Existing authorization, workflow binding, projection, stability, architecture, and
  documentation guards remain green.
