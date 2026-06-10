import {
  CloudUploadOutlined,
  DeleteOutlined,
  EditOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  SaveOutlined,
  SettingOutlined,
} from "@ant-design/icons";
import {
  Breadcrumb,
  Button,
  Input,
  Segmented,
  Space,
  Tag,
  Tooltip,
} from "antd";
import type { InputRef } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";

type WorkflowStudioHeaderProps = {
  readonly memberPublished: boolean;
  readonly publishDisabled: boolean;
  readonly publishNotice: string;
  readonly publishPending: boolean;
  readonly publishPlaceholderReason?: string;
  readonly publishTone: "default" | "processing" | "success" | "warning" | "error";
  readonly canRunActiveMember: boolean;
  readonly canSave: boolean;
  readonly dirty: boolean;
  readonly activeMemberRunPending: boolean;
  readonly activeMemberRunPlaceholderReason?: string;
  readonly onPublishMember: () => void;
  readonly onAddNode: () => void;
  readonly onDeleteConnection: () => void;
  readonly onDeleteNode: () => void;
  readonly onOpenRunOptions: () => void;
  readonly onRunActiveMember: () => void;
  readonly onNavigateBack: () => void;
  readonly onSave: () => void;
  readonly onTitleChange: (title: string) => void;
  readonly savePending: boolean;
  readonly savePlaceholderReason?: string;
  readonly selectedEdgeId: string;
  readonly selectedNodeId: string;
  readonly selectedTab: "editor" | "runs";
  readonly onTabChange: (tab: "editor" | "runs") => void;
  readonly teamName: string;
  readonly workflowTitle: string;
};

function formatPublishStatusLabel(input: {
  readonly checked: boolean;
  readonly pending: boolean;
  readonly tone: WorkflowStudioHeaderProps["publishTone"];
}): string {
  if (input.pending) {
    return t("teamMemberWorkflowStudio.header.publish.publishing", "Publishing");
  }

  if (input.tone === "error") {
    return t("teamMemberWorkflowStudio.header.publish.error", "Error");
  }

  if (input.tone === "processing") {
    return t("teamMemberWorkflowStudio.header.publish.binding", "Binding");
  }

  return input.checked
    ? t("teamMemberWorkflowStudio.header.publish.published", "Published")
    : t("teamMemberWorkflowStudio.header.publish.draft", "Draft");
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
      aria-label={t(
        "teamMemberWorkflowStudio.header.identityAria",
        "Workflow identity",
      )}
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
                {t("teamMemberWorkflowStudio.header.teamBreadcrumb", "Team")}
              </button>
            ),
          },
          {
            title:
              teamName ||
              t("teamMemberWorkflowStudio.header.currentTeam", "Current team"),
          },
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
            aria-label={t(
              "teamMemberWorkflowStudio.header.workflowTitleAria",
              "Workflow title",
            )}
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
            title={t(
              "teamMemberWorkflowStudio.header.editWorkflowName",
              "Edit workflow name",
            )}
            value={workflowTitle}
            variant="borderless"
          />
          <Tooltip
            title={t(
              "teamMemberWorkflowStudio.header.editWorkflowName",
              "Edit workflow name",
            )}
          >
            <Button
              aria-label={t(
                "teamMemberWorkflowStudio.header.editWorkflowName",
                "Edit workflow name",
              )}
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
        {dirty ? (
          <Tag color="gold">
            {t(
              "teamMemberWorkflowStudio.header.unsavedChanges",
              "Unsaved changes",
            )}
          </Tag>
        ) : null}
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
    aria-label={t(
      "teamMemberWorkflowStudio.header.primaryActionsAria",
      "Workflow primary actions",
    )}
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
            : t(
                "teamMemberWorkflowStudio.header.publishMember",
                "Publish member workflow",
              )
        }
      >
        {t("teamMemberWorkflowStudio.header.publishMemberShort", "Publish member")}
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
            ? t(
                "teamMemberWorkflowStudio.header.runActiveMember",
                "Run active member",
              )
            : activeMemberRunPlaceholderReason
        }
      >
        {t(
          "teamMemberWorkflowStudio.header.runActiveMember",
          "Run active member",
        )}
      </Button>
      <Tooltip title={t("teamMemberWorkflowStudio.header.runOptionsAria", "Run options")}>
        <Button
          aria-label={t(
            "teamMemberWorkflowStudio.header.runOptionsAria",
            "Run options",
          )}
          data-testid="workflow-run-options-button"
          icon={<SettingOutlined />}
          onClick={onOpenRunOptions}
          size="small"
        />
      </Tooltip>
    </Space.Compact>
    <Button icon={<PlusOutlined />} onClick={onAddNode} size="small">
      {t("teamMemberWorkflowStudio.header.addNode", "Add node")}
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
    aria-label={t("teamMemberWorkflowStudio.header.viewsAria", "Workflow views")}
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
        {
          label: t("teamMemberWorkflowStudio.header.tabs.editor", "Editor"),
          value: "editor",
        },
        {
          label: t("teamMemberWorkflowStudio.header.tabs.runs", "Runs"),
          value: "runs",
        },
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
  readonly onDeleteConnection: () => void;
  readonly onDeleteNode: () => void;
  readonly onSave: () => void;
  readonly savePending: boolean;
  readonly savePlaceholderReason?: string;
  readonly selectedEdgeId: string;
  readonly selectedNodeId: string;
};

const HeaderNodeActions: React.FC<HeaderNodeActionsProps> = ({
  canSave,
  onDeleteConnection,
  onDeleteNode,
  onSave,
  savePending,
  savePlaceholderReason,
  selectedEdgeId,
  selectedNodeId,
}) => {
  const hasSelectedConnection = Boolean(selectedEdgeId);
  const hasSelectedNode = Boolean(selectedNodeId);
  const deleteLabel = hasSelectedConnection
    ? t("teamMemberWorkflowStudio.header.deleteConnection", "Delete connection")
    : t("teamMemberWorkflowStudio.header.deleteNode", "Delete node");

  return (
    <section
      aria-label={t(
        "teamMemberWorkflowStudio.header.nodeActionsAria",
        "Workflow draft and node actions",
      )}
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
        disabled={!hasSelectedConnection && !hasSelectedNode}
        icon={<DeleteOutlined />}
        onClick={hasSelectedConnection ? onDeleteConnection : onDeleteNode}
        size="small"
      >
        {deleteLabel}
      </Button>
      <Button
        disabled={!canSave}
        icon={<SaveOutlined />}
        loading={savePending}
        onClick={onSave}
        size="small"
        title={
          canSave
            ? t("teamMemberWorkflowStudio.header.saveDraft", "Save draft")
            : savePlaceholderReason
        }
        type="primary"
      >
        {t("teamMemberWorkflowStudio.header.save", "Save")}
      </Button>
    </section>
  );
};

const WorkflowStudioHeader: React.FC<WorkflowStudioHeaderProps> = ({
  memberPublished,
  publishDisabled,
  publishNotice,
  publishPending,
  publishPlaceholderReason,
  publishTone,
  canRunActiveMember,
  canSave,
  dirty,
  activeMemberRunPending,
  activeMemberRunPlaceholderReason,
  onPublishMember,
  onAddNode,
  onDeleteConnection,
  onDeleteNode,
  onOpenRunOptions,
  onRunActiveMember,
  onNavigateBack,
  onSave,
  onTitleChange,
  savePending,
  savePlaceholderReason,
  selectedEdgeId,
  selectedNodeId,
  selectedTab,
  onTabChange,
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
    dirty ||
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
          onDeleteConnection={onDeleteConnection}
          onDeleteNode={onDeleteNode}
          onSave={onSave}
          savePending={savePending}
          savePlaceholderReason={savePlaceholderReason}
          selectedEdgeId={selectedEdgeId}
          selectedNodeId={selectedNodeId}
        />
      </div>
    </header>
  );
};

export default WorkflowStudioHeader;
