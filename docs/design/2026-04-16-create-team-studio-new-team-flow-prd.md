---
title: "Create Team -> Studio 新团队流程 PRD"
status: draft
owner: tbd
last_updated: 2026-04-22
references:
  - "./2026-04-22-team-member-first-prd.md"
---

# Create Team -> Studio 新团队流程 PRD

## 1. 背景

当前 `Create Team` 页面里的 `Open Studio`，实际打开的是一个通用 workflow 新草稿编辑器，而不是“创建 team + 进入初始成员工作流”的专属流程。

这导致用户在“我要创建一个新 team”的心智下，进入了一个混合了以下语义的页面：

1. 当前空白草稿
2. 当前 scope 的既有 binding
3. 当前 scope 的既有 service
4. 当前 scope 下的历史 workflow 列表

但按最新产品基线：

1. `scope` 下有很多 `team`
2. `team` 下有很多 `member`
3. 只有 `member` 能被 invoke
4. `team` 最多只维护一份默认路由配置

所以 `Create Team` 的正确目标，不是“创建一个团队入口”，而是：

1. 在当前 scope 下创建一个 team
2. 为它定义一个初始 member
3. 必要时把这个初始 member 设为默认路由目标
4. 进入 Studio 继续完成这个 member 的实现

---

## 2. 问题定义

当前体验的核心问题不是单个按钮文案，而是“创建 team 流程”和“通用 Studio 编辑器”被混成了一个入口。

具体问题如下：

1. `Create Team -> Open Studio` 打开的不是“新 team 创建流程”，而是“新 workflow 草稿”
2. 新建团队场景下仍然展示当前 scope 的已发布 service / binding 信息，容易让用户误以为自己在编辑现有 team
3. 页面同时展示 `draft`、既有入口名、`serviceId`，但没有解释边界
4. `保存` 与“完成团队创建”的语义区分不清
5. 用户不知道自己现在是在创建 team，还是只是在写一个实现草稿

---

## 3. 目标

`Create Team` 的目标应该是：让用户以最小理解成本完成“从无到有创建一个新 team，并进入它的初始 member 实现流”。

这条流程必须满足：

1. 用户清楚知道自己在创建 `team`，不是在编辑旧 team
2. 用户清楚区分 `teamName`、`initialMemberName`、`workflow 草稿名`、`serviceId`
3. 在用户点击完成前，当前操作不会影响任何既有 team
4. 完成动作执行后，系统给出明确反馈，并进入新 team 的详情页

---

## 4. 核心产品判断

1. `Create Team` 应该是一个 team 创建向导入口，不是 Studio 的别名入口
2. Studio 在这条路径里应该进入“新建 team 模式”，而不是普通编辑模式
3. 新建 team 模式下，页面主语应是“待创建的 team + 初始 member”，不是“当前 scope 的已有 binding”
4. 如果用户需要默认路由，就在这条路径里显式指定“当前初始 member 是否作为默认路由目标”

---

## 5. 用户故事

作为一个想创建新 team 的用户：

1. 我点击 `Create Team`
2. 我输入 team 的基本信息
3. 我输入初始 member 的名称
4. 我选择从空白开始，或者从已有行为复制
5. 我进入 Studio 完成这个初始 member 的实现
6. 我保存草稿
7. 我完成 team 创建
8. 我进入刚创建完成的 Team Detail

---

## 6. 信息架构

新建 team 流程建议拆成两层。

## 6.1 第 1 层：Create Team 向导

页面只负责收集最少必要信息：

1. `teamName`
2. `initialMemberName`
3. 创建方式

创建方式只保留两个主选项：

1. 从空白开始
2. 从已有行为复制

## 6.2 第 2 层：Studio 新建 Team 模式

进入 Studio 后，页面主语应切换成：

1. 正在创建的新 team
2. 当前初始 member 草稿
3. 当前 member 是否将作为默认路由目标
4. 完成前不会影响已有 team

不应再以“当前 scope 的既有 service / binding”作为页面主语。

---

## 7. 页面与状态设计

## 7.1 Create Team 页面

页面应包含：

1. 标题：`创建新团队`
2. 简短说明：`先定义团队名称和初始成员，再进入 Studio 完成细节`
3. 表单区：team name、initial member name、创建方式
4. 主动作：`进入 Studio`
5. 次动作：`取消`

## 7.2 Studio 新建 Team 模式

顶部必须明确表达当前状态：

1. 主标题：`正在创建团队「{teamName}」`
2. 副标题：`当前成员草稿：{initialMemberName}`
3. 状态提示：`尚未完成创建，不会影响已有团队`

右侧主动作建议改为：

1. `保存草稿`
2. `测试当前成员`
3. `完成团队创建`

## 7.3 完成创建确认

完成前展示一个轻量确认层：

1. team 名称
2. 初始 member 名称
3. 是否设为默认路由目标
4. 来源：空白 / 复制自某个行为
5. 当前 scope

## 7.4 创建成功

成功后要明确告诉用户：

1. 新 team 已创建
2. 初始 member 已创建
3. 默认路由是否已配置
4. 已跳转至 Team Detail

---

## 8. 新建 Team 模式下必须隐藏或降级的内容

以下内容不应以主信息出现：

1. 当前 scope 的既有默认 service 摘要
2. `更新现有默认路由`
3. 既有 team 的运行状态摘要
4. 将当前草稿与既有 service 强绑定的文案

以下内容可以保留，但必须降级为二级操作：

1. 查看已有 workflow 列表
2. 从已有行为复制
3. 查看既有 team 详情

---

## 9. 字段语义边界

以下字段必须在 UI 上明确分开：

1. `teamName`
   表达用户理解的 team 名称
2. `initialMemberName`
   表达当前 team 的初始成员名称
3. `workflow 草稿名`
   表达当前编辑中的实现定义名称
4. `serviceId`
   仅是内部实现标识，不应在创建 team 主链路里主展示

兼容说明：

若现有前端仍在路由或状态中使用 `entryName`，本期应把它重新解释为：

`initialMemberName`

---

## 10. 推荐文案

顶部文案：

1. `正在创建团队`
2. `当前成员草稿`
3. `完成前不会影响已有团队`

按钮文案：

1. `进入 Studio`
2. `保存草稿`
3. `完成团队创建`
4. `从已有行为复制`
5. `返回创建页`

---

## 11. 建议用户路径

### 11.1 从空白创建

1. 用户点击 `Create Team`
2. 输入 `teamName`
3. 输入 `initialMemberName`
4. 选择 `从空白开始`
5. 点击 `进入 Studio`
6. 在 Studio 新建 team 模式中编辑这个初始 member
7. 点击 `保存草稿`
8. 点击 `完成团队创建`
9. 跳转到新 Team Detail

### 11.2 从已有行为复制

1. 用户点击 `Create Team`
2. 输入 `teamName`
3. 输入 `initialMemberName`
4. 选择 `从已有行为复制`
5. 选择来源行为
6. 点击 `进入 Studio`
7. 在 Studio 中继续编辑该初始 member
8. 点击 `完成团队创建`
9. 跳转到新 Team Detail

---

## 12. 验收标准

1. 用户不会把“新建 team”误解为“编辑现有 team”
2. 用户能明确知道“保存草稿”不会影响已存在的 team
3. 用户能明确知道“完成团队创建”才会真正创建 team 和初始 member
4. 新建 team 模式下不主展示 `serviceId`
5. 创建成功后跳转到新 Team Detail，而不是停留在混合编辑态
