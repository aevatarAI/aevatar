import type { ConsoleMessageDescriptor } from '@/shared/i18n/messages';
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
  readonly control?: StudioNodeConfigurationFieldKind;
  readonly defaultValue?: unknown;
  readonly description?: ConsoleMessageDescriptor;
  readonly kind: StudioNodeConfigurationFieldKind;
  readonly label: ConsoleMessageDescriptor;
  readonly name: string;
  readonly options?: readonly StudioNodeConfigurationOption[];
  readonly path?: string;
  readonly parameterName: string;
  readonly placeholder?: ConsoleMessageDescriptor;
  readonly required?: boolean;
  readonly validation?: {
    readonly integer?: boolean;
    readonly max?: number;
    readonly min?: number;
  };
};

export type NodeConfigField = StudioNodeConfigurationField & {
  readonly path: string;
};

export type StudioNodeConfigurationSchema = {
  readonly fields: readonly NodeConfigField[];
  readonly stepType: string;
};

type StudioNodeConfigurationSchemaSource =
  | Record<string, unknown>
  | null
  | undefined;

type StudioNodeConfigurationSchemaDefinition = {
  readonly fields: readonly StudioNodeConfigurationField[];
  readonly stepType: string;
};

function message(id: string, defaultMessage: string): ConsoleMessageDescriptor {
  return { defaultMessage, id };
}

const SHARED_OPTIONS = {
  onFailure: [
    {
      label: message(
        'shared.studio.nodeConfiguration.option.onFailure.fail',
        'Fail the run',
      ),
      value: 'fail',
    },
    {
      label: message(
        'shared.studio.nodeConfiguration.option.onFailure.skip',
        'Skip this step',
      ),
      value: 'skip',
    },
    {
      label: message(
        'shared.studio.nodeConfiguration.option.onFailure.branch',
        'Go to a branch',
      ),
      value: 'branch',
    },
  ],
} satisfies Record<string, readonly StudioNodeConfigurationOption[]>;

const STEP_TYPE_OPTIONS = [
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.transform',
      'Transform',
    ),
    value: 'transform',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.assign',
      'Assign',
    ),
    value: 'assign',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.retrieveFacts',
      'Retrieve facts',
    ),
    value: 'retrieve_facts',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.cache',
      'Cache',
    ),
    value: 'cache',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.guard',
      'Guard',
    ),
    value: 'guard',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.conditional',
      'Conditional',
    ),
    value: 'conditional',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.switch',
      'Switch',
    ),
    value: 'switch',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.while',
      'While',
    ),
    value: 'while',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.delay',
      'Delay',
    ),
    value: 'delay',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.waitSignal',
      'Wait for signal',
    ),
    value: 'wait_signal',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.checkpoint',
      'Checkpoint',
    ),
    value: 'checkpoint',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.llmCall',
      'LLM call',
    ),
    value: 'llm_call',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.toolCall',
      'Tool call',
    ),
    value: 'tool_call',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.evaluate',
      'Evaluate',
    ),
    value: 'evaluate',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.reflect',
      'Reflect',
    ),
    value: 'reflect',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.foreach',
      'For each',
    ),
    value: 'foreach',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.parallel',
      'Parallel',
    ),
    value: 'parallel',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.race',
      'Race',
    ),
    value: 'race',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.mapReduce',
      'Map reduce',
    ),
    value: 'map_reduce',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.workflowCall',
      'Workflow call',
    ),
    value: 'workflow_call',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.dynamicWorkflow',
      'Dynamic workflow',
    ),
    value: 'dynamic_workflow',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.vote',
      'Vote',
    ),
    value: 'vote',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.connectorCall',
      'Connector call',
    ),
    value: 'connector_call',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.emit',
      'Emit',
    ),
    value: 'emit',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.humanInput',
      'Human input',
    ),
    value: 'human_input',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.humanApproval',
      'Human approval',
    ),
    value: 'human_approval',
  },
  {
    label: message(
      'shared.studio.nodeConfiguration.stepType.option.workflowYamlValidate',
      'Workflow YAML validation',
    ),
    value: 'workflow_yaml_validate',
  },
] satisfies readonly StudioNodeConfigurationOption[];

const SCHEMAS_BY_STEP_TYPE: Record<
  string,
  StudioNodeConfigurationSchemaDefinition
> = {
  assign: {
    stepType: 'assign',
    fields: [
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.assign.target.label',
          'Target variable',
        ),
        name: 'target',
        parameterName: 'target',
        placeholder: message(
          'shared.studio.nodeConfiguration.assign.target.placeholder',
          'result',
        ),
        required: true,
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.assign.value.label',
          'Value',
        ),
        name: 'value',
        parameterName: 'value',
        placeholder: message(
          'shared.studio.nodeConfiguration.assign.value.placeholder',
          '$input',
        ),
      },
    ],
  },
  cache: {
    stepType: 'cache',
    fields: [
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.cache.key.label',
          'Cache key',
        ),
        name: 'cacheKey',
        parameterName: 'cache_key',
        placeholder: message(
          'shared.studio.nodeConfiguration.cache.key.placeholder',
          '$input',
        ),
        required: true,
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.cache.ttl.label',
          'TTL seconds',
        ),
        name: 'ttlSeconds',
        parameterName: 'ttl_seconds',
        placeholder: message(
          'shared.studio.nodeConfiguration.cache.ttl.placeholder',
          '600',
        ),
      },
      {
        kind: 'select',
        label: message(
          'shared.studio.nodeConfiguration.cache.childStep.label',
          'Cached node',
        ),
        name: 'childStepType',
        options: STEP_TYPE_OPTIONS,
        parameterName: 'child_step_type',
      },
    ],
  },
  checkpoint: {
    stepType: 'checkpoint',
    fields: [
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.checkpoint.name.label',
          'Checkpoint name',
        ),
        name: 'name',
        parameterName: 'name',
        placeholder: message(
          'shared.studio.nodeConfiguration.checkpoint.name.placeholder',
          'before_publish',
        ),
      },
    ],
  },
  conditional: {
    stepType: 'conditional',
    fields: [
      {
        kind: 'multi-line',
        label: message(
          'shared.studio.nodeConfiguration.conditional.condition.label',
          'Condition',
        ),
        name: 'condition',
        parameterName: 'condition',
        placeholder: message(
          'shared.studio.nodeConfiguration.conditional.condition.placeholder',
          'eq($input, "ok")',
        ),
        required: true,
      },
    ],
  },
  connector_call: {
    stepType: 'connector_call',
    fields: [
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.connectorCall.connector.label',
          'Connector',
        ),
        name: 'connector',
        parameterName: 'connector',
        placeholder: message(
          'shared.studio.nodeConfiguration.connectorCall.connector.placeholder',
          'Configured connector name',
        ),
        required: true,
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.connectorCall.operation.label',
          'Operation',
        ),
        name: 'operation',
        parameterName: 'operation',
        placeholder: message(
          'shared.studio.nodeConfiguration.connectorCall.operation.placeholder',
          'Operation or endpoint name',
        ),
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.connectorCall.path.label',
          'Path',
        ),
        name: 'path',
        parameterName: 'path',
        placeholder: message(
          'shared.studio.nodeConfiguration.connectorCall.path.placeholder',
          '/v1/items',
        ),
      },
      {
        kind: 'select',
        label: message(
          'shared.studio.nodeConfiguration.connectorCall.method.label',
          'Method',
        ),
        name: 'method',
        parameterName: 'method',
        options: [
          {
            label: message(
              'shared.studio.nodeConfiguration.connectorCall.method.option.get',
              'GET',
            ),
            value: 'GET',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.connectorCall.method.option.post',
              'POST',
            ),
            value: 'POST',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.connectorCall.method.option.put',
              'PUT',
            ),
            value: 'PUT',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.connectorCall.method.option.patch',
              'PATCH',
            ),
            value: 'PATCH',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.connectorCall.method.option.delete',
              'DELETE',
            ),
            value: 'DELETE',
          },
        ],
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.connectorCall.timeout.label',
          'Timeout ms',
        ),
        name: 'timeoutMs',
        parameterName: 'timeout_ms',
        placeholder: message(
          'shared.studio.nodeConfiguration.connectorCall.timeout.placeholder',
          '10000',
        ),
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.connectorCall.retry.label',
          'Retries',
        ),
        name: 'retry',
        parameterName: 'retry',
        placeholder: message(
          'shared.studio.nodeConfiguration.connectorCall.retry.placeholder',
          '0',
        ),
      },
      {
        kind: 'select',
        label: message(
          'shared.studio.nodeConfiguration.connectorCall.onError.label',
          'On error',
        ),
        name: 'onError',
        options: SHARED_OPTIONS.onFailure,
        parameterName: 'on_error',
      },
    ],
  },
  delay: {
    stepType: 'delay',
    fields: [
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.delay.duration.label',
          'Duration ms',
        ),
        name: 'durationMs',
        parameterName: 'duration_ms',
        placeholder: message(
          'shared.studio.nodeConfiguration.delay.duration.placeholder',
          '1000',
        ),
        required: true,
      },
    ],
  },
  dynamic_workflow: {
    stepType: 'dynamic_workflow',
    fields: [
      {
        description: message(
          'shared.studio.nodeConfiguration.dynamicWorkflow.originalInput.description',
          'Optional input passed into the generated workflow after YAML extraction.',
        ),
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.dynamicWorkflow.originalInput.label',
          'Original input',
        ),
        name: 'originalInput',
        parameterName: 'original_input',
        placeholder: message(
          'shared.studio.nodeConfiguration.dynamicWorkflow.originalInput.placeholder',
          '$input',
        ),
      },
    ],
  },
  emit: {
    stepType: 'emit',
    fields: [
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.emit.eventType.label',
          'Event type',
        ),
        name: 'eventType',
        parameterName: 'event_type',
        placeholder: message(
          'shared.studio.nodeConfiguration.emit.eventType.placeholder',
          'workflow.completed',
        ),
        required: true,
      },
      {
        kind: 'multi-line',
        label: message(
          'shared.studio.nodeConfiguration.emit.payload.label',
          'Payload',
        ),
        name: 'payload',
        parameterName: 'payload',
        placeholder: message(
          'shared.studio.nodeConfiguration.emit.payload.placeholder',
          '$input',
        ),
      },
    ],
  },
  evaluate: {
    stepType: 'evaluate',
    fields: [
      {
        kind: 'multi-line',
        label: message(
          'shared.studio.nodeConfiguration.evaluate.criteria.label',
          'Criteria',
        ),
        name: 'criteria',
        parameterName: 'criteria',
        placeholder: message(
          'shared.studio.nodeConfiguration.evaluate.criteria.placeholder',
          'correctness and clarity',
        ),
        required: true,
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.evaluate.scale.label',
          'Scale',
        ),
        name: 'scale',
        parameterName: 'scale',
        placeholder: message(
          'shared.studio.nodeConfiguration.evaluate.scale.placeholder',
          '1-5',
        ),
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.evaluate.threshold.label',
          'Threshold',
        ),
        name: 'threshold',
        parameterName: 'threshold',
        placeholder: message(
          'shared.studio.nodeConfiguration.evaluate.threshold.placeholder',
          '4',
        ),
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.evaluate.onBelow.label',
          'Below threshold branch',
        ),
        name: 'onBelow',
        parameterName: 'on_below',
        placeholder: message(
          'shared.studio.nodeConfiguration.evaluate.onBelow.placeholder',
          'rewrite',
        ),
      },
    ],
  },
  foreach: {
    stepType: 'foreach',
    fields: [
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.foreach.delimiter.label',
          'Delimiter',
        ),
        name: 'delimiter',
        parameterName: 'delimiter',
        placeholder: message(
          'shared.studio.nodeConfiguration.foreach.delimiter.placeholder',
          '\\n---\\n',
        ),
      },
      {
        kind: 'select',
        label: message(
          'shared.studio.nodeConfiguration.foreach.subStepType.label',
          'Item step',
        ),
        name: 'subStepType',
        options: STEP_TYPE_OPTIONS,
        parameterName: 'sub_step_type',
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.foreach.subTargetRole.label',
          'Item target role',
        ),
        name: 'subTargetRole',
        parameterName: 'sub_target_role',
        placeholder: message(
          'shared.studio.nodeConfiguration.foreach.subTargetRole.placeholder',
          'assistant',
        ),
      },
    ],
  },
  guard: {
    stepType: 'guard',
    fields: [
      {
        kind: 'select',
        label: message(
          'shared.studio.nodeConfiguration.guard.check.label',
          'Check',
        ),
        name: 'check',
        options: [
          {
            label: message(
              'shared.studio.nodeConfiguration.guard.check.option.notEmpty',
              'Input is not empty',
            ),
            value: 'not_empty',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.guard.check.option.jsonValid',
              'Input is valid JSON',
            ),
            value: 'json_valid',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.guard.check.option.regex',
              'Matches regex',
            ),
            value: 'regex',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.guard.check.option.maxLength',
              'Within max length',
            ),
            value: 'max_length',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.guard.check.option.contains',
              'Contains keyword',
            ),
            value: 'contains',
          },
        ],
        parameterName: 'check',
        required: true,
      },
      {
        kind: 'select',
        label: message(
          'shared.studio.nodeConfiguration.guard.onFailure.label',
          'On failure',
        ),
        name: 'onFail',
        options: SHARED_OPTIONS.onFailure,
        parameterName: 'on_fail',
      },
    ],
  },
  human_approval: {
    stepType: 'human_approval',
    fields: [
      {
        kind: 'multi-line',
        label: message(
          'shared.studio.nodeConfiguration.humanApproval.prompt.label',
          'Approval prompt',
        ),
        name: 'prompt',
        parameterName: 'prompt',
        placeholder: message(
          'shared.studio.nodeConfiguration.humanApproval.prompt.placeholder',
          'Approve this step?',
        ),
        required: true,
      },
      {
        kind: 'select',
        label: message(
          'shared.studio.nodeConfiguration.humanApproval.onReject.label',
          'On rejection',
        ),
        name: 'onReject',
        options: [
          {
            label: message(
              'shared.studio.nodeConfiguration.humanApproval.onReject.option.fail',
              'Fail the run',
            ),
            value: 'fail',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.humanApproval.onReject.option.skip',
              'Skip this step',
            ),
            value: 'skip',
          },
        ],
        parameterName: 'on_reject',
      },
    ],
  },
  human_input: {
    stepType: 'human_input',
    fields: [
      {
        kind: 'multi-line',
        label: message(
          'shared.studio.nodeConfiguration.humanInput.prompt.label',
          'Input prompt',
        ),
        name: 'prompt',
        parameterName: 'prompt',
        placeholder: message(
          'shared.studio.nodeConfiguration.humanInput.prompt.placeholder',
          'Please provide the missing input.',
        ),
        required: true,
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.humanInput.variable.label',
          'Response variable',
        ),
        name: 'variable',
        parameterName: 'variable',
        placeholder: message(
          'shared.studio.nodeConfiguration.humanInput.variable.placeholder',
          'human_response',
        ),
      },
    ],
  },
  llm_call: {
    stepType: 'llm_call',
    fields: [
      {
        description: message(
          'shared.studio.nodeConfiguration.llmCall.instruction.description',
          'Prepended to the run input before the role is called.',
        ),
        kind: 'multi-line',
        label: message(
          'shared.studio.nodeConfiguration.llmCall.instruction.label',
          'Instruction',
        ),
        name: 'instruction',
        parameterName: 'prompt',
        placeholder: message(
          'shared.studio.nodeConfiguration.llmCall.instruction.placeholder',
          'Tell the role what this step should do.',
        ),
        required: true,
      },
    ],
  },
  map_reduce: {
    stepType: 'map_reduce',
    fields: [
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.mapReduce.delimiter.label',
          'Delimiter',
        ),
        name: 'delimiter',
        parameterName: 'delimiter',
        placeholder: message(
          'shared.studio.nodeConfiguration.mapReduce.delimiter.placeholder',
          '\\n---\\n',
        ),
      },
      {
        kind: 'select',
        label: message(
          'shared.studio.nodeConfiguration.mapReduce.mapStepType.label',
          'Map step',
        ),
        name: 'mapStepType',
        options: STEP_TYPE_OPTIONS,
        parameterName: 'map_step_type',
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.mapReduce.mapTargetRole.label',
          'Map target role',
        ),
        name: 'mapTargetRole',
        parameterName: 'map_target_role',
        placeholder: message(
          'shared.studio.nodeConfiguration.mapReduce.mapTargetRole.placeholder',
          'mapper',
        ),
      },
      {
        kind: 'select',
        label: message(
          'shared.studio.nodeConfiguration.mapReduce.reduceStepType.label',
          'Reduce step',
        ),
        name: 'reduceStepType',
        options: STEP_TYPE_OPTIONS,
        parameterName: 'reduce_step_type',
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.mapReduce.reduceTargetRole.label',
          'Reduce target role',
        ),
        name: 'reduceTargetRole',
        parameterName: 'reduce_target_role',
        placeholder: message(
          'shared.studio.nodeConfiguration.mapReduce.reduceTargetRole.placeholder',
          'reducer',
        ),
      },
      {
        kind: 'multi-line',
        label: message(
          'shared.studio.nodeConfiguration.mapReduce.reducePromptPrefix.label',
          'Reduce instruction',
        ),
        name: 'reducePromptPrefix',
        parameterName: 'reduce_prompt_prefix',
        placeholder: message(
          'shared.studio.nodeConfiguration.mapReduce.reducePromptPrefix.placeholder',
          'Merge these chunk summaries:',
        ),
      },
    ],
  },
  parallel: {
    stepType: 'parallel',
    fields: [
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.parallel.workers.label',
          'Workers',
        ),
        name: 'workers',
        parameterName: 'workers',
        placeholder: message(
          'shared.studio.nodeConfiguration.parallel.workers.placeholder',
          'agent_a,agent_b,agent_c',
        ),
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.parallel.count.label',
          'Parallel count',
        ),
        name: 'parallelCount',
        parameterName: 'parallel_count',
        placeholder: message(
          'shared.studio.nodeConfiguration.parallel.count.placeholder',
          '3',
        ),
      },
      {
        kind: 'select',
        label: message(
          'shared.studio.nodeConfiguration.parallel.voteStepType.label',
          'Vote step',
        ),
        name: 'voteStepType',
        options: STEP_TYPE_OPTIONS,
        parameterName: 'vote_step_type',
      },
    ],
  },
  race: {
    stepType: 'race',
    fields: [
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.race.workers.label',
          'Workers',
        ),
        name: 'workers',
        parameterName: 'workers',
        placeholder: message(
          'shared.studio.nodeConfiguration.race.workers.placeholder',
          'fast_model,cheap_model',
        ),
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.race.count.label',
          'Winner count',
        ),
        name: 'count',
        parameterName: 'count',
        placeholder: message(
          'shared.studio.nodeConfiguration.race.count.placeholder',
          '2',
        ),
      },
    ],
  },
  reflect: {
    stepType: 'reflect',
    fields: [
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.reflect.maxRounds.label',
          'Max rounds',
        ),
        name: 'maxRounds',
        parameterName: 'max_rounds',
        placeholder: message(
          'shared.studio.nodeConfiguration.reflect.maxRounds.placeholder',
          '3',
        ),
      },
      {
        kind: 'multi-line',
        label: message(
          'shared.studio.nodeConfiguration.reflect.criteria.label',
          'Criteria',
        ),
        name: 'criteria',
        parameterName: 'criteria',
        placeholder: message(
          'shared.studio.nodeConfiguration.reflect.criteria.placeholder',
          'accuracy and conciseness',
        ),
        required: true,
      },
    ],
  },
  retrieve_facts: {
    stepType: 'retrieve_facts',
    fields: [
      {
        kind: 'multi-line',
        label: message(
          'shared.studio.nodeConfiguration.retrieveFacts.query.label',
          'Query',
        ),
        name: 'query',
        parameterName: 'query',
        placeholder: message(
          'shared.studio.nodeConfiguration.retrieveFacts.query.placeholder',
          'What facts should this step retrieve?',
        ),
        required: true,
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.retrieveFacts.topK.label',
          'Top K',
        ),
        name: 'topK',
        parameterName: 'top_k',
        placeholder: message(
          'shared.studio.nodeConfiguration.retrieveFacts.topK.placeholder',
          '3',
        ),
      },
    ],
  },
  switch: {
    stepType: 'switch',
    fields: [
      {
        description: message(
          'shared.studio.nodeConfiguration.switch.on.description',
          'Value matched against branch keys such as bug, feature, or _default.',
        ),
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.switch.on.label',
          'Switch on',
        ),
        name: 'on',
        parameterName: 'on',
        placeholder: message(
          'shared.studio.nodeConfiguration.switch.on.placeholder',
          '$input',
        ),
        required: true,
      },
    ],
  },
  tool_call: {
    stepType: 'tool_call',
    fields: [
      {
        description: message(
          'shared.studio.nodeConfiguration.toolCall.tool.description',
          "Use the exact name from the workflow template or target role's tool setup, for example web_search.",
        ),
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.toolCall.tool.label',
          'Tool name',
        ),
        name: 'tool',
        parameterName: 'tool',
        placeholder: message(
          'shared.studio.nodeConfiguration.toolCall.tool.placeholder',
          'web_search',
        ),
        required: true,
      },
      {
        description: message(
          'shared.studio.nodeConfiguration.toolCall.arguments.description',
          'Use the property names documented by this tool. The value is passed as JSON text.',
        ),
        kind: 'multi-line',
        label: message(
          'shared.studio.nodeConfiguration.toolCall.arguments.label',
          'Arguments JSON',
        ),
        name: 'arguments',
        parameterName: 'arguments',
        placeholder: message(
          'shared.studio.nodeConfiguration.toolCall.arguments.placeholder',
          '\'{\'"query":"$input"\'}\'',
        ),
        required: false,
      },
    ],
  },
  transform: {
    stepType: 'transform',
    fields: [
      {
        kind: 'select',
        label: message(
          'shared.studio.nodeConfiguration.transform.operation.label',
          'Operation',
        ),
        name: 'operation',
        options: [
          {
            label: message(
              'shared.studio.nodeConfiguration.transform.operation.option.trim',
              'Trim whitespace',
            ),
            value: 'trim',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.transform.operation.option.identity',
              'Pass through',
            ),
            value: 'identity',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.transform.operation.option.uppercase',
              'Uppercase',
            ),
            value: 'uppercase',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.transform.operation.option.lowercase',
              'Lowercase',
            ),
            value: 'lowercase',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.transform.operation.option.count',
              'Count lines',
            ),
            value: 'count',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.transform.operation.option.take',
              'Take first lines',
            ),
            value: 'take',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.transform.operation.option.takeLast',
              'Take last lines',
            ),
            value: 'take_last',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.transform.operation.option.split',
              'Split into sections',
            ),
            value: 'split',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.transform.operation.option.join',
              'Join sections',
            ),
            value: 'join',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.transform.operation.option.jsonExtract',
              'Extract JSON',
            ),
            value: 'json_extract',
          },
        ],
        parameterName: 'op',
        required: true,
      },
    ],
  },
  vote: {
    stepType: 'vote',
    fields: [],
  },
  wait_signal: {
    stepType: 'wait_signal',
    fields: [
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.waitSignal.signalName.label',
          'Signal name',
        ),
        name: 'signalName',
        parameterName: 'signal_name',
        placeholder: message(
          'shared.studio.nodeConfiguration.waitSignal.signalName.placeholder',
          'continue',
        ),
        required: true,
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.waitSignal.timeout.label',
          'Timeout ms',
        ),
        name: 'timeoutMs',
        parameterName: 'timeout_ms',
        placeholder: message(
          'shared.studio.nodeConfiguration.waitSignal.timeout.placeholder',
          '60000',
        ),
      },
    ],
  },
  while: {
    stepType: 'while',
    fields: [
      {
        kind: 'select',
        label: message(
          'shared.studio.nodeConfiguration.while.step.label',
          'Loop step',
        ),
        name: 'step',
        options: STEP_TYPE_OPTIONS,
        parameterName: 'step',
      },
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.while.maxIterations.label',
          'Max iterations',
        ),
        name: 'maxIterations',
        parameterName: 'max_iterations',
        placeholder: message(
          'shared.studio.nodeConfiguration.while.maxIterations.placeholder',
          '5',
        ),
      },
      {
        kind: 'multi-line',
        label: message(
          'shared.studio.nodeConfiguration.while.condition.label',
          'Condition',
        ),
        name: 'condition',
        parameterName: 'condition',
        placeholder: message(
          'shared.studio.nodeConfiguration.while.condition.placeholder',
          'lt(iteration, 5)',
        ),
        required: true,
      },
    ],
  },
  workflow_call: {
    stepType: 'workflow_call',
    fields: [
      {
        kind: 'single-line',
        label: message(
          'shared.studio.nodeConfiguration.workflowCall.workflow.label',
          'Workflow',
        ),
        name: 'workflow',
        parameterName: 'workflow',
        placeholder: message(
          'shared.studio.nodeConfiguration.workflowCall.workflow.placeholder',
          'child_workflow',
        ),
        required: true,
      },
      {
        kind: 'select',
        label: message(
          'shared.studio.nodeConfiguration.workflowCall.lifecycle.label',
          'Lifecycle',
        ),
        name: 'lifecycle',
        options: [
          {
            label: message(
              'shared.studio.nodeConfiguration.workflowCall.lifecycle.option.scope',
              'Use scope workflow',
            ),
            value: 'scope',
          },
          {
            label: message(
              'shared.studio.nodeConfiguration.workflowCall.lifecycle.option.inline',
              'Inline call',
            ),
            value: 'inline',
          },
        ],
        parameterName: 'lifecycle',
      },
    ],
  },
  workflow_yaml_validate: {
    stepType: 'workflow_yaml_validate',
    fields: [],
  },
};

function normalizeStepType(value: string): string {
  return value.trim().toLowerCase();
}

function normalizeParameterName(value: string): string {
  return value.trim();
}

function labelFromParameterName(
  parameterName: string,
): ConsoleMessageDescriptor {
  const normalized = normalizeParameterName(parameterName);
  const label = normalized
    .replace(/[_-]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .trim()
    .replace(/\b\w/g, (character) => character.toUpperCase());

  return message(
    `shared.studio.nodeConfiguration.inferred.${normalized || 'parameter'}.label`,
    label || 'Parameter',
  );
}

function normalizeField(field: StudioNodeConfigurationField): NodeConfigField {
  const parameterName = normalizeParameterName(
    field.parameterName || field.path || '',
  );
  return {
    ...field,
    control: field.control ?? field.kind,
    parameterName,
    path: normalizeParameterName(field.path || parameterName),
  };
}

function inferControlFromValue(
  value: unknown,
): StudioNodeConfigurationFieldKind {
  if (typeof value === 'boolean') {
    return 'boolean';
  }

  if (typeof value === 'number') {
    return 'number';
  }

  if (Array.isArray(value)) {
    return 'array';
  }

  if (value && typeof value === 'object') {
    return 'object';
  }

  if (typeof value === 'string' && value.includes('\n')) {
    return 'textarea';
  }

  return 'text';
}

function inferConfigurationFields(
  parameters: Record<string, unknown> | null | undefined,
): readonly NodeConfigField[] {
  const normalizedParameters =
    parameters && typeof parameters === 'object' && !Array.isArray(parameters)
      ? parameters
      : {};

  return Object.entries(normalizedParameters).map(([parameterName, value]) => {
    const control = inferControlFromValue(value);
    return normalizeField({
      control,
      defaultValue: value,
      kind: control,
      label: labelFromParameterName(parameterName),
      name: parameterName,
      parameterName,
      path: parameterName,
    });
  });
}

function buildConfigurationSchema(
  stepType: string,
  parameters?: Record<string, unknown> | null,
): StudioNodeConfigurationSchema {
  const normalizedType = normalizeStepType(stepType);
  const schema = SCHEMAS_BY_STEP_TYPE[normalizedType];

  if (schema) {
    return {
      ...schema,
      fields: schema.fields.map(normalizeField),
    };
  }

  return {
    fields: inferConfigurationFields(parameters),
    stepType: normalizedType || 'step',
  };
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

export function getStudioNodeConfigurationSchema(
  stepType: string,
  parameters?: Record<string, unknown> | null,
): StudioNodeConfigurationSchema {
  return buildConfigurationSchema(stepType, parameters);
}

export function hasStudioNodeConfigurationSchema(stepType: string): boolean {
  return SCHEMAS_BY_STEP_TYPE[normalizeStepType(stepType)] !== undefined;
}

export function shouldShowRawStudioNodeConfiguration(
  stepType: string,
  parameters: Record<string, unknown> | null | undefined,
): boolean {
  const schema = SCHEMAS_BY_STEP_TYPE[normalizeStepType(stepType)];
  if (!schema) {
    return true;
  }

  const coveredParameters = new Set(
    schema.fields.map((field) =>
      resolveStepParameterName(stepType, field.parameterName),
    ),
  );

  return Object.keys(parameters ?? {}).some(
    (parameterName) =>
      !coveredParameters.has(resolveStepParameterName(stepType, parameterName)),
  );
}

export function readStudioNodeConfigurationValues(
  stepType: string,
  parameters: Record<string, unknown> | null | undefined,
  schemaParameters: StudioNodeConfigurationSchemaSource = parameters,
): Record<string, string> {
  const schema = getStudioNodeConfigurationSchema(stepType, schemaParameters);
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
  schemaParameters?: StudioNodeConfigurationSchemaSource,
): Record<string, unknown> {
  const result = applyStudioNodeConfigurationValuesWithValidation(
    stepType,
    parameters,
    values,
    schemaParameters,
  );

  if (!result.valid) {
    throw new Error(result.errors[0] ?? 'Node configuration is invalid.');
  }

  return result.parameters;
}

function readFieldValueFromInput(
  field: StudioNodeConfigurationField,
  value: string,
): { error?: string; include: boolean; value?: unknown } {
  const trimmed = value.trim();
  const control = field.control ?? field.kind;

  if (!trimmed && !field.required) {
    return { include: false };
  }

  if (!trimmed && field.required) {
    return {
      error: `${field.label.defaultMessage} is required.`,
      include: false,
    };
  }

  switch (control) {
    case 'array':
    case 'json':
    case 'object': {
      try {
        const parsed = JSON.parse(value) as unknown;
        if (control === 'array' && !Array.isArray(parsed)) {
          return {
            error: `${field.label.defaultMessage} must be a JSON array.`,
            include: false,
          };
        }
        if (
          control === 'object' &&
          (!parsed || typeof parsed !== 'object' || Array.isArray(parsed))
        ) {
          return {
            error: `${field.label.defaultMessage} must be a JSON object.`,
            include: false,
          };
        }
        return { include: true, value: parsed };
      } catch (error) {
        return {
          error:
            error instanceof Error
              ? `${field.label.defaultMessage}: ${error.message}`
              : `${field.label.defaultMessage} must be valid JSON.`,
          include: false,
        };
      }
    }
    case 'boolean':
      return { include: true, value: trimmed === 'true' };
    case 'number': {
      const parsed = Number(trimmed);
      if (!Number.isFinite(parsed)) {
        return {
          error: `${field.label.defaultMessage} must be a number.`,
          include: false,
        };
      }
      if (field.validation?.integer && !Number.isInteger(parsed)) {
        return {
          error: `${field.label.defaultMessage} must be a whole number.`,
          include: false,
        };
      }
      if (
        field.validation?.min !== undefined &&
        parsed < field.validation.min
      ) {
        return {
          error: `${field.label.defaultMessage} must be at least ${field.validation.min}.`,
          include: false,
        };
      }
      if (
        field.validation?.max !== undefined &&
        parsed > field.validation.max
      ) {
        return {
          error: `${field.label.defaultMessage} must be at most ${field.validation.max}.`,
          include: false,
        };
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
  schemaParameters: StudioNodeConfigurationSchemaSource = parameters,
): {
  readonly errors: readonly string[];
  readonly parameters: Record<string, unknown>;
  readonly valid: boolean;
} {
  const schema = getStudioNodeConfigurationSchema(stepType, schemaParameters);
  const nextParameters = {
    ...(parameters && typeof parameters === 'object' ? parameters : {}),
  };
  const errors: string[] = [];

  for (const field of schema.fields) {
    const value = values[field.name] ?? '';
    const fieldValue = readFieldValueFromInput(field, value);
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
