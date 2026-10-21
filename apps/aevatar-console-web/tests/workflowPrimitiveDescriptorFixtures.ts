import type { WorkflowPrimitiveDescriptor } from '../src/shared/models/runtime/query';

// Minimal catalogue responses for focused UI tests. Optional parameters may be
// omitted here; individual tests provide custom descriptors for schema changes.
export const workflowNodeDescriptorFixtures: WorkflowPrimitiveDescriptor[] = [
  {
    name: 'llm_call',
    aliases: [],
    category: 'ai',
    description: 'Send an instruction and workflow input to an AI model.',
    exampleWorkflows: [],
    parameters: [
      {
        name: 'prompt_prefix',
        label: 'Instruction',
        type: 'string',
        control: 'textarea',
        required: false,
        description: 'Instruction added before the workflow input.',
        default: '',
        enumValues: [],
      },
    ],
  },
  {
    name: 'tool_call',
    aliases: [],
    category: 'integration',
    description: 'Call a tool.',
    exampleWorkflows: [],
    parameters: [
      {
        name: 'tool',
        label: 'Tool name',
        type: 'string',
        required: true,
        description: 'Name of the tool.',
        default: '',
        enumValues: [],
      },
      {
        name: 'arguments',
        label: 'Arguments',
        type: 'string',
        control: 'textarea',
        required: false,
        description:
          "Property names and value types must match the tool's expected input.",
        default: '',
        enumValues: [],
      },
    ],
  },
  {
    name: 'vote',
    aliases: [],
    category: 'control',
    description: 'Choose a result.',
    exampleWorkflows: [],
    parameters: [
      {
        name: 'rule_mode',
        type: 'string',
        control: 'select',
        label: 'Rule',
        required: false,
        description: '',
        default: 'majority',
        enumValues: [
          'all',
          'majority',
          'quorum',
          'label_count_constraints',
          'predicate',
        ],
      },
    ],
  },
];
