import {
  CheckCircleOutlined,
  ExclamationCircleOutlined,
  ReloadOutlined,
  WarningOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Input, Select, Tag, Typography } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';
import { studioApi } from '@/shared/studio/api';
import type {
  StudioWorkflowCapability,
  StudioWorkflowCapabilityDescriptor,
} from '@/shared/studio/models';
import {
  capabilitySelectorKey,
  formatToolArguments,
  listOperationInputFields,
  type NyxIdOperationSelector,
  type OperationInputField,
  parseToolArguments,
  readOperationInputValue,
  reconcileOperationResponseMode,
  toDocumentCapability,
  writeOperationInputValue,
} from './toolCallConfiguration';

export type WorkflowToolCallConfigurationChange = {
  readonly capability: StudioWorkflowCapability | null;
  readonly parameters: Record<string, unknown>;
};

type WorkflowToolCallConfigurationProps = {
  readonly capability: StudioWorkflowCapability | null;
  readonly disabled: boolean;
  readonly onActionNameChange?: (name: string) => void;
  readonly onChange: (change: WorkflowToolCallConfigurationChange) => void;
  readonly onErrorChange: (error: string) => void;
  readonly parameters: Record<string, unknown>;
  readonly scopeId: string;
};

type OperationFieldValidation = {
  readonly blockingErrors: Readonly<Record<string, string>>;
  readonly missingRequired: Readonly<Record<string, string>>;
};

type OperationInputStatus = 'error' | 'warning' | undefined;

function selectorFromCapability(
  capability: StudioWorkflowCapability | null,
): NyxIdOperationSelector | null {
  const operation = capability?.nyxid_operation;
  if (!operation) return null;
  return {
    kind: 'nyxid_operation',
    userServiceId: operation.user_service_id,
    endpointId: operation.endpoint_id,
  };
}

function riskLabel(
  descriptor: StudioWorkflowCapabilityDescriptor | undefined,
): string | null {
  if (!descriptor) return null;
  if (descriptor.destructive) {
    return t(
      'workflowActivityVNext.nodeInspector.tool.risk.destructive',
      'Destructive',
    );
  }
  return descriptor.readOnly
    ? t('workflowActivityVNext.nodeInspector.tool.risk.readOnly', 'Read only')
    : t('workflowActivityVNext.nodeInspector.tool.risk.write', 'Writes data');
}

function currentInputText(value: unknown): string {
  if (value == null) return '';
  if (typeof value === 'string') return value;
  if (typeof value === 'object') return JSON.stringify(value, null, 2);
  return String(value);
}

function isEmptyInputValue(value: unknown): boolean {
  return (
    value == null || (typeof value === 'string' && value.trim().length === 0)
  );
}

function validateOperationFields(
  argumentsValue: Record<string, unknown>,
  fields: readonly OperationInputField[],
): OperationFieldValidation {
  const blockingErrors: Record<string, string> = {};
  const missingRequired: Record<string, string> = {};
  for (const field of fields) {
    const value = readOperationInputValue(argumentsValue, field);
    const result = writeOperationInputValue(argumentsValue, field, value);
    if (!result.error) continue;
    if (field.required && isEmptyInputValue(value)) {
      missingRequired[field.key] = result.error;
    } else {
      blockingErrors[field.key] = result.error;
    }
  }
  return { blockingErrors, missingRequired };
}

function inputLocationLabel(group: 'path' | 'query' | 'header'): string {
  if (group === 'path') {
    return t('workflowActivityVNext.nodeInspector.tool.location.path', 'Path');
  }
  if (group === 'header') {
    return t(
      'workflowActivityVNext.nodeInspector.tool.location.header',
      'Header',
    );
  }
  return t('workflowActivityVNext.nodeInspector.tool.location.query', 'Query');
}

function allowedValueLabel(field: OperationInputField, value: string): string {
  if (field.group !== 'response') return value;
  return value === 'file_artifact'
    ? t('workflowActivityVNext.nodeInspector.tool.response.file', 'File')
    : t('workflowActivityVNext.nodeInspector.tool.response.text', 'Text');
}

function remediationHref(locator: string, scopeId: string): string | null {
  if (locator.trim() !== 'nyxid:services') return null;
  return `/scopes/${encodeURIComponent(scopeId.trim())}/workflow-activity-vnext/settings?section=account`;
}

function StructuredOperationInput({
  controlId,
  describedBy,
  disabled,
  field,
  invalid,
  required,
  status,
  value,
  onCommit,
}: {
  readonly controlId: string;
  readonly describedBy: string;
  readonly disabled: boolean;
  readonly field: OperationInputField;
  readonly invalid: boolean;
  readonly required: boolean;
  readonly status: OperationInputStatus;
  readonly value: unknown;
  readonly onCommit: (value: string) => void;
}) {
  const [draft, setDraft] = React.useState(() => currentInputText(value));
  React.useEffect(() => setDraft(currentInputText(value)), [value]);

  return (
    <Input.TextArea
      aria-describedby={describedBy}
      aria-invalid={invalid}
      aria-required={required}
      aria-label={field.label}
      autoSize={{ minRows: 3, maxRows: 8 }}
      disabled={disabled}
      id={controlId}
      onBlur={() => onCommit(draft)}
      onChange={(event) => setDraft(event.target.value)}
      placeholder={field.schema.valueKind === 'array' ? '[ ]' : '{ }'}
      spellCheck={false}
      status={status}
      value={draft}
    />
  );
}

function OperationInputControl({
  controlId,
  describedBy,
  disabled,
  field,
  invalid,
  required,
  status,
  value,
  onCommit,
}: {
  readonly controlId: string;
  readonly describedBy: string;
  readonly disabled: boolean;
  readonly field: OperationInputField;
  readonly invalid: boolean;
  readonly required: boolean;
  readonly status: OperationInputStatus;
  readonly value: unknown;
  readonly onCommit: (value: unknown) => void;
}) {
  if (field.schema.allowedValues.length > 0) {
    return (
      <Select
        aria-describedby={describedBy}
        aria-invalid={invalid}
        aria-required={required}
        aria-label={field.label}
        disabled={disabled}
        id={controlId}
        onChange={onCommit}
        options={field.schema.allowedValues.map((entry) => ({
          label: allowedValueLabel(field, entry),
          value: entry,
        }))}
        placeholder={t(
          'workflowActivityVNext.nodeInspector.tool.chooseValue',
          'Choose a value',
        )}
        status={status}
        value={value == null ? undefined : String(value)}
      />
    );
  }
  if (field.schema.valueKind === 'boolean') {
    return (
      <Select
        allowClear
        aria-describedby={describedBy}
        aria-invalid={invalid}
        aria-required={required}
        aria-label={field.label}
        disabled={disabled}
        id={controlId}
        onChange={(nextValue) =>
          onCommit(nextValue === undefined ? undefined : nextValue === 'true')
        }
        options={[
          {
            label: t('workflowActivityVNext.common.yes', 'Yes'),
            value: 'true',
          },
          {
            label: t('workflowActivityVNext.common.no', 'No'),
            value: 'false',
          },
        ]}
        placeholder={t(
          'workflowActivityVNext.nodeInspector.tool.chooseValue',
          'Choose a value',
        )}
        status={status}
        value={value == null ? undefined : String(value)}
      />
    );
  }
  if (
    field.schema.valueKind === 'object' ||
    field.schema.valueKind === 'array'
  ) {
    return (
      <StructuredOperationInput
        controlId={controlId}
        describedBy={describedBy}
        disabled={disabled}
        field={field}
        invalid={invalid}
        required={required}
        status={status}
        onCommit={onCommit}
        value={value}
      />
    );
  }
  return (
    <Input
      aria-describedby={describedBy}
      aria-invalid={invalid}
      aria-required={required}
      aria-label={field.label}
      disabled={disabled}
      id={controlId}
      onChange={(event) => onCommit(event.target.value)}
      placeholder={
        field.schema.valueKind === 'integer' ||
        field.schema.valueKind === 'number'
          ? t(
              'workflowActivityVNext.nodeInspector.tool.numberPlaceholder',
              'Enter a number or workflow expression',
            )
          : t(
              'workflowActivityVNext.nodeInspector.tool.valuePlaceholder',
              'Enter a value or workflow expression',
            )
      }
      status={status}
      value={currentInputText(value)}
    />
  );
}

export default function WorkflowToolCallConfiguration({
  capability,
  disabled,
  onActionNameChange,
  onChange,
  onErrorChange,
  parameters,
  scopeId,
}: WorkflowToolCallConfigurationProps) {
  const selector = React.useMemo(
    () => selectorFromCapability(capability),
    [capability],
  );
  const selectorKey = selector ? capabilitySelectorKey(selector) : '';
  const [transientFieldErrors, setTransientFieldErrors] = React.useState<{
    readonly stateKey: string;
    readonly errors: Readonly<Record<string, string>>;
  }>({ stateKey: '', errors: {} });
  const discovery = useQuery({
    queryKey: ['workflow-capabilities', scopeId],
    queryFn: () => studioApi.listWorkflowCapabilities(scopeId),
    enabled: Boolean(scopeId.trim()),
    retry: false,
  });
  const descriptors = React.useMemo(
    () =>
      (discovery.data?.capabilities ?? []).filter(
        (
          descriptor,
        ): descriptor is StudioWorkflowCapabilityDescriptor & {
          readonly selector: NyxIdOperationSelector;
        } => descriptor.selector.kind === 'nyxid_operation',
      ),
    [discovery.data],
  );
  const selectedDescriptor = descriptors.find(
    (descriptor) => capabilitySelectorKey(descriptor.selector) === selectorKey,
  );
  const readiness = useQuery({
    queryKey: ['workflow-capability-readiness', scopeId, selectorKey],
    queryFn: () => {
      if (!selector) throw new Error('Capability selector is required.');
      return studioApi.inspectWorkflowCapabilityReadiness({
        scopeId,
        selector,
        executionMode: 'interactive',
      });
    },
    enabled: Boolean(scopeId.trim() && selector),
    retry: false,
  });
  const parsedArguments = React.useMemo(
    () => parseToolArguments(parameters.arguments),
    [parameters.arguments],
  );
  const settledReadiness = readiness.isFetching ? null : readiness.data;
  const operation = settledReadiness?.selectedOperation ?? null;
  const fields = React.useMemo(
    () => (operation ? listOperationInputFields(operation) : []),
    [operation],
  );
  const fieldStateKey = `${selectorKey}\u0000${parsedArguments.originalText}`;
  const fieldErrors =
    transientFieldErrors.stateKey === fieldStateKey
      ? transientFieldErrors.errors
      : {};
  const fieldValidation = React.useMemo(
    () =>
      parsedArguments.error
        ? { blockingErrors: {}, missingRequired: {} }
        : validateOperationFields(parsedArguments.arguments, fields),
    [fields, parsedArguments],
  );
  const configurationError =
    parsedArguments.error ??
    Object.values(fieldErrors).find(Boolean) ??
    Object.values(fieldValidation.blockingErrors).find(Boolean) ??
    '';
  const hasMissingRequiredInputs =
    Object.keys(fieldValidation.missingRequired).length > 0;

  React.useEffect(
    () => onErrorChange(configurationError),
    [configurationError, onErrorChange],
  );
  React.useEffect(() => {
    onActionNameChange?.(selectedDescriptor?.displayName ?? '');
  }, [onActionNameChange, selectedDescriptor?.displayName]);
  React.useEffect(() => {
    if (!operation || parsedArguments.error) return;
    const result = reconcileOperationResponseMode(
      parsedArguments.arguments,
      operation,
    );
    if (!result.changed) return;
    onChange({
      capability,
      parameters: {
        ...parameters,
        tool: 'nyxid_proxy',
        arguments: formatToolArguments(result.arguments),
      },
    });
  }, [capability, onChange, operation, parameters, parsedArguments]);

  const selectAction = (nextKey: string | undefined) => {
    if (!nextKey) {
      const nextParameters = { ...parameters };
      delete nextParameters.tool;
      delete nextParameters.arguments;
      onChange({ capability: null, parameters: nextParameters });
      return;
    }
    const descriptor = descriptors.find(
      (entry) => capabilitySelectorKey(entry.selector) === nextKey,
    );
    if (!descriptor) return;
    onChange({
      capability: toDocumentCapability(descriptor.selector),
      parameters: {
        ...parameters,
        tool: 'nyxid_proxy',
        arguments: parameters.arguments == null ? '{}' : parameters.arguments,
      },
    });
  };

  const commitField = (field: OperationInputField, rawValue: unknown) => {
    const result = writeOperationInputValue(
      parsedArguments.arguments,
      field,
      rawValue,
    );
    setTransientFieldErrors((current) => ({
      stateKey: fieldStateKey,
      errors: {
        ...(current.stateKey === fieldStateKey ? current.errors : {}),
        [field.key]: result.error ?? '',
      },
    }));
    if (result.error && result.arguments === parsedArguments.arguments) return;
    onChange({
      capability,
      parameters: {
        ...parameters,
        tool: 'nyxid_proxy',
        arguments: formatToolArguments(result.arguments),
      },
    });
  };

  const actionOptions = descriptors.map((descriptor) => {
    const risk = riskLabel(descriptor);
    return {
      displayName: descriptor.displayName,
      label: risk
        ? `${descriptor.displayName} · ${risk}`
        : descriptor.displayName,
      risk,
      value: capabilitySelectorKey(descriptor.selector),
    };
  });
  if (selector && !selectedDescriptor) {
    const savedActionLabel = t(
      'workflowActivityVNext.nodeInspector.tool.savedAction',
      'Saved action',
    );
    actionOptions.unshift({
      displayName: savedActionLabel,
      label: savedActionLabel,
      risk: null,
      value: selectorKey,
    });
  }

  return (
    <div className="wa-vnext__tool-config">
      <div className="wa-vnext__node-inspector-field">
        <label htmlFor="workflow-tool-action">
          {t('workflowActivityVNext.nodeInspector.tool.action', 'Action')}
        </label>
        <Typography.Text className="wa-vnext__node-inspector-help">
          {t(
            'workflowActivityVNext.nodeInspector.tool.actionHelp',
            'Choose what external service action this step should run.',
          )}
        </Typography.Text>
        <Select
          allowClear
          disabled={disabled || discovery.isLoading || discovery.isError}
          id="workflow-tool-action"
          loading={discovery.isLoading}
          labelRender={(option) =>
            actionOptions.find((entry) => entry.value === option.value)
              ?.displayName ?? option.label
          }
          onChange={selectAction}
          optionRender={(option) => {
            const action = actionOptions.find(
              (entry) => entry.value === option.value,
            );
            if (!action) return option.label;
            return (
              <div className="wa-vnext__tool-config-option">
                <span>{action.displayName}</span>
                {action.risk ? <small>{action.risk}</small> : null}
              </div>
            );
          }}
          options={actionOptions}
          placeholder={t(
            'workflowActivityVNext.nodeInspector.tool.actionPlaceholder',
            'Choose what this step should do',
          )}
          showSearch
          optionFilterProp="label"
          value={selectorKey || undefined}
        />
      </div>

      {discovery.isError ? (
        <Alert
          action={
            <Button
              disabled={disabled}
              icon={<ReloadOutlined />}
              onClick={() => void discovery.refetch()}
              size="small"
            >
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
          }
          title={t(
            'workflowActivityVNext.nodeInspector.tool.discoveryFailed',
            'Connected actions could not be loaded.',
          )}
          showIcon
          type="error"
        />
      ) : null}
      {discovery.isSuccess && descriptors.length === 0 ? (
        <Alert
          description={
            discovery.data.diagnostics.length > 0 ? (
              <div className="wa-vnext__tool-config-diagnostics">
                {discovery.data.diagnostics.map((diagnostic) => (
                  <Typography.Text key={diagnostic.code}>
                    {diagnostic.safeMessage}
                  </Typography.Text>
                ))}
              </div>
            ) : undefined
          }
          title={t(
            'workflowActivityVNext.nodeInspector.tool.empty',
            'No connected actions are available yet.',
          )}
          showIcon
          type="info"
        />
      ) : null}
      {selector && discovery.isSuccess && !selectedDescriptor ? (
        <Alert
          title={t(
            'workflowActivityVNext.nodeInspector.tool.savedUnavailable',
            'This saved action is no longer available.',
          )}
          showIcon
          type="warning"
        />
      ) : null}

      {selectedDescriptor ? (
        <div className="wa-vnext__tool-config-summary">
          <span>{selectedDescriptor.displayName}</span>
          <div className="wa-vnext__tool-config-badges">
            <Tag color={selectedDescriptor.destructive ? 'red' : 'default'}>
              {riskLabel(selectedDescriptor)}
            </Tag>
            {settledReadiness?.selectedOperation?.executionPolicy?.approval ===
            'required' ? (
              <Tag color="gold">
                {t(
                  'workflowActivityVNext.nodeInspector.tool.approvalRequired',
                  'Approval required',
                )}
              </Tag>
            ) : null}
          </div>
        </div>
      ) : null}

      {discovery.isLoading && !selector ? (
        <div className="wa-vnext__tool-config-status is-pending">
          <ReloadOutlined spin />
          <span>
            {t(
              'workflowActivityVNext.nodeInspector.tool.loadingActions',
              'Loading connected actions',
            )}
          </span>
        </div>
      ) : !selector ? (
        <div className="wa-vnext__tool-config-status is-pending">
          <ExclamationCircleOutlined />
          <span>
            {t(
              'workflowActivityVNext.nodeInspector.tool.chooseAction',
              'Choose an action',
            )}
          </span>
        </div>
      ) : readiness.isFetching ? (
        <div className="wa-vnext__tool-config-status is-pending">
          <ReloadOutlined spin />
          <span>
            {t(
              'workflowActivityVNext.nodeInspector.tool.checking',
              'Checking availability',
            )}
          </span>
        </div>
      ) : readiness.isError ? (
        <Alert
          action={
            <Button
              disabled={disabled}
              icon={<ReloadOutlined />}
              onClick={() => void readiness.refetch()}
              size="small"
            >
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
          }
          title={t(
            'workflowActivityVNext.nodeInspector.tool.readinessFailed',
            'This action could not be checked.',
          )}
          showIcon
          type="error"
        />
      ) : settledReadiness?.status === 'ready' ? (
        <div className="wa-vnext__tool-config-status is-ready">
          <CheckCircleOutlined />
          <span>
            {t('workflowActivityVNext.nodeInspector.tool.ready', 'Ready')}
          </span>
        </div>
      ) : settledReadiness ? (
        <div className="wa-vnext__tool-config-readiness">
          <div className="wa-vnext__tool-config-status is-warning">
            <WarningOutlined />
            <span>
              {settledReadiness.status === 'source_stale' ||
              settledReadiness.status === 'contract_drift'
                ? t(
                    'workflowActivityVNext.nodeInspector.tool.unavailable',
                    'Unavailable',
                  )
                : t(
                    'workflowActivityVNext.nodeInspector.tool.needsSetup',
                    'Needs setup',
                  )}
            </span>
          </div>
          {settledReadiness.blockers.map((blocker) => (
            <Typography.Paragraph key={blocker.code}>
              {blocker.safeMessage}
            </Typography.Paragraph>
          ))}
          {settledReadiness.remediations.map((remediation) => {
            const href = remediationHref(remediation.trustedLocator, scopeId);
            return href ? (
              <a href={href} key={remediation.actionKind}>
                {remediation.label}
              </a>
            ) : (
              <Typography.Text key={remediation.actionKind}>
                {remediation.label}
              </Typography.Text>
            );
          })}
        </div>
      ) : null}

      {parsedArguments.error ? (
        <Alert
          title={parsedArguments.error}
          description={t(
            'workflowActivityVNext.nodeInspector.tool.argumentsRecovery',
            'Open Advanced JSON to repair the existing action inputs, or change a guided field to replace them.',
          )}
          showIcon
          type="error"
        />
      ) : null}

      {fields.length > 0 ? (
        <div className="wa-vnext__tool-config-inputs">
          <Typography.Title level={5}>
            {t(
              'workflowActivityVNext.nodeInspector.tool.inputs',
              'Action inputs',
            )}
          </Typography.Title>
          {hasMissingRequiredInputs ? (
            <div
              className="wa-vnext__tool-config-status is-warning"
              role="status"
            >
              <ExclamationCircleOutlined />
              <span>
                {t(
                  'workflowActivityVNext.nodeInspector.tool.incompleteGuidance',
                  'Complete the required inputs before this step can run. You can still apply this draft.',
                )}
              </span>
            </div>
          ) : null}
          {fields.map((field) => {
            const controlId = `workflow-tool-field-${field.key}`;
            const helpId = `workflow-tool-field-help-${field.key}`;
            const errorId = `workflow-tool-field-error-${field.key}`;
            const blockingError =
              fieldErrors[field.key] ??
              fieldValidation.blockingErrors[field.key];
            const missingMessage = blockingError
              ? ''
              : fieldValidation.missingRequired[field.key];
            const fieldMessage = blockingError || missingMessage;
            const status: OperationInputStatus = blockingError
              ? 'error'
              : missingMessage
                ? 'warning'
                : undefined;
            const describedBy = fieldMessage ? `${helpId} ${errorId}` : helpId;
            return (
              <div className="wa-vnext__node-inspector-field" key={field.key}>
                <label htmlFor={controlId}>
                  {field.label}
                  {field.required ? <span aria-hidden="true"> *</span> : null}
                </label>
                <Typography.Text
                  className="wa-vnext__node-inspector-help"
                  id={helpId}
                >
                  {field.group === 'body'
                    ? t(
                        'workflowActivityVNext.nodeInspector.tool.bodyValue',
                        'Request body value',
                      )
                    : field.group === 'response'
                      ? t(
                          'workflowActivityVNext.nodeInspector.tool.responseHelp',
                          'Choose how this action should return its result',
                        )
                      : t(
                          'workflowActivityVNext.nodeInspector.tool.parameterLocation',
                          '{location} parameter',
                          {
                            location: inputLocationLabel(field.group),
                          },
                        )}
                  {field.required
                    ? t(
                        'workflowActivityVNext.nodeInspector.tool.requiredSuffix',
                        ' · Required',
                      )
                    : t(
                        'workflowActivityVNext.nodeInspector.tool.optionalSuffix',
                        ' · Optional',
                      )}
                </Typography.Text>
                <OperationInputControl
                  controlId={controlId}
                  describedBy={describedBy}
                  disabled={disabled}
                  field={field}
                  invalid={Boolean(blockingError)}
                  onCommit={(value) => commitField(field, value)}
                  required={field.required}
                  status={status}
                  value={readOperationInputValue(
                    parsedArguments.arguments,
                    field,
                  )}
                />
                {fieldMessage ? (
                  <Typography.Text
                    id={errorId}
                    role={blockingError ? 'alert' : undefined}
                    type={blockingError ? 'danger' : 'warning'}
                  >
                    {fieldMessage}
                  </Typography.Text>
                ) : null}
              </div>
            );
          })}
        </div>
      ) : null}
    </div>
  );
}
