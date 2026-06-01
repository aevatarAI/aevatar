# Approach solver - delete

role=solver_delete
thread=2026-06-01-settings-diagnostics-final
approach_round=1
verdict=propose

## Classification
d - Settings 已经分散展示了 LLM、runtime 与 auth 的多数事实，但缺少一个只读、可复制、面向支持排障的汇总入口；能力需要存在，但应收窄为现有查询与现有本地配置的组合视图，不新增诊断后端、全局状态、独立 diagnostics service 或通用 env dump。

## Recommended action
建议做“薄 Diagnostics tab”，删除/避免所有新增后端、缓存、服务层和泛化诊断抽象：只在现有 Settings 页面复用 `studioApi.getUserConfig`、`studioApi.getUserConfigModels`、auth session helper、runtime helper 与已暴露的 frontend env 配置，渲染 4-5 个紧凑只读面板，并用显式 allowlist 生成 Markdown support bundle；复制内容只包含状态、计数、URL、模式、过期时间、token type 和配置有效性，绝不复制 access token、refresh token、id token、apiKey、provider secret 或整段原始对象。

## Concrete plan
- Files to delete: none
- Files to edit: `apps/aevatar-console-web/src/pages/settings/index.tsx`; `apps/aevatar-console-web/src/pages/settings/index.test.tsx`
- Caller migrations: none; keep existing `/settings` and `?section=account` behavior, add `?section=diagnostics` to the same local tab parser/router only
- Tests to delete/update/add: update existing Settings tests for 3-tab navigation and URL state; add focused tests for auth present/missing diagnostics, provider/model readiness summary, copy bundle allowlist excluding token/secret strings, and keep current LLM/Account tests passing
- LOC delta estimate: +180/-20
- Migration path: one step: extend the existing Settings tab union and render branch in place; no compatibility shim

## Reverse-evidence
- `apps/aevatar-console-web/src/pages/settings/index.tsx:72` currently limits `SettingsSection` to `"llm" | "account"`, and `apps/aevatar-console-web/src/pages/settings/index.tsx:184`-`193` falls unknown URL sections back to LLM; adding diagnostics only needs this local parser to accept one more key.
- `apps/aevatar-console-web/src/pages/settings/index.tsx:195`-`197` already owns Settings URL state, and `apps/aevatar-console-web/src/pages/settings/index.tsx:930`-`934` already replaces the URL on tab switch; no new router surface is needed.
- `apps/aevatar-console-web/src/pages/settings/index.tsx:965`-`975` defines the tab definitions and tab refs in one place, and `apps/aevatar-console-web/src/pages/settings/index.tsx:978`-`1005` already implements Arrow/Home/End keyboard navigation; a third tab should reuse this rail rather than adding Ant Tabs or another control.
- `apps/aevatar-console-web/src/pages/settings/index.tsx:561`-`568` already loads `studioApi.getUserConfig` and `studioApi.getUserConfigModels`; Diagnostics should consume those same query objects and their loading/error states, not call new APIs or duplicate React Query keys.
- `apps/aevatar-console-web/src/pages/settings/index.tsx:659`-`669` already resolves the effective LLM route, `apps/aevatar-console-web/src/pages/settings/index.tsx:724`-`729` already computes provider health/counts, and `apps/aevatar-console-web/src/pages/settings/index.tsx:856`-`875` already builds technical preview rows for route/model/runtime values; these are the right source facts for LLM diagnostics.
- `apps/aevatar-console-web/src/shared/studio/models.ts:778`-`800` shows the user config/model response contains only the required diagnostics shape: default model, preferred route, runtime mode/base URLs, provider statuses, gateway URL, model maps, and supported models. There is no need for backend expansion.
- `apps/aevatar-console-web/src/shared/studio/userConfigRuntime.ts:24`-`46` already provides resolved runtime base URL and local/remote label helpers; duplicate runtime-mode parsing should be avoided.
- `apps/aevatar-console-web/src/pages/settings/accountContent.tsx:39` reads the restorable auth session, and `apps/aevatar-console-web/src/pages/settings/accountContent.tsx:142`-`160` already renders token expiry, token type, scope, and refresh availability without revealing token values. Diagnostics can mirror this non-secret pattern.
- `apps/aevatar-console-web/src/shared/auth/session.ts:4`-`11` confirms tokens include `accessToken`, `refreshToken`, and `idToken`; the copy bundle must be constructed field-by-field so these properties are never serialized.
- `apps/aevatar-console-web/config/config.ts:27`-`40` resolves `AEVATAR_CONSOLE_PUBLIC_PATH` into Umi `base/publicPath`, and `apps/aevatar-console-web/config/config.ts:150`-`164` lists the client-exposed env values. Diagnostics should allowlist relevant public values/statuses rather than iterating over `process.env`.

## Failure/recovery
- failure_class: none
- recovery: no recovery needed; focused Settings tests plus `pnpm --dir apps/aevatar-console-web test --runInBand settings` and `pnpm --dir apps/aevatar-console-web tsc` are sufficient fact sources

## Risks
- Copying by spreading `authSession`, `StudioSettings`, provider settings, or `process.env` could leak `accessToken`, `refreshToken`, `idToken`, `apiKey`, or secret-like env values; the implementation must use a positive allowlist and tests should inject sentinel secrets.
- Duplicating the LLM/runtime computations in a new diagnostics module could drift from the editable LLM tab; keep diagnostics in the same Settings data flow or extract only tiny pure formatters.
- Showing all exposed frontend env raw values may over-share OAuth client/config details. Prefer readiness/status fields and public URLs; if `NYXID_CLIENT_ID` is shown at all, copy only configured/unconfigured rather than the value.
- Adding a separate tab component could regress keyboard navigation; use the existing tab rail and refs.

## Escalation triggers
- none

## Reasoning trace
Checked the real Settings files and found no existing Diagnostics tab. The current Settings page already has a custom accessible tab rail, URL preservation, shared React Query loading for user config/model config, computed LLM effective route/provider health/runtime rows, and Account auth token metadata display. The missing value is consolidated operator visibility plus copy-to-clipboard. I rejected adding backend endpoints, a new diagnostics API/client, a process-wide env dump, a session serializer, or a separate routing surface because current files already hold the required facts and the task explicitly forbids backend APIs and secret copying.

⟦AI:FKST⟧
