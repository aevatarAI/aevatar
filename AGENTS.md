# Frontend Work Boundary and Rule Routing

## Scope

- The frontend work boundary is `apps/aevatar-console-web/`.
- Frontend-owned implementation, configuration, tests, assets, and package-local
  documentation must stay inside that directory unless the user explicitly
  requests a cross-boundary contract change.
- Repository-wide canonical or product documentation under `docs/` may describe
  the frontend, but changing it is a separate documented scope decision rather
  than an incidental package edit.
- Do not modify backend production code under `src/`, backend tests under
  `test/`, repository CI under `tools/ci/`, or external sibling repositories as
  part of an ordinary frontend task.
- Treat backend APIs and published external services as contracts. Do not make
  a frontend change depend on an unrequested backend or external-repository
  feature. If the existing contract is insufficient, report the boundary
  conflict before widening the task.

## Rule Routing

- This file only declares the frontend boundary and routes work to the rules
  that own it. It does not duplicate frontend implementation or test policy.
- When work touches `apps/aevatar-console-web/`, read its `AGENTS.md` completely
  before inspecting or changing other files in that subtree.
- Before adding, changing, selecting, or running frontend tests, also read
  `apps/aevatar-console-web/docs/testing-policy.md` completely.
- A more deeply nested `AGENTS.md`, if one is added later, may refine and tighten
  the rules for its own subtree. It must not weaken the work boundary, dotenv
  secrecy, OAuth origin coupling, generated-file protection, or test limits.
- Direct user and system instructions take precedence over repository files.
  When two repository rules appear to conflict without weakening those
  safeguards, follow the rule closest to the file being changed and surface any
  unresolved ambiguity.

## Layout

```text
repo/
|-- AGENTS.md                                  # Frontend boundary and rule routing
`-- apps/aevatar-console-web/
    |-- AGENTS.md                              # Frontend development workflow
    |-- docs/
    |   `-- testing-policy.md                  # Detailed frontend test policy
    `-- src/
```
