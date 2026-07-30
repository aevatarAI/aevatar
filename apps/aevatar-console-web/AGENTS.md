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
3. Add or update tests for changed observable behavior. Select the narrowest
   meaningful tests using `docs/testing-policy.md`.
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
- Preserve responsive behavior, keyboard access, focus visibility, semantic
  controls, and readable contrast. Verify dense real content and narrow mobile
  widths without overlap, clipping, or inaccessible actions.
- Put user-facing copy in the locale catalogs and keep supported catalogs in
  sync. Do not introduce hard-coded product copy where the existing locale
  system applies.
- Use established icon libraries and control patterns. Add accessible names or
  tooltips for icon-only or unfamiliar actions.

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
- Changed behavior has focused tests following `docs/testing-policy.md`.
- Relevant type checks, lint checks, and bundling checks pass.
- Generated output and unrelated files remain untouched.
- The final report lists exact validation commands and states that the full
  frontend suite was not run unless the user explicitly requested it.
