import type { WorkflowPrimitiveDescriptor } from '@/shared/models/runtime/query';
import {
  normalizeStepParametersForType,
  parseInspectorParameters,
  readStepParameterValue,
  resolveStepParameterName,
} from './document';
import type { StudioConnectorDefinition } from './models';
import { t } from '@/shared/i18n/messages';

export type NodeConfigFieldKind = 'json' | 'select' | 'text';

export type NodeConfigFieldOption = {
  readonly label: string;
  readonly value: string;
};

export type NodeConfigField = {
  readonly name: string;
  readonly label: string;
  readonly description: string;
  readonly kind: NodeConfigFieldKind;
  readonly placeholder: string;
  readonly required: boolean;
  readonly value: string;
  readonly valueType: string;
  readonly options: readonly NodeConfigFieldOption[];
};

export type NodeConfigFieldSet = {
  readonly fields: readonly NodeConfigField[];
  readonly parameters: Record<string, unknown> | null;
  readonly parseError: string;
};

type NodeConfigFieldMessage = {
  readonly defaultMessage: string;
  readonly id: string;
};

type NodeConfigFieldSource = {
  readonly name: string;
  readonly label?: string | NodeConfigFieldMessage;
  readonly description?: string | NodeConfigFieldMessage;
  readonly default?: string;
  readonly enumValues?: readonly string[];
  readonly kind?: NodeConfigFieldKind;
  readonly placeholder?: string | NodeConfigFieldMessage;
  readonly required?: boolean;
  readonly type?: string;
};

const LLM_CALL_STEP_TYPE = 'llm_call';
const PROMPT_PREFIX_PARAMETER = 'prompt_prefix';

const CONNECTOR_CALL_FIELDS: readonly NodeConfigFieldSource[] = [
  {
    name: 'connector',
    label: {
      id: 'shared.studio.nodeConfigFields.connector.label',
      defaultMessage: 'Connector',
    },
    description: {
      id: 'shared.studio.nodeConfigFields.connector.description',
      defaultMessage: 'Connector name passed to the runtime.',
    },
    kind: 'select',
    placeholder: {
      id: 'shared.studio.nodeConfigFields.connector.placeholder',
      defaultMessage: 'Select connector',
    },
    type: 'string',
  },
  {
    name: 'operation',
    label: {
      id: 'shared.studio.nodeConfigFields.operation.label',
      defaultMessage: 'Operation',
    },
    description: {
      id: 'shared.studio.nodeConfigFields.operation.description',
      defaultMessage:
        'Optional operation name for connector implementations that expose multiple operations.',
    },
    type: 'string',
  },
  {
    name: 'path',
    label: {
      id: 'shared.studio.nodeConfigFields.path.label',
      defaultMessage: 'Path',
    },
    description: {
      id: 'shared.studio.nodeConfigFields.path.description',
      defaultMessage: 'Optional request path or connector-specific target.',
    },
    type: 'string',
  },
  {
    name: 'method',
    label: {
      id: 'shared.studio.nodeConfigFields.method.label',
      defaultMessage: 'Method',
    },
    description: {
      id: 'shared.studio.nodeConfigFields.method.description',
      defaultMessage: 'HTTP method or connector-specific verb.',
    },
    enumValues: ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'],
    type: 'string',
    default: 'POST',
  },
  {
    name: 'timeout_ms',
    label: {
      id: 'shared.studio.nodeConfigFields.timeout.label',
      defaultMessage: 'Timeout ms',
    },
    description: {
      id: 'shared.studio.nodeConfigFields.timeout.description',
      defaultMessage: 'Connector timeout in milliseconds.',
    },
    type: 'number',
    default: '10000',
  },
  {
    name: 'retry',
    label: {
      id: 'shared.studio.nodeConfigFields.retry.label',
      defaultMessage: 'Retry',
    },
    description: {
      id: 'shared.studio.nodeConfigFields.retry.description',
      defaultMessage: 'Retry count for transient connector failures.',
    },
    type: 'number',
    default: '0',
  },
  {
    name: 'on_error',
    label: {
      id: 'shared.studio.nodeConfigFields.onError.label',
      defaultMessage: 'On error',
    },
    description: {
      id: 'shared.studio.nodeConfigFields.onError.description',
      defaultMessage: 'Failure behavior when the connector call cannot complete.',
    },
    enumValues: ['fail', 'continue'],
    type: 'string',
    default: 'fail',
  },
];

const LLM_CALL_FIELDS: readonly NodeConfigFieldSource[] = [
  {
    name: PROMPT_PREFIX_PARAMETER,
    label: {
      id: 'shared.studio.nodeConfigFields.promptInstruction.label',
      defaultMessage: 'Prompt instruction',
    },
    description: {
      id: 'shared.studio.nodeConfigFields.promptInstruction.description',
      defaultMessage:
        'Instruction added before each workflow run input reaches the LLM.',
    },
    placeholder: {
      id: 'shared.studio.nodeConfigFields.promptInstruction.placeholder',
      defaultMessage: 'e.g. Translate the user input to Japanese',
    },
    type: 'string',
  },
];

function normalizeString(value: unknown): string {
  return String(value ?? '').trim();
}

function formatFieldMessage(
  value: string | NodeConfigFieldMessage | null | undefined,
): string {
  if (!value) {
    return '';
  }

  if (typeof value === 'string') {
    return normalizeString(value);
  }

  return t(value.id, value.defaultMessage);
}

function normalizeStepType(value: unknown): string {
  return normalizeString(value).toLowerCase();
}

function formatFieldValue(value: unknown): string {
  if (value === null || value === undefined) {
    return '';
  }

  if (typeof value === 'string') {
    return value;
  }

  if (typeof value === 'number' || typeof value === 'boolean') {
    return String(value);
  }

  return JSON.stringify(value, null, 2);
}

function normalizeValueType(value: string | null | undefined): string {
  return normalizeString(value || 'string').toLowerCase();
}

function inferFieldKind(
  value: unknown,
  source: NodeConfigFieldSource,
): NodeConfigFieldKind {
  if (source.kind) {
    return source.kind;
  }

  if ((source.enumValues ?? []).length > 0) {
    return 'select';
  }

  if (Array.isArray(value) || (value !== null && typeof value === 'object')) {
    return 'json';
  }

  const valueType = normalizeValueType(source.type);
  return valueType === 'json' ||
    valueType === 'object' ||
    valueType === 'array' ||
    valueType === 'map'
    ? 'json'
    : 'text';
}

function formatLabel(name: string): string {
  return name
    .split(/[_\s-]+/)
    .filter(Boolean)
    .map((part) => `${part.slice(0, 1).toUpperCase()}${part.slice(1)}`)
    .join(' ');
}

function isLLMPromptInstructionParameter(
  stepType: string,
  parameterName: string,
): boolean {
  return (
    normalizeStepType(stepType) === LLM_CALL_STEP_TYPE &&
    resolveStepParameterName(stepType, parameterName) === PROMPT_PREFIX_PARAMETER
  );
}

function shouldUseParameterDefault(
  stepType: string,
  parameterName: string,
): boolean {
  return !isLLMPromptInstructionParameter(stepType, parameterName);
}

function normalizeFieldSourceName(
  stepType: string,
  source: NodeConfigFieldSource,
): NodeConfigFieldSource {
  const resolvedName = resolveStepParameterName(stepType, source.name);
  if (resolvedName === source.name) {
    return source;
  }

  return {
    ...source,
    name: resolvedName,
  };
}

function createField(
  stepType: string,
  parameters: Record<string, unknown>,
  source: NodeConfigFieldSource,
): NodeConfigField | null {
  const normalizedSource = normalizeFieldSourceName(stepType, source);
  const name = normalizeString(normalizedSource.name);
  if (!name) {
    return null;
  }

  const rawValue = readStepParameterValue(parameters, stepType, name);
  const fallbackDefault = shouldUseParameterDefault(stepType, name)
    ? normalizedSource.default
    : '';
  const value = rawValue ?? fallbackDefault ?? '';
  const options = (normalizedSource.enumValues ?? [])
    .map((entry) => normalizeString(entry))
    .filter(Boolean)
    .map((entry) => ({
      label: entry,
      value: entry,
    }));

  return {
    name,
    label:
      isLLMPromptInstructionParameter(stepType, name)
        ? t(
            'shared.studio.nodeConfigFields.promptInstruction.label',
            'Prompt instruction',
          )
        : formatFieldMessage(normalizedSource.label) || formatLabel(name),
    description:
      isLLMPromptInstructionParameter(stepType, name)
        ? t(
            'shared.studio.nodeConfigFields.promptInstruction.description',
            'Instruction added before each workflow run input reaches the LLM.',
          )
        : formatFieldMessage(normalizedSource.description) ||
          `Type: ${normalizeValueType(normalizedSource.type)}`,
    kind: inferFieldKind(value, {
      ...normalizedSource,
      enumValues: options.map((option) => option.value),
    }),
    placeholder:
      isLLMPromptInstructionParameter(stepType, name)
        ? t(
            'shared.studio.nodeConfigFields.promptInstruction.placeholder',
            'e.g. Translate the user input to Japanese',
          )
        : formatFieldMessage(normalizedSource.placeholder) ||
          normalizeString(normalizedSource.default) ||
          normalizeValueType(normalizedSource.type) ||
          'Value',
    required: Boolean(normalizedSource.required),
    value: formatFieldValue(value),
    valueType: normalizeValueType(normalizedSource.type),
    options,
  };
}

function createInferredFieldSource(
  name: string,
  value: unknown,
): NodeConfigFieldSource {
  if (Array.isArray(value)) {
    return {
      name,
      label: formatLabel(name),
      description: t(
        'shared.studio.nodeConfigFields.inferred.array',
        'Array value edited as JSON.',
      ),
      kind: 'json',
      type: 'array',
    };
  }

  if (value !== null && typeof value === 'object') {
    return {
      name,
      label: formatLabel(name),
      description: t(
        'shared.studio.nodeConfigFields.inferred.object',
        'Object value edited as JSON.',
      ),
      kind: 'json',
      type: 'object',
    };
  }

  if (typeof value === 'boolean') {
    return {
      name,
      label: formatLabel(name),
      description: t(
        'shared.studio.nodeConfigFields.inferred.boolean',
        'Boolean value.',
      ),
      enumValues: ['true', 'false'],
      type: 'boolean',
    };
  }

  if (typeof value === 'number') {
    return {
      name,
      label: formatLabel(name),
      description: t(
        'shared.studio.nodeConfigFields.inferred.number',
        'Numeric value.',
      ),
      type: 'number',
    };
  }

  return {
    name,
    label: formatLabel(name),
    description: t(
      'shared.studio.nodeConfigFields.inferred.string',
      'String value.',
    ),
    type: 'string',
  };
}

function createPrimitiveFieldSources(
  stepType: string,
  primitiveDescriptor: WorkflowPrimitiveDescriptor,
): NodeConfigFieldSource[] {
  return primitiveDescriptor.parameters.map((parameter) =>
    normalizeFieldSourceName(stepType, {
      name: parameter.name,
      label: parameter.name,
      description: parameter.description,
      default: parameter.default,
      enumValues: parameter.enumValues,
      required: parameter.required,
      type: parameter.type,
    }),
  );
}

function createKnownFieldSources(
  stepType: string,
  primitiveDescriptor?: WorkflowPrimitiveDescriptor | null,
): NodeConfigFieldSource[] {
  if (primitiveDescriptor) {
    return createPrimitiveFieldSources(stepType, primitiveDescriptor);
  }

  switch (normalizeStepType(stepType)) {
    case 'connector_call':
      return [...CONNECTOR_CALL_FIELDS];
    case LLM_CALL_STEP_TYPE:
      return [...LLM_CALL_FIELDS];
    default:
      return [];
  }
}

function createConnectorOptions(
  connectors: readonly StudioConnectorDefinition[],
): NodeConfigFieldOption[] {
  return connectors
    .map((connector) => {
      const name = normalizeString(connector.name);
      if (!name) {
        return null;
      }

      const type = normalizeString(connector.type);
      return {
        label: type ? `${name} - ${type}` : name,
        value: name,
      };
    })
    .filter((entry): entry is NodeConfigFieldOption => Boolean(entry));
}

function withConnectorOptions(
  fields: readonly NodeConfigField[],
  connectors: readonly StudioConnectorDefinition[],
): NodeConfigField[] {
  const connectorOptions = createConnectorOptions(connectors);
  if (connectorOptions.length === 0) {
    return [...fields];
  }

  return fields.map((field) =>
    field.name === 'connector'
      ? {
          ...field,
          kind: 'select',
          options: connectorOptions,
          placeholder:
            field.placeholder ||
            t(
              'shared.studio.nodeConfigFields.connector.placeholder',
              'Select connector',
            ),
        }
      : field,
  );
}

export function findNodeConfigPrimitiveDescriptor(
  primitives: readonly WorkflowPrimitiveDescriptor[],
  stepType: string,
): WorkflowPrimitiveDescriptor | null {
  const normalizedStepType = normalizeStepType(stepType);
  return (
    primitives.find((primitive) => {
      if (normalizeStepType(primitive.name) === normalizedStepType) {
        return true;
      }

      return primitive.aliases.some(
        (alias) => normalizeStepType(alias) === normalizedStepType,
      );
    }) ?? null
  );
}

export function validateNodeConfigParametersText(value: string): string {
  try {
    parseInspectorParameters(value);
    return '';
  } catch (error) {
    return error instanceof Error
      ? error.message
      : 'Step parameters must be a JSON object.';
  }
}

export function buildNodeConfigFields(options: {
  readonly connectors?: readonly StudioConnectorDefinition[];
  readonly nodeType: string;
  readonly parametersText: string;
  readonly primitiveDescriptor?: WorkflowPrimitiveDescriptor | null;
}): NodeConfigFieldSet {
  let parameters: Record<string, unknown>;
  try {
    parameters = normalizeStepParametersForType(
      options.nodeType,
      parseInspectorParameters(options.parametersText),
    );
  } catch (error) {
    return {
      fields: [],
      parameters: null,
      parseError:
        error instanceof Error
          ? error.message
          : 'Step parameters must be a JSON object.',
    };
  }

  const fieldSources = createKnownFieldSources(
    options.nodeType,
    options.primitiveDescriptor,
  );
  const seenNames = new Set<string>();
  const fields: NodeConfigField[] = [];

  for (const source of fieldSources) {
    const field = createField(options.nodeType, parameters, source);
    if (!field) {
      continue;
    }

    const normalizedName = normalizeStepType(field.name);
    if (seenNames.has(normalizedName)) {
      continue;
    }

    seenNames.add(normalizedName);
    fields.push(field);
  }

  for (const [name, value] of Object.entries(parameters)) {
    const resolvedName = resolveStepParameterName(options.nodeType, name);
    const normalizedName = normalizeStepType(resolvedName);
    if (!normalizedName || seenNames.has(normalizedName)) {
      continue;
    }

    const field = createField(
      options.nodeType,
      parameters,
      createInferredFieldSource(resolvedName, value),
    );
    if (!field) {
      continue;
    }

    seenNames.add(normalizedName);
    fields.push(field);
  }

  return {
    fields: withConnectorOptions(fields, options.connectors ?? []),
    parameters,
    parseError: '',
  };
}

function coerceFieldValue(field: NodeConfigField, rawValue: string): unknown {
  const trimmed = rawValue.trim();
  const valueType = normalizeValueType(field.valueType);

  if (!trimmed) {
    return '';
  }

  if (field.kind === 'json') {
    return JSON.parse(trimmed) as unknown;
  }

  if (valueType === 'bool' || valueType === 'boolean') {
    return trimmed.toLowerCase() === 'true';
  }

  if (
    valueType === 'number' ||
    valueType === 'int' ||
    valueType === 'int32' ||
    valueType === 'int64' ||
    valueType === 'float' ||
    valueType === 'double'
  ) {
    const parsed = Number(trimmed);
    return Number.isFinite(parsed) ? parsed : trimmed;
  }

  if (
    (valueType === 'json' ||
      valueType === 'object' ||
      valueType === 'array' ||
      valueType === 'map') &&
    ((trimmed.startsWith('{') && trimmed.endsWith('}')) ||
      (trimmed.startsWith('[') && trimmed.endsWith(']')))
  ) {
    try {
      return JSON.parse(trimmed) as unknown;
    } catch {
      return trimmed;
    }
  }

  return trimmed;
}

function indentMultiline(value: string): string {
  return value.replace(/\n/g, '\n  ');
}

function formatParametersWithRawJsonField(
  parameters: Record<string, unknown>,
  fieldName: string,
  rawValue: string,
): string {
  const entries = Object.entries(parameters)
    .filter(([name]) => name !== fieldName)
    .map(([name, value]) =>
      `${JSON.stringify(name)}: ${indentMultiline(JSON.stringify(value, null, 2))}`,
    );
  const normalizedRawValue = rawValue.trim() || 'null';
  entries.push(`${JSON.stringify(fieldName)}: ${indentMultiline(normalizedRawValue)}`);

  if (entries.length === 0) {
    return '{}';
  }

  return `{\n  ${entries.join(',\n  ')}\n}`;
}

export function updateNodeConfigFieldParametersText(options: {
  readonly field: NodeConfigField;
  readonly nodeType: string;
  readonly parametersText: string;
  readonly rawValue: string;
}): string {
  const parameters = normalizeStepParametersForType(
    options.nodeType,
    parseInspectorParameters(options.parametersText),
  );
  const nextParameters = { ...parameters };
  const trimmed = options.rawValue.trim();

  if (!trimmed) {
    delete nextParameters[options.field.name];
    return JSON.stringify(nextParameters, null, 2);
  }

  if (options.field.kind === 'json') {
    try {
      nextParameters[options.field.name] = coerceFieldValue(
        options.field,
        options.rawValue,
      );
    } catch {
      return formatParametersWithRawJsonField(
        nextParameters,
        options.field.name,
        options.rawValue,
      );
    }
  } else {
    nextParameters[options.field.name] = coerceFieldValue(
      options.field,
      options.rawValue,
    );
  }

  return JSON.stringify(nextParameters, null, 2);
}
