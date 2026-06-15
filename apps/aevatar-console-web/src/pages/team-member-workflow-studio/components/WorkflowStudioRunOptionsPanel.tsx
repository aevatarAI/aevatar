import { Button, Input, Space, Typography } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";

type WorkflowStudioRunOptionsPanelProps = {
  readonly onClose: () => void;
  readonly onRunMessageChange: (message: string) => void;
  readonly open: boolean;
  readonly runMessage: string;
  readonly width?: number;
};

const WorkflowStudioRunOptionsPanel: React.FC<WorkflowStudioRunOptionsPanelProps> = ({
  onClose,
  onRunMessageChange,
  open,
  runMessage,
  width = 420,
}) => {
  if (!open) {
    return null;
  }

  return (
    <aside
      aria-label={t(
        "teamMemberWorkflowStudio.runOptionsPanel.sectionAria",
        "Run options panel",
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
              {t("teamMemberWorkflowStudio.runOptionsPanel.title", "Run options")}
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
              "teamMemberWorkflowStudio.runOptionsPanel.messageLabel",
              "Draft run input",
            )}
          </Typography.Text>
          <Input.TextArea
            aria-label={t(
              "teamMemberWorkflowStudio.runOptionsPanel.messageLabel",
              "Draft run input",
            )}
            autoSize={{ minRows: 8, maxRows: 16 }}
            onChange={(event) => onRunMessageChange(event.target.value)}
            placeholder={t(
              "teamMemberWorkflowStudio.runOptionsPanel.messagePlaceholder",
              "Optional input sent to this workflow draft run",
            )}
            style={{
              marginTop: 8,
            }}
            value={runMessage}
          />
        </section>
      </div>
    </aside>
  );
};

export default WorkflowStudioRunOptionsPanel;
