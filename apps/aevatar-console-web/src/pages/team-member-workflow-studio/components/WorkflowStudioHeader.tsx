import {
  ArrowLeftOutlined,
  ClockCircleOutlined,
  CloudUploadOutlined,
  CodeOutlined,
  DeleteOutlined,
  EditOutlined,
  HistoryOutlined,
  MoreOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  ReloadOutlined,
  SaveOutlined,
} from '@ant-design/icons';
import type { InputRef, MenuProps } from 'antd';
import { Button, Dropdown, Input, Tag } from 'antd';
import React from 'react';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
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
  readonly canEditYaml: boolean;
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
  readonly onEditYaml: () => void;
  readonly onNavigateBack: () => void;
  readonly onNavigateToTeam: () => void;
  readonly onNavigateToTeams: () => void;
  readonly onSave: () => void;
  readonly onTitleChange: (title: string) => void;
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
  background: #fbfcfe;
  border-bottom: 1px solid #d8e0ea;
  display: block;
  flex: 0 0 auto;
  padding: 10px 16px;
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
  flex-wrap: wrap;
  gap: 10px;
  min-width: 0;
  overflow: visible;
  row-gap: 6px;
  white-space: normal;
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
  flex: 1 1 280px;
  gap: 8px;
  max-width: 100%;
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
  overflow: hidden;
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
  flex: 1 1 180px;
  gap: 4px;
  max-width: min(360px, 100%);
  min-width: 0;
  padding: 0 2px 0 6px;
}

.workflow-studio-header__title-shell--focused {
  background: #f9fafb;
  border-color: #a7c6f9;
  box-shadow: 0 0 0 2px rgba(47, 109, 246, 0.1);
}

.workflow-studio-header__title-input {
  flex: 1 1 auto;
  min-width: 0;
  width: 100%;
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
  flex: 0 1 auto;
  flex-wrap: wrap;
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
  flex-wrap: wrap;
  gap: 6px;
  justify-content: flex-end;
  max-width: 100%;
  min-width: 0;
  white-space: nowrap;
}

.workflow-studio-header__actions .ant-btn {
  border-color: #d8dce3;
  border-radius: 7px;
  box-shadow: none;
  flex: 0 0 auto;
  font-weight: 500;
  height: 32px;
}

.workflow-studio-header__actions .ant-btn-primary {
  background: #2f6df6;
  border-color: #2f6df6;
}

.workflow-studio-header__action-group {
  align-items: center;
  display: inline-flex;
  flex: 0 0 auto;
  gap: 4px;
  min-width: 0;
}

.workflow-studio-header__action-group--primary,
.workflow-studio-header__action-group--edit {
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-radius: 8px;
  padding: 2px;
}

.workflow-studio-header__action-group--commit {
  gap: 6px;
  padding-inline: 2px;
}

.workflow-studio-header__action-group--secondary {
  border-left: 1px solid #e2e8f0;
  margin-left: 2px;
  padding-left: 8px;
}

.workflow-studio-header__action-group--primary .ant-btn,
.workflow-studio-header__action-group--edit .ant-btn {
  background: transparent;
  border-color: transparent;
}

.workflow-studio-header__action-group--primary .ant-btn:hover,
.workflow-studio-header__action-group--primary .ant-btn:focus-visible,
.workflow-studio-header__action-group--edit .ant-btn:hover,
.workflow-studio-header__action-group--edit .ant-btn:focus-visible {
  background: #ffffff;
  border-color: #d8e0ea;
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

@media (max-width: 1500px) {
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

  .workflow-studio-header__action-group--secondary {
    padding-left: 6px;
  }
}

@media (max-width: 980px) {
  .workflow-studio-header__row {
    align-items: start;
    grid-template-columns: minmax(0, 1fr);
  }

  .workflow-studio-header__actions {
    justify-content: flex-start;
  }

  .workflow-studio-header__action-group--secondary {
    border-left: 0;
    margin-left: 0;
    padding-left: 0;
  }
}

@media (max-width: 760px) {
  .workflow-studio-header {
    padding-inline: 10px;
  }

  .workflow-studio-header__row {
    gap: 10px;
  }

  .workflow-studio-header__title-shell {
    max-width: none;
  }

  .workflow-studio-header__actions {
    justify-content: flex-start;
    min-width: 0;
    padding-bottom: 2px;
  }

  .workflow-studio-header__action-group {
    gap: 3px;
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
      <AevatarTooltip
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
      </AevatarTooltip>
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
      <AevatarTooltip title={t('teamMemberWorkflowStudio.header.back', 'Back')}>
        <Button
          aria-label={t('teamMemberWorkflowStudio.header.back', 'Back')}
          className="workflow-studio-header__back-button"
          data-aevatar-back-button="true"
          icon={<ArrowLeftOutlined />}
          onClick={onNavigateBack}
          size="small"
          type="text"
        />
      </AevatarTooltip>
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
  readonly canEditYaml: boolean;
  readonly invokeHref: string;
  readonly invokePlaceholderReason?: string;
  readonly onAddNode: () => void;
  readonly onDeleteConnection: () => void;
  readonly onDeleteNode: () => void;
  readonly onOpenAutomations: () => void;
  readonly onOpenDraftRunPanel: () => void;
  readonly onOpenInvoke: () => void;
  readonly onOpenPublishedRuns: () => void;
  readonly onEditYaml: () => void;
  readonly onPublishMember: () => void;
  readonly onRefreshPublishStatus: () => void;
  readonly onSave: () => void;
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
  canEditYaml,
  invokeHref,
  invokePlaceholderReason,
  onAddNode,
  onDeleteConnection,
  onDeleteNode,
  onOpenAutomations,
  onOpenDraftRunPanel,
  onEditYaml,
  onOpenInvoke,
  onOpenPublishedRuns,
  onPublishMember,
  onRefreshPublishStatus,
  onSave,
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
      data-responsive-actions="true"
      data-testid="workflow-header-primary-actions"
    >
      <div
        className="workflow-studio-header__action-group workflow-studio-header__action-group--primary"
        data-testid="workflow-header-run-actions"
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
          title={
            invokePlaceholderReason ??
            (canOpenInvoke
              ? t(
                  'teamMemberWorkflowStudio.header.invoke.open',
                  'Open the published member invoke workbench.',
                )
              : undefined)
          }
        >
          <span className="workflow-studio-header__action-label workflow-studio-header__action-label--secondary">
            {t('teamMemberWorkflowStudio.header.invoke', 'Invoke')}
          </span>
        </Button>
      </div>
      <div
        className="workflow-studio-header__action-group workflow-studio-header__action-group--edit"
        data-testid="workflow-header-edit-actions"
      >
        <Button
          aria-label={t('teamMemberWorkflowStudio.header.addNode', 'Add node')}
          className="workflow-studio-header__compact-button"
          icon={<PlusOutlined />}
          onClick={onAddNode}
          size="small"
          title={t('teamMemberWorkflowStudio.header.addNode', 'Add node')}
        >
          <span className="workflow-studio-header__action-label workflow-studio-header__action-label--secondary">
            {t('teamMemberWorkflowStudio.header.addNode', 'Add node')}
          </span>
        </Button>
        <Button
          aria-label={t('teamMemberWorkflowStudio.header.editYaml', 'Edit YAML')}
          className="workflow-studio-header__compact-button"
          disabled={!canEditYaml}
          icon={<CodeOutlined />}
          onClick={onEditYaml}
          size="small"
          title={
            canEditYaml
              ? t(
                  'teamMemberWorkflowStudio.header.editYamlTitle',
                  'Edit workflow YAML',
                )
              : t(
                  'teamMemberWorkflowStudio.header.editYamlUnavailable',
                  'Load the workflow draft before editing YAML.',
                )
          }
        >
          <span className="workflow-studio-header__action-label">
            {t('teamMemberWorkflowStudio.header.editYaml', 'Edit YAML')}
          </span>
        </Button>
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
      </div>
      <div
        className="workflow-studio-header__action-group workflow-studio-header__action-group--commit"
        data-testid="workflow-header-commit-actions"
      >
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
      </div>
      <div
        className="workflow-studio-header__action-group workflow-studio-header__action-group--secondary"
        data-testid="workflow-header-secondary-actions"
      >
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
          title={
            publishedRunsPlaceholderReason ??
            (canOpenPublishedRuns
              ? t(
                  'teamMemberWorkflowStudio.header.publishedRuns.open',
                  'View runs from the published member service.',
                )
              : undefined)
          }
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
      </div>
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
  canEditYaml,
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
  onEditYaml,
  onNavigateBack,
  onNavigateToTeam,
  onNavigateToTeams,
  onSave,
  onTitleChange,
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
          canEditYaml={canEditYaml}
          invokeHref={invokeHref}
          invokePlaceholderReason={invokePlaceholderReason}
          onAddNode={onAddNode}
          onDeleteConnection={onDeleteConnection}
          onDeleteNode={onDeleteNode}
          onOpenAutomations={onOpenAutomations}
          onOpenDraftRunPanel={onOpenDraftRunPanel}
          onEditYaml={onEditYaml}
          onOpenInvoke={onOpenInvoke}
          onOpenPublishedRuns={onOpenPublishedRuns}
          onPublishMember={onPublishMember}
          onRefreshPublishStatus={onRefreshPublishStatus}
          onSave={onSave}
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
