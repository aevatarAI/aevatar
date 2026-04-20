---
title: "Create Team draft pointer 修复 PRD"
status: draft
owner: codex
last_updated: 2026-04-17
---

# Create Team draft pointer 修复 PRD

## 1. 背景

当前 `Create Team` 已经拆出了 `new / resume` 两条路径，但“saved draft” 仍然主要依赖 URL 查询参数。

这导致页面在视觉上已经像一个修好的恢复流，但底层状态模型仍然不稳定：

1. 从 Teams 首页重新进入 `Create Team` 时，saved draft 可能消失
2. `Resume Saved Draft` 可能把用户带到一个已经失效的 workflow
3. 页面展示的 saved draft 摘要可能被未保存的 `entryName` 修改污染
4. “已保存草稿摘要”和“当前正在填写的新表单”缺少稳定边界

## 2. 问题定义

本轮要修的不是文案问题，而是 `Create Team` 的恢复指针模型不成立。

具体逻辑问题：

1. `Create Team` 只从 URL 读 `teamDraftWorkflowId / teamDraftWorkflowName`
2. Teams 首页默认跳转 `/teams/new`，不会自动带回 saved draft
3. Studio 中如果目标 draft 不存在，会静默切到别的 workflow 或空白草稿
4. Studio 会把当前未保存的 `entryName` 持续写回 URL，导致返回 `Create Team` 时，resume 摘要看起来像“旧 draft 绑定了新入口名”
5. 页面缺少一个稳定的“已保存 draft 指针”，无法区分：
   1. 已保存且可恢复的草稿
   2. 当前正在编辑但尚未保存的表单状态

## 3. 目标

本轮目标是让 `Create Team` 的 saved draft 恢复链路真正成立。

用户应当能够：

1. 在 Studio 保存草稿后，从任意正常入口重新进入 `Create Team`，仍然看到恢复入口
2. 点击 `Resume Saved Draft` 时，只恢复那份真正已保存的 draft
3. 在 Studio 里临时改 `entryName` 但未保存时，不污染 saved draft 摘要
4. 当 saved draft 已失效时，得到明确降级结果，而不是静默跳到别的 workflow

## 4. 非目标

本轮不做：

1. 独立后端 team draft 存储
2. 独立后端多 draft 管理器
3. 删除 draft 的后端实现
4. 发布成功后的完整 Team Details 跳转闭环

## 5. 方案

### 5.1 新增本地恢复指针集合

新增前端本地存储模型 `create-team draft pointers`，每个指针字段包含：

1. `teamName`
2. `entryName`
3. `teamDraftWorkflowId`
4. `teamDraftWorkflowName`
5. `updatedAt`

该集合表示“当前浏览器里可恢复的已保存 Create Team drafts”，不等于 URL 当前正在编辑的输入。
前端还会额外维护一个当前选中的 `selectedWorkflowId`，用于决定默认 resume 哪一份。

### 5.2 Create Team 页面改成双源读取

`Create Team` 页面分两类状态：

1. URL 输入：
   1. 当前新建表单的 `teamName / entryName`
2. 稳定恢复指针集合：
   1. 已保存草稿的 `workflowId / workflowName / teamName / entryName`

恢复区优先展示稳定恢复指针集合，不再直接把 URL 上的未保存表单值当成 saved draft 事实。
如果本地存在多份 draft，页面必须允许用户显式选择要恢复的那一份。

### 5.3 Studio 只在保存成功后更新恢复指针

只有在 `保存草稿` 成功后，才 upsert 本地恢复指针集合中的对应项。

以下行为都不能更新恢复指针：

1. 修改 `entryName`
2. 切换 workflow
3. 测试运行
4. 只是打开编辑器但未保存

### 5.4 失效草稿不能静默 fallback

如果 `Resume Saved Draft` 指向的 workflow 已不存在：

1. 清空本地恢复指针
2. 清空页面中的 saved draft 状态
3. Studio 明确降级到新建草稿
4. 给用户一个可见提示，说明旧 draft 已失效

禁止继续静默切到另一个 workflow。

## 6. 交互结果

### 6.1 Teams Home -> Create Team

即使 URL 没有带任何查询参数，只要浏览器里存在稳定恢复指针集合，`Create Team` 也应显示：

1. `Create New Team`
2. `Resume Saved Draft`

### 6.2 Studio -> 返回创建页

返回 `Create Team` 后：

1. resume 区显示所有可恢复的 saved drafts
2. 当前未保存的 `entryName` 只影响当前表单，不影响任一 saved draft 摘要
3. 新保存的 draft 只会追加或更新自己的那一项，不会覆盖其他 drafts

### 6.3 失效 draft

用户尝试恢复失效 draft 时：

1. 不得跳到别的 workflow
2. 不得继续显示旧 resume 摘要
3. 应回到新的空白 draft，并提示旧草稿已不可恢复

## 7. 验收标准

### 7.1 可恢复性

1. 从 Teams 首页进入 `Create Team` 时，能读取本地恢复指针集合并显示 resume 区
2. 当存在多份 drafts 时，旧 draft 不会被新 draft 覆盖
3. `Resume Saved Draft` 会把用户带回被选中的那份已保存 workflow

### 7.2 语义正确性

1. 未保存的 `entryName` 修改不会污染 saved draft 摘要
2. saved draft 摘要展示的是“最后一次保存成功”的元信息

### 7.3 失效处理

1. 当 resume 的 workflow 不存在时，Studio 不会静默切换到别的 workflow
2. 本地恢复指针会被清掉
3. 页面会回到新建态

## 8. 预计修改范围

1. `apps/aevatar-console-web/src/pages/teams/new.tsx`
2. `apps/aevatar-console-web/src/pages/studio/index.tsx`
3. `apps/aevatar-console-web/src/pages/teams/home.tsx`
4. `apps/aevatar-console-web/src/shared/*` 中新增 create-team draft pointer helper
5. 相关测试
