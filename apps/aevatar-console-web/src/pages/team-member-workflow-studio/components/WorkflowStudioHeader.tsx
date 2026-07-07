import {
  ArrowLeftOutlined,
  ClockCircleOutlined,
  CloudUploadOutlined,
  CodeOutlined,
  DeleteOutlined,
  DownOutlined,
  EditOutlined,
  FileTextOutlined,
  HistoryOutlined,
  MoreOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  ReloadOutlined,
  SaveOutlined,
} from '@ant-design/icons';
import type { InputRef, MenuProps } from 'antd';
import { Button, Dropdown, Input, Tag, Tooltip } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';

type WorkflowHeaderMenuItem = NonNullable<MenuProps['items']>[number];

type WorkflowStudioHeaderProps = {
  readonly automationsHref: string;
  readonly automationsPlaceholderReason?: string;
  readonly canOpenAutomations: boolean;
  readonly canOpenInvoke: boolean;
  readonly canOpenPublishedRuns: boolean;
  readonly invokeHref: string;
  readonly invokePlaceholderReason?: string;
  readonly memberPublished: boolean;
  readonly publishedRunsHref: string;
  readonly publishedRunsPlaceholderReason?: string;
  readonly publishDisabled: boolean;
  readonly publishNotice: string;
  readonly publishPending: boolean;
  readonly publishPlaceholderReason?: string;
  readonly publishTone:
    | 'default'
    | 'processing'
    | 'success'
    | 'warning'
    | 'error';
  readonly refreshPublishStatusPending: boolean;
  readonly showRefreshPublishStatus: boolean;
  readonly canOpenDraftRunPanel: boolean;
  readonly canSave: boolean;
  readonly canViewYaml: boolean;
  readonly dirty: boolean;
  readonly currentDraftRunPlaceholderReason?: string;
  readonly onPublishMember: () => void;
  readonly onOpenAutomations: () => void;
  readonly onOpenInvoke: () => void;
  readonly onOpenPublishedRuns: () => void;
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
  padding: 8px 16px;
}

.workflow-studio-header__row {
  align-items: center;
  display: grid;
  gap: 12px;
  grid-template-columns: minmax(0, 1fr) auto;
  min-width: 0;
}

.workflow-studio-header__identity {
  align-items: center;
  display: flex;
  gap: 10px;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
}

.workflow-studio-header__back-button {
  align-items: center;
  border: 0;
  color: #374151;
  display: inline-flex;
  flex: 0 0 auto;
  height: 30px;
  justify-content: center;
  width: 30px;
}

.workflow-studio-header__back-button:hover {
  background: #f3f4f6;
  color: #111827;
}

.workflow-studio-header__title-zone {
  align-items: center;
  display: flex;
  flex: 1 1 auto;
  gap: 8px;
  min-width: 0;
}

.workflow-studio-header__breadcrumbs {
  align-items: center;
  color: #6b7280;
  display: inline-flex;
  flex: 0 1 auto;
  font-size: 13px;
  font-weight: 600;
  gap: 6px;
  min-width: 0;
}

.workflow-studio-header__breadcrumb-link {
  color: #6b7280;
  display: inline-block;
  max-width: 140px;
  overflow: hidden;
  text-decoration: none;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.workflow-studio-header__breadcrumb-link:hover {
  color: #111827;
}

.workflow-studio-header__breadcrumb-separator {
  color: #9ca3af;
  flex: 0 0 auto;
}

.workflow-studio-header__title-divider {
  background: #d1d5db;
  flex: 0 0 auto;
  height: 18px;
  width: 1px;
}

.workflow-studio-header__title-shell {
  align-items: center;
  background: transparent;
  border: 1px solid transparent;
  border-radius: 6px;
  display: inline-flex;
  flex: 1 1 220px;
  gap: 4px;
  max-width: 360px;
  min-width: 96px;
  padding: 0 2px 0 6px;
}

.workflow-studio-header__title-shell--focused {
  background: #f9fafb;
  border-color: #a7c6f9;
  box-shadow: 0 0 0 2px rgba(47, 109, 246, 0.1);
}

.workflow-studio-header__title-input {
  min-width: 0;
}

.workflow-studio-header__title-input.ant-input {
  background: transparent;
  color: #111827;
  font-size: 14px;
  font-weight: 700;
  height: 28px;
  line-height: 24px;
  padding: 0;
}

.workflow-studio-header__title-input input {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.workflow-studio-header__edit-button {
  align-items: center;
  border: 0;
  color: #6b7280;
  display: inline-flex;
  flex: 0 0 auto;
  height: 26px;
  justify-content: center;
  opacity: 0;
  width: 26px;
}

.workflow-studio-header__title-shell:hover .workflow-studio-header__edit-button,
.workflow-studio-header__title-shell--focused .workflow-studio-header__edit-button {
  opacity: 1;
}

.workflow-studio-header__badges {
  align-items: center;
  border: 0;
  display: inline-flex;
  flex: 0 0 auto;
  gap: 6px;
  margin: 0;
  min-inline-size: 0;
  padding: 0;
}

.workflow-studio-header__status {
  align-items: center;
  display: inline-flex;
  flex: 0 0 auto;
  margin-inline-end: 0;
}

.workflow-studio-header__status.ant-tag {
  border-radius: 5px;
  font-size: 12px;
  line-height: 20px;
  margin-inline-end: 0;
  padding-inline: 8px;
}

.workflow-studio-header__actions {
  align-items: center;
  display: flex;
  flex-wrap: nowrap;
  gap: 4px;
  justify-content: flex-end;
  min-width: max-content;
  white-space: nowrap;
}

.workflow-studio-header__actions .ant-btn {
  border-color: #d8dce3;
  box-shadow: none;
  flex: 0 0 auto;
  font-weight: 500;
  height: 30px;
}

.workflow-studio-header__actions .ant-btn-primary {
  background: #2f6df6;
  border-color: #2f6df6;
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
  .workflow-studio-header__breadcrumbs {
    display: none;
  }

  .workflow-studio-header__title-divider {
    display: none;
  }

  .workflow-studio-header__title-shell {
    max-width: 320px;
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
    padding-inline: 10px;
  }

  .workflow-studio-header__row {
    gap: 10px;
    grid-template-columns: minmax(0, 1fr);
  }

  .workflow-studio-header__title-shell {
    max-width: none;
  }

  .workflow-studio-header__actions {
    justify-content: flex-start;
    min-width: 0;
    overflow-x: auto;
    padding-bottom: 2px;
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
  readonly tone: WorkflowStudioHeaderProps['publishTone'];
}): string {
  if (input.pending) {
    return t('teamMemberWorkflowStudio.header.publish.binding', 'Binding');
  }

  if (input.tone === 'error') {
    return t('teamMemberWorkflowStudio.header.publish.error', 'Error');
  }

  if (input.tone === 'processing') {
    return t('teamMemberWorkflowStudio.header.publish.bindingStatus', 'Binding');
  }

  return input.checked
    ? t('teamMemberWorkflowStudio.header.publish.published', 'Published')
    : t('teamMemberWorkflowStudio.header.publish.draft', 'Draft');
}

type HeaderBreadcrumbProps = {
  readonly onNavigateToTeam: () => void;
  readonly onNavigateToTeams: () => void;
  readonly teamHref: string;
  readonly teamName: string;
  readonly teamsHref: string;
};

const HeaderBreadcrumb: React.FC<HeaderBreadcrumbProps> = ({
  onNavigateToTeam,
  onNavigateToTeams,
  teamHref,
  teamName,
  teamsHref,
}) => (
  <nav
    aria-label={t(
      'teamMemberWorkflowStudio.header.breadcrumbAria',
      'Workflow location',
    )}
    className="workflow-studio-header__breadcrumbs"
  >
    <a
      className="workflow-studio-header__breadcrumb-link"
      href={teamsHref}
      onClick={(event) => {
        event.preventDefault();
        onNavigateToTeams();
      }}
    >
      {t('teamMemberWorkflowStudio.header.teamBreadcrumb', 'Team')}
    </a>
    <span
      aria-hidden="true"
      className="workflow-studio-header__breadcrumb-separator"
    >
      /
    </span>
    <a
      className="workflow-studio-header__breadcrumb-link"
      href={teamHref}
      onClick={(event) => {
        event.preventDefault();
        onNavigateToTeam();
      }}
    >
      {teamName ||
        t('teamMemberWorkflowStudio.header.currentTeam', 'Current team')}
    </a>
  </nav>
);

type HeaderIdentityProps = {
  readonly dirty: boolean;
  readonly onNavigateBack: () => void;
  readonly onNavigateToTeam: () => void;
  readonly onNavigateToTeams: () => void;
  readonly onTitleChange: (title: string) => void;
  readonly publishStatusColor:
    | WorkflowStudioHeaderProps['publishTone']
    | 'default';
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
  const workflowTitleNode = (
    <div
      className={[
        'workflow-studio-header__title-shell',
        titleFocused ? 'workflow-studio-header__title-shell--focused' : '',
      ]
        .filter(Boolean)
        .join(' ')}
    >
      <Input
        aria-label={t(
          'teamMemberWorkflowStudio.header.workflowTitleAria',
          'Workflow title',
        )}
        className="workflow-studio-header__title-input"
        onBlur={() => setTitleFocused(false)}
        onChange={(event) => onTitleChange(event.target.value)}
        onFocus={() => setTitleFocused(true)}
        ref={titleInputRef}
        title={t(
          'teamMemberWorkflowStudio.header.editWorkflowName',
          'Edit workflow name',
        )}
        value={workflowTitle}
        variant="borderless"
      />
      <Tooltip
        title={t(
          'teamMemberWorkflowStudio.header.editWorkflowName',
          'Edit workflow name',
        )}
      >
        <Button
          aria-label={t(
            'teamMemberWorkflowStudio.header.editWorkflowName',
            'Edit workflow name',
          )}
          className="workflow-studio-header__edit-button"
          icon={<EditOutlined />}
          onClick={() => titleInputRef.current?.focus()}
          size="small"
          type="text"
        />
      </Tooltip>
    </div>
  );

  return (
    <section
      aria-label={t(
        'teamMemberWorkflowStudio.header.identityAria',
        'Workflow identity',
      )}
      className="workflow-studio-header__identity"
      data-testid="workflow-header-identity"
    >
      <Tooltip title={t('teamMemberWorkflowStudio.header.back', 'Back')}>
        <Button
          aria-label={t('teamMemberWorkflowStudio.header.back', 'Back')}
          className="workflow-studio-header__back-button"
          data-aevatar-back-button="true"
          icon={<ArrowLeftOutlined />}
          onClick={onNavigateBack}
          size="small"
          type="text"
        />
      </Tooltip>
      <div className="workflow-studio-header__title-zone">
        <HeaderBreadcrumb
          onNavigateToTeam={onNavigateToTeam}
          onNavigateToTeams={onNavigateToTeams}
          teamHref={teamHref}
          teamName={teamName}
          teamsHref={teamsHref}
        />
        <span
          aria-hidden="true"
          className="workflow-studio-header__title-divider"
        />
        {workflowTitleNode}
      </div>
      <fieldset
        aria-label={t(
          'teamMemberWorkflowStudio.header.statusAria',
          'Workflow status',
        )}
        className="workflow-studio-header__badges"
      >
        <Tag
          className="workflow-studio-header__status"
          color={
            publishStatusColor === 'default' ? 'default' : publishStatusColor
          }
          title={publishStatusTitle}
        >
          {publishStatusLabel}
        </Tag>
        {dirty ? (
          <Tag className="workflow-studio-header__status" color="gold">
            {t(
              'teamMemberWorkflowStudio.header.unsavedChanges',
              'Unsaved changes',
            )}
          </Tag>
        ) : null}
      </fieldset>
    </section>
  );
};

type HeaderActionsProps = {
  readonly currentDraftRunPlaceholderReason?: string;
  readonly automationsHref: string;
  readonly automationsPlaceholderReason?: string;
  readonly canOpenAutomations: boolean;
  readonly canOpenDraftRunPanel: boolean;
  readonly canOpenInvoke: boolean;
  readonly canOpenPublishedRuns: boolean;
  readonly canSave: boolean;
  readonly canViewYaml: boolean;
  readonly invokeHref: string;
  readonly invokePlaceholderReason?: string;
  readonly onAddNode: () => void;
  readonly onDeleteConnection: () => void;
  readonly onDeleteNode: () => void;
  readonly onOpenAutomations: () => void;
  readonly onOpenDraftRunPanel: () => void;
  readonly onOpenInvoke: () => void;
  readonly onOpenPublishedRuns: () => void;
  readonly onPasteYamlClick: () => void;
  readonly onPublishMember: () => void;
  readonly onRefreshPublishStatus: () => void;
  readonly onSave: () => void;
  readonly onViewYaml: () => void;
  readonly pasteYamlPending: boolean;
  readonly publishDisabled: boolean;
  readonly publishPending: boolean;
  readonly publishPlaceholderReason?: string;
  readonly publishedRunsHref: string;
  readonly publishedRunsPlaceholderReason?: string;
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
  canOpenInvoke,
  canOpenPublishedRuns,
  canSave,
  canViewYaml,
  invokeHref,
  invokePlaceholderReason,
  onAddNode,
  onDeleteConnection,
  onDeleteNode,
  onOpenAutomations,
  onOpenDraftRunPanel,
  onOpenInvoke,
  onOpenPublishedRuns,
  onPasteYamlClick,
  onPublishMember,
  onRefreshPublishStatus,
  onSave,
  onViewYaml,
  pasteYamlPending,
  publishDisabled,
  publishPending,
  publishPlaceholderReason,
  publishedRunsHref,
  publishedRunsPlaceholderReason,
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
        'teamMemberWorkflowStudio.header.deleteSelectedConnection',
        'Delete selected connection',
      )
    : t(
        'teamMemberWorkflowStudio.header.deleteSelectedNode',
        'Delete selected node',
      );
  const yamlMenuItems: WorkflowHeaderMenuItem[] = [
    {
      disabled: !canViewYaml,
      icon: <CodeOutlined />,
      key: 'view-yaml',
      label: t('teamMemberWorkflowStudio.header.viewYaml', 'View YAML'),
      title: canViewYaml
        ? undefined
        : t(
            'teamMemberWorkflowStudio.header.viewYamlUnavailable',
            'Load the workflow draft before viewing YAML.',
          ),
    },
    {
      disabled: pasteYamlPending,
      icon: <FileTextOutlined />,
      key: 'paste-yaml',
      label: t('teamMemberWorkflowStudio.header.pasteYaml', 'Paste YAML'),
    },
  ];
  const moreMenuItems: WorkflowHeaderMenuItem[] = [
    hasSelectedConnection || hasSelectedNode
      ? {
          danger: true,
          icon: <DeleteOutlined />,
          key: hasSelectedConnection ? 'delete-connection' : 'delete-node',
          label: deleteSelectionLabel,
        }
      : null,
  ].filter(Boolean) as WorkflowHeaderMenuItem[];
  const moreMenuHasActions = Boolean(moreMenuItems.length);

  return (
    <section
      aria-label={t(
        'teamMemberWorkflowStudio.header.primaryActionsAria',
        'Workflow primary actions',
      )}
      className="workflow-studio-header__actions"
      data-nowrap="true"
      data-testid="workflow-header-primary-actions"
    >
      <Button
        aria-label={t('teamMemberWorkflowStudio.header.run', 'Run')}
        className="workflow-studio-header__compact-button"
        disabled={!canOpenDraftRunPanel}
        icon={<PlayCircleOutlined />}
        onClick={onOpenDraftRunPanel}
        size="small"
        title={
          canOpenDraftRunPanel
            ? t(
                'teamMemberWorkflowStudio.header.prepareDraftRun',
                'Prepare draft run',
              )
            : currentDraftRunPlaceholderReason
        }
      >
        <span className="workflow-studio-header__action-label">
          {t('teamMemberWorkflowStudio.header.run', 'Run')}
        </span>
      </Button>
      <Button
        aria-label={t('teamMemberWorkflowStudio.header.invoke', 'Invoke')}
        className="workflow-studio-header__compact-button"
        disabled={!canOpenInvoke}
        href={canOpenInvoke ? invokeHref : undefined}
        icon={<PlayCircleOutlined />}
        onClick={(event) => {
          event.preventDefault();
          onOpenInvoke();
        }}
        size="small"
        title={invokePlaceholderReason}
      >
        <span className="workflow-studio-header__action-label workflow-studio-header__action-label--secondary">
          {t('teamMemberWorkflowStudio.header.invoke', 'Invoke')}
        </span>
      </Button>
      <Button
        aria-label={t('teamMemberWorkflowStudio.header.addNode', 'Add node')}
        className="workflow-studio-header__compact-button"
        icon={<PlusOutlined />}
        onClick={onAddNode}
        size="small"
      >
        <span className="workflow-studio-header__action-label workflow-studio-header__action-label--secondary">
          {t('teamMemberWorkflowStudio.header.addNode', 'Add node')}
        </span>
      </Button>
      <Button
        aria-label={t(
          'teamMemberWorkflowStudio.header.publishedRuns',
          'Published runs',
        )}
        className="workflow-studio-header__compact-button"
        disabled={!canOpenPublishedRuns}
        href={canOpenPublishedRuns ? publishedRunsHref : undefined}
        icon={<HistoryOutlined />}
        onClick={(event) => {
          event.preventDefault();
          onOpenPublishedRuns();
        }}
        size="small"
        title={publishedRunsPlaceholderReason}
      >
        <span className="workflow-studio-header__action-label workflow-studio-header__action-label--secondary">
          {t('teamMemberWorkflowStudio.header.publishedRuns', 'Published runs')}
        </span>
      </Button>
      <Button
        aria-label={t(
          'teamMemberWorkflowStudio.header.recurringWork',
          'Recurring work',
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
                'teamMemberWorkflowStudio.header.openAutomations',
                'Open recurring work for this member',
              )
            : automationsPlaceholderReason
        }
      >
        <span className="workflow-studio-header__action-label workflow-studio-header__action-label--secondary">
          {t('teamMemberWorkflowStudio.header.recurringWork', 'Recurring work')}
        </span>
      </Button>
      <Button
        aria-label={t('teamMemberWorkflowStudio.header.save', 'Save')}
        className="workflow-studio-header__primary-button"
        disabled={!canSave}
        icon={<SaveOutlined />}
        loading={savePending}
        onClick={onSave}
        size="small"
        title={
          canSave
            ? t('teamMemberWorkflowStudio.header.saveDraft', 'Save draft')
            : savePlaceholderReason
        }
        type={canSave ? 'primary' : 'default'}
      >
        <span className="workflow-studio-header__action-label">
          {t('teamMemberWorkflowStudio.header.save', 'Save')}
        </span>
      </Button>
      {showStatusAction ? (
        showPublishAction ? (
          <Button
            aria-label={t('teamMemberWorkflowStudio.header.publish', 'Publish')}
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
                    'teamMemberWorkflowStudio.header.publishMember',
                    'Publish member workflow',
                  )
            }
          >
            <span className="workflow-studio-header__action-label">
              {t('teamMemberWorkflowStudio.header.publish', 'Publish')}
            </span>
          </Button>
        ) : (
          <Button
            aria-label={t(
              'teamMemberWorkflowStudio.header.refreshPublishStatus',
              'Refresh status',
            )}
            className="workflow-studio-header__status-button"
            disabled={refreshPublishStatusPending}
            icon={<ReloadOutlined />}
            loading={refreshPublishStatusPending}
            onClick={onRefreshPublishStatus}
            size="small"
            title={t(
              'teamMemberWorkflowStudio.header.refreshPublishStatus',
              'Refresh status',
            )}
          >
            <span className="workflow-studio-header__action-label">
              {t(
                'teamMemberWorkflowStudio.header.refreshPublishStatus',
                'Refresh status',
              )}
            </span>
          </Button>
        )
      ) : null}
      <Dropdown
        menu={{
          items: yamlMenuItems,
          onClick: ({ key }) => {
            if (key === 'view-yaml' && canViewYaml) {
              onViewYaml();
              return;
            }

            if (key === 'paste-yaml' && !pasteYamlPending) {
              onPasteYamlClick();
            }
          },
        }}
        placement="bottomRight"
        trigger={['click']}
      >
        <Button
          aria-label={t('teamMemberWorkflowStudio.header.yamlActions', 'YAML')}
          className="workflow-studio-header__compact-button"
          icon={<FileTextOutlined />}
          loading={pasteYamlPending}
          size="small"
          title={t(
            'teamMemberWorkflowStudio.header.yamlActionsTitle',
            'View or import workflow YAML',
          )}
        >
          <span className="workflow-studio-header__action-label workflow-studio-header__action-label--secondary">
            {t('teamMemberWorkflowStudio.header.yaml', 'YAML')}
          </span>
          <DownOutlined style={{ fontSize: 10 }} />
        </Button>
      </Dropdown>
      {moreMenuHasActions ? (
        <Dropdown
          menu={{
            items: moreMenuItems,
            onClick: ({ key }) => {
              if (key === 'delete-node') {
                const confirmed =
                  typeof window === 'undefined' ||
                  window.confirm(
                    t(
                      'teamMemberWorkflowStudio.header.confirmDeleteNode',
                      'Delete the selected node? This cannot be undone.',
                    ),
                  );
                if (confirmed) {
                  onDeleteNode();
                }
                return;
              }

              if (key === 'delete-connection') {
                const confirmed =
                  typeof window === 'undefined' ||
                  window.confirm(
                    t(
                      'teamMemberWorkflowStudio.header.confirmDeleteConnection',
                      'Delete the selected connection? This cannot be undone.',
                    ),
                  );
                if (confirmed) {
                  onDeleteConnection();
                }
              }
            },
          }}
          placement="bottomRight"
          trigger={['click']}
        >
          <Button
            aria-label={t(
              'teamMemberWorkflowStudio.header.moreActions',
              'More workflow actions',
            )}
            className="workflow-studio-header__icon-button"
            icon={<MoreOutlined />}
            size="small"
            title={t('teamMemberWorkflowStudio.header.more', 'More')}
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
  canOpenInvoke,
  canOpenPublishedRuns,
  invokeHref,
  invokePlaceholderReason,
  memberPublished,
  publishedRunsHref,
  publishedRunsPlaceholderReason,
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
  onOpenInvoke,
  onOpenPublishedRuns,
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
  const showPublishAction = Boolean(
    !memberPublished && (publishPending || !showRefreshPublishStatus),
  );
  const stablePublishedStatusVisible = Boolean(
    memberPublished && publishTone === 'success' && !publishPending,
  );
  const publishStatusTitle = [
    publishNotice,
    stablePublishedStatusVisible ? '' : publishPlaceholderReason,
  ]
    .filter(Boolean)
    .join(' · ');
  const showRefreshStatusAction = Boolean(
    showRefreshPublishStatus && !stablePublishedStatusVisible,
  );

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
          canOpenInvoke={canOpenInvoke}
          canOpenPublishedRuns={canOpenPublishedRuns}
          canSave={canSave}
          canViewYaml={canViewYaml}
          invokeHref={invokeHref}
          invokePlaceholderReason={invokePlaceholderReason}
          onAddNode={onAddNode}
          onDeleteConnection={onDeleteConnection}
          onDeleteNode={onDeleteNode}
          onOpenAutomations={onOpenAutomations}
          onOpenDraftRunPanel={onOpenDraftRunPanel}
          onOpenInvoke={onOpenInvoke}
          onOpenPublishedRuns={onOpenPublishedRuns}
          onPasteYamlClick={onOpenPasteYaml}
          onPublishMember={onPublishMember}
          onRefreshPublishStatus={onRefreshPublishStatus}
          onSave={onSave}
          onViewYaml={onViewYaml}
          pasteYamlPending={pasteYamlPending}
          publishDisabled={publishDisabled}
          publishPending={publishPending}
          publishPlaceholderReason={publishPlaceholderReason}
          publishedRunsHref={publishedRunsHref}
          publishedRunsPlaceholderReason={publishedRunsPlaceholderReason}
          refreshPublishStatusPending={refreshPublishStatusPending}
          savePending={savePending}
          savePlaceholderReason={savePlaceholderReason}
          selectedEdgeId={selectedEdgeId}
          selectedNodeId={selectedNodeId}
          showPublishAction={showPublishAction}
          showRefreshPublishStatus={showRefreshStatusAction}
        />
      </div>
    </header>
  );
};

export default WorkflowStudioHeader;
