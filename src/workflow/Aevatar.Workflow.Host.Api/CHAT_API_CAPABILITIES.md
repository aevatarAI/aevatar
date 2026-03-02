# Chat API Capabilities (Host Entry)

本文件是 Host 侧入口说明。完整且唯一的能力文档请查看：

- `docs/workflow-chat-ws-api-capability.md`

## 快速结论

- `/api/chat`（SSE）与 `/api/ws/chat`（WebSocket）能力一致，仅传输协议不同。
- 支持 `workflowYamls > workflow > default(auto)` 的运行选择优先级。
- `workflow` 仅用于 file-backed workflow 名称查找；`workflowYamls` 仅用于 inline YAML bundle（首项入口）。
- 当 `workflow` 与 `workflowYamls` 同时出现时，固定使用 `workflowYamls`。
- 内建 `direct / auto / auto_review`：
  - `auto`：可根据 prompt 自动生成 workflow YAML，先经过强制校验，再进入 `human_approval`，审批通过即执行。
  - `auto_review`：同样支持多轮优化与审批，但审批通过后只定稿，不自动执行。
- Human-in-the-loop 通过：
  - `POST /api/workflows/resume`（恢复 `human_input/human_approval`）
  - `POST /api/workflows/signal`（恢复等待 signal 的步骤）

## 文档统一约定

- 规范内容以 `docs/workflow-chat-ws-api-capability.md` 为准。
- 本文件与 `README.md` 仅保留入口与摘要，避免多处重复维护导致漂移。
