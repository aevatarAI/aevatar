# Public Codex Exec Smoke Skill Design

## Product Decision

Publish one public Ornn skill named `verify-codex-exec`. Its only purpose is to
let the current authenticated user run the canonical managed `codex_exec`
smoke test and receive an honest account-scoped verdict.

The skill is a diagnostic client of the existing Aevatar capability. It does
not add another execution path, provision credentials itself, modify rollout
configuration, or treat configuration and health checks as proof of
availability.

The authoritative success condition is a real `codex_exec` invocation whose
trimmed output is exactly:

```text
CODEX_EXEC_READY
```

## Approaches Considered

### One canonical managed execution

Load a tool-based Ornn skill that calls `codex_exec` once with the existing
`managed_sandbox`, `empty_git`, and 180-second contract.

This is selected because it exercises the user's Aevatar eligibility,
credential readiness, NyxID proxy route, chrono-sandbox, OpenSandbox runner,
Codex CLI, LLM route, and cleanup path while keeping cost and latency bounded.

### Configuration-only readiness check

Inspect feature flags, credential status, or service health without running
Codex.

This is rejected because those checks prove only partial dependencies. They
cannot establish that the current user can complete a real managed Codex run.

### Multi-step implementation benchmark

Ask Codex to create files, run tests, and return a larger artifact.

This is rejected for the default public check because it increases latency,
token cost, and timeout variance without improving the binary availability
decision. More demanding behavior belongs in a separate benchmark skill.

## Ornn Package Contract

The package is self-contained:

```text
verify-codex-exec/
  SKILL.md
```

Its Ornn metadata declares:

- name: `verify-codex-exec`;
- version: `1.0`;
- category: `tool-based`;
- tool list: exactly `codex_exec`;
- tags covering Aevatar, Codex, managed sandbox, smoke test, and diagnostics.

The description triggers only when a user asks whether managed `codex_exec` is
available for their account or asks to run its readiness/smoke check.

No scripts, workflows, references, assets, credentials, service identifiers,
or environment variables are bundled.

## Execution Contract

The skill must invoke `codex_exec` exactly once with this semantic payload:

```json
{
  "target": {
    "kind": "managed_sandbox"
  },
  "workspace": {
    "kind": "empty_git"
  },
  "prompt": "Reply with exactly CODEX_EXEC_READY",
  "timeout_secs": 180
}
```

The skill must not:

- answer from documentation, configuration, memory, or a previous run;
- substitute shell, SSH, OpenSandbox, chrono-sandbox, or NyxID proxy calls;
- retry automatically after an error or unexpected output;
- ask the user for an agent key, token, credential, service ID, or route;
- claim success from process exit alone when the exact marker is missing.

## Result Semantics

The final response uses the user's language and starts with one of two clear
verdicts:

- available: the tool completed successfully and the trimmed output equals
  `CODEX_EXEC_READY`;
- unavailable or inconclusive: every other outcome.

On success, include the target and elapsed time when the tool result exposes
them.

On failure, preserve the stable tool error code and redacted `diagnostic_id`
when available, followed by a short explanation appropriate to the category:

- tool absent or disabled;
- user ineligible or credential readiness failed;
- execution timed out;
- execution failed;
- successful process with unexpected output.

Never include access tokens, agent keys, request headers, raw credentials, or
internal secret references. Do not print a full upstream response when a
stable error code and diagnostic ID are available.

## Validation Strategy

Skill validation follows four gates:

1. Baseline scenario without the skill demonstrates that an agent may infer
   readiness without running the exact tool call or may return an ambiguous
   verdict.
2. With the skill loaded, the same scenario produces exactly one canonical
   `codex_exec` call and applies the strict marker rule.
3. The package passes the live Ornn skill-format validation endpoint.
4. After publication, the skill is read back by exact GUID/version, changed
   from private to public, and found through the public catalog surface.

A live positive execution is run through the user's authenticated Aevatar
surface. A negative-result test may use a supplied synthetic tool result so it
does not mutate account eligibility or production credentials.

## Publication Flow

Ornn creates new skills as private. Publication therefore uses this ordered
flow:

1. validate the ZIP package;
2. upload it as a new private skill;
3. read it back and verify name, version, tool declaration, and package files;
4. run the still-private skill once through Aevatar and verify the exact
   canonical tool call and success marker;
5. replace permissions with `isPrivate=false` and empty user/org share lists;
6. verify public search and public read visibility for the same immutable
   version and package hash.

If any gate fails, do not describe the skill as public or ready.

## Aevatar Boundary

This work requires no Aevatar runtime or configuration change. The skill
consumes the existing generic `codex_exec` tool contract. Eligibility,
transparent credential readiness, feature flags, execution limits, timeout
mapping, and sandbox cleanup remain owned by Aevatar and chrono-sandbox.

The public Ornn skill does not widen the managed Codex rollout. A user outside
the enabled population can discover and invoke the skill, but receives the
same fail-closed account-scoped result as a direct `codex_exec` call.

## Done Criteria

The work is complete when:

- the Ornn package passes format validation;
- the skill is publicly searchable and anonymously readable;
- an authenticated invocation causes exactly one canonical managed
  `codex_exec` call;
- exact `CODEX_EXEC_READY` produces an available verdict;
- timeout, eligibility, tool absence, and unexpected output never produce a
  false-positive verdict;
- no credential or token appears in package files, logs, or final output.
