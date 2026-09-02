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
import {
  type ConsoleMessageDescriptor,
  formatConsoleMessage,
  t,
} from '@/shared/i18n/messages';
import {
  parseInspectorParameters,
  type StudioStepInspectorDraft,
} from '@/shared/studio/document';
import { formatStudioStepTypeLabel } from '@/shared/studio/graph';
import type { StudioWorkflowCapability } from '@/shared/studio/models';
import {
  applyRawStudioNodeConfiguration,
  applyStudioNodeConfigurationValuesWithValidation,
  formatRawStudioNodeConfiguration,
  getStudioNodeConfigurationSchema,
  readStudioNodeConfigurationValues,
  type StudioStructuredNodeConfigField,
} from '@/shared/studio/nodeConfigFields';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowToolCallConfiguration from './WorkflowToolCallConfiguration';

export type WorkflowNodeInspectorHandle = {
  requestDiscardOrProceed: (proceed: () => void) => void;
};

export type WorkflowNodeConfigurationChange = {
  readonly capability: StudioWorkflowCapability | null;
  readonly parametersText: string;
};

type WorkflowNodeInspectorProps = {
  readonly disabled?: boolean;
  readonly error?: string;
  readonly onClose: () => void;
  readonly onConfigurationChange: (
    change: WorkflowNodeConfigurationChange,
  ) => Promise<boolean> | boolean;
  readonly onConfigurationErrorChange: (error: string) => void;
  readonly onUnappliedChangesChange?: (hasUnappliedChanges: boolean) => void;
  readonly scopeId: string;
  readonly stepDraft: StudioStepInspectorDraft | null;
};

const STEP_PURPOSES: Readonly<Record<string, ConsoleMessageDescriptor>> = {
  assign: {
    id: 'workflowActivityVNext.nodeInspector.purpose.assign',
    defaultMessage: 'Store a value for later steps in this workflow.',
  },
  conditional: {
    id: 'workflowActivityVNext.nodeInspector.purpose.conditional',
    defaultMessage: 'Choose the next path by evaluating a condition.',
  },
  connector_call: {
    id: 'workflowActivityVNext.nodeInspector.purpose.connectorCall',
    defaultMessage: 'Call an operation provided by a configured connector.',
  },
  delay: {
    id: 'workflowActivityVNext.nodeInspector.purpose.delay',
    defaultMessage: 'Pause this workflow before the next step continues.',
  },
  emit: {
    id: 'workflowActivityVNext.nodeInspector.purpose.emit',
    defaultMessage:
      'Publish an event for another part of the workflow or system.',
  },
  human_approval: {
    id: 'workflowActivityVNext.nodeInspector.purpose.humanApproval',
    defaultMessage: 'Pause the workflow until a person approves or rejects it.',
  },
  human_input: {
    id: 'workflowActivityVNext.nodeInspector.purpose.humanInput',
    defaultMessage: 'Pause the workflow and collect input from a person.',
  },
  llm_call: {
    id: 'workflowActivityVNext.nodeInspector.purpose.llmCall',
    defaultMessage: 'Send an instruction and workflow input to an AI model.',
  },
  switch: {
    id: 'workflowActivityVNext.nodeInspector.purpose.switch',
    defaultMessage: 'Choose a branch by matching the current workflow value.',
  },
  tool_call: {
    id: 'workflowActivityVNext.nodeInspector.purpose.toolCall',
    defaultMessage:
      'Run an action from a connected service or registered tool.',
  },
  transform: {
    id: 'workflowActivityVNext.nodeInspector.purpose.transform',
    defaultMessage: 'Transform the current workflow value into the next value.',
  },
  wait_signal: {
    id: 'workflowActivityVNext.nodeInspector.purpose.waitSignal',
    defaultMessage: 'Pause the workflow until the expected signal arrives.',
  },
  workflow_call: {
    id: 'workflowActivityVNext.nodeInspector.purpose.workflowCall',
    defaultMessage: 'Run another workflow and use its result here.',
  },
};

function stepPurpose(stepType: string): string {
  const purpose = STEP_PURPOSES[stepType.trim().toLowerCase()];
  return purpose
    ? formatConsoleMessage(purpose)
    : t(
        'workflowActivityVNext.nodeInspector.purpose.default',
        'Configure how this step behaves when the workflow runs.',
      );
}

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
  if (!rawConfigurationText.trim()) return readDraftParameters(stepDraft);
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
  const [capability, setCapability] =
    React.useState<StudioWorkflowCapability | null>(null);
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
      setCapability(null);
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
    setCapability(stepDraft.capability);
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
  }, [
    stepDraft?.capability,
    stepDraft?.id,
    stepDraft?.parametersText,
    stepDraft?.type,
  ]);

  return {
    capability,
    configurationValues,
    hasUnappliedChanges,
    rawConfigurationText,
    rawError,
    rawErrorDetails,
    rememberSchemaParameters,
    schemaParameters,
    setCapability,
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
      scopeId,
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
    const [actionName, setActionName] = React.useState('');
    const {
      capability,
      configurationValues,
      hasUnappliedChanges,
      rawConfigurationText,
      rawError,
      rawErrorDetails,
      rememberSchemaParameters,
      schemaParameters,
      setCapability,
      setConfigurationValues,
      setHasUnappliedChanges,
      setRawConfigurationText,
      setRawError,
      setRawErrorDetails,
      setStructuredError,
      structuredError,
    } = useConfigurationDraft(stepDraft);

    React.useEffect(() => setActionName(''), [stepDraft?.id, stepDraft?.type]);

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
    const runtimeTool =
      typeof parameters.tool === 'string' ? parameters.tool.trim() : '';
    const usesGuidedToolCall =
      stepDraft.type.trim().toLowerCase() === 'tool_call' &&
      (!runtimeTool ||
        runtimeTool === 'nyxid_proxy' ||
        Boolean(capability?.nyxid_operation));
    const inspectorTitleName =
      actionName ||
      (usesGuidedToolCall
        ? t('workflowActivityVNext.nodeInspector.tool.action', 'Action')
        : nodeTypeLabel);

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

    const applyParametersText = async (
      parametersText: string,
      nextCapability: StudioWorkflowCapability | null,
    ) => {
      if (controlsDisabled) return;
      setApplying(true);
      try {
        const applied = await onConfigurationChange({
          capability: nextCapability,
          parametersText,
        });
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
      if (usesGuidedToolCall) {
        try {
          const nextParameters = applyRawStudioNodeConfiguration(
            stepDraft.type,
            rawConfigurationText,
          );
          const nextRawText = formatRawStudioNodeConfiguration(nextParameters);
          setRawConfigurationText(nextRawText);
          setRawError('');
          setRawErrorDetails('');
          rememberSchemaParameters(nextParameters);
          void applyParametersText(nextRawText, capability);
        } catch (nextError) {
          setRawConfigurationError(nextError);
        }
        return;
      }
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
      void applyParametersText(nextRawText, capability);
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

    const updateGuidedToolCall = (change: {
      readonly capability: StudioWorkflowCapability | null;
      readonly parameters: Record<string, unknown>;
    }) => {
      if (controlsDisabled) return;
      const nextRawText = formatRawStudioNodeConfiguration(change.parameters);
      const nextSchemaParameters = rememberSchemaParameters(change.parameters);
      setCapability(change.capability);
      setRawConfigurationText(nextRawText);
      setConfigurationValues(
        readStudioNodeConfigurationValues(
          stepDraft.type,
          change.parameters,
          nextSchemaParameters,
        ),
      );
      setRawError('');
      setRawErrorDetails('');
      setHasUnappliedChanges(true);
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
                  { name: inspectorTitleName },
                )}
              </Typography.Title>
              <Typography.Text className="wa-vnext__node-inspector-subtitle">
                {nodeTypeLabel}
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
            <Typography.Paragraph className="wa-vnext__node-inspector-purpose">
              {stepPurpose(stepDraft.type)}
            </Typography.Paragraph>
            <section aria-labelledby="wa-vnext-node-configuration-title">
              <Typography.Title
                className="wa-vnext__node-inspector-section-title"
                id="wa-vnext-node-configuration-title"
                level={5}
              >
                {t(
                  'workflowActivityVNext.nodeInspector.configuration',
                  'Settings',
                )}
              </Typography.Title>
              {usesGuidedToolCall ? (
                <WorkflowToolCallConfiguration
                  capability={capability}
                  disabled={controlsDisabled}
                  onActionNameChange={setActionName}
                  onChange={updateGuidedToolCall}
                  onErrorChange={setStructuredError}
                  parameters={parameters}
                  scopeId={scopeId}
                />
              ) : (
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
              )}
              {validationError ? (
                <Alert
                  className="wa-vnext__node-inspector-error"
                  description={
                    rawErrorDetails ? (
                      <TechnicalDetails
                        summary={t(
                          'workflowActivityVNext.nodeInspector.errorDetails',
                          'Error details',
                        )}
                      >
                        {rawErrorDetails}
                      </TechnicalDetails>
                    ) : undefined
                  }
                  title={validationError}
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
                    'Apply this step before saving the workflow.',
                  )}
                </Typography.Text>
              ) : null}
              {!validationError && error ? (
                <Alert
                  className="wa-vnext__node-inspector-error"
                  description={
                    <TechnicalDetails
                      summary={t(
                        'workflowActivityVNext.nodeInspector.errorDetails',
                        'Error details',
                      )}
                    >
                      {error}
                    </TechnicalDetails>
                  }
                  title={t(
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
                            'workflowActivityVNext.nodeInspector.stepId',
                            'Step ID',
                          )}
                        </dt>
                        <dd>{stepDraft.id}</dd>
                      </div>
                      <div>
                        <dt>
                          {t(
                            'workflowActivityVNext.nodeInspector.type',
                            'Type',
                          )}
                        </dt>
                        <dd>{stepDraft.type}</dd>
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
                      {stepDraft.type.trim().toLowerCase() === 'tool_call' ? (
                        <>
                          <div>
                            <dt>
                              {t(
                                'workflowActivityVNext.nodeInspector.runtimeTool',
                                'Runtime tool',
                              )}
                            </dt>
                            <dd>{displayValue(runtimeTool)}</dd>
                          </div>
                          <div>
                            <dt>
                              {t(
                                'workflowActivityVNext.nodeInspector.userServiceId',
                                'User service ID',
                              )}
                            </dt>
                            <dd>
                              {displayValue(
                                capability?.nyxid_operation?.user_service_id ??
                                  '',
                              )}
                            </dd>
                          </div>
                          <div>
                            <dt>
                              {t(
                                'workflowActivityVNext.nodeInspector.endpointId',
                                'Endpoint ID',
                              )}
                            </dt>
                            <dd>
                              {displayValue(
                                capability?.nyxid_operation?.endpoint_id ?? '',
                              )}
                            </dd>
                          </div>
                        </>
                      ) : null}
                    </dl>
                  ),
                  key: 'step-details',
                  label: t(
                    'workflowActivityVNext.nodeInspector.stepDetails',
                    'Technical details',
                  ),
                },
                {
                  children: (
                    <div className="wa-vnext__node-inspector-advanced">
                      <Typography.Paragraph className="wa-vnext__node-inspector-description">
                        {t(
                          'workflowActivityVNext.nodeInspector.advancedDescription',
                          "Edit this step's runtime parameters as JSON.",
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
                    </div>
                  ),
                  key: 'advanced',
                  label: t(
                    'workflowActivityVNext.nodeInspector.advanced',
                    'Advanced JSON',
                  ),
                },
              ]}
            />
          </div>
          <footer className="wa-vnext__node-inspector-actions">
            <Button disabled={applying} onClick={requestClose}>
              {t('workflowActivityVNext.common.cancel', 'Cancel')}
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
                'Apply step',
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
