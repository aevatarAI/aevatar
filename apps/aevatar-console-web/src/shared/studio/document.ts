import type {
  StudioConnectorDefinition,
  StudioWorkflowDocument,
  StudioWorkflowRoleDocument,
  StudioWorkflowStepDocument,
} from './models';

export type StudioStepInspectorDraft = {
  readonly kind: 'step';
  readonly id: string;
  readonly type: string;
  readonly targetRole: string;
  readonly next: string;
  readonly branchesText: string;
  readonly parametersText: string;
};

export type StudioRoleInspectorDraft = {
  readonly kind: 'role';
  readonly id: string;
  readonly name: string;
  readonly provider: string;
  readonly model: string;
  readonly systemPrompt: string;
  readonly connectorsText: string;
};

export type StudioNodeInspectorDraft =
  | StudioStepInspectorDraft
  | StudioRoleInspectorDraft;

const ROLE_COMPATIBLE_STEP_TYPES = new Set([
  'llm_call',
  'tool_call',
  'evaluate',
  'reflect',
  'while',
  'parallel',
  'race',
  'connector_call',
]);

const DEFAULT_PARAMETERS_BY_STEP_TYPE: Record<string, Record<string, unknown>> = {
  transform: { op: 'trim' },
  assign: { target: 'result', value: '$input' },
  retrieve_facts: { query: '', top_k: '3' },
  cache: { cache_key: '$input', ttl_seconds: '600', child_step_type: 'llm_call' },
  guard: { check: 'not_empty', on_fail: 'fail' },
  conditional: { condition: '${' + 'eq($input, "ok")' + '}' },
  switch: { on: '$input' },
  while: {
    step: 'llm_call',
    max_iterations: '5',
    condition: '${' + 'lt(iteration, 5)' + '}',
  },
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
  human_input: {
    prompt: 'Please provide the missing input.',
    variable: 'human_response',
  },
  human_approval: { prompt: 'Approve this step?', on_reject: 'fail' },
  workflow_yaml_validate: {},
};

function normalizeString(value: unknown): string {
  return String(value ?? '').trim();
}

function cloneRecord<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

function normalizeConnectorsText(value: string): string[] {
  return value
    .split(/\r?\n|,/)
    .map((entry) => entry.trim())
    .filter(Boolean);
}

function normalizeStepIdCandidate(value: string): string {
  return normalizeString(value).replace(/\s+/g, '_');
}

export function cloneStudioWorkflowDocument(
  document: StudioWorkflowDocument | null | undefined,
): StudioWorkflowDocument | null {
  return document ? cloneRecord(document) : null;
}

export function formatInspectorParameters(
  parameters: Record<string, unknown> | null | undefined,
): string {
  const normalized = parameters && typeof parameters === 'object' ? parameters : {};
  return JSON.stringify(normalized, null, 2);
}

export function parseInspectorParameters(
  value: string,
): Record<string, unknown> {
  const trimmed = value.trim();
  if (!trimmed) {
    return {};
  }

  const parsed = JSON.parse(trimmed) as unknown;
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('Step parameters must be a JSON object.');
  }

  return Object.fromEntries(Object.entries(parsed));
}

export function formatInspectorBranches(
  branches: Record<string, string> | null | undefined,
): string {
  const normalized =
    branches && typeof branches === 'object' && !Array.isArray(branches)
      ? branches
      : {};
  return JSON.stringify(normalized, null, 2);
}

export function parseInspectorBranches(value: string): Record<string, string> {
  const trimmed = value.trim();
  if (!trimmed) {
    return {};
  }

  const parsed = JSON.parse(trimmed) as unknown;
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('Step branches must be a JSON object.');
  }

  return Object.fromEntries(
    Object.entries(parsed)
      .map(([label, target]) => [normalizeString(label), normalizeString(target)])
      .filter(([label, target]) => Boolean(label) && Boolean(target)),
  );
}

function listStepIds(document: StudioWorkflowDocument): string[] {
  return Array.isArray(document.steps)
    ? document.steps
        .map((entry) => normalizeString(entry.id))
        .filter(Boolean)
    : [];
}

function createUniqueStepId(
  document: StudioWorkflowDocument,
  preferredBase: string,
): string {
  const existing = new Set(listStepIds(document));
  const base = normalizeStepIdCandidate(preferredBase) || 'step';
  if (!existing.has(base)) {
    return base;
  }

  let suffix = 2;
  while (existing.has(`${base}_${suffix}`)) {
    suffix += 1;
  }

  return `${base}_${suffix}`;
}

function listRoleIds(document: StudioWorkflowDocument): string[] {
  return Array.isArray(document.roles)
    ? document.roles
        .map((entry) => normalizeString(entry.id))
        .filter(Boolean)
    : [];
}

function resolvePreferredGraphNodeId(document: StudioWorkflowDocument): string {
  const firstStepId = listStepIds(document)[0];
  if (firstStepId) {
    return `step:${firstStepId}`;
  }

  const firstRoleId = listRoleIds(document)[0];
  return firstRoleId ? `role:${firstRoleId}` : '';
}

function createUniqueRoleId(
  document: StudioWorkflowDocument,
  preferredBase: string,
): string {
  const existing = new Set(listRoleIds(document));
  const base = normalizeStepIdCandidate(preferredBase).replace(/_step$/, '') || 'role';
  if (!existing.has(base)) {
    return base;
  }

  let suffix = 2;
  while (existing.has(`${base}_${suffix}`)) {
    suffix += 1;
  }

  return `${base}_${suffix}`;
}

function createDefaultStepParameters(stepType: string): Record<string, unknown> {
  return cloneRecord(DEFAULT_PARAMETERS_BY_STEP_TYPE[stepType] ?? {});
}

export function createStepInspectorDraft(step: {
  id: string;
  type: string;
  targetRole: string;
  parameters: Record<string, unknown>;
  next: string | null;
  branches?: Record<string, string>;
}): StudioStepInspectorDraft {
  return {
    kind: 'step',
    id: step.id,
    type: step.type,
    targetRole: step.targetRole,
    next: step.next ?? '',
    branchesText: formatInspectorBranches(step.branches),
    parametersText: formatInspectorParameters(step.parameters),
  };
}

export function createRoleInspectorDraft(role: {
  id: string;
  name: string;
  provider: string;
  model: string;
  systemPrompt: string;
  connectors: string[];
}): StudioRoleInspectorDraft {
  return {
    kind: 'role',
    id: role.id,
    name: role.name,
    provider: role.provider,
    model: role.model,
    systemPrompt: role.systemPrompt,
    connectorsText: role.connectors.join('\n'),
  };
}

export function applyStepInspectorDraft(
  document: StudioWorkflowDocument,
  currentStepId: string,
  draft: StudioStepInspectorDraft,
): { document: StudioWorkflowDocument; nodeId: string } {
  const nextId = normalizeString(draft.id) || currentStepId;
  const nextType = normalizeString(draft.type) || 'step';
  const nextTargetRole = normalizeString(draft.targetRole);
  const nextStepId = normalizeString(draft.next);
  const nextBranches = parseInspectorBranches(draft.branchesText);
  const nextParameters = parseInspectorParameters(draft.parametersText);

  const steps = Array.isArray(document.steps) ? document.steps : [];
  const nextSteps = steps.map((entry) => {
    const step = { ...entry } as StudioWorkflowStepDocument;
    const stepId = normalizeString(step.id);
    if (stepId === currentStepId) {
      delete step.target_role;
      return {
        ...step,
        id: nextId,
        type: nextType,
        originalType: nextType,
        targetRole: nextTargetRole || null,
        parameters: nextParameters,
        next: nextStepId || null,
        branches: nextBranches,
      } satisfies StudioWorkflowStepDocument;
    }

    const updatedBranches = Object.fromEntries(
      Object.entries(step.branches ?? {}).map(([label, target]) => [
        label,
        target === currentStepId ? nextId : target,
      ]),
    );

    return {
      ...step,
      next: step.next === currentStepId ? nextId : step.next ?? null,
      branches: updatedBranches,
    } satisfies StudioWorkflowStepDocument;
  });

  return {
    document: {
      ...document,
      steps: nextSteps,
    },
    nodeId: `step:${nextId}`,
  };
}

export function insertStepAfter(
  document: StudioWorkflowDocument,
  currentStepId: string,
): { document: StudioWorkflowDocument; nodeId: string } {
  const roles = Array.isArray(document.roles) ? document.roles : [];
  const steps = Array.isArray(document.steps) ? document.steps : [];
  const currentIndex = steps.findIndex(
    (entry) => normalizeString(entry.id) === currentStepId,
  );
  const currentStep =
    currentIndex >= 0 ? ({ ...steps[currentIndex] } as StudioWorkflowStepDocument) : null;
  const fallbackRoleId = normalizeString(roles[0]?.id);
  const currentTargetRole = normalizeString(
    currentStep?.targetRole ?? currentStep?.target_role,
  );
  const baseId = currentStep ? `${currentStepId}_next` : 'step';
  const nextId = createUniqueStepId(document, baseId);
  const insertedStep: StudioWorkflowStepDocument = {
    id: nextId,
    type: 'llm_call',
    originalType: 'llm_call',
    targetRole: currentTargetRole || fallbackRoleId || null,
    parameters: {},
    next: currentStep?.next ?? null,
    branches: {},
  };

  if (currentIndex < 0) {
    return {
      document: {
        ...document,
        steps: [...steps, insertedStep],
      },
      nodeId: `step:${nextId}`,
    };
  }

  const nextSteps = steps.map((entry, index) => {
    if (index !== currentIndex) {
      return { ...entry } as StudioWorkflowStepDocument;
    }

    const step = { ...entry } as StudioWorkflowStepDocument;
    return {
      ...step,
      next: nextId,
    } satisfies StudioWorkflowStepDocument;
  });
  nextSteps.splice(currentIndex + 1, 0, insertedStep);

  return {
    document: {
      ...document,
      steps: nextSteps,
    },
    nodeId: `step:${nextId}`,
  };
}

export function insertStepByType(
  document: StudioWorkflowDocument,
  stepType: string,
  options?: {
    readonly afterStepId?: string | null;
    readonly targetRoleId?: string | null;
    readonly connectorName?: string | null;
    readonly connectors?: readonly StudioConnectorDefinition[];
  },
): { document: StudioWorkflowDocument; nodeId: string } {
  const roles = Array.isArray(document.roles) ? document.roles : [];
  const steps = Array.isArray(document.steps) ? document.steps : [];
  const normalizedStepType = normalizeString(stepType) || 'llm_call';
  const sourceStepId = normalizeString(options?.afterStepId);
  const sourceIndex = sourceStepId
    ? steps.findIndex((entry) => normalizeString(entry.id) === sourceStepId)
    : -1;
  const sourceStep =
    sourceIndex >= 0 ? ({ ...steps[sourceIndex] } as StudioWorkflowStepDocument) : null;
  const preferredRoleId =
    normalizeString(options?.targetRoleId) ||
    normalizeString(sourceStep?.targetRole ?? sourceStep?.target_role) ||
    normalizeString(roles[0]?.id);
  const nextId = createUniqueStepId(document, normalizedStepType);
  const parameters = createDefaultStepParameters(normalizedStepType);
  const connectorName = normalizeString(options?.connectorName);

  if (normalizedStepType === 'connector_call') {
    const preferredConnector =
      connectorName ||
      normalizeString(options?.connectors?.[0]?.name);
    if (preferredConnector) {
      parameters.connector = preferredConnector;
    }
  }

  const insertedStep: StudioWorkflowStepDocument = {
    id: nextId,
    type: normalizedStepType,
    originalType: normalizedStepType,
    targetRole: ROLE_COMPATIBLE_STEP_TYPES.has(normalizedStepType)
      ? preferredRoleId || null
      : null,
    parameters,
    next: sourceStep?.next ?? null,
    branches: {},
  };

  if (sourceIndex < 0) {
    return {
      document: {
        ...document,
        steps: [...steps, insertedStep],
      },
      nodeId: `step:${nextId}`,
    };
  }

  const nextSteps = steps.map((entry, index) => {
    if (index !== sourceIndex) {
      return { ...entry } as StudioWorkflowStepDocument;
    }

    const step = { ...entry } as StudioWorkflowStepDocument;
    delete step.target_role;
    return {
      ...step,
      next: nextId,
    } satisfies StudioWorkflowStepDocument;
  });
  nextSteps.splice(sourceIndex + 1, 0, insertedStep);

  return {
    document: {
      ...document,
      steps: nextSteps,
    },
    nodeId: `step:${nextId}`,
  };
}

export function removeStep(
  document: StudioWorkflowDocument,
  currentStepId: string,
): { document: StudioWorkflowDocument; nodeId: string } {
  const roles = Array.isArray(document.roles) ? document.roles : [];
  const steps = Array.isArray(document.steps) ? document.steps : [];
  const currentIndex = steps.findIndex(
    (entry) => normalizeString(entry.id) === currentStepId,
  );
  if (currentIndex < 0) {
    return {
      document,
      nodeId: resolvePreferredGraphNodeId(document),
    };
  }

  const removedStep = steps[currentIndex] as StudioWorkflowStepDocument;
  const replacementStepId = normalizeString(removedStep.next);
  const nextSteps = steps
    .filter((entry) => normalizeString(entry.id) !== currentStepId)
    .map((entry) => {
      const step = { ...entry } as StudioWorkflowStepDocument;
      const next = normalizeString(step.next);
      const branches = Object.fromEntries(
        Object.entries(step.branches ?? {})
          .map(([label, target]) => [
            label,
            target === currentStepId ? replacementStepId : target,
          ])
          .filter(([, target]) => Boolean(normalizeString(target))),
      );

      return {
        ...step,
        next: next === currentStepId ? replacementStepId || null : step.next ?? null,
        branches,
      } satisfies StudioWorkflowStepDocument;
    });

  const preferredStep =
    nextSteps.find((entry) => normalizeString(entry.id) === replacementStepId) ??
    nextSteps[currentIndex] ??
    nextSteps[currentIndex - 1] ??
    nextSteps[0];
  const fallbackRoleId = normalizeString(roles[0]?.id);

  return {
    document: {
      ...document,
      steps: nextSteps,
    },
    nodeId: preferredStep?.id
      ? `step:${normalizeString(preferredStep.id)}`
      : fallbackRoleId
        ? `role:${fallbackRoleId}`
        : '',
  };
}

export function removeSteps(
  document: StudioWorkflowDocument,
  stepIds: readonly string[],
): { document: StudioWorkflowDocument; nodeId: string } {
  const normalizedStepIds = Array.from(
    new Set(stepIds.map((stepId) => normalizeString(stepId)).filter(Boolean)),
  );
  if (normalizedStepIds.length === 0) {
    return {
      document,
      nodeId: resolvePreferredGraphNodeId(document),
    };
  }

  const stepIdSet = new Set(normalizedStepIds);
  const orderedStepIds = listStepIds(document).filter((stepId) => stepIdSet.has(stepId));
  if (orderedStepIds.length === 0) {
    return {
      document,
      nodeId: resolvePreferredGraphNodeId(document),
    };
  }

  let nextResult: { document: StudioWorkflowDocument; nodeId: string } = {
    document,
    nodeId: resolvePreferredGraphNodeId(document),
  };

  for (const stepId of orderedStepIds) {
    nextResult = removeStep(nextResult.document, stepId);
  }

  return nextResult;
}

export function suggestBranchLabelForStep(
  stepType: string,
  branches: Record<string, string>,
): string | null {
  const normalizedType = normalizeString(stepType).toLowerCase();
  if (normalizedType === 'conditional') {
    if (!branches.true) {
      return 'true';
    }

    if (!branches.false) {
      return 'false';
    }

    return 'true';
  }

  if (normalizedType === 'switch') {
    return '_default';
  }

  return null;
}

export function connectStepToTarget(
  document: StudioWorkflowDocument,
  sourceStepId: string,
  targetStepId: string,
  branchLabel?: string | null,
): { document: StudioWorkflowDocument; nodeId: string } {
  const steps = Array.isArray(document.steps) ? document.steps : [];
  const nextSteps = steps.map((entry) => {
    const step = { ...entry } as StudioWorkflowStepDocument;
    if (normalizeString(step.id) !== sourceStepId) {
      return step;
    }

    const normalizedBranchLabel = normalizeString(branchLabel);
    const nextBranches = {
      ...(step.branches ?? {}),
    };

    if (normalizedBranchLabel) {
      nextBranches[normalizedBranchLabel] = targetStepId;
    } else {
      delete step.target_role;
      step.next = targetStepId;
    }

    return {
      ...step,
      next: normalizedBranchLabel ? step.next ?? null : targetStepId,
      branches: normalizedBranchLabel ? nextBranches : nextBranches,
    } satisfies StudioWorkflowStepDocument;
  });

  return {
    document: {
      ...document,
      steps: nextSteps,
    },
    nodeId: `step:${sourceStepId}`,
  };
}

export function removeStepConnection(
  document: StudioWorkflowDocument,
  sourceStepId: string,
  targetStepId: string,
  branchLabel?: string | null,
): { document: StudioWorkflowDocument; nodeId: string } {
  const normalizedBranchLabel = normalizeString(branchLabel);
  const steps = Array.isArray(document.steps) ? document.steps : [];
  const nextSteps = steps.map((entry) => {
    const step = { ...entry } as StudioWorkflowStepDocument;
    if (normalizeString(step.id) !== sourceStepId) {
      return step;
    }

    const nextBranches = {
      ...(step.branches ?? {}),
    };

    if (normalizedBranchLabel) {
      delete nextBranches[normalizedBranchLabel];
    } else if (normalizeString(step.next) === targetStepId) {
      delete step.target_role;
      step.next = null;
    }

    return {
      ...step,
      next:
        normalizedBranchLabel || normalizeString(step.next) !== targetStepId
          ? step.next ?? null
          : null,
      branches: nextBranches,
    } satisfies StudioWorkflowStepDocument;
  });

  return {
    document: {
      ...document,
      steps: nextSteps,
    },
    nodeId: `step:${sourceStepId}`,
  };
}

export function applyRoleInspectorDraft(
  document: StudioWorkflowDocument,
  currentRoleId: string,
  draft: StudioRoleInspectorDraft,
): { document: StudioWorkflowDocument; nodeId: string } {
  const nextId = normalizeString(draft.id) || currentRoleId;
  const nextName = normalizeString(draft.name) || nextId;
  const nextProvider = normalizeString(draft.provider);
  const nextModel = normalizeString(draft.model);
  const nextSystemPrompt = draft.systemPrompt.trim();
  const nextConnectors = normalizeConnectorsText(draft.connectorsText);

  const roles = Array.isArray(document.roles) ? document.roles : [];
  const nextRoles = roles.map((entry) => {
    const role = { ...entry } as StudioWorkflowRoleDocument;
    if (normalizeString(role.id) !== currentRoleId) {
      return role;
    }

    return {
      ...role,
      id: nextId,
      name: nextName,
      provider: nextProvider || null,
      model: nextModel || null,
      systemPrompt: nextSystemPrompt,
      connectors: nextConnectors,
    } satisfies StudioWorkflowRoleDocument;
  });

  const steps = Array.isArray(document.steps) ? document.steps : [];
  const nextSteps = steps.map((entry) => {
    const step = { ...entry } as StudioWorkflowStepDocument;
    const targetRole = normalizeString(step.targetRole ?? step.target_role);
    if (targetRole !== currentRoleId) {
      return step;
    }

    delete step.target_role;
    return {
      ...step,
      targetRole: nextId,
    } satisfies StudioWorkflowStepDocument;
  });

  return {
    document: {
      ...document,
      roles: nextRoles,
      steps: nextSteps,
    },
    nodeId: `role:${nextId}`,
  };
}

export function addWorkflowRole(
  document: StudioWorkflowDocument,
): { document: StudioWorkflowDocument; nodeId: string } {
  const roles = Array.isArray(document.roles) ? document.roles : [];
  const nextRoleId = createUniqueRoleId(document, 'role');
  const nextRole: StudioWorkflowRoleDocument = {
    id: nextRoleId,
    name: nextRoleId,
    systemPrompt: '',
    provider: null,
    model: null,
    connectors: [],
  };

  return {
    document: {
      ...document,
      roles: [...roles, nextRole],
    },
    nodeId: `role:${nextRoleId}`,
  };
}

export function insertCatalogRoleInWorkflow(
  document: StudioWorkflowDocument,
  role: {
    readonly id: string;
    readonly name: string;
    readonly systemPrompt: string;
    readonly provider: string;
    readonly model: string;
    readonly connectors: readonly string[];
  },
): { document: StudioWorkflowDocument; nodeId: string } {
  const roles = Array.isArray(document.roles) ? document.roles : [];
  const roleId = createUniqueRoleId(document, role.id || role.name || 'role');
  const nextRole: StudioWorkflowRoleDocument = {
    id: roleId,
    name: normalizeString(role.name) || roleId,
    systemPrompt: role.systemPrompt || '',
    provider: normalizeString(role.provider) || null,
    model: normalizeString(role.model) || null,
    connectors: [...role.connectors],
  };

  return {
    document: {
      ...document,
      roles: [...roles, nextRole],
    },
    nodeId: `role:${roleId}`,
  };
}

export function updateWorkflowRole(
  document: StudioWorkflowDocument,
  currentRoleId: string,
  nextRole: {
    readonly id: string;
    readonly name: string;
    readonly provider: string;
    readonly model: string;
    readonly systemPrompt: string;
    readonly connectors: readonly string[];
  },
): { document: StudioWorkflowDocument; nodeId: string } {
  return applyRoleInspectorDraft(
    document,
    currentRoleId,
    {
      kind: 'role',
      id: nextRole.id,
      name: nextRole.name,
      provider: nextRole.provider,
      model: nextRole.model,
      systemPrompt: nextRole.systemPrompt,
      connectorsText: nextRole.connectors.join('\n'),
    },
  );
}

export function removeWorkflowRole(
  document: StudioWorkflowDocument,
  currentRoleId: string,
): { document: StudioWorkflowDocument; nodeId: string } {
  const roles = Array.isArray(document.roles) ? document.roles : [];
  const nextRoles = roles.filter(
    (entry) => normalizeString(entry.id) !== currentRoleId,
  );
  const steps = Array.isArray(document.steps) ? document.steps : [];
  const nextSteps = steps.map((entry) => {
    const step = { ...entry } as StudioWorkflowStepDocument;
    const targetRole = normalizeString(step.targetRole ?? step.target_role);
    if (targetRole !== currentRoleId) {
      return step;
    }

    delete step.target_role;
    return {
      ...step,
      targetRole: null,
    } satisfies StudioWorkflowStepDocument;
  });

  return {
    document: {
      ...document,
      roles: nextRoles,
      steps: nextSteps,
    },
    nodeId:
      nextRoles[0]?.id
        ? `role:${normalizeString(nextRoles[0].id)}`
        : nextSteps[0]?.id
          ? `step:${normalizeString(nextSteps[0].id)}`
          : '',
  };
}
