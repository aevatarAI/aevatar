import type {
  WorkflowPrimitiveDescriptor,
  WorkflowPrimitiveParameterDescriptor,
} from '@/shared/models/runtime/query';
import {
  applyRawStudioNodeConfiguration,
  applyStudioNodeConfigurationValuesWithValidation,
  getStudioNodeConfigurationSchema,
  readStudioNodeConfigurationValues,
} from './nodeConfigFields';

const parameter = (
  name: string,
  overrides: Partial<WorkflowPrimitiveParameterDescriptor> = {},
): WorkflowPrimitiveParameterDescriptor => ({
  name,
  type: 'string',
  required: false,
  description: '',
  default: '',
  enumValues: [],
  ...overrides,
});
const primitive = (
  name: string,
  parameters: WorkflowPrimitiveParameterDescriptor[],
): WorkflowPrimitiveDescriptor => ({
  name,
  parameters,
  aliases: [],
  category: 'general',
  description: '',
  exampleWorkflows: [],
});

describe('descriptor-driven node configuration', () => {
  it('uses server fields for a known node without merging its built-in fields', () => {
    const descriptor = primitive('llm_call', [
      parameter('new_backend_option', {
        label: 'New backend option',
        enumValues: ['strict', 'relaxed'],
      }),
    ]);
    const schema = getStudioNodeConfigurationSchema('llm_call', descriptor);

    expect(schema.source).toBe('descriptor');
    expect(schema.fields.map((field) => field.name)).toEqual([
      'new_backend_option',
    ]);
    expect(schema.fields[0]).toMatchObject({
      control: 'select',
      options: [{ value: 'strict' }, { value: 'relaxed' }],
    });
    expect(getStudioNodeConfigurationSchema('llm_call').source).toBe('legacy');
    expect(
      getStudioNodeConfigurationSchema('llm_call').fields.some(
        (field) => field.parameterName === 'prompt_prefix',
      ),
    ).toBe(true);
  });

  it('treats an explicitly empty server schema as authoritative', () => {
    expect(
      getStudioNodeConfigurationSchema('llm_call', primitive('llm_call', [])),
    ).toMatchObject({ source: 'descriptor', fields: [] });
    expect(getStudioNodeConfigurationSchema('custom_step')).toMatchObject({
      source: 'unavailable',
      fields: [],
    });
    expect(
      applyRawStudioNodeConfiguration(
        'custom_step',
        '{"unpublished_option":true}',
      ),
    ).toEqual({ unpublished_option: true });
  });

  it('supports registered aliases and preserves unadvertised parameters when applying edits', () => {
    const descriptor = {
      ...primitive('extension_step', [
        parameter('new_option'),
        parameter('mode', { enumValues: ['strict'] }),
      ]),
      aliases: ['extension_alias'],
    };
    const parameters = {
      new_option: 'before',
      mode: 'STRICT',
      retained: { secret: 'reference-only' },
    };
    const values = readStudioNodeConfigurationValues(
      'extension_alias',
      parameters,
      descriptor,
    );
    expect(values).toEqual({ new_option: 'before', mode: 'STRICT' });
    expect(
      applyStudioNodeConfigurationValuesWithValidation(
        'extension_alias',
        parameters,
        { ...values, new_option: 'after' },
        descriptor,
      ),
    ).toEqual({
      valid: true,
      errors: [],
      parameters: { ...parameters, new_option: 'after' },
    });
  });

  it('validates server types, constraints and enum choices before applying a configuration', () => {
    const descriptor = primitive('extension_step', [
      parameter('count', {
        type: 'int',
        control: 'text',
        minimum: 1,
        maximum: 10,
        integer: true,
      }),
      parameter('mode', { enumValues: ['strict'] }),
      parameter('items', { type: 'array', control: 'json' }),
      parameter('enabled', { type: 'bool', control: 'text' }),
    ]);
    const invalid = applyStudioNodeConfigurationValuesWithValidation(
      'extension_step',
      {},
      {
        count: '11',
        mode: 'unknown',
        items: '{}',
        enabled: 'yes',
      },
      descriptor,
    );
    expect(invalid.valid).toBe(false);
    expect(invalid.errors).toHaveLength(4);
    const valid = applyStudioNodeConfigurationValuesWithValidation(
      'extension_step',
      { retained: true },
      {
        count: '3',
        mode: 'strict',
        items: '["one"]',
        enabled: 'false',
      },
      descriptor,
    );
    expect(valid).toEqual({
      valid: true,
      errors: [],
      parameters: {
        count: 3,
        mode: 'strict',
        items: ['one'],
        enabled: false,
        retained: true,
      },
    });
    expect(
      applyStudioNodeConfigurationValuesWithValidation(
        'extension_step',
        {},
        { count: '1.5' },
        descriptor,
      ).valid,
    ).toBe(false);
  });

  it('keeps execution-time expressions and does not materialize optional defaults', () => {
    const descriptor = primitive('extension_step', [
      parameter('count', { type: 'int', control: 'number', default: '10' }),
      parameter('enabled', { type: 'bool', control: 'boolean' }),
      parameter('payload', { type: 'object', control: 'json' }),
      parameter('optional', { default: 'server-default' }),
    ]);
    const parameters = {
      count: '$input',
      // biome-ignore lint/suspicious/noTemplateCurlyInString: Workflow expressions are authored as literal strings.
      enabled: '${flags.enabled}',
      payload: '{{ steps.previous }}',
    };
    const values = readStudioNodeConfigurationValues(
      'extension_step',
      parameters,
      descriptor,
    );
    expect(
      applyStudioNodeConfigurationValuesWithValidation(
        'extension_step',
        parameters,
        values,
        descriptor,
      ),
    ).toEqual({ valid: true, errors: [], parameters });
  });

  it('keeps the shipped schema usable when a descriptor is absent', () => {
    const schema = getStudioNodeConfigurationSchema('wait_signal', null);
    expect(schema.source).toBe('legacy');
    const parameters = { signal_name: 'resume', retained: 'keep me' };
    const values = readStudioNodeConfigurationValues(
      'wait_signal',
      parameters,
      null,
    );
    const signalFieldName =
      schema.fields.find((field) => field.parameterName === 'signal_name')
        ?.name ?? '';
    expect(
      applyStudioNodeConfigurationValuesWithValidation(
        'wait_signal',
        parameters,
        { ...values, [signalFieldName]: 'continue' },
        null,
      ),
    ).toEqual({
      valid: true,
      errors: [],
      parameters: { signal_name: 'continue', retained: 'keep me' },
    });
  });

  it('preserves authored JSON strings and whitespace separators while editing other parameters', () => {
    const descriptor = primitive('extension_step', [
      parameter('arguments', { type: 'string', control: 'json' }),
      parameter('separator'),
      parameter('message'),
    ]);
    const parameters = {
      arguments: '{ "value": [1, 2] }',
      separator: ' \t',
      message: 'before',
    };
    const values = readStudioNodeConfigurationValues(
      'extension_step',
      parameters,
      descriptor,
    );
    expect(
      applyStudioNodeConfigurationValuesWithValidation(
        'extension_step',
        parameters,
        { ...values, message: 'after' },
        descriptor,
      ),
    ).toEqual({
      valid: true,
      errors: [],
      parameters: { ...parameters, message: 'after' },
    });
    expect(
      applyStudioNodeConfigurationValuesWithValidation(
        'extension_step',
        parameters,
        { ...values, arguments: '{invalid}' },
        descriptor,
      ).valid,
    ).toBe(false);
  });

  it('preserves integer literals above the JavaScript precision limit', () => {
    const descriptor = primitive('lease', [
      parameter('generation', { type: 'int', integer: true, minimum: 0 }),
    ]);
    const parameters = { generation: '9007199254740993' };
    expect(
      applyStudioNodeConfigurationValuesWithValidation(
        'lease',
        parameters,
        readStudioNodeConfigurationValues('lease', parameters, descriptor),
        descriptor,
      ),
    ).toEqual({
      valid: true,
      errors: [],
      parameters,
    });
  });
});
