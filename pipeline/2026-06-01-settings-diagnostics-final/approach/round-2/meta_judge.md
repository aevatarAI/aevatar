# Meta judge

thread=2026-06-01-settings-diagnostics-final
approach_round=2
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
三份 solver artifact 都确认能力缺口真实、实现边界应限制在 `apps/aevatar-console-web` 的 Settings 前端内，且都明确不新增后端 API、不改 runtime contract、不触碰 host protected paths 或 trusted-boundary，因此不触发升级；但 round-2 仍未达到 3/3 同一 framing：minimal 与 delete 要求只改 `settings/index.tsx` 和 `settings/index.test.tsx`，structural 仍要求新增 `diagnosticsReport.ts` 与 `diagnosticsContent.tsx` 以及 `SettingsDiagnosticsReport` 本地契约，LOC、命名、文件边界和 failure/recovery 语义均冲突，所以不能 consensus，必须继续围绕是否允许新增 Settings-local diagnostics 文件这一窄问题收敛。

## Framing agreement check
- Same boundary: yes
- Same files: no - minimal/delete 只编辑 `settings/index.tsx` 与 `settings/index.test.tsx`，structural 还新增 `settings/diagnosticsReport.ts` 与 `settings/diagnosticsContent.tsx`
- LOC variance <=30%: no - estimates are +220/-15, +440/-12 plus possible +35, and +240/-10; max/min added LOC is at least 440/220 = 2.0
- Naming/migration agreement: no - all agree no migration and `diagnostics` section key, but structural introduces `SettingsDiagnosticsReport`/`buildSettingsDiagnosticsReport`/`formatSettingsDiagnosticsMarkdown` while minimal/delete require only non-exported inline formatter/builder functions
- Failure/recovery agreement: no - minimal/delete classify failure as none/no recovery needed, while structural classifies conformance-fail with recovery through local allowlist/report mapping adjustments and verification

## If consensus
- Chosen framing: n/a
- Implement plan: n/a
- Implementation constraint: n/a

## If converge
- Convergence Question: Is the implementation boundary for Settings Diagnostics strictly the two existing files `apps/aevatar-console-web/src/pages/settings/index.tsx` and `index.test.tsx` with only non-exported inline allowlist Markdown helpers, or is adding `diagnosticsReport.ts` and `diagnosticsContent.tsx` inside the same Settings directory required for the non-secret copy contract?
- What each solver must address:
  - minimal: State whether the two-file inline boundary is a hard requirement or whether a Settings-local report/content split is acceptable, and ground the answer in secret-exclusion auditability and current `index.tsx` size.
  - structural: State whether the new `diagnosticsReport.ts`/`diagnosticsContent.tsx` files are strictly required, or whether the same allowlisted report/copy safety can be satisfied by non-exported helpers in `index.tsx`.
  - delete: State whether the no-new-file boundary remains hard after considering auditability of copied diagnostics, and whether any implementation failure would change that boundary.
- Autonomous cap note: The next round remains subject to evolve Lua round/time caps; if caps fire, evolve Lua fails closed outside this arbitration artifact.

## If escalate
- Trigger category:
- What needs human input:
- Suggested next step:

## Round audit trail
- solver-minimal: runtime pipeline artifact in this approach round
- solver-structural: runtime pipeline artifact in this approach round
- solver-delete: runtime pipeline artifact in this approach round

⟦AI:FKST⟧
