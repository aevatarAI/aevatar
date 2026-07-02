# Aevatar FKST Devloop Policy

This host package defines aevatar-specific constraints for FKST-driven development. Generic FKST packages own the GitHub/devloop mechanics; this package owns only host policy and verification contracts for this repository.

## Rule source and projection

`CLAUDE.md` is the authoritative human and Claude Code instruction source for this repository. FKST does not execute the full `CLAUDE.md` directly. This `devloop.md` file is the FKST-facing projection of the highest-impact `CLAUDE.md` rules for automated development, and `conformance/pack.toml` contains the machine-checkable rules that keep this projection present and enforce selected invariants.

When `CLAUDE.md` changes, review whether this FKST projection or its conformance pack must change. When generated-code behavior needs a new hard stop, add or adjust a rule in `conformance/pack.toml`; when FKST needs clearer guidance, update this policy.

## Issue selection and ownership

- Only one issue may be processed at a time for this repository.
- Do not run parallel FKST development loops against multiple aevatar issues.
- Only issues with no assignee may be selected for automated implementation.
- Assign the issue to the FKST actor before implementation starts.
- If assignment fails, stop before making a branch or code changes.
- If an issue is already assigned, treat it as owned by someone else and skip it unless the assignee explicitly delegates the issue.
- If the issue appears to be user error, duplicate, obsolete, or missing reproduction, report that conclusion instead of forcing a code change.

## Issue analysis

Before implementation, FKST should first confirm the reported behavior still exists in the current codebase. Prefer identifying the general invariant and production path behind the issue, then fix the root cause rather than the narrow example symptom. If the issue is obsolete, too narrow, or lacks enough evidence for a safe general fix, report that clearly instead of adding a patch.

## Allowed auto-fix scope

FKST may edit these paths when directly required by the assigned issue:

- `src/`
- `test/`
- `tools/ci/`
- `scripts/run.sh`
- workflow or configuration files only when the issue specifically requires them

FKST should avoid editing these paths unless the issue explicitly requires it and verification covers the change:

- external repositories
- generated files
- historical ADR files under `docs/adr/`
- unrelated frontend, deployment, or infrastructure files
- local runtime artifacts, credentials, caches, and worktrees

## Projected CLAUDE.md rules for automated code work

FKST-generated code must follow the repository `CLAUDE.md`. This host package projects the highest-impact rules into FKST policy so they are visible to generated-code review and conformance.

### Architecture boundaries

- Keep strict layering: `Domain / Application / Infrastructure / Host`; API projects are only hosts/composition roots and must not carry business orchestration.
- Preserve dependency direction and dependency inversion; upper layers depend on abstractions, not concrete lower-level implementations.
- Do not modify external repositories to satisfy aevatar requirements. If an external surface is insufficient, work within this repository or stop with a clear limitation.
- Delete dead duplicate paths instead of adding compatibility shells or parallel implementations.

### Command, actor, projection, and readmodel rules

- Preserve command/event/read separation: commands produce events, queries read materialized readmodels.
- Do not add generic actor query/reply protocols or stream request-reply flows to fake RPC.
- Projection must consume committed facts through the unified projection pipeline; do not create a second projection path.
- Query paths must not replay events, read event store snapshots, prime projections, or side-read write-model internals.
- Actor state and behavior stay together in the business actor; do not split the same business entity into technical read/write/store actors.
- Cross-node or cross-turn facts must live in actor persistent state or distributed state, not in service-level in-memory dictionaries.

### Data contract and naming rules

- Stable business semantics must be modeled as typed proto fields, typed options, or typed sub-messages, not string-key bags.
- Use Protobuf for actor state, domain events, commands, callbacks, snapshots, checkpoints, and internal cross-node payloads.
- Do not hard-code concrete skill, command, or template names in production code; use metadata or generic discovery surfaces.
- Names must express business semantics and repository namespace/directory meaning.

### Hard-gate scope

FKST hard gates should focus on three mandatory outcomes: the implementation process must run to completion with required verification, non-trivial product behavior changes must have runtime impact verification evidence, and the resulting design must not violate repository architecture boundaries. Detailed code-quality findings should remain review comments or targeted guards unless they represent CI failure, missing runtime verification, or an architecture violation.

### Testing and verification rules

- Behavior changes require tests or an explicit explanation when the issue is not a code bug.
- Do not use `[Skip]`, disabled tests, bypassed hooks, or guard suppression to make validation pass.
- Do not add arbitrary `Task.Delay(...)` or unstable waits; eventual-consistency probes must use approved guard patterns.
- For architecture, projection, query/readmodel, workflow binding, runtime, or test changes, run the matching `tools/ci/*guard*.sh` script in addition to build/test.

### Runtime impact verification hard gate

For non-trivial product behavior changes, FKST must not rely only on unit tests when the affected behavior can be exercised through a local service flow. Documentation-only, test-only, formatting-only, and tooling-only changes do not require runtime impact verification unless they also change product runtime behavior. FKST must analyze the diff, route definitions, application services, and existing tests to identify the smallest affected public workflow or API path.

When local runtime verification is feasible, FKST must start the service, prepare minimal mock or seed data needed by the affected flow, call the affected API or workflow path, and verify both the immediate response and any relevant readmodel or query result.

The host policy must not maintain a hard-coded file-to-endpoint mapping. Endpoint and flow selection is an FKST reasoning task based on the actual change.

If runtime impact verification is not feasible, FKST must state why in the PR and list the substitute verification that was run. A PR must not be marked approved or merge-ready without either runtime impact verification evidence or an explicit infeasibility justification.

## Required verification

Use `scripts/run.sh` as the stable host command entrypoint when available.

For normal code changes:

- `dotnet build aevatar.slnx --nologo`

For test changes:

- `bash tools/ci/test_stability_guards.sh`
- the relevant `dotnet test ... --nologo` command

For architecture, actor, projection, readmodel, query, or workflow-binding changes:

- `bash tools/ci/architecture_guards.sh`
- any narrower guard matching the changed area

For runtime-sensitive or service-startup-sensitive fixes:

- `scripts/run.sh main-flow-smoke`

## PR expectations

- Base the PR on the configured integration branch.
- Keep the PR scoped to the assigned issue.
- Include verification commands and results in the PR body.
- Include runtime impact verification evidence for non-trivial product behavior changes, or explain why local runtime verification was not feasible and what substitute verification was run.
- Do not disable tests, guards, or hooks to make the PR pass.
- Do not mark an FKST PR as approved or merge-ready while automated review has unresolved P1/P2 comments; either address them or record why they are false positives before advancing state.
