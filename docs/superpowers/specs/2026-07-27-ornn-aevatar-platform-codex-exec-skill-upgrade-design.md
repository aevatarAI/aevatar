# Ornn Aevatar Platform Codex Exec Skill Upgrade Design

## Status

Approach A was approved on July 27, 2026 and completed on July 28, 2026. Five
immutable skill versions and `aevatar-platform@1.13` are published, independently
read back, hash-verified, and forward-tested from the published registry surface.
No Aevatar, NyxID, Ornn, or chrono-sandbox product source was changed.

## Goal

Upgrade the canonical Ornn `aevatar-platform` skillset so an agent can discover,
configure, invoke, verify, and diagnose Aevatar `codex_exec` using the current
runtime contract. Update only skills whose behavior or routing is affected.

This is a skill-content and skillset-publication change. It does not change the
Aevatar, NyxID, Ornn, or chrono-sandbox product implementations.

## Product Mismatch

The published Ornn skills imply an obsolete managed execution path:

- Aevatar directly owns OpenSandbox provisioning and credentials;
- managed access depends on `llm:proxy` consent;
- Credential Vault or a credential proxy injects the runner token;
- Landlock is the required inner isolation boundary;
- process-local capacity and old sandbox failure names describe readiness.

The current implementation instead uses one typed Aevatar tool with two
infrastructure targets. Managed execution flows through the user's exact NyxID
`chrono-sandbox` UserService into a one-shot gVisor workload. Private SSH flows
through a user-owned NyxID SSH service and the target host's Codex installation.

The initial `aevatar-platform@1.11` baseline did not include either canonical
`codex_exec` skill, so its router could not discover the capability. Its platform
map also presented `scope -> team -> member -> service` as a global linear
lifecycle, which conflicted with the current identity boundary: `memberId`,
`workflowId`, and `publishedServiceId` identify separate resources.

While this upgrade was in progress, unrelated immutable releases advanced the
registry to `aevatar-platform-map@1.8`, `aevatar-feasibility-advisor@1.2`,
`aevatar-triage@1.4`, and `aevatar-platform@1.12`. The candidates were rebased on
those versions rather than overwriting or discarding their Agent Profile,
scheduling, Agent Key, credential-source, and admission/readback semantics.

## Verified Sources

The upgrade is grounded in these exact sources:

- Aevatar commit `aba74805c6b40f3848a554b85e4192e7c06abfa2`:
  - `NyxIdCodexExecTool` argument admission and target dispatch;
  - `ManagedCodexExecutionCoordinator` transparent credential readiness;
  - `NyxIdManagedCodexChronoTransport` fixed NyxID proxy call and result mapping;
  - `PrivateSshCodexExecutionAdapter` fixed Base64-to-`codex exec -` command;
  - `docs/canon/managed-codex-execution.md` and focused tests.
- chrono-sandbox managed branch commit
  `1e8134d8ac5f256e90bebb70695200a5c46aa72c`:
  - exact `POST /codex/execute` contract;
  - fixed runtime profile and Codex command;
  - gVisor workload creation, bounded JSONL parsing, timeout classification,
    strict cleanup, and sanitized diagnostics.
- Codex CLI `0.144.5`, pinned by the production Aevatar runner image, plus the
  current official Codex non-interactive-mode manual. Fresh-context skill
  evaluations used the locally available Codex CLI `0.144.3`; the platform
  contract was taken from the pinned runner and source, not inferred from that
  evaluator version.
- Ornn API and registry state read on July 27-28, 2026:
  - `aevatar-platform@1.11` and its exact 12-member closure;
  - `aevatar-codex-exec-node-setup@3.0`;
  - `aevatar-codex-exec-workflow-sample@2.0`;
  - concurrent `aevatar-platform-map@1.8`,
    `aevatar-feasibility-advisor@1.2`, `aevatar-triage@1.4`, and
    `aevatar-platform@1.12` releases;
  - current immutable-version and system-assigned skillset-revision contracts.

The chrono-sandbox default `main` branch does not yet contain the managed Codex
surface. Skill claims about that surface must therefore cite the managed branch
above and the matching Aevatar integration contract, not chrono-sandbox `main`.

## Authoritative `codex_exec` Contract

### Shared tool boundary

`codex_exec` accepts only:

- a strongly typed `target`;
- `prompt`, capped at 6000 UTF-8 bytes;
- optional `timeout_secs`;
- `workspace` only for `managed_sandbox`.

It does not expose a repository, workspace path, image, architecture, model,
provider, credential, shell command, approval flag, Codex profile, or sandbox
implementation. Target fields cannot be mixed.

### Managed sandbox

The caller supplies:

```json
{
  "target": { "kind": "managed_sandbox" },
  "workspace": { "kind": "empty_git" },
  "prompt": "Reply with exactly CODEX_EXEC_READY",
  "timeout_secs": 180
}
```

The default and maximum managed timeout are 180 seconds. The tool does not
request remote-tool approval. The normal path is:

```text
codex_exec
  -> ManagedCodexExecutionCoordinator
  -> transparent per-NyxID-user credential readiness
  -> exact personal chrono-sandbox UserService through NyxID
  -> chrono-sandbox POST /codex/execute
  -> one-shot OpenSandbox workload under gVisor
  -> fixed Codex CLI command
  -> bounded terminal result and strict cleanup
```

chrono-sandbox writes the prompt as data and runs:

```bash
codex --ask-for-approval never exec --ephemeral --json \
  - < /workspace/.aevatar/prompt.txt
```

The runtime-written Codex profile fixes the Responses provider, model, retry
bounds, approval policy, and `sandbox_mode="danger-full-access"`. This is not a
caller-granted host permission: Codex's inner sandbox is deliberately disabled
because gVisor is the workload isolation boundary. There is no Landlock,
Bubblewrap, Credential Vault substitution, or TLS-intercepting credential proxy
in this design.

NyxID terminates Aevatar's persistent per-user invocation key and injects a
five-minute delegation token into the one-shot process only as
`NYXID_LLM_TOKEN`. The current internal rollout validates exact `proxy:*`
delegation and remains `InternalOnly`; skills must not generalize this into a
public security guarantee or instruct callers to handle either raw credential.

A successful managed tool result is structured JSON containing:

- `status="succeeded"`;
- `target="managed_sandbox"`;
- final Codex text in `output`;
- `exit_code=0`;
- a sanitized `diagnostic_id`;
- optional `elapsed_ms`.

### Private SSH

The caller supplies:

```json
{
  "target": {
    "kind": "private_ssh",
    "private_ssh": {
      "service": "personal-codex-node",
      "principal": "runner"
    }
  },
  "prompt": "Reply with exactly CODEX_EXEC_READY",
  "timeout_secs": 300
}
```

Private SSH accepts no `workspace`. Its timeout defaults to 30 seconds and is
capped at 300 seconds. Aevatar Base64-encodes the prompt and sends a fixed
decode pipe ending in `codex exec -` through the selected NyxID SSH service.
The target host owns the Git workspace, Codex authentication/configuration,
forced-command wrapper, and Codex sandbox policy.

Private SSH approval remains host policy: it requires the local tool-approval
path unless that Aevatar host explicitly sets `BypassSshExecApproval`. The tool
returns the NyxID SSH response without converting it to the managed result
shape. Verification therefore checks the SSH response's `exit_code`,
`timed_out`, and stdout rather than managed `status/target/diagnostic_id` fields.

## Skill Changes

The two canonical Codex skills advanced from their original baselines. The three
platform skills were first rebased onto immutable versions published concurrently
during execution, then released as the next versions. The skillset similarly
advanced from the initial `1.11` baseline through concurrent `1.12` to final
`1.13`.

| Skill | Initial baseline | Rebase baseline | Published | Applied change |
|---|---:|---:|---:|---|
| `aevatar-codex-exec-workflow-sample` | 2.0 | 2.0 | 3.0 | Preserved the valid typed payloads; replaced obsolete readiness, failure, and isolation claims; documented the distinct managed and SSH result contracts. |
| `aevatar-codex-exec-node-setup` | 3.0 | 3.0 | 4.0 | Replaced the obsolete managed architecture and security model; updated prerequisites, transparent readiness, operations handoff, failure map, and dependency to sample 3.0; retained and re-verified private SSH hardening. |
| `aevatar-platform-map` | 1.7 | 1.8 | 1.9 | Retained concurrent platform semantics; routed setup/use/verification to both canonical Codex skills; corrected member/workflow/service identity semantics; stopped presenting one global lifecycle. |
| `aevatar-feasibility-advisor` | 1.1 | 1.2 | 1.3 | Retained concurrent surface-detection semantics; added target selection and feasibility boundaries for managed empty ephemeral Git work versus a user-owned private SSH workspace. |
| `aevatar-triage` | 1.3 | 1.4 | 1.5 | Retained concurrent credential-source diagnostics; added layered Codex diagnostics across Aevatar, NyxID, chrono-sandbox, OpenSandbox/gVisor, the runner, and private SSH using typed errors and sanitized diagnostics. |
| `aevatar-platform` skillset | 1.11 | 1.12 | 1.13 | Retained 10 unchanged `1.12` member refs, advanced the three platform skills, added both Codex skills as roots, and updated the master router instructions and description. |

No content change was made to `workflow-authoring`, `team-builder`,
`scheduler`, `service-publisher`, `automation`, `channels-delivery`, either
NyxID connector skill, or the fallback skill. They neither teach the obsolete
managed boundary nor own `codex_exec` setup. A workflow that needs Codex must
load the canonical Codex setup/sample skill for its exact payload instead of
duplicating the contract inside workflow-authoring.

## Product Routing Semantics

The published platform router treats `codex_exec` as an in-session execution
capability, not as a new Studio resource stage:

- “Can Aevatar use Codex for this?” -> `aevatar-feasibility-advisor`.
- “Set up or repair Codex execution” ->
  `aevatar-codex-exec-node-setup`.
- “Prove the configured route works” ->
  `aevatar-codex-exec-workflow-sample`.
- “Put a Codex task in a workflow” -> load the canonical Codex contract first,
  then use `aevatar-workflow-authoring` for the workflow document.
- “Why did codex_exec fail?” -> `aevatar-triage`, which may hand off to the
  setup skill after locating the failing boundary.

The router must keep these resource identities separate:

- `memberId`: Studio team-member authority and the member workflow-editor path;
- `workflowId`: draft/definition identity only;
- `publishedServiceId`: callable service identity only.

The workflow editor is a member implementation surface. It does not make the
member ID a workflow ID, and publishing does not turn either ID into a service
ID by string convention.

## Skill TDD and Verification

Each skill was upgraded and deployed independently rather than authored as one
untested batch.

For each skill, execution followed this gate:

1. Save the exact published old version as the current-behavior baseline.
2. Run realistic retrieval/application scenarios both without the skill and
   against that version. Record the no-skill control, the old skill's incorrect
   or missing behavior, and any rationalization the agent uses.
3. Author the minimal next version that corrects those observed failures.
4. Validate the package with Ornn's current skill-format validator.
5. Run the same scenarios against the candidate and inspect every result.
6. Check for stale terms and forbidden claims.
7. Publish the immutable version.
8. Read it back by exact version and verify its file hash/content before moving
   to the next skill.

At minimum, scenarios must prove that an agent:

- emits the exact managed and private request shapes without mixed fields;
- selects managed only for bounded work in an empty ephemeral Git repository;
- selects private SSH when the user's fixed host workspace/config is required;
- does not request model, image, raw credential, or sandbox flags;
- identifies gVisor as the managed isolation boundary and does not propose
  Landlock, Credential Vault, or credential-proxy repair;
- distinguishes structured managed results from raw SSH results;
- routes setup, verification, authoring, and triage to the right skill;
- preserves member/workflow/service identity separation.

Static scans for the two managed Codex skills must reject stale assertions for
`llm:proxy`, Credential Vault injection, credential proxy, Landlock, direct
Aevatar OpenSandbox ownership, caller-selected model/image/profile, and a
process-local capacity slot. Historical context may name an obsolete term only
to say explicitly that it is not the current design.

The integrated RED case used the exact published `aevatar-platform@1.12`
registry surface. It could not select `private_ssh`, did not route through the
node-setup or workflow-sample skills, omitted the mandatory
`CODEX_EXEC_READY` proof, and speculated about a managed workspace bound to an
existing private repository. The candidate GREEN case corrected those failures.
After publication, the same fresh-context scenario was repeated using only
independently downloaded `1.13` registry artifacts; it selected `private_ssh`,
kept the three Aevatar identities distinct, routed setup -> public sample proof
-> workflow authoring -> team binding -> service publication, and assigned
`managed_proxy_timeout` only to the bounded managed transport path. Mechanical
assertions passed against the 159-line answer and its 2610-line trace.

## Publication Order and Identity Boundary

Two Ornn owners are involved:

- NyxID user `2db990b5-29ea-4a32-acf5-0008420afa1f` owns the two
  `aevatar-codex-exec-*` skills.
- NyxID user `5d0d7b72-acff-49af-bb1b-9f30bbb7c102` owns the three platform
  skills and `aevatar-platform` skillset.

The completed publication sequence was:

1. Authenticate as the Codex-skill owner.
2. Publish and read back workflow sample 3.0.
3. Publish and read back node setup 4.0, pinned to sample 3.0.
4. Authenticate as the platform owner.
5. Rebase on and publish platform map 1.9 from concurrent 1.8.
6. Rebase on and publish feasibility advisor 1.3 from concurrent 1.2.
7. Rebase on and publish triage 1.5 from concurrent 1.4.
8. Publish the skillset from concurrent 1.12 with all exact member refs and the
   complete new master instructions; Ornn assigned revision 1.13.
9. Resolve the exact 1.13 closure and verify the five upgraded refs,
   both Codex skills, the master prompt, member visibility, and hash stability.

Never pass tokens between profiles, print them, copy profile files, or use one
owner's credential to impersonate the other. If the first owner cannot be
authenticated, stop before publishing any dependent platform versions rather
than creating duplicate replacement skills.

## Failure and Rollback Semantics

Ornn skill versions and skillset revisions are immutable. If a candidate fails
before publication, discard the candidate and leave the registry unchanged. If
a published version is wrong, publish a corrected next version; do not delete
or mutate history as rollback.

The skillset was not updated until every referenced member version had been
published and read back successfully. Concurrent `aevatar-platform@1.12`
therefore remained the latest revision throughout the cross-account member
publication phase. Before publishing 1.13, every proposed member ref and the
complete request were resolved and validated locally. Because versions remain
immutable, a defect found after publication would require a corrected later
revision; neither 1.12 nor 1.13 can be mutated in place.

## Published Evidence

Ornn published these immutable skill versions on July 28, 2026:

| Skill | Version | SHA-256 / Ornn `skillHash` |
|---|---:|---|
| `aevatar-codex-exec-workflow-sample` | 3.0 | `142f8e2734acd2c235d38b5cf9548c6e98fffeec42e61832b45701f926062575` |
| `aevatar-codex-exec-node-setup` | 4.0 | `8e92a11dd3b05a8c3923ff1d421c52469510c3dcc98611feefbd953017b5f9d5` |
| `aevatar-platform-map` | 1.9 | `31b77c9f766ada4d423a73dada8624a5a1c0317b7495cdf8cae0fe3b7224c561` |
| `aevatar-feasibility-advisor` | 1.3 | `e8545cf55045b6098f151a84820e3b93d35bea1890fdda6103e176b0f991cd57` |
| `aevatar-triage` | 1.5 | `a81a98deeec50dc90b0b65137bed985123c99d104ddab223fd754d73e19f7235` |

For every row, the local candidate ZIP SHA-256, the server closure's
`skillHash`, the first downloaded ZIP, and a second independent downloaded ZIP
are identical. Both registry JSON downloads also match exactly and contain the
same names, versions, metadata, and file maps.

The published skillset is:

- name: `aevatar-platform`;
- GUID: `248b99d6-36ff-4d41-bb45-baa25c6a9cad`;
- version: `1.13`;
- visibility: `all-public`;
- roots and unique closure members: 15, with no version conflict;
- publish-request SHA-256:
  `2acc241c61cac4a78bff63be0d9a7d3973f163fbb61dfef4e28f11931f9a1df1`.

Exact detail readback matches all 15 requested roots and the complete master
instructions. Version history contains `1.13` with 15 members and preserves
`1.12` with 13. Closure resolution contains 15 unique skills; node setup's
dependency on workflow sample 3.0 is correctly deduplicated. The independent
published-surface GREEN evaluation described above used only these downloaded
registry artifacts, not candidate files or implementation-source context.

## Acceptance Criteria

The completed upgrade satisfies the following criteria:

- all five affected skills exist at the target versions and pass Ornn format
  validation plus their forward scenarios;
- their published files match the verified local candidates exactly;
- the new `aevatar-platform` revision resolves a readable, conflict-free
  closure containing both canonical Codex skills and the three upgraded
  platform skills;
- the master prompt routes Codex feasibility, setup, sample verification,
  workflow authoring, and triage correctly;
- no affected current skill teaches the obsolete managed security or ownership
  model;
- all unchanged skillset members retain their exact previous refs;
- no Aevatar, Ornn, NyxID, or chrono-sandbox source repository is modified as
  part of publishing the skill content.
