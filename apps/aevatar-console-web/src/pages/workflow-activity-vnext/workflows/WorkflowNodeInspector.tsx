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
  resolveStepParameterName,
  type StudioStepInspectorDraft,
} from '@/shared/studio/document';
import { formatStudioStepTypeLabel } from '@/shared/studio/graph';
import {
  applyRawStudioNodeConfiguration,
  applyStudioNodeConfigurationValuesWithValidation,
  formatRawStudioNodeConfiguration,
  getStudioNodeConfigurationSchema,
  hasStudioNodeConfigurationSchema,
  readStudioNodeConfigurationValues,
  type StudioStructuredNodeConfigField,
} from '@/shared/studio/nodeConfigFields';
import { isNodeConfigurationExpression } from '@/shared/studio/nodeConfiguration';
import NodeConfigurationCatalogStatus from '@/shared/studio/NodeConfigurationCatalogStatus';
import { useNodeConfigurationDescriptor } from '@/shared/studio/useNodeConfigurationDescriptor';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import TechnicalDetails from '../TechnicalDetails';
import StructuredJsonArgumentsEditor from './StructuredJsonArgumentsEditor';

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

const STEP_PURPOSES: Readonly<Record<string, ConsoleMessageDescriptor>> = {
  assign: {
    id: 'workflowActivityVNext.nodeInspector.purpose.assign',
    defaultMessage: 'Store a value for later steps in this workflow.',
  },
  cache: {
    id: 'workflowActivityVNext.nodeInspector.purpose.cache',
    defaultMessage: 'Reuse a previous result when the same cache key appears.',
  },
  checkpoint: {
    id: 'workflowActivityVNext.nodeInspector.purpose.checkpoint',
    defaultMessage: 'Record a named recovery point in this workflow.',
  },
  conditional: {
    id: 'workflowActivityVNext.nodeInspector.purpose.conditional',
    defaultMessage: 'Continue only when the configured condition is true.',
  },
  connector_call: {
    id: 'workflowActivityVNext.nodeInspector.purpose.connectorCall',
    defaultMessage: 'Call an operation provided by a configured connector.',
  },
  delay: {
    id: 'workflowActivityVNext.nodeInspector.purpose.delay',
    defaultMessage: 'Pause this workflow before the next step continues.',
  },
  dynamic_workflow: {
    id: 'workflowActivityVNext.nodeInspector.purpose.dynamicWorkflow',
    defaultMessage: 'Create and run workflow steps from generated YAML.',
  },
  emit: {
    id: 'workflowActivityVNext.nodeInspector.purpose.emit',
    defaultMessage: 'Publish an event for another workflow or system listener.',
  },
  evaluate: {
    id: 'workflowActivityVNext.nodeInspector.purpose.evaluate',
    defaultMessage: 'Score the current result against clear criteria.',
  },
  foreach: {
    id: 'workflowActivityVNext.nodeInspector.purpose.foreach',
    defaultMessage: 'Run the same child step for every input item.',
  },
  guard: {
    id: 'workflowActivityVNext.nodeInspector.purpose.guard',
    defaultMessage: 'Check the input before allowing the workflow to continue.',
  },
  human_approval: {
    id: 'workflowActivityVNext.nodeInspector.purpose.humanApproval',
    defaultMessage: 'Pause until a person approves or rejects the next action.',
  },
  human_input: {
    id: 'workflowActivityVNext.nodeInspector.purpose.humanInput',
    defaultMessage: 'Pause and collect information from a person.',
  },
  llm_call: {
    id: 'workflowActivityVNext.nodeInspector.purpose.llmCall',
    defaultMessage: 'Send an instruction and workflow input to an AI model.',
  },
  map_reduce: {
    id: 'workflowActivityVNext.nodeInspector.purpose.mapReduce',
    defaultMessage:
      'Process input chunks separately, then combine the results.',
  },
  parallel: {
    id: 'workflowActivityVNext.nodeInspector.purpose.parallel',
    defaultMessage:
      'Run several workers at the same time and combine their work.',
  },
  race: {
    id: 'workflowActivityVNext.nodeInspector.purpose.race',
    defaultMessage: 'Run several workers and continue with the first results.',
  },
  reflect: {
    id: 'workflowActivityVNext.nodeInspector.purpose.reflect',
    defaultMessage:
      'Review and improve a result for a limited number of rounds.',
  },
  retrieve_facts: {
    id: 'workflowActivityVNext.nodeInspector.purpose.retrieveFacts',
    defaultMessage: 'Find relevant facts to use in later workflow steps.',
  },
  switch: {
    id: 'workflowActivityVNext.nodeInspector.purpose.switch',
    defaultMessage: 'Choose the next branch by matching the current value.',
  },
  tool_call: {
    id: 'workflowActivityVNext.nodeInspector.purpose.toolCall',
    defaultMessage: 'Run a registered tool with the input you provide.',
  },
  transform: {
    id: 'workflowActivityVNext.nodeInspector.purpose.transform',
    defaultMessage: 'Transform the current value before the next step uses it.',
  },
  vote: {
    id: 'workflowActivityVNext.nodeInspector.purpose.vote',
    defaultMessage: 'Choose a result from the available worker responses.',
  },
  wait_signal: {
    id: 'workflowActivityVNext.nodeInspector.purpose.waitSignal',
    defaultMessage: 'Pause until the expected signal arrives or time runs out.',
  },
  while: {
    id: 'workflowActivityVNext.nodeInspector.purpose.while',
    defaultMessage: 'Repeat a child step while the condition remains true.',
  },
  workflow_call: {
    id: 'workflowActivityVNext.nodeInspector.purpose.workflowCall',
    defaultMessage: 'Run another workflow and use its result here.',
  },
  workflow_yaml_validate: {
    id: 'workflowActivityVNext.nodeInspector.purpose.workflowYamlValidate',
    defaultMessage: 'Check generated workflow YAML before it is used.',
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
  try {
    return parseInspectorParameters(rawConfigurationText);
  } catch {
    return readDraftParameters(stepDraft);
  }
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
  const [rawError, setRawError] = React.useState('');
  const [rawErrorDetails, setRawErrorDetails] = React.useState('');
  const [structuredError, setStructuredError] = React.useState('');
  const [hasUnappliedChanges, setHasUnappliedChanges] = React.useState(false);
  React.useEffect(() => {
    if (!stepDraft) {
      setConfigurationValues({});
      setRawConfigurationText('');
      setRawError('');
      setRawErrorDetails('');
      setStructuredError('');
      setHasUnappliedChanges(false);
      return;
    }

    const parameters = readDraftParameters(stepDraft);
    setConfigurationValues({});
    setRawConfigurationText(formatRawStudioNodeConfiguration(parameters));
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
    const [argumentsEditorError, setArgumentsEditorError] = React.useState('');
    const [pendingDiscardAction, setPendingDiscardAction] = React.useState<
      (() => void) | null
    >(null);
    const {
      configurationValues,
      hasUnappliedChanges,
      rawConfigurationText,
      rawError,
      rawErrorDetails,
      setConfigurationValues,
      setHasUnappliedChanges,
      setRawConfigurationText,
      setRawError,
      setRawErrorDetails,
      setStructuredError,
      structuredError,
    } = useConfigurationDraft(stepDraft);
    const { descriptor, query: descriptorQuery } =
      useNodeConfigurationDescriptor(stepDraft?.type);

    const stepType = stepDraft?.type ?? '';
    const schema = getStudioNodeConfigurationSchema(stepType, descriptor);
    const hasSchema = hasStudioNodeConfigurationSchema(stepType, descriptor);
    const coveredParameters = new Set(schema.fields.map((field) =>
      resolveStepParameterName(stepType, field.parameterName),
    ));
    const uncoveredEdits = Object.fromEntries(Object.entries(configurationValues)
      .filter(([name]) => !coveredParameters.has(name)));
    const parameters = {
      ...(stepDraft ? readCurrentParameters(stepDraft, rawConfigurationText) : {}),
      ...uncoveredEdits,
    };
    const fieldValues = readStudioNodeConfigurationValues(stepType, parameters, descriptor);
    for (const field of schema.fields) {
      const parameterName = resolveStepParameterName(stepType, field.parameterName);
      if (Object.hasOwn(configurationValues, parameterName)) {
        fieldValues[field.name] = configurationValues[parameterName];
      }
    }
    const currentStructuredError = structuredError
      ? applyStudioNodeConfigurationValuesWithValidation(stepType, parameters, fieldValues, descriptor).errors[0] ?? ''
      : '';
    const argumentsField = schema.fields.find(
      (field) => field.parameterName === 'arguments',
    );
    const usesStructuredArgumentsEditor =
      stepType.trim().toLowerCase() === 'tool_call' &&
      Boolean(argumentsField) &&
      !isNodeConfigurationExpression(
        argumentsField ? fieldValues[argumentsField.name] ?? '' : '',
      );
    const currentArgumentsError = usesStructuredArgumentsEditor
      ? argumentsEditorError
      : '';
    const validationError = currentStructuredError || currentArgumentsError || rawError;
    const displayedRawConfiguration = Object.keys(uncoveredEdits).length && !rawError
      ? formatRawStudioNodeConfiguration(parameters)
      : rawConfigurationText;

    React.useEffect(() => {
      onConfigurationErrorChange(
        validationError,
      );
    }, [
      onConfigurationErrorChange,
      validationError,
    ]);

    React.useEffect(() => {
      setArgumentsEditorError('');
    }, [stepDraft?.id, stepDraft?.type]);

    React.useEffect(() => {
      if (!usesStructuredArgumentsEditor) setArgumentsEditorError('');
    }, [usesStructuredArgumentsEditor]);

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
      const nextValues = { ...fieldValues, [fieldName]: value };
      const result = applyStudioNodeConfigurationValuesWithValidation(
        stepDraft.type,
        parameters,
        nextValues,
        descriptor,
      );
      const field = schema.fields.find((entry) => entry.name === fieldName);
      const parameterName = resolveStepParameterName(
        stepDraft.type,
        field?.parameterName ?? fieldName,
      );
      setConfigurationValues((current) => ({ ...current, [parameterName]: value }));
      setStructuredError(result.errors[0] ?? '');
      setRawError('');
      setRawErrorDetails('');
      setHasUnappliedChanges(true);
      if (result.valid) {
        setRawConfigurationText(
          formatRawStudioNodeConfiguration(result.parameters),
        );
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
      if (controlsDisabled || currentArgumentsError) return;
      const result = applyStudioNodeConfigurationValuesWithValidation(
        stepDraft.type,
        parameters,
        fieldValues,
        descriptor,
      );
      const nextError = result.errors[0] ?? '';
      setStructuredError(nextError);
      setRawError('');
      setRawErrorDetails('');
      if (!result.valid) return;

      const nextRawText = formatRawStudioNodeConfiguration(result.parameters);
      setRawConfigurationText(nextRawText);
      void applyParametersText(nextRawText);
    };

    const updateRawConfiguration = (value: string) => {
      if (controlsDisabled) return;
      setRawConfigurationText(value);
      setHasUnappliedChanges(true);
      try {
        applyRawStudioNodeConfiguration(
          stepDraft.type,
          value,
        );
        setRawError('');
        setRawErrorDetails('');
        setStructuredError('');
        setConfigurationValues({});
      } catch (nextError) {
        setRawConfigurationError(nextError);
      }
    };

    const requestClose = () => {
      requestDiscardOrProceed(onClose);
    };

    const renderFieldControl = (field: StudioStructuredNodeConfigField) => {
      const value = fieldValues[field.name] ?? '';
      const control = field.control ?? field.kind;
      if (
        usesStructuredArgumentsEditor &&
        field.parameterName === 'arguments'
      ) {
        return (
          <StructuredJsonArgumentsEditor
            disabled={controlsDisabled}
            key={`${stepDraft.id}:${field.name}`}
            onChange={(nextValue) => updateFieldValue(field.name, nextValue)}
            onErrorChange={setArgumentsEditorError}
            value={value}
          />
        );
      }
      if (control === 'select' && !isNodeConfigurationExpression(value)) {
        return (
          <Select
            aria-required={field.required}
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
            status={currentStructuredError ? 'error' : undefined}
            value={value || undefined}
          />
        );
      }
      if (control === 'boolean' && ['', 'true', 'false'].includes(value)) {
        return (
          <Switch
            aria-label={formatConsoleMessage(field.label)}
            aria-required={field.required}
            checked={(value || String(field.defaultValue ?? '')) === 'true'}
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
            aria-required={field.required}
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
            status={currentStructuredError ? 'error' : undefined}
            value={value}
          />
        );
      }
      return (
        <Input
          aria-label={formatConsoleMessage(field.label)}
          aria-required={field.required}
          disabled={controlsDisabled}
          inputMode={control === 'number' ? 'decimal' : undefined}
          onChange={(event) => updateFieldValue(field.name, event.target.value)}
          placeholder={
            field.placeholder
              ? formatConsoleMessage(field.placeholder)
              : undefined
          }
          status={currentStructuredError ? 'error' : undefined}
          value={value}
        />
      );
    };

    const renderField = (field: StudioStructuredNodeConfigField) => {
      const guidance = field.description
        ? formatConsoleMessage(field.description)
        : field.placeholder
          ? t(
              'workflowActivityVNext.nodeInspector.fieldExample',
              'Example: {value}',
              { value: formatConsoleMessage(field.placeholder) },
            )
          : '';

      return (
        <div className="wa-vnext__node-inspector-field" key={field.name}>
          <div className="wa-vnext__node-inspector-field-heading">
            <span>{formatConsoleMessage(field.label)}</span>
            <small>
              {field.required
                ? t(
                    'workflowActivityVNext.nodeInspector.fieldRequired',
                    'Required',
                  )
                : t(
                    'workflowActivityVNext.nodeInspector.fieldOptional',
                    'Optional',
                  )}
            </small>
          </div>
          {renderFieldControl(field)}
          {guidance ? <small>{guidance}</small> : null}
        </div>
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
            <section
              aria-labelledby="wa-vnext-node-configuration-title"
              className="wa-vnext__node-inspector-settings"
            >
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
              <NodeConfigurationCatalogStatus
                cached={Boolean(descriptor)}
                fallback={!descriptor && hasSchema}
                failed={descriptorQuery.isError}
                loading={descriptorQuery.isPending}
                missing={!descriptor}
                onRetry={() => { void descriptorQuery.refetch(); }}
                retrying={descriptorQuery.isFetching}
              />
              <div className="wa-vnext__node-inspector-fields">
                {descriptorQuery.isPending ? null : schema.fields.length > 0 ? (
                  schema.fields.map(renderField)
                ) : hasSchema ? (
                  <Typography.Text
                    className="wa-vnext__node-inspector-empty"
                    type="secondary"
                  >
                    {t(
                      'workflowActivityVNext.nodeInspector.noSettings',
                      'No settings are needed for this step.',
                    )}
                  </Typography.Text>
                ) : null}
              </div>
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
                        value={displayedRawConfiguration}
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
                Boolean(currentStructuredError) ||
                Boolean(currentArgumentsError) ||
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
