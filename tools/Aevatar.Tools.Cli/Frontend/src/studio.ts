import { MarkerType, type Edge, type Node, type XYPosition } from '@xyflow/react';

export type StudioView = 'editor' | 'execution';
export type RightPanelTab = 'node' | 'roles' | 'yaml';

export type PrimitiveCategory = {
  key: string;
  label: string;
  color: string;
  items: string[];
};

export const PRIMITIVE_CATEGORIES: PrimitiveCategory[] = [
  {
    key: 'data',
    label: 'Data',
    color: '#3B82F6',
    items: ['transform', 'assign', 'retrieve_facts', 'cache'],
  },
  {
    key: 'control',
    label: 'Control',
    color: '#8B5CF6',
    items: ['guard', 'conditional', 'switch', 'while', 'delay', 'wait_signal', 'checkpoint'],
  },
  {
    key: 'ai',
    label: 'AI',
    color: '#EC4899',
    items: ['llm_call', 'tool_call', 'evaluate', 'reflect'],
  },
  {
    key: 'composition',
    label: 'Composition',
    color: '#F59E0B',
    items: ['foreach', 'parallel', 'race', 'map_reduce', 'workflow_call', 'dynamic_workflow', 'vote'],
  },
  {
    key: 'integration',
    label: 'Integration',
    color: '#10B981',
    items: ['connector_call', 'emit'],
  },
  {
    key: 'human',
    label: 'Human',
    color: '#06B6D4',
    items: ['human_input', 'human_approval'],
  },
  {
    key: 'validation',
    label: 'Validation',
    color: '#64748B',
    items: ['workflow_yaml_validate'],
  },
];

export const ALL_PRIMITIVES = PRIMITIVE_CATEGORIES.flatMap(category => category.items);
export const DEFAULT_NODE_TYPE = 'llm_call';

const ROLE_COMPATIBLE_TYPES = new Set([
  'llm_call',
  'tool_call',
  'evaluate',
  'reflect',
  'while',
  'parallel',
  'race',
  'connector_call',
]);

const DEFAULT_PARAMETERS_BY_TYPE: Record<string, Record<string, unknown>> = {
  transform: { op: 'trim' },
  assign: { target: 'result', value: '$input' },
  retrieve_facts: { query: '', top_k: '3' },
  cache: { cache_key: '$input', ttl_seconds: '600', child_step_type: 'llm_call' },
  guard: { check: 'not_empty', on_fail: 'fail' },
  conditional: { condition: '${eq($input, "ok")}' },
  switch: { on: '$input' },
  while: { step: 'llm_call', max_iterations: '5', condition: '${lt(iteration, 5)}' },
  delay: { duration_ms: '1000' },
  wait_signal: { signal_name: 'continue', timeout_ms: '60000' },
  checkpoint: { name: 'checkpoint_1' },
  llm_call: { prompt_prefix: 'Review the input and produce the next step.' },
  tool_call: { tool: 'web_search' },
  evaluate: { criteria: 'correctness', scale: '1-5', threshold: '4' },
  reflect: { max_rounds: '3', criteria: 'accuracy and conciseness' },
  foreach: { delimiter: '\\n---\\n', sub_step_type: 'llm_call' },
  parallel: { workers: 'assistant', parallel_count: '3', vote_step_type: 'vote' },
  race: { workers: 'assistant', count: '2' },
  map_reduce: {
    delimiter: '\\n---\\n',
    map_step_type: 'llm_call',
    reduce_step_type: 'llm_call',
  },
  workflow_call: { workflow: 'child_workflow', lifecycle: 'scope' },
  dynamic_workflow: { original_input: '$input' },
  vote: {},
  connector_call: {
    connector: '',
    operation: '',
    path: '',
    method: 'POST',
    timeout_ms: '10000',
    retry: '0',
    on_error: 'fail',
  },
  emit: { event_type: 'workflow.completed', payload: '$input' },
  human_input: { prompt: 'Please provide the missing input.', variable: 'human_response' },
  human_approval: { prompt: 'Approve this step?', on_reject: 'fail' },
  workflow_yaml_validate: {},
};

export type RoleState = {
  key: string;
  id: string;
  name: string;
  systemPrompt: string;
  provider: string;
  model: string;
  connectorsText: string;
};

export type StudioNodeData = {
  label: string;
  stepId: string;
  stepType: string;
  targetRole: string;
  parameters: Record<string, unknown>;
  executionStatus?: 'idle' | 'active' | 'waiting' | 'completed' | 'failed';
  executionFocused?: boolean;
};

export type StudioEdgeData = {
  kind: 'next' | 'branch';
  branchLabel?: string;
  executionState?: 'idle' | 'active' | 'completed';
};

export type WorkflowMetaState = {
  workflowId: string | null;
  directoryId: string | null;
  fileName: string;
  filePath: string;
  name: string;
  description: string;
  closedWorldMode: boolean;
  yaml: string;
  findings: ValidationFinding[];
  dirty: boolean;
  lastSavedAt: string | null;
};

export type ValidationFinding = {
  level?: string | number;
  message: string;
  path?: string | null;
  code?: string | null;
};

export type ConnectorState = {
  key: string;
  name: string;
  type: 'http' | 'cli' | 'mcp';
  enabled: boolean;
  timeoutMs: string;
  retry: string;
  http: {
    baseUrl: string;
    allowedMethods: string[];
    allowedPaths: string[];
    allowedInputKeys: string[];
    defaultHeaders: Record<string, string>;
  };
  cli: {
    command: string;
    fixedArguments: string[];
    allowedOperations: string[];
    allowedInputKeys: string[];
    workingDirectory: string;
    environment: Record<string, string>;
  };
  mcp: {
    serverName: string;
    command: string;
    arguments: string[];
    environment: Record<string, string>;
    defaultTool: string;
    allowedTools: string[];
    allowedInputKeys: string[];
  };
};

export type ExecutionLogItem = {
  tone: 'started' | 'completed' | 'failed' | 'run' | 'pending';
  title: string;
  meta: string;
  copy: string;
  timestamp: string;
  stepId: string | null;
  interaction: ExecutionInteractionState | null;
};

export type StepExecutionState = {
  stepId: string;
  status: 'idle' | 'active' | 'waiting' | 'completed' | 'failed';
  stepType: string;
  targetRole: string;
  startedAt: string | null;
  completedAt: string | null;
  success: boolean | null;
  error: string;
  nextStepId: string;
  branchKey: string;
};

export type ExecutionInteractionState = {
  kind: 'human_input' | 'human_approval';
  runId: string;
  stepId: string;
  prompt: string;
  timeoutSeconds: number | null;
  variableName: string;
};

export type ExecutionTrace = {
  stepStates: Map<string, StepExecutionState>;
  traversedEdges: Set<string>;
  logs: ExecutionLogItem[];
  latestStepId: string | null;
  defaultLogIndex: number | null;
};

export function createEmptyWorkflowMeta(): WorkflowMetaState {
  return {
    workflowId: null,
    directoryId: null,
    fileName: '',
    filePath: '',
    name: 'draft',
    description: '',
    closedWorldMode: false,
    yaml: '',
    findings: [],
    dirty: false,
    lastSavedAt: null,
  };
}

export function createRoleState(index = 1, overrides: Partial<RoleState> = {}): RoleState {
  return {
    key: overrides.key || `role_${crypto.randomUUID()}`,
    id: overrides.id ?? (index === 1 ? 'assistant' : `role_${index}`),
    name: overrides.name ?? (index === 1 ? 'Assistant' : `Role ${index}`),
    systemPrompt: overrides.systemPrompt ?? '',
    provider: overrides.provider ?? '',
    model: overrides.model ?? '',
    connectorsText: overrides.connectorsText ?? '',
  };
}

export function createDefaultRoles(): RoleState[] {
  return [createRoleState(1)];
}

export function toRoleState(raw: any, index = 1): RoleState {
  return createRoleState(index, {
    id: raw?.id || '',
    name: raw?.name || raw?.id || '',
    systemPrompt: raw?.systemPrompt || raw?.system_prompt || '',
    provider: raw?.provider || '',
    model: raw?.model || '',
    connectorsText: Array.isArray(raw?.connectors) ? raw.connectors.join('\n') : (raw?.connectorsText || ''),
  });
}

export function toRolePayload(role: RoleState) {
  return {
    id: role.id.trim(),
    name: (role.name || role.id).trim(),
    systemPrompt: role.systemPrompt || '',
    provider: role.provider.trim(),
    model: role.model.trim(),
    connectors: splitLines(role.connectorsText),
  };
}

export function createEmptyConnector(type: ConnectorState['type'] = 'http', name = ''): ConnectorState {
  return {
    key: `connector_${crypto.randomUUID()}`,
    name,
    type,
    enabled: true,
    timeoutMs: '30000',
    retry: '0',
    http: {
      baseUrl: '',
      allowedMethods: ['POST'],
      allowedPaths: ['/'],
      allowedInputKeys: [],
      defaultHeaders: {},
    },
    cli: {
      command: '',
      fixedArguments: [],
      allowedOperations: [],
      allowedInputKeys: [],
      workingDirectory: '',
      environment: {},
    },
    mcp: {
      serverName: '',
      command: '',
      arguments: [],
      environment: {},
      defaultTool: '',
      allowedTools: [],
      allowedInputKeys: [],
    },
  };
}

export function toConnectorState(raw: any): ConnectorState {
  return {
    key: `connector_${crypto.randomUUID()}`,
    name: raw?.name || '',
    type: (raw?.type || 'http') as ConnectorState['type'],
    enabled: raw?.enabled !== false,
    timeoutMs: String(raw?.timeoutMs ?? 30000),
    retry: String(raw?.retry ?? 0),
    http: {
      baseUrl: raw?.http?.baseUrl || '',
      allowedMethods: Array.isArray(raw?.http?.allowedMethods) ? [...raw.http.allowedMethods] : ['POST'],
      allowedPaths: Array.isArray(raw?.http?.allowedPaths) ? [...raw.http.allowedPaths] : ['/'],
      allowedInputKeys: Array.isArray(raw?.http?.allowedInputKeys) ? [...raw.http.allowedInputKeys] : [],
      defaultHeaders: { ...(raw?.http?.defaultHeaders || {}) },
    },
    cli: {
      command: raw?.cli?.command || '',
      fixedArguments: Array.isArray(raw?.cli?.fixedArguments) ? [...raw.cli.fixedArguments] : [],
      allowedOperations: Array.isArray(raw?.cli?.allowedOperations) ? [...raw.cli.allowedOperations] : [],
      allowedInputKeys: Array.isArray(raw?.cli?.allowedInputKeys) ? [...raw.cli.allowedInputKeys] : [],
      workingDirectory: raw?.cli?.workingDirectory || '',
      environment: { ...(raw?.cli?.environment || {}) },
    },
    mcp: {
      serverName: raw?.mcp?.serverName || '',
      command: raw?.mcp?.command || '',
      arguments: Array.isArray(raw?.mcp?.arguments) ? [...raw.mcp.arguments] : [],
      environment: { ...(raw?.mcp?.environment || {}) },
      defaultTool: raw?.mcp?.defaultTool || '',
      allowedTools: Array.isArray(raw?.mcp?.allowedTools) ? [...raw.mcp.allowedTools] : [],
      allowedInputKeys: Array.isArray(raw?.mcp?.allowedInputKeys) ? [...raw.mcp.allowedInputKeys] : [],
    },
  };
}

export function toConnectorPayload(connector: ConnectorState) {
  return {
    name: connector.name.trim(),
    type: connector.type,
    enabled: connector.enabled,
    timeoutMs: normalizeInteger(connector.timeoutMs, 30000),
    retry: normalizeInteger(connector.retry, 0),
    http: {
      baseUrl: connector.http.baseUrl.trim(),
      allowedMethods: connector.http.allowedMethods.map(item => item.trim().toUpperCase()).filter(Boolean),
      allowedPaths: connector.http.allowedPaths.map(item => item.trim()).filter(Boolean),
      allowedInputKeys: connector.http.allowedInputKeys.map(item => item.trim()).filter(Boolean),
      defaultHeaders: connector.http.defaultHeaders,
    },
    cli: {
      command: connector.cli.command.trim(),
      fixedArguments: connector.cli.fixedArguments.map(item => item.trim()).filter(Boolean),
      allowedOperations: connector.cli.allowedOperations.map(item => item.trim()).filter(Boolean),
      allowedInputKeys: connector.cli.allowedInputKeys.map(item => item.trim()).filter(Boolean),
      workingDirectory: connector.cli.workingDirectory.trim(),
      environment: connector.cli.environment,
    },
    mcp: {
      serverName: connector.mcp.serverName.trim(),
      command: connector.mcp.command.trim(),
      arguments: connector.mcp.arguments.map(item => item.trim()).filter(Boolean),
      environment: connector.mcp.environment,
      defaultTool: connector.mcp.defaultTool.trim(),
      allowedTools: connector.mcp.allowedTools.map(item => item.trim()).filter(Boolean),
      allowedInputKeys: connector.mcp.allowedInputKeys.map(item => item.trim()).filter(Boolean),
    },
  };
}

export function createUniqueConnectorName(connectors: ConnectorState[], type: ConnectorState['type']) {
  const used = new Set(connectors.map(connector => connector.name));
  const base = `${type}_connector`;
  let index = 1;
  let candidate = base;

  while (used.has(candidate)) {
    index += 1;
    candidate = `${base}_${index}`;
  }

  return candidate;
}

export function splitLines(value: string) {
  return String(value || '')
    .split(/\r?\n|,/)
    .map(item => item.trim())
    .filter(Boolean);
}

export function formatMapText(values: Record<string, string>) {
  return Object.entries(values || {})
    .map(([key, value]) => `${key}: ${value}`)
    .join('\n');
}

export function parseMapText(rawValue: string) {
  return String(rawValue || '')
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(Boolean)
    .reduce<Record<string, string>>((result, line) => {
      const separatorIndex = line.includes(':') ? line.indexOf(':') : line.indexOf('=');
      if (separatorIndex <= 0) {
        return result;
      }

      const key = line.slice(0, separatorIndex).trim();
      const value = line.slice(separatorIndex + 1).trim();
      if (key) {
        result[key] = value;
      }
      return result;
    }, {});
}

export function getCategoryForType(type: string) {
  return PRIMITIVE_CATEGORIES.find(category => category.items.includes(type))
    || {
      key: 'custom',
      label: 'Custom',
      color: '#6B7280',
      items: [],
    };
}

export function createNode(
  stepType: string,
  position: XYPosition,
  existingNodes: Array<Node<StudioNodeData>>,
  roles: RoleState[],
  connectors: ConnectorState[],
  overrides: Partial<StudioNodeData> = {},
) {
  const parameters = structuredClone(DEFAULT_PARAMETERS_BY_TYPE[stepType] || {});
  const connectorName = stepType === 'connector_call'
    ? getPreferredConnector(connectors)?.name || ''
    : '';

  if (connectorName) {
    applyConnectorDefaults(parameters, connectorName, connectors);
  }

  const stepId = createUniqueStepId(stepType, existingNodes);
  const targetRole = ROLE_COMPATIBLE_TYPES.has(stepType)
    ? overrides.targetRole ?? roles[0]?.id ?? ''
    : overrides.targetRole ?? '';

  return {
    id: `node_${crypto.randomUUID()}`,
    type: 'aevatarNode',
    position,
    data: {
      label: overrides.label || stepId,
      stepId,
      stepType,
      targetRole,
      parameters: {
        ...parameters,
        ...structuredClone(overrides.parameters || {}),
      },
    },
  } as Node<StudioNodeData>;
}

export function getDefaultParametersForType(stepType: string) {
  return structuredClone(DEFAULT_PARAMETERS_BY_TYPE[stepType] || {});
}

export function supportsRole(stepType: string) {
  return ROLE_COMPATIBLE_TYPES.has(stepType);
}

export function createUniqueStepId(stepType: string, existingNodes: Array<Node<StudioNodeData>>) {
  const base = stepType.replace(/[^a-z0-9]+/gi, '_').toLowerCase();
  const used = new Set(existingNodes.map(node => node.data.stepId));
  let index = existingNodes.length + 1;
  let candidate = `${base}_${index}`;

  while (used.has(candidate)) {
    index += 1;
    candidate = `${base}_${index}`;
  }

  return candidate;
}

export function applyConnectorDefaults(
  parameters: Record<string, unknown>,
  connectorName: string,
  connectors: ConnectorState[],
) {
  if (!connectorName) {
    return parameters;
  }

  const connector = connectors.find(item => item.name === connectorName);
  parameters.connector = connectorName;
  if (!connector) {
    return parameters;
  }

  if (connector.type === 'http') {
    if (!parameters.method || !connector.http.allowedMethods.includes(String(parameters.method).toUpperCase())) {
      parameters.method = connector.http.allowedMethods[0] || 'POST';
    }
    if (!parameters.path && connector.http.allowedPaths.length > 0) {
      parameters.path = connector.http.allowedPaths[0];
    }
    return parameters;
  }

  if (connector.type === 'cli') {
    if (!parameters.operation && connector.cli.allowedOperations.length > 0) {
      parameters.operation = connector.cli.allowedOperations[0];
    }
    return parameters;
  }

  if (!parameters.operation) {
    parameters.operation = connector.mcp.defaultTool || connector.mcp.allowedTools[0] || '';
  }
  return parameters;
}

export function getPreferredConnector(connectors: ConnectorState[]) {
  return connectors.find(connector => connector.enabled) || connectors[0] || null;
}

export function buildGraphFromWorkflow(
  workflow: any,
  layout?: any,
  rolesFallback: RoleState[] = createDefaultRoles(),
) {
  const document = workflow?.document || workflow?.rootWorkflow || workflow;
  const roleStates = Array.isArray(document?.roles) && document.roles.length > 0
    ? document.roles.map((role: any, index: number) => createRoleState(index + 1, {
      id: role.id || '',
      name: role.name || role.id || '',
      systemPrompt: role.systemPrompt || '',
      provider: role.provider || '',
      model: role.model || '',
      connectorsText: Array.isArray(role.connectors) ? role.connectors.join('\n') : '',
    }))
    : rolesFallback;

  const steps = Array.isArray(document?.steps) ? document.steps : [];
  const savedLayoutPositions = shouldUseSavedLayout(layout) ? layout?.nodePositions || {} : {};
  const autoLayoutPositions = buildAutoLayoutPositions(steps);
  const nodes = steps.map((step: any, index: number) => {
    const position = savedLayoutPositions[step.id] || autoLayoutPositions[step.id];
    return {
      id: `node_${crypto.randomUUID()}`,
      type: 'aevatarNode',
      position: {
        x: Number.isFinite(position?.x) ? position.x : 220 + index * 300,
        y: Number.isFinite(position?.y) ? position.y : 180,
      },
      data: {
        label: step.id,
        stepId: step.id,
        stepType: step.type || step.originalType || DEFAULT_NODE_TYPE,
        targetRole: step.targetRole || step.target_role || '',
        parameters: normalizeParameters(step.parameters),
      },
    } as Node<StudioNodeData>;
  });

  const nodeByStepId = new Map(nodes.map((node: Node<StudioNodeData>) => [node.data.stepId, node]));
  const edges: Array<Edge<StudioEdgeData>> = [];
  steps.forEach((step: any, index: number) => {
    const sourceNode = nodeByStepId.get(step.id) as Node<StudioNodeData> | undefined;
    if (step.next && sourceNode && nodeByStepId.has(step.next)) {
      const targetNode = nodeByStepId.get(step.next) as Node<StudioNodeData>;
      edges.push(createEdge(sourceNode.id, targetNode.id));
    } else if (!step.next && (!step.branches || Object.keys(step.branches).length === 0) && sourceNode && index < steps.length - 1) {
      const nextStepId = steps[index + 1]?.id;
      if (typeof nextStepId === 'string' && nodeByStepId.has(nextStepId)) {
        const targetNode = nodeByStepId.get(nextStepId) as Node<StudioNodeData>;
        edges.push(createEdge(sourceNode.id, targetNode.id));
      }
    }

    Object.entries(step.branches || {}).forEach(([label, targetStepId]) => {
      if (typeof targetStepId === 'string' && sourceNode && nodeByStepId.has(targetStepId)) {
        const targetNode = nodeByStepId.get(targetStepId) as Node<StudioNodeData>;
        edges.push(createEdge(sourceNode.id, targetNode.id, label));
      }
    });
  });

  return {
    roles: roleStates,
    nodes,
    edges,
  };
}

export function buildWorkflowDocument(
  meta: WorkflowMetaState,
  roles: RoleState[],
  nodes: Array<Node<StudioNodeData>>,
  edges: Array<Edge<StudioEdgeData>>,
) {
  const outgoing = new Map<string, Array<Edge<StudioEdgeData>>>();
  edges.forEach(edge => {
    const current = outgoing.get(edge.source) || [];
    current.push(edge);
    outgoing.set(edge.source, current);
  });

  return {
    name: meta.name.trim() || 'draft',
    description: meta.description.trim(),
    configuration: {
      closedWorldMode: meta.closedWorldMode,
    },
    roles: roles
      .filter(role => role.id.trim())
      .map(role => ({
        id: role.id.trim(),
        name: role.name.trim() || role.id.trim(),
        systemPrompt: role.systemPrompt || '',
        provider: role.provider.trim() || null,
        model: role.model.trim() || null,
        connectors: splitLines(role.connectorsText),
      })),
    steps: nodes.map(node => {
      const nodeEdges = outgoing.get(node.id) || [];
      const nextEdge = nodeEdges.find(edge => edge.data?.kind === 'next');
      const branchEdges = nodeEdges.filter(edge => edge.data?.kind === 'branch');
      return {
        id: node.data.stepId,
        type: node.data.stepType,
        originalType: node.data.stepType,
        targetRole: node.data.targetRole || null,
        usedRoleAlias: false,
        parameters: cleanParameters(node.data.parameters),
        next: nextEdge ? nodes.find(candidate => candidate.id === nextEdge.target)?.data.stepId || null : null,
        branches: Object.fromEntries(
          branchEdges
            .map(edge => [
              edge.data?.branchLabel || '_default',
              nodes.find(candidate => candidate.id === edge.target)?.data.stepId || null,
            ])
            .filter(([, stepId]) => Boolean(stepId)),
        ),
        children: [],
        importedFromChildren: false,
        retry: null,
        onError: null,
        timeoutMs: null,
      };
    }),
  };
}

export function buildLayoutDocument(meta: WorkflowMetaState, nodes: Array<Node<StudioNodeData>>) {
  return {
    nodePositions: Object.fromEntries(
      nodes.map(node => [
        node.data.stepId,
        {
          x: node.position.x,
          y: node.position.y,
        },
      ]),
    ),
    viewport: {
      x: 0,
      y: 0,
      zoom: 1,
    },
    mode: 'manual',
    layoutVersion: 2,
    groups: {},
    collapsed: [],
    entryWorkflow: meta.name.trim() || 'draft',
  };
}

export function createEdge(sourceId: string, targetId: string, branchLabel?: string) {
  const branch = Boolean(branchLabel);
  const color = branch ? '#8B5CF6' : '#2F6FEC';
  return {
    id: `edge_${sourceId}_${targetId}_${branchLabel || 'next'}`,
    source: sourceId,
    target: targetId,
    type: 'smoothstep',
    label: branchLabel,
    animated: false,
    data: {
      kind: branch ? 'branch' : 'next',
      branchLabel,
    },
    style: {
      stroke: color,
      strokeWidth: 2.5,
    },
    markerEnd: {
      type: MarkerType.ArrowClosed,
      width: 11,
      height: 11,
      color,
    },
    zIndex: 4,
    labelStyle: {
      fill: '#6B7280',
      fontSize: 12,
    },
  } as Edge<StudioEdgeData>;
}

export function getNextConditionalBranchLabel(
  sourceNodeId: string,
  edges: Array<Edge<StudioEdgeData>>,
) {
  const branchLabels = new Set(
    edges
      .filter(edge => edge.source === sourceNodeId && edge.data?.kind === 'branch')
      .map(edge => edge.data?.branchLabel),
  );

  if (!branchLabels.has('true')) {
    return 'true';
  }

  if (!branchLabels.has('false')) {
    return 'false';
  }

  return 'true';
}

export function parseParameterInput(rawValue: string) {
  const trimmed = rawValue.trim();

  if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
    try {
      return JSON.parse(trimmed);
    } catch {
      return rawValue;
    }
  }

  if (trimmed === 'true') {
    return true;
  }

  if (trimmed === 'false') {
    return false;
  }

  if (trimmed === 'null') {
    return null;
  }

  return rawValue;
}

export function formatParameterValue(value: unknown) {
  if (value === null || value === undefined) {
    return '';
  }

  if (typeof value === 'string') {
    return value;
  }

  if (typeof value === 'boolean' || typeof value === 'number') {
    return String(value);
  }

  return JSON.stringify(value, null, 2);
}

export function buildExecutionTrace(detail: any): ExecutionTrace | null {
  if (!detail) {
    return null;
  }

  const stepStates = new Map<string, StepExecutionState>();
  const traversedEdges = new Set<string>();
  const logs: ExecutionLogItem[] = [];
  let latestStepId: string | null = null;

  for (const frame of detail.frames || []) {
    const parsed = safeJsonParse(frame.payload);
    const timestamp = frame.receivedAtUtc;
    if (!parsed) {
      continue;
    }

    const customName = parsed.custom?.name || '';
    const customPayload = parsed.custom?.payload || null;

    if (customName === 'aevatar.step.request') {
      const stepId = customPayload?.stepId || parsed.stepStarted?.stepName;
      if (!stepId) {
        continue;
      }

      const stepState = getOrCreateExecutionStepState(stepStates, stepId);
      stepState.status = 'active';
      stepState.stepType = customPayload?.stepType || stepState.stepType || '';
      stepState.targetRole = customPayload?.targetRole || stepState.targetRole || '';
      stepState.startedAt = timestamp;
      latestStepId = stepId;
      logs.push({
        tone: 'started',
        title: `${stepId} started`,
        meta: [customPayload?.stepType, customPayload?.targetRole].filter(Boolean).join(' · '),
        copy: buildExecutionLogCopy(customPayload?.input),
        timestamp,
        stepId,
        interaction: null,
      });
      continue;
    }

    if (customName === 'aevatar.human_input.request') {
      const stepId = customPayload?.stepId;
      const runId = customPayload?.runId;
      const interactionKind = normalizeExecutionInteractionKind(customPayload?.suspensionType);
      if (!stepId || !runId || !interactionKind) {
        continue;
      }

      const stepState = getOrCreateExecutionStepState(stepStates, stepId);
      stepState.status = 'waiting';
      stepState.stepType = stepState.stepType || interactionKind;
      latestStepId = stepId;

      const timeoutSeconds = normalizeExecutionTimeout(customPayload?.timeoutSeconds);
      const interaction: ExecutionInteractionState = {
        kind: interactionKind,
        runId,
        stepId,
        prompt: String(customPayload?.prompt || '').trim(),
        timeoutSeconds,
        variableName: String(customPayload?.variableName || '').trim(),
      };

      logs.push({
        tone: 'pending',
        title: interactionKind === 'human_approval'
          ? `${stepId} waiting for approval`
          : `${stepId} waiting for input`,
        meta: [
          interactionKind === 'human_approval' ? 'human approval' : 'human input',
          interaction.variableName ? `variable ${interaction.variableName}` : null,
          timeoutSeconds ? `timeout ${timeoutSeconds}s` : null,
        ].filter(Boolean).join(' · '),
        copy: buildExecutionLogCopy(interaction.prompt),
        timestamp,
        stepId,
        interaction,
      });
      continue;
    }

    if (customName === 'aevatar.step.completed') {
      const stepId = customPayload?.stepId || parsed.stepFinished?.stepName;
      if (!stepId) {
        continue;
      }

      const stepState = getOrCreateExecutionStepState(stepStates, stepId);
      stepState.status = customPayload?.success === false ? 'failed' : 'completed';
      stepState.completedAt = timestamp;
      stepState.success = customPayload?.success !== false;
      stepState.error = customPayload?.error || '';
      stepState.nextStepId = customPayload?.nextStepId || '';
      stepState.branchKey = customPayload?.branchKey || '';

      if (customPayload?.nextStepId) {
        traversedEdges.add(`${stepId}->${customPayload.nextStepId}`);
      }

      latestStepId = stepId;
      logs.push({
        tone: customPayload?.success === false ? 'failed' : 'completed',
        title: `${stepId} ${customPayload?.success === false ? 'failed' : 'completed'}`,
        meta: [
          stepState.stepType,
          customPayload?.branchKey ? `branch ${customPayload.branchKey}` : null,
          customPayload?.nextStepId ? `next ${customPayload.nextStepId}` : null,
        ].filter(Boolean).join(' · '),
        copy: buildExecutionLogCopy(customPayload?.error || customPayload?.output),
        timestamp,
        stepId,
        interaction: null,
      });
      continue;
    }

    if (customName === 'studio.human.resume') {
      const stepId = customPayload?.stepId;
      if (!stepId) {
        continue;
      }

      const stepState = getOrCreateExecutionStepState(stepStates, stepId);
      stepState.status = 'active';
      latestStepId = stepId;
      const interactionKind = normalizeExecutionInteractionKind(customPayload?.suspensionType);
      const approved = customPayload?.approved !== false;
      logs.push({
        tone: 'run',
        title: interactionKind === 'human_approval'
          ? `${stepId} ${approved ? 'approved' : 'rejected'}`
          : `${stepId} input submitted`,
        meta: interactionKind === 'human_approval'
          ? `human approval · ${approved ? 'approved' : 'rejected'}`
          : 'human input submitted',
        copy: buildExecutionLogCopy(customPayload?.userInput),
        timestamp,
        stepId,
        interaction: null,
      });
      continue;
    }

    if (parsed.runError?.message) {
      logs.push({
        tone: 'failed',
        title: 'Run failed',
        meta: parsed.runError.code || '',
        copy: parsed.runError.message,
        timestamp,
        stepId: latestStepId,
        interaction: null,
      });
      continue;
    }

    if (parsed.runFinished) {
      logs.push({
        tone: 'run',
        title: 'Run finished',
        meta: '',
        copy: '',
        timestamp,
        stepId: latestStepId,
        interaction: null,
      });
      continue;
    }

    if (customName === 'aevatar.run.context') {
      logs.push({
        tone: 'run',
        title: 'Run started',
        meta: customPayload?.workflowName || detail.workflowName || '',
        copy: '',
        timestamp,
        stepId: null,
        interaction: null,
      });
    }
  }

  let defaultLogIndex: number | null = null;
  for (let index = logs.length - 1; index >= 0; index -= 1) {
    const stepId = logs[index].stepId;
    if (logs[index].interaction && stepId && stepStates.get(stepId)?.status === 'waiting') {
      defaultLogIndex = index;
      break;
    }
  }

  for (let index = logs.length - 1; index >= 0 && defaultLogIndex === null; index -= 1) {
    if (logs[index].stepId) {
      defaultLogIndex = index;
      break;
    }
  }

  return {
    stepStates,
    traversedEdges,
    logs,
    latestStepId,
    defaultLogIndex,
  };
}

export function getExecutionFocusStepId(trace: ExecutionTrace | null, activeLogIndex: number | null) {
  if (!trace) {
    return null;
  }

  const activeLog = Number.isInteger(activeLogIndex)
    ? trace.logs[activeLogIndex as number]
    : null;

  return activeLog?.stepId || trace.latestStepId || null;
}

export function decorateNodesForExecution(
  nodes: Array<Node<StudioNodeData>>,
  trace: ExecutionTrace | null,
  activeLogIndex: number | null,
) {
  const focusedStepId = getExecutionFocusStepId(trace, activeLogIndex);

  return nodes.map(node => {
    const stepState = trace?.stepStates.get(node.data.stepId);
    return {
      ...node,
      draggable: false,
      selectable: true,
      data: {
        ...node.data,
        executionStatus: stepState?.status || 'idle',
        executionFocused: focusedStepId === node.data.stepId,
      },
    };
  });
}

export function decorateEdgesForExecution(
  edges: Array<Edge<StudioEdgeData>>,
  nodes: Array<Node<StudioNodeData>>,
  trace: ExecutionTrace | null,
  activeLogIndex: number | null,
) {
  const focusedStepId = getExecutionFocusStepId(trace, activeLogIndex);
  const stepIdByNodeId = new Map(nodes.map(node => [node.id, node.data.stepId]));

  return edges.map(edge => {
    const sourceStepId = stepIdByNodeId.get(edge.source);
    const targetStepId = stepIdByNodeId.get(edge.target);
    const traversed = sourceStepId && targetStepId
      ? trace?.traversedEdges.has(`${sourceStepId}->${targetStepId}`)
      : false;
    const isFocused = focusedStepId && (sourceStepId === focusedStepId || targetStepId === focusedStepId);

    const color = isFocused
      ? '#2F6FEC'
      : traversed
        ? '#22C55E'
        : edge.data?.kind === 'branch'
          ? '#8B5CF6'
          : '#94A3B8';

    return {
      ...edge,
      type: edge.type || 'smoothstep',
      animated: Boolean(isFocused),
      style: {
        stroke: color,
        strokeWidth: isFocused ? 2.8 : 2.5,
      },
      markerEnd: {
        type: MarkerType.ArrowClosed,
        width: 11,
        height: 11,
        color,
      },
      zIndex: 4,
    };
  });
}

export function findExecutionLogIndexForStep(trace: ExecutionTrace | null, stepId: string) {
  if (!trace?.logs?.length || !stepId) {
    return null;
  }

  for (let index = trace.logs.length - 1; index >= 0; index -= 1) {
    if (trace.logs[index].stepId === stepId) {
      return index;
    }
  }

  return null;
}

export function toFindingLevel(level: string | number | undefined) {
  if (typeof level === 'string') {
    return level.toLowerCase();
  }

  return Number(level) === 2 ? 'error' : 'warning';
}

function normalizeParameters(parameters: Record<string, unknown> | undefined | null) {
  return Object.fromEntries(
    Object.entries(parameters || {}).map(([key, value]) => [key, cloneJsonValue(value)]),
  );
}

function cleanParameters(parameters: Record<string, unknown>) {
  return Object.fromEntries(
    Object.entries(parameters || {}).filter(([key]) => key.trim()),
  );
}

function cloneJsonValue<T>(value: T): T {
  if (value === null || value === undefined) {
    return value;
  }

  return structuredClone(value);
}

function normalizeInteger(value: string, fallbackValue: number) {
  const parsed = parseInt(String(value || '').trim(), 10);
  return Number.isFinite(parsed) ? parsed : fallbackValue;
}

function getOrCreateExecutionStepState(stepStates: Map<string, StepExecutionState>, stepId: string) {
  if (!stepStates.has(stepId)) {
    stepStates.set(stepId, {
      stepId,
      status: 'idle',
      stepType: '',
      targetRole: '',
      startedAt: null,
      completedAt: null,
      success: null,
      error: '',
      nextStepId: '',
      branchKey: '',
    });
  }

  return stepStates.get(stepId)!;
}

function safeJsonParse(value: string) {
  try {
    return JSON.parse(value);
  } catch {
    return null;
  }
}

function buildExecutionLogCopy(value: unknown) {
  const text = String(value || '').trim();
  if (!text) {
    return '';
  }

  return text.length > 180 ? `${text.slice(0, 177)}...` : text;
}

function normalizeExecutionInteractionKind(value: unknown): ExecutionInteractionState['kind'] | null {
  const text = String(value || '').trim().toLowerCase();
  if (text === 'human_input' || text === 'human_approval') {
    return text;
  }

  return null;
}

function normalizeExecutionTimeout(value: unknown) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

function shouldUseSavedLayout(layout: any) {
  return layout?.mode === 'manual' && layout?.nodePositions && typeof layout.nodePositions === 'object';
}

function buildAutoLayoutPositions(steps: any[]) {
  const validStepIds = new Set(
    steps
      .map(step => String(step?.id || '').trim())
      .filter(Boolean),
  );
  if (validStepIds.size === 0) {
    return {};
  }

  const outgoing = new Map<string, string[]>();
  const incomingCount = new Map<string, number>();
  for (const stepId of validStepIds) {
    outgoing.set(stepId, []);
    incomingCount.set(stepId, 0);
  }

  steps.forEach((step: any, index: number) => {
    const sourceId = String(step?.id || '').trim();
    if (!sourceId || !validStepIds.has(sourceId)) {
      return;
    }

    const nextTargets: string[] = [];
    const explicitNext = String(step?.next || '').trim();
    if (explicitNext && validStepIds.has(explicitNext)) {
      nextTargets.push(explicitNext);
    } else if (!step?.next && (!step?.branches || Object.keys(step.branches).length === 0) && index < steps.length - 1) {
      const fallbackNext = String(steps[index + 1]?.id || '').trim();
      if (fallbackNext && validStepIds.has(fallbackNext)) {
        nextTargets.push(fallbackNext);
      }
    }

    const branchTargets = Object.entries(step?.branches || {})
      .sort(([left], [right]) => compareBranchLabels(left, right))
      .map(([, targetStepId]) => String(targetStepId || '').trim())
      .filter(targetStepId => targetStepId && validStepIds.has(targetStepId));

    for (const targetStepId of [...nextTargets, ...branchTargets]) {
      const currentTargets = outgoing.get(sourceId) || [];
      if (!currentTargets.includes(targetStepId)) {
        currentTargets.push(targetStepId);
        outgoing.set(sourceId, currentTargets);
        incomingCount.set(targetStepId, (incomingCount.get(targetStepId) || 0) + 1);
      }
    }
  });

  const treeChildren = new Map<string, string[]>();
  const depths = new Map<string, number>();
  const visited = new Set<string>();
  const rootOrder = new Set<string>();
  const firstStepId = String(steps[0]?.id || '').trim();
  if (firstStepId) {
    rootOrder.add(firstStepId);
  }

  for (const step of steps) {
    const stepId = String(step?.id || '').trim();
    if (stepId && (incomingCount.get(stepId) || 0) === 0) {
      rootOrder.add(stepId);
    }
  }

  for (const step of steps) {
    const stepId = String(step?.id || '').trim();
    if (stepId) {
      rootOrder.add(stepId);
    }
  }

  function visit(stepId: string, depth: number) {
    if (visited.has(stepId)) {
      return;
    }

    visited.add(stepId);
    depths.set(stepId, depth);
    const children = outgoing.get(stepId) || [];
    const treeTargets: string[] = [];
    for (const childId of children) {
      if (visited.has(childId)) {
        continue;
      }

      treeTargets.push(childId);
      visit(childId, depth + 1);
    }
    treeChildren.set(stepId, treeTargets);
  }

  for (const rootId of rootOrder) {
    if (!visited.has(rootId)) {
      visit(rootId, 0);
    }
  }

  const subtreeSize = new Map<string, number>();
  function measure(stepId: string): number {
    const children = treeChildren.get(stepId) || [];
    if (children.length === 0) {
      subtreeSize.set(stepId, 1);
      return 1;
    }

    const total = children.reduce((sum, childId) => sum + measure(childId), 0);
    const size = Math.max(1, total);
    subtreeSize.set(stepId, size);
    return size;
  }

  for (const rootId of rootOrder) {
    if (depths.has(rootId) && !subtreeSize.has(rootId)) {
      measure(rootId);
    }
  }

  const positions: Record<string, { x: number; y: number }> = {};
  let globalRow = 0;

  function place(stepId: string, startRow: number) {
    const children = treeChildren.get(stepId) || [];
    const size = subtreeSize.get(stepId) || 1;
    const depth = depths.get(stepId) || 0;
    const centerRow = startRow + (size - 1) / 2;
    positions[stepId] = {
      x: 240 + depth * 330,
      y: 180 + centerRow * 200,
    };

    let nextRow = startRow;
    for (const childId of children) {
      const childSize = subtreeSize.get(childId) || 1;
      place(childId, nextRow);
      nextRow += childSize;
    }
  }

  for (const rootId of rootOrder) {
    if (!depths.has(rootId) || positions[rootId]) {
      continue;
    }

    place(rootId, globalRow);
    globalRow += (subtreeSize.get(rootId) || 1) + 0.8;
  }

  return positions;
}

function compareBranchLabels(left: string, right: string) {
  const rank = (value: string) => {
    const normalized = String(value || '').trim().toLowerCase();
    if (normalized === 'true') return 0;
    if (normalized === 'false') return 1;
    if (normalized === '_default' || normalized === 'default') return 2;
    return 3;
  };

  const rankDifference = rank(left) - rank(right);
  if (rankDifference !== 0) {
    return rankDifference;
  }

  return String(left || '').localeCompare(String(right || ''));
}
