import type { WorkflowPrimitiveDescriptor } from '@/shared/models/runtime/query';
import {
  buildNodeConfigFields,
  updateNodeConfigFieldParametersText,
  validateNodeConfigParametersText,
} from './nodeConfigFields';

const descriptor: WorkflowPrimitiveDescriptor = {
  name: 'custom_step',
  aliases: [],
  category: 'general',
  description: '',
  exampleWorkflows: [],
  parameters: [
    {
      name: 'config',
      type: 'object',
      control: 'json',
      label: 'Configuration',
      required: false,
      description: '',
      default: '',
      enumValues: [],
    },
  ],
};

describe('legacy Studio descriptor adapter', () => {
  it('writes fallback edits to the canonical parameter instead of the display field name', () => {
    const parametersText = '{"prompt_prefix":"Before","retained":true}';
    const field = buildNodeConfigFields({
      nodeType: 'llm_call',
      parametersText,
    }).fields[0];
    expect(field).toMatchObject({ name: 'prompt_prefix', value: 'Before' });
    expect(
      JSON.parse(
        updateNodeConfigFieldParametersText({
          field,
          nodeType: 'llm_call',
          parametersText,
          rawValue: 'After',
        }),
      ),
    ).toEqual({ prompt_prefix: 'After', retained: true });
  });

  it('uses the same server fields and preserves other parameters during JSON edits', () => {
    const parametersText = JSON.stringify({
      config: { mode: 'strict' },
      retained: 3,
    });
    const fields = buildNodeConfigFields({
      nodeType: 'custom_step',
      parametersText,
      primitiveDescriptor: descriptor,
    }).fields;
    expect(fields).toHaveLength(1);
    expect(fields[0]).toMatchObject({
      name: 'config',
      kind: 'json',
      valueType: 'object',
    });
    expect(
      JSON.parse(
        updateNodeConfigFieldParametersText({
          field: fields[0],
          nodeType: 'custom_step',
          parametersText,
          rawValue: '{"mode":"relaxed"}',
        }),
      ),
    ).toEqual({ config: { mode: 'relaxed' }, retained: 3 });
  });

  it('retains invalid JSON text so the apply path cannot silently accept it', () => {
    const parametersText = '{"config":{}}';
    const field = buildNodeConfigFields({
      nodeType: 'custom_step',
      parametersText,
      primitiveDescriptor: descriptor,
    }).fields[0];
    const nextText = updateNodeConfigFieldParametersText({
      field,
      nodeType: 'custom_step',
      parametersText,
      rawValue: '{"mode":',
    });
    expect(validateNodeConfigParametersText(nextText)).toBeTruthy();
  });

  it('preserves object and array template literals through legacy JSON and sibling edits', () => {
    const templateDescriptor: WorkflowPrimitiveDescriptor = {
      ...descriptor,
      parameters: [
        ...descriptor.parameters,
        { ...descriptor.parameters[0], name: 'items', type: 'array' },
        { ...descriptor.parameters[0], name: 'arguments', type: 'string' },
        {
          ...descriptor.parameters[0],
          name: 'typed_json',
          type: 'json',
          control: 'text',
        },
        {
          ...descriptor.parameters[0],
          name: 'message',
          type: 'string',
          control: 'text',
        },
      ],
    };
    const parameters = {
      // biome-ignore lint/suspicious/noTemplateCurlyInString: Workflow expressions are authored as literal strings.
      config: { message: '${input}' },
      items: ['{{ steps.previous }}'],
      // biome-ignore lint/suspicious/noTemplateCurlyInString: Workflow expressions are authored as literal strings.
      arguments: '{ "message": "${input}" }',
      typed_json: { message: '{{ steps.previous }}' },
      message: 'before',
    };
    let parametersText = JSON.stringify(parameters);
    const fields = buildNodeConfigFields({
      nodeType: 'custom_step',
      parametersText,
      primitiveDescriptor: templateDescriptor,
    }).fields;

    for (const field of fields) {
      parametersText = updateNodeConfigFieldParametersText({
        field,
        nodeType: 'custom_step',
        parametersText,
        rawValue: field.name === 'message' ? 'after' : field.value,
      });
    }

    expect(JSON.parse(parametersText)).toEqual({
      ...parameters,
      message: 'after',
    });
  });

  it('keeps whole-field runtime expressions in legacy JSON fields', () => {
    const expressionDescriptor: WorkflowPrimitiveDescriptor = {
      ...descriptor,
      parameters: [
        ...descriptor.parameters,
        { ...descriptor.parameters[0], name: 'items', type: 'array' },
      ],
    };
    const parameters = { config: '{{ steps.previous }}', items: '$input' };
    let parametersText = JSON.stringify(parameters);
    const fields = buildNodeConfigFields({
      nodeType: 'custom_step',
      parametersText,
      primitiveDescriptor: expressionDescriptor,
    }).fields;

    for (const field of fields) {
      parametersText = updateNodeConfigFieldParametersText({
        field,
        nodeType: 'custom_step',
        parametersText,
        rawValue: field.value,
      });
    }

    expect(JSON.parse(parametersText)).toEqual(parameters);
  });
});
