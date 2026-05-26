---
title: "Create Team 草稿恢复 PRD"
status: draft
owner: tbd
last_updated: 2026-04-22
references:
  - "./2026-04-22-team-member-first-prd.md"
---

# Create Team 草稿恢复 PRD

## 1. 背景

当前 `Create Team -> Open Studio` 已经具备“新建 team 模式”的基础语义，但还缺一个关键闭环：

1. 用户在 Studio 里点击了 `保存草稿`
2. 系统确实保存了当前初始 member 的实现草稿
3. 但用户回到 `Create Team` 后，看不到“这次创建流程对应的已保存草稿”
4. 下次再进入 Studio，也不能明确恢复到这份草稿

结果是用户只能知道“保存成功了”，但不知道：

1. 草稿现在和哪次创建 team 流程绑定
2. 应该从哪里继续编辑
3. 再次点 `Open Studio` 是继续上次，还是又新开一份

---

## 2. 问题定义

当前系统里其实存在两种不同语义的草稿：

1. `member implementation draft`
   Studio 保存到 workspace/scope 的 workflow document
2. `create team draft`
   `teamName / initialMemberName / 当前这次创建流程对应的已保存实现草稿`

目前第 1 种草稿已经能保存；第 2 种草稿没有正式建模，只是部分信息停留在 URL 中。

---

## 3. 目标

本轮目标是补齐“创建 team 草稿恢复”闭环，但不新增独立后端表。

用户应该能够：

1. 在 Studio 里点击 `保存草稿`
2. 返回 `Create Team` 后，看见“当前创建流程已关联的草稿”
3. 再次点击 `Open Studio` 时，继续进入这份草稿
4. 明确区分“继续已有草稿”和“删除草稿”

---

## 4. 核心产品判断

本轮采用“最小正确模型”：

1. `保存草稿` 继续只保存当前初始 member 的实现定义
2. 创建 team 流程额外维护一个“草稿恢复指针”
3. 这个恢复指针属于创建 team 流程本身，不等于“当前正在浏览的 workflow”

恢复指针包含：

1. `teamName`
2. `initialMemberName`
3. `teamDraftWorkflowId`
4. `teamDraftWorkflowName`

其中：

1. `teamDraftWorkflowId` 只在成功点击 `保存草稿` 后更新
2. 用户在 Studio 里临时点开别的行为定义，不应污染这个指针

兼容说明：

如果现有前端仍使用 `entryName`，本期应把它解释为：

`initialMemberName`

---

## 5. 状态模型

### 5.1 Create Team 页面状态

`Create Team` 页面负责持有：

1. `teamName`
2. `initialMemberName`
3. 当前创建流程关联的已保存草稿 ID
4. 当前创建流程关联的已保存草稿名称

### 5.2 Studio 新建 Team 模式状态

Studio 新建 team 模式负责持有：

1. 当前正在编辑的初始 member 实现
2. `teamName`
3. `initialMemberName`
4. 当前创建流程的草稿恢复指针

这两组状态不能混淆：

1. “当前正在编辑的实现”可以变化
2. “草稿恢复指针”只能在保存成功后变化

---

## 6. 目标用户路径

### 6.1 首次创建

1. 用户进入 `Create Team`
2. 输入 `teamName` 与 `initialMemberName`
3. 点击 `Open Studio`
4. 进入一个新草稿
5. 点击 `保存草稿`
6. 返回 `Create Team`
7. 页面显示“已保存草稿：xxx”
8. 再次点击 `Open Studio`
9. 直接恢复到这份草稿

### 6.2 中断后继续

1. 用户此前已经保存过草稿
2. 再次回到 `Create Team`
3. 页面直接显示已保存草稿摘要
4. 用户点击 `继续编辑`
5. Studio 打开这份草稿，而不是新建空白草稿

---

## 7. 交互设计

### 7.1 Create Team 页面新增模块

在表单区下方增加一个轻量的“当前草稿”摘要卡。

有已保存草稿时展示：

1. 标题：`已保存草稿`
2. 草稿名称：`{teamDraftWorkflowName}`
3. 辅助说明：`这份草稿会作为当前团队创建流程的继续编辑入口`
4. 主动作：`继续编辑草稿`
5. 次动作：`Delete Draft`

### 7.2 Open Studio 语义

`Open Studio` 的行为改成：

1. 若存在 `teamDraftWorkflowId`，则恢复该草稿
2. 若不存在，则新建空白草稿

### 7.3 Save Draft 语义

在新建 team 模式下：

1. `保存草稿` 成功后
2. 更新“草稿恢复指针”
3. 允许用户返回创建页继续稍后处理

---

## 8. 路由与数据传递

本轮不新增后端 team draft API，先通过现有路由显式传递恢复指针。

### 8.1 Create Team 路由参数

新增：

1. `teamDraftWorkflowId`
2. `teamDraftWorkflowName`

说明：

1. `initialMemberName` 可继续沿用现有 `entryName` 参数名做兼容
2. 但产品语义必须改成“初始 member 名称”

### 8.2 Studio 路由参数

新增：

1. `teamDraftWorkflowId`
2. `teamDraftWorkflowName`

说明：

1. `workflow` 仍表示“当前打开的实现定义”
2. `teamDraftWorkflowId` 表示“当前创建流程的恢复指针”
3. 这两个字段不能混用

---

## 9. 规则

### 9.1 何时更新恢复指针

只允许在以下时机更新：

1. 新建 team 模式下点击 `保存草稿` 成功后

### 9.2 何时不能更新恢复指针

以下行为都不应更新恢复指针：

1. 只是切换了当前定义
2. 只是从已有行为复制但还没保存
3. 只是修改了 `initialMemberName`
4. 只是测试运行

---

## 10. 验收标准

1. `Create Team` 页面能展示当前创建流程关联的已保存草稿
2. `Open Studio` 能诚实表达“继续已有草稿”或“新建草稿”
3. `initialMemberName` 与 `workflowName` 保持独立语义
4. 草稿恢复不会把 team、member、service 混成同一个对象
