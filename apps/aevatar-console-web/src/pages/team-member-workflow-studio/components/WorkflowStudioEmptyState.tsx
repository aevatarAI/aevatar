import { PlusOutlined } from "@ant-design/icons";
import { Button, Typography } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";

type WorkflowStudioEmptyStateProps = {
  readonly description?: string;
  readonly onAddFirstStep?: () => void;
  readonly onStartFromTemplate?: () => void;
  readonly showTemplateLauncher?: boolean;
  readonly templateLauncherRef?: React.Ref<HTMLButtonElement>;
  readonly title?: string;
};

const WorkflowStudioEmptyState: React.FC<WorkflowStudioEmptyStateProps> = ({
  description = "Start this workflow by adding the first step.",
  onAddFirstStep,
  onStartFromTemplate,
  showTemplateLauncher = false,
  templateLauncherRef,
  title = "Add first step",
}) => (
  <div
    data-testid="workflow-studio-empty-state"
    style={{
      alignItems: "center",
      display: "flex",
      flexDirection: "column",
      gap: 10,
      left: "50%",
      pointerEvents: "auto",
      position: "absolute",
      top: "50%",
      transform: "translate(-50%, -50%)",
      zIndex: 5,
    }}
  >
    <Button
      aria-label={title}
      icon={<PlusOutlined />}
      onClick={onAddFirstStep}
      style={{
        border: "1px dashed #9ca3af",
        borderRadius: 8,
        height: 112,
        width: 112,
      }}
    />
    <Typography.Text strong style={{ color: "#1f2937", fontSize: 18 }}>
      {title}
    </Typography.Text>
    <Typography.Text
      style={{ color: "#6b7280", fontSize: 13, textAlign: "center" }}
    >
      {description}
    </Typography.Text>
    {showTemplateLauncher ? (
      <button
        aria-label={t(
          "teamMemberWorkflowStudio.templates.launcherAria",
          "Start from a workflow template",
        )}
        onClick={onStartFromTemplate}
        ref={templateLauncherRef}
        style={{
          background: "transparent",
          border: 0,
          color: "#2563eb",
          cursor: "pointer",
          font: "inherit",
          minHeight: 44,
          padding: "8px 10px",
          textDecoration: "underline",
          textUnderlineOffset: 3,
        }}
        type="button"
      >
        {t(
          "teamMemberWorkflowStudio.templates.launcher",
          "or start from a template",
        )}
      </button>
    ) : null}
  </div>
);

export default WorkflowStudioEmptyState;
