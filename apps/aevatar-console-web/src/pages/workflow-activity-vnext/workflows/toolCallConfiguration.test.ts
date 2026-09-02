import { setLocale } from '@umijs/max';
import type {
  StudioWorkflowCapabilityOperation,
  StudioWorkflowCapabilitySelector,
} from '@/shared/studio/models';
import {
  capabilitySelectorKey,
  formatToolArguments,
  listOperationInputFields,
  type OperationInputField,
  parseToolArguments,
  readOperationInputValue,
  reconcileOperationResponseMode,
  toDocumentCapability,
  writeOperationInputValue,
} from './toolCallConfiguration';

const selector: Extract<
  StudioWorkflowCapabilitySelector,
  { readonly kind: 'nyxid_operation' }
> = {
  kind: 'nyxid_operation',
  userServiceId: 'us-posthog-alpha',
  endpointId: 'update-dashboard',
};

const operation: StudioWorkflowCapabilityOperation = {
  userServiceId: 'us-posthog-alpha',
  endpointId: 'update-dashboard',
  serviceSlug: 'posthog',
  httpMethod: 'PATCH',
  pathTemplate: '/api/dashboards/{dashboard_id}',
  parameters: [
    {
      name: 'dashboard_id',
      location: 'path',
      required: true,
      schema: {
        valueKind: 'integer',
        properties: [],
        requiredProperties: [],
        items: null,
        allowedValues: [],
        additionalPropertiesAllowed: false,
      },
    },
    {
      name: 'include_archived',
      location: 'query',
      required: false,
      schema: {
        valueKind: 'boolean',
        properties: [],
        requiredProperties: [],
        items: null,
        allowedValues: [],
        additionalPropertiesAllowed: false,
      },
    },
  ],
  requestBody: {
    required: true,
    mediaType: 'application/json',
    schema: {
      valueKind: 'object',
      properties: [
        {
          name: 'name',
          schema: {
            valueKind: 'string',
            properties: [],
            requiredProperties: [],
            items: null,
            allowedValues: [],
            additionalPropertiesAllowed: false,
          },
        },
        {
          name: 'filters',
          schema: {
            valueKind: 'array',
            properties: [],
            requiredProperties: [],
            items: {
              valueKind: 'string',
              properties: [],
              requiredProperties: [],
              items: null,
              allowedValues: [],
              additionalPropertiesAllowed: false,
            },
            allowedValues: [],
            additionalPropertiesAllowed: false,
          },
        },
      ],
      requiredProperties: ['name'],
      items: null,
      allowedValues: [],
      additionalPropertiesAllowed: false,
    },
  },
  responsePolicy: {
    textAllowed: true,
    fileArtifactAllowed: false,
    mediaTypes: ['application/json'],
  },
  executionPolicy: {
    risk: 'write',
    approval: 'required',
    enforcementOwner: 'aevatar',
    allowedExecutionModes: ['interactive'],
  },
};

function requireField(
  field: OperationInputField | undefined,
): OperationInputField {
  expect(field).toBeDefined();
  if (!field) throw new Error('Expected operation input field.');
  return field;
}

describe('guided tool call configuration', () => {
  afterEach(() => setLocale('en-US', false));

  it('uses exact selector identities without depending on display names', () => {
    expect(capabilitySelectorKey(selector)).toBe(
      'nyxid_operation\u0000us-posthog-alpha\u0000update-dashboard',
    );
    expect(toDocumentCapability(selector)).toEqual({
      nyxid_operation: {
        user_service_id: 'us-posthog-alpha',
        endpoint_id: 'update-dashboard',
      },
    });
  });

  it('maps declared path, query, and body properties into required-first inputs', () => {
    expect(
      listOperationInputFields(operation).map((field) => ({
        key: field.key,
        group: field.group,
        required: field.required,
        valueKind: field.schema.valueKind,
      })),
    ).toEqual([
      {
        key: 'path:dashboard_id',
        group: 'path',
        required: true,
        valueKind: 'integer',
      },
      {
        key: 'body:name',
        group: 'body',
        required: true,
        valueKind: 'string',
      },
      {
        key: 'query:include_archived',
        group: 'query',
        required: false,
        valueKind: 'boolean',
      },
      {
        key: 'body:filters',
        group: 'body',
        required: false,
        valueKind: 'array',
      },
    ]);
  });

  it.each([
    {
      name: 'text-only',
      responsePolicy: {
        textAllowed: true,
        fileArtifactAllowed: false,
        mediaTypes: ['application/json'],
      },
      initial: { future_envelope: { version: 2 } },
      expected: {
        future_envelope: { version: 2 },
        response_mode: 'text',
      },
      changed: true,
    },
    {
      name: 'file-only',
      responsePolicy: {
        textAllowed: false,
        fileArtifactAllowed: true,
        mediaTypes: ['application/pdf'],
      },
      initial: { response_mode: 'text', future_envelope: { version: 2 } },
      expected: {
        response_mode: 'file_artifact',
        future_envelope: { version: 2 },
      },
      changed: true,
    },
    {
      name: 'dual-mode with a valid saved choice',
      responsePolicy: {
        textAllowed: true,
        fileArtifactAllowed: true,
        mediaTypes: ['application/json', 'application/pdf'],
      },
      initial: {
        response_mode: 'file_artifact',
        future_envelope: { version: 2 },
      },
      expected: {
        response_mode: 'file_artifact',
        future_envelope: { version: 2 },
      },
      changed: false,
    },
  ])('derives response mode for a $name operation without dropping unknown arguments', ({
    responsePolicy,
    initial,
    expected,
    changed,
  }) => {
    const result = reconcileOperationResponseMode(initial, {
      ...operation,
      responsePolicy,
    });

    expect(result).toEqual({ arguments: expected, changed });
  });

  it('renders a required response-format choice only when both modes are allowed', () => {
    const dualModeOperation = {
      ...operation,
      responsePolicy: {
        textAllowed: true,
        fileArtifactAllowed: true,
        mediaTypes: ['application/json', 'application/pdf'],
      },
    };

    expect(
      listOperationInputFields(dualModeOperation).map((field) => field.key),
    ).toContain('response:response_mode');
    expect(
      listOperationInputFields(operation).map((field) => field.key),
    ).not.toContain('response:response_mode');
  });

  it('preserves unknown argument keys while updating one declared input', () => {
    const parsed = parseToolArguments(
      JSON.stringify({
        query: { existing: 'keep-me' },
        future_envelope: { version: 2 },
        response_mode: 'text',
      }),
    );
    expect(parsed.error).toBeNull();
    const queryField = requireField(
      listOperationInputFields(operation).find(
        (field) => field.key === 'query:include_archived',
      ),
    );

    const updated = writeOperationInputValue(
      parsed.arguments,
      queryField,
      true,
    );

    expect(updated.error).toBeNull();
    expect(updated.arguments).toEqual({
      query: { existing: 'keep-me', include_archived: true },
      future_envelope: { version: 2 },
      response_mode: 'text',
    });
    expect(JSON.parse(formatToolArguments(updated.arguments))).toEqual(
      updated.arguments,
    );
  });

  it('keeps workflow expressions as strings even for numeric inputs', () => {
    const workflowExpression = `\${steps.lookup.output}`;
    const pathField = requireField(
      listOperationInputFields(operation).find(
        (field) => field.key === 'path:dashboard_id',
      ),
    );
    const updated = writeOperationInputValue({}, pathField, workflowExpression);

    expect(updated.error).toBeNull();
    expect(readOperationInputValue(updated.arguments, pathField)).toBe(
      workflowExpression,
    );
  });

  it.each([
    ['integer', '42', 42],
    ['array', '["one","two"]', ['one', 'two']],
  ])('coerces valid %s literals', (kind, rawValue, expectedValue) => {
    const field = requireField(
      listOperationInputFields(operation).find(
        (entry) => entry.schema.valueKind === kind,
      ),
    );

    const updated = writeOperationInputValue({}, field, rawValue);

    expect(updated.error).toBeNull();
    expect(readOperationInputValue(updated.arguments, field)).toEqual(
      expectedValue,
    );
  });

  it('coerces a boolean literal', () => {
    const field = requireField(
      listOperationInputFields(operation).find(
        (entry) => entry.schema.valueKind === 'boolean',
      ),
    );
    const updated = writeOperationInputValue({}, field, 'true');

    expect(updated.error).toBeNull();
    expect(readOperationInputValue(updated.arguments, field)).toBe(true);
  });

  it('reports invalid JSON without replacing the original argument text', () => {
    const parsed = parseToolArguments('{not-json');

    expect(parsed).toEqual({
      arguments: {},
      error: 'Action inputs must be a JSON object.',
      originalText: '{not-json',
    });
  });

  it('removes an empty optional value and reports an empty required value', () => {
    const [requiredFieldCandidate, , optionalFieldCandidate] =
      listOperationInputFields(operation);
    const requiredField = requireField(requiredFieldCandidate);
    const optionalField = requireField(optionalFieldCandidate);
    const initial = {
      path_params: { dashboard_id: 42 },
      query: { include_archived: true },
    };

    const optionalResult = writeOperationInputValue(initial, optionalField, '');
    expect(optionalResult.error).toBeNull();
    expect(optionalResult.arguments).toEqual({
      path_params: { dashboard_id: 42 },
    });

    const requiredResult = writeOperationInputValue(initial, requiredField, '');
    expect(requiredResult.error).toBe('Dashboard id is required.');
    expect(requiredResult.arguments).toEqual({
      query: { include_archived: true },
    });
  });

  it('localizes generated labels and validation guidance', () => {
    setLocale('zh-CN', false);
    const dualModeFields = listOperationInputFields({
      ...operation,
      responsePolicy: {
        textAllowed: true,
        fileArtifactAllowed: true,
        mediaTypes: ['application/json', 'application/pdf'],
      },
    });
    const requiredField = requireField(
      dualModeFields.find((field) => field.key === 'path:dashboard_id'),
    );

    expect(
      dualModeFields.find((field) => field.key === 'response:response_mode')
        ?.label,
    ).toBe('结果格式');
    expect(parseToolArguments('{not-json').error).toBe(
      '操作输入必须是 JSON 对象。',
    );
    expect(writeOperationInputValue({}, requiredField, '').error).toBe(
      'Dashboard id 为必填项。',
    );
  });
});
