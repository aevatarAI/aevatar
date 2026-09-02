import type {
  StudioWorkflowCapabilityOperation,
  StudioWorkflowCapabilitySelector,
} from '@/shared/studio/models';
import {
  capabilitySelectorKey,
  formatToolArguments,
  listOperationInputFields,
  parseToolArguments,
  readOperationInputValue,
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

describe('guided tool call configuration', () => {
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

  it('preserves unknown argument keys while updating one declared input', () => {
    const parsed = parseToolArguments(
      JSON.stringify({
        query: { existing: 'keep-me' },
        future_envelope: { version: 2 },
        response_mode: 'text',
      }),
    );
    expect(parsed.error).toBeNull();
    const queryField = listOperationInputFields(operation).find(
      (field) => field.key === 'query:include_archived',
    );
    expect(queryField).toBeDefined();

    const updated = writeOperationInputValue(
      parsed.arguments,
      queryField!,
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
    const workflowExpression = '$' + '{steps.lookup.output}';
    const pathField = listOperationInputFields(operation).find(
      (field) => field.key === 'path:dashboard_id',
    );
    const updated = writeOperationInputValue(
      {},
      pathField!,
      workflowExpression,
    );

    expect(updated.error).toBeNull();
    expect(readOperationInputValue(updated.arguments, pathField!)).toBe(
      workflowExpression,
    );
  });

  it.each([
    ['integer', '42', 42],
    ['array', '[\"one\",\"two\"]', ['one', 'two']],
  ])('coerces valid %s literals', (kind, rawValue, expectedValue) => {
    const field = listOperationInputFields(operation).find(
      (entry) => entry.schema.valueKind === kind,
    );
    expect(field).toBeDefined();

    const updated = writeOperationInputValue({}, field!, rawValue);

    expect(updated.error).toBeNull();
    expect(readOperationInputValue(updated.arguments, field!)).toEqual(
      expectedValue,
    );
  });

  it('coerces a boolean literal', () => {
    const field = listOperationInputFields(operation).find(
      (entry) => entry.schema.valueKind === 'boolean',
    );
    const updated = writeOperationInputValue({}, field!, 'true');

    expect(updated.error).toBeNull();
    expect(readOperationInputValue(updated.arguments, field!)).toBe(true);
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
    const [requiredField, , optionalField] =
      listOperationInputFields(operation);
    const initial = {
      path_params: { dashboard_id: 42 },
      query: { include_archived: true },
    };

    const optionalResult = writeOperationInputValue(
      initial,
      optionalField!,
      '',
    );
    expect(optionalResult.error).toBeNull();
    expect(optionalResult.arguments).toEqual({
      path_params: { dashboard_id: 42 },
    });

    const requiredResult = writeOperationInputValue(
      initial,
      requiredField!,
      '',
    );
    expect(requiredResult.error).toBe('Dashboard id is required.');
    expect(requiredResult.arguments).toEqual({
      query: { include_archived: true },
    });
  });
});
