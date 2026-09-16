import {
  CheckCircleOutlined,
  ReloadOutlined,
  RobotOutlined,
} from "@ant-design/icons";
import { Alert, Button, Input, Space, Tag, Typography } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";
import { aevatarMonoFontFamily } from "@/shared/ui/compactText";
import WorkflowStudioSidePanel from "./WorkflowStudioSidePanel";

type WorkflowStudioAiProposalPanelProps = {
  readonly error: string;
  readonly hasConflict: boolean;
  readonly onApply: () => void;
  readonly onClose: () => void;
  readonly onGenerate: () => void;
  readonly onPromptChange: (prompt: string) => void;
  readonly open: boolean;
  readonly pending: boolean;
  readonly prompt: string;
  readonly proposalYaml: string;
  readonly reasoning: string;
  readonly width: number;
};

const proposalStackStyle: React.CSSProperties = {
  alignContent: "start",
  display: "grid",
  gap: 16,
  minWidth: 0,
};

const proposalSectionStyle: React.CSSProperties = {
  display: "grid",
  gap: 8,
  minWidth: 0,
};

const proposalLabelStyle: React.CSSProperties = {
  color: "#374151",
  fontSize: 12,
  fontWeight: 700,
};

const proposalYamlStyle: React.CSSProperties = {
  fontFamily: aevatarMonoFontFamily,
  fontSize: 12,
  lineHeight: 1.6,
  minHeight: 220,
  resize: "vertical",
};

const WorkflowStudioAiProposalPanel: React.FC<
  WorkflowStudioAiProposalPanelProps
> = ({
  error,
  hasConflict,
  onApply,
  onClose,
  onGenerate,
  onPromptChange,
  open,
  pending,
  prompt,
  proposalYaml,
  reasoning,
  width,
}) => {
  if (!open) {
    return null;
  }

  const hasProposal = Boolean(proposalYaml.trim());
  const canGenerate = Boolean(prompt.trim()) && !pending;
  const generateLabel = error
    ? t("teamMemberWorkflowStudio.aiProposal.retry", "Retry proposal")
    : t("teamMemberWorkflowStudio.aiProposal.generate", "Generate proposal");

  return (
    <WorkflowStudioSidePanel
      ariaLabel={t(
        "teamMemberWorkflowStudio.aiProposal.panelAria",
        "AI workflow proposal panel",
      )}
      onClose={onClose}
      subtitle={t(
        "teamMemberWorkflowStudio.aiProposal.subtitle",
        "Candidate workflow change",
      )}
      title={t("teamMemberWorkflowStudio.aiProposal.title", "Ask AI")}
      width={width}
    >
      <div style={proposalStackStyle}>
        <section style={proposalSectionStyle}>
          <label
            htmlFor="workflow-ai-change-request"
            style={proposalLabelStyle}
          >
            {t(
              "teamMemberWorkflowStudio.aiProposal.changeRequest",
              "Change request",
            )}
          </label>
          <Input.TextArea
            autoSize={{ minRows: 4, maxRows: 9 }}
            disabled={pending}
            id="workflow-ai-change-request"
            onChange={(event) => onPromptChange(event.target.value)}
            placeholder={t(
              "teamMemberWorkflowStudio.aiProposal.promptPlaceholder",
              "Add an approval step after triage",
            )}
            value={prompt}
          />
          <Button
            disabled={!canGenerate}
            icon={error ? <ReloadOutlined /> : <RobotOutlined />}
            loading={pending}
            onClick={onGenerate}
            type="primary"
          >
            {generateLabel}
          </Button>
        </section>

        {error ? <Alert message={error} showIcon type="error" /> : null}
        {hasConflict ? (
          <Alert
            message={t(
              "teamMemberWorkflowStudio.aiProposal.stale",
              "Draft changed after this proposal was generated. Generate a new proposal from the current draft.",
            )}
            showIcon
            type="warning"
          />
        ) : null}

        {reasoning ? (
          <section aria-label={t(
            "teamMemberWorkflowStudio.aiProposal.reasoningAria",
            "Proposal reasoning",
          )} style={proposalSectionStyle}>
            <Typography.Text strong>
              {t("teamMemberWorkflowStudio.aiProposal.reasoning", "Reasoning")}
            </Typography.Text>
            <Typography.Paragraph
              style={{ color: "#4b5563", margin: 0, whiteSpace: "pre-wrap" }}
            >
              {reasoning}
            </Typography.Paragraph>
          </section>
        ) : null}

        {hasProposal ? (
          <section style={proposalSectionStyle}>
            <Space align="center" wrap>
              <Tag color={hasConflict ? "warning" : "success"}>
                {hasConflict
                  ? t(
                      "teamMemberWorkflowStudio.aiProposal.outdated",
                      "Outdated proposal",
                    )
                  : t(
                      "teamMemberWorkflowStudio.aiProposal.ready",
                      "Ready to review",
                    )}
              </Tag>
            </Space>
            <label
              htmlFor="workflow-ai-proposed-yaml"
              style={proposalLabelStyle}
            >
              {t(
                "teamMemberWorkflowStudio.aiProposal.proposedYaml",
                "Proposed workflow YAML",
              )}
            </label>
            <Input.TextArea
              id="workflow-ai-proposed-yaml"
              readOnly
              style={proposalYamlStyle}
              value={proposalYaml}
            />
            <Button
              disabled={hasConflict || pending}
              icon={<CheckCircleOutlined />}
              onClick={onApply}
              type="primary"
            >
              {t(
                "teamMemberWorkflowStudio.aiProposal.apply",
                "Apply proposal",
              )}
            </Button>
          </section>
        ) : null}
      </div>
    </WorkflowStudioSidePanel>
  );
};

export default WorkflowStudioAiProposalPanel;
