import type {
  StudioWorkflowCapability,
  StudioWorkflowCapabilityOperation,
  StudioWorkflowCapabilitySchema,
  StudioWorkflowCapabilitySelector,
} from '@/shared/studio/models';

export type NyxIdOperationSelector = Extract<
  StudioWorkflowCapabilitySelector,
  { readonly kind: 'nyxid_operation' }
>;

export type OperationInputField = {
  readonly key: string;
  readonly name: string;
  readonly label: string;
  readonly group: 'path' | 'query' | 'header' | 'body';
  readonly path: readonly string[];
  readonly required: boolean;
  readonly schema: StudioWorkflowCapabilitySchema;
};

export type ToolArgumentParseResult = {
  readonly arguments: Record<string, unknown>;
  readonly error: string | null;
  readonly originalText: string;
};

export type ToolArgumentWriteResult = {
  readonly arguments: Record<string, unknown>;
  readonly error: string | null;
};

export function capabilitySelectorKey(selector: NyxIdOperationSelector): string {
  return [
    selector.kind,
    selector.userServiceId,
    selector.endpointId,
  ].join('\u0000');
}

export function toDocumentCapability(
  selector: NyxIdOperationSelector,
): StudioWorkflowCapability {
  return {
    nyxid_operation: {
      user_service_id: selector.userServiceId,
      endpoint_id: selector.endpointId,
    },
  };
}

function humanizeInputName(name: string): string {
  const words = name.trim().replace(/[_-]+/g, ' ');
  return words ? words.charAt(0).toUpperCase() + words.slice(1) : 'Value';
}

function parameterPath(
  location: 'path' | 'query' | 'header',
  name: string,
): readonly string[] {
  if (location === 'path') return ['path_params', name];
  if (location === 'header') return ['headers', name];
  return ['query', name];
}

export function listOperationInputFields(
  operation: StudioWorkflowCapabilityOperation,
): readonly OperationInputField[] {
  const parameterFields = operation.parameters.map((parameter) => ({
    key: parameter.location + ':' + parameter.name,
    name: parameter.name,
    label: humanizeInputName(parameter.name),
    group: parameter.location,
    path: parameterPath(parameter.location, parameter.name),
    required: parameter.required,
    schema: parameter.schema,
  }));
  const body = operation.requestBody;
  const bodyFields: OperationInputField[] = [];
  if (body) {
    if (body.schema.valueKind === 'object' && body.schema.properties.length > 0) {
      const requiredProperties = new Set(body.schema.requiredProperties);
      for (const property of body.schema.properties) {
        bodyFields.push({
          key: 'body:' + property.name,
          name: property.name,
          label: humanizeInputName(property.name),
          group: 'body',
          path: ['body', property.name],
          required: requiredProperties.has(property.name),
          schema: property.schema,
        });
      }
    } else {
      bodyFields.push({
        key: 'body:body',
        name: 'body',
        label: 'Request body',
        group: 'body',
        path: ['body'],
        required: body.required,
        schema: body.schema,
      });
    }
  }

  return [...parameterFields, ...bodyFields].sort(
    (left, right) => Number(right.required) - Number(left.required),
  );
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

export function parseToolArguments(value: unknown): ToolArgumentParseResult {
  const originalText =
    typeof value === 'string'
      ? value
      : value == null
        ? ''
        : JSON.stringify(value);
  if (value == null || (typeof value === 'string' && !value.trim())) {
    return { arguments: {}, error: null, originalText };
  }
  if (isRecord(value)) {
    return { arguments: { ...value }, error: null, originalText };
  }

  try {
    const parsed = JSON.parse(originalText) as unknown;
    if (!isRecord(parsed)) {
      throw new Error('not an object');
    }
    return { arguments: parsed, error: null, originalText };
  } catch {
    return {
      arguments: {},
      error: 'Action inputs must be a JSON object.',
      originalText,
    };
  }
}

export function formatToolArguments(
  argumentsValue: Record<string, unknown>,
): string {
  return JSON.stringify(argumentsValue);
}

export function readOperationInputValue(
  argumentsValue: Record<string, unknown>,
  field: OperationInputField,
): unknown {
  let current: unknown = argumentsValue;
  for (const segment of field.path) {
    if (!isRecord(current)) return undefined;
    current = current[segment];
  }
  return current;
}

function isWorkflowExpression(value: string): boolean {
  const normalized = value.trim();
  return normalized.startsWith('$' + '{') && normalized.endsWith('}');
}

function coerceInputValue(
  value: unknown,
  field: OperationInputField,
): { value?: unknown; error: string | null } {
  if (typeof value === 'string' && isWorkflowExpression(value)) {
    return { value, error: null };
  }
  if (field.schema.allowedValues.length > 0) {
    const normalized = String(value);
    if (!field.schema.allowedValues.includes(normalized)) {
      return {
        error: field.label + ' must be one of the available values.',
      };
    }
    return { value: normalized, error: null };
  }

  if (field.schema.valueKind === 'string') {
    return { value: String(value), error: null };
  }
  if (field.schema.valueKind === 'boolean') {
    if (typeof value === 'boolean') return { value, error: null };
    if (value === 'true') return { value: true, error: null };
    if (value === 'false') return { value: false, error: null };
    return { error: field.label + ' must be true or false.' };
  }
  if (
    field.schema.valueKind === 'integer' ||
    field.schema.valueKind === 'number'
  ) {
    const numberValue =
      typeof value === 'number' ? value : Number(String(value).trim());
    const valid =
      Number.isFinite(numberValue) &&
      (field.schema.valueKind !== 'integer' || Number.isInteger(numberValue));
    return valid
      ? { value: numberValue, error: null }
      : {
          error:
            field.label +
            (field.schema.valueKind === 'integer'
              ? ' must be a whole number.'
              : ' must be a number.'),
        };
  }

  let structuredValue = value;
  if (typeof value === 'string') {
    try {
      structuredValue = JSON.parse(value);
    } catch {
      return { error: field.label + ' must be valid JSON.' };
    }
  }
  const valid =
    field.schema.valueKind === 'array'
      ? Array.isArray(structuredValue)
      : isRecord(structuredValue);
  return valid
    ? { value: structuredValue, error: null }
    : {
        error:
          field.label +
          (field.schema.valueKind === 'array'
            ? ' must be a JSON array.'
            : ' must be a JSON object.'),
      };
}

function deletePath(
  argumentsValue: Record<string, unknown>,
  path: readonly string[],
): Record<string, unknown> {
  const next = { ...argumentsValue };
  if (path.length === 1) {
    delete next[path[0]];
    return next;
  }

  const [parentKey, childKey] = path;
  const parent = isRecord(next[parentKey]) ? { ...next[parentKey] } : {};
  delete parent[childKey];
  if (Object.keys(parent).length > 0) {
    next[parentKey] = parent;
  } else {
    delete next[parentKey];
  }
  return next;
}

function writePath(
  argumentsValue: Record<string, unknown>,
  path: readonly string[],
  value: unknown,
): Record<string, unknown> {
  const next = { ...argumentsValue };
  if (path.length === 1) {
    next[path[0]] = value;
    return next;
  }

  const [parentKey, childKey] = path;
  const parent = isRecord(next[parentKey]) ? { ...next[parentKey] } : {};
  parent[childKey] = value;
  next[parentKey] = parent;
  return next;
}

export function writeOperationInputValue(
  argumentsValue: Record<string, unknown>,
  field: OperationInputField,
  rawValue: unknown,
): ToolArgumentWriteResult {
  const empty =
    rawValue == null ||
    (typeof rawValue === 'string' && rawValue.trim().length === 0);
  if (empty) {
    return {
      arguments: deletePath(argumentsValue, field.path),
      error: field.required ? field.label + ' is required.' : null,
    };
  }

  const coerced = coerceInputValue(rawValue, field);
  if (coerced.error) {
    return { arguments: argumentsValue, error: coerced.error };
  }
  return {
    arguments: writePath(argumentsValue, field.path, coerced.value),
    error: null,
  };
}
