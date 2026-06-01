# Meta judge

thread=2026-06-01-settings-diagnostics-final
approach_round=3
@fkst-record approach-meta
field decision string consensus
field kind string implementation-boundary
field trigger_category string
@fkst-end

## Solver verdicts
- minimal: propose
- structural: propose
- delete: propose

## Decision
三份 solver artifact 均为 `propose`，且 round-3 已从上一轮的文件边界分歧收敛为同一 framing：只在 `apps/aevatar-console-web/src/pages/settings/index.tsx` 与 `apps/aevatar-console-web/src/pages/settings/index.test.tsx` 内实现 Settings 的第三个 `Diagnostics` section，复用现有 `studioApi.getUserConfig`、`studioApi.getUserConfigModels`、auth session、runtime resolver 与 Settings shared UI，通过 `index.tsx` 内非导出的显式 allowlist Markdown builder 生成非密钥复制包；三者都拒绝新增 backend API、runtime contract、shared/exported diagnostics report surface、`diagnosticsReport.ts` 或 `diagnosticsContent.tsx`，触碰文件也不落入 host protected path policy 中的 root `src/`、root `test/`、`docs/`、`tools/` 等受保护路径，因此无硬升级触发，判定为 `consensus`。

## Framing agreement check
- Same boundary: yes - 三者都限制为 `apps/aevatar-console-web` 的 Settings 前端实现，不新增后端、runtime、SDK、文档架构或跨层契约。
- Same files: yes - 三者的 concrete file set 都是 `apps/aevatar-console-web/src/pages/settings/index.tsx` 与 `apps/aevatar-console-web/src/pages/settings/index.test.tsx`；structural 明确不再要求 `diagnosticsReport.ts`，delete 明确若出现该类候选文件应删除并内联。
- LOC variance <=30%: yes - estimates are minimal `+270/-8` net `+262`, structural `+330/-25` net `+305`, delete `+230/-10` net `+220`; net spread is `85/305 = 27.9%` and all variance is contained in the same two-file framing and focused test detail.
- Naming/migration agreement: yes - 三者都使用 `diagnostics` section/tab key、private local allowlist formatter/copy handler、无 exported `SettingsDiagnosticsReport`、无 migration 或 compatibility shim。
- Failure/recovery agreement: yes - 三者均为 `failure_class: none`，恢复语义都是在同一 Settings 文件内修正 allowlist/UI/test mocks 后运行 `pnpm --dir apps/aevatar-console-web test --runInBand settings` 与 `pnpm --dir apps/aevatar-console-web tsc`。

## If consensus
- Chosen framing: structural
- Implement plan: `apps/aevatar-console-web/src/pages/settings/index.tsx`: add `diagnostics` to `SettingsSection`, `readSettingsSection`, `buildSettingsHref`, `handleSectionChange`, `tabDefinitions`, tab refs, header copy action, and a dense Diagnostics tab body using existing `AevatarPanel`, `SummaryField`, `SummaryMetric`, `FieldMetaPill`, and existing query/runtime helpers. `apps/aevatar-console-web/src/pages/settings/index.test.tsx`: extend focused Settings tests for Diagnostics navigation, auth status variants, readiness summaries, clipboard Markdown redaction, and existing LLM/Account behavior. Add no exported report type, SDK surface, backend API, route contract, or shared Settings abstraction.
- Implementation constraint: 下一阶段必须保持两文件边界；不得新增 backend APIs、runtime contracts、protected-path edits、Settings-local exported report/content modules、或任何 secrets/token/API key/provider secret 的 UI、clipboard、logs、tests 输出；复制包必须逐字段 allowlist 构造，不能 stringify/spread 原始 session/config/provider 对象。

## If converge
- Convergence Question: n/a
- What each solver must address:
  - minimal: n/a
  - structural: n/a
  - delete: n/a
- Autonomous cap note: n/a; consensus reached in this round, while any future round remains subject to evolve Lua round/time caps.

## If escalate
- Trigger category: n/a
- What needs human input: n/a
- Suggested next step: n/a

## Round audit trail
- solver-minimal: runtime pipeline artifact in this approach round
- solver-structural: runtime pipeline artifact in this approach round
- solver-delete: runtime pipeline artifact in this approach round

⟦AI:FKST⟧
