import { getLegacyNodeConfigurationSchema } from './nodeConfigFieldSchemas';
import type {
  WorkflowPrimitiveDescriptor,
  WorkflowPrimitiveParameterDescriptor,
} from '@/shared/models/runtime/query';
import {
  formatConsoleMessage,
  t,
  type ConsoleMessageDescriptor,
} from '@/shared/i18n/messages';
import {
  formatInspectorParameters,
  normalizeStepParametersForType,
  parseInspectorParameters,
  readStepParameterValue,
  resolveStepParameterName,
} from './document';

export type StudioNodeConfigurationFieldKind =
  | 'array'
  | 'boolean'
  | 'json'
  | 'number'
  | 'object'
  | 'select'
  | 'single-line'
  | 'multi-line'
  | 'text'
  | 'textarea';

export type StudioNodeConfigurationOption = {
  readonly label: ConsoleMessageDescriptor;
  readonly value: string;
};

export type StudioNodeConfigurationField = {
  readonly control: StudioNodeConfigurationFieldKind;
  readonly defaultValue?: unknown;
  readonly description?: ConsoleMessageDescriptor;
  readonly kind: StudioNodeConfigurationFieldKind;
  readonly label: ConsoleMessageDescriptor;
  readonly name: string;
  readonly options: readonly StudioNodeConfigurationOption[];
  readonly path: string;
  readonly parameterName: string;
  readonly placeholder?: ConsoleMessageDescriptor;
  readonly required: boolean;
  readonly valueType: string;
  readonly validation: {
    readonly integer: boolean;
    readonly max?: number;
    readonly min?: number;
  };
};

export type NodeConfigField = StudioNodeConfigurationField;
export type StudioNodeConfigurationSchema = {
  readonly source: 'descriptor' | 'legacy' | 'unavailable';
  readonly fields: readonly NodeConfigField[];
  readonly stepType: string;
};

function normalizeStepType(value: string): string {
  return value.trim().toLowerCase();
}

function matchesDescriptor(
  stepType: string,
  descriptor?: WorkflowPrimitiveDescriptor | null,
): descriptor is WorkflowPrimitiveDescriptor {
  if (!descriptor) return false;
  const type = normalizeStepType(stepType);
  return [descriptor.name, ...descriptor.aliases].some(
    (name) => normalizeStepType(name) === type,
  );
}

function fieldControl(
  parameter: WorkflowPrimitiveParameterDescriptor,
): StudioNodeConfigurationFieldKind {
  const type = parameter.type.trim().toLowerCase();
  if (type === 'array' || type === 'object' || type === 'map') {
    return type === 'map' ? 'object' : type;
  }
  if (parameter.enumValues.length) return 'select';
  if (parameter.control) return parameter.control;
  if (
    ['int', 'integer', 'int32', 'int64', 'float', 'double', 'number'].includes(
      type,
    )
  )
    return 'number';
  if (type === 'bool' || type === 'boolean') return 'boolean';
  if (type === 'json') return 'json';
  return 'text';
}

export function getStudioNodeConfigurationSchema(
  stepType: string,
  descriptor?: WorkflowPrimitiveDescriptor | null,
): StudioNodeConfigurationSchema {
  if (!matchesDescriptor(stepType, descriptor)) {
    return (
      getLegacyNodeConfigurationSchema(stepType) ?? {
        source: 'unavailable',
        stepType: normalizeStepType(stepType),
        fields: [],
      }
    );
  }
  const fields: NodeConfigField[] = [];
  const seen = new Set<string>();
  for (const parameter of descriptor.parameters) {
    const name = resolveStepParameterName(stepType, parameter.name);
    if (!name || seen.has(name)) continue;
    seen.add(name);
    const key = `shared.studio.nodeConfiguration.${normalizeStepType(descriptor.name)}.${name}`;
    const label =
      parameter.label ||
      name
        .replace(/[_-]+/g, ' ')
        .replace(/\b\w/g, (letter) => letter.toUpperCase());
    const control = fieldControl(parameter);
    fields.push({
      control,
      kind: control,
      name,
      parameterName: name,
      path: name,
      label: { id: `${key}.label`, defaultMessage: label },
      description: parameter.description
        ? { id: `${key}.description`, defaultMessage: parameter.description }
        : undefined,
      placeholder: {
        id: `${key}.placeholder`,
        defaultMessage: parameter.placeholder || parameter.default,
      },
      required: parameter.required,
      defaultValue: parameter.default,
      valueType: parameter.type,
      options: parameter.enumValues.map((value) => ({
        label: { id: `${key}.option.${value}`, defaultMessage: value },
        value,
      })),
      validation: {
        integer:
          parameter.integer ??
          ['int', 'integer', 'int32', 'int64'].includes(
            parameter.type.toLowerCase(),
          ),
        min: parameter.minimum ?? undefined,
        max: parameter.maximum ?? undefined,
      },
    });
  }
  return {
    source: 'descriptor',
    stepType: normalizeStepType(stepType),
    fields,
  };
}

export function hasStudioNodeConfigurationSchema(
  stepType: string,
  descriptor?: WorkflowPrimitiveDescriptor | null,
): boolean {
  return (
    getStudioNodeConfigurationSchema(stepType, descriptor).source !==
    'unavailable'
  );
}

// Runtime templates are values supplied at execution; literal-type validation
// must not coerce an authored reference into a number, boolean, or JSON value.
export function isNodeConfigurationExpression(value: string): boolean {
  return (
    /^\$[a-zA-Z_][\w.[\]-]*$/.test(value.trim()) ||
    /\$\{[^}]+\}|\{\{[\s\S]+?\}\}/.test(value)
  );
}

function stringifyConfigurationValue(value: unknown): string {
  if (value === undefined || value === null) {
    return '';
  }

  if (
    typeof value === 'string' ||
    typeof value === 'number' ||
    typeof value === 'boolean'
  ) {
    return String(value);
  }

  return JSON.stringify(value, null, 2);
}

export function readStudioNodeConfigurationValues(
  stepType: string,
  parameters: Record<string, unknown> | null | undefined,
  descriptor?: WorkflowPrimitiveDescriptor | null,
): Record<string, string> {
  const schema = getStudioNodeConfigurationSchema(stepType, descriptor);
  return Object.fromEntries(
    schema.fields.map((field) => [
      field.name,
      stringifyConfigurationValue(
        readStepParameterValue(parameters, stepType, field.parameterName),
      ),
    ]),
  );
}

export function applyStudioNodeConfigurationValues(
  stepType: string,
  parameters: Record<string, unknown> | null | undefined,
  values: Record<string, string>,
  descriptor?: WorkflowPrimitiveDescriptor | null,
): Record<string, unknown> {
  const result = applyStudioNodeConfigurationValuesWithValidation(
    stepType,
    parameters,
    values,
    descriptor,
  );

  if (!result.valid) {
    throw new Error(
      result.errors[0] ??
        t(
          'shared.studio.nodeConfiguration.validation.invalid',
          'Node configuration is invalid.',
        ),
    );
  }

  return result.parameters;
}

function readFieldValueFromInput(
  field: StudioNodeConfigurationField,
  value: string,
  existingValue: unknown,
): { error?: string; include: boolean; value?: unknown } {
  const trimmed = value.trim();
  const control = field.control ?? field.kind;
  const valueType = field.valueType.trim().toLowerCase();
  const isString = valueType === 'string';
  const parseKind = [
    'int',
    'integer',
    'int32',
    'int64',
    'float',
    'double',
    'number',
  ].includes(valueType)
    ? 'number'
    : ['bool', 'boolean'].includes(valueType)
      ? 'boolean'
      : valueType === 'map'
        ? 'object'
        : ['array', 'object', 'json'].includes(valueType)
          ? valueType
          : ['array', 'object', 'json'].includes(control)
            ? control
            : 'text';
  const empty = isString && parseKind === 'text' ? value === '' : !trimmed;
  const label = formatConsoleMessage(field.label);
  const invalid = (
    key: string,
    message: string,
    values: Record<string, string | number> = {},
  ) => ({
    error: t(`shared.studio.nodeConfiguration.validation.${key}`, message, {
      label,
      ...values,
    }),
    include: false,
  });

  if (empty && !field.required) {
    return { include: false };
  }

  if (!trimmed && field.required) {
    return invalid('required', '{label} is required.');
  }

  if (isNodeConfigurationExpression(value)) {
    return { include: true, value };
  }

  // The catalogue publishes canonical choices. Existing workflows may use
  // runtime-supported aliases or casing; editing a sibling field must not
  // rewrite or reject a value the user has left intact.
  if (
    field.options.length &&
    !field.options.some((option) => option.value === value) &&
    value !== stringifyConfigurationValue(existingValue)
  ) {
    return invalid('options', '{label} must be one of the available options.');
  }

  switch (parseKind) {
    case 'array':
    case 'json':
    case 'object': {
      try {
        const parsed = JSON.parse(value) as unknown;
        if (parseKind === 'array' && !Array.isArray(parsed)) {
          return invalid('array', '{label} must be a JSON array.');
        }
        if (
          parseKind === 'object' &&
          (!parsed || typeof parsed !== 'object' || Array.isArray(parsed))
        ) {
          return invalid('object', '{label} must be a JSON object.');
        }
        return { include: true, value: isString ? value : parsed };
      } catch {
        return invalid('json', '{label} must be valid JSON.');
      }
    }
    case 'boolean':
      if (trimmed !== 'true' && trimmed !== 'false') {
        return invalid('boolean', '{label} must be true or false.');
      }
      return { include: true, value: trimmed === 'true' };
    case 'number': {
      const parsed = Number(trimmed);
      if (!Number.isFinite(parsed)) {
        return invalid('number', '{label} must be a number.');
      }
      if (field.validation?.integer && !Number.isInteger(parsed)) {
        return invalid('integer', '{label} must be a whole number.');
      }
      if (
        field.validation?.min !== undefined &&
        parsed < field.validation.min
      ) {
        return invalid('minimum', '{label} must be at least {minimum}.', {
          minimum: field.validation.min,
        });
      }
      if (
        field.validation?.max !== undefined &&
        parsed > field.validation.max
      ) {
        return invalid('maximum', '{label} must be at most {maximum}.', {
          maximum: field.validation.max,
        });
      }
      // Workflow parameters also accept decimal strings. Preserve large
      // integer literals exactly instead of rounding them through JavaScript.
      if (field.validation.integer && !Number.isSafeInteger(parsed)) {
        if (/^[+-]?\d+$/.test(trimmed)) {
          return { include: true, value: trimmed };
        }
        return invalid(
          'integerLiteral',
          '{label} must use decimal digits for large whole numbers.',
        );
      }
      return { include: true, value: parsed };
    }
    default:
      return { include: true, value };
  }
}

export function applyStudioNodeConfigurationValuesWithValidation(
  stepType: string,
  parameters: Record<string, unknown> | null | undefined,
  values: Record<string, string>,
  descriptor?: WorkflowPrimitiveDescriptor | null,
): {
  readonly errors: readonly string[];
  readonly parameters: Record<string, unknown>;
  readonly valid: boolean;
} {
  const schema = getStudioNodeConfigurationSchema(stepType, descriptor);
  const nextParameters = {
    ...(parameters && typeof parameters === 'object' ? parameters : {}),
  };
  const errors: string[] = [];

  for (const field of schema.fields) {
    const value = values[field.name] ?? '';
    const fieldValue = readFieldValueFromInput(
      field,
      value,
      readStepParameterValue(parameters, stepType, field.parameterName),
    );
    if (fieldValue.error) {
      errors.push(fieldValue.error);
      continue;
    }

    const resolvedParameterName = resolveStepParameterName(
      stepType,
      field.parameterName,
    );
    if (fieldValue.include) {
      nextParameters[resolvedParameterName] = fieldValue.value;
    } else {
      delete nextParameters[resolvedParameterName];
    }
  }

  return {
    errors,
    parameters: normalizeStepParametersForType(stepType, nextParameters),
    valid: errors.length === 0,
  };
}

export function applyRawStudioNodeConfiguration(
  stepType: string,
  rawConfigurationText: string,
): Record<string, unknown> {
  return normalizeStepParametersForType(
    stepType,
    parseInspectorParameters(rawConfigurationText),
  );
}

export function formatRawStudioNodeConfiguration(
  parameters: Record<string, unknown> | null | undefined,
): string {
  return formatInspectorParameters(parameters);
}
