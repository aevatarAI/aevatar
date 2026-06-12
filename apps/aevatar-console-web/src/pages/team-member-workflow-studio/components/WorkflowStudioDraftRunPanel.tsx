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
      closeAriaLabel={t(
        "teamMemberWorkflowStudio.draftRunPanel.closeAria",
        "Close draft run panel",
      )}
      onClose={onClose}
      title={t("teamMemberWorkflowStudio.draftRunPanel.title", "Draft run")}
      width={width}
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
