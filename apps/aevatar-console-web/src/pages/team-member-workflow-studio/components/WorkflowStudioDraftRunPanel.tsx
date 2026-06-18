import { InfoCircleOutlined, PlayCircleOutlined } from "@ant-design/icons";
import { Button, Input, Typography } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";
import WorkflowStudioSidePanel from "./WorkflowStudioSidePanel";

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
    <WorkflowStudioSidePanel
      ariaLabel={t(
        "teamMemberWorkflowStudio.draftRunPanel.sectionAria",
        "Draft run panel",
      )}
      bodyStyle={{
        display: "flex",
        flexDirection: "column",
        gap: 0,
        overflow: "hidden",
        padding: 0,
      }}
      closeAriaLabel={t(
        "teamMemberWorkflowStudio.draftRunPanel.closeAria",
        "Close draft run panel",
      )}
      onClose={onClose}
      title={
        <span style={{ alignItems: "center", display: "inline-flex", gap: 8 }}>
          <PlayCircleOutlined />
          <span>{t("teamMemberWorkflowStudio.draftRunPanel.title", "Draft run")}</span>
        </span>
      }
      width={width}
    >
      <div
        style={{
          alignContent: "start",
          display: "grid",
          flex: "1 1 auto",
          gap: 28,
          minHeight: 0,
          overflow: "auto",
          padding: "28px 24px 24px",
        }}
      >
        <section
          style={{
            display: "grid",
            gap: 12,
          }}
        >
          <div style={{ display: "grid", gap: 4 }}>
            <Typography.Text strong>
              {t(
                "teamMemberWorkflowStudio.draftRunPanel.messageLabel",
                "Draft run input",
              )}
            </Typography.Text>
            <Typography.Text style={{ color: "#64748b" }}>
              {t(
                "teamMemberWorkflowStudio.draftRunPanel.emptyInputHint",
                "Leave blank to run this draft without user input.",
              )}
            </Typography.Text>
          </div>
          <Input.TextArea
            aria-label={t(
              "teamMemberWorkflowStudio.draftRunPanel.messageLabel",
              "Draft run input",
            )}
            autoSize={{ minRows: 7, maxRows: 10 }}
            onChange={(event) => onRunMessageChange(event.target.value)}
            placeholder={t(
              "teamMemberWorkflowStudio.draftRunPanel.messagePlaceholder",
              "Optional input sent to this workflow draft run",
            )}
            style={{ fontSize: 15 }}
            value={runMessage}
          />
        </section>

        <div
          style={{
            alignItems: "center",
            background: "#f8fafc",
            border: "1px solid #e5e7eb",
            borderRadius: 4,
            color: "#475569",
            display: "grid",
            fontSize: 12,
            gap: 8,
            gridTemplateColumns: "auto minmax(0, 1fr)",
            padding: "10px 12px",
          }}
        >
          <InfoCircleOutlined />
          <Typography.Text style={{ color: "inherit", fontSize: 12 }}>
            {t(
              "teamMemberWorkflowStudio.draftRunPanel.filesBackendPendingNotice",
              "File input for draft runs is pending backend support.",
            )}
          </Typography.Text>
        </div>
      </div>

      <div
        style={{
          borderTop: "1px solid #e5e7eb",
          display: "grid",
          gap: 10,
          padding: "20px 24px 24px",
        }}
      >
        <Button
          disabled={!canRun}
          icon={<PlayCircleOutlined />}
          loading={pending}
          onClick={onRun}
          size="large"
          style={{
            boxShadow: canRun ? "0 12px 24px rgba(15, 23, 42, 0.14)" : undefined,
            height: 54,
            width: "100%",
          }}
          title={canRun ? undefined : disabledReason}
          type="primary"
        >
          {t(
            "teamMemberWorkflowStudio.draftRunPanel.startDraftRun",
            "Start draft run",
          )}
        </Button>
        {!canRun && disabledReason ? (
          <Typography.Text style={{ color: "#6b7280", fontSize: 12 }}>
            {disabledReason}
          </Typography.Text>
        ) : null}
      </div>
    </WorkflowStudioSidePanel>
  );
};

export default WorkflowStudioDraftRunPanel;
