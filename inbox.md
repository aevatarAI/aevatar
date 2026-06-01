# Add Settings diagnostics for console environment readiness

## Scope
Improve `apps/aevatar-console-web/src/pages/settings`.

Add a third Settings section/tab named `Diagnostics` next to the existing `LLM` and `Account` sections.

The Diagnostics section should help an operator understand whether the console is ready to use without opening devtools or asking backend engineers.

Include compact, readable panels for:
- Auth session status: signed in / missing session / token expiry / token type.
- LLM defaults: effective route, default model, ready provider count, gateway URL.
- Runtime mode: runtime mode label, resolved runtime base URL, local/remote status when available from existing user config.
- Frontend environment: public path, relevant configured frontend env flags that are already exposed to the client.
- API/provider loading state and error summaries using existing `studioApi.getUserConfig` and `studioApi.getUserConfigModels`; do not add backend APIs.

Add a `Copy diagnostics` action that copies a Markdown support bundle to the clipboard. The bundle should include only non-secret values. Never copy access tokens, refresh tokens, API keys, or provider secrets.

## UX constraints
- Preserve the existing Ant Design Pro shell and Settings visual language.
- Keep the layout dense and operational, not decorative.
- Missing fields should render as `Unavailable` or `n/a`.
- The copy action must show success/failure feedback.
- The tab switch should preserve URL state with `?section=diagnostics`.
- Keyboard navigation must continue to work for the Settings tab rail.

## Tests
Update focused Settings tests for:
- diagnostics tab navigation and URL state
- auth session present and missing states
- provider/model readiness summary
- copy diagnostics excluding token/secret values
- existing LLM and Account tab behavior still passing

## Verification
Run:
- `pnpm --dir apps/aevatar-console-web test --runInBand settings`
- `pnpm --dir apps/aevatar-console-web tsc`
