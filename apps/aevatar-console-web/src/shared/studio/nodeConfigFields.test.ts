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
});
