import {
  CheckCircleOutlined,
  ExclamationCircleOutlined,
  ReloadOutlined,
  WarningOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Input, Select, Switch, Tag, Typography } from 'antd';
import React from 'react';
import { studioApi } from '@/shared/studio/api';
import type {
  StudioWorkflowCapability,
  StudioWorkflowCapabilityDescriptor,
} from '@/shared/studio/models';
import {
  capabilitySelectorKey,
  formatToolArguments,
  listOperationInputFields,
  parseToolArguments,
  readOperationInputValue,
  toDocumentCapability,
  writeOperationInputValue,
  type NyxIdOperationSelector,
  type OperationInputField,
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
  if (descriptor.destructive) return 'Destructive';
  return descriptor.readOnly ? 'Read only' : 'Writes data';
}

function currentInputText(value: unknown): string {
  if (value == null) return '';
  if (typeof value === 'string') return value;
  if (typeof value === 'object') return JSON.stringify(value, null, 2);
  return String(value);
}

function StructuredOperationInput({
  disabled,
  field,
  value,
  onCommit,
}: {
  readonly disabled: boolean;
  readonly field: OperationInputField;
  readonly value: unknown;
  readonly onCommit: (value: string) => void;
}) {
  const [draft, setDraft] = React.useState(() => currentInputText(value));
  React.useEffect(() => setDraft(currentInputText(value)), [value]);

  return (
    <Input.TextArea
      aria-label={field.label}
      autoSize={{ minRows: 3, maxRows: 8 }}
      disabled={disabled}
      onBlur={() => onCommit(draft)}
      onChange={(event) => setDraft(event.target.value)}
      placeholder={field.schema.valueKind === 'array' ? '[ ]' : '{ }'}
      spellCheck={false}
      value={draft}
    />
  );
}

function OperationInputControl({
  disabled,
  field,
  value,
  onCommit,
}: {
  readonly disabled: boolean;
  readonly field: OperationInputField;
  readonly value: unknown;
  readonly onCommit: (value: unknown) => void;
}) {
  if (field.schema.allowedValues.length > 0) {
    return (
      <Select
        aria-label={field.label}
        disabled={disabled}
        onChange={onCommit}
        options={field.schema.allowedValues.map((entry) => ({
          label: entry,
          value: entry,
        }))}
        placeholder="Choose a value"
        value={value == null ? undefined : String(value)}
      />
    );
  }
  if (field.schema.valueKind === 'boolean') {
    return (
      <Switch
        aria-label={field.label}
        checked={value === true}
        disabled={disabled}
        onChange={onCommit}
      />
    );
  }
  if (
    field.schema.valueKind === 'object' ||
    field.schema.valueKind === 'array'
  ) {
    return (
      <StructuredOperationInput
        disabled={disabled}
        field={field}
        onCommit={onCommit}
        value={value}
      />
    );
  }
  return (
    <Input
      aria-label={field.label}
      disabled={disabled}
      onChange={(event) => onCommit(event.target.value)}
      placeholder={
        field.schema.valueKind === 'integer' ||
        field.schema.valueKind === 'number'
          ? 'Enter a number or workflow expression'
          : 'Enter a value or workflow expression'
      }
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
  const [fieldErrors, setFieldErrors] = React.useState<Record<string, string>>(
    {},
  );
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
    queryFn: () =>
      studioApi.inspectWorkflowCapabilityReadiness({
        scopeId,
        selector: selector!,
        executionMode: 'interactive',
      }),
    enabled: Boolean(scopeId.trim() && selector),
    retry: false,
  });
  const parsedArguments = React.useMemo(
    () => parseToolArguments(parameters.arguments),
    [parameters.arguments],
  );
  const operation = readiness.data?.selectedOperation ?? null;
  const fields = React.useMemo(
    () => (operation ? listOperationInputFields(operation) : []),
    [operation],
  );
  const configurationError =
    parsedArguments.error ??
    Object.values(fieldErrors).find(Boolean) ??
    '';

  React.useEffect(() => onErrorChange(configurationError), [
    configurationError,
    onErrorChange,
  ]);
  React.useEffect(() => {
    onActionNameChange?.(selectedDescriptor?.displayName ?? '');
  }, [onActionNameChange, selectedDescriptor?.displayName]);
  React.useEffect(() => setFieldErrors({}), [selectorKey]);

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
        arguments:
          parameters.arguments == null ? '{}' : parameters.arguments,
      },
    });
  };

  const commitField = (field: OperationInputField, rawValue: unknown) => {
    const result = writeOperationInputValue(
      parsedArguments.arguments,
      field,
      rawValue,
    );
    setFieldErrors((current) => ({
      ...current,
      [field.key]: result.error ?? '',
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

  const actionOptions = descriptors.map((descriptor) => ({
    label: descriptor.displayName,
    value: capabilitySelectorKey(descriptor.selector),
  }));
  if (selector && !selectedDescriptor) {
    actionOptions.unshift({
      label: 'Saved action',
      value: selectorKey,
    });
  }

  return (
    <div className="wa-vnext__tool-config">
      <div className="wa-vnext__node-inspector-field">
        <label htmlFor="workflow-tool-action">Action</label>
        <Typography.Text className="wa-vnext__node-inspector-help">
          Choose what external service action this step should run.
        </Typography.Text>
        <Select
          allowClear
          disabled={disabled || discovery.isLoading || discovery.isError}
          id="workflow-tool-action"
          loading={discovery.isLoading}
          onChange={selectAction}
          options={actionOptions}
          placeholder="Choose what this step should do"
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
              Retry
            </Button>
          }
          message="Connected actions could not be loaded."
          showIcon
          type="error"
        />
      ) : null}
      {discovery.isSuccess && descriptors.length === 0 ? (
        <Alert
          message="No connected actions are available yet."
          showIcon
          type="info"
        />
      ) : null}
      {selector && discovery.isSuccess && !selectedDescriptor ? (
        <Alert
          message="This saved action is no longer available."
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
            {readiness.data?.selectedOperation?.executionPolicy?.approval ===
            'required' ? (
              <Tag color="gold">Approval required</Tag>
            ) : null}
          </div>
        </div>
      ) : null}

      {!selector ? (
        <div className="wa-vnext__tool-config-status is-pending">
          <ExclamationCircleOutlined />
          <span>Choose an action</span>
        </div>
      ) : readiness.isLoading ? (
        <div className="wa-vnext__tool-config-status is-pending">
          <ReloadOutlined spin />
          <span>Checking availability</span>
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
              Retry
            </Button>
          }
          message="This action could not be checked."
          showIcon
          type="error"
        />
      ) : readiness.data?.status === 'ready' ? (
        <div className="wa-vnext__tool-config-status is-ready">
          <CheckCircleOutlined />
          <span>Ready</span>
        </div>
      ) : readiness.data ? (
        <div className="wa-vnext__tool-config-readiness">
          <div className="wa-vnext__tool-config-status is-warning">
            <WarningOutlined />
            <span>
              {readiness.data.status === 'source_stale' ||
              readiness.data.status === 'contract_drift'
                ? 'Unavailable'
                : 'Needs setup'}
            </span>
          </div>
          {readiness.data.blockers.map((blocker) => (
            <Typography.Paragraph key={blocker.code}>
              {blocker.safeMessage}
            </Typography.Paragraph>
          ))}
          {readiness.data.remediations.map((remediation) =>
            remediation.trustedLocator.startsWith('/') ? (
              <a
                href={remediation.trustedLocator}
                key={remediation.actionKind}
              >
                {remediation.label}
              </a>
            ) : (
              <Typography.Text key={remediation.actionKind}>
                {remediation.label}
              </Typography.Text>
            ),
          )}
        </div>
      ) : null}

      {parsedArguments.error ? (
        <Alert
          message={parsedArguments.error}
          description="Open Advanced JSON to repair the existing action inputs, or change a guided field to replace them."
          showIcon
          type="error"
        />
      ) : null}

      {fields.length > 0 ? (
        <div className="wa-vnext__tool-config-inputs">
          <Typography.Title level={5}>Action inputs</Typography.Title>
          {fields.map((field) => {
            const helpId = 'workflow-tool-field-help-' + field.key;
            const error = fieldErrors[field.key];
            return (
              <div
                className="wa-vnext__node-inspector-field"
                key={field.key}
              >
                <label>
                  {field.label}
                  {field.required ? <span aria-hidden="true"> *</span> : null}
                </label>
                <Typography.Text
                  className="wa-vnext__node-inspector-help"
                  id={helpId}
                >
                  {field.group === 'body'
                    ? 'Request body value'
                    : field.group.charAt(0).toUpperCase() +
                      field.group.slice(1) +
                      ' parameter'}
                  {field.required ? ' · Required' : ' · Optional'}
                </Typography.Text>
                <OperationInputControl
                  disabled={disabled}
                  field={field}
                  onCommit={(value) => commitField(field, value)}
                  value={readOperationInputValue(
                    parsedArguments.arguments,
                    field,
                  )}
                />
                {error ? (
                  <Typography.Text role="alert" type="danger">
                    {error}
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
