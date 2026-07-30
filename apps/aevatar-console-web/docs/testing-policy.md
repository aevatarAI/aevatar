# Frontend Testing Policy

## Purpose and Scope

This document defines how to design, select, run, and report tests for
`apps/aevatar-console-web/`. It is mandatory whenever frontend behavior or
tests change. The default is focused, risk-based verification, not a complete
frontend test run.

## Test Stack and Projects

- Jest is the test runner. Testing Library and `@testing-library/jest-dom` are
  the default tools for rendered behavior.
- `jest.config.ts` defines two projects:
  - `node` is for explicitly listed DOM-free logic tests.
  - `jsdom` is for all remaining component, route, hook, and browser-facing
    behavior tests.
- Tests live beside production code as `*.test.ts` or `*.test.tsx`. Shared setup,
  mocks, and reusable render utilities live under `tests/`.
- When adding a truly DOM-free test to the `node` project, add its exact path to
  `nodeTestFiles` in `jest.config.ts`. Do not move a test to `node` merely to
  avoid configuring realistic browser behavior.

## Incremental Test Policy

- Run only tests directly affected by the production files and behavior changed
  in the current task.
- Prefer, in order: a named test case, one test file, a small explicit set of
  test files, then `--findRelatedTests` for a genuinely shared dependency.
- Run a whole Jest project only when a narrower selection would not provide
  meaningful verification. Do not silently expand to every frontend test when
  the affected set is uncertain.
- Do not run the complete frontend suite, browser end-to-end suites, deployment
  smoke suites, or unrelated test groups unless the user explicitly asks for
  them in the current task. This does not exclude a focused unit test for a
  product feature whose UI happens to use the words "smoke test."
- Runtime configuration checks required by the frontend `AGENTS.md` are safety
  prerequisites, not permission to launch an end-to-end or smoke-test suite.
- Static verification is separate from unit-test scope. Continue to run the
  relevant TypeScript, Biome, and build checks required by the frontend
  `AGENTS.md`.

## Selecting Affected Tests

1. Identify the changed observable contract: rendered output, user action,
   navigation, API mapping, auth transition, query state, or pure function.
2. Run the colocated test for the changed module or its nearest owning route.
3. Trace direct consumers when changing a shared API adapter, route builder,
   query key, auth helper, locale catalog, or reusable component. Add their
   focused tests only when their behavior can change.
4. Use `--findRelatedTests` when an import fan-out is broad and the dependency
   graph is more reliable than manual selection. Review the selected files;
   do not treat an unexpectedly broad result as permission to run everything.
5. If no meaningful affected test can be identified, report that exact gap.
   Do not substitute the complete suite as an unexamined fallback.

## Commands

Run a single test case in one file:

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --runInBand \
  --runTestsByPath src/path/to/feature.test.tsx \
  --testNamePattern 'observable behavior name'
```

Run one complete test file:

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --runInBand \
  --runTestsByPath src/path/to/feature.test.tsx
```

Run a small explicit set of files:

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --runInBand \
  --runTestsByPath \
  src/path/to/first.test.ts \
  src/path/to/second.test.tsx
```

Run affected tests for a shared production file:

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --runInBand \
  --findRelatedTests src/path/to/shared-module.ts
```

Select a Jest project only when project-wide validation is justified:

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --runInBand \
  --selectProjects node
```

The complete suite command may be run locally only when the user explicitly
requests it in the current task. CI may invoke it from its own workflow, but
that does not authorize an agent to broaden local verification:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand
```

## Test Design

- Assert observable behavior and stable contracts, not component internals,
  hook call order, implementation-specific markup, or incidental class names.
- Prefer accessible queries in this order: role and accessible name, label,
  visible text, then test ID only when the UI has no stable semantic query.
- Exercise behavior through user-visible interactions. Directly invoking an
  internal callback is not a substitute for clicking, typing, selecting, or
  navigating through the rendered surface.
- Cover relevant success, loading, empty, failure, retry, authorization, and
  stale-response states. Do not add states that the production workflow cannot
  actually reach.
- Test API adapters at their request and response boundary. Test components
  against typed adapter behavior rather than duplicating transport parsing in
  component fixtures.
- Keep each test file focused on one behavior domain and one coherent fixture
  lifecycle. Split files that become unrelated coverage buckets.
- Use snapshots only for stable, reviewable structure where a snapshot makes a
  semantic regression easier to detect. Prefer explicit assertions for product
  behavior, navigation, copy, and state transitions.

## Mocks and Fixtures

- Mock at system boundaries: network adapters, navigation, browser APIs, clock,
  or a heavy third-party component. Do not mock the unit's own collaborators so
  deeply that the real behavior is no longer exercised.
- Reuse shared mocks and `tests/reactQueryTestUtils.tsx` when they match the
  behavior. Do not create a second incompatible QueryClient setup in an
  individual file.
- Global cleanup already restores Jest spies and resets shared storage,
  history, Ant Design modals, Testing Library renders, and test QueryClients
  after each jsdom test. A test may add local cleanup but must not depend on
  mock calls or other state from a previous test.
- Fixtures must expose identity mistakes. Use visibly distinct values such as
  `memberId = 'm-alpha'`, `workflowId = 'wf-alpha'`, and
  `publishedServiceId = 'svc-alpha'`; never reuse one value for multiple
  identity domains.
- Keep fixtures minimal but realistic. Include only fields relevant to the
  behavior, while preserving actual nullability, version, and error semantics.

## Async and Time-Based Behavior

- Await user actions and observable UI settlement. Use `findBy*` or `waitFor`
  only for behavior that is genuinely asynchronous.
- A passing `waitFor` should resolve as soon as the condition is true. Do not
  use it to hide an assertion race or stale state transition.
- Do not add arbitrary sleeps, polling loops, or timeout increases to make a
  test pass. Use controlled promises, fake timers, explicit deferred responses,
  or a deterministic event boundary.
- When testing stale requests or ordering, hold and resolve named promises in
  the required sequence and assert that older results cannot overwrite newer
  state.
- Restore real timers within the test that enabled fake timers, even when the
  assertion fails.

## Change-to-Verification Guide

| Changed surface | Minimum meaningful verification |
| --- | --- |
| Pure function or mapper | Its focused `*.test.ts` file and `tsc` |
| Component or hook | Focused colocated `*.test.ts` or `*.test.tsx`, `tsc`, and lint for affected files |
| Shared API/auth/navigation module | Module test plus directly affected consumer tests and `tsc` |
| Route or Umi configuration | Focused route/config tests, `tsc`, and `build` |
| Locale catalog or user-facing copy | Locale/catalog tests and affected rendered test |
| Build-time environment or proxy logic | Focused config tests, `tsc`, and `build` |
| Jest configuration, shared setup, or shared test helper | A representative focused test from every affected Jest project plus direct helper consumers; report residual coverage gaps |
| Frontend documentation only | `git diff --check` plus validation that referenced relative paths exist; no unit test required |

This table is a floor, not a request to run unrelated tests. Increase coverage
only when the dependency fan-out or user-facing risk justifies it.

## Coverage and Reporting

- Coverage is diagnostic evidence, not a reason to create low-value tests or
  exercise generated code. Do not organize test files as generic coverage
  buckets.
- Generated files and third-party code are outside the coverage target.
- Report every command that ran and whether it passed. Name any test case or
  file selected through a filter.
- Explicitly state that the complete frontend suite was not run under the
  incremental policy. If validation was skipped or could not run, state the
  exact gap and reason.
