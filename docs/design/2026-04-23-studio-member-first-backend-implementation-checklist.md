---
title: "Studio Member-First Backend Implementation Checklist"
status: draft
owner: tbd
last_updated: 2026-04-23
references:
  - "../decisions/0012-studio-member-first-published-service.md"
  - "./2026-04-22-team-member-first-prd.md"
  - "./2026-04-22-studio-member-lifecycle-spec.md"
---

# Studio Member-First Backend Implementation Checklist

## 0. 目标

把 [ADR-0012](../decisions/0012-studio-member-first-published-service.md) 落成可执行的后端实施清单。

这份 checklist 锁定的不是视觉设计，而是后端事实模型：

1. `member` 是 Studio 的唯一主语
2. 每个 member 拥有一个稳定 `publishedServiceId`
3. `workflow / script / gagent` 是 member 的实现类型
4. `Bind` 是把当前 member revision 发布到该 member 自己的 published service
5. Studio 后续必须基于 member-centric API 工作，而不是继续把 scope default binding 当主路径

补充约束：

6. 本轮先把 `scope` 视为 Studio 当前的 team context 容器，不要求先落地独立 `team` authority

---

## 1. 当前后端基线

仓库里已经有可复用的 binding/runtime 能力，但还没有真正的 member 权威模型。

### 已有能力

当前 scope binding 已支持三种实现类型：

1. `workflow`
2. `script`
3. `gagent`

关键位置：

1. [ScopeBindingCommandApplicationService.cs](../../src/platform/Aevatar.GAgentService.Application/Bindings/ScopeBindingCommandApplicationService.cs)
2. [ScopeServiceEndpoints.cs](../../src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs)
3. [ScopeBindingModels.cs](../../src/platform/Aevatar.GAgentService.Abstractions/ScopeBindings/ScopeBindingModels.cs)

当前 `ScopeBindingUpsertRequest` 已支持显式 `ServiceId`：

1. [ScopeBindingModels.cs](../../src/platform/Aevatar.GAgentService.Abstractions/ScopeBindings/ScopeBindingModels.cs)

当前 binding command 的行为是：

1. 若 `ServiceId` 为空，则回退到默认 service
2. 若 `ServiceId` 非空，则绑定到指定 service

关键位置：

1. [ScopeBindingCommandApplicationService.cs](../../src/platform/Aevatar.GAgentService.Application/Bindings/ScopeBindingCommandApplicationService.cs)
2. [ScopeWorkflowCapabilityOptions.cs](../../src/platform/Aevatar.GAgentService.Application/Workflows/ScopeWorkflowCapabilityOptions.cs)

### 当前缺口

当前仓库没有真正可供 Studio 使用的 backend `member` 权威模型。

缺失内容包括：

1. member write-side authority
2. member read model
3. member-centric create/list/get/bind API
4. 稳定 `publishedServiceId`
5. `memberId -> publishedServiceId` 的权威映射

这也是为什么前端当前还会误把 `serviceId` 当成 `memberId` 语义来源。

---

## 2. 本轮范围锁定

### 必须坚持

1. `memberId` 不是 `serviceId`
2. `publishedServiceId` 必须由后端生成并持久化
3. member 的事实状态必须由 actor 持久态或正式 read model 承载
4. 不能在中间层用进程内字典维护 `memberId -> serviceId`
5. member binding 必须复用现有 service binding 能力，而不是新造第二套 binding 系统
6. 设计必须覆盖 `workflow / script / gagent`

### 本轮不做

1. Team Router 全量重做
2. Team Detail 的完整后端重构
3. 删除所有 legacy scope/service runtime endpoint
4. 前端视觉实现

---

## 3. 建议模块落位

建议按当前仓库结构落在下面几层：

### Domain / Application

1. `src/Aevatar.Studio.Domain`
2. `src/Aevatar.Studio.Application`

建议承载：

1. member 领域模型
2. member 应用服务 / command port / query port
3. Studio 面向成员的 typed contracts

### Projection

1. `src/Aevatar.Studio.Projection`

建议承载：

1. member current-state read model
2. member roster read model
3. member binding status read model

### Host

1. `src/Aevatar.Studio.Hosting`
2. `src/platform/Aevatar.GAgentService.Hosting`

建议分责：

1. `Aevatar.Studio.Hosting`
   承接 Studio member-centric HTTP 接口
2. `Aevatar.GAgentService.Hosting`
   继续承接底层 scope/service binding/runtime endpoint

一句话：

Studio 对上暴露 `member`，
runtime 对下继续复用 `service binding`。

---

## 4. 目标交付

本轮 backend 最终要交付：

1. member authority model
2. stable `publishedServiceId`
3. member-centric Studio API
4. member read model
5. member binding orchestration
6. migration / compatibility path

---

## 5. 分阶段实施

## Phase 0: 冻结 canonical member contract

目标：

先把 typed contract 和状态机写死，避免实现中继续漂移。

任务：

- [ ] 定义 `StudioMemberImplementationKind`
- [ ] 定义 `StudioMemberLifecycleStage`
- [ ] 定义 `StudioMemberRecord`
- [ ] 定义 `StudioMemberImplementationRef`
- [ ] 定义 `StudioMemberBindingStatus`
- [ ] 定义 `StudioMemberSummary`
- [ ] 定义 `StudioMemberDetail`

`StudioMemberRecord` 至少包含：

1. `memberId`
2. `scopeId`
3. `teamId?`
4. `displayName`
5. `description`
6. `implementationKind`
7. `implementationRef?`
8. `publishedServiceId`
9. `lifecycleStage`
10. `createdAt`
11. `updatedAt`

实现要求：

1. 核心语义必须强类型
2. 不允许把 `implementationRef` 做成无语义 bag
3. 当前仓库内可控事实优先建模为 typed 字段

验收：

1. member 的 identity / implementation / published service 三层关系在 typed contract 中一次定义清楚
2. 后续 API 和 read model 都只复用这套语义，不再重复发明

## Phase 1: 实现 member authority write-side

目标：

建立真正的 member 权威事实源。

任务：

- [ ] 选择 member authority 的 actor / state owner
- [ ] 实现 `CreateMember`
- [ ] 实现 `RenameMember`
- [ ] 实现 `UpdateMemberImplementation`
- [ ] 实现 `GetMemberAuthorityState`
- [ ] 明确 member lifecycle stage 变迁

约束：

1. `publishedServiceId` 必须在 `CreateMember` 时生成
2. 生成后直接持久化到权威状态
3. rename 不能改 `publishedServiceId`
4. 不允许通过 query-time 推导 `publishedServiceId`
5. `CreateMember` 不要求前端先传入现成 `implementationRef`
6. 若某种 implementation kind 需要初始 draft/ref，由后端在创建时自动生成或延后到 Build 阶段生成

建议：

- [ ] `publishedServiceId` 基于 immutable `memberId` 派生
- [ ] 生成一次后保存，不在 read 时重复计算

验收：

1. 后端存在真正 member 权威对象
2. `memberId -> publishedServiceId` 映射来自权威状态，不来自前端推断

## Phase 2: 新增 member current-state / roster read model

目标：

让 Studio 可以直接读 member roster，而不是再从 workflow/service/script 拼装伪 member。

任务：

- [ ] 新增 member current-state read model proto / typed document
- [ ] 新增 member roster projector
- [ ] 新增 member detail projector
- [ ] 新增 member binding status projector
- [ ] 新增 query port

建议 read models：

1. `StudioMemberCurrentStateDocument`
2. `StudioMemberRosterDocument`
3. `StudioMemberBindingStatusDocument`

实现要求：

1. read model version 必须来自同一个 member authority version
2. 不允许 query-time replay 组装 member roster
3. 不允许在 query 路径里临时读取 event store 还原 member

验收：

1. Studio 可以用 query port 直接拿 member list
2. read model 中已包含 `publishedServiceId`

## Phase 3: 新增 member-centric Studio APIs

目标：

给 Studio 提供真正围绕 member 的 API，而不是继续把 scope default binding 当主路径。

任务：

- [ ] 新增 `POST /api/scopes/{scopeId}/members`
- [ ] 新增 `GET /api/scopes/{scopeId}/members`
- [ ] 新增 `GET /api/scopes/{scopeId}/members/{memberId}`
- [ ] 新增 `PUT /api/scopes/{scopeId}/members/{memberId}/binding`
- [ ] 新增 `GET /api/scopes/{scopeId}/members/{memberId}/binding`

`POST /members` 输入至少支持：

1. `displayName`
2. `description`
3. `implementationKind`

说明：

1. 前端默认不传 raw `implementationRef`
2. 若后端需要初始 implementation shell，应由后端按 `implementationKind` 自动生成
3. `implementationRef` 只应作为返回事实或后续 build/update 结果出现，不应成为普通用户创建 member 的前置理解成本

`POST /members` 输出至少返回：

1. `memberId`
2. `displayName`
3. `implementationKind`
4. `publishedServiceId`
5. `lifecycleStage`

验收：

1. Studio 后续不需要再从 workflow/service API 组合出“member”
2. `memberId` 正式成为前端主路由主键

## Phase 4: 实现 member binding orchestration

目标：

把 member publish 语义接到现有 binding 能力上。

任务：

- [ ] `PUT /members/{memberId}/binding` 读取 member authority
- [ ] 解析 `publishedServiceId`
- [ ] 根据 member 当前 `implementationKind` 构造 binding request
- [ ] 调用现有 binding command，并显式传 `ServiceId = publishedServiceId`
- [ ] 返回 member-friendly binding result

复用要求：

1. 复用现有 `ScopeBindingCommandApplicationService`
2. 不重写 workflow/script/gagent binding 细节
3. 不新造第二套 revision publish 机制

member binding 返回值至少包含：

1. `memberId`
2. `publishedServiceId`
3. `revisionId`
4. `implementationKind`
5. `invoke contract summary`

验收：

1. member bind 不再落到 scope default service
2. 同一个 member 后续每次 bind 都复用同一个 `publishedServiceId`

## Phase 5: 兼容与迁移

目标：

在不打断现有运行时能力的前提下切入新模型。

任务：

- [ ] 保留现有 `/api/scopes/{scopeId}/binding`
- [ ] 保留 service-level runtime / revisions / runs 查询面
- [ ] 设计旧 Studio 路由兼容规则
- [ ] 设计历史 scope-bound service 到 member 的 bootstrap/backfill 路径
- [ ] 设计 `serviceId` 旧链接映射到 `memberId` 的过渡查询

要求：

1. compatibility path 只能是迁移桥，不得成为永久第二主语
2. 新增 member API 后，Studio 主路径必须优先走 member

验收：

1. 新老链接都可工作
2. Studio 新实现不再依赖 legacy scope default binding 语义

## Phase 6: 测试与门禁

目标：

避免 member 语义在后续演进里再次被 service/workflow 偷换。

任务：

- [ ] 为 member authority 补单元测试
- [ ] 为 member binding 补集成测试
- [ ] 为 `publishedServiceId` rename-safe 语义补测试
- [ ] 为 `workflow / script / gagent` 三种创建与绑定路径补测试
- [ ] 为旧 `serviceId` 兼容跳转补测试

建议新增测试断言：

1. create member 后必有稳定 `publishedServiceId`
2. rename member 后 `publishedServiceId` 不变
3. bind member 时显式传入 `ServiceId = publishedServiceId`
4. 不传 `ServiceId` 的旧 scope binding 路径仍可继续工作

---

## 6. 建议文件落位

建议新增或扩展：

### `src/Aevatar.Studio.Application`

1. `Studio/Contracts/MemberContracts.cs`
2. `Studio/Abstractions/IStudioMemberCommandPort.cs`
3. `Studio/Abstractions/IStudioMemberQueryPort.cs`
4. `Studio/Services/StudioMemberService.cs`

### `src/Aevatar.Studio.Projection`

1. `ReadModels/studio_member_readmodels.proto`
2. `Projectors/StudioMemberCurrentStateProjector.cs`
3. `Projectors/StudioMemberRosterProjector.cs`
4. `QueryPorts/ProjectionStudioMemberQueryPort.cs`

### `src/Aevatar.Studio.Hosting`

1. `Controllers/MembersController.cs`
   或
2. `Endpoints/StudioMemberEndpoints.cs`

### `src/platform/Aevatar.GAgentService.Application`

1. 复用现有 `Bindings/ScopeBindingCommandApplicationService.cs`
2. 必要时新增 member binding adapter，而不是重写 binding 逻辑

---

## 7. 一句话验收标准

当这份 checklist 完成后，Studio 后端应满足：

1. member 是权威主语
2. 每个 member 有稳定 `publishedServiceId`
3. Studio 能直接 create/list/get/bind member
4. workflow / script / gagent 都通过 member 主链路进入 bind/invoke
