# Meta judge

thread=2026-06-01-settings-diagnostics-codexcli
approach_round=1
@fkst-record approach-meta
field decision string converge
field kind string file-set-helper-boundary
field trigger_category string none
@fkst-end

## Solver verdicts
- minimal: propose
- structural: propose
- delete: propose

## Decision
结论是 `converge`：三位 solver 都支持在 `apps/aevatar-console-web/src/pages/settings` 内新增前端只读 Diagnostics tab，复用现有 `studioApi.getUserConfig`、`studioApi.getUserConfigModels`、auth session helper、runtime helper 与已暴露客户端配置，不新增后端 API、不改 runtime contract、不触碰 protected paths；但三者没有达到 3/3 同一实现形态，minimal 主张基本集中在 `index.tsx` 与测试，structural 主张新增 `diagnosticsModel.ts` 与 `diagnosticsContent.tsx`，delete 允许 inline 或一个小 sibling helper，文件集、helper 命名和 LOC 估算差异超过直接实施的共识栏。

## Framing agreement check
- Same boundary: yes - 三者都限定为 console frontend Settings 本地实现，不新增 backend/proto/runtime/docs/architecture 变更。
- Same files: no - minimal 是 `index.tsx` + `index.test.tsx`；structural 是 `index.tsx` + `diagnosticsModel.ts` + `diagnosticsContent.tsx` + `index.test.tsx`；delete 是 `index.tsx` + `index.test.tsx`，并可选一个 settings sibling helper/component。
- LOC variance <=30%: no - minimal 约 `+350/-25`，structural 约 `+430/-18`，delete 约 `+180/-20` inline 或 `+240/-20` split；最大差异无法按 30% 内收敛。
- Naming/migration agreement: no - 都同意无迁移，但 structural 指定 `buildSettingsDiagnosticsViewModel` / `buildSettingsDiagnosticsMarkdown`，minimal 未指定 helper，delete 只允许可选小 helper。
- Failure/recovery agreement: yes - 三者均为 `failure_class: none`，并认为通过 focused Settings tests 与 `pnpm --dir apps/aevatar-console-web tsc` 验证即可。

## If consensus
- Chosen framing: n/a
- Implement plan: n/a
- Implementation constraint: n/a

## If converge
- Convergence Question: Should the Diagnostics implementation use exactly which settings-local file/helper boundary: inline in `index.tsx` with tests only, one small sibling helper/component, or the structural split into `diagnosticsModel.ts` and `diagnosticsContent.tsx`?
- What each solver must address:
  - minimal: State whether the inline two-file plan is still required, or whether a settings-local sanitized view-model/content split is acceptable; give the exact file list and LOC estimate.
  - structural: Justify whether both `diagnosticsModel.ts` and `diagnosticsContent.tsx` are necessary for the whitelist copy boundary, or narrow to a smaller file set; give the exact file list and LOC estimate.
  - delete: Choose one concrete implementation shape instead of optional inline/sibling wording, and state the exact file list, helper boundary, and LOC estimate.
- Autonomous cap note: The next round remains subject to evolve Lua round/time caps; if convergence cannot reach 3/3 before the cap, the gate must fail closed according to the controller policy.

## If escalate
- Trigger category: n/a
- What needs human input: n/a
- Suggested next step: n/a

## Round audit trail
- solver-minimal: runtime pipeline artifact in this approach round
- solver-structural: runtime pipeline artifact in this approach round
- solver-delete: runtime pipeline artifact in this approach round

⟦AI:FKST⟧
