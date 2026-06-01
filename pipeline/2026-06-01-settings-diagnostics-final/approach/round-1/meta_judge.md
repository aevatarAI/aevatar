# Meta judge

thread=2026-06-01-settings-diagnostics-final
approach_round=1
@fkst-record approach-meta
field decision string converge
field kind string implementation-granularity
field trigger_category string
@fkst-end

## Solver verdicts
- minimal: propose
- structural: propose
- delete: propose

## Decision
三个 solver 都同意只在 `apps/aevatar-console-web` 内做 Settings 本地 Diagnostics tab，复用现有 auth session、`studioApi.getUserConfig`、`studioApi.getUserConfigModels`、runtime helper 与客户端已暴露 env allowlist，不新增后端 API、不改 runtime contract、不中断现有 LLM/Account 行为，也没有触发 host protected path 或 trusted-boundary 升级；但三者在文件边界、是否新增 settings-local helper/component、以及 LOC 成本上没有达到 3/3 同一实现框架，因此不能直接 consensus，下一轮应收敛到“inline 两文件实现”还是“拆出本地 diagnostics helper/content”的窄问题。

## Framing agreement check
- Same boundary: yes
- Same files: no - minimal/delete 只编辑 `settings/index.tsx` 与 `settings/index.test.tsx`，structural 还新增 `settings/diagnosticsReport.ts` 与 `settings/diagnosticsContent.tsx`
- LOC variance <=30%: no - estimates are +180/-20, +260/-20, and +520/-20; max/min add lines ratio is 520/180
- Naming/migration agreement: no - all agree no migration and `diagnostics` section key, but structural names a new `SettingsDiagnosticsReport`/`buildSettingsDiagnosticsReport` local contract that the other two do not accept
- Failure/recovery agreement: yes

## If consensus
- Chosen framing: 
- Implement plan: 
- Implementation constraint: 

## If converge
- Convergence Question: Should the Diagnostics implementation stay inline in `apps/aevatar-console-web/src/pages/settings/index.tsx` plus `index.test.tsx`, or should it split into settings-local `diagnosticsReport.ts`/`diagnosticsContent.tsx` helpers, given the shared requirement to keep the copy bundle explicitly allowlisted and non-secret?
- What each solver must address:
  - minimal: Confirm whether inline implementation is sufficient for UI/copy fact parity and secret exclusion, or accept the structural local helper split with exact file names.
  - structural: Justify why the extra `diagnosticsReport.ts` and `diagnosticsContent.tsx` files are required despite the small Settings-local scope, or narrow to the two-file plan.
  - delete: Confirm whether avoiding added files is a hard boundary or only a preference, and state the maximum allowed local formatter extraction if inline code becomes unsafe or too large.
- Autonomous cap note: The next round remains subject to evolve Lua round/time caps and must still fail closed if a protected-path or trusted-boundary trigger appears.

## If escalate
- Trigger category: 
- What needs human input: 
- Suggested next step: 

## Round audit trail
- solver-minimal: runtime pipeline artifact in this approach round
- solver-structural: runtime pipeline artifact in this approach round
- solver-delete: runtime pipeline artifact in this approach round

⟦AI:FKST⟧
