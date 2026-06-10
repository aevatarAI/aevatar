import {
  applyRawStudioNodeConfiguration,
  applyStudioNodeConfigurationValues,
  applyStudioNodeConfigurationValuesWithValidation,
  formatRawStudioNodeConfiguration,
  getStudioNodeConfigurationSchema,
  readStudioNodeConfigurationValues,
} from './nodeConfigFields';

describe('studio node configuration semantics', () => {
  it('presents llm_call prompt_prefix as an Instruction field while preserving runtime parameters', () => {
    const schema = getStudioNodeConfigurationSchema('llm_call');
    const values = readStudioNodeConfigurationValues('llm_call', {
      prompt_prefix: 'Classify the request.',
    });

    expect(schema.fields).toEqual([
      expect.objectContaining({
        label: expect.objectContaining({ defaultMessage: 'Instruction' }),
        name: 'instruction',
        parameterName: 'prompt',
        path: 'prompt',
      }),
    ]);
    expect(values).toEqual({
      instruction: 'Classify the request.',
    });

    const nextParameters = applyStudioNodeConfigurationValues(
      'llm_call',
      { prompt_prefix: 'Old instruction' },
      { instruction: 'Updated instruction' },
    );

    expect(nextParameters).toEqual({
      prompt_prefix: 'Updated instruction',
    });
    expect(nextParameters).not.toHaveProperty('prompt');
  });

  it('reads legacy llm_call prompt but writes the canonical prompt_prefix parameter', () => {
    const values = readStudioNodeConfigurationValues('llm_call', {
      prompt: 'Legacy instruction',
    });

    expect(values).toEqual({
      instruction: 'Legacy instruction',
    });
    expect(
      applyStudioNodeConfigurationValues(
        'llm_call',
        { prompt: 'Legacy instruction' },
        { instruction: 'Canonical instruction' },
      ),
    ).toEqual({
      prompt_prefix: 'Canonical instruction',
    });
  });

  it('maps transform operation without exposing raw backend op as the user field name', () => {
    const schema = getStudioNodeConfigurationSchema('transform');
    const values = readStudioNodeConfigurationValues('transform', { op: 'trim' });

    expect(schema.fields[0]).toEqual(
      expect.objectContaining({
        label: expect.objectContaining({ defaultMessage: 'Operation' }),
        name: 'operation',
        parameterName: 'op',
        path: 'op',
      }),
    );
    expect(values).toEqual({ operation: 'trim' });
    expect(
      applyStudioNodeConfigurationValues(
        'transform',
        { op: 'trim' },
        { operation: 'uppercase' },
      ),
    ).toEqual({ op: 'uppercase' });
  });

  it('presents child step type values as product node labels while preserving runtime ids', () => {
    const schema = getStudioNodeConfigurationSchema('cache');
    const values = readStudioNodeConfigurationValues('cache', {
      cache_key: '$input',
      child_step_type: 'llm_call',
      ttl_seconds: '600',
    });
    const childStepTypeField = schema.fields.find(
      (field) => field.name === 'childStepType',
    );

    expect(childStepTypeField).toEqual(
      expect.objectContaining({
        kind: 'select',
        label: expect.objectContaining({ defaultMessage: 'Cached node' }),
        parameterName: 'child_step_type',
        path: 'child_step_type',
      }),
    );
    expect(childStepTypeField?.placeholder).toBeUndefined();
    expect(childStepTypeField?.options).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          label: expect.objectContaining({ defaultMessage: 'LLM call' }),
          value: 'llm_call',
        }),
      ]),
    );
    expect(values).toEqual({
      cacheKey: '$input',
      childStepType: 'llm_call',
      ttlSeconds: '600',
    });
    expect(
      applyStudioNodeConfigurationValues(
        'cache',
        { cache_key: '$input', child_step_type: 'llm_call', ttl_seconds: '600' },
        { cacheKey: '$input', childStepType: 'llm_call', ttlSeconds: '900' },
      ),
    ).toEqual({
      cache_key: '$input',
      child_step_type: 'llm_call',
      ttl_seconds: '900',
    });
  });

  it('keeps string-shaped numeric parameters as strings for the existing runtime contract', () => {
    const values = readStudioNodeConfigurationValues('wait_signal', {
      signal_name: 'continue',
      timeout_ms: '60000',
    });

    expect(values).toEqual({
      signalName: 'continue',
      timeoutMs: '60000',
    });
    expect(
      applyStudioNodeConfigurationValues(
        'wait_signal',
        { signal_name: 'continue', timeout_ms: '60000' },
        { signalName: 'approval-ready', timeoutMs: '90000' },
      ),
    ).toEqual({
      signal_name: 'approval-ready',
      timeout_ms: '90000',
    });
  });

  it('keeps raw configuration as an explicit advanced JSON path', () => {
    expect(formatRawStudioNodeConfiguration({ op: 'trim' })).toBe(
      '{\n  "op": "trim"\n}',
    );
    expect(
      applyRawStudioNodeConfiguration('transform', '{ "op": "lowercase" }'),
    ).toEqual({ op: 'lowercase' });
  });

  it('infers typed fields for unknown node parameters from the workflow document', () => {
    const parameters = {
      enabled: true,
      limit: 3,
      notes: 'line one\nline two',
      payload: { source: 'input' },
      tags: ['alpha', 'beta'],
      title: 'Draft',
    };
    const schema = getStudioNodeConfigurationSchema('custom_step', parameters);

    expect(schema.fields).toEqual([
      expect.objectContaining({
        control: 'boolean',
        label: expect.objectContaining({ defaultMessage: 'Enabled' }),
        name: 'enabled',
        path: 'enabled',
      }),
      expect.objectContaining({
        control: 'number',
        label: expect.objectContaining({ defaultMessage: 'Limit' }),
        name: 'limit',
        path: 'limit',
      }),
      expect.objectContaining({
        control: 'textarea',
        label: expect.objectContaining({ defaultMessage: 'Notes' }),
        name: 'notes',
        path: 'notes',
      }),
      expect.objectContaining({
        control: 'object',
        label: expect.objectContaining({ defaultMessage: 'Payload' }),
        name: 'payload',
        path: 'payload',
      }),
      expect.objectContaining({
        control: 'array',
        label: expect.objectContaining({ defaultMessage: 'Tags' }),
        name: 'tags',
        path: 'tags',
      }),
      expect.objectContaining({
        control: 'text',
        label: expect.objectContaining({ defaultMessage: 'Title' }),
        name: 'title',
        path: 'title',
      }),
    ]);
    expect(readStudioNodeConfigurationValues('custom_step', parameters)).toEqual({
      enabled: 'true',
      limit: '3',
      notes: 'line one\nline two',
      payload: '{\n  "source": "input"\n}',
      tags: '[\n  "alpha",\n  "beta"\n]',
      title: 'Draft',
    });
  });

  it('applies inferred typed values without diverging from raw parameters', () => {
    const result = applyStudioNodeConfigurationValuesWithValidation(
      'custom_step',
      {
        enabled: true,
        limit: 3,
        payload: { source: 'input' },
      },
      {
        enabled: 'false',
        limit: '5',
        payload: '{ "source": "updated" }',
      },
    );

    expect(result).toEqual({
      errors: [],
      parameters: {
        enabled: false,
        limit: 5,
        payload: { source: 'updated' },
      },
      valid: true,
    });
  });

  it('keeps inferred fields available from a stable schema source when values are cleared', () => {
    const result = applyStudioNodeConfigurationValuesWithValidation(
      'custom_step',
      {
        enabled: true,
        title: 'Draft',
      },
      {
        enabled: 'true',
        title: '',
      },
      {
        enabled: true,
        title: 'Draft',
      },
    );

    expect(result).toEqual({
      errors: [],
      parameters: {
        enabled: true,
      },
      valid: true,
    });
    expect(
      readStudioNodeConfigurationValues(
        'custom_step',
        result.parameters,
        {
          enabled: true,
          title: 'Draft',
        },
      ),
    ).toEqual({
      enabled: 'true',
      title: '',
    });
  });

  it('rejects invalid inferred structured JSON fields', () => {
    const result = applyStudioNodeConfigurationValuesWithValidation(
      'custom_step',
      { payload: { source: 'input' } },
      { payload: 'not-json' },
    );

    expect(result.valid).toBe(false);
    expect(result.errors[0]).toContain('Payload');
    expect(result.errors[0]).toContain('Unexpected token');
    expect(result.parameters).toEqual({ payload: { source: 'input' } });
  });
});
