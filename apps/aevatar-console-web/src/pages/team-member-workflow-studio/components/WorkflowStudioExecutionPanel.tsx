import { Alert, Typography } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";
import { buildExecutionTrace } from "@/shared/studio/execution";
import type { StudioExecutionDetail } from "@/shared/studio/models";

type WorkflowStudioExecutionPanelProps = {
  readonly detail: StudioExecutionDetail | null;
  readonly error?: string;
};

const WorkflowStudioExecutionPanel: React.FC<WorkflowStudioExecutionPanelProps> = ({
  detail,
  error,
}) => {
  const trace = React.useMemo(() => buildExecutionTrace(detail), [detail]);
  const rawFrames = detail?.frames ?? [];
  const hasExecutionContent = Boolean(error || detail);

  if (!hasExecutionContent) {
    return null;
  }

  return (
    <aside
      aria-label={t(
        "teamMemberWorkflowStudio.executionPanel.consoleAria",
        "Member run console",
      )}
      style={{
        background: "#ffffff",
        borderTop: "1px solid #e5e7eb",
        display: "grid",
        flex: "0 0 210px",
        gap: 10,
        gridTemplateRows: "minmax(0, 1fr)",
        minHeight: 0,
        padding: "14px 18px",
      }}
    >
      <section
        data-testid="member-run-result-panel"
        style={{
          display: "grid",
          gap: 8,
          gridTemplateRows: "auto minmax(0, 1fr)",
          minHeight: 0,
          minWidth: 0,
        }}
      >
        {error ? (
          <Alert message={error} showIcon type="error" />
        ) : detail ? (
          <>
            <Typography.Text strong>{detail.workflowName}</Typography.Text>
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
                  maxHeight: 84,
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
                maxHeight: 130,
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
                        style={{
                          color: "#374151",
                          fontSize: 12,
                          margin: "3px 0 0",
                        }}
                      >
                        {log.previewText}
                      </Typography.Paragraph>
                    ) : null}
                  </div>
                ))
              ) : (
                <Typography.Text style={{ color: "#6b7280", fontSize: 12 }}>
                  {rawFrames.length
                    ? t(
                        "teamMemberWorkflowStudio.executionPanel.rawFrames",
                        "{count} run event(s) received, but no step logs are available yet.",
                        { count: rawFrames.length },
                      )
                    : t(
                        "teamMemberWorkflowStudio.executionPanel.emptyLogs",
                        "Run logs will appear here after the active member returns events.",
                      )}
                </Typography.Text>
              )}
            </div>
          </>
        ) : null}
      </section>
    </aside>
  );
};

export default WorkflowStudioExecutionPanel;
