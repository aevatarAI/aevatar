# Aevatar Console Web Test Requirements

本文件适用于 `apps/aevatar-console-web/**` 下的所有现有和新增代码，尤其是
`*.test.ts`、`*.test.tsx`、测试 fixture、测试 helper 与 production UI copy。
这些要求是前端测试的强制约束，不是建议。

## #3010 CI 失败复盘

GitHub Actions job `90603668673` 共暴露了三条失败测试，根因分为两类：

| 错误 | 具体表现 | 根因 | 后续要求 |
| --- | --- | --- | --- |
| 陈旧 UI 断言 | `detail.test.tsx` 的两条用例仍断言已经移除的“选择团队成员”界面 | API contract 调整期间测试沿用了中间实现，没有重新对齐已确认保留的 `origin/dev` UI | 先确认当前产品要求和基准 UI，再更新断言；不得用陈旧测试反向要求 production UI 恢复错误界面 |
| 绕过 i18n | `TeamAutomationsTab.tsx` 中 cadence 描述和 `Member` fallback 使用硬编码英文 | 恢复原 UI 时只关注了视觉结果，没有沿用现有 `copy(...)` 和语言目录契约 | 所有用户可见文案必须通过已有或新增的 i18n key 输出，动态值必须使用插值参数 |

同时暴露出一个维护问题：测试名称仍使用 `Team-level selector shell` 描述已经不存在的
selector。测试名称、setup、断言和实际用户行为必须表达同一件事；产品行为变化时四者要一起
检查，不能只修改让测试通过的那一行断言。

## UI 与 API 边界

- 后端 API、DTO、状态码或请求路径变化，默认只允许影响 API adapter、query/mutation 和
  contract tests。除非需求明确要求改变 UI，否则不得修改页面布局、控件、文案、弹窗字段、
  空状态和交互流程。
- 涉及现有页面时，以当前明确产品要求和 `origin/dev` 的用户可见行为作为 UI 基准。
  不得把开发过程中的临时 UI 写进长期测试。
- 测试应验证用户可见语义和重要业务边界，例如当前 heading、空状态、可执行操作以及“不应
  发出某类请求”。避免断言无业务意义的 DOM 层级、临时 class、实现私有状态或中间组件。
- 删除或替换一个 UI 元素时，必须同步搜索它在测试名称、fixture、查询方式和断言中的全部
  引用。不能留下描述旧行为但实际断言新行为的测试。
- 不得为了满足陈旧 UI 断言而修改 production UI，也不得为了通过接口测试而增加未经需求
  确认的按钮、说明、错误区块或额外步骤。

## 测试语义要求

- 每条测试只描述一个清晰行为。测试名称必须说明用户场景、动作和可观察结果，且必须与当前
  setup 和断言一致。
- 优先使用 role、accessible name 和真实可见文案查询 UI。只有目标没有合理语义查询方式时
  才使用 `data-testid`。
- 正向 UI 断言与关键负向边界应同时保留。例如一个 shell 不应加载 member automation 时，
  除了验证空状态，还要断言对应 API 未被调用。
- 不得通过放宽为模糊匹配、无条件等待、删除负向断言或仅检查组件存在来掩盖回归。
- 测试 fixture 必须使用可区分的身份值，例如 `memberId = "m-alpha"`、
  `workflowId = "wf-alpha"`、`publishedServiceId = "svc-alpha"`。禁止复用相同字符串，
  也不得依赖前缀、相等关系或路由位置推断身份。
- 涉及 scoped Team member surface 时，测试必须使用 canonical
  `/scopes/:scopeId/teams/:teamId/members/:memberId/...` 语义，不能恢复 legacy route。
- 异步 API 返回 `202 Accepted` 时，测试只能断言真实达到的 accepted/pending 阶段；不得把
  accepted 写成 completed，也不得通过 UI 文案暗示已经同步完成。

## i18n 与文案断言

- production UI 中所有英文和中文可见文案都必须通过 `copy(id, defaultMessage, values)` 或
  项目现有等价 i18n API 输出。
- 动态文案必须使用命名插值，例如 `{time}`、`{timezone}`、`{memberName}`；不得用模板字符串
  拼接可见英文句子来绕过语言目录。
- fallback 文案同样属于用户可见 copy。`"Member"`、`"Unknown"`、状态名称等 fallback
  不能直接硬编码在 JSX 或 UI handoff helper 中。
- 新增或调整 i18n key 时，必须保持默认文案与英文 catalog 一致，并运行
  `src/locales/hardcodedCopyAudit.test.ts` 的相关用例。
- 测试可以断言当前 locale 的最终可见文案，但不能复制另一套独立翻译逻辑到测试中。

## 失败处理流程

1. 使用 CI 给出的准确测试文件和 `testNamePattern` 在本地复现失败。
2. 将失败归类为 production regression、陈旧测试、contract 变化或静态门禁违规。
3. 对 UI 相关失败，核对当前需求、`origin/dev` 基准和实际页面；不能仅根据失败断言猜测正确
   UI。
4. 对 API 相关失败，检查真实 request/response contract；不能根据 handler 名称或旧 mock
   推测字段和状态。
5. 先修复最小根因，再运行原始失败用例；随后只扩展到受影响文件的增量测试。
6. 如果测试名称、fixture 或 helper 已经不再表达当前行为，必须一起修正，不能留下语义债务。

## 本地验证范围

- 默认只运行本次变更直接影响的 Jest 文件或 `testNamePattern`，并使用 `--runInBand`。
- 禁止在本地把无文件范围的全量 `pnpm test` 作为常规验证，也不要因为一次修复再启动“第二轮
  全测”。完整前端测试由 PR 的 GitHub Actions 执行。
- production TypeScript 发生变化时运行：
  `pnpm --dir apps/aevatar-console-web tsc`。
- 新增或修改测试文件时运行：`bash tools/ci/test_stability_guards.sh`。
- UI copy 发生变化时增量运行：
  `pnpm --dir apps/aevatar-console-web test src/locales/hardcodedCopyAudit.test.ts --runInBand`。
- 提交前始终运行 `git diff --check`，并检查 changed files 没有超出任务范围。
- 推送 PR 后由 GitHub Actions 执行全量测试。除非用户明确要求，不要等待远端全测完成再交付。

## 提交前检查清单

- 测试名称、setup、fixture 和断言描述同一个当前行为。
- API contract 变化没有意外改变 UI。
- UI 与 `origin/dev` 的差异都有明确产品需求依据。
- 所有用户可见 copy 都走 i18n，包括 fallback 和动态 cadence 文案。
- `memberId`、`workflowId`、`publishedServiceId` fixture 明确区分。
- 原始失败用例和受影响增量测试通过。
- 没有运行无必要的本地全量 Jest。
