---
title: Aevatar Test Strategy
status: canonical
owner: Aevatar Maintainers
---

# Aevatar Test Strategy

This document defines which test surface is authoritative, how contributors
select local validation, and how test files are organized. Coverage is a
quality signal; it is not a test-suite ownership model.

## Test Authority

| Stage | Required surface | Purpose |
|---|---|---|
| Local development | Narrowest affected test method, class, or frontend test file | Fast behavior feedback for the current change |
| Affected backend project | One `test/*.Tests.csproj` when a narrower filter would not be meaningful | Validate the changed module and its direct test consumers |
| Pull request CI | `aevatar.slnx` through `coverage_quality_guard.sh` | Authoritative fast-suite result and coverage gate |
| Slow-test CI | `Aevatar.Integration.Slow.Tests` through `slow_test_guards.sh` | Minute-scale scenarios kept out of the fast solution |
| Release gate | Full solution, slow tests, and distributed smoke tests | Cross-module and deployment confidence |
| Console CI | Type check, complete Jest suite, and production build | Frontend contract, component, and packaging confidence |

`aevatar.slnx` owns every normal backend test project. The only project outside
that solution is `Aevatar.Integration.Slow.Tests`, whose owner is
`tools/ci/slow_test_guards.sh`. Solution filters (`*.slnf`) describe build
boundaries and must not become competing test entrypoints.

## Local Selection

Start from the changed production module and observable behavior:

1. Run an individual test method or class when the behavior has a stable test
   name.
2. Run an individual frontend test file for a localized TypeScript or React
   change.
3. Run the affected test project only when filtering would omit meaningful
   fixture or discovery coverage.
4. Do not silently run the full repository because the affected tests are
   unclear. Record the exact validation gap instead.

Full-suite execution belongs to CI and release validation unless a task
explicitly requests it. Static guards, builds, type checks, linters, and docs
checks remain required when their owned area changes.

## Suite Organization

Executable test files and classes are named after a durable behavior,
contract, or architecture rule:

- Backend suites use `*Tests.cs`.
- Frontend suites use `*.test.ts` or `*.test.tsx`.
- Coverage percentages and branch execution are never suite ownership axes;
  new `*CoverageTests.cs` files are rejected.
- One file owns one behavior domain and one fixture lifecycle. When a file
  needs unrelated fixtures, endpoint families, or systems under test, split it
  at that boundary.
- Test kits, fakes, fixtures, attributes, and shared assertions use names that
  state their supporting role and do not masquerade as executable suites.

Historical `*CoverageTests.cs` files and behavior-named files that still declare
partial `*CoverageTests` classes are listed in
`tools/ci/test_coverage_file_allowlist.tsv`. Their line counts are ceilings, not
targets. Each migration moves assertions intact into behavior-oriented suites,
updates any current CI anchors, and removes the obsolete allowlist entry.
Archived design documents may keep old names as historical evidence.

## Determinism

Tests remain asynchronous end to end. Do not use
`GetAwaiter().GetResult()` or introduce polling sleeps to bridge asynchronous
setup. Prefer explicit completion signals such as `TaskCompletionSource` or
channels.

Polling is allowed only for genuine cross-process or cross-node eventual
consistency probes. Such a file must be listed in
`tools/ci/test_polling_allowlist.txt`, and the change must explain why a
deterministic signal is unavailable.

## Adding Tests

Before adding or moving a backend test project:

1. Place it under `test/` and mark it as a test project.
2. Add a normal project to `aevatar.slnx`, or add the single slow project to
   `slow_test_guards.sh`; never leave an orphan project.
3. Name suites by behavior or contract and keep support types explicit.
4. Run the affected incremental tests.
5. Run `bash tools/ci/test_stability_guards.sh`.
6. Run specialized architecture guards owned by the changed behavior.

For frontend tests, run the affected Jest file plus TypeScript checking when
the change touches shared contracts or component props. CI remains the
authority for the complete serial Jest suite and production build.
