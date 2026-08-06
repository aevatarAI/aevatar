import {
  ArrowLeftOutlined,
  FileAddOutlined,
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
import { studioApi } from '@/shared/studio/api';
import type {
  StudioValidationFinding,
  StudioWorkflowSaveResult,
} from '@/shared/studio/models';
import { useDraftMaterialization } from '../hooks/useDraftMaterialization';
import {
  buildWorkflowActivityEditorHref,
  buildWorkflowActivitySectionHref,
} from '../navigation';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import {
  BUNDLED_WORKFLOW_TEMPLATES,
  createBlankWorkflowYaml,
  hasBlockingFindings,
  slugifyWorkflowFileName,
  type WorkflowCreationMode,
} from './workflowCreation';

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
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
      key: 'blank',
      icon: <FileAddOutlined />,
      label: t('workflowActivityVNext.new.mode.blank', 'Start blank'),
      description: t(
        'workflowActivityVNext.new.mode.blank.description',
        'Start with an empty canvas and add steps yourself.',
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
  const [generatedYaml, setGeneratedYaml] = React.useState('');
  const [generatedReady, setGeneratedReady] = React.useState(false);
  const [templateId, setTemplateId] = React.useState(
    BUNDLED_WORKFLOW_TEMPLATES[0]?.id ?? '',
  );
  const [directoryId, setDirectoryId] = React.useState('');
  const [findings, setFindings] = React.useState<
    readonly StudioValidationFinding[]
  >([]);
  const [submitting, setSubmitting] = React.useState(false);
  const [failure, setFailure] = React.useState('');
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
    if (!directoryId && workspace.data?.directories[0]?.directoryId) {
      setDirectoryId(workspace.data.directories[0].directoryId);
    }
  }, [directoryId, workspace.data]);

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

  const persist = async (nextYaml: string, suggestedName?: string) => {
    const workflowName = (name || suggestedName || '').trim();
    if (!workflowName || !directoryId || submitting) return;
    setSubmitting(true);
    setFailure('');
    try {
      await finishSave(
        await studioApi.createWorkflowDraft({
          directoryId,
          fileName: slugifyWorkflowFileName(workflowName),
          scopeId,
          workflowName,
          yaml: nextYaml,
        }),
      );
    } catch (error) {
      setFailure(errorMessage(error));
    } finally {
      setSubmitting(false);
    }
  };

  const validateAndPersist = async (nextYaml: string) => {
    if (!nextYaml.trim() || submitting) return;
    setSubmitting(true);
    setFailure('');
    setFindings([]);
    try {
      const parsed = await studioApi.parseYaml({ yaml: nextYaml });
      setFindings(parsed.findings);
      if (hasBlockingFindings(parsed.document, parsed.findings)) return;
      const parsedName = String(parsed.document?.name ?? '').trim();
      setSubmitting(false);
      await persist(nextYaml, parsedName);
    } catch (error) {
      setFailure(errorMessage(error));
    } finally {
      setSubmitting(false);
    }
  };

  const generate = async () => {
    if (!prompt.trim() || submitting) return;
    setSubmitting(true);
    setFailure('');
    setFindings([]);
    setGeneratedYaml('');
    setGeneratedReady(false);
    try {
      const generated = await studioApi.authorWorkflow(
        { prompt },
        { onText: setGeneratedYaml },
      );
      const parsed = await studioApi.parseYaml({ yaml: generated });
      setGeneratedYaml(generated);
      setFindings(parsed.findings);
      if (hasBlockingFindings(parsed.document, parsed.findings)) return;
      setGeneratedReady(true);
      if (!name.trim()) setName(String(parsed.document?.name ?? '').trim());
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

  const selectedTemplate = BUNDLED_WORKFLOW_TEMPLATES.find(
    (item) => item.id === templateId,
  );
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
  const templateName = t(
    'workflowActivityVNext.new.templateName.incidentTriage',
    'Incident triage',
  );
  const templateDescription = t(
    'workflowActivityVNext.new.templateDescription.incidentTriage',
    'Classify an incident, prepare a response, and request human approval.',
  );
  const disabledByWorkspace =
    workspace.isPending ||
    workspace.isError ||
    workspace.data?.directories.length === 0;
  const selectMode = (nextMode: WorkflowCreationMode) => {
    setFailure('');
    setFindings([]);
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
      {workspace.isPending ? (
        <Alert
          message={t(
            'workflowActivityVNext.new.workspaceLoading',
            'Loading save locations…',
          )}
          showIcon
          type="info"
        />
      ) : null}
      {workspace.isError ? (
        <Alert
          action={
            <Button onClick={() => void workspace.refetch()}>
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
          }
          message={t(
            'workflowActivityVNext.new.workspaceUnavailable',
            'Save locations unavailable',
          )}
          showIcon
          type="error"
        />
      ) : null}
      {workspace.data?.directories.length === 0 ? (
        <Alert
          message={t(
            'workflowActivityVNext.new.noDirectories',
            'No save location is available. Try again later or contact your administrator.',
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
              disabled={disabledByWorkspace}
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
        <section className="wa-vnext__panel">
          <div className="wa-vnext__form">
            <Space>
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
            </Space>
            <div>
              <span>
                {t('workflowActivityVNext.new.directory', 'Save location')}
              </span>
              <Select
                aria-label={t(
                  'workflowActivityVNext.new.directory',
                  'Save location',
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
                value={directoryId || undefined}
              />
            </div>
            <div>
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

            {mode === 'describe' ? (
              <>
                <div>
                  <span>
                    {t('workflowActivityVNext.new.goal', 'Automation goal')}
                  </span>
                  <Input.TextArea
                    aria-label={t(
                      'workflowActivityVNext.new.goal',
                      'Automation goal',
                    )}
                    onChange={(event) => setPrompt(event.target.value)}
                    rows={5}
                    className="wa-vnext__field-control"
                    value={prompt}
                  />
                </div>
                {generatedYaml ? (
                  <div>
                    <span>
                      {t(
                        'workflowActivityVNext.new.generatedYaml',
                        'Generated YAML',
                      )}
                    </span>
                    <Input.TextArea
                      aria-label={t(
                        'workflowActivityVNext.new.generatedYaml',
                        'Generated YAML',
                      )}
                      onChange={(event) => {
                        setGeneratedYaml(event.target.value);
                        setGeneratedReady(false);
                      }}
                      rows={12}
                      className="wa-vnext__editor-yaml wa-vnext__field-control"
                      value={generatedYaml}
                    />
                  </div>
                ) : null}
                <div className="wa-vnext__form-actions">
                  <Button
                    disabled={!prompt.trim()}
                    loading={submitting}
                    onClick={() => void generate()}
                  >
                    {t(
                      'workflowActivityVNext.new.generate',
                      'Generate workflow',
                    )}
                  </Button>
                  {generatedYaml && generatedReady ? (
                    <Button
                      disabled={!name.trim()}
                      loading={submitting}
                      onClick={() => void persist(generatedYaml)}
                      type="primary"
                    >
                      {t(
                        'workflowActivityVNext.new.createGenerated',
                        'Create workflow',
                      )}
                    </Button>
                  ) : null}
                </div>
              </>
            ) : null}

            {mode === 'blank' ? (
              <Button
                disabled={!name.trim()}
                loading={submitting}
                onClick={() => void persist(createBlankWorkflowYaml(name))}
                type="primary"
              >
                {t('workflowActivityVNext.new.createBlank', 'Create workflow')}
              </Button>
            ) : null}

            {mode === 'import' ? (
              <>
                <div>
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
                <Button
                  disabled={!yaml.trim()}
                  loading={submitting}
                  onClick={() => void validateAndPersist(yaml)}
                  type="primary"
                >
                  {t(
                    'workflowActivityVNext.new.validateCreate',
                    'Validate and create',
                  )}
                </Button>
              </>
            ) : null}

            {mode === 'template' ? (
              <>
                <div>
                  <span>
                    {t('workflowActivityVNext.new.template', 'Template')}
                  </span>
                  <Select
                    aria-label={t(
                      'workflowActivityVNext.new.template',
                      'Template',
                    )}
                    onChange={setTemplateId}
                    options={BUNDLED_WORKFLOW_TEMPLATES.map((item) => ({
                      label: templateName,
                      value: item.id,
                    }))}
                    className="wa-vnext__field-control"
                    value={templateId}
                  />
                </div>
                {selectedTemplate ? (
                  <div>
                    <strong>{templateName}</strong>
                    <p className="wa-vnext__creation-option-description">
                      {templateDescription}
                    </p>
                  </div>
                ) : null}
                <Button
                  disabled={!selectedTemplate || !name.trim()}
                  loading={submitting}
                  onClick={() =>
                    selectedTemplate &&
                    void validateAndPersist(selectedTemplate.yaml)
                  }
                  type="primary"
                >
                  {t(
                    'workflowActivityVNext.new.createTemplate',
                    'Create from template',
                  )}
                </Button>
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
            {failure ? (
              <Alert
                description={<TechnicalDetails>{failure}</TechnicalDetails>}
                message={t(
                  'workflowActivityVNext.new.createFailed',
                  "Workflow couldn't be created",
                )}
                showIcon
                type="error"
              />
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
