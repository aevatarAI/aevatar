import {
  ArrowLeftOutlined,
  CloudUploadOutlined,
  CodeOutlined,
  DeleteOutlined,
  EditOutlined,
  FileTextOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  ReloadOutlined,
  SaveOutlined,
} from "@ant-design/icons";
import {
  Breadcrumb,
  Button,
  Input,
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
  readonly refreshPublishStatusPending: boolean;
  readonly showRefreshPublishStatus: boolean;
  readonly canOpenDraftRunPanel: boolean;
  readonly canSave: boolean;
  readonly canViewYaml: boolean;
  readonly dirty: boolean;
  readonly activeMemberRunPlaceholderReason?: string;
  readonly onPublishMember: () => void;
  readonly onRefreshPublishStatus: () => void;
  readonly onAddNode: () => void;
  readonly onDeleteConnection: () => void;
  readonly onDeleteNode: () => void;
  readonly onOpenDraftRunPanel: () => void;
  readonly onOpenPasteYaml: () => void;
  readonly onViewYaml: () => void;
  readonly onNavigateBack: () => void;
  readonly onNavigateToTeam: () => void;
  readonly onNavigateToTeams: () => void;
  readonly onSave: () => void;
  readonly onTitleChange: (title: string) => void;
  readonly pasteYamlPending: boolean;
  readonly savePending: boolean;
  readonly savePlaceholderReason?: string;
  readonly selectedEdgeId: string;
  readonly selectedNodeId: string;
  readonly teamHref: string;
  readonly teamName: string;
  readonly teamsHref: string;
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
    return t("teamMemberWorkflowStudio.header.publish.publishingStatus", "Publishing");
  }

  return input.checked
    ? t("teamMemberWorkflowStudio.header.publish.published", "Published")
    : t("teamMemberWorkflowStudio.header.publish.draft", "Draft");
}

type HeaderIdentityProps = {
  readonly dirty: boolean;
  readonly onNavigateBack: () => void;
  readonly onNavigateToTeam: () => void;
  readonly onNavigateToTeams: () => void;
  readonly onTitleChange: (title: string) => void;
  readonly publishStatusColor: WorkflowStudioHeaderProps["publishTone"] | "default";
  readonly publishStatusLabel: string;
  readonly publishStatusTitle: string;
  readonly teamHref: string;
  readonly teamName: string;
  readonly teamsHref: string;
  readonly workflowTitle: string;
};

const HeaderIdentity: React.FC<HeaderIdentityProps> = ({
  dirty,
  onNavigateBack,
  onNavigateToTeam,
  onNavigateToTeams,
  onTitleChange,
  publishStatusColor,
  publishStatusLabel,
  publishStatusTitle,
  teamHref,
  teamName,
  teamsHref,
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
              <a
                href={teamsHref}
                onClick={(event) => {
                  event.preventDefault();
                  onNavigateToTeams();
                }}
                style={{
                  color: "#6b7280",
                  cursor: "pointer",
                  textDecoration: "none",
                }}
              >
                {t("teamMemberWorkflowStudio.header.teamBreadcrumb", "Team")}
              </a>
            ),
          },
          {
            title: (
              <a
                href={teamHref}
                onClick={(event) => {
                  event.preventDefault();
                  onNavigateToTeam();
                }}
                style={{
                  color: "#374151",
                  cursor: "pointer",
                  textDecoration: "none",
                }}
              >
                {teamName ||
                  t("teamMemberWorkflowStudio.header.currentTeam", "Current team")}
              </a>
            ),
          },
        ]}
      />
      <Space size={10} style={{ minWidth: 0 }} wrap>
        <Tooltip title={t("teamMemberWorkflowStudio.header.back", "Back")}>
          <Button
            aria-label={t("teamMemberWorkflowStudio.header.back", "Back")}
            icon={<ArrowLeftOutlined />}
            onClick={onNavigateBack}
            size="small"
            style={{
              alignItems: "center",
              borderColor: "#d1d5db",
              color: "#4b5563",
              display: "inline-flex",
              flex: "0 0 auto",
              height: 30,
              justifyContent: "center",
              width: 30,
            }}
          />
        </Tooltip>
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
  readonly activeMemberRunPlaceholderReason?: string;
  readonly canOpenDraftRunPanel: boolean;
  readonly canViewYaml: boolean;
  readonly onAddNode: () => void;
  readonly onOpenDraftRunPanel: () => void;
  readonly onPasteYamlClick: () => void;
  readonly onPublishMember: () => void;
  readonly onRefreshPublishStatus: () => void;
  readonly onViewYaml: () => void;
  readonly pasteYamlPending: boolean;
  readonly publishDisabled: boolean;
  readonly publishPending: boolean;
  readonly publishPlaceholderReason?: string;
  readonly refreshPublishStatusPending: boolean;
  readonly showPublishButton: boolean;
  readonly showRefreshPublishStatus: boolean;
};

const HeaderPrimaryActions: React.FC<HeaderPrimaryActionsProps> = ({
  activeMemberRunPlaceholderReason,
  canOpenDraftRunPanel,
  canViewYaml,
  onAddNode,
  onOpenDraftRunPanel,
  onPasteYamlClick,
  onPublishMember,
  onRefreshPublishStatus,
  onViewYaml,
  pasteYamlPending,
  publishDisabled,
  publishPending,
  publishPlaceholderReason,
  refreshPublishStatusPending,
  showPublishButton,
  showRefreshPublishStatus,
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
    {showRefreshPublishStatus ? (
      <Button
        icon={<ReloadOutlined />}
        loading={refreshPublishStatusPending}
        onClick={onRefreshPublishStatus}
        size="small"
        title={t(
          "teamMemberWorkflowStudio.header.refreshPublishStatusTitle",
          "Refresh published member status",
        )}
      >
        {t(
          "teamMemberWorkflowStudio.header.refreshPublishStatus",
          "Refresh status",
        )}
      </Button>
    ) : null}
    <Button
      disabled={!canOpenDraftRunPanel}
      icon={<PlayCircleOutlined />}
      onClick={onOpenDraftRunPanel}
      size="small"
      title={
        canOpenDraftRunPanel
          ? t(
              "teamMemberWorkflowStudio.header.prepareDraftRun",
              "Prepare draft run",
            )
          : activeMemberRunPlaceholderReason
      }
    >
      {t(
        "teamMemberWorkflowStudio.header.runDraft",
        "Run draft",
      )}
    </Button>
    <Button
      icon={<FileTextOutlined />}
      loading={pasteYamlPending}
      onClick={onPasteYamlClick}
      size="small"
    >
      {t("teamMemberWorkflowStudio.header.pasteYaml", "Paste YAML")}
    </Button>
    <Button
      disabled={!canViewYaml}
      icon={<CodeOutlined />}
      onClick={onViewYaml}
      size="small"
      title={
        canViewYaml
          ? t("teamMemberWorkflowStudio.header.viewYaml", "View YAML")
          : t(
              "teamMemberWorkflowStudio.header.viewYamlUnavailable",
              "Load the workflow draft before viewing YAML.",
            )
      }
    >
      {t("teamMemberWorkflowStudio.header.viewYaml", "View YAML")}
    </Button>
    <Button icon={<PlusOutlined />} onClick={onAddNode} size="small">
      {t("teamMemberWorkflowStudio.header.addNode", "Add node")}
    </Button>
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
  refreshPublishStatusPending,
  showRefreshPublishStatus,
  canOpenDraftRunPanel,
  canSave,
  canViewYaml,
  dirty,
  activeMemberRunPlaceholderReason,
  onPublishMember,
  onAddNode,
  onDeleteConnection,
  onDeleteNode,
  onOpenDraftRunPanel,
  onOpenPasteYaml,
  onRefreshPublishStatus,
  onViewYaml,
  onNavigateBack,
  onNavigateToTeam,
  onNavigateToTeams,
  onSave,
  onTitleChange,
  pasteYamlPending,
  savePending,
  savePlaceholderReason,
  selectedEdgeId,
  selectedNodeId,
  teamHref,
  teamName,
  teamsHref,
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
    publishTone === "error" ||
    (!memberPublished && publishTone !== "processing") ||
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
          onNavigateToTeam={onNavigateToTeam}
          onNavigateToTeams={onNavigateToTeams}
          onTitleChange={onTitleChange}
          publishStatusColor={publishTone}
          publishStatusLabel={publishStatusLabel}
          publishStatusTitle={publishStatusTitle}
          teamHref={teamHref}
          teamName={teamName}
          teamsHref={teamsHref}
          workflowTitle={workflowTitle}
        />
        <HeaderPrimaryActions
          activeMemberRunPlaceholderReason={activeMemberRunPlaceholderReason}
          canOpenDraftRunPanel={canOpenDraftRunPanel}
          canViewYaml={canViewYaml}
          onAddNode={onAddNode}
          onOpenDraftRunPanel={onOpenDraftRunPanel}
          onPasteYamlClick={onOpenPasteYaml}
          onPublishMember={onPublishMember}
          onRefreshPublishStatus={onRefreshPublishStatus}
          onViewYaml={onViewYaml}
          pasteYamlPending={pasteYamlPending}
          publishDisabled={publishDisabled}
          publishPending={publishPending}
          publishPlaceholderReason={publishPlaceholderReason}
          refreshPublishStatusPending={refreshPublishStatusPending}
          showPublishButton={showPublishButton}
          showRefreshPublishStatus={showRefreshPublishStatus}
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
          justifyContent: "flex-end",
          minWidth: 0,
          paddingTop: 10,
        }}
      >
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
