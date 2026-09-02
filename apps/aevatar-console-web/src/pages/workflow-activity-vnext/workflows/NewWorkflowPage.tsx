import {
  ArrowLeftOutlined,
  FileTextOutlined,
  ImportOutlined,
  RobotOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Input, Select, Space, Typography } from 'antd';
import React from 'react';
import { scopesApi } from '@/shared/api/scopesApi';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import { isStudioApiStatus, studioApi } from '@/shared/studio/api';
import type {
  StudioValidationFinding,
  StudioWorkflowSaveResult,
} from '@/shared/studio/models';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { useDraftMaterialization } from '../hooks/useDraftMaterialization';
import {
  buildWorkflowActivityEditorHref,
  buildWorkflowActivitySectionHref,
  buildWorkflowActivityTemplatesHref,
} from '../navigation';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import {
  createBlankWorkflowYaml,
  hasBlockingFindings,
  resolveAvailableWorkflowFileName,
  type WorkflowCreationMode,
} from './workflowCreation';

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function isWorkflowCreateResultUnknown(error: unknown): boolean {
  return (
    isStudioApiStatus(error, 408) ||
    isStudioApiStatus(error, 504) ||
    error instanceof TypeError ||
    (typeof DOMException !== 'undefined' &&
      error instanceof DOMException &&
      error.name === 'AbortError')
  );
}

const NewWorkflowPage: React.FC<{ readonly scopeId: string }> = ({
  scopeId,
}) => {
  const modeItems: readonly {
    readonly key: WorkflowCreationMode;
    readonly icon: React.ReactNode;
    readonly label: string;
    readonly description: string;
  }[] = [
    {
      key: 'describe',
      icon: <RobotOutlined />,
      label: t('workflowActivityVNext.new.mode.describe', 'Describe'),
      description: t(
        'workflowActivityVNext.new.mode.describe.description',
        'Turn a goal into a workflow you can review and edit.',
      ),
    },
    {
      key: 'import',
      icon: <ImportOutlined />,
      label: t('workflowActivityVNext.new.mode.import', 'Import YAML'),
      description: t(
        'workflowActivityVNext.new.mode.import.description',
        'Bring in an existing YAML workflow.',
      ),
    },
    {
      key: 'template',
      icon: <FileTextOutlined />,
      label: t('workflowActivityVNext.new.mode.template', 'Use template'),
      description: t(
        'workflowActivityVNext.new.mode.template.description',
        'Start from a ready-made workflow and customize it.',
      ),
    },
  ];
  const [mode, setMode] = React.useState<WorkflowCreationMode | null>(null);
  const [name, setName] = React.useState('');
  const [prompt, setPrompt] = React.useState('');
  const [yaml, setYaml] = React.useState('');
  const [directoryId, setDirectoryId] = React.useState('');
  const [findings, setFindings] = React.useState<
    readonly StudioValidationFinding[]
  >([]);
  const [submitting, setSubmitting] = React.useState(false);
  const [failure, setFailure] = React.useState('');
  const toast = useConsoleToast();
  const materialization = useDraftMaterialization(scopeId);
  const workspace = useQuery({
    queryKey: ['workflow-activity-vnext', 'workspace', scopeId],
    queryFn: () => studioApi.getWorkspaceSettings(scopeId),
    retry: false,
  });
  const existingWorkflows = useQuery({
    queryKey: ['workflow-activity-vnext', 'drafts', scopeId],
    queryFn: () => studioApi.listWorkflowDrafts(scopeId),
    retry: false,
  });
  const existingCommittedWorkflows = useQuery({
    queryKey: ['workflow-activity-vnext', 'committed', scopeId],
    queryFn: () => scopesApi.listWorkflows(scopeId),
    retry: false,
  });

  React.useEffect(() => {
    if (!workspace.data) return;
    if (
      directoryId &&
      workspace.data.directories.some(
        (item) => item.directoryId === directoryId,
      )
    ) {
      return;
    }
    setDirectoryId(workspace.data.directories[0]?.directoryId ?? '');
  }, [directoryId, workspace.data]);

  React.useEffect(() => {
    if (!failure) return;
    toast.error(
      t(
        'workflowActivityVNext.new.createFailed',
        "Workflow couldn't be created",
      ),
    );
  }, [failure, toast]);

  const navigateToWorkflow = React.useCallback(
    (workflowId: string) =>
      history.push(buildWorkflowActivityEditorHref(scopeId, workflowId)),
    [scopeId],
  );

  const finishSave = React.useCallback(
    async (result: StudioWorkflowSaveResult) => {
      if (result.kind === 'materialized') {
        navigateToWorkflow(result.workflow.workflowId);
        return;
      }
      const readable = await materialization.observe(result.receipt);
      if (readable) navigateToWorkflow(readable.workflowId);
    },
    [materialization.observe, navigateToWorkflow],
  );

  const persistDraft = async (nextYaml: string, workflowName: string) => {
    if (!directoryId) {
      setFailure(
        t(
          'workflowActivityVNext.new.saveTargetRequired',
          'Choose an available save location before creating the workflow.',
        ),
      );
      return;
    }
    let result: StudioWorkflowSaveResult;
    try {
      result = await studioApi.createWorkflowDraft({
        directoryId,
        fileName: resolveAvailableWorkflowFileName(
          workflowName,
          directoryId,
          existingWorkflows.data ?? [],
        ),
        scopeId,
        workflowName,
        yaml: nextYaml,
      });
    } catch (error) {
      if (!isWorkflowCreateResultUnknown(error)) throw error;
      toast.warning(
        t(
          'workflowActivityVNext.new.createUnconfirmed',
          "Workflow creation couldn't be confirmed. Check Workflows before trying again.",
        ),
      );
      void existingWorkflows.refetch();
      return;
    }
    await finishSave(result);
  };

  const createBlank = async () => {
    const workflowName = name.trim();
    if (!workflowName || submitting) return;
    setSubmitting(true);
    setFailure('');
    setFindings([]);
    try {
      await persistDraft(createBlankWorkflowYaml(workflowName), workflowName);
    } catch (error) {
      setFailure(errorMessage(error));
    } finally {
      setSubmitting(false);
    }
  };

  const validateAndPersist = async (
    nextYaml: string,
    preferredName?: string,
  ) => {
    if (!nextYaml.trim() || submitting) return;
    setSubmitting(true);
    setFailure('');
    setFindings([]);
    try {
      const parsed = await studioApi.parseYaml({ yaml: nextYaml });
      setFindings(parsed.findings);
      if (hasBlockingFindings(parsed.document, parsed.findings)) return;
      const workflowName = (
        preferredName || String(parsed.document?.name ?? '')
      ).trim();
      if (!workflowName) return;
      await persistDraft(nextYaml, workflowName);
    } catch (error) {
      setFailure(errorMessage(error));
    } finally {
      setSubmitting(false);
    }
  };

  const generateAndOpen = async () => {
    const workflowName = name.trim();
    if (!workflowName || !prompt.trim() || submitting) return;
    setSubmitting(true);
    setFailure('');
    setFindings([]);
    try {
      const generated = await studioApi.authorWorkflow(
        { prompt },
        { onText: () => undefined },
      );
      const parsed = await studioApi.parseYaml({ yaml: generated });
      setFindings(parsed.findings);
      if (hasBlockingFindings(parsed.document, parsed.findings)) return;
      await persistDraft(generated, workflowName);
    } catch (error) {
      setFailure(errorMessage(error));
    } finally {
      setSubmitting(false);
    }
  };

  const retryObservation = async () => {
    const readable = await materialization.retry();
    if (readable) navigateToWorkflow(readable.workflowId);
  };

  const normalizedName = name.trim().toLocaleLowerCase();
  const duplicateName = Boolean(
    normalizedName &&
      (existingWorkflows.data?.some(
        (workflow) =>
          workflow.name.trim().toLocaleLowerCase() === normalizedName,
      ) ||
        existingCommittedWorkflows.data?.some(
          (workflow) =>
            (workflow.displayName || workflow.workflowName)
              .trim()
              .toLocaleLowerCase() === normalizedName,
        )),
  );
  const saveTargetUnavailable = !directoryId;
  const workspaceAccessDenied =
    isStudioApiStatus(workspace.error, 401) ||
    isStudioApiStatus(workspace.error, 403);
  const reviewAccess = () =>
    history.push(buildWorkflowActivitySectionHref(scopeId, 'settings'));
  const selectMode = (nextMode: WorkflowCreationMode) => {
    setFailure('');
    setFindings([]);
    if (nextMode === 'template') {
      history.push(buildWorkflowActivityTemplatesHref(scopeId));
      return;
    }
    setMode(nextMode);
  };

  return (
    <WorkflowActivityVNextShell
      activeSection="workflows"
      description={t(
        'workflowActivityVNext.new.description',
        'Choose how you want to start.',
      )}
      headerActions={
        <Button
          icon={<ArrowLeftOutlined />}
          onClick={() =>
            history.push(buildWorkflowActivitySectionHref(scopeId, 'workflows'))
          }
        >
          {t('workflowActivityVNext.new.back', 'Back to workflows')}
        </Button>
      }
      scopeId={scopeId}
      title={t('workflowActivityVNext.new.title', 'New workflow')}
    >
      {workspace.isError ? (
        <Alert
          action={
            <Space wrap>
              <Button onClick={() => void workspace.refetch()}>
                {t('workflowActivityVNext.common.retry', 'Retry')}
              </Button>
              <Button onClick={reviewAccess}>
                {t('workflowActivityVNext.new.reviewAccess', 'Review access')}
              </Button>
            </Space>
          }
          description={t(
            'workflowActivityVNext.new.workspaceUnavailableDescription',
            'Choose a creation method now. Your input stays on this page while you restore access.',
          )}
          message={t(
            workspaceAccessDenied
              ? 'workflowActivityVNext.new.workspaceUnauthorized'
              : 'workflowActivityVNext.new.workspaceUnavailable',
            workspaceAccessDenied
              ? "You don't have access to a save location in the current workspace."
              : "The current workspace's save location couldn't be loaded.",
          )}
          showIcon
          type="error"
        />
      ) : null}
      {workspace.data?.directories.length === 0 ? (
        <Alert
          action={
            <Space wrap>
              <Button onClick={() => void workspace.refetch()}>
                {t('workflowActivityVNext.common.retry', 'Retry')}
              </Button>
              <Button onClick={reviewAccess}>
                {t('workflowActivityVNext.new.reviewAccess', 'Review access')}
              </Button>
            </Space>
          }
          description={t(
            'workflowActivityVNext.new.noDirectoriesDescription',
            'A save location is required to own the workflow. You can prepare your input here while access is restored.',
          )}
          message={t(
            'workflowActivityVNext.new.noDirectories',
            'No save location is available in the current workspace.',
          )}
          showIcon
          type="warning"
        />
      ) : null}

      {!mode ? (
        <fieldset
          aria-label={t(
            'workflowActivityVNext.new.chooserAria',
            'Workflow creation methods',
          )}
          className="wa-vnext__creation-options"
        >
          {modeItems.map((item) => (
            <button
              aria-label={item.label}
              className="wa-vnext__creation-option"
              key={item.key}
              onClick={() => selectMode(item.key)}
              type="button"
            >
              <span className="wa-vnext__creation-option-icon">
                {item.icon}
              </span>
              <strong className="wa-vnext__creation-option-title">
                {item.label}
              </strong>
              <span className="wa-vnext__creation-option-description">
                {item.description}
              </span>
            </button>
          ))}
        </fieldset>
      ) : (
        <section className="wa-vnext__creation-surface">
          <div className="wa-vnext__creation-form">
            <div className="wa-vnext__creation-heading">
              <Button
                icon={<ArrowLeftOutlined />}
                onClick={() => setMode(null)}
                type="text"
              >
                {t('workflowActivityVNext.new.changeMethod', 'Change method')}
              </Button>
              <Typography.Title className="wa-vnext__form-title" level={3}>
                {modeItems.find((item) => item.key === mode)?.label ?? mode}
              </Typography.Title>
            </div>
            {(workspace.data?.directories.length ?? 0) > 1 ? (
              <div className="wa-vnext__creation-field">
                <span>
                  {t('workflowActivityVNext.new.directory', 'Save to')}
                </span>
                <Select
                  aria-label={t(
                    'workflowActivityVNext.new.directory',
                    'Save to',
                  )}
                  onChange={setDirectoryId}
                  options={(workspace.data?.directories ?? []).map((item) => ({
                    label:
                      item.isBuiltIn && item.label.trim() === scopeId
                        ? t(
                            'workflowActivityVNext.new.defaultWorkspace',
                            'Default workspace',
                          )
                        : item.label,
                    value: item.directoryId,
                  }))}
                  className="wa-vnext__field-control"
                  disabled={saveTargetUnavailable}
                  loading={workspace.isPending}
                  value={directoryId || undefined}
                />
              </div>
            ) : null}
            {mode === 'describe' ? (
              <div className="wa-vnext__creation-field">
                <span>
                  {t('workflowActivityVNext.new.name', 'Workflow name')}
                </span>
                <Input
                  aria-label={t(
                    'workflowActivityVNext.new.name',
                    'Workflow name',
                  )}
                  onChange={(event) => setName(event.target.value)}
                  className="wa-vnext__field-control"
                  value={name}
                />
                {duplicateName ? (
                  <p className="wa-vnext__duplicate-warning" role="status">
                    {t(
                      'workflowActivityVNext.workflows.duplicateNameWarning',
                      'Another workflow already uses this name. Duplicate names are allowed.',
                    )}
                  </p>
                ) : null}
              </div>
            ) : null}

            {mode === 'describe' ? (
              <>
                <div className="wa-vnext__creation-field">
                  <span>
                    {t(
                      'workflowActivityVNext.new.goal',
                      'What should this workflow do?',
                    )}
                  </span>
                  <Input.TextArea
                    aria-label={t(
                      'workflowActivityVNext.new.goal',
                      'What should this workflow do?',
                    )}
                    onChange={(event) => setPrompt(event.target.value)}
                    rows={5}
                    className="wa-vnext__field-control"
                    value={prompt}
                  />
                </div>
                <div className="wa-vnext__creation-actions">
                  <Button
                    disabled={!name.trim() || saveTargetUnavailable}
                    loading={submitting}
                    onClick={() =>
                      void (prompt.trim() ? generateAndOpen() : createBlank())
                    }
                    type="primary"
                  >
                    {t(
                      'workflowActivityVNext.new.generate',
                      'Generate and open',
                    )}
                  </Button>
                </div>
              </>
            ) : null}

            {mode === 'import' ? (
              <>
                <div className="wa-vnext__creation-field">
                  <span>
                    {t('workflowActivityVNext.new.yaml', 'Workflow YAML')}
                  </span>
                  <Input.TextArea
                    aria-label={t(
                      'workflowActivityVNext.new.yaml',
                      'Workflow YAML',
                    )}
                    onChange={(event) => setYaml(event.target.value)}
                    rows={16}
                    className="wa-vnext__editor-yaml wa-vnext__field-control"
                    value={yaml}
                  />
                </div>
                <div className="wa-vnext__creation-actions">
                  <Button
                    disabled={!yaml.trim() || saveTargetUnavailable}
                    loading={submitting}
                    onClick={() => void validateAndPersist(yaml)}
                    type="primary"
                  >
                    {t(
                      'workflowActivityVNext.new.validateCreate',
                      'Import and open',
                    )}
                  </Button>
                </div>
              </>
            ) : null}

            {findings.length > 0 ? (
              <div aria-live="polite">
                {findings.map((finding) => (
                  <Alert
                    key={[
                      finding.code,
                      finding.path,
                      finding.level,
                      finding.message,
                    ].join('|')}
                    message={finding.message}
                    showIcon
                    type={
                      String(finding.level).toLowerCase() === 'error'
                        ? 'error'
                        : 'warning'
                    }
                  />
                ))}
              </div>
            ) : null}
            {materialization.phase !== 'idle' && materialization.receipt ? (
              <div
                className={
                  materialization.phase === 'failed'
                    ? 'wa-vnext__notice wa-vnext__notice--error'
                    : 'wa-vnext__notice'
                }
                role="status"
              >
                <strong>
                  {materialization.phase === 'delayed'
                    ? t(
                        'workflowActivityVNext.new.projectionDelayed',
                        'This is taking longer than expected',
                      )
                    : materialization.phase === 'failed'
                      ? t(
                          'workflowActivityVNext.new.observationFailed',
                          "Workflow couldn't be opened",
                        )
                      : t(
                          'workflowActivityVNext.new.observing',
                          'Creating workflow…',
                        )}
                </strong>
                <p>
                  {materialization.phase === 'delayed' ||
                  materialization.phase === 'failed'
                    ? t(
                        'workflowActivityVNext.new.retryDescription',
                        'Your work is safe. Try again to finish opening the workflow.',
                      )
                    : t(
                        'workflowActivityVNext.new.creatingDescription',
                        'This usually takes only a moment.',
                      )}
                </p>
                {materialization.error ? (
                  <TechnicalDetails>
                    {errorMessage(materialization.error)}
                  </TechnicalDetails>
                ) : null}
                {materialization.phase === 'delayed' ||
                materialization.phase === 'failed' ? (
                  <Button onClick={() => void retryObservation()}>
                    {t(
                      'workflowActivityVNext.new.retryObservation',
                      'Try again',
                    )}
                  </Button>
                ) : null}
              </div>
            ) : null}
          </div>
        </section>
      )}
    </WorkflowActivityVNextShell>
  );
};

export default NewWorkflowPage;
