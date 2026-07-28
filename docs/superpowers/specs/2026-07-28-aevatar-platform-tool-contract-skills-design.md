---
title: "Ornn Aevatar Platform Tool Contract Skill Upgrade"
status: "Published and verified on 2026-07-28"
owner: eanzhao
---

# Ornn Aevatar Platform Tool Contract Skill Upgrade

## Goal

Audit the complete published `aevatar-platform` skillset against the current
merged Aevatar tool contracts, publish new immutable versions only for skills
that teach stale behavior, and publish one new revision of the existing
`aevatar-platform` skillset with an exact conflict-free closure.

The trigger is the changed `nyxid_proxy` execution contract, but the work is
not limited to that tool. The audit covers every public tool name, argument
schema, result/lifecycle promise, approval boundary, and resource identity used
by any of the 15 current member skills.

This is a skill-content and skillset-publication change. It does not modify
Aevatar, NyxID, Ornn, chrono-sandbox, or downstream service product code.

## Current Published Baseline

The live Ornn registry read on July 28, 2026 reports:

- skillset name: `aevatar-platform`;
- GUID: `248b99d6-36ff-4d41-bb45-baa25c6a9cad`;
- current and latest version: `1.13`;
- visibility: `all-public`;
- root and unique closure member count: 15.

The exact 1.13 closure is:

| Skill | Version |
| --- | ---: |
| `fallback-to-calling-agent` | 1.0 |
| `aevatar-workflow-authoring` | 1.5 |
| `aevatar-team-builder` | 1.3 |
| `aevatar-scheduler` | 1.8 |
| `aevatar-service-publisher` | 1.5 |
| `aevatar-platform-map` | 1.9 |
| `aevatar-agent-profile-management` | 1.0 |
| `aevatar-feasibility-advisor` | 1.3 |
| `aevatar-triage` | 1.5 |
| `firecrawl-via-nyxid` | 1.1 |
| `github-via-nyxid` | 1.0 |
| `aevatar-automation` | 1.2 |
| `aevatar-channels-delivery` | 1.2 |
| `aevatar-codex-exec-workflow-sample` | 3.0 |
| `aevatar-codex-exec-node-setup` | 4.0 |

Every audit starts from independently downloaded exact-version JSON and ZIP
artifacts whose SHA-256 matches the closure `skillHash`. Temporary files from
the previous release may accelerate discovery only after their hashes match the
live immutable registry; they are not authority by path or recency.

## Verified Product Sources

The published 1.13 Codex upgrade recorded Aevatar commit
`aba74805c6b40f3848a554b85e4192e7c06abfa2` as its product-contract
baseline. Concurrent members in 1.13 remain authoritative by their published
packages, not by assuming that one source commit describes all 15 skills.

The design audit inspected the merged local `origin/feature/integrate` ref at
`14aca8177d5b332436b0dc7451c70df0fe67c5e4`. Relevant merged changes
after the recorded baseline include:

- `13c7944a8161d5d0da2639ab45aa70e2d63feebf`: request-time,
  list-only channel connected-service inventory;
- `2d25508532a8a06e1c4a2f419bddd60148a03ac2` and follow-up
  `7713578bdc38c31c98233466f128b8a95f6b665c`: member invocation and
  default chat endpoint;
- `b0eb7c43c911787a8b6ffb9bba70f982c4d711bf` and rollout follow-up
  `7b316e93943d6896c9915552094f09c4f2005f01`: exact NyxID operation
  admission proof, readiness selector, and proof-bound proxy execution;
- `7b438a75940a3c78ad776b8c3e13cf1c44c6ec98` through
  `807882018b9875489e34401bee67de061f22037e`: managed Codex readiness,
  read-only execution, timeout budgets, and recovery operations.

GitHub remote refresh initially failed twice during design discovery with
independent TLS handshake errors. The release later refreshed successfully over
GitHub SSH port 443 immediately before the skillset mutation. The final merged
source authority was
`de38876690952257cc3c992615de1f643d8b285c`.

The late delta from `14aca8177d5b332436b0dc7451c70df0fe67c5e4`
to that revision added draft workflow validation detail, observatory UI
stability, OAuth client projection recovery, and committed-fact maintenance.
It did not change any audited tool name or argument schema. It did add
`externalCapabilityReadiness` to applicable `INVALID_WORKFLOW_YAML` responses.
A fresh-context test using only `aevatar-workflow-authoring@2.1` correctly
treated its typed blocker/remediation as authoritative and selected rebind
instead of a blind YAML retry, so no additional 2.2 release was justified.

Unmerged worktrees and staged local experiments are explicitly excluded. A
contract enters a public skill only after it is present in the refreshed merged
source chosen for the release.

## Selected Approach

Perform a closure-wide contract audit, then migrate only affected skills in
place. Keep every existing skill GUID and name, publish exact next immutable
versions, and update the existing skillset only after all changed members have
passed independent readback.

This approach was selected over two alternatives:

1. Updating only skills that contain the literal string `nyxid_proxy` would
   miss readiness, connected-service operation, member invocation, and managed
   Codex lifecycle changes.
2. Rewriting all 15 skills would create version noise and retest unrelated
   behavior without evidence of drift.

The unit of change is one proven stale skill, not one source commit and not the
whole family.

## Tool Contract Classification

Every product delta is classified before a skill changes:

| Class | Meaning | Skill action |
| --- | --- | --- |
| Call shape | Tool name, required field, allowed field, enum, or caller-visible dynamic schema changed | Update every skill that teaches or emits that call. |
| Result or lifecycle | The same call now proves a different stage, returns new typed readiness, or has different timeout/recovery semantics | Update skills that make decisions or diagnoses from the result. |
| Routing or identity | A new canonical tool exists or an identity must be resolved from a different contract | Update the semantic router and the resource-owning skill. |
| Internal implementation | Credential plumbing, owner-scope fallback, storage, or refactoring changed without altering caller behavior | Keep the skill version unless a RED scenario proves user-visible drift. |

Descriptions and schemas embedded in the live request tool catalog remain the
immediate invocation authority. A skill explains how to choose and compose
those tools; it must not override a tool schema from memory.

## Authoritative NyxID Invocation Modes

The phrase “use `nyxid_proxy`” now spans distinct contexts. Skills must first
identify the actual exposed tool and schema and then follow exactly one row:

| Context | Tool surface | Caller-supplied identity and route | Caller-supplied operation values |
| --- | --- | --- | --- |
| Raw generic proxy | `nyxid_proxy` with its static schema | Required exact `service_id + slug + path`; optional method, body, non-sensitive headers, and response mode | Raw body/header values allowed only by the static schema. |
| Interactive connected-service operation | Request-local `nyxid_service_operation__*` tool | Exact enumerated `user_service_id`; method and path come from the effective OpenAPI operation | Only fields emitted in that dynamic operation schema. |
| Compiled workflow external call | Workflow `tool_call` to `nyxid_proxy` with server-owned admission proof | No caller `service_id`, slug, path, method, operation ID, or contract digest | Only admitted `path_params`, `query`, `headers`, `body`, and permitted `response_mode` fields. |

The modes are not fallback encodings of one another. In the compiled workflow
path, the committed proof owns exact UserService identity, slug snapshot,
operation ID, HTTP method, path template, contract digest, value schemas, and
response policy. Supplying a raw route field must fail before any HTTP request.

In the interactive operation path, the exact UserService effective OpenAPI is
the contract authority. Workflow admission requires an explicit, globally
unique, case-sensitive `operationId`. Missing or duplicate operation IDs fail
closed; method/path-derived identities are not acceptable substitutes.

## Other Known Public Contract Changes

### External capability readiness

`inspect_external_workflow_capability_readiness` now accepts
`selector + execution_mode`. A NyxID operation selector is copied from the
listed capability descriptor and contains exact `user_service_id` and
`operation_id`. Skills must not reconstruct the removed generic
`capability` bag or infer either identity from slug/path.

### Connected-service management

- The full request-local `nyxid_service_inventory` may list all enumerated
  instances or inspect one enumerated `user_service_id`.
- The channel sender inventory tool is a different list-only exposure with an
  empty object schema. It must be called with `{}` and cannot accept a
  hand-written ID.
- `nyxid_service_update.openapi_spec_url` is an approval-gated update:
  omission preserves the current override, a non-empty value sets it, and an
  empty string clears it. Aevatar does not become the OpenAPI owner.
- Dynamic operations, request, update, route, and delete continue to use exact
  UserService identity and request-local revalidation. Slug equality is not an
  identity conversion.

### Studio member invocation

`aevatar_invoke_member` is the canonical in-session tool for invoking a
Studio member. It requires `member_id + payload`. `endpoint_id` is
optional and defaults to `chat`; it is supplied only when a different
published endpoint is explicitly known.

This does not collapse identities. The tool consumes `memberId`, not a
draft `workflowId` or `publishedServiceId`. Service invocation remains
a separate published-service contract.

### Skill loading

The public `use_skill` call shape remains
`skill + optional args + optional mount_workflows`. Omission or
`mount_workflows=false` is read-only loading. Only explicit
`mount_workflows=true` may write workflows. Request-local remote-skill
credential resolution and channel authority changes are internal unless an old
skill incorrectly claims a different user-visible guarantee.

### Managed Codex execution

The `codex_exec` argument schema and two target payloads remain as published
in 1.13, but the managed readiness and operations contract changed after those
skills were released:

- normal managed execution is read-only with respect to credentials and does
  not create, repair, or rotate one;
- the authenticated user explicitly reconciles through
  `POST /api/managed-codex/credential` and reads the projected status;
- execution proceeds only when a newer authoritative state reports
  `execution_ready=true` with readiness reason `ready`;
  lifecycle `status=active` alone is insufficient;
- rotation is an explicit recovery action and raw secrets remain invisible;
- the timeout order is 180-second chrono execution, below a 300-second Aevatar
  managed request, below at least 315 seconds at NyxID/ingress, below the
  330-second Aevatar NyxID client ceiling, below at least a 360-second workflow
  canary budget;
- typed readiness and proxy failures must remain attributable rather than
  collapsing into one generic tool failure.

The two Codex skills and `aevatar-triage` must therefore be audited again
even though they were published on the same day as this upgrade request.

## Skill Impact Audit

All 15 skills are downloaded and tested. The following is an expected impact
map, not permission to publish before RED evidence:

| Skill group | Expected audit focus |
| --- | --- |
| `aevatar-workflow-authoring` | Compiled operation-only `nyxid_proxy` arguments, exact capability selector, operation IDs, file-artifact response policy. |
| `github-via-nyxid` and `firecrawl-via-nyxid` | Distinguish raw proxy, interactive dynamic operation, and compiled workflow examples. |
| `aevatar-channels-delivery` | Empty-schema channel inventory, request-time sender authority, admitted Lark operations and artifacts. |
| `aevatar-service-publisher` and `aevatar-team-builder` | Route direct member invocation to `aevatar_invoke_member` without crossing member/workflow/service identities. |
| `aevatar-platform-map` | Route the three NyxID invocation modes and new member invocation surface. |
| `aevatar-feasibility-advisor` | Managed Codex explicit readiness prerequisites and feasibility boundaries. |
| `aevatar-triage` | Operation-admission, readiness, managed credential, timeout, and owner-attributed failures. |
| Both `aevatar-codex-exec-*` skills | Explicit credential preparation/status proof, read-only normal execution, timeout hierarchy, and canary evidence. |
| Scheduler, automation, Agent Profile, and fallback skills | Verify current tool calls and lifecycle promises; retain exact versions when no failing scenario exists. |

The final changed-skill matrix and versions are determined from exact registry
readback at implementation time. No target version is preallocated in this
design because concurrent immutable releases must be rebased, not overwritten.

## Skill TDD and Evaluation

Follow one complete RED-GREEN-readback cycle per affected skill before moving
to the next skill.

### RED

For each published member:

1. Verify the exact old package hash and extract it into an isolated temporary
   directory outside all repositories.
2. Run a no-skill fresh-context control for the relevant task.
3. Run the same retrieval/application task with only the published skill.
4. Record the exact stale call, missing distinction, incorrect lifecycle
   promise, or identity error. If the old skill already behaves correctly,
   retain its version.

### GREEN

Create the minimal next version from the exact immutable baseline. Repeat the
same scenario with only the candidate. At minimum the combined suite covers:

1. a valid raw proxy call with exact `service_id + slug + path`;
2. an interactive dynamic operation call with enumerated
   `user_service_id` and operation fields;
3. a compiled workflow `nyxid_proxy` call containing operation values
   only and rejecting forged route fields;
4. readiness using the exact selector returned by capability listing;
5. setting, preserving, and clearing `openapi_spec_url`;
6. channel inventory called with an empty object;
7. member invocation with `memberId=m-alpha` while
   `workflowId=wf-alpha` and `publishedServiceId=svc-alpha`
   remain separate;
8. managed Codex explicit preparation, readiness readback, long timeout
   hierarchy, canary proof, and typed failure attribution.

Each candidate must pass the current Ornn package validator, frontmatter and
dependency validation, static stale-pattern scans, and manually inspected
fresh-context outputs. Examples must use only tools actually available on the
stated caller surface.

### Static rejection patterns

Mechanical scans reject positive instructions that:

- put raw route identity into an admitted workflow operation call;
- omit exact route identity from a raw `nyxid_proxy` call;
- use the removed readiness `capability` bag;
- derive an operation from method/path when `operationId` is absent or
  ambiguous;
- pass `user_service_id` to the channel list-only inventory tool;
- treat `memberId`, `workflowId`, or
  `publishedServiceId` as interchangeable;
- claim normal managed `codex_exec` repairs credentials;
- accept `status=active` without explicit execution readiness;
- give a managed workflow canary less than the required outer timeout budget;
- expose credentials, Vault references, Agent Keys, bearer tokens, or raw
  upstream bodies.

Historical terms may appear only in clearly marked migration or negative
explanations that cannot be mistaken for current instructions.

## Skillset Router Changes

After affected members pass independently, the `aevatar-platform` master
instructions are rebased on the latest registry revision. The router must:

- tell callers to inspect the actual exposed tool/schema before selecting one
  NyxID invocation mode;
- route workflow external operations through capability listing, exact
  readiness selector, compilation/admission, and proof-bound execution;
- route direct member invocation without turning it into service or workflow
  identity;
- route connected-service contract maintenance to the NyxID owner skill;
- route managed Codex setup and recovery through explicit readiness before
  the public canary;
- preserve all existing Agent Profile, scheduling, channel, identity,
  accepted-versus-observed, and credential honesty rules from the latest
  skillset.

Unchanged member refs remain byte-for-byte exact. Changed refs use exact literal
versions. No `latest`, version range, name-only dependency, or duplicate
skill identity is allowed.

## Publication and Ownership

Ornn skills and skillsets are immutable. The safe order is:

1. Refresh the merged source and live registry; record both exact revisions.
2. Resolve the latest skillset closure and detect concurrent versions.
3. Complete RED, candidate validation, GREEN, publication, exact JSON/ZIP
   readback, and hash comparison for one changed skill.
4. Repeat for the next changed skill only after the previous version is proven.
5. Resolve every proposed root and dependency closure and reject conflicts.
6. Publish the existing `aevatar-platform` GUID once with all exact refs
   and complete rebased master instructions; omit a caller-assigned version.
7. Read back detail, version history, closure, visibility, instructions, and
   every changed package from the published surface.
8. Repeat the integrated fresh-context scenario using only those independent
   readbacks.

Before any mutation, require an authenticated NyxID user with effective Ornn
write authority over the exact resource. Exact creator identity is not required
when the skill or skillset grants the caller's organization `write` access.
Never print, copy, or transfer credentials between profiles. The two Codex
skills were published with the default local NyxID credential through the
ChronoAI organization write grant; their original `createdBy` values remained
unchanged.

Concurrent publication is handled by rebase. If any target skill or the
skillset advances after the baseline read, download the new immutable version,
rerun the delta and RED cases, and build from that version. Never overwrite or
silently discard concurrent content.

## Failure and Rollback

No registry mutation occurs during audit or candidate testing. A candidate
failure is discarded outside the repositories.

Once published, a skill version or skillset revision is not edited or deleted
as rollback. A defect requires a corrected later immutable version. The
skillset is not updated until every changed dependency has passed exact
readback, so a partial member release leaves the previous platform revision
fully usable.

If final closure or integrated evaluation fails after skillset publication,
preserve the faulty revision for history, correct the responsible member, and
publish one later skillset revision. Do not mutate the previous revision or
repoint an exact version.

## Acceptance Criteria

The upgrade is complete only when:

- a successful remote refresh and exact merged source revision are recorded;
- all 15 current member packages are independently downloaded, hash-verified,
  and mapped to every tool contract they teach;
- each changed skill has a documented old-version RED failure and candidate
  GREEN result;
- each unchanged skill has evidence that no audited public contract drift
  requires a release;
- every changed package passes Ornn validation and exact immutable readback;
- the new skillset preserves unrelated member refs and all concurrent content;
- the new closure is readable, all-public, unique by skill name, and free of
  version conflicts;
- the published-only integrated evaluation selects the correct NyxID mode,
  readiness selector, member identity, and managed Codex lifecycle;
- no product source repository is changed by the skill publication workflow;
- repository documentation lint and diff checks pass for the final evidence
  update.

## Publication Evidence

The complete 15-member `aevatar-platform@1.13` closure produced this final
impact matrix:

| Skill | Baseline | Decision | Final |
| --- | ---: | --- | ---: |
| `fallback-to-calling-agent` | 1.0 | unchanged | 1.0 |
| `aevatar-workflow-authoring` | 1.5 | affected | 2.1 |
| `aevatar-team-builder` | 1.3 | unchanged | 1.3 |
| `aevatar-scheduler` | 1.8 | unchanged | 1.8 |
| `aevatar-service-publisher` | 1.5 | unchanged | 1.5 |
| `aevatar-platform-map` | 1.9 | affected | 1.10 |
| `aevatar-agent-profile-management` | 1.0 | unchanged | 1.0 |
| `aevatar-feasibility-advisor` | 1.3 | affected | 1.4 |
| `aevatar-triage` | 1.5 | affected | 1.6 |
| `firecrawl-via-nyxid` | 1.1 | affected | 1.2 |
| `github-via-nyxid` | 1.0 | affected | 1.1 |
| `aevatar-automation` | 1.2 | unchanged | 1.2 |
| `aevatar-channels-delivery` | 1.2 | affected | 1.3 |
| `aevatar-codex-exec-workflow-sample` | 3.0 | affected | 3.1 |
| `aevatar-codex-exec-node-setup` | 4.0 | affected | 4.1 |

The nine final immutable publications are:

| Skill | Version | GUID | SHA-256 |
| --- | ---: | --- | --- |
| `aevatar-workflow-authoring` | 2.1 | `bdfb0ec1-41cc-4909-815a-eb1a12b7aa2e` | `1ce600cacca5b949d3746d41f5d693987184aa8aad1cdc7aa7a366311ef0e6f4` |
| `aevatar-platform-map` | 1.10 | `b8bf9e98-2658-4e09-9c51-2e4958137091` | `74ec410a0150fe55bd3360628f95f012a832c4b2eebf3b93c50b53ba19fbf4e8` |
| `aevatar-feasibility-advisor` | 1.4 | `d0619556-402e-4baf-aa26-fbfe78ac937c` | `d6252a65f1928c942004c2eb69f302154d7e2b53f0ea521787c210876e96f0e7` |
| `aevatar-triage` | 1.6 | `fbd40315-317f-4f80-9885-b44b83e1a204` | `da2dbdb97350328fac2f248f17f0719a1166955088c14f31b21e61f4711fe5df` |
| `aevatar-channels-delivery` | 1.3 | `d2b575d3-0d80-4167-99e4-6161be47db7f` | `4770b46984239d882caa00d7e38e1ec05fed4af70fa6d13b087637a99d73fdc8` |
| `firecrawl-via-nyxid` | 1.2 | `47c28da0-430c-4cd8-adf2-4c5fbdf4e6c2` | `f5a0f83d119a5722ef17d39b8ae437bc4c9e2dcdef0899994353032dc6c1f45b` |
| `github-via-nyxid` | 1.1 | `abd23ac2-ed1c-4f8c-a6bc-6270390cbe32` | `089479f0bce9de830c3ecbedc688e4f85fcadc9d937d743aa41014472227bd09` |
| `aevatar-codex-exec-workflow-sample` | 3.1 | `f69ba2d0-4ae9-4ae5-8fd6-92b287695427` | `01cc4ac1ebdf62a0474d433b8bc6b8b96b73bada5c76c053e5c2a33c5bc4967e` |
| `aevatar-codex-exec-node-setup` | 4.1 | `9d4361eb-602e-4186-a12a-6b95801906c4` | `5afbd50a77b8e92e5ebbeb34a338c51774a352d40d97e26f77c020afecdecbba` |

`aevatar-workflow-authoring@1.6` was rejected before write with Ornn
`BREAKING_CHANGE_WITHOUT_MAJOR_BUMP`. Version 2.0 was then published with hash
`188b5b1ea5c92103dc8dba009e57077b6da0b755fa3decad106f93727c19b7a5`,
but its published-only evaluation exposed a flattened readiness selector. It
is preserved as superseded evidence; 2.1 is the corrected final reference.

The skillset was published once by stable GUID
`248b99d6-36ff-4d41-bb45-baa25c6a9cad`. Ornn assigned
`aevatar-platform@1.14`; it is `all-public` with 15 readable closure nodes and
the exact planned member order. Detail, history, and closure matched the
request after Ornn's documented trailing-whitespace trim, and 1.13 remained
readable with its original members and master prompt.

All 15 final ZIP packages were independently downloaded from the 1.14 closure,
validated as ZIPs, and matched their closure SHA-256 values. Both the proposed
exact-package evaluation and the final published-only evaluation correctly
produced the empty channel inventory call, dynamic operation call, admitted
workflow call and readiness selector, `m-alpha` invocation, managed credential
and readiness sequence, five deadlines, and typed failure attribution.
Evaluation traces and registry artifacts remain in the external release
workspace; the repository records no tokens, raw identity envelopes, or
package binaries.
