import {
  DeleteOutlined,
  DownOutlined,
  MoreOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  SaveOutlined,
} from "@ant-design/icons";
import {
  Breadcrumb,
  Button,
  Dropdown,
  Input,
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
  readonly executePending: boolean;
  readonly executePlaceholderReason?: string;
  readonly mode: "new" | "existing";
  readonly onActivate: () => void;
  readonly onAddNode: () => void;
  readonly onDeleteNode: () => void;
  readonly onExecute: () => void;
  readonly onNavigateBack: () => void;
  readonly onSave: () => void;
  readonly onSetTeamEntry: () => void;
  readonly onTitleChange: (title: string) => void;
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
  executePending,
  executePlaceholderReason,
  mode,
  onActivate,
  onAddNode,
  onDeleteNode,
  onExecute,
  onNavigateBack,
  onSave,
  onSetTeamEntry,
  onTitleChange,
  savePending,
  savePlaceholderReason,
  selectedNodeId,
  selectedTab,
  onTabChange,
  teamEntryNotice,
  teamEntryPending,
  teamName,
  workflowTitle,
}) => (
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
        <Typography.Text style={{ color: "#6b7280", fontSize: 13 }}>
          {mode === "new" ? "Draft workflow member" : "Workflow member"}
        </Typography.Text>
        {dirty ? <Tag color="gold">Unsaved changes</Tag> : null}
      </Space>
    </div>
    <Space size={12} wrap>
      <Segmented
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
      <Space size={6} title={activationPlaceholderReason}>
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
        />
        <Tag color={activationTone === "default" ? "default" : activationTone}>
          {activationNotice}
        </Tag>
      </Space>
      <Button
        disabled={!canExecute}
        icon={<PlayCircleOutlined />}
        loading={executePending}
        onClick={onExecute}
        title={canExecute ? "Execute workflow" : executePlaceholderReason}
      >
        Execute workflow
      </Button>
      <Button icon={<PlusOutlined />} onClick={onAddNode}>
        Add node
      </Button>
      <Button
        disabled={!selectedNodeId}
        icon={<DeleteOutlined />}
        onClick={onDeleteNode}
      >
        Delete node
      </Button>
      <Button
        disabled={!canSave}
        icon={<SaveOutlined />}
        loading={savePending}
        onClick={onSave}
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
          title={teamEntryNotice}
        >
          <DownOutlined />
        </Button>
      </Dropdown>
    </Space>
  </header>
);

export default WorkflowStudioHeader;
