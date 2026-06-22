import {
  ClockCircleOutlined,
  CloudUploadOutlined,
  CodeOutlined,
  DeleteOutlined,
  DownOutlined,
  EditOutlined,
  FileTextOutlined,
  MoreOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  ReloadOutlined,
  SaveOutlined,
} from "@ant-design/icons";
import { Breadcrumb, Button, Dropdown, Input, Tag, Tooltip } from "antd";
import type { InputRef, MenuProps } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";
import { AevatarBackButton } from "@/shared/ui/aevatarPageShells";

type WorkflowHeaderMenuItem = NonNullable<MenuProps["items"]>[number];

type WorkflowStudioHeaderProps = {
  readonly automationsHref: string;
  readonly automationsPlaceholderReason?: string;
  readonly canOpenAutomations: boolean;
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
  readonly currentDraftRunPlaceholderReason?: string;
  readonly onPublishMember: () => void;
  readonly onOpenAutomations: () => void;
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

const workflowStudioHeaderCss = `
.workflow-studio-header {
  background: #ffffff;
  border-bottom: 1px solid #e5e7eb;
  display: block;
  flex: 0 0 auto;
  padding: 10px 20px;
}

.workflow-studio-header__row {
  align-items: center;
  display: grid;
  gap: 16px;
  grid-template-columns: minmax(0, 1fr) auto;
  min-width: 0;
}

.workflow-studio-header__identity {
  align-items: center;
  display: flex;
  gap: 8px;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
}

.workflow-studio-header__breadcrumb {
  flex: 0 1 auto;
  max-width: clamp(120px, 22vw, 280px);
  min-width: 72px;
  overflow: hidden;
}

.workflow-studio-header__breadcrumb .ant-breadcrumb {
  min-width: 0;
  white-space: nowrap;
}

.workflow-studio-header__breadcrumb .ant-breadcrumb ol {
  flex-wrap: nowrap;
  min-width: 0;
}

.workflow-studio-header__breadcrumb .ant-breadcrumb li {
  min-width: 0;
}

.workflow-studio-header__breadcrumb-link {
  color: #4b5563;
  cursor: pointer;
  display: inline-block;
  max-width: 140px;
  overflow: hidden;
  text-decoration: none;
  text-overflow: ellipsis;
  vertical-align: bottom;
  white-space: nowrap;
}

.workflow-studio-header__breadcrumb-link--muted {
  color: #6b7280;
}

.workflow-studio-header__title-shell {
  align-items: center;
  background: transparent;
  border: 1px solid transparent;
  border-radius: 6px;
  display: inline-flex;
  flex: 1 1 220px;
  gap: 2px;
  max-width: 460px;
  min-width: 96px;
  padding: 0 2px;
}

.workflow-studio-header__title-shell--focused {
  background: #f9fafb;
  border-color: #93c5fd;
}

.workflow-studio-header__title-input {
  min-width: 0;
}

.workflow-studio-header__title-input input {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.workflow-studio-header__status {
  align-items: center;
  display: inline-flex;
  flex: 0 0 auto;
  margin-inline-end: 0;
}

.workflow-studio-header__actions {
  align-items: center;
  display: flex;
  flex-wrap: nowrap;
  gap: 6px;
  justify-content: flex-end;
  min-width: max-content;
  white-space: nowrap;
}

.workflow-studio-header__actions .ant-btn {
  flex: 0 0 auto;
}

.workflow-studio-header__primary-button {
  min-width: 72px;
}

.workflow-studio-header__status-button {
  min-width: 88px;
}

.workflow-studio-header__compact-button {
  min-width: 68px;
}

.workflow-studio-header__icon-button {
  align-items: center;
  display: inline-flex;
  flex: 0 0 auto;
  height: 30px;
  justify-content: center;
  width: 30px;
}

@media (max-width: 1080px) {
  .workflow-studio-header__breadcrumb {
    max-width: 180px;
  }

  .workflow-studio-header__title-shell {
    max-width: 360px;
  }

  .workflow-studio-header__action-label--secondary {
    display: none;
  }

  .workflow-studio-header__compact-button {
    min-width: 34px;
    padding-inline: 8px;
  }
}

@media (max-width: 760px) {
  .workflow-studio-header {
    padding-inline: 12px;
  }

  .workflow-studio-header__row {
    gap: 10px;
  }

  .workflow-studio-header__breadcrumb {
    display: none;
  }

  .workflow-studio-header__title-shell {
    max-width: none;
  }

  .workflow-studio-header__action-label {
    display: none;
  }

  .workflow-studio-header__primary-button,
  .workflow-studio-header__status-button,
  .workflow-studio-header__compact-button {
    min-width: 34px;
    padding-inline: 8px;
  }
}
`;

function formatPublishStatusLabel(input: {
  readonly checked: boolean;
  readonly pending: boolean;
  readonly tone: WorkflowStudioHeaderProps["publishTone"];
}): string {
  if (input.pending) {
    return t("teamMemberWorkflowStudio.header.publish.binding", "Binding");
  }

  if (input.tone === "error") {
    return t("teamMemberWorkflowStudio.header.publish.error", "Error");
  }

  if (input.tone === "processing") {
    return t("teamMemberWorkflowStudio.header.publish.bindingStatus", "Binding");
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

  return (
    <section
      aria-label={t(
        "teamMemberWorkflowStudio.header.identityAria",
        "Workflow identity",
      )}
      className="workflow-studio-header__identity"
      data-testid="workflow-header-identity"
    >
      <AevatarBackButton
        ariaLabel={t("teamMemberWorkflowStudio.header.back", "Back")}
        onBack={onNavigateBack}
        title={t("teamMemberWorkflowStudio.header.back", "Back")}
      />
      <div className="workflow-studio-header__breadcrumb">
        <Breadcrumb
          items={[
            {
              title: (
                <a
                  className="workflow-studio-header__breadcrumb-link workflow-studio-header__breadcrumb-link--muted"
                  href={teamsHref}
                  onClick={(event) => {
                    event.preventDefault();
                    onNavigateToTeams();
                  }}
                >
                  {t("teamMemberWorkflowStudio.header.teamBreadcrumb", "Team")}
                </a>
              ),
            },
            {
              title: (
                <a
                  className="workflow-studio-header__breadcrumb-link"
                  href={teamHref}
                  onClick={(event) => {
                    event.preventDefault();
                    onNavigateToTeam();
                  }}
                >
                  {teamName ||
                    t("teamMemberWorkflowStudio.header.currentTeam", "Current team")}
                </a>
              ),
            },
          ]}
        />
      </div>
      <div
        className={[
          "workflow-studio-header__title-shell",
          titleFocused ? "workflow-studio-header__title-shell--focused" : "",
        ]
          .filter(Boolean)
          .join(" ")}
      >
        <Input
          aria-label={t(
            "teamMemberWorkflowStudio.header.workflowTitleAria",
            "Workflow title",
          )}
          className="workflow-studio-header__title-input"
          onBlur={() => setTitleFocused(false)}
          onChange={(event) => onTitleChange(event.target.value)}
          onFocus={() => setTitleFocused(true)}
          ref={titleInputRef}
          style={{
            background: "transparent",
            color: "#111827",
            fontSize: 20,
            fontWeight: 700,
            height: 34,
            lineHeight: "28px",
            padding: 0,
            width: "100%",
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
        className="workflow-studio-header__status"
        color={publishStatusColor === "default" ? "default" : publishStatusColor}
        title={publishStatusTitle}
      >
        {publishStatusLabel}
      </Tag>
      {dirty ? (
        <Tag className="workflow-studio-header__status" color="gold">
          {t(
            "teamMemberWorkflowStudio.header.unsavedChanges",
            "Unsaved changes",
          )}
        </Tag>
      ) : null}
    </section>
  );
};

type HeaderActionsProps = {
  readonly currentDraftRunPlaceholderReason?: string;
  readonly automationsHref: string;
  readonly automationsPlaceholderReason?: string;
  readonly canOpenAutomations: boolean;
  readonly canOpenDraftRunPanel: boolean;
  readonly canSave: boolean;
  readonly canViewYaml: boolean;
  readonly onAddNode: () => void;
  readonly onDeleteConnection: () => void;
  readonly onDeleteNode: () => void;
  readonly onOpenAutomations: () => void;
  readonly onOpenDraftRunPanel: () => void;
  readonly onPasteYamlClick: () => void;
  readonly onPublishMember: () => void;
  readonly onRefreshPublishStatus: () => void;
  readonly onSave: () => void;
  readonly onViewYaml: () => void;
  readonly pasteYamlPending: boolean;
  readonly publishDisabled: boolean;
  readonly publishPending: boolean;
  readonly publishPlaceholderReason?: string;
  readonly refreshPublishStatusPending: boolean;
  readonly savePending: boolean;
  readonly savePlaceholderReason?: string;
  readonly selectedEdgeId: string;
  readonly selectedNodeId: string;
  readonly showPublishAction: boolean;
  readonly showRefreshPublishStatus: boolean;
};

const HeaderActions: React.FC<HeaderActionsProps> = ({
  currentDraftRunPlaceholderReason,
  automationsHref,
  automationsPlaceholderReason,
  canOpenAutomations,
  canOpenDraftRunPanel,
  canSave,
  canViewYaml,
  onAddNode,
  onDeleteConnection,
  onDeleteNode,
  onOpenAutomations,
  onOpenDraftRunPanel,
  onPasteYamlClick,
  onPublishMember,
  onRefreshPublishStatus,
  onSave,
  onViewYaml,
  pasteYamlPending,
  publishDisabled,
  publishPending,
  publishPlaceholderReason,
  refreshPublishStatusPending,
  savePending,
  savePlaceholderReason,
  selectedEdgeId,
  selectedNodeId,
  showPublishAction,
  showRefreshPublishStatus,
}) => {
  const hasSelectedConnection = Boolean(selectedEdgeId);
  const hasSelectedNode = Boolean(selectedNodeId);
  const showStatusAction = showPublishAction || showRefreshPublishStatus;
  const deleteSelectionLabel = hasSelectedConnection
    ? t(
        "teamMemberWorkflowStudio.header.deleteSelectedConnection",
        "Delete selected connection",
      )
    : t(
        "teamMemberWorkflowStudio.header.deleteSelectedNode",
        "Delete selected node",
      );
  const yamlMenuItems: WorkflowHeaderMenuItem[] = [
    {
      disabled: !canViewYaml,
      icon: <CodeOutlined />,
      key: "view-yaml",
      label: t("teamMemberWorkflowStudio.header.viewYaml", "View YAML"),
      title: canViewYaml
        ? undefined
        : t(
            "teamMemberWorkflowStudio.header.viewYamlUnavailable",
            "Load the workflow draft before viewing YAML.",
          ),
    },
    {
      disabled: pasteYamlPending,
      icon: <FileTextOutlined />,
      key: "paste-yaml",
      label: t("teamMemberWorkflowStudio.header.pasteYaml", "Paste YAML"),
    },
  ];
  const moreMenuItems: WorkflowHeaderMenuItem[] = [
    hasSelectedConnection || hasSelectedNode
      ? {
          danger: true,
          icon: <DeleteOutlined />,
          key: hasSelectedConnection ? "delete-connection" : "delete-node",
          label: deleteSelectionLabel,
        }
      : null,
  ].filter(Boolean) as WorkflowHeaderMenuItem[];
  const moreMenuHasActions = Boolean(moreMenuItems.length);

  return (
    <section
      aria-label={t(
        "teamMemberWorkflowStudio.header.primaryActionsAria",
        "Workflow primary actions",
      )}
      className="workflow-studio-header__actions"
      data-nowrap="true"
      data-testid="workflow-header-primary-actions"
    >
      <Button
        aria-label={t("teamMemberWorkflowStudio.header.run", "Run")}
        className="workflow-studio-header__compact-button"
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
            : currentDraftRunPlaceholderReason
        }
      >
        <span className="workflow-studio-header__action-label">
          {t("teamMemberWorkflowStudio.header.run", "Run")}
        </span>
      </Button>
      <Button
        aria-label={t("teamMemberWorkflowStudio.header.addNode", "Add node")}
        className="workflow-studio-header__compact-button"
        icon={<PlusOutlined />}
        onClick={onAddNode}
        size="small"
      >
        <span className="workflow-studio-header__action-label workflow-studio-header__action-label--secondary">
          {t("teamMemberWorkflowStudio.header.addNode", "Add node")}
        </span>
      </Button>
      <Button
        aria-label={t(
          "teamMemberWorkflowStudio.header.recurringWork",
          "Recurring work",
        )}
        className="workflow-studio-header__compact-button"
        disabled={!canOpenAutomations}
        href={canOpenAutomations ? automationsHref : undefined}
        icon={<ClockCircleOutlined />}
        onClick={(event) => {
          event.preventDefault();
          onOpenAutomations();
        }}
        size="small"
        title={
          canOpenAutomations
            ? t(
                "teamMemberWorkflowStudio.header.openAutomations",
                "Open recurring work for this member",
              )
            : automationsPlaceholderReason
        }
      >
        <span className="workflow-studio-header__action-label workflow-studio-header__action-label--secondary">
          {t("teamMemberWorkflowStudio.header.recurringWork", "Recurring work")}
        </span>
      </Button>
      <Button
        aria-label={t("teamMemberWorkflowStudio.header.save", "Save")}
        className="workflow-studio-header__primary-button"
        disabled={!canSave}
        icon={<SaveOutlined />}
        loading={savePending}
        onClick={onSave}
        size="small"
        title={
          canSave
            ? t(
                "teamMemberWorkflowStudio.header.saveDraft",
                "Save draft",
              )
            : savePlaceholderReason
        }
        type={canSave ? "primary" : "default"}
      >
        <span className="workflow-studio-header__action-label">
          {t("teamMemberWorkflowStudio.header.save", "Save")}
        </span>
      </Button>
      {showStatusAction ? (
        showPublishAction ? (
          <Button
            aria-label={t("teamMemberWorkflowStudio.header.publish", "Publish")}
            className="workflow-studio-header__status-button"
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
            <span className="workflow-studio-header__action-label">
              {t("teamMemberWorkflowStudio.header.publish", "Publish")}
            </span>
          </Button>
        ) : (
          <Button
            aria-label={t(
              "teamMemberWorkflowStudio.header.refreshPublishStatus",
              "Refresh status",
            )}
            className="workflow-studio-header__status-button"
            disabled={refreshPublishStatusPending}
            icon={<ReloadOutlined />}
            loading={refreshPublishStatusPending}
            onClick={onRefreshPublishStatus}
            size="small"
            title={t(
              "teamMemberWorkflowStudio.header.refreshPublishStatus",
              "Refresh status",
            )}
          >
            <span className="workflow-studio-header__action-label">
              {t(
                "teamMemberWorkflowStudio.header.refreshPublishStatus",
                "Refresh status",
              )}
            </span>
          </Button>
        )
      ) : null}
      <Dropdown
        menu={{
          items: yamlMenuItems,
          onClick: ({ key }) => {
            if (key === "view-yaml" && canViewYaml) {
              onViewYaml();
              return;
            }

            if (key === "paste-yaml" && !pasteYamlPending) {
              onPasteYamlClick();
            }
          },
        }}
        placement="bottomRight"
        trigger={["click"]}
      >
        <Button
          aria-label={t("teamMemberWorkflowStudio.header.yamlActions", "YAML")}
          className="workflow-studio-header__compact-button"
          icon={<FileTextOutlined />}
          loading={pasteYamlPending}
          size="small"
          title={t(
            "teamMemberWorkflowStudio.header.yamlActionsTitle",
            "View or import workflow YAML",
          )}
        >
          <span className="workflow-studio-header__action-label workflow-studio-header__action-label--secondary">
            {t("teamMemberWorkflowStudio.header.yaml", "YAML")}
          </span>
          <DownOutlined style={{ fontSize: 10 }} />
        </Button>
      </Dropdown>
      {moreMenuHasActions ? (
        <Dropdown
          menu={{
            items: moreMenuItems,
            onClick: ({ key }) => {
              if (key === "delete-node") {
                const confirmed =
                  typeof window === "undefined" ||
                  window.confirm(
                    t(
                      "teamMemberWorkflowStudio.header.confirmDeleteNode",
                      "Delete the selected node? This cannot be undone.",
                    ),
                  );
                if (confirmed) {
                  onDeleteNode();
                }
                return;
              }

              if (key === "delete-connection") {
                const confirmed =
                  typeof window === "undefined" ||
                  window.confirm(
                    t(
                      "teamMemberWorkflowStudio.header.confirmDeleteConnection",
                      "Delete the selected connection? This cannot be undone.",
                    ),
                  );
                if (confirmed) {
                  onDeleteConnection();
                }
              }
            },
          }}
          placement="bottomRight"
          trigger={["click"]}
        >
          <Button
            aria-label={t(
              "teamMemberWorkflowStudio.header.moreActions",
              "More workflow actions",
            )}
            className="workflow-studio-header__icon-button"
            icon={<MoreOutlined />}
            size="small"
            title={t("teamMemberWorkflowStudio.header.more", "More")}
          />
        </Dropdown>
      ) : null}
    </section>
  );
};

const WorkflowStudioHeader: React.FC<WorkflowStudioHeaderProps> = ({
  automationsHref,
  automationsPlaceholderReason,
  canOpenAutomations,
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
  currentDraftRunPlaceholderReason,
  onPublishMember,
  onOpenAutomations,
  onRefreshPublishStatus,
  onAddNode,
  onDeleteConnection,
  onDeleteNode,
  onOpenDraftRunPanel,
  onOpenPasteYaml,
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
  const showPublishAction =
    publishPending ||
    (dirty && !publishDisabled) ||
    (!showRefreshPublishStatus &&
      (publishTone === "processing" ||
        publishTone === "error" ||
        dirty ||
        !memberPublished ||
        !publishDisabled));

  return (
    <header className="workflow-studio-header">
      <style>{workflowStudioHeaderCss}</style>
      <div
        className="workflow-studio-header__row"
        data-testid="workflow-header-main-row"
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
        <HeaderActions
          currentDraftRunPlaceholderReason={currentDraftRunPlaceholderReason}
          automationsHref={automationsHref}
          automationsPlaceholderReason={automationsPlaceholderReason}
          canOpenAutomations={canOpenAutomations}
          canOpenDraftRunPanel={canOpenDraftRunPanel}
          canSave={canSave}
          canViewYaml={canViewYaml}
          onAddNode={onAddNode}
          onDeleteConnection={onDeleteConnection}
          onDeleteNode={onDeleteNode}
          onOpenAutomations={onOpenAutomations}
          onOpenDraftRunPanel={onOpenDraftRunPanel}
          onPasteYamlClick={onOpenPasteYaml}
          onPublishMember={onPublishMember}
          onRefreshPublishStatus={onRefreshPublishStatus}
          onSave={onSave}
          onViewYaml={onViewYaml}
          pasteYamlPending={pasteYamlPending}
          publishDisabled={publishDisabled}
          publishPending={publishPending}
          publishPlaceholderReason={publishPlaceholderReason}
          refreshPublishStatusPending={refreshPublishStatusPending}
          savePending={savePending}
          savePlaceholderReason={savePlaceholderReason}
          selectedEdgeId={selectedEdgeId}
          selectedNodeId={selectedNodeId}
          showPublishAction={showPublishAction}
          showRefreshPublishStatus={showRefreshPublishStatus}
        />
      </div>
    </header>
  );
};

export default WorkflowStudioHeader;
