---
title: "Ornn Aevatar Platform Scheduled Agent Key Skill Evolution"
status: "Approved on 2026-07-27"
owner: eanzhao
---

# Ornn Aevatar Platform Scheduled Agent Key Skill Evolution

## Context

The public Ornn skillset `aevatar-platform` is the entry point for Aevatar
workflow, Team, service, schedule, automation, channel, and diagnostic skills.
Its current immutable revision is `1.11`, with stable GUID
`248b99d6-36ff-4d41-bb45-baa25c6a9cad`.

The skillset currently mixes three different scheduled-resource models:

1. canonical Studio Team member automation;
2. an independently created scheduled Ornn skill agent;
3. the generic platform `/api/schedules` resource.

That conflation produces a material credential error. Several member skills
state that every scheduled run exchanges a NyxID binding for one fixed
300-second broker token at fire time and shares that token for the whole run.
That remains a possible credential model for a generic schedule using a NyxID
binding source, but it is not the current implementation for either canonical
Team member automation or `scheduled_agent_creator`.

Both Agent Key paths now provision a dedicated, constrained NyxID Agent Key,
store its raw value only in `ISecretVault`, persist only a typed reference, and
late-resolve that reference when workflow execution needs the caller
credential. Consequently, the existing global five-minute diagnosis can cause
an agent to choose the wrong API, misstate credential custody, recommend an
unnecessary workflow split, or miss a real expiry, revocation, authorization,
or Vault failure.

This change evolves the affected Ornn skills to match the deployed Aevatar
contracts. It does not change Aevatar runtime code, add a scheduling runtime,
publish the separate Agent Key canary, or create production schedules.

## Verified Sources

The design uses current implementation and observable contracts rather than
skill prose as authority.

### Aevatar schedule and credential authority

- `docs/canon/scheduled-skill-runners.md` defines canonical Team member
  automation ownership, generic-schedule isolation, accepted-receipt honesty,
  lifecycle states, credential lifecycle, and authorization facts.
- `docs/adr/0037-scheduled-invocation-credential-source-model.md` defines the
  typed credential-source model and the no-raw-token invariant.
- `docs/adr/0041-scheduled-invocation-agent-key-credential-reference.md`
  defines `scheduled_invocation_agent_key` as a trusted, Vault-backed typed
  source.
- `docs/adr/0043-scheduled-credential-lifecycle-compensation.md` defines
  actor-owned, dual-track NyxID/Vault compensation and revocation.
- `src/platform/Aevatar.GAgentService.Core/Schedules/scheduled_dispatch_state.proto`
  persists the typed Agent Key reference, active/candidate/pending-revocation
  generations, authorization fact, lifecycle status, and revocation tracks.
- `src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs`
  owns Team automation preflight, write-side revalidation, credential
  generation, activation, reauthorization, update, run-now, pause/resume,
  delete, and revocation orchestration.
- `agents/Aevatar.GAgents.Scheduled/StudioScheduledCredentialMaterializer.cs`
  creates a deterministic per-automation credential effect, issues the key,
  stores the raw value in the Vault, and revokes NyxID and Vault tracks.
- `agents/Aevatar.GAgents.Scheduled/Authoring/ScheduledAgentApiKeyIssuer.cs`
  revalidates the exact authorization plan, requests a targeted scope plan,
  fixes both wildcard flags to `false`, and creates the NyxID key from exact
  service and node grants.
- `agents/Aevatar.GAgents.Scheduled/Authoring/ScheduledAgentCreatorTool.cs`
  proves `scheduled_agent_creator` remains a supported but distinct resource
  path and also provisions a constrained Agent Key.
- `src/platform/Aevatar.GAgentService.Infrastructure/Schedules/ScheduledServiceInvocationDispatchPort.cs`
  validates the committed authorization fact and projects a borrowed durable
  credential reference into the workflow caller context.
- `src/workflow/Aevatar.Workflow.Core/Execution/WorkflowRunExecutionContextStateAccess.cs`
  resolves a durable caller reference through `ISecretVault` each time a
  caller credential is requested.
- `test/Aevatar.Workflow.Core.Tests/Execution/WorkflowExecutionRuntimeContextTests.cs`
  explicitly proves a borrowed scheduled Agent Key is late-resolved on every
  call and can observe rotated material after six minutes.
- `src/Aevatar.Studio.Hosting/Endpoints/StudioMemberAutomationEndpoints.cs`
  defines the canonical Team automation HTTP surface.
- `src/Aevatar.AI.ToolProviders.StudioProvisioning/ScheduleStudioMemberWorkflowTool.cs`
  defines the canonical in-session creation tool and its server-derived
  identities.
- `src/Aevatar.AI.ToolProviders.StudioProvisioning/StudioQueryTools.cs` defines
  the owner-scoped schedule list and detail tools.

### Ornn remote state

Authenticated, read-only NyxID proxy calls established the current remote
state:

- skillset name: `aevatar-platform`;
- skillset GUID: `248b99d6-36ff-4d41-bb45-baa25c6a9cad`;
- current revision: `1.11`;
- current member count: `12`;
- current visibility: `all-public`;
- Ornn UserService ID used for reads:
  `919208f4-d5f3-4840-8eba-8643820ce7f2`;
- Ornn catalog service ID, deliberately not interchangeable with the
  UserService ID: `27dc402b-c7f1-4db3-a840-e13b7956b6a7`.

The stable affected skills are:

| Skill | GUID | Current | Target |
| --- | --- | ---: | ---: |
| `aevatar-scheduler` | `8d4bb4e0-81e8-472b-bd71-2777130aba2f` | `1.7` | `1.8` |
| `aevatar-automation` | `1f8e2f07-67d3-4ac8-b7de-4596f36f4634` | `1.1` | `1.2` |
| `aevatar-channels-delivery` | `d2b575d3-0d80-4167-99e4-6161be47db7f` | `1.1` | `1.2` |
| `aevatar-triage` | `fbd40315-317f-4f80-9885-b44b83e1a204` | `1.3` | `1.4` |
| `aevatar-platform-map` | `b8bf9e98-2658-4e09-9c51-2e4958137091` | `1.7` | `1.8` |

Remote fixed-version package downloads were used to read the exact current
`SKILL.md` bodies. The current `aevatar-platform` master prompt and affected
skills contain the stale global 300-second scheduled-run model.

## Semantic Mismatch

The current skills imply:

> A scheduled run always mints one 300-second broker token at fire time and
> shares it across the run.

The current implementation says:

> Credential behavior depends on the schedule resource and its typed source.
> Canonical Team automation and scheduled skill agents use a persistent,
> dedicated, constrained Agent Key whose raw value remains in the Vault and is
> late-resolved when workflow execution needs it. A NyxID-binding schedule may
> instead exchange the binding for a short-lived bearer.

This is simultaneously a contract, runtime, ownership, and mental-model
mismatch. Fixing only the displayed TTL would leave the wrong resource routing
and lifecycle guidance in place.

## Goal

Make every entry into the `aevatar-platform` skillset choose the correct
scheduled resource, describe its real credential lifecycle, and diagnose
failures from typed evidence.

After the change, an agent must:

- choose canonical member automation for an already-bound Studio member;
- retain `scheduled_agent_creator` for independent Ornn skill agents and
  one-shot reminders;
- reserve generic `/api/schedules` for platform-level service/envelope
  scheduling;
- keep `memberId`, draft `workflowId`, and `publishedServiceId` distinct;
- understand dedicated Agent Key provisioning, Vault custody, late
  resolution, generations, and dual-track revocation;
- treat `202 Accepted` as admission rather than completion;
- diagnose `token_expired` only after identifying the actual credential
  source;
- never expose secret material.

## Non-Goals

- Do not modify Aevatar, NyxID, or Ornn runtime code.
- Do not change schedule, Agent Key, authorization, Vault, projection, or
  identity contracts.
- Do not add `aevatar-scheduled-agent-key-canary` to this skillset.
- Do not create or mutate a production Team, member, workflow, schedule, run,
  Agent Key, or Vault secret.
- Do not update unrelated Ornn skills merely because they mention scheduling.
- Do not redesign the twelve-member skillset or replace it with a new one.
- Do not deprecate `scheduled_agent_creator`; it remains a supported resource
  with different ownership and management semantics.

## Approaches Considered

### Selected: in-place semantic migration

Publish new immutable versions for the five affected skills, then publish a
new revision of the existing skillset with updated pins and master prompt.

This is selected because every common entry point becomes correct while
stable GUIDs, discovery names, visibility, unrelated members, and old versions
remain intact.

### New shared Agent Key reference skill

Add one new `aevatar-scheduled-agent-key` member and ask existing skills to
load it.

This would reduce repeated reference prose, but a model may act on an existing
skill's frontmatter or inline diagnosis without loading the extra member. It
would also leave stale trigger descriptions active until every caller follows
the reference.

### Rewrite the entire skill family

Replace all twelve member skills and the master prompt together.

This would maximize editorial consistency but needlessly retest workflow,
Team, service, connector, and fallback behavior that has no Agent Key drift.
It enlarges the failure and rollback surface without adding user value.

## Resource Decision Contract

The five skills and the skillset master prompt must share this decision table:

| User intent | Resource owner | Canonical entry |
| --- | --- | --- |
| Schedule an already-bound Studio member workflow | `ScheduledDispatchGAgent` under `scope -> team -> member` ownership | `aevatar_schedule_member_workflow` or `/api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations` |
| Manage that member automation | Exact Team/member owner tuple | Canonical list/detail/update/pause/resume/run-now/reauthorize/delete routes |
| Create an independent scheduled Ornn skill agent | Scheduled agent/catalog actors | `scheduled_agent_creator`, then `agent_builder` |
| Create a delayed one-shot reminder | Scheduled agent path | `scheduled_agent_creator` with `one_shot` |
| Directly schedule a raw service invocation or envelope | Generic schedule actor | Generic `/api/schedules` |

No entry may use route position, prefixes, equality, or a familiar string to
convert between `memberId`, draft `workflowId`, `publishedServiceId`,
UserService ID, catalog service ID, schedule ID, or Agent Key ID.

## Canonical Team Automation Contract

### Identity and target resolution

The canonical owner is the exact `scopeId + teamId + memberId` resource path.
`teamId` is a containment guard, and the durable schedule owner is the typed
Team member owner. The server reads the member summary and derives
`publishedServiceId`. The caller cannot substitute a draft `workflowId`,
service ID, grant ID, binding ID, route, model, or credential ID.

Skill examples must use visibly distinct fixtures, for example:

```text
memberId = m-alpha
draftWorkflowId = wf-alpha
publishedServiceId = svc-alpha
```

### Preflight

Preflight builds a typed authorization plan from current facts:

- the exact member and containing Team;
- the already-bound workflow revision and prepared artifact;
- typed connector and NyxID capability refs;
- the owner LLM route, model, and exact UserService ID when required;
- the owner-scoped NyxID authorization catalog;
- policy, source versions, expiry, disclosures, and permission digest.

Preflight is read-only. It does not provision a key. A client must never
invent grants, wildcard settings, node IDs, route/model choices, or caller
binding evidence.

### Create

Create uses:

```text
credentialProvisioningKind = dedicated_scheduled_invocation_agent_key
```

The server revalidates the confirmed permission digest and policy version
against current sources before any key-creation effect. It commits the stable
operation identity, mutation digest, credential owner, deterministic key name,
and requested Vault reference before granting a fenced effect attempt.

The issuer requests a fresh, targeted NyxID scope plan for the exact sorted
UserService IDs. It validates owner, authenticated actor, service/node grants,
provider contract, policy, digest, and freshness. Key creation fixes:

```text
allow_all_services = false
allow_all_nodes = false
```

Only the targeted plan supplies allowed service IDs, node IDs, and the
provider scope-plan digest.

### Secret custody

The one-time raw key returned by NyxID is written only to `ISecretVault` with
purpose `scheduled.invocation-agent-key`. Durable state, committed events,
read models, logs, tools, and public APIs expose only stable non-secret facts,
including a typed reference, Agent Key ID, expiry, authorization fact, and
credential generation.

Skills must never ask the user to paste the Agent Key, accept it in schedule
JSON, print it, store it in an Ornn package, or reconstruct it from an ID.

### Admission and activation

`202 Accepted` and tool status `accepted` or `pending` mean command/effect
admission only. They do not prove:

- credential issuance completed;
- the Vault write completed;
- activation committed;
- the read model observed the new state;
- cron fired;
- the workflow succeeded.

After creation, clients must reread the canonical owner-scoped automation and
require a newer authoritative `stateVersion`. A ready automation normally
shows:

- `authorizationStatus == active`;
- `credentialSourceKind == scheduled_invocation_agent_key`;
- `enabled == true` for firing;
- a future `credentialExpiresAtUtc`;
- a positive `credentialGeneration`;
- `revocationPending == false`.

`active` describes credential health. `enabled` controls firing. They are
independent dimensions.

### Fire and workflow credential access

Cron and run-now use the same active credential generation. Run-now proves a
manual fire only; it does not prove cron-origin execution.

For a workflow Agent Key dispatch, Aevatar projects a borrowed
`DurableCallerCredentialRef` into the workflow caller context. The raw Agent
Key is not copied into workflow state. Each LLM, tool, or connector path that
requests the caller credential resolves the reference through `ISecretVault`
at that time. Resolution is fail-closed for an absent, expired, revoked,
mismatched, or malformed reference.

Therefore, these Agent Key paths must not be described as one 300-second
broker token minted at fire and shared for the whole run. A generic schedule
using a NyxID binding remains a different source and may exchange that binding
for a short-lived bearer.

### Pause and resume

Pause disables future firing but preserves the active credential. Resume
re-enables firing if the credential remains usable. Neither action is a
credential revoke or reauthorization operation.

### Update

Cron, timezone, prompt, display name, and enabled changes revalidate current
authorization facts. An update does not silently expand key grants or replace
the active credential. Authorization drift produces an explicit
reauthorization requirement.

### Reauthorize

Reauthorization begins from a new preflight and confirmation. It provisions a
new dedicated key generation from the newly validated exact plan. Only after
the replacement generation is committed can the old generation be revoked.
The system must not mutate an existing key to widen its authority.

### Delete and compensation

Delete commits the tombstone and revocation intent before external cleanup.
NyxID key revocation and Vault secret revocation are independent durable
tracks. A row remains visible as `deleting` or `revocation_pending` while
either required track is incomplete.

Retry uses the original delete operation and idempotency identity plus fresh
owner authority. It does not synthesize a new delete, reauthorization, or
credential. Only completion of all required tracks permits the deleted
automation to disappear.

### Expiry and drift

An expired, missing, revoked, unresolvable, or authorization-mismatched key
fails closed. The owner-scoped automation moves toward `needs_authorization`
and future fire leases are canceled. It must not fall back to an interactive
user bearer, Host default, inferred binding, inferred service, or wildcard
grant.

## Scheduled Skill Agent Contract

`scheduled_agent_creator` remains valid for an independent Ornn skill agent
or one-shot reminder. It is not an alias for Team member automation.

The tool:

- derives its caller and ownership from trusted context;
- plans and revalidates typed authorization;
- provisions a constrained Agent Key;
- stores raw material in the Vault;
- persists a typed reference;
- returns an accepted creation receipt;
- delegates later management to `agent_builder`.

The current request uses exact typed service requirements:

```text
required_nyx_services[] = {
  user_service_id,
  service_slug_snapshot
}
```

The removed `required_service_slugs` contract must not be recommended. A slug
snapshot is an integrity/display value and cannot substitute for the exact
UserService ID. `nyx_user_service_id` identifies the exact outbound provider
when applicable.

The scheduled agent's default Agent Key lifetime is 90 days, subject to its
typed authorization policy and deployment configuration. Skills must describe
the projected expiry rather than promise an eternal key.

## Generic Schedule Contract

Generic `/api/schedules` is a platform-level resource for a raw service
invocation or actor envelope. It is not the canonical API for Team member
automation and must not be used as a fallback when the owner is a Team member.

Its typed auth source may differ. If it uses a NyxID binding source, Aevatar
may exchange that binding for a short-lived bearer. TTL guidance is valid only
after the source and token class are identified. The generic source's behavior
must not be generalized to dedicated Agent Key resources.

## Shared Diagnostic Contract

### Identify the source before interpreting a 401

The skills must first determine the resource and typed credential source.
Evidence may include:

- canonical path and owner tuple;
- `credentialSourceKind`;
- `authorizationStatus`;
- `credentialExpiresAtUtc`;
- `credentialGeneration`;
- `stateVersion`;
- `lastAuthorizationErrorCode`;
- `revocationPending`;
- `nyxIdRevocationStatus`;
- `vaultRevocationStatus`;
- exact run/tool failure and timestamp.

For `scheduled_invocation_agent_key`, inspect key expiry, Vault resolution,
reference integrity, committed caller authority, authorization fact, exact
service/node grants, generation, and revocation state. Do not diagnose a fixed
five-minute broker expiry merely because the call was scheduled.

For a NyxID binding-exchange source, identify the actual exchanged token and
then use its verified `iat`/`exp` or the current provider contract. Do not apply
one token class's lifetime to another.

### Stable recovery behavior

- Lost create response: reread by exact owner and original operation identity;
  do not create a second schedule with new identities.
- `authorization_plan_changed`: rerun preflight before retrying.
- `needs_authorization`: run a new preflight and explicit reauthorize.
- `revocation_pending`: retry revocation with the original delete operation
  identity.
- Missing owner binding or authorization catalog evidence: fail closed and
  report the prerequisite; do not invent evidence or trigger projection repair.
- Projection pending: report eventual visibility and required version; do not
  replay or prime projection from the query path.

### Secret-output policy

No skill, test artifact, package, log excerpt, or final response may contain:

- raw Agent Key;
- bearer, access, refresh, delegation, or service-account token;
- Vault reference or ciphertext;
- permission digest;
- unfiltered API-key inventory;
- authorization headers.

Stable resource IDs may be reported when needed for management or cleanup,
but they must never be presented as secret material or used to derive another
identity.

## Skill Changes

### `aevatar-scheduler` 1.8

Make this the routing and operating guide for Aevatar scheduling rather than
a generic `/api/schedules` recipe.

It must:

- classify the desired scheduled resource first;
- make canonical Team member automation the default for an already-bound
  member workflow;
- document both in-session tool and canonical REST paths;
- explain preflight, confirmed create, state reread, lifecycle management,
  reauthorization, and dual-track deletion;
- retain a clearly separated advanced generic-schedule section;
- link scheduled Ornn skill-agent intent to `aevatar-automation`;
- remove the global 300-second run limit and stale `scopeOwnerNyxId` Team
  automation path;
- keep cron preview guidance only where supported by the chosen API.

### `aevatar-automation` 1.2

Retain `scheduled_agent_creator`, one-shot reminders, skill reuse/authoring,
Lark negotiation, and `agent_builder` lifecycle.

It must:

- describe its dedicated constrained Agent Key and Vault custody;
- replace `required_service_slugs` with exact `required_nyx_services` refs;
- remove the claim that scheduled agents share a 300-second fire token;
- distinguish scheduled agents from Studio member automations;
- route an already-bound member request to `aevatar-scheduler`;
- treat accepted creation as pending and verify through `agent_builder` state;
- retain key revocation behavior on delete without exposing secret material.

### `aevatar-channels-delivery` 1.2

Keep channel registration, delivery targets, Lark semantics, and generic
capability-tool diagnosis.

It must replace the blanket five-minute scheduled-run diagnosis with a
source-first decision:

- Agent Key-backed run: inspect key/reference/fact/expiry/revocation;
- binding-exchange run: verify the actual short-lived token;
- interactive run: inspect its own token class;
- provider-specific failure: isolate it when sibling calls still succeed.

### `aevatar-triage` 1.4

Keep its three-layer Aevatar/NyxID/Ornn investigation model and code-grounded
issue policy.

It must:

- distinguish Team automation, scheduled agent, and generic schedule;
- use typed lifecycle and credential evidence;
- remove `token_expired at ~5 minutes` as a universal expected behavior;
- add Agent Key-specific failure and recovery branches;
- treat accepted receipts, committed state, read-model visibility, run
  completion, and external effects as separate evidence stages;
- avoid recommending projection repair as a normal schedule operation.

### `aevatar-platform-map` 1.8

Update the product map to represent ownership rather than a false linear
lifecycle:

```text
scope
  -> team
     -> member
        -> implementation surface (workflow/script/gagent)
        -> publishedServiceId
        -> automations
```

Independent scheduled agents and generic schedules remain sibling platform
resources, not aliases or stages of a member.

The router must choose `aevatar-scheduler` for scheduling, with the scheduler
then choosing the correct resource. It must remove the global five-minute
statement and update the golden path so publishing a member service does not
erase member ownership.

### `aevatar-platform` skillset 1.12

The existing skillset GUID and twelve-member composition remain stable. After
the five member skills are successfully published, update the master prompt
and pins:

```text
aevatar-scheduler@1.8
aevatar-automation@1.2
aevatar-channels-delivery@1.2
aevatar-triage@1.4
aevatar-platform-map@1.8
```

Keep these pins unchanged:

```text
fallback-to-calling-agent@1.0
aevatar-workflow-authoring@1.5
aevatar-team-builder@1.3
aevatar-service-publisher@1.5
aevatar-feasibility-advisor@1.1
firecrawl-via-nyxid@1.1
github-via-nyxid@1.0
```

The master prompt must carry the three-resource routing decision and no
longer describe `scheduled_agent_creator` or a 300-second broker credential as
the global scheduling model.

## Skills Deliberately Not Updated

- `aevatar-team-builder` mentions scheduling only as a next step and does not
  define Agent Key or TTL behavior.
- `aevatar-service-publisher` mentions scheduling only as a next step and
  keeps external-trigger API keys separate.
- `aevatar-workflow-authoring` only links to the scheduler and does not define
  credential behavior.

Their existing versions remain pinned.

## Skill TDD Validation

Skill changes follow RED-GREEN-REFACTOR per skill, never as one unverified
batch.

### RED baseline

Run realistic tasks against the fixed old version without exposing the
candidate guidance. Capture the output and exact incorrect behavior.

Required baselines:

1. Schedule an existing `m-alpha` member whose draft is `wf-alpha` and
   published service is `svc-alpha`.
   - Expected old failure: generic `/api/schedules`, identity conflation, or
     `scheduled_agent_creator` selection.
2. Interpret a `202 Accepted` member automation create.
   - Expected old failure: premature success without canonical reread.
3. Diagnose `token_expired` six minutes into an Agent Key-backed run.
   - Expected old failure: automatic 300-second broker-TTL attribution.
4. Add a new exact external service dependency to an existing automation.
   - Expected old failure: reuse or expand the old key instead of preflight +
     reauthorize.
5. Pause and resume.
   - Expected old failure: credential revocation or recreation claim.
6. Delete returns pending revocation.
   - Expected old failure: treat admission as cleanup completion or retry with
     a new operation.
7. Create an independent scheduled Ornn skill agent.
   - Expected old failure: use removed `required_service_slugs` or lose the
     supported `scheduled_agent_creator` path.

If a baseline does not exhibit the suspected failure, do not add guidance for
that failure based only on expectation. Reclassify the needed change from a
discipline correction to a positive output contract or omit it.

### GREEN candidate

Run the same tasks with the candidate skill. Success requires:

- correct resource selection;
- distinct identities;
- correct preflight/create/reread flow;
- correct Agent Key and Vault model;
- source-first token diagnosis;
- correct generation and revocation behavior;
- no secret output.

### REFACTOR

Inspect new mistakes and rationalizations. Tighten the decision table or
positive response shape without adding unrelated prose. Rerun until the same
scenarios converge.

Each skill is completed and deployed before moving to the next skill.

## Package and Publication Flow

### Local source recovery

Recover the current fixed remote package into the local Ornn repository under
`skills/<name>/`. These five new directories are the auditable source for the
candidate revisions. Preserve unrelated uncommitted files in that repository,
especially the existing `nyxid-service-*` work.

Verify before editing:

- stable GUID;
- exact old version;
- package file list;
- SHA-256 of downloaded bytes;
- current `SKILL.md` content.

### Per-skill gate

For each skill in this order:

1. run the old-version baseline;
2. edit only that candidate package;
3. verify frontmatter name, target version, description triggers, tags, and
   package structure;
4. scan for stale claims and sensitive material;
5. validate the package locally;
6. call Ornn live `/api/v1/skill-format/validate` through the exact connected
   `ornn-api` UserService;
7. forward-test the candidate in a fresh context;
8. update the stable remote GUID in place;
9. download the exact new version by GUID and version;
10. verify files, name, version, and SHA-256.

The proposed order is:

1. `aevatar-scheduler`;
2. `aevatar-automation`;
3. `aevatar-channels-delivery`;
4. `aevatar-triage`;
5. `aevatar-platform-map`.

Stop on the first failed gate. Do not update the skillset while a candidate is
unpublished or unverified.

### Stale-term scan

Candidate packages must not retain these statements as global truths:

- every scheduled run uses one 300-second token;
- every scheduled fire exchanges a binding;
- `required_service_slugs` is the current creation contract;
- `scopeOwnerNyxId` is the Team automation primary auth model;
- generic `/api/schedules` owns Team member automation;
- `202 Accepted` proves completion;
- pause revokes the credential;
- a key ID or slug can replace a UserService ID.

References to a 300-second token remain allowed only inside a clearly scoped,
verified binding-exchange discussion.

### Skillset gate

Only after all five skills pass:

1. publish a new immutable `aevatar-platform` revision with the new master
   prompt and exact pins;
2. reread exact detail and closure;
3. verify the stable skillset GUID;
4. verify the expected new revision;
5. verify all twelve members and five new pins;
6. verify no unexpected dependency or version conflict;
7. verify `memberVisibilityState == all-public`;
8. verify old revisions remain readable as rollback points.

## Security and Operational Boundaries

- Use NyxID CLI/proxy surfaces that do not print stored tokens.
- Never inspect or print CLI token files, browser storage, kubeconfig, Vault
  contents, raw Agent Key inventory, or authorization headers.
- Redact provider error bodies if they may echo credentials.
- Package validation and publication may mutate Ornn skills only after the
  written specification and implementation plan are approved.
- Production Aevatar and NyxID remain read-only during this change.
- Do not execute the scheduled Agent Key canary as part of this release.

## Failure and Rollback

Ornn skill versions and skillset revisions are immutable. If a member update
fails verification, stop before changing the skillset; `aevatar-platform@1.11`
continues pinning the old member.

If the final skillset revision is incorrect, publish a new revision that
restores the last verified pins and master prompt. Do not mutate or pretend to
erase immutable history.

An ambiguous update response is not success. Recover by exact stable GUID,
version list, package download, and hash before deciding whether to retry.
Never publish the same logical revision under a new skill name to hide an
uncertain update.

## Done Criteria

The evolution is complete only when:

- all five old-version baselines were recorded;
- all five candidate skills pass their corresponding scenarios;
- every package passes local and live Ornn format validation;
- every skill is updated under its existing GUID and downloaded back by exact
  new version;
- `aevatar-platform` publishes a new revision with the intended twelve-member
  closure and five exact new pins;
- the master prompt routes all three scheduled-resource models correctly;
- no affected skill retains the stale global 300-second model or removed
  service-slug-only contract;
- no raw credential, Vault reference, permission digest, or unfiltered key
  inventory appears in artifacts or output;
- unrelated Aevatar and Ornn worktree changes remain untouched;
- no production schedule, Agent Key, workflow run, or canary was created.
