import type { WorkflowPrimitiveDescriptor } from '@/shared/models/runtime/query';
import {
  formatConsoleMessage,
  type ConsoleMessageDescriptor,
} from '@/shared/i18n/messages';
import {
  normalizeStepParametersForType,
  parseInspectorParameters,
} from './document';
import type { StudioConnectorDefinition } from './models';
import {
  getStudioNodeConfigurationSchema,
  isNodeConfigurationExpression,
  readStudioNodeConfigurationValues,
} from './nodeConfiguration';

export {
  applyRawStudioNodeConfiguration,
  applyStudioNodeConfigurationValues,
  applyStudioNodeConfigurationValuesWithValidation,
  formatRawStudioNodeConfiguration,
  getStudioNodeConfigurationSchema,
  hasStudioNodeConfigurationSchema,
  readStudioNodeConfigurationValues,
} from './nodeConfiguration';
export type {
  NodeConfigField as StudioStructuredNodeConfigField,
  StudioNodeConfigurationField,
  StudioNodeConfigurationFieldKind,
  StudioNodeConfigurationOption,
  StudioNodeConfigurationSchema,
} from './nodeConfiguration';

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

export function formatNodeConfigFieldCopy(copy: NodeConfigFieldCopy): string {
  return typeof copy === 'string' ? copy : formatConsoleMessage(copy);
}

export function findNodeConfigPrimitiveDescriptor(
  primitives: readonly WorkflowPrimitiveDescriptor[],
  stepType: string,
): WorkflowPrimitiveDescriptor | null {
  const normalizedType = stepType.trim().toLowerCase();
  return (
    primitives.find((primitive) =>
      [primitive.name, ...primitive.aliases].some(
        (name) => name.trim().toLowerCase() === normalizedType,
      ),
    ) ?? null
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

// The legacy Studio renderer consumes the same server-derived fields as the
// current inspector. Only its presentation model differs.
export function buildNodeConfigFields(options: {
  readonly connectors?: readonly StudioConnectorDefinition[];
  readonly nodeType: string;
  readonly parametersText: string;
  readonly primitiveDescriptor?: WorkflowPrimitiveDescriptor | null;
}): NodeConfigFieldSet {
  try {
    const parameters = normalizeStepParametersForType(
      options.nodeType,
      parseInspectorParameters(options.parametersText),
    );
    const schema = getStudioNodeConfigurationSchema(
      options.nodeType,
      options.primitiveDescriptor,
    );
    const values = readStudioNodeConfigurationValues(
      options.nodeType,
      parameters,
      options.primitiveDescriptor,
    );
    const fields = schema.fields.map((field): NodeConfigField => {
      const connectorOptions =
        field.name === 'connector'
          ? (options.connectors ?? [])
              .filter((connector) => connector.enabled)
              .map((connector) => ({
                label: `${connector.name} - ${connector.type}`,
                value: connector.name,
              }))
          : [];
      return {
        name: field.parameterName,
        label: field.label,
        description: field.description ?? '',
        placeholder: field.placeholder ?? '',
        required: field.required,
        value: values[field.name] ?? '',
        valueType: field.valueType,
        kind:
          connectorOptions.length || field.kind === 'select'
            ? 'select'
            : ['json', 'object', 'array'].includes(field.kind)
              ? 'json'
              : 'text',
        options: connectorOptions.length ? connectorOptions : field.options,
      };
    });
    return { fields, parameters, parseError: '' };
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
}

function coerceFieldValue(field: NodeConfigField, rawValue: string): unknown {
  const trimmed = rawValue.trim();
  const valueType = field.valueType.trim().toLowerCase();

  if (isNodeConfigurationExpression(rawValue)) return rawValue;

  if (field.kind === 'json') {
    const parsed: unknown = JSON.parse(rawValue);
    return valueType === 'string' ? rawValue : parsed;
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
    if (Number.isInteger(parsed) && !Number.isSafeInteger(parsed))
      return rawValue;
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

  return rawValue;
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
    .map(
      ([name, value]) =>
        `${JSON.stringify(name)}: ${indentMultiline(JSON.stringify(value, null, 2))}`,
    );
  const normalizedRawValue = rawValue.trim() || 'null';
  entries.push(
    `${JSON.stringify(fieldName)}: ${indentMultiline(normalizedRawValue)}`,
  );

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
  const empty =
    options.field.valueType === 'string' && options.field.kind !== 'json'
      ? options.rawValue === ''
      : !options.rawValue.trim();

  if (empty) {
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
