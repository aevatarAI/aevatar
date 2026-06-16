# Chat API Capabilities (Host Entry)

本文件是 Host 侧入口说明。完整且唯一的能力文档请查看：

- `docs/canon/chat-api.md`

## 快速结论

- `/api/chat`（SSE）与 `/api/ws/chat`（WebSocket）能力一致，仅传输协议不同。
- `/api/chat` 支持 `application/json` 与 `multipart/form-data`；multipart producer 只负责把单个 `file` 表单文件写入 artifact store，并把返回的 typed `WorkflowFileRef` 注入既有 input part。
- multipart 默认允许 image/audio/video 与 PDF、DOCX、CSV、plain text、markdown、XLSX；非 image/audio/video 的允许类型统一进入 `inputParts[].type = "file"`。
- 支持 typed `source`、`workflowYamls`、`workflow` 与 `default(auto)` 的运行选择；`default(auto)` 仅在未提供 source/workflow/workflowYamls 时触发。
- `workflow` 用于已注册 workflow 名称查找（内建 + 文件加载）；`workflowYamls` 仅用于 inline YAML bundle（首项入口）。
- 当 `workflow` 与 `workflowYamls` 同时出现时，固定使用 `workflowYamls`。
- 内建 `direct / auto / auto_review`：
  - `auto`：可根据 prompt 自动生成 workflow YAML，先经过强制校验，再进入 `human_approval`，审批通过即执行。
  - `auto_review`：同样支持多轮优化与审批，但审批通过后只定稿，不自动执行。
- Human-in-the-loop 通过：
  - `POST /api/workflows/resume`（恢复 `human_input/human_approval`）
  - `POST /api/workflows/signal`（恢复等待 signal 的步骤）
- multipart 上传在 Host/API 边界 fail closed：先校验 caller credential、文件数量、字段名、大小与媒体类型；actor-facing input 只保留 `fileRef`，不携带 bytes/base64。

## 文档统一约定

- 规范内容以 `docs/canon/chat-api.md` 为准。
- 本文件与 `README.md` 仅保留入口与摘要，避免多处重复维护导致漂移。
