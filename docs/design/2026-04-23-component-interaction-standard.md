# 组件交互标准

## 目标

统一 `aevatar-console-web` 中按钮和可交互组件的默认交互表现，避免同一项目里同时出现：

1. 没有 hover / active 反馈的自定义按钮
2. 异步动作没有 loading、防重复点击和错误提示
3. disabled 只是视觉变灰，但仍可触发点击
4. 页面不同区域使用完全不同的交互语言

## 默认标准

所有按钮 / 可交互组件默认必须满足：

1. 状态完整性
   - `default / hover / active / disabled / loading`
2. 交互反馈
   - hover 有明显视觉反馈
   - active 有按压反馈
3. 异步动作
   - loading 期间可见
   - loading 期间不可重复触发
4. 错误处理
   - 失败必须有用户可见反馈
   - 优先使用 `message.error(...)`，必要时在局部补 `Alert`
5. 动画
   - 所有状态切换都应有统一 `transition`
6. 可访问性
   - disabled 状态不可点击
   - keyboard focus 有可见反馈

## 当前落地方式

### 1. 统一样式类

在 `src/global.less` 中维护：

1. `.aevatar-interactive-button`
2. `.aevatar-interactive-chip`
3. `.aevatar-pressable-card`

它们负责：

1. hover
2. active
3. focus-visible
4. disabled / aria-disabled
5. loading 锁定

### 2. 统一类名出口

统一从：

`src/shared/ui/interactionStandards.ts`

引入 class name 常量，避免后续组件继续散写裸字符串。

### 3. 异步动作约束

凡是异步点击动作，默认必须：

1. 显示 loading
2. 使用本地 pending lock 防止重复点击
3. catch 后调用 `message.error(...)`
4. 如页面本身需要上下文解释，再同步写入局部 `Alert`

## 当前适用范围

本标准已优先落在当前 Studio 的关键工作流：

1. `StudioShell`
2. `Studio index inventory actions`
3. `StudioBuildPanels`

后续新增交互组件默认继续遵循这套标准，不再回到裸 `<button>` + 无状态反馈的写法。
