# Aevatar Console Frontend Guidelines

## Applicability

- These instructions apply to the entire `apps/aevatar-console-web/` subtree.
- Read the repository-root `AGENTS.md` first. For any testing work, also read
  `docs/testing-policy.md` before choosing or running tests.
- Keep frontend work inside this subtree. A required backend contract change is
  a separate scope decision, not an incidental frontend edit.

## Owned Surface

- `config/` owns Umi configuration, routes, proxying, and build-time injection.
- `src/pages/` owns route-level product surfaces.
- `src/shared/` owns reusable API, auth, navigation, state, and UI capabilities.
- `src/locales/` owns user-facing localized copy.
- `tests/` owns shared Jest setup, mocks, and cross-cutting test helpers.
- `docs/` owns frontend-specific product, implementation, and verification
  documentation.
- Repository-wide canonical and cross-stack product documents remain under the
  root `docs/` tree and are outside an ordinary package-local frontend change.
- Do not hand-edit generated or transient output, including `dist/`,
  `coverage/`, `test-results/`, `src/.umi*`, or dependency contents under
  `node_modules/`.

## Technical Baseline

- Use React 19, TypeScript in strict mode, Umi Max, Ant Design, TanStack Query,
  Jest, Testing Library, Biome, and pnpm through the versions already pinned by
  this package.
- Prefer existing components, hooks, API adapters, query keys, route builders,
  test helpers, and design tokens before adding another abstraction.
- Keep remote state behind the existing API and TanStack Query boundaries.
  Components must not invent a second cache or derive authoritative backend
  state from route strings, display labels, or transient UI state.
- Keep protocol adaptation in the existing frontend boundary modules. UI
  components should consume typed frontend models rather than parse transport
  payloads inline.
- When authentication, SDK, or protocol behavior is ambiguous or differs from a
  published contract, verify the relevant versioned public documentation and
  installed SDK first. Inspect external source only when that evidence is
  insufficient, and never make this repository depend on an external product
  change.
- Avoid new `any`, broad type assertions, and multi-purpose identifiers. Model
  stable product semantics with explicit types.

## Development Workflow

1. Inspect the affected route, component, shared dependency, and nearest tests
   before editing. Follow the established local pattern unless it violates a
   rule in this file.
2. Make the smallest coherent change that completes the requested workflow.
   Preserve the existing information architecture and navigation model unless
   the user explicitly requests a redesign.
3. Decide whether the changed risk warrants new tests, then choose the
   highest-value test layer that directly protects that risk using
   `docs/testing-policy.md`. Do not default to unit tests when the behavior is
   created by multiple modules or browser-facing integration. After selecting
   the right tests, use the narrowest meaningful command to run them.
4. Run the relevant static checks and build checks for the changed surface.
   Report the exact commands and explicitly state any validation not run.

Run commands from the repository root unless a command says otherwise:

```bash
pnpm --dir apps/aevatar-console-web install --frozen-lockfile
pnpm --dir apps/aevatar-console-web tsc
pnpm --dir apps/aevatar-console-web biome:lint
pnpm --dir apps/aevatar-console-web build
```

- Install dependencies only when they are missing or dependency metadata has
  changed. In a linked worktree, complete the host environment synchronizer
  required by the local Codex setup before installing, starting the app, or
  running integration-style verification.
- Run `tsc` after TypeScript or configuration changes.
- Run Biome on the affected files, or `biome:lint` when the changed surface is
  broad enough to justify the package-level lint command.
- Run `build` when changing routes, Umi configuration, build-time environment
  injection, dependencies, public assets, or another bundling-sensitive path.
- Do not run the complete frontend test suite by default. The incremental test
  rules in `docs/testing-policy.md` are mandatory for ordinary development.

## Local Runtime and OAuth

- Do not print, diff, summarize, commit, or expose dotenv values. Treat
  `.env.local` as an ignored runtime contract.
- Before starting the OAuth-enabled console, make the actual frontend origin
  and `NYXID_REDIRECT_URI` origin match exactly in scheme, hostname, and port.
  `AEVATAR_CONSOLE_FRONTEND_PORT` and the callback origin must be changed
  together when a worktree needs a different port.
- Reuse the configured port only when its listener belongs to the intended
  server for the same worktree. Otherwise choose a free port and update both
  coupled settings in that worktree before startup.
- Use the resulting origin consistently for the listener, browser URL, and
  OAuth callback, and confirm that the OAuth provider accepts the callback
  before attempting login.
- Keep `AEVATAR_API_TARGET` and `AEVATAR_STUDIO_API_TARGET` aligned with the
  intended backend processes. Keep `/api/auth/*` routed through the Studio
  backend as documented in `README.md`.
- Do not introduce port `5000`; Web API examples must also avoid `5050`.

## Product Identity and Routing

- Do not model Studio workflow as a global linear
  `Build -> Bind -> Invoke -> Observe` lifecycle. Existing uses of those words
  describe local resource states or actions, not a universal product phase.
- `memberId`, `workflowId`, and `publishedServiceId` are separate identities:
  `memberId` is Studio team-member authority, `workflowId` is a workspace draft
  or definition identity, and `publishedServiceId` is callable runtime identity.
- Never assume `memberId === workflowId` in ordinary product code. Historical
  repair or materialization behavior cannot become a route, API, or fixture
  convention.
- Never send `workflowId` to a member API, `memberId` to a workflow-draft API,
  or either value in place of `publishedServiceId`. Resolve conversions only
  from an explicit backend contract or read model, never from prefixes, string
  equality, route position, or naming conventions.
- Use `routeMemberId` or `memberId` for member identities read from paths,
  `routeDraftWorkflowId` or `draftWorkflowId` for draft hints, and
  `publishedServiceId` for service identities. An unresolved value must be
  named as a candidate until its source establishes the concrete identity.
- Canonical Team routes express `scope -> team -> member` ownership:
  `/scopes/:scopeId/teams`, `/scopes/:scopeId/teams/:teamId`, and
  `/scopes/:scopeId/teams/:teamId/members/:memberId/...`.
- `/scopes` is only the authenticated technical entry for resolving a scope; it
  is not the Team collection URL.
- Canonical member workflow editors are
  `/scopes/:scopeId/teams/:teamId/members/:memberId/workflow` and
  `/scopes/:scopeId/teams/:teamId/members/new/workflow`. The `workflow` path
  segment names the member implementation editor surface, not a workflow
  resource identity. A `workflowId` query value is only a draft hint and cannot
  replace the path's member identity.
- Do not add or preserve hidden `/teams/:scopeId...` compatibility routes.
  Parse paths by resource name, not by fragile segment indexes.

## Workflow Activity vNext Baseline

- Before changing any route, page, component, hook, query, adapter, model,
  style, locale, or test for
  `/scopes/:scopeId/workflow-activity-vnext`, read all three of these sources
  completely:
  `docs/design-baselines/workflow-activity-vnext/README.md` and
  `docs/superpowers/specs/2026-08-04-workflow-activity-vnext-design.md` and
  `docs/superpowers/specs/2026-08-04-workflow-activity-vnext-user-paths.md`.
- Treat
  `docs/design-baselines/workflow-activity-vnext/aevatar-workflow-activity-vnext.excalidraw`
  as the primary visual, information-architecture, and interaction reference.
  Treat the design specification as the normative route, product identity,
  API, state, and backend-compatibility contract. Treat the user-path
  specification as the normative journey, decision, recovery, and completion
  evidence contract.
- The PNG and HTML prototypes are reference artifacts, not runtime data
  sources. Never copy their hard-coded records, `localStorage` persistence,
  timers, simulated receipts, or successful-looking defaults into production
  code.
- Production remote state must come from real API responses or real user
  actions acknowledged by those APIs. When an API is pending, empty,
  unavailable, delayed, or failed, render that exact state; do not insert mock
  or fixture fallback data.
- Keep mock and fixture data in clearly named test-only files. Production
  routes, components, hooks, queries, and API adapters must not import them.
- Reuse the existing protected-route, `/login`, `/auth/callback`,
  `NyxIDAuthClient`, sanitized `returnTo`, session restoration, sign-in,
  sign-out, and service-access review behavior. Do not create a vNext auth
  route, provider, callback, token cache, session store, or identity fallback.
- Reuse the existing Umi locale configuration, `ConsoleLanguageSwitch`,
  `getLocale`/`setLocale`, message helpers, and `en-US`/`zh-CN` catalogues. Add
  every new vNext message to both catalogues; do not create a vNext locale
  context, storage key, or hard-coded visible-copy path.
- Login, callback, language, and account presentation may adopt the vNext
  visual system, but auth, redirect, callback, session, language, persistence,
  error, and accessibility behavior must remain unchanged. If vNext hides the
  global header, reuse its existing language/account actions inside the local
  shell rather than cloning their logic.
- Include the design-baseline declaration from
  `docs/design-baselines/workflow-activity-vnext/README.md` in every vNext
  implementation task and pull request so the Excalidraw hash, contract
  documents, user paths, and real-API-only data-source rule are reviewable.
- Run
  `python3 docs/design-baselines/workflow-activity-vnext/verify-baseline.py`
  from `apps/aevatar-console-web/` before and after changing the baseline. The
  verifier must confirm the declared hash, deterministic generator output, and
  exact 17-frame inventory.
- Keep this feature frontend-only and isolated to its new route namespace.
  Do not change backend code or alter existing Workflow, Run, Settings, Studio,
  Team, member, redirect, or menu behavior to implement it.

## UI and Interaction

- For page, component, console, playground, or visual-polish work, follow
  `../../docs/canon/frontend-design.md` as the stable design baseline and use
  the repository's `aevatar-frontend-design` skill when it is available.
- Choose one explicit visual direction for a change and keep it consistent
  with the existing console. Do not fall back to generic AI-dashboard styling,
  purple-white gradients, repetitive card grids, or undifferentiated panels.
- Do not treat `Inter`, `Arial`, `Roboto`, or `system-ui` as a default preferred
  font stack; follow the established product typography and design baseline.
- Reuse or extend design tokens, CSS variables, and theme tokens for color,
  typography, spacing, radius, shadow, and motion. Avoid large sets of isolated
  magic values.
- Treat the console as an operational tool: prioritize scannability, clear
  hierarchy, predictable navigation, and efficient repeated actions.
- Every visible title or heading must add distinct orientation, resource
  identity, current-state, or task meaning. Do not repeat the same noun at the
  modal/drawer shell and content levels, and do not add helper copy that merely
  paraphrases an adjacent heading. Prefer one resource-owned container title;
  add a nested heading only when it names a genuinely different state, view,
  or user task.
- Scope asynchronous loading feedback to the data region owned by the query or
  command. Keep committed sibling regions mounted and usable, preserving their
  selection and scroll position when they are not being reloaded. Use a
  whole-workspace loading state only when the initiating operation actually
  invalidates or blocks the whole workspace.
- Reuse the shared loading language instead of composing page-local spinners or
  indicator cards: use `AevatarContentSkeleton` for an initial structured data
  surface, `AevatarLoadingOverlay` when a command temporarily blocks committed
  content, and `AevatarLoadingDots` only for compact inline progress.
- Default user-facing surfaces must show only information needed to understand
  the current task, result, or next action. Do not expose backend architecture,
  transport, storage, or consistency terminology such as `read model`,
  `projection`, `materialization`, `receipt`, raw actor/command/correlation
  identifiers, state versions/watermarks, DTO or endpoint names, or query
  sampling limits in page titles, descriptions, helper copy, primary tables,
  empty states, or primary error messages. Preserve truthful loading, accepted,
  delayed, and failed semantics in plain product language. When raw values are
  genuinely useful for support or debugging, place them behind an explicit,
  user-opened technical-details disclosure instead of making them the default
  interface.
- Preserve responsive behavior, keyboard access, focus visibility, semantic
  controls, and readable contrast. Verify dense real content and narrow mobile
  widths without overlap, clipping, or inaccessible actions.
- Put user-facing copy in the locale catalogs and keep supported catalogs in
  sync. Do not introduce hard-coded product copy where the existing locale
  system applies.
- Use established icon libraries and control patterns. Add accessible names or
  tooltips for icon-only or unfamiliar actions.

### Action Feedback and Toasts

- Use the shared `ConsoleToastProvider` and `useConsoleToast` from
  `src/shared/ui/ConsoleToast.tsx` for transient user-action feedback. Do not
  introduce new direct `antd` `message` or `notification` calls inside React
  product surfaces. Non-React transport error boundaries retain their existing
  handling unless the boundary receives a deliberate React-safe migration.
- A success toast is evidence of a completed user-visible action, not of a
  click, request dispatch, `202 Accepted` response, local optimistic update,
  or background observation still in progress. Show it only after the API
  contract has reached the state the copy claims.
- Keep a toast short, localized, and action-oriented. Do not put endpoint
  names, DTO fields, request IDs, raw backend errors, or recovery diagnostics
  in it; expose those through the surface's existing technical-details path.
- Report transient API request failures through the shared error toast. Do not
  add page-wide warning or error banners above otherwise usable content, and
  do not render an error while another request required to classify the same
  state is still pending.
- Use persistent inline state, alerts, or panels for loading, accepted,
  observing, delayed, retryable, authorization, forbidden, and primary-content
  failures where the user needs a durable next step. A toast must not be the
  only evidence of a durable status or recovery action.
- Emit at most one toast for one user action. Avoid success toasts for local
  form edits that still require an explicit page-level save. Migrate touched
  user-feedback paths to the shared abstraction while preserving legacy
  behavior outside the requested surface unless a deliberate migration is in
  scope.

## Change and Review Hygiene

- Branch names use `<type>/YYYY-MM-DD_<purpose>`, where `<type>` is one of
  `feat`, `fix`, `refactor`, `docs`, `test`, or `chore`, and `<purpose>` uses
  only lowercase letters, digits, and hyphens.
- Commit messages are imperative and describe one purpose. Pull requests state
  the problem and solution, affected paths, exact verification commands and
  results, and documentation impact.
- Date-prefixed frontend documents use `YYYY-MM-DD-...`; date-time-prefixed
  documents use `YYYY-MM-DD-HH-mm-ss-...`. Do not place timestamps at the end
  of filenames or mix timestamp formats within one document family.

## Completion Criteria

- The requested frontend behavior is complete across success, loading, empty,
  failure, and retry states that are relevant to the workflow.
- The testing decision follows `docs/testing-policy.md`: changed risks have
  high-value coverage at the appropriate layer, or the no-new-test decision
  satisfies the policy's explicit exception.
- Relevant type checks, lint checks, and bundling checks pass.
- Generated output and unrelated files remain untouched.
- The final report lists exact validation commands and states that the full
  frontend suite was not run unless the user explicitly requested it.
