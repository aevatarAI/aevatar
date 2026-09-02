---
title: "Agent Profile Ornn Skillset Upgrade"
status: approved
owner: eanzhao
---

# Agent Profile Ornn Skillset Upgrade

## 1. Goal

Upgrade the production Ornn `aevatar-platform` skillset so an agent can reason
correctly about Aevatar Agent Profiles without claiming that an undeployed
capability is available.

The upgrade must:

- teach the real Agent Profile authority, publication, binding, and execution
  boundaries;
- route Agent Profile requests to a focused management skill instead of the
  workflow, team, member, service, or schedule lifecycle;
- detect the capability surface before attempting any Profile operation;
- work unchanged as the Phase 1 management contract becomes available;
- preserve exact Ornn versions and immutable skillset history; and
- avoid republishing unrelated members of the existing skillset.

## 2. Verified Baseline

### 2.1 Production Ornn state

The production Ornn API is reached through the authenticated NyxID service
`ornn-api`. The current skillset was read through:

```text
GET /api/v1/skillsets/aevatar-platform
GET /api/v1/skillsets/aevatar-platform/versions
GET /api/v1/skillsets/aevatar-platform/closure
```

The verified baseline on 2026-07-27 is:

| Fact | Value |
|---|---|
| Skillset | `aevatar-platform` |
| GUID | `248b99d6-36ff-4d41-bb45-baa25c6a9cad` |
| Current revision | `1.11` |
| Owner/publisher ID | `5d0d7b72-acff-49af-bb1b-9f30bbb7c102` |
| Visibility | `all-public` |
| Direct members | 12 |
| Last update | 2026-07-09 |

The exact current members are:

| Skill | Version | GUID |
|---|---:|---|
| `fallback-to-calling-agent` | `1.0` | `5f0fa2d8-55f2-4049-a1b0-f0722fcba7a2` |
| `aevatar-workflow-authoring` | `1.5` | `bdfb0ec1-41cc-4909-815a-eb1a12b7aa2e` |
| `aevatar-team-builder` | `1.3` | `6587bde4-6acc-4acb-8152-1dbbbe154e72` |
| `aevatar-scheduler` | `1.7` | `8d4bb4e0-81e8-472b-bd71-2777130aba2f` |
| `aevatar-service-publisher` | `1.5` | `b753047e-ad14-4d85-a12a-ca68534d9e20` |
| `aevatar-platform-map` | `1.7` | `b8bf9e98-2658-4e09-9c51-2e4958137091` |
| `aevatar-feasibility-advisor` | `1.1` | `d0619556-402e-4baf-aa26-fbfe78ac937c` |
| `aevatar-triage` | `1.3` | `fbd40315-317f-4f80-9885-b44b83e1a204` |
| `firecrawl-via-nyxid` | `1.1` | `47c28da0-430c-4cd8-adf2-4c5fbdf4e6c2` |
| `github-via-nyxid` | `1.0` | `abd23ac2-ed1c-4f8c-a6bc-6270390cbe32` |
| `aevatar-automation` | `1.1` | `1f8e2f07-67d3-4ac8-b7de-4596f36f4634` |
| `aevatar-channels-delivery` | `1.1` | `d2b575d3-0d80-4167-99e4-6161be47db7f` |

All 12 closure entries were direct members at `depth=0`. The current master
instructions and `aevatar-platform-map` present one dominant resource chain:

```text
scope -> team -> member (workflow | script | gagent)
      -> service -> schedule / external trigger
```

They contain no Agent Profile route. The map also says Ornn has no separate
collection object, although `aevatar-platform` is now a real, versioned Ornn
skillset with its own master instructions and immutable revisions.

### 2.2 Current production Aevatar state

The live Aevatar OpenAPI document is available at:

```text
GET /api/openapi.json
```

It currently exposes 265 paths and no path containing `agent-profiles`.
Authenticated probes of the intended discovery route also return HTTP 404.
Therefore production does not currently expose the Agent Profile management or
discovery contract.

The checked-in Mainnet defaults agree with the live result:

```json
{
  "AgentProfiles": {
    "NyxIdChat": {
      "Enabled": false,
      "ExternalReference": "nyxid-chat"
    }
  },
  "AgentProfileRollout": {
    "NyxIdChat": {
      "NewBindingsEnabled": false,
      "CohortBasisPoints": 0,
      "ReviewedProfilePath": ""
    }
  }
}
```

The current branch nevertheless contains an earlier deployment-owned
`AgentProfileSnapshot` runtime and rollout implementation for NyxID Chat. That
implementation is disabled by default. It must not be described as a
self-service Profile management API.

### 2.3 Phase 1 implementation state

The complete owner-managed Profile implementation exists on the local branch:

```text
feat/2026-07-22_agent-profile-phase-1
```

At the time of this design there is no matching open or closed GitHub PR. The
branch contains:

- `AgentProfileNamespaceGAgent` as the authority for human references and
  provisioning status;
- one `AgentProfileGAgent` per opaque Profile identity as the authority for the
  draft, exact bindings, mutation outcomes, and published snapshot;
- three actor-scoped current-state read models for namespace lookup, owner
  management, and protected execution;
- owner HTTP management and discovery endpoints;
- the `agent_profiles` in-session tool over the same Application contract;
- publish-side exact Ornn resolution and package sealing;
- a Mainnet read-model-backed binder for new NyxID direct conversations; and
- immutable conversation-owned execution bindings with fail-closed, per-turn
  routing and tool attenuation.

The skill upgrade may teach this complete contract, but it must gate every use
on the capability actually advertised by the running deployment.

## 3. Semantic Decision

Agent Profile is an independent product resource, not another phase or alias in
the Studio workflow lifecycle.

The platform has two orthogonal resource surfaces:

| Surface | Authority chain | Purpose |
|---|---|---|
| Build and operate | `scope -> team -> member -> published service -> schedule / external trigger` | Define and run business capabilities. |
| Agent Profile | `ownerHandle/profileSlug -> opaque profileId -> draft -> published snapshot` | Define an agent's purpose, instructions, exact skill routing, maximum tool policy, and recovery policy. |

The following identities remain separate:

- `profileId` identifies an Agent Profile.
- `workflowId` identifies a workflow draft or definition.
- `memberId` identifies a Studio team member authority.
- `publishedServiceId` identifies a callable service runtime.
- a conversation Actor id identifies one runtime conversation.

No string equality, prefix, route position, or lifecycle story may convert one
identity into another. Creating or publishing a Profile does not create a
workflow, member, team, service, schedule, or conversation binding.

## 4. Actual Agent Profile Contract

### 4.1 Authority and lifecycle

An ordinary Profile is owned by a typed authenticated user identity plus an
independent owning scope. A system Profile has typed system ownership. The
human reference carries `ownerHandle` and `profileSlug` as separate fields;
`profileId` is opaque and immutable.

The lifecycle is:

1. create a Profile and its initial draft;
2. read the owner management model and strong ETag;
3. replace the complete draft or upsert/remove exact skill bindings under
   optimistic concurrency;
4. validate the complete canonical draft;
5. publish a server-sealed snapshot; and
6. reread the management or discovery model until the accepted mutation is
   materially visible.

A `202 Accepted` receipt promises only `accepted for dispatch`. It does not
promise Actor commit, projection visibility, publication, or runtime binding.

### 4.2 Draft content

The complete draft contains:

- `displayName`;
- `purpose`;
- `instructions`;
- ordered skill bindings;
- a maximum Profile tool policy; and
- an optional explicit recovery tool policy.

The stable skill activation modes are:

| Mode | Meaning |
|---|---|
| `ALWAYS` | Its procedure joins every profiled prompt. It has no routing policy and cannot widen tools. |
| `ROUTED` | It participates in exact-alias and bounded classifier selection. It requires a typed routing policy. |
| `DEFAULT_FOR_UNMATCHED_TURN` | It is eligible only on a true no-match or when no routed candidates exist. It also requires a typed routing policy. At most one may be published. |

A routing policy contains:

- a stable intent ID;
- a routing description;
- globally unique explicit trigger aliases;
- a task tool policy; and
- a typed side-effect class: `READ_ONLY`, `EXTERNAL_HANDOFF`, `SERVICE_CALL`,
  or `MAINTENANCE`.

Tool policies are ceilings. A Profile, recovery policy, or selected skill may
only remove authority that the route and caller already possess. No Profile or
skill grants credentials, OAuth scopes, API keys, tools, or services.

### 4.3 Exact Ornn publication

Every Profile skill binding carries exactly:

- `skillGuid`;
- literal `<major>.<minor>` version;
- `expectedName`; and
- `expectedPublisherId`.

Name-only references, `latest`, dist-tags, inline skill bodies, inferred
publishers, or version ranges are invalid Profile authority.

Validation and publication read the exact Ornn detail and package endpoints,
verify all four identity facts, validate the package, and seal normalized
instructions, declared tools, supported assets, and deterministic digests into
the published snapshot. Ornn is a publish-side dependency, not an Actor or turn
dependency.

### 4.4 Runtime consumption

The converged Phase 1 runtime supports one consumer: newly created NyxID direct
conversations selected by a Host-owned rollout admission manifest.

The binder resolves `system/nyxid-chat` through the namespace read model, loads
the protected execution model once by opaque `profileId`, verifies the expected
revision, snapshot digest, exact closure, and admission pins, then carries a
complete immutable binding into the conversation creation command. The
conversation Actor persists that binding once.

Consequences:

- existing conversations are not hot-upgraded;
- publication alone does not bind a Profile to a conversation;
- arbitrary owner Profiles are not automatically selected by the system
  rollout;
- Workflow, Studio, relay, Channel, Scheduled, and AgentRun are currently not
  Profile execution consumers; and
- turns do not query Profile authority or fetch Ornn content at runtime.

Per-turn selection, prompt materialization, and tools can only preserve or
reduce the committed authority ceiling. Failures degrade to recovery or a
restricted-empty tool set; they never restore unrestricted legacy authority.

## 5. Capability Detection

Capability detection must distinguish “contract not deployed” from “specific
resource missing or invisible.” A 404 from a Profile resource route is not a
valid deployment probe.

### 5.1 In-session tool mode

Use the Profile tool path only when the exact `agent_profiles` tool is present
in the current tool surface. Do not infer it from another `aevatar_*` tool,
Ornn access, a system prompt, or a skill description.

If absent, report that the current Aevatar session does not expose Agent Profile
management. The agent may prepare a proposed Profile draft for later use, but
must not claim it created, validated, published, or bound anything.

### 5.2 Client REST mode

Read the live document:

```text
GET /api/openapi.json
```

The management capability is available only when the document advertises the
complete intended route family:

```text
POST   /api/scopes/{scopeId}/agent-profiles
GET    /api/scopes/{scopeId}/agent-profiles/{profileSlug}
PUT    /api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft
PUT    /api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft/skills/{bindingId}
DELETE /api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft/skills/{bindingId}
POST   /api/scopes/{scopeId}/agent-profiles/{profileSlug}:validate
POST   /api/scopes/{scopeId}/agent-profiles/{profileSlug}:publish
GET    /api/agent-profiles/{ownerHandle}/{profileSlug}
```

Require the full family so a partially deployed or mixed-version Host is not
treated as a safe mutation surface.

When absent, stop before any Profile mutation and report the unavailable
deployment capability. Do not fall back to workflow, team, service, or schedule
creation.

When present, a later 404 means only that the requested resource was not found
or is not visible to the caller. It no longer means that the contract is
undeployed.

## 6. Chosen Upgrade

Use a targeted semantic migration rather than republishing all existing
members.

### 6.1 New skill

Publish `aevatar-agent-profile-management@1.0`.

Its trigger surface covers:

- create or edit an Agent Profile;
- configure agent purpose or instructions;
- bind exact Ornn skills to a Profile;
- choose `ALWAYS`, `ROUTED`, or default activation;
- define Profile, recovery, or task tool ceilings;
- validate or publish a Profile; and
- explain or diagnose Profile ETag, validation, publication, or binding
  behavior.

Its workflow is:

1. identify in-session tool mode or client REST mode;
2. perform the appropriate capability detection;
3. stop honestly if the complete contract is absent;
4. read the current management state and ETag before versioned mutations;
5. obtain exact Ornn GUID, literal version, expected name, and expected
   publisher before upserting a skill;
6. execute one typed management action;
7. treat an accepted receipt as dispatch only; and
8. reread the management model until the expected committed mutation is
   visible, without inventing a synchronous completion guarantee.

The skill must state that publication is not runtime binding and that the
current runtime consumer boundary is limited to Host-selected, newly created
NyxID direct conversations.

### 6.2 Updated skills

Publish the following focused updates:

| Skill | From | Planned version | Change |
|---|---:|---:|---|
| `aevatar-platform-map` | `1.7` | `1.8` | Add the orthogonal Profile resource surface, capability detection, and focused routing; correct the obsolete “no collection object” statement. |
| `aevatar-feasibility-advisor` | `1.1` | `1.2` | Distinguish Profile management availability, publication, runtime consumption, and Host-controlled rollout. |
| `aevatar-triage` | `1.3` | `1.4` | Add deployment-surface detection and Profile-specific 404/412/422/503/202 diagnosis. |

The planned versions are package versions and must be rechecked against the
latest remote state immediately before each publish. If any latest version or
package hash has moved, stop that publication, rediff from the new exact
version, and choose the next valid literal version.

### 6.3 Unchanged skills

Do not republish these nine members:

- `fallback-to-calling-agent@1.0`;
- `aevatar-workflow-authoring@1.5`;
- `aevatar-team-builder@1.3`;
- `aevatar-scheduler@1.7`;
- `aevatar-service-publisher@1.5`;
- `firecrawl-via-nyxid@1.1`;
- `github-via-nyxid@1.0`;
- `aevatar-automation@1.1`; and
- `aevatar-channels-delivery@1.1`.

They are not Profile authorities or current Profile runtime consumers. Changing
them would create versions without behavioral value and could falsely imply
that Profile publication affects their workflows.

### 6.4 Skillset update

After all four skill versions are independently published and verified,
publish one new immutable `aevatar-platform` revision containing 13 exact
members.

Only these member references change relative to `1.11`:

- add `aevatar-agent-profile-management@1.0`;
- replace `aevatar-platform-map@1.7` with the verified new version;
- replace `aevatar-feasibility-advisor@1.1` with the verified new version; and
- replace `aevatar-triage@1.3` with the verified new version.

The master instructions must:

- present the build/operate and Agent Profile surfaces independently;
- route Profile management to the new focused skill;
- require capability detection before Profile operations;
- define honest behavior when the deployment lacks the contract;
- state that `202` is accepted-only;
- require exact Ornn identity and ETag-based mutations; and
- state the current runtime consumer boundary.

Ornn assigns skillset revisions automatically. `1.12` is the expected successor
to the verified `1.11` baseline, but the implementation must use the revision
returned by the server rather than asserting the number in advance.

## 7. Failure and Diagnostic Semantics

The management and triage skills use this interpretation after a successful
capability probe:

| Observation | Interpretation and action |
|---|---|
| OpenAPI lacks the complete route family | The deployment has not exposed the complete management contract. Stop before mutation. |
| `agent_profiles` tool absent | This session has no Profile management capability. Stop before mutation. |
| Profile route returns `404` | The requested Profile does not exist or is not visible. Do not reinterpret it as deployment absence. |
| Mutation returns `412` | The ETag is stale. Reread the full management model and reconstruct the intended complete mutation; do not blindly replay the old request. |
| Validation or publish returns `422` | Inspect typed diagnostics for draft shape, exact Ornn identity, routing policy, tool-policy subset, or package sealing failure. |
| Operation returns `503` | Exact Ornn resolution, ingress proof, Actor dispatch, or another declared dependency is unavailable. Report accepted/committed state honestly and retry only when safe. |
| Mutation returns `202` | Record operation and correlation facts, then reread. Do not claim commit, projection visibility, publication, or binding. |
| Published Profile is not used by a workflow/channel/schedule/existing chat | Expected current consumer boundary, not proof that publication failed. |

Triage must first establish which commit/image is deployed. Feature-branch
source cannot be used to claim that production exposes a route that its live
OpenAPI omits.

## 8. Test Design

Skill changes follow RED-GREEN-REFACTOR independently. Do not write all skills
and test them as one batch.

### 8.1 Baseline failures

Before writing or editing each skill, run fresh-context scenarios against the
current exact remote skill or against no focused Profile skill. Record the raw
answer and classify these failure patterns:

- routes Profile creation to workflow/team/service creation;
- treats `profileId`, `workflowId`, `memberId`, or `publishedServiceId` as
  interchangeable;
- calls Profile routes without checking the capability surface;
- uses name-only, `latest`, a dist-tag, or inline skill content as Profile
  publication authority;
- omits ETag/`If-Match` on versioned mutations;
- treats `202` as committed or published;
- promises an existing conversation, workflow, channel, AgentRun, or schedule
  will consume a Profile; or
- interprets every Profile 404 as the contract being undeployed.

### 8.2 Candidate tests

Run the same scenarios with each candidate skill. Required cases include:

1. Current production has no Profile paths in `/api/openapi.json`; the agent
   reports the unavailable capability and performs no Profile mutation.
2. The route family exists but one Profile read returns 404; the agent reports
   missing/invisible resource rather than undeployed capability.
3. A user asks to create a Profile and bind an exact routed skill; the agent
   requests or resolves all four exact Ornn identity facts and includes a typed
   routing policy.
4. A user asks to bind a Profile to an existing schedule; the agent states that
   Scheduled is not a current Profile consumer and does not mutate the schedule
   or pretend publication supplies a binding.
5. A publish returns `202`; the agent reports accepted-only and names the read
   evidence required before claiming publication.
6. A mutation returns `412`; the agent rereads before reconstructing the
   mutation.
7. A Profile has an `ALWAYS` member; the agent does not attach a routing policy.
8. A Profile has a `ROUTED` or default member; the agent supplies the complete
   typed routing policy and respects policy subsets.

The focused management skill receives the full application and variation
suite. Router, feasibility, and triage receive narrower tests matched to their
responsibilities.

### 8.3 Package validation

Before every remote skill write:

- verify the candidate frontmatter and package layout locally;
- preserve category, tags, and all unchanged assets from the exact source
  package;
- run Ornn `POST /api/v1/skill-format/validate` on the exact candidate ZIP;
- verify no credential, bearer, local cache, or temporary response has entered
  the package; and
- reread the current remote GUID, latest version, publisher, and package hash to
  detect concurrent changes.

After every skill write, read the new exact detail and exact JSON package by
GUID plus literal version. Verify name, publisher, version, public visibility,
file set, body, and server-reported skill hash before proceeding to another
skill.

## 9. Deployment Sequence

Use this order so the current skillset remains valid throughout:

1. Baseline-test, author, validate, publish, make public, and exact-read
   `aevatar-agent-profile-management`.
2. Baseline-test, update, validate, publish, and exact-read
   `aevatar-platform-map`.
3. Baseline-test, update, validate, publish, and exact-read
   `aevatar-feasibility-advisor`.
4. Baseline-test, update, validate, publish, and exact-read `aevatar-triage`.
5. Reread `aevatar-platform` and require the same GUID, owner, and expected
   predecessor revision before composing the new member list.
6. Publish one new skillset revision with all 13 exact member references and the
   complete master instructions.
7. Exact-read the returned skillset revision and its closure.

Do not update the skillset between member publications. Until step 6 succeeds,
`aevatar-platform@1.11` continues to point only at its existing verified
members.

## 10. Acceptance

The final exact skillset read and closure must prove:

- the returned skillset GUID is
  `248b99d6-36ff-4d41-bb45-baa25c6a9cad`;
- the owner remains `5d0d7b72-acff-49af-bb1b-9f30bbb7c102`;
- the revision is the exact server-returned new revision;
- there are exactly 13 direct members and every direct member has `depth=0`;
- only the three planned existing members changed versions;
- the new management member has the exact verified GUID, version, publisher,
  and hash;
- the other nine exact member references are unchanged;
- `memberVisibilityState` is `all-public`;
- `unreadableMembers` is empty;
- the master instructions contain the two resource surfaces, capability
  detection, honest unavailable behavior, accepted-only ACK semantics, exact
  Ornn identity, ETag mutation semantics, and current runtime consumer limit;
  and
- forward tests using the new exact skillset follow those semantics.

The live production test is expected to discover no Agent Profile paths and
stop without mutation. That honest result is acceptance evidence, not a failed
test.

## 11. Rollback

Ornn versions and skillset revisions are immutable. Rollback never deletes or
rewrites history.

- If one skill is wrong, publish a corrected next version and verify it.
- If the new skillset composition is wrong, publish a corrective skillset
  revision that restores the exact 12 members and master instructions from
  `aevatar-platform@1.11`.
- Do not delete the newly published Profile skill merely to remove it from the
  active collection.
- Do not mutate or delete prior skillset revisions.

## 12. Evidence and Repository Hygiene

The implementation report records:

- each previous and new exact skill version;
- each skill GUID, publisher, and server-reported hash;
- the returned skillset revision;
- exact closure verification;
- baseline and candidate forward-test outcomes; and
- the production capability probe result.

Do not commit or retain in the repository:

- NyxID or Ornn tokens;
- CLI receipt or session files;
- downloaded response dumps;
- generated ZIP packages;
- temporary candidate directories; or
- unrelated changes from the existing dirty worktree.

## 13. Non-Goals

- Merging or deploying the Agent Profile Phase 1 branch.
- Enabling the Mainnet Profile rollout.
- Creating or publishing a user Profile as part of this skillset update.
- Adding a Profile UI.
- Binding Profiles to Workflow, Studio, relay, Channel, Scheduled, AgentRun,
  arbitrary services, or existing conversations.
- Rewriting all Aevatar skills for stylistic consistency.
- Changing Aevatar, NyxID, or Ornn source contracts.
- Treating the dynamic `aevatar-platform` skillset as the exact trust closure of
  a published Agent Profile; Profile publication still uses exact individual
  skill references and server-side sealing.
