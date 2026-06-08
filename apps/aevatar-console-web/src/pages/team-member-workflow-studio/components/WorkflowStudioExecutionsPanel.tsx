import { Alert, Button, Empty, List, Spin, Tag, Typography } from "antd";
import React from "react";
import type { StudioExecutionSummary } from "@/shared/studio/models";

type WorkflowStudioExecutionsPanelProps = {
  readonly emptyReason: string;
  readonly error: string;
  readonly executions: readonly StudioExecutionSummary[];
  readonly loading: boolean;
  readonly onOpenExecution: (executionId: string) => void;
};

function formatExecutionTime(value: string | null | undefined): string {
  if (!value) {
    return "";
  }

  const date = new Date(value);
  return Number.isFinite(date.getTime()) ? date.toLocaleString() : value;
}

function formatDuration(startValue: string, endValue: string | null): string {
  const start = Date.parse(startValue);
  const end = endValue ? Date.parse(endValue) : Date.now();
  if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) {
    return "";
  }

  const seconds = Math.round((end - start) / 1000);
  if (seconds < 60) {
    return `${seconds}s`;
  }

  const minutes = Math.floor(seconds / 60);
  return `${minutes}m ${seconds % 60}s`;
}

function readStatusColor(status: string): string {
  const normalized = status.trim().toLowerCase();
  if (normalized.includes("fail") || normalized.includes("error")) {
    return "red";
  }

  if (normalized.includes("success") || normalized.includes("complete")) {
    return "green";
  }

  if (normalized.includes("running") || normalized.includes("pending")) {
    return "processing";
  }

  return "default";
}

const WorkflowStudioExecutionsPanel: React.FC<WorkflowStudioExecutionsPanelProps> = ({
  emptyReason,
  error,
  executions,
  loading,
  onOpenExecution,
}) => (
  <section
    aria-label="Workflow executions"
    style={{
      background: "#ffffff",
      flex: 1,
      minHeight: 0,
      overflow: "auto",
      padding: 22,
    }}
  >
    <Typography.Title level={4} style={{ marginTop: 0 }}>
      Executions
    </Typography.Title>
    <Typography.Paragraph style={{ color: "#6b7280", maxWidth: 760 }}>
      This tab only shows executions that can be safely scoped to the current
      workflow member by stable workflow or service identifiers.
    </Typography.Paragraph>
    {error ? (
      <Alert message={error} showIcon type="error" />
    ) : loading ? (
      <div style={{ display: "grid", justifyItems: "center", padding: 48 }}>
        <Spin />
      </div>
    ) : executions.length ? (
      <List
        dataSource={[...executions]}
        renderItem={(execution) => (
          <List.Item
            actions={[
              <Button
                key="open"
                onClick={() => onOpenExecution(execution.executionId)}
                size="small"
              >
                Inspect
              </Button>,
            ]}
          >
            <List.Item.Meta
              description={[
                formatExecutionTime(execution.startedAtUtc),
                formatDuration(execution.startedAtUtc, execution.completedAtUtc),
                execution.serviceId ? `service ${execution.serviceId}` : "",
              ]
                .filter(Boolean)
                .join(" · ")}
              title={
                <span>
                  <Tag color={readStatusColor(execution.status)}>
                    {execution.status || "unknown"}
                  </Tag>
                  {execution.workflowName || "Workflow execution"}
                </span>
              }
            />
            <Typography.Text style={{ color: "#6b7280", fontSize: 12 }}>
              {execution.executionId}
            </Typography.Text>
          </List.Item>
        )}
      />
    ) : (
      <Empty
        description={
          emptyReason ||
          "No safely scoped executions are available for this workflow member."
        }
        image={Empty.PRESENTED_IMAGE_SIMPLE}
      />
    )}
  </section>
);

export default WorkflowStudioExecutionsPanel;
