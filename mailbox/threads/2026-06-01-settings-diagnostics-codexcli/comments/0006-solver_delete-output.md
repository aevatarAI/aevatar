# Approach solver - delete

role=solver_delete
thread=2026-06-01-settings-diagnostics-codexcli
approach_round=1
verdict=propose

## Classification
d - capability is useful, but should be a narrow diagnostic composition over existing Settings/auth/config facts, not a new backend/API/config surface.

## Recommended action
保留 `Diagnostics` tab 这个很小的操作员可见能力，但删除式收窄实现边界：不要新增后端 API、不要新增环境变量暴露、不要复制或展示任何 token/API key/provider secret、不要把现有 LLM/Account 页面改成第二套状态模型；只把当前 Settings 页已经计算的 LLM/runtime/provider facts、现有 auth session helper、现有 client-exposed frontend env/feature facts组合成一个只读诊断视图，并把 copy bundle 做成纯非敏感 Markdown 摘要。

## Concrete plan
- Files to delete: none
- Files to edit: `apps/aevatar-console-web/src/pages/settings/index.tsx`; optionally add one small sibling component/helper under `apps/aevatar-console-web/src/pages/settings/` only if `index.tsx` would grow too much; `apps/aevatar-console-web/src/pages/settings/index.test.tsx`
- Caller migrations: none; keep existing Settings route and `AccountSettingsContent` behavior unchanged
- Tests to delete/update/add: update `apps/aevatar-console-web/src/pages/settings/index.test.tsx` for diagnostics tab URL state, auth present/missing, provider/model readiness, copy bundle redaction/exclusion, and existing LLM/Account behavior
- LOC delta estimate: +180/-20 if implemented inline, or +240/-20 if split into a focused `diagnosticsContent.tsx`; no backend LOC
- Migration path: add the third tab key and render path directly in Settings; no compatibility shim

## Reverse-evidence
- Current Settings tab state is a closed union of only `llm | account`, so the requested third tab requires a local UI extension rather than deleting an obsolete path: `apps/aevatar-console-web/src/pages/settings/index.tsx:72`, `apps/aevatar-console-web/src/pages/settings/index.tsx:965`
- URL state is already local and deterministic through `readSettingsSection`, `buildSettingsHref`, and `history.replace`; extend those instead of adding a router layer: `apps/aevatar-console-web/src/pages/settings/index.tsx:184`, `apps/aevatar-console-web/src/pages/settings/index.tsx:195`, `apps/aevatar-console-web/src/pages/settings/index.tsx:930`, `apps/aevatar-console-web/src/shared/navigation/history.ts:58`
- Keyboard tab rail behavior already exists and should be preserved by adding the new tab to `tabDefinitions`/refs only: `apps/aevatar-console-web/src/pages/settings/index.tsx:972`, `apps/aevatar-console-web/src/pages/settings/index.tsx:978`, `apps/aevatar-console-web/src/pages/settings/index.tsx:1386`
- LLM readiness facts are already computed from `studioApi.getUserConfig` and `studioApi.getUserConfigModels`: queries at `apps/aevatar-console-web/src/pages/settings/index.tsx:561`, provider/model derivations at `apps/aevatar-console-web/src/pages/settings/index.tsx:617`, effective route at `apps/aevatar-console-web/src/pages/settings/index.tsx:659`, model groups at `apps/aevatar-console-web/src/pages/settings/index.tsx:683`, errors at `apps/aevatar-console-web/src/pages/settings/index.tsx:936`
- Runtime facts already resolve from existing user config helpers; do not add an endpoint: `apps/aevatar-console-web/src/pages/settings/index.tsx:712`, `apps/aevatar-console-web/src/pages/settings/index.tsx:716`, `apps/aevatar-console-web/src/shared/studio/userConfigRuntime.ts:24`
- Account/auth facts are already available from local session storage helpers and currently displayed without revealing token values: `apps/aevatar-console-web/src/pages/settings/accountContent.tsx:39`, `apps/aevatar-console-web/src/pages/settings/accountContent.tsx:145`, `apps/aevatar-console-web/src/pages/settings/accountContent.tsx:149`, `apps/aevatar-console-web/src/pages/settings/accountContent.tsx:157`
- The auth session type contains sensitive token fields, so copy diagnostics must whitelist safe fields rather than serialize the session: `apps/aevatar-console-web/src/shared/auth/session.ts:4`, `apps/aevatar-console-web/src/shared/auth/session.ts:9`, `apps/aevatar-console-web/src/shared/auth/session.ts:10`, `apps/aevatar-console-web/src/shared/auth/session.ts:127`
- Frontend env flags exposed to the client are explicitly defined in Umi config, so Diagnostics should only read that existing client surface: `apps/aevatar-console-web/config/config.ts:150`, `apps/aevatar-console-web/config/config.ts:162`, `apps/aevatar-console-web/src/shared/config/consoleFeatures.ts:27`
- Studio API already decodes the provider readiness and gateway URL shape required by the task: `apps/aevatar-console-web/src/shared/studio/api.ts:303`, `apps/aevatar-console-web/src/shared/studio/api.ts:351`, `apps/aevatar-console-web/src/shared/studio/api.ts:2268`, `apps/aevatar-console-web/src/shared/studio/api.ts:2287`

## Failure/recovery
- failure_class: none
- recovery: no recovery needed; implementation can be verified by the requested focused Settings tests and `tsc`

## Risks
- Biggest overbuild risk is creating a new diagnostics data service or config abstraction for facts already present in `SettingsPage`; keep this as a render/helper extraction only.
- Biggest under-delete risk is leaving duplicate LLM/runtime formatting logic in both LLM and Diagnostics; acceptable only if kept to one or two tiny pure helpers, otherwise extract local pure helpers inside Settings.
- Biggest safety risk is accidentally copying `accessToken`, `refreshToken`, `idToken`, API keys, or provider secrets; build the Markdown bundle from a whitelist of labels/values and add a negative test containing those sentinel strings.
- The external `../NyxID` repository was not present, so no external contract source could be read; this is not blocking because the implementation should only inspect existing local auth session fields and must not change NyxID behavior.

## Escalation triggers
- none

## Reasoning trace
I rejected "delete/do nothing" because the operator task is not already satisfied: current LLM and Account tabs expose fragments, but no single readiness view and no safe copy bundle exist. I rejected adding backend support because all requested facts are already available through local auth session helpers, current Settings React Query calls, user config runtime helpers, and Umi-defined frontend env. I rejected a broad shared diagnostics framework because this request is scoped to one Settings page and the host policy protects backend/runtime/docs paths.

⟦AI:FKST⟧
