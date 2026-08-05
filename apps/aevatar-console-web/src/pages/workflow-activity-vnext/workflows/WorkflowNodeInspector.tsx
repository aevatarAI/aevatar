import { CloseOutlined } from '@ant-design/icons';
import {
  Alert,
  Button,
  Collapse,
  Input,
  Modal,
  Select,
  Switch,
  Tooltip,
  Typography,
} from 'antd';
import React from 'react';
import { formatConsoleMessage, t } from '@/shared/i18n/messages';
import {
  parseInspectorParameters,
  type StudioStepInspectorDraft,
} from '@/shared/studio/document';
import { formatStudioStepTypeLabel } from '@/shared/studio/graph';
import {
  applyRawStudioNodeConfiguration,
  applyStudioNodeConfigurationValuesWithValidation,
  formatRawStudioNodeConfiguration,
  getStudioNodeConfigurationSchema,
  readStudioNodeConfigurationValues,
  type StudioStructuredNodeConfigField,
} from '@/shared/studio/nodeConfigFields';

type WorkflowNodeInspectorProps = {
  readonly error?: string;
  readonly onClose: () => void;
  readonly onConfigurationChange: (
    parametersText: string,
  ) => Promise<void> | void;
  readonly onConfigurationErrorChange: (error: string) => void;
  readonly stepDraft: StudioStepInspectorDraft | null;
};

function readDraftParameters(
  stepDraft: StudioStepInspectorDraft,
): Record<string, unknown> {
  try {
    return parseInspectorParameters(stepDraft.parametersText);
  } catch {
    return {};
  }
}

function readCurrentParameters(
  stepDraft: StudioStepInspectorDraft,
  rawConfigurationText: string,
): Record<string, unknown> {
  try {
    return parseInspectorParameters(rawConfigurationText);
  } catch {
    return readDraftParameters(stepDraft);
  }
}

function mergeSchemaParameters(
  current: Record<string, unknown>,
  next: Record<string, unknown>,
): Record<string, unknown> {
  return { ...current, ...next };
}

function displayValue(value: string): string {
  return (
    value.trim() || t('workflowActivityVNext.nodeInspector.notSet', 'Not set')
  );
}

function summarizeBranches(branchesText: string): string {
  try {
    const parsed = JSON.parse(branchesText) as unknown;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return t('workflowActivityVNext.nodeInspector.noBranches', 'No branches');
    }
    const entries = Object.entries(parsed)
      .map(([label, target]) => [label.trim(), String(target ?? '').trim()])
      .filter(([label, target]) => Boolean(label) && Boolean(target));
    return entries.length > 0
      ? entries.map(([label, target]) => `${label} -> ${target}`).join(', ')
      : t('workflowActivityVNext.nodeInspector.noBranches', 'No branches');
  } catch {
    return t(
      'workflowActivityVNext.nodeInspector.branchesUnavailable',
      'Branches unavailable',
    );
  }
}

function useConfigurationDraft(stepDraft: StudioStepInspectorDraft | null) {
  const [configurationValues, setConfigurationValues] = React.useState<
    Record<string, string>
  >({});
  const [rawConfigurationText, setRawConfigurationText] = React.useState('');
  const [schemaParameters, setSchemaParameters] = React.useState<
    Record<string, unknown>
  >({});
  const [rawError, setRawError] = React.useState('');
  const [structuredError, setStructuredError] = React.useState('');
  const [hasUnappliedChanges, setHasUnappliedChanges] = React.useState(false);
  const schemaParametersRef = React.useRef<Record<string, unknown>>({});
  const stepKeyRef = React.useRef('');

  const rememberSchemaParameters = React.useCallback(
    (parameters: Record<string, unknown>): Record<string, unknown> => {
      const nextSchemaParameters = mergeSchemaParameters(
        schemaParametersRef.current,
        parameters,
      );
      schemaParametersRef.current = nextSchemaParameters;
      setSchemaParameters(nextSchemaParameters);
      return nextSchemaParameters;
    },
    [],
  );

  React.useEffect(() => {
    if (!stepDraft) {
      setConfigurationValues({});
      setRawConfigurationText('');
      schemaParametersRef.current = {};
      stepKeyRef.current = '';
      setSchemaParameters({});
      setRawError('');
      setStructuredError('');
      setHasUnappliedChanges(false);
      return;
    }

    const parameters = readDraftParameters(stepDraft);
    const stepKey = `${stepDraft.id}\u0000${stepDraft.type}`;
    const nextSchemaParameters =
      stepKeyRef.current === stepKey
        ? mergeSchemaParameters(schemaParametersRef.current, parameters)
        : parameters;
    schemaParametersRef.current = nextSchemaParameters;
    stepKeyRef.current = stepKey;
    setConfigurationValues(
      readStudioNodeConfigurationValues(
        stepDraft.type,
        parameters,
        nextSchemaParameters,
      ),
    );
    setRawConfigurationText(formatRawStudioNodeConfiguration(parameters));
    setSchemaParameters(nextSchemaParameters);
    setRawError('');
    setStructuredError('');
    setHasUnappliedChanges(false);
  }, [stepDraft?.id, stepDraft?.parametersText, stepDraft?.type]);

  return {
    configurationValues,
    hasUnappliedChanges,
    rawConfigurationText,
    rawError,
    rememberSchemaParameters,
    schemaParameters,
    setConfigurationValues,
    setHasUnappliedChanges,
    setRawConfigurationText,
    setRawError,
    setStructuredError,
    structuredError,
  } as const;
}

const WorkflowNodeInspector: React.FC<WorkflowNodeInspectorProps> = ({
  error,
  onClose,
  onConfigurationChange,
  onConfigurationErrorChange,
  stepDraft,
}) => {
  const [applying, setApplying] = React.useState(false);
  const [discardConfirmationOpen, setDiscardConfirmationOpen] =
    React.useState(false);
  const {
    configurationValues,
    hasUnappliedChanges,
    rawConfigurationText,
    rawError,
    rememberSchemaParameters,
    schemaParameters,
    setConfigurationValues,
    setHasUnappliedChanges,
    setRawConfigurationText,
    setRawError,
    setStructuredError,
    structuredError,
  } = useConfigurationDraft(stepDraft);

  React.useEffect(() => {
    onConfigurationErrorChange(structuredError || rawError);
  }, [onConfigurationErrorChange, rawError, structuredError]);

  if (!stepDraft) return null;

  const parameters = readCurrentParameters(stepDraft, rawConfigurationText);
  const schema = getStudioNodeConfigurationSchema(
    stepDraft.type,
    schemaParameters,
  );
  const activeError = structuredError || rawError || error || '';
  const nodeTypeLabel = formatStudioStepTypeLabel(stepDraft.type);

  const updateFieldValue = (fieldName: string, value: string) => {
    const nextValues = { ...configurationValues, [fieldName]: value };
    const result = applyStudioNodeConfigurationValuesWithValidation(
      stepDraft.type,
      parameters,
      nextValues,
      schemaParameters,
    );
    setConfigurationValues(nextValues);
    setStructuredError(result.errors[0] ?? '');
    setRawError('');
    setHasUnappliedChanges(true);
    if (result.valid) {
      setRawConfigurationText(
        formatRawStudioNodeConfiguration(result.parameters),
      );
      rememberSchemaParameters(result.parameters);
    }
  };

  const applyParametersText = async (parametersText: string) => {
    if (applying) return;
    setApplying(true);
    try {
      await onConfigurationChange(parametersText);
      setHasUnappliedChanges(false);
    } finally {
      setApplying(false);
    }
  };

  const applyConfiguration = () => {
    const result = applyStudioNodeConfigurationValuesWithValidation(
      stepDraft.type,
      parameters,
      configurationValues,
      schemaParameters,
    );
    const nextError = result.errors[0] ?? '';
    setStructuredError(nextError);
    setRawError('');
    if (!result.valid) return;

    const nextRawText = formatRawStudioNodeConfiguration(result.parameters);
    setRawConfigurationText(nextRawText);
    rememberSchemaParameters(result.parameters);
    void applyParametersText(nextRawText);
  };

  const updateRawConfiguration = (value: string) => {
    setRawConfigurationText(value);
    setHasUnappliedChanges(true);
    try {
      const nextParameters = applyRawStudioNodeConfiguration(
        stepDraft.type,
        value,
      );
      const nextSchemaParameters = rememberSchemaParameters(nextParameters);
      setRawError('');
      setStructuredError('');
      setConfigurationValues(
        readStudioNodeConfigurationValues(
          stepDraft.type,
          nextParameters,
          nextSchemaParameters,
        ),
      );
    } catch (nextError) {
      setRawError(
        nextError instanceof Error
          ? nextError.message
          : t(
              'workflowActivityVNext.nodeInspector.rawConfigurationError',
              'Configuration must be a JSON object.',
            ),
      );
    }
  };

  const applyRawConfiguration = () => {
    try {
      const nextParameters = applyRawStudioNodeConfiguration(
        stepDraft.type,
        rawConfigurationText,
      );
      const nextRawText = formatRawStudioNodeConfiguration(nextParameters);
      const nextSchemaParameters = rememberSchemaParameters(nextParameters);
      setRawError('');
      setStructuredError('');
      setRawConfigurationText(nextRawText);
      setConfigurationValues(
        readStudioNodeConfigurationValues(
          stepDraft.type,
          nextParameters,
          nextSchemaParameters,
        ),
      );
      void applyParametersText(nextRawText);
    } catch (nextError) {
      setRawError(
        nextError instanceof Error
          ? nextError.message
          : t(
              'workflowActivityVNext.nodeInspector.rawConfigurationError',
              'Configuration must be a JSON object.',
            ),
      );
    }
  };

  const requestClose = () => {
    if (applying) return;
    if (hasUnappliedChanges) {
      setDiscardConfirmationOpen(true);
      return;
    }
    onClose();
  };

  const renderFieldControl = (field: StudioStructuredNodeConfigField) => {
    const value = configurationValues[field.name] ?? '';
    const control = field.control ?? field.kind;
    if (control === 'select') {
      return (
        <Select
          aria-label={formatConsoleMessage(field.label)}
          onChange={(nextValue) => updateFieldValue(field.name, nextValue)}
          options={(field.options ?? []).map((option) => ({
            label: formatConsoleMessage(option.label),
            value: option.value,
          }))}
          placeholder={
            field.placeholder
              ? formatConsoleMessage(field.placeholder)
              : undefined
          }
          status={structuredError ? 'error' : undefined}
          value={value || undefined}
        />
      );
    }
    if (control === 'boolean') {
      return (
        <Switch
          aria-label={formatConsoleMessage(field.label)}
          checked={value === 'true'}
          onChange={(checked) => updateFieldValue(field.name, String(checked))}
        />
      );
    }
    if (
      control === 'array' ||
      control === 'json' ||
      control === 'object' ||
      control === 'multi-line' ||
      control === 'textarea'
    ) {
      return (
        <Input.TextArea
          aria-label={formatConsoleMessage(field.label)}
          autoSize={{ maxRows: 10, minRows: 4 }}
          onChange={(event) => updateFieldValue(field.name, event.target.value)}
          placeholder={
            field.placeholder
              ? formatConsoleMessage(field.placeholder)
              : undefined
          }
          status={structuredError ? 'error' : undefined}
          value={value}
        />
      );
    }
    return (
      <Input
        aria-label={formatConsoleMessage(field.label)}
        inputMode={control === 'number' ? 'decimal' : undefined}
        onChange={(event) => updateFieldValue(field.name, event.target.value)}
        placeholder={
          field.placeholder
            ? formatConsoleMessage(field.placeholder)
            : undefined
        }
        status={structuredError ? 'error' : undefined}
        value={value}
      />
    );
  };

  return (
    <>
      <aside
        aria-label={t(
          'workflowActivityVNext.nodeInspector.sectionAria',
          'Configure {name}',
          { name: stepDraft.id },
        )}
        className="wa-vnext__node-inspector"
      >
        <header className="wa-vnext__node-inspector-header">
          <div>
            <Typography.Title
              className="wa-vnext__node-inspector-title"
              level={5}
            >
              {t(
                'workflowActivityVNext.nodeInspector.title',
                'Configure {name}',
                { name: nodeTypeLabel },
              )}
            </Typography.Title>
            <Typography.Text className="wa-vnext__node-inspector-subtitle">
              {stepDraft.id}
            </Typography.Text>
          </div>
          <Tooltip
            title={t(
              'workflowActivityVNext.nodeInspector.close',
              'Close configuration',
            )}
          >
            <Button
              aria-label={t(
                'workflowActivityVNext.nodeInspector.closeAria',
                'Close node configuration',
              )}
              disabled={applying}
              icon={<CloseOutlined />}
              onClick={requestClose}
              type="text"
            />
          </Tooltip>
        </header>
        <div className="wa-vnext__node-inspector-body">
          <section aria-labelledby="wa-vnext-node-configuration-title">
            <Typography.Title
              className="wa-vnext__node-inspector-section-title"
              id="wa-vnext-node-configuration-title"
              level={5}
            >
              {t(
                'workflowActivityVNext.nodeInspector.configuration',
                'Configuration',
              )}
            </Typography.Title>
            <Typography.Paragraph className="wa-vnext__node-inspector-description">
              {t(
                'workflowActivityVNext.nodeInspector.configurationDescription',
                'Set what this step needs before the workflow runs.',
              )}
            </Typography.Paragraph>
            <div className="wa-vnext__node-inspector-fields">
              {schema.fields.length > 0 ? (
                schema.fields.map((field) => (
                  <div
                    className="wa-vnext__node-inspector-field"
                    key={field.name}
                  >
                    <span>{formatConsoleMessage(field.label)}</span>
                    {renderFieldControl(field)}
                    {field.description ? (
                      <small>{formatConsoleMessage(field.description)}</small>
                    ) : null}
                  </div>
                ))
              ) : (
                <Alert
                  message={t(
                    'workflowActivityVNext.nodeInspector.noGuidedFields',
                    'Guided options are not available for this step yet.',
                  )}
                  showIcon
                  type="info"
                />
              )}
            </div>
            {activeError ? (
              <Alert
                className="wa-vnext__node-inspector-error"
                message={activeError}
                role="alert"
                showIcon
                type="error"
              />
            ) : null}
          </section>
          <Collapse
            className="wa-vnext__node-inspector-disclosure"
            items={[
              {
                children: (
                  <dl className="wa-vnext__node-inspector-details">
                    <div>
                      <dt>
                        {t('workflowActivityVNext.nodeInspector.type', 'Type')}
                      </dt>
                      <dd>{nodeTypeLabel}</dd>
                    </div>
                    <div>
                      <dt>
                        {t(
                          'workflowActivityVNext.nodeInspector.targetRole',
                          'Target role',
                        )}
                      </dt>
                      <dd>{displayValue(stepDraft.targetRole)}</dd>
                    </div>
                    <div>
                      <dt>
                        {t(
                          'workflowActivityVNext.nodeInspector.nextStep',
                          'Next step',
                        )}
                      </dt>
                      <dd>{displayValue(stepDraft.next)}</dd>
                    </div>
                    <div>
                      <dt>
                        {t(
                          'workflowActivityVNext.nodeInspector.branches',
                          'Branches',
                        )}
                      </dt>
                      <dd>{summarizeBranches(stepDraft.branchesText)}</dd>
                    </div>
                  </dl>
                ),
                key: 'step-details',
                label: t(
                  'workflowActivityVNext.nodeInspector.stepDetails',
                  'Step details',
                ),
              },
              {
                children: (
                  <div className="wa-vnext__node-inspector-advanced">
                    <Typography.Paragraph className="wa-vnext__node-inspector-description">
                      {t(
                        'workflowActivityVNext.nodeInspector.advancedDescription',
                        'Use JSON only when the setting is not available above.',
                      )}
                    </Typography.Paragraph>
                    <Input.TextArea
                      aria-label={t(
                        'workflowActivityVNext.nodeInspector.rawConfigurationAria',
                        'Raw configuration',
                      )}
                      autoSize={{ maxRows: 16, minRows: 8 }}
                      onChange={(event) =>
                        updateRawConfiguration(event.target.value)
                      }
                      spellCheck={false}
                      status={rawError ? 'error' : undefined}
                      value={rawConfigurationText}
                    />
                    <Button
                      disabled={Boolean(rawError) || applying}
                      onClick={applyRawConfiguration}
                    >
                      {t(
                        'workflowActivityVNext.nodeInspector.applyJson',
                        'Apply JSON',
                      )}
                    </Button>
                  </div>
                ),
                key: 'advanced',
                label: t(
                  'workflowActivityVNext.nodeInspector.advanced',
                  'Advanced options',
                ),
              },
            ]}
          />
        </div>
        <footer className="wa-vnext__node-inspector-actions">
          <Button disabled={applying} onClick={requestClose}>
            {t('workflowActivityVNext.common.close', 'Close')}
          </Button>
          <Button
            disabled={
              applying ||
              Boolean(structuredError) ||
              Boolean(rawError) ||
              !hasUnappliedChanges
            }
            loading={applying}
            onClick={applyConfiguration}
            type="primary"
          >
            {t(
              'workflowActivityVNext.nodeInspector.applyChanges',
              'Apply changes',
            )}
          </Button>
        </footer>
      </aside>
      <Modal
        cancelText={t('workflowActivityVNext.common.cancel', 'Cancel')}
        okText={t(
          'workflowActivityVNext.nodeInspector.discard',
          'Discard changes',
        )}
        okButtonProps={{ danger: true }}
        onCancel={() => setDiscardConfirmationOpen(false)}
        onOk={() => {
          setDiscardConfirmationOpen(false);
          onClose();
        }}
        open={discardConfirmationOpen}
        title={t(
          'workflowActivityVNext.nodeInspector.discardTitle',
          'Discard node changes?',
        )}
      >
        <p>
          {t(
            'workflowActivityVNext.nodeInspector.discardDescription',
            'Your unapplied changes to this step will be lost.',
          )}
        </p>
      </Modal>
    </>
  );
};

export default WorkflowNodeInspector;
