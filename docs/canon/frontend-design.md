---
title: "Aevatar 前端设计基线"
status: active
owner: potter
---

# Aevatar 前端设计基线

本文档定义 Aevatar 仓库内前端实现的默认设计口径。凡是页面、组件、控制台、playground、样式系统、视觉 polish 或前端重构任务，默认以本文为准。

## 1. 适用范围

当前仓库内的主要前端工作面包括：

- `tools/Aevatar.Tools.Cli/Frontend`：CLI playground React/Vite 源码。
- `tools/Aevatar.Tools.Cli/wwwroot`：CLI host 分发的静态产物。
- `demos/Aevatar.Demos.Workflow.Web/wwwroot`：Demo Web 静态 playground。
- `apps/aevatar-console-web`：控制台 Web 应用。

如果未来新增前端宿主，也默认继承本文档；只有该宿主存在更高优先级的局部设计文档时，才允许局部覆盖。

## 2. 默认设计立场

### 2.1 先定方向，再写界面

前端实现前，必须先回答三个问题：

1. 这是给谁用的？
2. 这个工作面最重要的交互是什么？
3. 这一屏最让人记住的视觉特征是什么？

允许的风格可以很大胆，也可以很克制，但必须单一、明确、连续。  
可选方向例如：editorial、industrial、warm technical、brutalist、refined minimal、retro-futurist。  
禁止把多种弱风格混合成“看起来像模板站”的中间态。

### 2.2 连续性优先于炫技

在已有产品表面上工作时，优先保持：

- 信息架构不变
- 核心导航不变
- 主要操作流程不变
- 领域术语不变

设计提升应主要体现在层次、比例、排版、状态、密度控制、质感和动效，而不是随意重排用户已经形成肌肉记忆的结构。

## 3. 明确禁止项

以下模式在仓库内默认视为低质量实现，应避免作为默认方案：

- 以 `Inter`、`Arial`、`Roboto`、宽泛 `system-ui` 作为首选字体栈
- 紫白渐变默认主题
- 千篇一律的 SaaS 卡片墙和统计卡堆叠
- 没有层次变化的浅灰边框 + 白底面板拼接
- 为了“看起来现代”而堆砌玻璃态、发光和悬浮阴影
- 缺少主题 token、完全依赖散落硬编码颜色和 spacing
- 只追求截图好看，不考虑真实内容密度、滚动状态和空/错/载入态

如确有历史兼容或局部延续需求，必须说明为什么不能收敛到更有辨识度的方案。

## 4. 设计系统要求

### 4.1 Token 优先

颜色、字体、字号、间距、圆角、阴影、边框、动效时长与 easing，优先收敛为：

- CSS variables
- theme tokens
- 可复用样式原语

禁止在多个页面中长期复制相近但不相等的硬编码值。

### 4.2 字体策略

- 优先选择有性格的 display font 搭配克制的正文字体。
- 如果工作面受现有框架或设计系统限制，允许局部保守，但仍应提升字重、字阶、行高和密度控制。
- 控制台类产品要优先保证可读性，再追求风格化。

### 4.3 版式与层次

- 优先通过留白、对齐、尺寸级差、色块关系建立层次，不靠额外说明文字补层次。
- 允许不对称布局、强调区、分层背景、局部高对比，但必须服务于任务流。
- 面板、侧栏、工作区和检查区要有清晰主次，避免全部元素同权重。

### 4.4 动效

- 动效必须有职责：进入、聚焦、反馈、切换、状态确认。
- 允许少量高质量的 page-load 或 panel transition。
- 禁止到处堆 hover 特效和无意义的微动效。

## 5. 按工作面执行的规则

## 5.1 CLI Playground

`tools/Aevatar.Tools.Cli/Frontend` 是 playground 的权威源码。

要求：

- 优先从共享 token、排版和 panel 原语入手，不做一次性“补丁式美化”
- 修改后必须同步生成 `tools/Aevatar.Tools.Cli/wwwroot`
- 必须确保 `demos/Aevatar.Demos.Workflow.Web/wwwroot` 与 CLI playground 产物保持一致

## 5.2 Console Web

`apps/aevatar-console-web` 运行在既有控制台壳之上。

要求：

- 默认在现有 Ant Design Pro shell 内做 refinement
- 除非用户明确要求大改，否则不推翻全局布局和导航组织
- 强调页面层级、阅读节奏、表单/面板状态和高频操作可见性

## 5.3 Demo Web

`demos/Aevatar.Demos.Workflow.Web/wwwroot` 主要承担演示和体验验证职责。

要求：

- 与 CLI playground 保持资产和核心样式一致
- 若存在 Demo 特有按钮或壳层差异，必须限制在 demo 边界内，不反向污染主源码结构

## 6. 质量门槛

前端实现默认要满足以下要求：

- 桌面端与移动端都能正常加载和操作
- 键盘导航可达
- 真实内容密度下仍然可读
- 空态、加载态、错误态至少具备基本视觉处理
- 不为了风格牺牲表单、编辑器、图形工作区等核心交互的可用性

## 7. 验证要求

修改 `tools/Aevatar.Tools.Cli/Frontend` 后，至少执行：

```bash
pnpm -C tools/Aevatar.Tools.Cli/Frontend exec tsc -b
pnpm -C tools/Aevatar.Tools.Cli/Frontend exec vite build --outDir tools/Aevatar.Tools.Cli/wwwroot
bash tools/ci/playground_asset_drift_guard.sh
```

修改 `apps/aevatar-console-web` 后，至少执行与改动相称的前端构建校验，例如：

```bash
npm --prefix apps/aevatar-console-web run tsc
npm --prefix apps/aevatar-console-web run build
```

如果任务只涉及文档或规则更新，至少执行文档 lint，确保权威文档结构合法。
