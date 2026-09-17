import type { WorkflowPrimitiveDescriptor } from '@/shared/models/runtime/query';
import {
  formatConsoleMessage,
  type ConsoleMessageDescriptor,
} from '@/shared/i18n/messages';
import {
  normalizeStepParametersForType,
  parseInspectorParameters,
  readStepParameterValue,
  resolveStepParameterName,
} from './document';
import type { StudioConnectorDefinition } from './models';

export {
  applyRawStudioNodeConfiguration,
  applyStudioNodeConfigurationValues,
  applyStudioNodeConfigurationValuesWithValidation,
  formatRawStudioNodeConfiguration,
  getStudioNodeConfigurationSchema,
  hasStudioNodeConfigurationSchema,
  readStudioNodeConfigurationValues,
  shouldShowRawStudioNodeConfiguration,
} from './nodeConfigFieldSchemas';
export type {
  NodeConfigField as StudioStructuredNodeConfigField,
  StudioNodeConfigurationField,
  StudioNodeConfigurationFieldKind,
  StudioNodeConfigurationOption,
  StudioNodeConfigurationSchema,
} from './nodeConfigFieldSchemas';

export type NodeConfigFieldKind = 'json' | 'select' | 'text';

export type NodeConfigFieldCopy = ConsoleMessageDescriptor | string;

export type NodeConfigFieldOption = {
  readonly label: NodeConfigFieldCopy;
  readonly value: string;
};

export type NodeConfigField = {
  readonly name: string;
  readonly label: NodeConfigFieldCopy;
  readonly description: NodeConfigFieldCopy;
  readonly kind: NodeConfigFieldKind;
  readonly placeholder: NodeConfigFieldCopy;
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

type NodeConfigFieldSource = {
  readonly name: string;
  readonly label?: NodeConfigFieldCopy;
  readonly description?: NodeConfigFieldCopy;
  readonly default?: string;
  readonly enumValues?: readonly string[];
  readonly kind?: NodeConfigFieldKind;
  readonly placeholder?: NodeConfigFieldCopy;
  readonly required?: boolean;
  readonly type?: string;
};

const LLM_CALL_STEP_TYPE = 'llm_call';
const PROMPT_PREFIX_PARAMETER = 'prompt_prefix';

function message(
  id: string,
  defaultMessage: string,
): ConsoleMessageDescriptor {
  return { defaultMessage, id };
}

const PROMPT_INSTRUCTION_LABEL = message(
  'shared.studio.nodeConfigFields.promptInstruction.label',
  'Prompt instruction',
);

const PROMPT_INSTRUCTION_DESCRIPTION = message(
  'shared.studio.nodeConfigFields.promptInstruction.description',
  'Instruction added before each workflow run input reaches the LLM.',
);

const PROMPT_INSTRUCTION_PLACEHOLDER = message(
  'shared.studio.nodeConfigFields.promptInstruction.placeholder',
  'e.g. Translate the user input to Japanese',
);

const CONNECTOR_CALL_FIELDS: readonly NodeConfigFieldSource[] = [
  {
    name: 'connector',
    label: message(
      'shared.studio.nodeConfigFields.connector.label',
      'Connector',
    ),
    description: message(
      'shared.studio.nodeConfigFields.connector.description',
      'Connector name passed to the runtime.',
    ),
    kind: 'select',
    placeholder: message(
      'shared.studio.nodeConfigFields.connector.placeholder',
      'Select connector',
    ),
    type: 'string',
  },
  {
    name: 'operation',
    label: message(
      'shared.studio.nodeConfigFields.operation.label',
      'Operation',
    ),
    description: message(
      'shared.studio.nodeConfigFields.operation.description',
      'Optional operation name for connector implementations that expose multiple operations.',
    ),
    type: 'string',
  },
  {
    name: 'path',
    label: message('shared.studio.nodeConfigFields.path.label', 'Path'),
    description: message(
      'shared.studio.nodeConfigFields.path.description',
      'Optional request path or connector-specific target.',
    ),
    type: 'string',
  },
  {
    name: 'method',
    label: message('shared.studio.nodeConfigFields.method.label', 'Method'),
    description: message(
      'shared.studio.nodeConfigFields.method.description',
      'HTTP method or connector-specific verb.',
    ),
    enumValues: ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'],
    type: 'string',
    default: 'POST',
  },
  {
    name: 'timeout_ms',
    label: message(
      'shared.studio.nodeConfigFields.timeoutMs.label',
      'Timeout ms',
    ),
    description: message(
      'shared.studio.nodeConfigFields.timeoutMs.description',
      'Connector timeout in milliseconds.',
    ),
    type: 'number',
    default: '10000',
  },
  {
    name: 'retry',
    label: message('shared.studio.nodeConfigFields.retry.label', 'Retry'),
    description: message(
      'shared.studio.nodeConfigFields.retry.description',
      'Retry count for transient connector failures.',
    ),
    type: 'number',
    default: '0',
  },
  {
    name: 'on_error',
    label: message(
      'shared.studio.nodeConfigFields.onError.label',
      'On error',
    ),
    description: message(
      'shared.studio.nodeConfigFields.onError.description',
      'Failure behavior when the connector call cannot complete.',
    ),
    enumValues: ['fail', 'continue'],
    type: 'string',
    default: 'fail',
  },
];

const LLM_CALL_FIELDS: readonly NodeConfigFieldSource[] = [
  {
    name: PROMPT_PREFIX_PARAMETER,
    label: PROMPT_INSTRUCTION_LABEL,
    description: PROMPT_INSTRUCTION_DESCRIPTION,
    placeholder: PROMPT_INSTRUCTION_PLACEHOLDER,
    type: 'string',
  },
];

const FALLBACK_VALUE_PLACEHOLDER = message(
  'shared.studio.nodeConfigFields.value.placeholder',
  'Value',
);

const INFERRED_ARRAY_DESCRIPTION = message(
  'shared.studio.nodeConfigFields.inferred.array.description',
  'Array value edited as JSON.',
);

const INFERRED_OBJECT_DESCRIPTION = message(
  'shared.studio.nodeConfigFields.inferred.object.description',
  'Object value edited as JSON.',
);

const INFERRED_BOOLEAN_DESCRIPTION = message(
  'shared.studio.nodeConfigFields.inferred.boolean.description',
  'Boolean value.',
);

const INFERRED_NUMBER_DESCRIPTION = message(
  'shared.studio.nodeConfigFields.inferred.number.description',
  'Numeric value.',
);

const INFERRED_STRING_DESCRIPTION = message(
  'shared.studio.nodeConfigFields.inferred.string.description',
  'String value.',
);

function normalizeString(value: unknown): string {
  return String(value ?? '').trim();
}

function normalizeCopyText(value: NodeConfigFieldCopy | null | undefined): string {
  if (!value) {
    return '';
  }

  return typeof value === 'string'
    ? normalizeString(value)
    : normalizeString(value.defaultMessage);
}

function resolveFieldCopy(
  value: NodeConfigFieldCopy | null | undefined,
  fallback: NodeConfigFieldCopy,
): NodeConfigFieldCopy {
  return normalizeCopyText(value) ? value! : fallback;
}

export function formatNodeConfigFieldCopy(copy: NodeConfigFieldCopy): string {
  return typeof copy === 'string' ? copy : formatConsoleMessage(copy);
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

  const isPromptInstruction = isLLMPromptInstructionParameter(stepType, name);
  const fieldSource = isPromptInstruction
    ? {
        ...normalizedSource,
        kind: 'text' as const,
        type: 'string',
      }
    : normalizedSource;
  const rawValue = readStepParameterValue(parameters, stepType, name);
  const fallbackDefault = shouldUseParameterDefault(stepType, name)
    ? fieldSource.default
    : '';
  const value = rawValue ?? fallbackDefault ?? '';
  const options = (fieldSource.enumValues ?? [])
    .map((entry) => normalizeString(entry))
    .filter(Boolean)
    .map((entry) => ({
      label: entry,
      value: entry,
    }));

  return {
    name,
    label:
      isPromptInstruction
        ? PROMPT_INSTRUCTION_LABEL
        : resolveFieldCopy(fieldSource.label, formatLabel(name)),
    description:
      isPromptInstruction
        ? PROMPT_INSTRUCTION_DESCRIPTION
        : resolveFieldCopy(
            fieldSource.description,
            `Type: ${normalizeValueType(fieldSource.type)}`,
          ),
    kind: inferFieldKind(value, {
      ...fieldSource,
      enumValues: options.map((option) => option.value),
    }),
    placeholder:
      isPromptInstruction
        ? PROMPT_INSTRUCTION_PLACEHOLDER
        : resolveFieldCopy(
            fieldSource.placeholder,
            normalizeString(fieldSource.default) ||
              normalizeValueType(fieldSource.type) ||
              FALLBACK_VALUE_PLACEHOLDER,
          ),
    required: Boolean(fieldSource.required),
    value: formatFieldValue(value),
    valueType: normalizeValueType(fieldSource.type),
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
      description: INFERRED_ARRAY_DESCRIPTION,
      kind: 'json',
      type: 'array',
    };
  }

  if (value !== null && typeof value === 'object') {
    return {
      name,
      label: formatLabel(name),
      description: INFERRED_OBJECT_DESCRIPTION,
      kind: 'json',
      type: 'object',
    };
  }

  if (typeof value === 'boolean') {
    return {
      name,
      label: formatLabel(name),
      description: INFERRED_BOOLEAN_DESCRIPTION,
      enumValues: ['true', 'false'],
      type: 'boolean',
    };
  }

  if (typeof value === 'number') {
    return {
      name,
      label: formatLabel(name),
      description: INFERRED_NUMBER_DESCRIPTION,
      type: 'number',
    };
  }

  return {
    name,
    label: formatLabel(name),
    description: INFERRED_STRING_DESCRIPTION,
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
    .map((connector): NodeConfigFieldOption | null => {
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
          placeholder: normalizeCopyText(field.placeholder)
            ? field.placeholder
            : CONNECTOR_CALL_FIELDS[0].placeholder!,
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
