---
title: "Studio Member-First Frontend Implementation Checklist"
status: draft
owner: tbd
last_updated: 2026-04-23
references:
  - "../adr/0016-studio-member-first-published-service.md"
  - "./2026-04-22-studio-member-lifecycle-spec.md"
  - "./2026-04-23-studio-member-first-backend-implementation-checklist.md"
---

# Studio Member-First Frontend Implementation Checklist

## 0. 目标

把 [ADR-0016](../adr/0016-studio-member-first-published-service.md) 转成可执行的前端实施清单。

这份 checklist 的核心目标有两个：

1. 在后端 member API 到位之前，先停止继续放大错误主语
2. 在后端 member API 到位之后，平滑切到真正的 member-first Studio

一句话：

前端先做语义收口，再做 API 切换，最后做 legacy 清理。

---

## 1. 当前前端基线

当前 Studio 已经具备不少可复用能力，但主语仍然混杂。

### 已有能力

当前前端已具备：

1. workflow build surface
2. script build surface
3. gagent build surface
4. workflow bind path
5. script bind path
6. gagent bind path
7. invoke workbench
8. observe / run context

关键文件：

1. [studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx)
2. [StudioMemberBindPanel.tsx](../../apps/aevatar-console-web/src/pages/studio/components/bind/StudioMemberBindPanel.tsx)
3. [StudioMemberInvokePanel.tsx](../../apps/aevatar-console-web/src/pages/studio/components/StudioMemberInvokePanel.tsx)
4. [StudioBuildPanels.tsx](../../apps/aevatar-console-web/src/pages/studio/components/StudioBuildPanels.tsx)
5. [shared/studio/api.ts](../../apps/aevatar-console-web/src/shared/studio/api.ts)

当前前端已经有的 bind API：

1. `bindScopeWorkflow`
2. `bindScopeScript`
3. `bindScopeGAgent`

### 当前问题

1. `Create member` 仍然是 workflow-only 语义
2. `routeState.memberId` 仍然在很多路径里实际承载 `serviceId`
3. 左侧 `Team members` 还混入 workflow/script/service 三类对象
4. Bind 页在已发布状态下仍像一个 `service inspector`
5. Bind 成功后的语义仍然偏向“切到某个 service”，而不是“发布当前 member”

---

## 2. 本轮范围锁定

### 必须坚持

1. 前端不能继续扩大 `memberId = serviceId` 假设
2. 前端不能伪造不存在的 backend member truth
3. 能先做的结构收口先做，但不假装后端已经具备 member API
4. `workflow / script / gagent` 必须在 Create/Build 语义上统一成 implementation kind
5. Bind 页的主语始终是“当前 member”

### 本轮不做

1. 在前端自建假的 canonical member store
2. 在 query-time 用 workflow/service/script 临时拼出永久真相
3. 在后端没给 member API 前，假装已经完成真正的 member-first 路由切换

---

## 3. 推荐实施顺序

前端推荐拆成两大段：

1. `Phase A`
   不依赖新后端，先做结构和语义收口
2. `Phase B`
   等 member API 就位后，切到真正 member-first 主路径

Phase A 的用户优先顺序建议是：

1. 先修 `Create member` 入口
2. 再修 `Bind` 页信息结构
3. 再统一 `Build / Bind / Invoke / Observe` 文案
4. 最后并行清理 legacy route 变量与测试命名

也就是说，A0 更偏代码卫生，A1/A2/A3 更偏用户立即可见收益。

---

## 4. Phase A：现在就能做的前端工作

## Phase A0: 冻结术语与过渡变量语义

目标：

先把代码里不诚实的语义显式标出来，避免继续扩散。

任务：

- [ ] 盘点 `routeState.memberId` 在当前实现里哪些路径实际是 `serviceId`
- [ ] 为这些路径引入诚实的过渡变量名，例如：
  - `legacyFocusedServiceId`
  - `focusedPublishedServiceId`
  - `legacyMemberKey`
- [ ] 在注释中明确哪些是 legacy 兼容路径
- [ ] 禁止新增代码继续把 `serviceId` 直接当成 member identity

关键位置：

1. [studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx)
2. [shared/studio/navigation.ts](../../apps/aevatar-console-web/src/shared/studio/navigation.ts)
3. [studio/index.test.tsx](../../apps/aevatar-console-web/src/pages/studio/index.test.tsx)

验收：

1. 新代码 review 时不会再误把 service 当 member
2. 前端状态模型里 legacy/service/member 边界更清楚

优先级说明：

这一步应与 A1/A2/A3 并行推进，但不应阻塞用户可见体验修正。

## Phase A1: 收口 Create member 交互结构

目标：

先把 Create member 的交互语义修正为三种实现类型。

任务：

- [ ] 重做 `Create member` modal 的信息结构
- [ ] 增加 implementation kind 选择：
  - `Workflow`
  - `Script`
  - `GAgent`
- [ ] 表单文案从“Create workflow member”改为“Create member”
- [ ] 根据 implementation kind 切换不同的创建说明
- [ ] 在后端 member API 未就位前，对 script/gagent 创建动作使用诚实 blocking state 或 feature flag

关键位置：

1. [studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx)

注意：

1. 这一步可以先改 modal 结构
2. 但不能伪装 script/gagent 已经有完整 create-member backend

验收：

1. 用户在交互上已经看到 member 是三种 implementation kind 的统一入口
2. 不再把“创建 workflow 文件”伪装成完整的 Create member 语义

## Phase A2: 收口 Bind 页信息结构

目标：

在不等新 member API 的前提下，先让 Bind 页更像“当前 member 的 contract 页面”。

任务：

- [ ] 保留 `StudioMemberBindPanel` 的 contract / smoke test / continue to invoke 结构
- [ ] 弱化“Published service picker”作为主入口的视觉权重
- [ ] 强化“当前目标 contract”叙事
- [ ] 把 workflow-only 的 bind 待绑定文案改成 member-first 文案
- [ ] 给 script/gagent bind 待接入位置预留统一接口

关键位置：

1. [StudioMemberBindPanel.tsx](../../apps/aevatar-console-web/src/pages/studio/components/bind/StudioMemberBindPanel.tsx)
2. [studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx)

验收：

1. Bind 不再像“浏览任意 service”
2. 用户更容易理解这是当前 member 生命周期的一步

## Phase A3: 收口 Build / Bind / Invoke / Observe 的页面文案

目标：

让页面主语先统一，哪怕底层 API 还没全切。

任务：

- [ ] 统一 context bar 文案围绕当前 member
- [ ] 清理“workflow member only”误导文案
- [ ] 清理“published service first”误导文案
- [ ] 把 Build 中的 workflow/script/gagent 文案统一成 implementation kind
- [ ] 把 Observe 空态改成 member lifecycle 分阶段空态

验收：

1. 整个 Studio 的用户心智先统一到 member-first
2. 文案不会继续向错误模型借力

---

## 5. Phase B：依赖新后端 member API 的前端工作

## Phase B0: 接入真实 member roster

前置条件：

后端完成：

1. `GET /api/scopes/{scopeId}/members`
2. `GET /api/scopes/{scopeId}/members/{memberId}`

目标：

让左侧 rail 真正只展示 member。

任务：

- [ ] 定义 `StudioMemberSummary` 前端模型，直接映射后端 member DTO
- [ ] 左侧 rail 改成消费真实 member list
- [ ] 移除 workflow/script/service 混合拼装逻辑
- [ ] 每张 member 卡片展示：
  - `displayName`
  - `implementationKind`
  - `lifecycleStage`
  - `binding / live status`
  - `latest run / status`

注意：

1. 左侧 roster 默认不展示 raw `publishedServiceId`
2. `publishedServiceId` 只在 Bind 详情区或高级信息里只读展示

关键位置：

1. [StudioShell.tsx](../../apps/aevatar-console-web/src/pages/studio/components/StudioShell.tsx)
2. [studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx)

验收：

1. `Team members` 终于是真正的 member roster
2. Studio 不再需要 query-time 拼装“伪 member”

## Phase B1: 接入真实 Create member API

前置条件：

后端完成：

1. `POST /api/scopes/{scopeId}/members`

目标：

Create member 正式从“资产创建”切到“成员创建”。

任务：

- [ ] 新增 `studioApi.createMember(...)`
- [ ] 按 implementation kind 分别处理创建成功后的路由
- [ ] 创建完成后直接拿到：
  - `memberId`
  - `publishedServiceId`
  - `implementationKind`
- [ ] 根据类型跳到对应 Build surface

实现建议：

1. Workflow member -> workflow build
2. Script member -> script build
3. GAgent member -> gagent build

验收：

1. Create member 的真实后端语义与 UI 语义一致
2. 不再调用 `saveWorkflow(...)` 冒充 member creation

## Phase B2: Studio 路由切换到真正的 `memberId`

前置条件：

后端 member API 已稳定。

目标：

让 `memberId` 真正回归 member 身份。

任务：

- [ ] 更新 `buildStudioRoute(...)`
- [ ] 更新 route state parser
- [ ] 把 `memberId` 的 canonical 语义切换为真实 member identity
- [ ] 保留 legacy `serviceId` 链接兼容解析

关键位置：

1. [shared/studio/navigation.ts](../../apps/aevatar-console-web/src/shared/studio/navigation.ts)
2. [studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx)
3. [studio/index.test.tsx](../../apps/aevatar-console-web/src/pages/studio/index.test.tsx)

验收：

1. Studio URL 中的 `memberId` 不再承载 `serviceId`
2. 旧链接还能跳转到正确 member

## Phase B3: Bind 页切到 member binding API

前置条件：

后端完成：

1. `PUT /api/scopes/{scopeId}/members/{memberId}/binding`
2. `GET /api/scopes/{scopeId}/members/{memberId}/binding`

目标：

Bind 页正式摆脱 service picker 语义。

任务：

- [ ] 新增 `studioApi.bindMember(...)`
- [ ] 新增 `studioApi.getMemberBinding(...)`
- [ ] Bind 页展示只读 `publishedServiceId`
- [ ] 移除“先选任意 published service”主交互
- [ ] 主 CTA 改成：
  - `Bind current revision`
  - `Publish latest revision`

验收：

1. Bind 页主语完全收口为当前 member
2. 当前 member 的 published service 只读展示，不再让普通用户管理 `serviceId`

## Phase B4: Invoke / Observe 切到 member-first 上下文

前置条件：

后端 member binding API 已稳定。

目标：

让 Invoke / Observe 都围绕当前 member，而不是围绕 service picker。

任务：

- [ ] Invoke 默认走当前 member 的 `publishedServiceId`
- [ ] 只在高级模式保留 service-level runtime 深链
- [ ] Observe 的 run/history/context 都挂在当前 member 下理解
- [ ] 从 Bind -> Invoke -> Observe 的 selection 统一围绕 `memberId`

验收：

1. 用户从 Create member 到 Observe 的整条链都不需要先理解 service catalog
2. runtime 事实退回到“当前 member 的运行结果”

---

## 6. 建议分工

### 可以先做的前端工作

1. 术语冻结
2. route 过渡变量重命名
3. Create member modal 结构重做
4. Bind 页信息结构收口
5. 文案与空态修正
6. 测试名称和断言语义修正

### 必须等后端 member API 的工作

1. 真实 member roster
2. 真实 Create member API
3. 真正的 `memberId` 路由切换
4. Bind 页彻底移除 service picker
5. member-first invoke / observe 主路径

---

## 7. 建议测试

### Phase A 测试

- [ ] Create member modal 展示三种 implementation kind
- [ ] legacy service-backed route 变量仍可工作
- [ ] Bind 页面文案与结构已围绕当前 member
- [ ] workflow-only 误导文案被移除

### Phase B 测试

- [ ] member roster 直接来自 backend member API
- [ ] create member 返回真实 `memberId`
- [ ] route 中的 `memberId` 真正是 member
- [ ] bind member 后展示稳定 `publishedServiceId`
- [ ] rename member 不影响 Bind/Invoke 使用的 published service

---

## 8. 一句话验收标准

当这份 checklist 完成后，前端 Studio 应满足：

1. 用户看到的主语始终是 member
2. `workflow / script / gagent` 只是 implementation kind
3. `Bind` 只是在发布当前 member
4. 普通用户不需要理解或输入 `serviceId`
