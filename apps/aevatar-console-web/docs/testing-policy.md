# Frontend Testing Policy

## Purpose and Scope

This document defines how to design, select, run, and report tests for
`apps/aevatar-console-web/`. It is mandatory whenever frontend behavior or
tests change. The default is focused, risk-based verification, not a complete
frontend test run.

## What Is Worth Testing

Tests exist to protect observable product behavior and important invariants
from plausible regressions. A code change alone does not create an obligation
to add a test. Add or change tests when they protect at least one distinct risk,
such as:

- new or changed rendered behavior, user interaction, navigation, auth
  transition, API mapping, query state, recovery path, or other observable
  contract;
- a high-risk business rule involving identity, authorization, destructive
  action, data loss, stale responses, concurrency, caching, or error recovery;
- a reproduced regression whose failure mode can be expressed deterministically;
- a boundary where independently evolving modules, transport contracts,
  browser APIs, or runtime configuration can realistically disagree.

If a change introduces no new observable behavior, changes no high-risk
business rule, and existing tests already cover the relevant regression risk,
it is valid to add no test. State that decision and its evidence in the task or
pull-request report. Never manufacture a test merely to make the change look
complete.

Before adding each test case, answer all four questions:

1. What concrete business behavior or invariant does this test protect?
2. What plausible production-code defect would make it fail?
3. Would it still pass after an internal refactor that preserves product
   behavior?
4. Is the same risk already protected by another test?

Do not add the case when the first two answers are unclear, when the answer to
the third question is no, or when the fourth answer is yes without a distinct
coverage gap. These answers do not need boilerplate source comments, but the
test name, setup, actions, and assertions must make the protected behavior and
failure mode reviewable.

## Choosing the Test Layer

Use the highest-value test layer for the risk: the layer that gives the best
combination of behavioral fidelity, defect detection, determinism, maintenance
cost, and diagnostic clarity. This is neither an instruction to choose the
smallest code unit nor an instruction to choose the broadest possible test.

| Test layer | Use it when | Do not use it as a substitute for |
| --- | --- | --- |
| Pure unit test | A deterministic algorithm, validator, formatter, mapper, or state transition has meaningful behavior that can be exercised through its public contract with few or no mocks | Behavior that emerges only through React rendering, routing, Query state, storage, or collaboration between modules |
| Module or adapter integration test | Request construction, response mapping, auth, caching, navigation, or coordination across internal modules is the risk; run the real internal collaborators and control only the external boundary | A mock graph that merely verifies which internal function called another internal function |
| Component or route integration test in jsdom | The user-visible behavior depends on components, hooks, router state, Query state, and interactions working together; render the realistic owning surface and act through accessible UI | Direct callback invocation, mocked hooks, mocked child components, or isolated implementation details that bypass the real workflow |
| Browser end-to-end or smoke test | A critical cross-system journey, OAuth redirect, browser/runtime behavior, deployment wiring, or real routing contract cannot be proven below the browser boundary | Routine branches already covered deterministically at a lower layer |

For frontend workflows, a component or route integration test is often more
valuable than many mock-heavy unit tests. Do not decompose an integration risk
into numerous isolated unit tests merely to keep each test small. Mock external
boundaries, not the internal behavior whose collaboration is under test.

The layer decision is separate from permission to run broad suites. If the
remaining risk genuinely requires browser end-to-end or smoke verification but
the current task does not authorize it, report the exact gap. Do not disguise
that gap with weak unit tests.

## Test Set Size and File Boundaries

- An ordinary feature change should normally add 2 to 6 high-value test cases.
  This is a calibration range, not a quota; a justified no-test decision or a
  smaller set is valid.
- Adding more than 8 test cases requires a test-by-test explanation in the task
  or pull-request report of the distinct risk protected by each case. Count
  materially distinct parameterized rows as separate cases.
- A test may contain multiple assertions when they jointly describe one
  independent business behavior. Do not split one behavior into many tests
  solely to force one assertion per test.
- When a test file grows beyond approximately 300 lines, inspect it for
  repeated setup, duplicate scenarios, oversized fixtures, and combinatorial
  explosion. Extract reusable setup or reduce equivalent cases where that
  improves clarity; do not split mechanically by line count.
- Split test files by independent business behavior or behavior domain, not by
  production-function count, assertion count, or arbitrary file length.

## Explicitly Prohibited Weak Tests

- Do not create mock-heavy unit tests for behavior whose real risk is the
  integration between components, hooks, router state, Query state, adapters,
  or browser APIs.
- Do not mock every internal collaborator and then assert only call counts,
  call order, or argument forwarding. A boundary request shape may be asserted
  when that shape is itself the observable contract.
- Do not duplicate production logic in the fixture or expected-value
  calculation; such tests can reproduce the same defect on both sides.
- Do not test framework behavior, trivial getters, constants, type-system
  guarantees, generated code, or markup details without a concrete product
  regression they protect.
- Do not add snapshots, render-only assertions, or `toBeDefined()` checks as a
  substitute for meaningful behavior and state-transition assertions.
- Do not add duplicate happy paths, superficial input permutations, or full
  Cartesian combinations when they protect the same risk. Use equivalence
  classes and add a case only when its failure mode is distinct.
- Do not couple tests to private callbacks, hook call order, incidental DOM
  structure, CSS classes, or internal function boundaries when the product
  behavior is unchanged.
- Do not add a test only to raise coverage, increase test count, or satisfy a
  perceived requirement that every changed function have a unit test.
- Reject a test that would still pass if the relevant production behavior were
  deleted, replaced with a constant, or disconnected from the user workflow.

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

## Incremental Test Execution Policy

- After deciding what is worth testing and selecting the appropriate test
  layer, run only tests directly affected by the production files and behavior
  changed in the current task.
- Choose execution scope independently from test layer. Prefer, in order: a
  named test case, one test file, a small explicit set of test files, then
  `--findRelatedTests` for a genuinely shared dependency.
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

## Selecting Tests to Run

1. Identify the changed observable contract and concrete regression risk:
   rendered output, user action, navigation, API mapping, auth transition,
   query state, integration boundary, or pure logic.
2. Inspect existing coverage before proposing a new case. Apply the four
   questions above and choose the test layer that most directly covers any
   remaining risk.
3. Run the colocated test for the changed module or its nearest owning route
   when that test protects the affected behavior.
4. Trace direct consumers when changing a shared API adapter, route builder,
   query key, auth helper, locale catalog, or reusable component. Add their
   focused tests only when their behavior can change.
5. Use `--findRelatedTests` when an import fan-out is broad and the dependency
   graph is more reliable than manual selection. Review the selected files;
   do not treat an unexpectedly broad result as permission to run everything.
6. If no meaningful affected test can be identified, report that exact gap.
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
- Keep each test file focused on one independent behavior domain and one
  coherent fixture lifecycle. Apply the file-boundary rules above instead of
  splitting by function or line count.
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

## Risk-to-Test-Layer Guide

| Changed risk | Preferred highest-value evidence |
| --- | --- |
| Pure business rule, state transition, validator, or mapper | Focused unit test through the public contract, plus `tsc` |
| Component, hook, or user interaction | Component integration test through rendered behavior, plus `tsc` and affected-file lint |
| Shared API, auth, caching, or navigation coordination | Module or adapter integration test; add a consumer test only for a separate consumer-visible risk |
| Route or Umi configuration | Focused route/config integration test, `tsc`, and `build` |
| Locale catalog or user-facing copy | Existing locale/catalog validation and a rendered test only when the changed copy or selection behavior carries regression risk |
| Build-time environment or proxy logic | Focused config or boundary integration test, `tsc`, and `build` |
| Jest configuration, shared setup, or shared test helper | A representative focused test from every affected Jest project plus direct helper consumers; report residual coverage gaps |
| Frontend documentation only | `git diff --check` plus validation that referenced relative paths exist; no unit test required |

This table guides layer choice; it is not a quota and does not override the
no-new-test exception. Increase coverage only when dependency fan-out or a
distinct user-facing risk justifies it.

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
