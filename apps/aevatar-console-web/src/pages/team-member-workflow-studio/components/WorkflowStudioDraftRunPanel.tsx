import { PlayCircleOutlined } from "@ant-design/icons";
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
        alignContent: "start",
        gap: 14,
        gridAutoRows: "max-content",
        overflow: "auto",
      }}
      closeAriaLabel={t(
        "teamMemberWorkflowStudio.draftRunPanel.closeAria",
        "Close draft run panel",
      )}
      onClose={onClose}
      title={t("teamMemberWorkflowStudio.draftRunPanel.title", "Draft run")}
      width={width}
    >
      <section
        style={{
          display: "grid",
          gap: 10,
        }}
      >
        <div style={{ display: "grid", gap: 3 }}>
          <Typography.Text strong>
            {t(
              "teamMemberWorkflowStudio.draftRunPanel.messageLabel",
              "Draft run input",
            )}
          </Typography.Text>
          <Typography.Text style={{ color: "#64748b", fontSize: 12 }}>
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
          autoSize={{ minRows: 4, maxRows: 8 }}
          onChange={(event) => onRunMessageChange(event.target.value)}
          placeholder={t(
            "teamMemberWorkflowStudio.draftRunPanel.messagePlaceholder",
            "Optional input sent to this workflow draft run",
          )}
          value={runMessage}
        />
      </section>
      <div
        style={{
          alignItems: "center",
          display: "flex",
          flexWrap: "wrap",
          gap: 8,
          justifyContent: "space-between",
          paddingTop: 2,
        }}
      >
        <Button
          disabled={!canRun}
          icon={<PlayCircleOutlined />}
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
              flex: "1 1 180px",
            }}
          >
            {disabledReason}
          </Typography.Text>
        ) : null}
      </div>
    </WorkflowStudioSidePanel>
  );
};

export default WorkflowStudioDraftRunPanel;
