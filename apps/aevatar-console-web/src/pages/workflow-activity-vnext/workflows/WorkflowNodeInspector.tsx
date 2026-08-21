import { CloseOutlined } from '@ant-design/icons';
import {
  Alert,
  Button,
  Collapse,
  Input,
  Modal,
  Select,
  Switch,
  Typography,
} from 'antd';
import React from 'react';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
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
import TechnicalDetails from '../TechnicalDetails';

export type WorkflowNodeInspectorHandle = {
  requestDiscardOrProceed: (proceed: () => void) => void;
};

type WorkflowNodeInspectorProps = {
  readonly disabled?: boolean;
  readonly error?: string;
  readonly onClose: () => void;
  readonly onConfigurationChange: (
    parametersText: string,
  ) => Promise<boolean> | boolean;
  readonly onConfigurationErrorChange: (error: string) => void;
  readonly onUnappliedChangesChange?: (hasUnappliedChanges: boolean) => void;
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
  const [rawErrorDetails, setRawErrorDetails] = React.useState('');
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
      setRawErrorDetails('');
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
    setRawErrorDetails('');
    setStructuredError('');
    setHasUnappliedChanges(false);
  }, [stepDraft?.id, stepDraft?.parametersText, stepDraft?.type]);

  return {
    configurationValues,
    hasUnappliedChanges,
    rawConfigurationText,
    rawError,
    rawErrorDetails,
    rememberSchemaParameters,
    schemaParameters,
    setConfigurationValues,
    setHasUnappliedChanges,
    setRawConfigurationText,
    setRawError,
    setRawErrorDetails,
    setStructuredError,
    structuredError,
  } as const;
}

const WorkflowNodeInspector = React.forwardRef<
  WorkflowNodeInspectorHandle,
  WorkflowNodeInspectorProps
>(
  (
    {
      disabled = false,
      error,
      onClose,
      onConfigurationChange,
      onConfigurationErrorChange,
      onUnappliedChangesChange,
      stepDraft,
    },
    ref,
  ) => {
    const [applying, setApplying] = React.useState(false);
    const [discardConfirmationOpen, setDiscardConfirmationOpen] =
      React.useState(false);
    const [pendingDiscardAction, setPendingDiscardAction] = React.useState<
      (() => void) | null
    >(null);
    const {
      configurationValues,
      hasUnappliedChanges,
      rawConfigurationText,
      rawError,
      rawErrorDetails,
      rememberSchemaParameters,
      schemaParameters,
      setConfigurationValues,
      setHasUnappliedChanges,
      setRawConfigurationText,
      setRawError,
      setRawErrorDetails,
      setStructuredError,
      structuredError,
    } = useConfigurationDraft(stepDraft);

    React.useEffect(() => {
      onConfigurationErrorChange(structuredError || rawError);
    }, [onConfigurationErrorChange, rawError, structuredError]);

    React.useEffect(() => {
      onUnappliedChangesChange?.(hasUnappliedChanges);
      return () => onUnappliedChangesChange?.(false);
    }, [hasUnappliedChanges, onUnappliedChangesChange]);

    const requestDiscardOrProceed = React.useCallback(
      (proceed: () => void) => {
        if (applying) return;
        if (hasUnappliedChanges) {
          setPendingDiscardAction(() => proceed);
          setDiscardConfirmationOpen(true);
          return;
        }
        proceed();
      },
      [applying, hasUnappliedChanges],
    );

    React.useImperativeHandle(ref, () => ({ requestDiscardOrProceed }), [
      requestDiscardOrProceed,
    ]);

    if (!stepDraft) return null;

    const parameters = readCurrentParameters(stepDraft, rawConfigurationText);
    const schema = getStudioNodeConfigurationSchema(
      stepDraft.type,
      schemaParameters,
    );
    const validationError = structuredError || rawError;
    const nodeTypeLabel = formatStudioStepTypeLabel(stepDraft.type);
    const controlsDisabled = disabled || applying;

    const setRawConfigurationError = (nextError: unknown) => {
      setRawError(
        t(
          'workflowActivityVNext.nodeInspector.rawConfigurationError',
          'Configuration must be a JSON object.',
        ),
      );
      setRawErrorDetails(nextError instanceof Error ? nextError.message : '');
    };

    const updateFieldValue = (fieldName: string, value: string) => {
      if (controlsDisabled) return;
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
      setRawErrorDetails('');
      setHasUnappliedChanges(true);
      if (result.valid) {
        setRawConfigurationText(
          formatRawStudioNodeConfiguration(result.parameters),
        );
        rememberSchemaParameters(result.parameters);
      }
    };

    const applyParametersText = async (parametersText: string) => {
      if (controlsDisabled) return;
      setApplying(true);
      try {
        const applied = await onConfigurationChange(parametersText);
        if (applied) {
          setHasUnappliedChanges(false);
          onConfigurationErrorChange('');
        }
      } catch (configurationError) {
        onConfigurationErrorChange(
          configurationError instanceof Error
            ? configurationError.message
            : String(configurationError),
        );
      } finally {
        setApplying(false);
      }
    };

    const applyConfiguration = () => {
      if (controlsDisabled) return;
      const result = applyStudioNodeConfigurationValuesWithValidation(
        stepDraft.type,
        parameters,
        configurationValues,
        schemaParameters,
      );
      const nextError = result.errors[0] ?? '';
      setStructuredError(nextError);
      setRawError('');
      setRawErrorDetails('');
      if (!result.valid) return;

      const nextRawText = formatRawStudioNodeConfiguration(result.parameters);
      setRawConfigurationText(nextRawText);
      rememberSchemaParameters(result.parameters);
      void applyParametersText(nextRawText);
    };

    const updateRawConfiguration = (value: string) => {
      if (controlsDisabled) return;
      setRawConfigurationText(value);
      setHasUnappliedChanges(true);
      try {
        const nextParameters = applyRawStudioNodeConfiguration(
          stepDraft.type,
          value,
        );
        const nextSchemaParameters = rememberSchemaParameters(nextParameters);
        setRawError('');
        setRawErrorDetails('');
        setStructuredError('');
        setConfigurationValues(
          readStudioNodeConfigurationValues(
            stepDraft.type,
            nextParameters,
            nextSchemaParameters,
          ),
        );
      } catch (nextError) {
        setRawConfigurationError(nextError);
      }
    };

    const applyRawConfiguration = () => {
      if (controlsDisabled) return;
      try {
        const nextParameters = applyRawStudioNodeConfiguration(
          stepDraft.type,
          rawConfigurationText,
        );
        const nextRawText = formatRawStudioNodeConfiguration(nextParameters);
        const nextSchemaParameters = rememberSchemaParameters(nextParameters);
        setRawError('');
        setRawErrorDetails('');
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
        setRawConfigurationError(nextError);
      }
    };

    const requestClose = () => {
      requestDiscardOrProceed(onClose);
    };

    const renderFieldControl = (field: StudioStructuredNodeConfigField) => {
      const value = configurationValues[field.name] ?? '';
      const control = field.control ?? field.kind;
      if (control === 'select') {
        return (
          <Select
            disabled={controlsDisabled}
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
            disabled={controlsDisabled}
            onChange={(checked) =>
              updateFieldValue(field.name, String(checked))
            }
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
            disabled={controlsDisabled}
            onChange={(event) =>
              updateFieldValue(field.name, event.target.value)
            }
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
          disabled={controlsDisabled}
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
            <AevatarTooltip
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
            </AevatarTooltip>
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
                {schema.fields.map((field) => (
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
                ))}
              </div>
              {validationError ? (
                <Alert
                  className="wa-vnext__node-inspector-error"
                  description={
                    rawErrorDetails ? (
                      <TechnicalDetails>{rawErrorDetails}</TechnicalDetails>
                    ) : undefined
                  }
                  message={validationError}
                  role="alert"
                  showIcon
                  type="error"
                />
              ) : null}
              {hasUnappliedChanges ? (
                <Typography.Text
                  className="wa-vnext__node-inspector-hint"
                  type="secondary"
                >
                  {t(
                    'workflowActivityVNext.nodeInspector.applyBeforeSave',
                    'Apply changes before saving this workflow.',
                  )}
                </Typography.Text>
              ) : null}
              {!validationError && error ? (
                <Alert
                  className="wa-vnext__node-inspector-error"
                  description={<TechnicalDetails>{error}</TechnicalDetails>}
                  message={t(
                    'workflowActivityVNext.nodeInspector.applyFailed',
                    "Couldn't apply configuration",
                  )}
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
                          {t(
                            'workflowActivityVNext.nodeInspector.type',
                            'Type',
                          )}
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
                        disabled={controlsDisabled}
                        onChange={(event) =>
                          updateRawConfiguration(event.target.value)
                        }
                        spellCheck={false}
                        status={rawError ? 'error' : undefined}
                        value={rawConfigurationText}
                      />
                      <Button
                        disabled={controlsDisabled || Boolean(rawError)}
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
                controlsDisabled ||
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
          onCancel={() => {
            setDiscardConfirmationOpen(false);
            setPendingDiscardAction(null);
          }}
          onOk={() => {
            const proceed = pendingDiscardAction;
            setDiscardConfirmationOpen(false);
            setPendingDiscardAction(null);
            setHasUnappliedChanges(false);
            proceed?.();
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
  },
);

WorkflowNodeInspector.displayName = 'WorkflowNodeInspector';

export default WorkflowNodeInspector;
