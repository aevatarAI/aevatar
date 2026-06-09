import { Alert, Button, Empty, List, Spin, Tag, Typography } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";
import type { StudioExecutionSummary } from "@/shared/studio/models";

type WorkflowStudioMemberRunsPanelProps = {
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

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
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

function readRunPreview(execution: StudioExecutionSummary): {
  label: string;
  value: string;
} | null {
  const error = trimOptional(execution.error);
  if (error) {
    return {
      label: t("teamMemberWorkflowStudio.runsPanel.preview.error", "Error"),
      value: error,
    };
  }

  const output = trimOptional(execution.output);
  if (output) {
    return {
      label: t("teamMemberWorkflowStudio.runsPanel.preview.output", "Output"),
      value: output,
    };
  }

  const prompt = trimOptional(execution.prompt);
  return prompt
    ? {
        label: t("teamMemberWorkflowStudio.runsPanel.preview.input", "Input"),
        value: prompt,
      }
    : null;
}

const WorkflowStudioMemberRunsPanel: React.FC<WorkflowStudioMemberRunsPanelProps> = ({
  emptyReason,
  error,
  executions,
  loading,
  onOpenExecution,
}) => (
  <section
    aria-label={t("teamMemberWorkflowStudio.runsPanel.sectionAria", "Member runs")}
    style={{
      background: "#ffffff",
      flex: 1,
      minHeight: 0,
      overflow: "auto",
      padding: 22,
    }}
  >
    <Typography.Title level={4} style={{ marginTop: 0 }}>
      {t("teamMemberWorkflowStudio.runsPanel.title", "Member runs")}
    </Typography.Title>
    <Typography.Paragraph style={{ color: "#6b7280", maxWidth: 760 }}>
      {t(
        "teamMemberWorkflowStudio.runsPanel.description",
        "This tab only shows runs with an explicit link to the current workflow member.",
      )}
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
        renderItem={(execution) => {
          const timingSummary = [
            formatExecutionTime(execution.startedAtUtc),
            formatDuration(execution.startedAtUtc, execution.completedAtUtc),
          ]
            .filter(Boolean)
            .join(" · ");
          const preview = readRunPreview(execution);

          return (
            <List.Item
              actions={[
                <Button
                  key="open"
                  onClick={() => onOpenExecution(execution.executionId)}
                  size="small"
                >
                  {t("teamMemberWorkflowStudio.runsPanel.openRun", "Open run")}
                </Button>,
              ]}
            >
              <List.Item.Meta
                description={
                  <div style={{ display: "grid", gap: 4 }}>
                    {timingSummary ? (
                      <Typography.Text style={{ color: "#6b7280", fontSize: 12 }}>
                        {timingSummary}
                      </Typography.Text>
                    ) : null}
                    {preview ? (
                      <Typography.Paragraph
                        ellipsis={{ rows: 2 }}
                        style={{
                          color: "#374151",
                          fontSize: 12,
                          margin: 0,
                        }}
                      >
                        {preview.label}: {preview.value}
                      </Typography.Paragraph>
                    ) : null}
                  </div>
                }
                title={
                  <span>
                    <Tag color={readStatusColor(execution.status)}>
                      {execution.status ||
                        t(
                          "teamMemberWorkflowStudio.runsPanel.unknownStatus",
                          "unknown",
                        )}
                    </Tag>
                    {execution.workflowName ||
                      t(
                        "teamMemberWorkflowStudio.runsPanel.fallbackName",
                        "Member run",
                      )}
                  </span>
                }
              />
            </List.Item>
          );
        }}
      />
    ) : (
      <Empty
        description={
          emptyReason ||
          t(
            "teamMemberWorkflowStudio.runsPanel.empty",
            "No runs are linked to this workflow member yet.",
          )
        }
        image={Empty.PRESENTED_IMAGE_SIMPLE}
      />
    )}
  </section>
);

export default WorkflowStudioMemberRunsPanel;
