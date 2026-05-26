---
title: "CLI Console Invoke Workbench PRD"
status: draft
owner: codex
last_updated: 2026-04-22
reference:
  - "/Users/xiezixin/Downloads/aevatar-console/project/js/app.jsx"
  - "/Users/xiezixin/Downloads/aevatar-console/project/js/chrome.jsx"
  - "/Users/xiezixin/Downloads/aevatar-console/project/js/invoke.jsx"
---

# CLI Console Invoke Workbench PRD

## 1. 结论

`Invoke` 页的正确目标不是“单独一块 invoke 表单”。

它应该是一个 `Service Workbench`，并且在视觉和交互结构上与
`/Users/xiezixin/Downloads/aevatar-console/project`
中的原型保持同构：

1. 顶部是 `Service Header + Build / Bind / Invoke / Observe`
2. 左侧是 `Playground`
3. 左下是 `Request history`
4. 右侧是 `AGUI events`
5. `AGUI events` 至少支持 `timeline / trace / tabs / bubbles / raw`

前一版 PRD 只把这页定义成“一个更诚实的 invoke 表单”，这不够。真正的用户心智是：

> “我想站在一个 service 的工作台里，发一次真实请求，然后立刻看到这次 run 到底发生了什么。”

## 2. 用户问题

当前页面让用户产生三个落差：

1. 没有 service 级上下文，像一个散装小工具，不像工作台。
2. 请求、历史、事件被拆散了，用户无法在一次视线里完成“发请求 -> 看 run -> 对比上一条”的闭环。
3. 页面虽然看起来在“调 service”，但没有把 `Invoke` 放在 `Build / Bind / Observe` 这条主线上，缺少阶段感和状态感。

## 3. 产品定位

`Invoke` 页是 `Console` 中某个 service 的运行工作台，不是纯 API console，也不是多轮聊天页。

它要同时满足三件事：

1. 能发一次真实 invoke
2. 能看懂这次 run 的过程
3. 能回看和比较最近几次请求

因此它不是：

1. 通用 JSON 调试台
2. 多轮 Chat 替身
3. 只展示原始事件 dump 的调试页

## 4. 真实能力边界

虽然页面形态要升级成 workbench，但请求契约仍然必须诚实：

1. 当前流式 invoke 只发送真实支持的字段：`prompt`、可选 `actorId`、可选 `headers`
2. `onboarding` 不是可真实 invoke 的 scope service
3. `command-only endpoint` 不应在这条流式 workbench 里伪装可调
4. `Raw` 仍然负责 typed payload 和低层 API

结论：

1. 页面要长得像 `Playground`
2. 但 playground 里能编辑或看到的内容，必须与真实请求一一对应
3. 不能再出现“看起来像任意 body，实际上只发 prompt”的欺骗感

## 5. 页面结构

## 5.1 Service Header

展示：

1. service 类型
2. service id
3. service 名称
4. 当前 endpoint / 当前状态
5. scope 与最近一次运行时间
6. 顶部动作：`Compare runs`、`Share`、`Invoke`

## 5.2 Stepper

展示四个阶段：

1. `Build`
2. `Bind`
3. `Invoke`
4. `Observe`

这里的价值不是做复杂路由，而是让用户知道自己当前在 service 生命周期的哪一步。

## 5.3 Playground

左侧 `Playground` 是发请求的主入口，必须包含：

1. 当前 transport path
2. invoke 协议提示
3. `Prompt`
4. 可选 `Actor ID`
5. 可选 `Headers`
6. `Actual request` 预览
7. 操作按钮：`Run`、`Stop`、`Load fixture`、`Replay last`、`Save request`

## 5.4 Request History

左下 `Request history` 是当前 service 的 invoke 历史轨。

每一项至少展示：

1. run id 或本地 session id
2. 发起时间
3. prompt 摘要
4. 本次结果摘要
5. 结果状态

点击某条历史后，右侧面板必须切换到那次 invoke 的事件和结果。

## 5.5 AGUI Events

右侧是统一事件面板，至少包含：

1. 顶部指标条：events / steps / tool calls / errors / elapsed / state
2. 事件视图切换：`timeline / trace / tabs / bubbles / raw`
3. 右侧摘要列：`Run summary + Response`

顺序上必须优先满足用户理解，而不是调试原始性：

1. 先让用户知道结果是否对
2. 再让用户知道过程是否对
3. 最后才是原始 frame

## 6. 关键交互

## 6.1 首次进入

1. 用户先看到 service header 和 stepper
2. 左侧能直接知道当前调的是哪个 service / 哪条 path
3. 右侧能直接知道这里会显示什么类型的运行过程

## 6.2 发起一次 invoke

1. 用户在 `Playground` 填 prompt
2. 点击 `Run` 或顶部 `Invoke`
3. 当前请求写入 `Request history`
4. 右侧 `AGUI events` 实时滚动
5. `Run summary` 和 `Response` 同步更新

## 6.3 回看历史

1. 用户点击 `Request history` 中任意一条
2. 左侧恢复该次请求内容
3. 右侧切换到该次 invoke 的 events / summary / response

## 6.4 遇到 human input

若 run 进入 `human_input`：

1. 页面必须明确标记“当前 run 已暂停，等待继续”
2. 应提示去 `Chat` 继续，而不是让用户误以为这里还能完整续跑

## 6.5 不支持的 service

如果当前 service 不是这条 streaming invoke 的合法目标：

1. 页面仍保留 workbench 外壳
2. 但 `Playground` 必须明确说明原因
3. 同时给出跳转动作：`Go to Chat` 或 `Open Raw`

## 7. 视觉与信息架构原则

1. 必须采用 service workbench 视觉，而不是中性管理后台卡片堆叠
2. 顶部上下文、左侧请求、右侧事件必须形成明显的运行台布局
3. `Request history` 与 `AGUI events` 要有明显“可对照”的关系
4. 页面默认就要让用户知道：
   - 我在调哪个 service
   - 这次发了什么
   - 这次 run 现在走到哪一步
   - 和上一条相比有什么不同

## 8. 验收标准

## 8.1 结构

1. `Invoke` 页采用 `Service Header + Stepper + Playground + Request History + AGUI Events` 布局
2. 视觉结构与参考原型同构，而不是只保留局部元素

## 8.2 诚实性

1. 页面展示的请求内容与真实发送 payload 一致
2. 不支持的 service / endpoint 不得伪装成可执行
3. `Request history` 不能显示假 run 数据；若是本地 session history，必须按真实 session 展示

## 8.3 可观察性

1. 发起 invoke 后，右侧至少能看到 timeline 事件视图
2. 用户可以切换 `timeline / trace / tabs / bubbles / raw`
3. `Run summary` 与 `Response` 在右侧固定可见

## 8.4 回看能力

1. 当前 service 的最近 invoke 必须进入历史轨
2. 点击历史项后，页面要回放对应请求与结果
3. `Replay last` 能基于上一条请求重新执行

## 9. 非目标

本轮不做：

1. 真正的跨 run 差异比较算法
2. command endpoint 的完整 typed payload builder
3. 全局跨 service 历史中心
4. 在 invoke workbench 内直接完成 workflow human input 续跑

这些能力可以作为下一轮，但这一轮必须先把 `Invoke` 做成一个真正可用、可理解、可回看的 service workbench。
