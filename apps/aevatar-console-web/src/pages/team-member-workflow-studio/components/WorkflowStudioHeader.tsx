import {
  CloudUploadOutlined,
  DeleteOutlined,
  DownOutlined,
  EditOutlined,
  MoreOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  SaveOutlined,
  SettingOutlined,
} from "@ant-design/icons";
import {
  Breadcrumb,
  Button,
  Dropdown,
  Input,
  Segmented,
  Space,
  Tag,
  Tooltip,
} from "antd";
import type { InputRef } from "antd";
import React from "react";

type WorkflowStudioHeaderProps = {
  readonly memberPublished: boolean;
  readonly publishDisabled: boolean;
  readonly publishNotice: string;
  readonly publishPending: boolean;
  readonly publishPlaceholderReason?: string;
  readonly publishTone: "default" | "processing" | "success" | "warning" | "error";
  readonly canRunActiveMember: boolean;
  readonly canSave: boolean;
  readonly canSetTeamEntry: boolean;
  readonly dirty: boolean;
  readonly activeMemberRunPending: boolean;
  readonly activeMemberRunPlaceholderReason?: string;
  readonly onPublishMember: () => void;
  readonly onAddNode: () => void;
  readonly onDeleteNode: () => void;
  readonly onOpenRunOptions: () => void;
  readonly onRunActiveMember: () => void;
  readonly onNavigateBack: () => void;
  readonly onSave: () => void;
  readonly onSetTeamEntry: () => void;
  readonly onTitleChange: (title: string) => void;
  readonly savePending: boolean;
  readonly savePlaceholderReason?: string;
  readonly selectedNodeId: string;
  readonly selectedTab: "editor" | "runs";
  readonly onTabChange: (tab: "editor" | "runs") => void;
  readonly teamEntryNotice: string;
  readonly teamEntryPending: boolean;
  readonly teamName: string;
  readonly workflowTitle: string;
};

function formatPublishStatusLabel(input: {
  readonly checked: boolean;
  readonly pending: boolean;
  readonly tone: WorkflowStudioHeaderProps["publishTone"];
}): string {
  if (input.pending) {
    return "Publishing";
  }

  if (input.tone === "error") {
    return "Error";
  }

  if (input.tone === "processing") {
    return "Binding";
  }

  return input.checked ? "Published" : "Draft";
}

type HeaderIdentityProps = {
  readonly dirty: boolean;
  readonly onNavigateBack: () => void;
  readonly onTitleChange: (title: string) => void;
  readonly publishStatusColor: WorkflowStudioHeaderProps["publishTone"] | "default";
  readonly publishStatusLabel: string;
  readonly publishStatusTitle: string;
  readonly teamName: string;
  readonly workflowTitle: string;
};

const HeaderIdentity: React.FC<HeaderIdentityProps> = ({
  dirty,
  onNavigateBack,
  onTitleChange,
  publishStatusColor,
  publishStatusLabel,
  publishStatusTitle,
  teamName,
  workflowTitle,
}) => {
  const titleInputRef = React.useRef<InputRef>(null);
  const [titleFocused, setTitleFocused] = React.useState(false);
  const workflowTitleInputWidth = Math.min(
    300,
    Math.max(80, workflowTitle.trim().length * 15 + 12),
  );

  return (
    <section
      aria-label="Workflow identity"
      data-testid="workflow-header-identity"
      style={{
        display: "grid",
        gap: 8,
        minWidth: 0,
      }}
    >
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
        <div
          style={{
            alignItems: "center",
            background: titleFocused ? "#f9fafb" : "transparent",
            border: titleFocused
              ? "1px solid #93c5fd"
              : "1px solid transparent",
            borderRadius: 6,
            display: "inline-flex",
            gap: 2,
            maxWidth: "100%",
            padding: "0 2px",
          }}
        >
          <Input
            aria-label="Workflow title"
            onBlur={() => setTitleFocused(false)}
            onChange={(event) => onTitleChange(event.target.value)}
            onFocus={() => setTitleFocused(true)}
            ref={titleInputRef}
            style={{
              background: "transparent",
              color: "#111827",
              fontSize: 22,
              fontWeight: 700,
              height: 38,
              lineHeight: "30px",
              padding: 0,
              width: workflowTitleInputWidth,
            }}
            title="Edit workflow name"
            value={workflowTitle}
            variant="borderless"
          />
          <Tooltip title="Edit workflow name">
            <Button
              aria-label="Edit workflow name"
              icon={<EditOutlined />}
              onClick={() => titleInputRef.current?.focus()}
              size="small"
              style={{
                alignItems: "center",
                borderColor: "#d1d5db",
                color: "#4b5563",
                display: "inline-flex",
                flex: "0 0 auto",
                height: 28,
                justifyContent: "center",
                width: 28,
              }}
            />
          </Tooltip>
        </div>
        <Tag
          color={publishStatusColor === "default" ? "default" : publishStatusColor}
          style={{
            alignItems: "center",
            display: "inline-flex",
            marginInlineEnd: 0,
          }}
          title={publishStatusTitle}
        >
          {publishStatusLabel}
        </Tag>
        {dirty ? <Tag color="gold">Unsaved changes</Tag> : null}
      </Space>
    </section>
  );
};

type HeaderPrimaryActionsProps = {
  readonly activeMemberRunPending: boolean;
  readonly activeMemberRunPlaceholderReason?: string;
  readonly canRunActiveMember: boolean;
  readonly onAddNode: () => void;
  readonly onOpenRunOptions: () => void;
  readonly onPublishMember: () => void;
  readonly onRunActiveMember: () => void;
  readonly publishDisabled: boolean;
  readonly publishPending: boolean;
  readonly publishPlaceholderReason?: string;
  readonly showPublishButton: boolean;
};

const HeaderPrimaryActions: React.FC<HeaderPrimaryActionsProps> = ({
  activeMemberRunPending,
  activeMemberRunPlaceholderReason,
  canRunActiveMember,
  onAddNode,
  onOpenRunOptions,
  onPublishMember,
  onRunActiveMember,
  publishDisabled,
  publishPending,
  publishPlaceholderReason,
  showPublishButton,
}) => (
  <section
    aria-label="Workflow primary actions"
    data-testid="workflow-header-primary-actions"
    style={{
      alignItems: "center",
      display: "flex",
      flexWrap: "wrap",
      gap: 8,
      justifyContent: "flex-end",
      minWidth: 0,
    }}
  >
    {showPublishButton ? (
      <Button
        disabled={publishDisabled}
        icon={<CloudUploadOutlined />}
        loading={publishPending}
        onClick={onPublishMember}
        size="small"
        title={
          publishDisabled
            ? publishPlaceholderReason
            : "Publish member workflow"
        }
      >
        Publish member
      </Button>
    ) : null}
    <Space.Compact>
      <Button
        disabled={!canRunActiveMember}
        icon={<PlayCircleOutlined />}
        loading={activeMemberRunPending}
        onClick={onRunActiveMember}
        size="small"
        title={
          canRunActiveMember
            ? "Run active member"
            : activeMemberRunPlaceholderReason
        }
      >
        Run active member
      </Button>
      <Tooltip title="Run options">
        <Button
          aria-label="Run options"
          data-testid="workflow-run-options-button"
          icon={<SettingOutlined />}
          onClick={onOpenRunOptions}
          size="small"
        />
      </Tooltip>
    </Space.Compact>
    <Button icon={<PlusOutlined />} onClick={onAddNode} size="small">
      Add node
    </Button>
  </section>
);

type HeaderTabsProps = {
  readonly onTabChange: (tab: "editor" | "runs") => void;
  readonly selectedTab: "editor" | "runs";
};

const HeaderTabs: React.FC<HeaderTabsProps> = ({
  onTabChange,
  selectedTab,
}) => (
  <section
    aria-label="Workflow views"
    data-testid="workflow-header-tabs"
    style={{
      alignItems: "center",
      display: "flex",
      minWidth: 0,
    }}
  >
    <Segmented
      size="small"
      options={[
        { label: "Editor", value: "editor" },
        { label: "Runs", value: "runs" },
      ]}
      onChange={(value) => {
        if (value === "editor" || value === "runs") {
          onTabChange(value);
        }
      }}
      value={selectedTab}
    />
  </section>
);

type HeaderNodeActionsProps = {
  readonly canSave: boolean;
  readonly canSetTeamEntry: boolean;
  readonly onDeleteNode: () => void;
  readonly onSave: () => void;
  readonly onSetTeamEntry: () => void;
  readonly savePending: boolean;
  readonly savePlaceholderReason?: string;
  readonly selectedNodeId: string;
  readonly teamEntryNotice: string;
  readonly teamEntryPending: boolean;
};

const HeaderNodeActions: React.FC<HeaderNodeActionsProps> = ({
  canSave,
  canSetTeamEntry,
  onDeleteNode,
  onSave,
  onSetTeamEntry,
  savePending,
  savePlaceholderReason,
  selectedNodeId,
  teamEntryNotice,
  teamEntryPending,
}) => (
  <section
    aria-label="Workflow draft and node actions"
    data-testid="workflow-header-node-actions"
    style={{
      alignItems: "center",
      display: "flex",
      flexWrap: "wrap",
      gap: 8,
      justifyContent: "flex-end",
      minWidth: 0,
    }}
  >
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
  </section>
);

const WorkflowStudioHeader: React.FC<WorkflowStudioHeaderProps> = ({
  memberPublished,
  publishDisabled,
  publishNotice,
  publishPending,
  publishPlaceholderReason,
  publishTone,
  canRunActiveMember,
  canSave,
  canSetTeamEntry,
  dirty,
  activeMemberRunPending,
  activeMemberRunPlaceholderReason,
  onPublishMember,
  onAddNode,
  onDeleteNode,
  onOpenRunOptions,
  onRunActiveMember,
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
}) => {
  const publishStatusLabel = formatPublishStatusLabel({
    checked: memberPublished,
    pending: publishPending,
    tone: publishTone,
  });
  const publishStatusTitle = [publishNotice, publishPlaceholderReason]
    .filter(Boolean)
    .join(" · ");
  const showPublishButton =
    publishPending ||
    publishTone === "processing" ||
    publishTone === "error" ||
    !memberPublished ||
    !publishDisabled;

  return (
    <header
      style={{
        background: "#ffffff",
        borderBottom: "1px solid #e5e7eb",
        display: "grid",
        flex: "0 0 auto",
        gap: 12,
        padding: "14px 22px 12px",
      }}
    >
      <div
        data-testid="workflow-header-main-row"
        style={{
          alignItems: "center",
          display: "flex",
          flexWrap: "wrap",
          gap: "12px 24px",
          justifyContent: "space-between",
          minWidth: 0,
        }}
      >
        <HeaderIdentity
          dirty={dirty}
          onNavigateBack={onNavigateBack}
          onTitleChange={onTitleChange}
          publishStatusColor={publishTone}
          publishStatusLabel={publishStatusLabel}
          publishStatusTitle={publishStatusTitle}
          teamName={teamName}
          workflowTitle={workflowTitle}
        />
        <HeaderPrimaryActions
          activeMemberRunPending={activeMemberRunPending}
          activeMemberRunPlaceholderReason={activeMemberRunPlaceholderReason}
          canRunActiveMember={canRunActiveMember}
          onAddNode={onAddNode}
          onOpenRunOptions={onOpenRunOptions}
          onPublishMember={onPublishMember}
          onRunActiveMember={onRunActiveMember}
          publishDisabled={publishDisabled}
          publishPending={publishPending}
          publishPlaceholderReason={publishPlaceholderReason}
          showPublishButton={showPublishButton}
        />
      </div>
      <div
        data-testid="workflow-header-context-row"
        style={{
          alignItems: "center",
          borderTop: "1px solid #f3f4f6",
          display: "flex",
          flexWrap: "wrap",
          gap: "10px 16px",
          justifyContent: "space-between",
          minWidth: 0,
          paddingTop: 10,
        }}
      >
        <HeaderTabs onTabChange={onTabChange} selectedTab={selectedTab} />
        <HeaderNodeActions
          canSave={canSave}
          canSetTeamEntry={canSetTeamEntry}
          onDeleteNode={onDeleteNode}
          onSave={onSave}
          onSetTeamEntry={onSetTeamEntry}
          savePending={savePending}
          savePlaceholderReason={savePlaceholderReason}
          selectedNodeId={selectedNodeId}
          teamEntryNotice={teamEntryNotice}
          teamEntryPending={teamEntryPending}
        />
      </div>
    </header>
  );
};

export default WorkflowStudioHeader;
