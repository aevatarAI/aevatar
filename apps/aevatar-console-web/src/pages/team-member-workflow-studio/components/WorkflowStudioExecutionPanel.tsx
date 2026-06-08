import { Alert, Input, Space, Tag, Typography } from "antd";
import React from "react";
import { buildExecutionTrace } from "@/shared/studio/execution";
import type { StudioExecutionDetail } from "@/shared/studio/models";

type WorkflowExecutionStatus = "idle" | "running" | "succeeded" | "failed";

type WorkflowStudioExecutionPanelProps = {
  readonly detail: StudioExecutionDetail | null;
  readonly error?: string;
  readonly onPromptChange: (prompt: string) => void;
  readonly prompt: string;
  readonly status: WorkflowExecutionStatus;
};

function formatExecutionTime(value: string | null | undefined): string {
  if (!value) {
    return "";
  }

  const date = new Date(value);
  return Number.isFinite(date.getTime()) ? date.toLocaleString() : value;
}

function readStatusColor(status: WorkflowExecutionStatus): string {
  switch (status) {
    case "failed":
      return "red";
    case "running":
      return "processing";
    case "succeeded":
      return "green";
    default:
      return "default";
  }
}

const WorkflowStudioExecutionPanel: React.FC<WorkflowStudioExecutionPanelProps> = ({
  detail,
  error,
  onPromptChange,
  prompt,
  status,
}) => {
  const trace = React.useMemo(() => buildExecutionTrace(detail), [detail]);
  const rawFrames = detail?.frames ?? [];

  return (
    <aside
      aria-label="Workflow execution console"
      style={{
        background: "#ffffff",
        borderTop: "1px solid #e5e7eb",
        display: "grid",
        flex: "0 0 210px",
        gap: 12,
        gridTemplateColumns: "minmax(280px, 360px) minmax(0, 1fr)",
        minHeight: 0,
        padding: "14px 18px",
      }}
    >
      <section style={{ display: "grid", gap: 8, minWidth: 0 }}>
        <Space align="center" size={8} wrap>
          <Typography.Text strong>Workflow run</Typography.Text>
          <Tag color={readStatusColor(status)}>{status}</Tag>
        </Space>
        <Input.TextArea
          aria-label="Execution prompt"
          autoSize={{ minRows: 3, maxRows: 4 }}
          onChange={(event) => onPromptChange(event.target.value)}
          placeholder="Prompt for a whole-workflow draft run"
          value={prompt}
        />
        <Typography.Text style={{ color: "#6b7280", fontSize: 12 }}>
          Execute workflow runs the current draft as a whole workflow. Step-level
          execution remains unavailable until backend semantics support it.
        </Typography.Text>
      </section>
      <section
        style={{
          borderLeft: "1px solid #eef0f4",
          display: "grid",
          gap: 8,
          minHeight: 0,
          minWidth: 0,
          paddingLeft: 16,
        }}
      >
        {error ? (
          <Alert message={error} showIcon type="error" />
        ) : detail ? (
          <>
            <Space size={8} wrap>
              <Typography.Text strong>{detail.workflowName}</Typography.Text>
              <Typography.Text style={{ color: "#6b7280", fontSize: 12 }}>
                {detail.executionId}
              </Typography.Text>
              {detail.startedAtUtc ? (
                <Typography.Text style={{ color: "#6b7280", fontSize: 12 }}>
                  {formatExecutionTime(detail.startedAtUtc)}
                </Typography.Text>
              ) : null}
            </Space>
            {detail.error ? (
              <Alert message={detail.error} showIcon type="error" />
            ) : null}
            {detail.output ? (
              <pre
                style={{
                  background: "#f8fafc",
                  border: "1px solid #e5e7eb",
                  borderRadius: 6,
                  margin: 0,
                  maxHeight: 72,
                  overflow: "auto",
                  padding: 10,
                  whiteSpace: "pre-wrap",
                }}
              >
                {detail.output}
              </pre>
            ) : null}
            <div
              style={{
                display: "grid",
                gap: 6,
                maxHeight: 112,
                overflow: "auto",
              }}
            >
              {trace?.logs.length ? (
                trace.logs.map((log) => (
                  <div
                    key={[
                      log.timestamp,
                      log.title,
                      log.meta,
                      log.stepId ?? "",
                      log.previewText,
                    ].join(":")}
                    style={{
                      border: "1px solid #eef0f4",
                      borderRadius: 6,
                      padding: "7px 9px",
                    }}
                  >
                    <Typography.Text strong style={{ fontSize: 12 }}>
                      {log.title}
                    </Typography.Text>
                    {log.meta ? (
                      <Typography.Text style={{ color: "#6b7280", fontSize: 12 }}>
                        {" "}
                        {log.meta}
                      </Typography.Text>
                    ) : null}
                    {log.previewText ? (
                      <Typography.Paragraph
                        style={{ color: "#374151", fontSize: 12, margin: "3px 0 0" }}
                      >
                        {log.previewText}
                      </Typography.Paragraph>
                    ) : null}
                  </div>
                ))
              ) : (
                <Typography.Text style={{ color: "#6b7280", fontSize: 12 }}>
                  {rawFrames.length
                    ? `${rawFrames.length} raw execution frame(s) received.`
                    : "Execution logs will appear here after a workflow run returns frames."}
                </Typography.Text>
              )}
            </div>
          </>
        ) : (
          <Typography.Text style={{ color: "#6b7280", fontSize: 12 }}>
            No workflow execution has been started in this editor session.
          </Typography.Text>
        )}
      </section>
    </aside>
  );
};

export default WorkflowStudioExecutionPanel;
