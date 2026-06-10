import {
  buildNodeConfigFields,
  formatNodeConfigFieldCopy,
  updateNodeConfigFieldParametersText,
  validateNodeConfigParametersText,
} from './nodeConfigFields';

describe('nodeConfigFields', () => {
  it('adapts connector fields without requiring the renderer to know the node type', () => {
    const fieldSet = buildNodeConfigFields({
      connectors: [
        {
          enabled: true,
          name: 'web-search',
          retry: 0,
          timeoutMs: 10000,
          type: 'http',
        },
      ],
      nodeType: 'connector_call',
      parametersText: JSON.stringify({ connector: 'web-search', limit: 3 }),
    });

    expect(fieldSet.parseError).toBe('');
    expect(fieldSet.fields.map((field) => field.name)).toEqual(
      expect.arrayContaining(['connector', 'operation', 'limit']),
    );
    expect(fieldSet.fields.find((field) => field.name === 'connector')).toMatchObject({
      kind: 'select',
      options: [{ label: 'web-search - http', value: 'web-search' }],
      value: 'web-search',
    });
    expect(fieldSet.fields.find((field) => field.name === 'limit')).toMatchObject({
      kind: 'text',
      value: '3',
      valueType: 'number',
    });
  });

  it('maps runtime prompt descriptors to prompt_prefix for llm_call', () => {
    const fieldSet = buildNodeConfigFields({
      nodeType: 'llm_call',
      parametersText: JSON.stringify({ prompt: 'Legacy prompt' }),
      primitiveDescriptor: {
        aliases: [],
        category: 'ai',
        description: 'Call an LLM.',
        exampleWorkflows: [],
        name: 'llm_call',
        parameters: [
          {
            default: '',
            description: 'Prompt override',
            enumValues: [],
            name: 'prompt',
            required: false,
            type: 'string',
          },
        ],
      },
    });

    expect(fieldSet.fields).toHaveLength(1);
    expect(fieldSet.fields[0]).toMatchObject({
      name: 'prompt_prefix',
      value: 'Legacy prompt',
    });
    expect(formatNodeConfigFieldCopy(fieldSet.fields[0].label)).toBe(
      'Prompt instruction',
    );

    const nextText = updateNodeConfigFieldParametersText({
      field: fieldSet.fields[0],
      nodeType: 'llm_call',
      parametersText: JSON.stringify({ prompt: 'Legacy prompt' }),
      rawValue: 'Translate the input.',
    });

    expect(JSON.parse(nextText)).toEqual({
      prompt_prefix: 'Translate the input.',
    });
  });

  it('infers unknown-node fields and edits object or array values through JSON fallback', () => {
    const fieldSet = buildNodeConfigFields({
      nodeType: 'custom_step',
      parametersText: JSON.stringify({
        config: { mode: 'strict' },
        tags: ['risk'],
      }),
    });

    expect(fieldSet.fields.find((field) => field.name === 'config')).toMatchObject({
      kind: 'json',
      value: '{\n  "mode": "strict"\n}',
      valueType: 'object',
    });
    expect(fieldSet.fields.find((field) => field.name === 'tags')).toMatchObject({
      kind: 'json',
      value: '[\n  "risk"\n]',
      valueType: 'array',
    });

    const nextText = updateNodeConfigFieldParametersText({
      field: fieldSet.fields.find((field) => field.name === 'config')!,
      nodeType: 'custom_step',
      parametersText: JSON.stringify({
        config: { mode: 'strict' },
        tags: ['risk'],
      }),
      rawValue: '{\n  "mode": "relaxed"\n}',
    });

    expect(JSON.parse(nextText)).toEqual({
      config: { mode: 'relaxed' },
      tags: ['risk'],
    });
  });

  it('keeps invalid object edits in parametersText so the existing apply path blocks them', () => {
    const fieldSet = buildNodeConfigFields({
      nodeType: 'custom_step',
      parametersText: JSON.stringify({ config: { mode: 'strict' } }),
    });
    const configField = fieldSet.fields.find((field) => field.name === 'config');
    if (!configField) {
      throw new Error('Expected config field.');
    }

    const nextText = updateNodeConfigFieldParametersText({
      field: configField,
      nodeType: 'custom_step',
      parametersText: JSON.stringify({ config: { mode: 'strict' } }),
      rawValue: '{ "mode": ',
    });

    expect(validateNodeConfigParametersText(nextText)).toBeTruthy();
  });
});
