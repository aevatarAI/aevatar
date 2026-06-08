import {
  DeleteOutlined,
  DownOutlined,
  EditOutlined,
  MoreOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  SaveOutlined,
  UpOutlined,
} from "@ant-design/icons";
import {
  Breadcrumb,
  Button,
  Dropdown,
  Input,
  Popover,
  Segmented,
  Space,
  Switch,
  Tag,
  Typography,
} from "antd";
import React from "react";

type WorkflowStudioHeaderProps = {
  readonly activationChecked: boolean;
  readonly activationDisabled: boolean;
  readonly activationNotice: string;
  readonly activationPending: boolean;
  readonly activationPlaceholderReason?: string;
  readonly activationTone: "default" | "processing" | "success" | "warning" | "error";
  readonly canExecute: boolean;
  readonly canSave: boolean;
  readonly canSetTeamEntry: boolean;
  readonly dirty: boolean;
  readonly executionRunId: string;
  readonly executionStartedAt: string;
  readonly executionStatus: "idle" | "running" | "succeeded" | "failed";
  readonly executePending: boolean;
  readonly executePlaceholderReason?: string;
  readonly onActivate: () => void;
  readonly onAddNode: () => void;
  readonly onDeleteNode: () => void;
  readonly onExecute: () => void;
  readonly onNavigateBack: () => void;
  readonly onRunInputChange: (input: string) => void;
  readonly onSave: () => void;
  readonly onSetTeamEntry: () => void;
  readonly onTitleChange: (title: string) => void;
  readonly runInput: string;
  readonly savePending: boolean;
  readonly savePlaceholderReason?: string;
  readonly selectedNodeId: string;
  readonly selectedTab: "editor" | "executions";
  readonly onTabChange: (tab: "editor" | "executions") => void;
  readonly teamEntryNotice: string;
  readonly teamEntryPending: boolean;
  readonly teamName: string;
  readonly workflowTitle: string;
};

function formatActivationStatusLabel(input: {
  readonly checked: boolean;
  readonly pending: boolean;
  readonly tone: WorkflowStudioHeaderProps["activationTone"];
}): string {
  if (input.pending) {
    return "Publishing";
  }

  if (input.tone === "error") {
    return "Error";
  }

  return input.checked ? "Ready" : "Inactive";
}

function formatExecutionTime(value: string | null | undefined): string {
  if (!value) {
    return "";
  }

  const date = new Date(value);
  return Number.isFinite(date.getTime()) ? date.toLocaleString() : value;
}

function readExecutionStatusColor(
  status: WorkflowStudioHeaderProps["executionStatus"],
): string {
  switch (status) {
    case "failed":
      return "red";
    case "running":
      return "processing";
    case "succeeded":
      return "green";
    default:
      return "default";
  }
}

const WorkflowStudioHeader: React.FC<WorkflowStudioHeaderProps> = ({
  activationChecked,
  activationDisabled,
  activationNotice,
  activationPending,
  activationPlaceholderReason,
  activationTone,
  canExecute,
  canSave,
  canSetTeamEntry,
  dirty,
  executionRunId,
  executionStartedAt,
  executionStatus,
  executePending,
  executePlaceholderReason,
  onActivate,
  onAddNode,
  onDeleteNode,
  onExecute,
  onNavigateBack,
  onRunInputChange,
  onSave,
  onSetTeamEntry,
  onTitleChange,
  runInput,
  savePending,
  savePlaceholderReason,
  selectedNodeId,
  selectedTab,
  onTabChange,
  teamEntryNotice,
  teamEntryPending,
  teamName,
  workflowTitle,
}) => {
  const [runInputExpanded, setRunInputExpanded] = React.useState(false);
  const formattedExecutionStartedAt = formatExecutionTime(executionStartedAt);
  const normalizedExecutionRunId = executionRunId.trim();
  const hasRunInput = runInput.trim().length > 0;
  const runInputEditor = (
    <div
      aria-label="Run options"
      style={{
        display: "grid",
        gap: 8,
        width: 520,
      }}
    >
      <Space align="center" size={8}>
        <EditOutlined style={{ color: "#6b7280" }} />
        <Typography.Text strong style={{ fontSize: 12 }}>
          Run input
        </Typography.Text>
        <Typography.Text style={{ color: "#6b7280", fontSize: 12 }}>
          Optional test payload
        </Typography.Text>
      </Space>
      <Input.TextArea
        aria-label="Run input"
        autoSize={{ minRows: 2, maxRows: 4 }}
        onChange={(event) => onRunInputChange(event.target.value)}
        placeholder="Optional input for this workflow run"
        style={{ maxHeight: 150 }}
        value={runInput}
      />
    </div>
  );

  return (
    <header
      style={{
        alignItems: "center",
        background: "#ffffff",
        borderBottom: "1px solid #e5e7eb",
        display: "flex",
        flex: "0 0 auto",
        gap: 18,
        justifyContent: "space-between",
        minHeight: 88,
        padding: "14px 22px",
      }}
    >
      <div style={{ display: "grid", gap: 8, minWidth: 0 }}>
        <Breadcrumb
          items={[
            {
              title: (
                <button
                  onClick={onNavigateBack}
                  style={{
                    background: "transparent",
                    border: 0,
                    color: "#6b7280",
                    cursor: "pointer",
                    padding: 0,
                  }}
                  type="button"
                >
                  Team
                </button>
              ),
            },
            { title: teamName || "Current team" },
          ]}
        />
        <Space size={10} style={{ minWidth: 0 }} wrap>
          <Input
            aria-label="Workflow title"
            onChange={(event) => onTitleChange(event.target.value)}
            style={{
              color: "#111827",
              fontSize: 22,
              fontWeight: 700,
              height: 40,
              lineHeight: "30px",
              maxWidth: 520,
              minWidth: 280,
              paddingLeft: 0,
            }}
            value={workflowTitle}
            variant="borderless"
          />
          {dirty ? <Tag color="gold">Unsaved changes</Tag> : null}
        </Space>
        <div
          style={{
            display: "grid",
            gap: 8,
            minWidth: 0,
          }}
        >
          <Space
            align="center"
            data-testid="workflow-run-summary"
            size={8}
            style={{ minWidth: 0 }}
            wrap
          >
            <Typography.Text strong style={{ color: "#1f2937", fontSize: 13 }}>
              Workflow run
            </Typography.Text>
            <Tag color={readExecutionStatusColor(executionStatus)}>
              {executionStatus}
            </Tag>
            {normalizedExecutionRunId ? (
              <Typography.Text
                ellipsis
                style={{ color: "#6b7280", fontSize: 12, maxWidth: 240 }}
              >
                {normalizedExecutionRunId}
              </Typography.Text>
            ) : null}
            {formattedExecutionStartedAt ? (
              <Typography.Text style={{ color: "#6b7280", fontSize: 12 }}>
                {formattedExecutionStartedAt}
              </Typography.Text>
            ) : null}
            {hasRunInput ? <Tag color="blue">input set</Tag> : null}
            <Popover
              content={runInputEditor}
              onOpenChange={setRunInputExpanded}
              open={runInputExpanded}
              placement="bottomLeft"
              trigger="click"
            >
              <Button
                icon={
                  runInputExpanded ? (
                    <UpOutlined aria-hidden />
                  ) : (
                    <DownOutlined aria-hidden />
                  )
                }
                size="small"
                type={hasRunInput ? "default" : "text"}
              >
                Run input
              </Button>
            </Popover>
          </Space>
        </div>
      </div>
      <div
        style={{
          alignItems: "center",
          display: "flex",
          flex: "0 0 auto",
          gap: 8,
          minWidth: 0,
          whiteSpace: "nowrap",
        }}
      >
        <Segmented
          size="small"
          options={[
            { label: "Editor", value: "editor" },
            { label: "Executions", value: "executions" },
          ]}
          onChange={(value) => {
            if (value === "editor" || value === "executions") {
              onTabChange(value);
            }
          }}
          value={selectedTab}
        />
        <Space
          size={6}
          style={{ flex: "0 0 auto" }}
          title={[activationNotice, activationPlaceholderReason]
            .filter(Boolean)
            .join(" · ")}
        >
          <Typography.Text style={{ color: "#6b7280", fontSize: 13 }}>
            {activationChecked ? "Active" : "Inactive"}
          </Typography.Text>
          <Switch
            aria-label="Activate workflow member"
            checked={activationChecked}
            disabled={activationDisabled}
            loading={activationPending}
            onChange={(checked) => {
              if (checked) {
                onActivate();
              }
            }}
            size="small"
          />
          <Tag
            color={activationTone === "default" ? "default" : activationTone}
            style={{ marginInlineEnd: 0 }}
          >
            {formatActivationStatusLabel({
              checked: activationChecked,
              pending: activationPending,
              tone: activationTone,
            })}
          </Tag>
        </Space>
        <Button
          disabled={!canExecute}
          icon={<PlayCircleOutlined />}
          loading={executePending}
          onClick={onExecute}
          size="small"
          title={canExecute ? "Execute workflow" : executePlaceholderReason}
        >
          Execute workflow
        </Button>
        <Button icon={<PlusOutlined />} onClick={onAddNode} size="small">
          Add node
        </Button>
        <Button
          disabled={!selectedNodeId}
          icon={<DeleteOutlined />}
          onClick={onDeleteNode}
          size="small"
        >
          Delete node
        </Button>
        <Button
          disabled={!canSave}
          icon={<SaveOutlined />}
          loading={savePending}
          onClick={onSave}
          size="small"
          title={canSave ? "Save draft" : savePlaceholderReason}
          type="primary"
        >
          Save
        </Button>
        <Dropdown
          menu={{
            items: [
              {
                disabled: !canSetTeamEntry,
                key: "set-team-entry",
                label: "Set as Team entry",
              },
            ],
            onClick: ({ key }) => {
              if (key === "set-team-entry" && canSetTeamEntry) {
                onSetTeamEntry();
              }
            },
          }}
          trigger={["click"]}
        >
          <Button
            aria-label="More workflow actions"
            icon={<MoreOutlined />}
            loading={teamEntryPending}
            size="small"
            title={teamEntryNotice}
          >
            <DownOutlined />
          </Button>
        </Dropdown>
      </div>
    </header>
  );
};

export default WorkflowStudioHeader;
