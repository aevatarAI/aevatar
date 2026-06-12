import { Button, Input, Space, Typography } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";

type WorkflowStudioDraftRunPanelProps = {
  readonly canRun: boolean;
  readonly disabledReason?: string;
  readonly onClose: () => void;
  readonly onRun: () => void;
  readonly onRunMessageChange: (message: string) => void;
  readonly open: boolean;
  readonly pending: boolean;
  readonly runMessage: string;
  readonly width?: number;
};

const WorkflowStudioDraftRunPanel: React.FC<WorkflowStudioDraftRunPanelProps> = ({
  canRun,
  disabledReason,
  onClose,
  onRun,
  onRunMessageChange,
  open,
  pending,
  runMessage,
  width = 420,
}) => {
  if (!open) {
    return null;
  }

  return (
    <aside
      aria-label={t(
        "teamMemberWorkflowStudio.draftRunPanel.sectionAria",
        "Draft run panel",
      )}
      style={{
        background: "#ffffff",
        borderLeft: "1px solid #e5e7eb",
        display: "flex",
        flexDirection: "column",
        flexShrink: 0,
        minHeight: 0,
        width,
      }}
    >
      <header
        style={{
          borderBottom: "1px solid #e5e7eb",
          padding: "16px 18px",
        }}
      >
        <Space align="start" style={{ justifyContent: "space-between", width: "100%" }}>
          <div style={{ minWidth: 0 }}>
            <Typography.Text strong style={{ color: "#111827", fontSize: 16 }}>
              {t("teamMemberWorkflowStudio.draftRunPanel.title", "Draft run")}
            </Typography.Text>
          </div>
          <Button onClick={onClose} size="small">
            {t("teamMemberWorkflowStudio.common.close", "Close")}
          </Button>
        </Space>
      </header>
      <div
        style={{
          display: "grid",
          gap: 14,
          overflow: "auto",
          padding: 18,
        }}
      >
        <section>
          <Typography.Text strong>
            {t(
              "teamMemberWorkflowStudio.draftRunPanel.messageLabel",
              "Draft run input",
            )}
          </Typography.Text>
          <Input.TextArea
            aria-label={t(
              "teamMemberWorkflowStudio.draftRunPanel.messageLabel",
              "Draft run input",
            )}
            autoSize={{ minRows: 8, maxRows: 16 }}
            onChange={(event) => onRunMessageChange(event.target.value)}
            placeholder={t(
              "teamMemberWorkflowStudio.draftRunPanel.messagePlaceholder",
              "Optional input sent to this workflow draft run",
            )}
            style={{
              marginTop: 8,
            }}
            value={runMessage}
          />
        </section>
        <div
          style={{
            display: "grid",
            gap: 8,
            justifyItems: "start",
          }}
        >
          <Button
            disabled={!canRun}
            icon={null}
            loading={pending}
            onClick={onRun}
            title={canRun ? undefined : disabledReason}
            type="primary"
          >
            {t(
              "teamMemberWorkflowStudio.draftRunPanel.startDraftRun",
              "Start draft run",
            )}
          </Button>
          {!canRun && disabledReason ? (
            <Typography.Text
              style={{
                color: "#6b7280",
                fontSize: 12,
              }}
            >
              {disabledReason}
            </Typography.Text>
          ) : null}
        </div>
      </div>
    </aside>
  );
};

export default WorkflowStudioDraftRunPanel;
