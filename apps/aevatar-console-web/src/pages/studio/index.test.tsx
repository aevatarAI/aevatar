import {
  act,
  cleanup,
  fireEvent,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import { setLocale } from '@umijs/max';
import { Modal } from 'antd';
import React from 'react';
import { runtimeGAgentApi } from '@/shared/api/runtimeGAgentApi';
import { runtimeQueryApi } from '@/shared/api/runtimeQueryApi';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { ensureActiveAuthSession } from '@/shared/auth/client';
import { studioApi } from '@/shared/studio/api';
import { saveStudioObserveSessionSeed } from '@/shared/studio/observeSession';
import { scriptsApi } from '@/shared/studio/scriptsApi';
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from '../../../tests/reactQueryTestUtils';
import StudioPage, { buildStudioMemberBindingPendingNotice } from './index';

const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

jest.mock('@/shared/ui/ConsoleToast', () => {
  const actual = jest.requireActual('@/shared/ui/ConsoleToast');
  return {
    ...actual,
    useConsoleToast: () => mockConsoleToast,
  };
});

jest.mock('antd', () => {
  const actual = jest.requireActual('antd');
  const modal = actual.Modal;
  return {
    ...actual,
    Modal: Object.assign(modal, {
      confirm: jest.fn(),
    }),
  };
});

const STUDIO_AUTO_RELOGIN_ATTEMPT_KEY = 'aevatar-console:studio:auto-relogin:';

type MockChildrenProps = {
  readonly children?: any;
};

type MockNotice =
  | {
      readonly type: 'success' | 'info' | 'warning' | 'error';
    }
  | null
  | undefined;

type MockValueEvent = {
  readonly target: {
    readonly value: string;
  };
};

const mockWorkflowDocument = {
  name: 'workspace-demo',
  description: 'Workspace workflow',
  roles: [
    {
      id: 'assistant',
      name: 'Assistant',
      systemPrompt: 'Help the operator.',
      provider: 'tornado',
      model: 'gpt-test',
      connectors: ['web-search'],
    },
  ],
  steps: [
    {
      id: 'draft_step',
      type: 'llm_call',
      targetRole: 'assistant',
      parameters: {
        prompt_prefix: 'Draft the response',
      },
      next: 'approve_step',
      branches: {},
    },
    {
      id: 'approve_step',
      type: 'human_approval',
      targetRole: '',
      parameters: {
        reviewer: 'operator',
      },
      next: null,
      branches: {},
    },
  ],
};

function mockCloneValue<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

function mockBuildWorkflowYaml(document: typeof mockWorkflowDocument): string {
  const roleLines = document.roles.flatMap((role) => {
    const lines = [`  - id: ${role.id}`];
    if (role.name) {
      lines.push(`    name: ${role.name}`);
    }
    if (role.provider) {
      lines.push(`    provider: ${role.provider}`);
    }
    if (role.model) {
      lines.push(`    model: ${role.model}`);
    }
    return lines;
  });

  const stepLines = document.steps.flatMap((step) => {
    const lines = [`  - id: ${step.id}`, `    type: ${step.type}`];
    if (step.targetRole) {
      lines.push(`    targetRole: ${step.targetRole}`);
    }
    const parameterEntries = Object.entries(step.parameters ?? {});
    if (parameterEntries.length > 0) {
      lines.push('    parameters:');
      for (const [key, value] of parameterEntries) {
        lines.push(`      ${key}: ${String(value)}`);
      }
    }
    if (step.next) {
      lines.push(`    next: ${step.next}`);
    }
    return lines;
  });

  return [
    `name: ${document.name}`,
    'roles:',
    ...roleLines,
    'steps:',
    ...stepLines,
    '',
  ].join('\n');
}

function mockBuildServiceRevisionCatalog(
  overrides?: Partial<{
    scopeId: string;
    serviceId: string;
    displayName: string;
    workflowName: string;
    revisionId: string;
    deploymentStatus: string;
  }>,
) {
  const scopeId = overrides?.scopeId ?? 'scope-1';
  const serviceId = overrides?.serviceId ?? 'default';
  const displayName = overrides?.displayName ?? 'workspace-demo';
  const workflowName = overrides?.workflowName ?? displayName;
  const revisionId = overrides?.revisionId ?? 'rev-2';
  const deploymentStatus = overrides?.deploymentStatus ?? 'Active';

  return {
    scopeId,
    serviceId,
    serviceKey: `${scopeId}:default:default:${serviceId}`,
    displayName,
    defaultServingRevisionId: revisionId,
    activeServingRevisionId: revisionId,
    deploymentId: 'dep-2',
    deploymentStatus,
    primaryActorId: 'actor-default',
    catalogStateVersion: 2,
    catalogLastEventId: 'event-2',
    updatedAt: '2026-03-26T08:00:00Z',
    revisions: [
      {
        revisionId,
        implementationKind: 'workflow',
        status: 'Published',
        artifactHash: 'hash-2',
        failureReason: '',
        isDefaultServing: true,
        isActiveServing: true,
        isServingTarget: true,
        allocationWeight: 100,
        servingState: 'Active',
        deploymentId: 'dep-2',
        primaryActorId: 'actor-default',
        createdAt: '2026-03-26T07:00:00Z',
        preparedAt: '2026-03-26T07:01:00Z',
        publishedAt: '2026-03-26T07:02:00Z',
        retiredAt: null,
        workflowName,
        workflowDefinitionActorId: 'scope-workflow:scope-1:default',
        inlineWorkflowCount: 1,
        scriptId: '',
        scriptRevision: '',
        scriptDefinitionActorId: '',
        scriptSourceHash: '',
        staticAgentKind: '',
      },
    ],
  };
}

function mockBuildScriptServiceRevisionCatalog(
  overrides?: Partial<{
    scopeId: string;
    serviceId: string;
    displayName: string;
    scriptId: string;
    revisionId: string;
  }>,
) {
  const scriptId = overrides?.scriptId ?? 'script-alpha';
  const revisionId = overrides?.revisionId ?? 'rev-script-1';
  const catalog = mockBuildServiceRevisionCatalog({
    scopeId: overrides?.scopeId,
    serviceId: overrides?.serviceId ?? scriptId,
    displayName: overrides?.displayName ?? scriptId,
    workflowName: '',
    revisionId,
  });

  return {
    ...catalog,
    revisions: [
      {
        ...catalog.revisions[0],
        implementationKind: 'script',
        workflowName: '',
        workflowDefinitionActorId: '',
        inlineWorkflowCount: 0,
        scriptId,
        scriptRevision: revisionId,
        scriptDefinitionActorId: 'definition-1',
        scriptSourceHash: 'hash-1',
      },
    ],
  };
}

function mockBuildGAgentServiceRevisionCatalog(
  overrides?: Partial<{
    scopeId: string;
    serviceId: string;
    displayName: string;
    agentKind: string;
    revisionId: string;
  }>,
) {
  const agentKind = overrides?.agentKind ?? 'Tests.OrdersGAgent';
  const revisionId = overrides?.revisionId ?? 'rev-gagent-1';
  const catalog = mockBuildServiceRevisionCatalog({
    scopeId: overrides?.scopeId,
    serviceId: overrides?.serviceId ?? 'gagent-1',
    displayName: overrides?.displayName ?? 'gagent-1',
    workflowName: '',
    revisionId,
  });

  return {
    ...catalog,
    revisions: [
      {
        ...catalog.revisions[0],
        implementationKind: 'gagent',
        workflowName: '',
        workflowDefinitionActorId: '',
        inlineWorkflowCount: 0,
        scriptId: '',
        scriptRevision: '',
        scriptDefinitionActorId: '',
        scriptSourceHash: '',
        staticAgentKind: agentKind,
      },
    ],
  };
}

function mockBuildServiceRunSummary(
  overrides?: Partial<{
    scopeId: string;
    serviceId: string;
    runId: string;
    actorId: string;
    workflowName: string;
    completionStatus: string;
    lastUpdatedAt: string;
    lastError: string;
  }>,
) {
  const scopeId = overrides?.scopeId ?? 'scope-1';
  const serviceId = overrides?.serviceId ?? 'default';
  const runId = overrides?.runId ?? 'execution-1';
  const actorId = overrides?.actorId ?? 'actor-1';
  const workflowName = overrides?.workflowName ?? 'workspace-demo';
  const completionStatus = overrides?.completionStatus ?? 'running';
  const lastUpdatedAt = overrides?.lastUpdatedAt ?? '2026-03-18T00:00:30Z';
  const lastError = overrides?.lastError ?? '';

  return {
    scopeId,
    serviceId,
    runId,
    actorId,
    definitionActorId: `definition:${workflowName}`,
    revisionId: 'rev-2',
    deploymentId: 'dep-2',
    workflowName,
    completionStatus,
    stateVersion: 2,
    lastEventId: `event:${runId}`,
    lastUpdatedAt,
    boundAt: '2026-03-18T00:00:00Z',
    bindingUpdatedAt: '2026-03-18T00:00:00Z',
    lastSuccess: completionStatus === 'completed' ? true : null,
    totalSteps: 2,
    completedSteps: completionStatus === 'running' ? 1 : 2,
    roleReplyCount: 0,
    lastOutput: completionStatus === 'completed' ? 'Completed output' : '',
    lastError,
  };
}

function mockBuildServiceRunAuditSnapshot(
  overrides?: Partial<{
    scopeId: string;
    serviceId: string;
    runId: string;
    actorId: string;
    workflowName: string;
    completionStatus: string;
    input: string;
    finalOutput: string;
    finalError: string;
  }>,
) {
  const summary = mockBuildServiceRunSummary({
    scopeId: overrides?.scopeId,
    serviceId: overrides?.serviceId,
    runId: overrides?.runId,
    actorId: overrides?.actorId,
    workflowName: overrides?.workflowName,
    completionStatus: overrides?.completionStatus,
    lastError: overrides?.finalError,
  });
  const completed =
    summary.completionStatus === 'completed' ||
    summary.completionStatus === 'failed' ||
    summary.completionStatus === 'stopped';

  return {
    summary,
    audit: {
      reportVersion: '1',
      projectionScope: 'current-state',
      topologySource: 'projection',
      completionStatus: summary.completionStatus,
      workflowName: summary.workflowName,
      rootActorId: summary.actorId,
      commandId: `command:${summary.runId}`,
      stateVersion: summary.stateVersion,
      lastEventId: summary.lastEventId,
      createdAt: '2026-03-18T00:00:00Z',
      updatedAt: summary.lastUpdatedAt,
      startedAt: '2026-03-18T00:00:00Z',
      endedAt: completed ? summary.lastUpdatedAt : null,
      durationMs: completed ? 30000 : 0,
      success: summary.completionStatus === 'completed' ? true : null,
      input: overrides?.input ?? 'Run the demo workflow.',
      finalOutput:
        overrides?.finalOutput ??
        (summary.completionStatus === 'completed' ? 'Completed output' : ''),
      finalError: overrides?.finalError ?? summary.lastError,
      topology: [],
      steps: [],
      roleReplies: [],
      timeline: [],
      summary: {
        totalSteps: summary.totalSteps,
        requestedSteps: summary.totalSteps,
        completedSteps: summary.completedSteps,
        roleReplyCount: summary.roleReplyCount,
        stepTypeCounts: {},
      },
    },
  };
}

let mockParsedDocument = mockCloneValue(mockWorkflowDocument);
let mockWorkflowFile: any;
let mockWorkflowSummaries: any[];
let mockStudioMembers: any[];
let mockConnectorCatalog: any;
let mockConnectorDraftResponse: any;
let mockRoleCatalog: any;
let mockRoleDraftResponse: any;
let mockLastWorkflowBuildPanelProps: any;
const defaultStudioAppContext = {
  mode: 'proxy',
  scopeId: null,
  scopeResolved: false,
  scopeSource: '',
  workflowStorageMode: 'workspace',
  scriptStorageMode: 'draft',
  features: {
    publishedWorkflows: true,
    scripts: false,
  },
  scriptContract: {
    inputType: 'type.googleapis.com/example.Command',
    readModelFields: ['input', 'output'],
  },
};

function mockCreateDefaultStudioAppContext() {
  return {
    ...defaultStudioAppContext,
    features: {
      ...defaultStudioAppContext.features,
    },
    scriptContract: {
      ...defaultStudioAppContext.scriptContract,
    },
  };
}

function mockCreateDefaultStudioAuthSession() {
  return {
    enabled: false,
    authenticated: false,
    providerDisplayName: 'NyxID',
  };
}

function mockCreateDefaultWorkflowSummaries() {
  return [
    {
      workflowId: 'workflow-1',
      name: 'workspace-demo',
      description: 'Workspace workflow',
      fileName: 'workspace-demo.yaml',
      filePath: '/tmp/workflows/workspace-demo.yaml',
      directoryId: 'dir-1',
      directoryLabel: 'Workspace',
      stepCount: 2,
      hasLayout: true,
      updatedAtUtc: '2026-03-18T00:00:00Z',
    },
  ];
}

function mockCreateDefaultStudioMembers() {
  return [
    {
      memberId: 'workspace-demo',
      scopeId: 'scope-1',
      displayName: 'workspace-demo',
      description: 'Workspace workflow member',
      implementationKind: 'workflow',
      lifecycleStage: 'bind_ready',
      publishedServiceId: 'default',
      lastBoundRevisionId: 'rev-2',
      teamId: 't-alpha',
      createdAt: '2026-04-27T08:00:00Z',
      updatedAt: '2026-04-27T08:05:00Z',
    },
  ];
}

function mockCreateDefaultTeamSummary(overrides: Record<string, unknown> = {}) {
  return {
    teamId: 't-alpha',
    scopeId: 'scope-1',
    displayName: 'Alpha Team',
    description: 'Team summary',
    lifecycleStage: 'active',
    memberCount: 1,
    createdAt: '2026-05-01T08:00:00Z',
    updatedAt: '2026-05-01T08:05:00Z',
    entryMemberId: null,
    ...overrides,
  };
}

async function mockAuthorWorkflowSuccess(
  _input: { prompt: string },
  options?: {
    onText?: (text: string) => void;
    onReasoning?: (text: string) => void;
  },
) {
  options?.onReasoning?.('Thinking through the workflow structure.');
  options?.onText?.('name: ai-generated\nsteps: []\n');
  return 'name: ai-generated\nsteps: []\n';
}

function createStudioApiStatusError(
  message: string,
  status: number,
  code: string,
): Error & { code: string; status: number } {
  const error = new Error(message) as Error & { code: string; status: number };
  error.name = 'StudioApiError';
  error.code = code;
  error.status = status;
  return error;
}

function resetMockState(): void {
  mockParsedDocument = mockCloneValue(mockWorkflowDocument);
  mockWorkflowSummaries = mockCreateDefaultWorkflowSummaries();
  mockStudioMembers = mockCreateDefaultStudioMembers();
  mockWorkflowFile = {
    workflowId: 'workflow-1',
    name: 'workspace-demo',
    fileName: 'workspace-demo.yaml',
    filePath: '/tmp/workflows/workspace-demo.yaml',
    directoryId: 'dir-1',
    directoryLabel: 'Workspace',
    yaml: mockBuildWorkflowYaml(mockParsedDocument),
    findings: [],
    draftExists: true,
    updatedAtUtc: '2026-03-18T00:00:00Z',
    document: mockParsedDocument,
  };
  mockConnectorCatalog = {
    homeDirectory: 'actor://connector-catalog',
    filePath: 'actor://connector-catalog/connectors',
    fileExists: true,
    connectors: [
      {
        name: 'web-search',
        type: 'http',
        enabled: true,
        timeoutMs: 10000,
        retry: 1,
        http: {
          baseUrl: 'https://example.test',
          allowedMethods: ['GET'],
          allowedPaths: ['/search'],
          allowedInputKeys: ['query'],
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
      },
    ],
  };
  mockConnectorDraftResponse = {
    homeDirectory: 'actor://connector-catalog',
    filePath: 'actor://connector-catalog/connectors/draft',
    fileExists: false,
    updatedAtUtc: null,
    draft: null,
  };
  mockRoleCatalog = {
    homeDirectory: 'actor://role-catalog',
    filePath: 'actor://role-catalog/roles',
    fileExists: true,
    roles: [
      {
        id: 'assistant',
        name: 'Assistant',
        systemPrompt: 'Help the operator.',
        provider: 'tornado',
        model: 'gpt-test',
        connectors: ['web-search'],
      },
    ],
  };
  mockRoleDraftResponse = {
    homeDirectory: 'actor://role-catalog',
    filePath: 'actor://role-catalog/roles/draft',
    fileExists: false,
    updatedAtUtc: null,
    draft: null,
  };
}

jest.mock('@/shared/auth/client', () => ({
  ensureActiveAuthSession: jest.fn(async () => null),
}));

jest.mock('@/shared/api/runtimeQueryApi', () => ({
  runtimeQueryApi: {
    listPrimitives: jest.fn(async () => [
      {
        name: 'llm_call',
        aliases: [],
        category: 'core',
        description: 'LLM call',
        parameters: [],
        exampleWorkflows: [],
      },
      {
        name: 'demo_template',
        aliases: ['render_template'],
        category: 'demo',
        description: 'Demo template primitive',
        parameters: [],
        exampleWorkflows: ['demo_template'],
      },
    ]),
  },
}));

jest.mock('@/shared/api/runtimeGAgentApi', () => ({
  runtimeGAgentApi: {
    listKinds: jest.fn(async () => [
      {
        agentKind: 'Tests.OrdersGAgent',
        displayName: 'Orders Assistant',
        diagnosticClrTypeName: 'Tests.OrdersGAgent, Tests',
        endpoints: [],
      },
    ]),
    listActors: jest.fn(async () => [
      {
        agentKind: 'Tests.OrdersGAgent',
        actorIds: ['orders-gagent'],
      },
    ]),
  },
}));

jest.mock('@/shared/api/scopeRuntimeApi', () => ({
  scopeRuntimeApi: {
    listServices: jest.fn(async () => [
      {
        serviceId: 'default',
        displayName: 'workspace-demo',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-default',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with the published workflow.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]),
    getServiceRevisions: jest.fn(async (_scopeId: string, serviceId: string) =>
      mockBuildServiceRevisionCatalog({ serviceId }),
    ),
    listMemberRuns: jest.fn(async (_scopeId: string, memberId: string) => ({
      scopeId: 'scope-1',
      serviceId: memberId,
      serviceKey: `scope-1:default:default:${memberId}`,
      displayName: 'workspace-demo',
      runs: [mockBuildServiceRunSummary({ serviceId: memberId })],
    })),
    listServiceRuns: jest.fn(async (_scopeId: string, serviceId: string) => ({
      scopeId: 'scope-1',
      serviceId,
      serviceKey: `scope-1:default:default:${serviceId}`,
      displayName: 'workspace-demo',
      runs: [mockBuildServiceRunSummary({ serviceId })],
    })),
    getMemberRunAudit: jest.fn(
      async (_scopeId: string, memberId: string, runId: string) =>
        mockBuildServiceRunAuditSnapshot({ serviceId: memberId, runId }),
    ),
    getServiceRunAudit: jest.fn(
      async (_scopeId: string, serviceId: string, runId: string) =>
        mockBuildServiceRunAuditSnapshot({ serviceId, runId }),
    ),
  },
}));

jest.mock('@/shared/api/runtimeRunsApi', () => ({
  runtimeRunsApi: {
    streamTeamChat: jest.fn(async () => ({ ok: true })),
    stop: jest.fn(async (_scopeId: string, request: { runId: string }) => ({
      accepted: true,
      runId: request.runId,
    })),
    resume: jest.fn(async (_scopeId: string, request: { runId: string }) => ({
      accepted: true,
      runId: request.runId,
    })),
    signal: jest.fn(async (_scopeId: string, request: { runId: string }) => ({
      accepted: true,
      runId: request.runId,
    })),
  },
}));

const mockEnsureActiveAuthSession =
  ensureActiveAuthSession as jest.MockedFunction<
    (_config?: unknown) => Promise<Record<string, unknown> | null>
  >;
const mockRuntimeQueryApi = runtimeQueryApi as unknown as {
  listPrimitives: jest.Mock;
};
const mockRuntimeGAgentApi = runtimeGAgentApi as unknown as {
  listKinds: jest.Mock;
  listActors: jest.Mock;
};
const mockScopeRuntimeApi = scopeRuntimeApi as unknown as {
  listServices: jest.Mock;
  getServiceRevisions: jest.Mock;
  listMemberRuns: jest.Mock;
  listServiceRuns: jest.Mock;
  getMemberRunAudit: jest.Mock;
  getServiceRunAudit: jest.Mock;
};
const mockRuntimeRunsApi = runtimeRunsApi as unknown as {
  streamTeamChat: jest.Mock;
  stop: jest.Mock;
  resume: jest.Mock;
  signal: jest.Mock;
};

jest.mock('@/shared/studio/api', () => ({
  isStudioApiErrorCode: (error: unknown, status: number, code: string) =>
    error instanceof Error &&
    error.name === 'StudioApiError' &&
    'status' in error &&
    error.status === status &&
    'code' in error &&
    error.code === code,
  isStudioApiStatus: (error: unknown, status: number) =>
    error instanceof Error &&
    error.name === 'StudioApiError' &&
    'status' in error &&
    error.status === status,
  studioApi: {
    getAppContext: jest.fn(async () => mockCreateDefaultStudioAppContext()),
    getAuthSession: jest.fn(async () => mockCreateDefaultStudioAuthSession()),
    getWorkspaceSettings: jest.fn(async () => ({
      runtimeBaseUrl: 'https://aevatar-console-backend-api.aevatar.ai',
      directories: [
        {
          directoryId: 'dir-1',
          label: 'Workspace',
          path: '/tmp/workflows',
          isBuiltIn: false,
        },
      ],
    })),
    getUserLlmSettings: jest.fn(async () => ({
      savedSelection: {
        routeKind: 'gateway',
        routeValue: '/api/v1/llm/gateway/v1',
        modelSelection: {
          kind: 'explicit_model',
          modelId: 'gpt-4.1-mini',
        },
      },
      savedRouteLabel: 'Company LLM Gateway',
      selectionStatus: 'ready',
      catalogDiagnostic: 'unspecified',
      remediation: 'none',
      catalogStatus: 'ready',
      capabilities: {
        canEditRoute: true,
        canEditModel: true,
        canSave: true,
        canRetryCatalog: false,
      },
      routeOptions: [
        {
          routeValue: '',
          label: 'Company LLM Gateway',
          source: 'gateway_provider',
          status: 'ready',
          allowed: true,
          ready: true,
          serviceId: null,
          serviceSlug: null,
          description: null,
        },
        {
          routeValue: '/api/v1/proxy/s/openai',
          label: 'OpenAI',
          source: 'user_service',
          status: 'ready',
          allowed: true,
          ready: true,
          serviceId: 'svc-openai',
          serviceSlug: 'openai',
          description: null,
        },
      ],
      modelGroupsByRoute: [
        {
          routeValue: '',
          groupId: 'openai-gateway',
          label: 'OpenAI Gateway',
          models: ['gpt-4.1-mini', 'gpt-5.4-mini'],
        },
        {
          routeValue: '/api/v1/proxy/s/openai',
          groupId: 'openai',
          label: 'OpenAI',
          models: ['gpt-4.1-mini', 'gpt-5.4-mini'],
        },
      ],
    })),
    listMembers: jest.fn(async () => ({
      scopeId: 'scope-1',
      members: mockStudioMembers,
      nextPageToken: null,
    })),
    listTeamMembers: jest.fn(async (_scopeId: string, teamId: string) => ({
      scopeId: 'scope-1',
      members: mockStudioMembers.filter((member) => member.teamId === teamId),
      nextPageToken: null,
    })),
    getTeam: jest.fn(async (_scopeId: string, teamId: string) =>
      mockCreateDefaultTeamSummary({
        scopeId: _scopeId,
        teamId,
        entryMemberId: 'workspace-demo',
      }),
    ),
    setTeamEntryMember: jest.fn(
      async (_scopeId: string, _teamId: string, memberId: string) => ({
        ...mockCreateDefaultTeamSummary({ scopeId: _scopeId, teamId: _teamId }),
        scopeId: _scopeId,
        teamId: _teamId,
        entryMemberId: memberId,
      }),
    ),
    clearTeamEntryMember: jest.fn(
      async (_scopeId: string, _teamId: string) => ({
        ...mockCreateDefaultTeamSummary({ scopeId: _scopeId, teamId: _teamId }),
        scopeId: _scopeId,
        teamId: _teamId,
        entryMemberId: null,
      }),
    ),
    getMember: jest.fn(async (_scopeId: string, memberId: string) => {
      const matchedMember =
        mockStudioMembers.find((member) => member.memberId === memberId) ??
        mockStudioMembers[0];
      return {
        summary: matchedMember,
        implementationRef:
          matchedMember?.implementationKind === 'workflow'
            ? {
                implementationKind: 'workflow',
                workflowId: matchedMember.displayName,
                workflowRevision: matchedMember.lastBoundRevisionId,
              }
            : matchedMember?.implementationKind === 'script'
              ? {
                  implementationKind: 'script',
                  scriptId: matchedMember.scriptId || matchedMember.displayName,
                  scriptRevision: matchedMember.lastBoundRevisionId,
                }
              : {
                  implementationKind: 'gagent',
                  agentKind: matchedMember?.displayName || '',
                },
        lastBinding: matchedMember?.lastBoundRevisionId
          ? {
              publishedServiceId: matchedMember.publishedServiceId,
              revisionId: matchedMember.lastBoundRevisionId,
              implementationKind: matchedMember.implementationKind,
              boundAt: matchedMember.updatedAt,
            }
          : null,
      };
    }),
    createMember: jest.fn(
      async (input: {
        scopeId: string;
        displayName: string;
        implementationKind: 'workflow' | 'script' | 'gagent';
        description?: string | null;
        memberId?: string | null;
        teamId?: string | null;
      }) => {
        const nextMemberId =
          input.memberId?.trim() ||
          input.displayName
            .trim()
            .toLowerCase()
            .replace(/[^a-z0-9_-]+/g, '-');
        const nextMember = {
          memberId: nextMemberId,
          scopeId: input.scopeId,
          displayName: input.displayName.trim(),
          description: input.description?.trim() || '',
          implementationKind: input.implementationKind,
          lifecycleStage: 'created',
          publishedServiceId: `member-${nextMemberId}`,
          lastBoundRevisionId: null,
          teamId: input.teamId ?? null,
          createdAt: '2026-04-27T08:10:00Z',
          updatedAt: '2026-04-27T08:10:00Z',
        };
        mockStudioMembers = [nextMember, ...mockStudioMembers];
        return nextMember;
      },
    ),
    createMemberWithId: jest.fn(
      async (input: {
        scopeId: string;
        memberId: string;
        displayName: string;
        implementationKind: 'workflow' | 'script' | 'gagent';
        description?: string | null;
        teamId?: string | null;
      }) => {
        const nextMemberId = input.memberId.trim();
        const nextMember = {
          memberId: nextMemberId,
          scopeId: input.scopeId,
          displayName: input.displayName.trim(),
          description: input.description?.trim() || '',
          implementationKind: input.implementationKind,
          lifecycleStage: 'created',
          publishedServiceId: `member-${nextMemberId}`,
          lastBoundRevisionId: null,
          teamId: input.teamId ?? null,
          createdAt: '2026-04-27T08:10:00Z',
          updatedAt: '2026-04-27T08:10:00Z',
        };
        mockStudioMembers = [nextMember, ...mockStudioMembers];
        return nextMember;
      },
    ),
    getSkillsHealth: jest.fn(async () => ({
      baseUrl: 'https://ornn.chrono-ai.fun',
      reachable: true,
      message: 'Connected to Ornn.',
    })),
    searchSkills: jest.fn(async () => ({
      baseUrl: 'https://ornn.chrono-ai.fun',
      total: 1,
      totalPages: 1,
      page: 1,
      pageSize: 100,
      items: [
        {
          guid: 'skill-1',
          name: 'ornn-search',
          description: 'Search Ornn for reusable skills.',
          isPrivate: false,
        },
      ],
    })),
    listWorkflows: jest.fn(async () => mockWorkflowSummaries),
    getTemplateWorkflow: jest.fn(async () => ({
      catalog: {
        name: 'published-demo',
        description: 'Published demo workflow',
      },
      yaml: [
        'name: published-demo',
        'description: Published demo workflow',
        'roles:',
        '  - id: reviewer',
        '    name: Reviewer',
        'steps:',
        '  - id: step_prepare',
        '    type: llm_call',
        '    targetRole: reviewer',
        '    next: step_finish',
        '  - id: step_finish',
        '    type: emit',
        '    targetRole: reviewer',
        '',
      ].join('\n'),
      definition: {
        name: 'published-demo',
        description: 'Published demo workflow',
        closedWorldMode: false,
        roles: [
          {
            id: 'reviewer',
            name: 'Reviewer',
            systemPrompt: 'Review the published flow.',
            provider: 'tornado',
            model: 'gpt-review',
            temperature: 0.1,
            maxTokens: 512,
            maxToolRounds: 2,
            maxHistoryMessages: 6,
            streamBufferCapacity: 4,
            eventModules: [],
            eventRoutes: '',
            connectors: [],
          },
        ],
        steps: [
          {
            id: 'step_prepare',
            type: 'llm_call',
            targetRole: 'reviewer',
            parameters: {
              prompt: '{{prompt}}',
            },
            next: 'step_finish',
            branches: {},
            children: [],
          },
          {
            id: 'step_finish',
            type: 'emit',
            targetRole: 'reviewer',
            parameters: {},
            next: '',
            branches: {},
            children: [],
          },
        ],
      },
      edges: [{ from: 'step_prepare', to: 'step_finish', label: 'next' }],
    })),
    getWorkflow: jest.fn(async () => mockWorkflowFile),
    saveWorkflow: jest.fn(
      async (input: {
        workflowId?: string;
        draftExists?: boolean | null;
        directoryId: string;
        workflowName: string;
        fileName?: string | null;
        yaml: string;
      }) => {
        const resolvedWorkflowId =
          input.workflowId || `workflow-${mockWorkflowSummaries.length + 1}`;
        mockWorkflowFile = {
          ...mockWorkflowFile,
          workflowId: resolvedWorkflowId,
          name: input.workflowName,
          fileName: input.fileName || `${input.workflowName}.yaml`,
          filePath: `/tmp/workflows/${input.fileName || `${input.workflowName}.yaml`}`,
          directoryId: input.directoryId,
          yaml: input.yaml,
          draftExists: input.draftExists ?? true,
          updatedAtUtc: '2026-03-18T00:05:00Z',
          document: {
            ...mockWorkflowFile.document,
            name: input.workflowName,
          },
        };
        const existingSummaryIndex = mockWorkflowSummaries.findIndex(
          (workflow) => workflow.workflowId === resolvedWorkflowId,
        );
        const nextSummary = {
          workflowId: resolvedWorkflowId,
          name: input.workflowName,
          description: 'Workspace workflow',
          fileName: mockWorkflowFile.fileName,
          filePath: mockWorkflowFile.filePath,
          directoryId: input.directoryId,
          directoryLabel: 'Workspace',
          stepCount: 0,
          hasLayout: true,
          updatedAtUtc: '2026-03-18T00:05:00Z',
        };
        if (existingSummaryIndex >= 0) {
          mockWorkflowSummaries[existingSummaryIndex] = nextSummary;
        } else {
          mockWorkflowSummaries = [nextSummary, ...mockWorkflowSummaries];
        }

        return mockWorkflowFile;
      },
    ),
    deleteWorkflow: jest.fn(async (workflowId: string) => {
      mockWorkflowSummaries = mockWorkflowSummaries.filter(
        (workflow) => workflow.workflowId !== workflowId,
      );
      if (mockWorkflowFile.workflowId === workflowId) {
        const fallback = mockWorkflowSummaries[0];
        if (fallback) {
          mockWorkflowFile = {
            ...mockWorkflowFile,
            workflowId: fallback.workflowId,
            name: fallback.name,
            fileName: fallback.fileName,
            filePath: fallback.filePath,
            directoryId: fallback.directoryId,
            yaml: `name: ${fallback.name}\nsteps: []\n`,
            document: {
              ...mockWorkflowFile.document,
              name: fallback.name,
            },
          };
        }
      }
      return undefined;
    }),
    deleteMember: jest.fn(
      async (input: { scopeId: string; memberId: string }) => {
        return {
          status: 'delete_accepted',
          scopeId: input.scopeId,
          memberId: input.memberId,
          ackedAt: '2026-07-09T08:12:00Z',
        };
      },
    ),
    parseYaml: jest.fn(async (input: { yaml: string }) => ({
      document: input.yaml.includes('name: legacy_draft')
        ? {
            name: 'legacy_draft',
            description: '',
            roles: [],
            steps: [],
          }
        : input.yaml.includes('name: published-demo')
          ? {
              name: 'published-demo',
              description: 'Published demo workflow',
              roles: [],
              steps: [],
            }
          : input.yaml.includes('name: draft')
            ? {
                name: 'draft',
                description: '',
                roles: [],
                steps: [],
              }
            : input.yaml.includes('name: ai-generated')
              ? {
                  name: 'ai-generated',
                  description: 'Generated by Studio AI',
                  roles: [],
                  steps: [],
                }
              : mockParsedDocument,
      findings: [],
    })),
    serializeYaml: jest.fn(
      async (input: { document: typeof mockWorkflowDocument }) => {
        mockParsedDocument = mockCloneValue(input.document);
        mockWorkflowFile = {
          ...mockWorkflowFile,
          yaml: mockBuildWorkflowYaml(mockParsedDocument),
          document: mockParsedDocument,
        };

        return {
          yaml: mockWorkflowFile.yaml,
          document: mockParsedDocument,
          findings: [],
        };
      },
    ),
    previewExplicitRequests: jest.fn(),
    listExecutions: jest.fn(async () => [
      {
        executionId: 'execution-1',
        workflowName: 'workspace-demo',
        prompt: 'Run the demo workflow.',
        status: 'running',
        startedAtUtc: '2026-03-18T00:00:00Z',
        completedAtUtc: null,
        actorId: 'actor-1',
        error: null,
      },
    ]),
    getExecution: jest.fn(async (executionId: string) => ({
      executionId,
      workflowName: 'workspace-demo',
      prompt:
        executionId === 'execution-2'
          ? 'Run the active draft from Studio.'
          : 'Run the demo workflow.',
      status: 'running',
      startedAtUtc:
        executionId === 'execution-2'
          ? '2026-03-18T00:06:00Z'
          : '2026-03-18T00:00:00Z',
      completedAtUtc: null,
      actorId: executionId === 'execution-2' ? 'actor-2' : 'actor-1',
      error: null,
      frames: [],
    })),
    startExecution: jest.fn(
      async (input: { workflowName: string; prompt: string }) => ({
        executionId: 'execution-2',
        workflowName: input.workflowName,
        prompt: input.prompt,
        runtimeBaseUrl: 'https://aevatar-console-backend-api.aevatar.ai',
        status: 'running',
        startedAtUtc: '2026-03-18T00:06:00Z',
        completedAtUtc: null,
        actorId: 'actor-2',
        error: null,
        frames: [
          {
            receivedAtUtc: '2026-03-18T00:06:01Z',
            payload: '{"event":"started"}',
          },
        ],
      }),
    ),
    bindScopeWorkflow: jest.fn(
      async (input: {
        scopeId: string;
        displayName?: string;
        workflowYamls: string[];
      }) => ({
        scopeId: input.scopeId,
        serviceId: 'default',
        displayName: input.displayName || 'workspace-demo',
        targetKind: 'workflow',
        targetName: input.displayName || 'workspace-demo',
        revisionId: 'rev-2',
        workflowName: input.displayName || 'workspace-demo',
        definitionActorIdPrefix: 'scope-workflow:scope-1:default',
        expectedActorId: 'scope-workflow:scope-1:default:dep-1',
      }),
    ),
    bindScopeScript: jest.fn(
      async (input: {
        scopeId: string;
        displayName?: string;
        scriptId: string;
        scriptRevision: string;
      }) => ({
        scopeId: input.scopeId,
        serviceId: input.scriptId,
        displayName: input.displayName || input.scriptId,
        targetKind: 'script',
        targetName: input.scriptId,
        revisionId: 'rev-script-binding',
        script: {
          scriptId: input.scriptId,
          scriptRevision: input.scriptRevision,
          definitionActorId: 'definition-1',
        },
        expectedActorId: `scope-script:${input.scopeId}:${input.scriptId}:dep-1`,
      }),
    ),
    bindMemberScript: jest.fn(
      async (input: {
        scopeId: string;
        memberId: string;
        displayName?: string;
        scriptId: string;
        scriptRevision: string;
      }) => {
        const existingMember = mockStudioMembers.find(
          (member) => member.memberId === input.memberId,
        );
        const serviceId =
          existingMember?.publishedServiceId || `member-${input.memberId}`;
        mockStudioMembers = mockStudioMembers.map((member) =>
          member.memberId === input.memberId
            ? {
                ...member,
                lifecycleStage: 'bind_ready',
                publishedServiceId: serviceId,
                lastBoundRevisionId: 'rev-script-binding',
                updatedAt: '2026-04-27T08:15:00Z',
              }
            : member,
        );

        return {
          scopeId: input.scopeId,
          serviceId,
          displayName: input.displayName || input.scriptId,
          targetKind: 'script',
          targetName: input.scriptId,
          revisionId: 'rev-script-binding',
          script: {
            scriptId: input.scriptId,
            scriptRevision: input.scriptRevision,
            definitionActorId: 'definition-1',
          },
          expectedActorId: `scope-script:${input.scopeId}:${serviceId}:dep-1`,
        };
      },
    ),
    bindMemberWorkflow: jest.fn(
      async (input: {
        scopeId: string;
        memberId: string;
        displayName?: string;
        workflowId: string;
        workflowYamls: string[];
      }) => {
        mockStudioMembers = mockStudioMembers.map((member) =>
          member.memberId === input.memberId
            ? {
                ...member,
                lifecycleStage: 'bind_ready',
                lastBoundRevisionId: 'rev-2',
                updatedAt: '2026-04-27T08:15:00Z',
              }
            : member,
        );

        return {
          status: 'accepted',
          bindingRunId: 'bind-member-workflow-1',
          scopeId: input.scopeId,
          memberId: input.memberId,
        };
      },
    ),
    bindMemberGAgent: jest.fn(
      async (input: {
        scopeId: string;
        memberId: string;
        displayName?: string;
        agentKind: string;
        endpoints: Array<{
          endpointId: string;
          displayName?: string;
          kind?: string;
          requestTypeUrl?: string;
          responseTypeUrl?: string;
          description?: string;
        }>;
      }) => {
        const existingMember = mockStudioMembers.find(
          (member) => member.memberId === input.memberId,
        );
        const serviceId =
          existingMember?.publishedServiceId || `member-${input.memberId}`;
        mockStudioMembers = mockStudioMembers.map((member) =>
          member.memberId === input.memberId
            ? {
                ...member,
                lifecycleStage: 'bind_ready',
                publishedServiceId: serviceId,
                lastBoundRevisionId: 'rev-gagent-1',
                updatedAt: '2026-04-27T08:15:00Z',
              }
            : member,
        );

        return {
          status: 'accepted',
          bindingRunId: 'bind-member-gagent-1',
          scopeId: input.scopeId,
          memberId: input.memberId,
        };
      },
    ),
    getMemberBindingRun: jest.fn(
      async (scopeId: string, memberId: string, bindingRunId: string) => ({
        bindingRunId,
        scopeId,
        memberId,
        status: 'succeeded',
        failure: null,
        updatedAt: '2026-04-27T08:15:01Z',
      }),
    ),
    bindScopeGAgent: jest.fn(
      async (input: {
        scopeId: string;
        displayName?: string;
        agentKind: string;
        endpoints: Array<{
          endpointId: string;
          displayName?: string;
          kind?: string;
          requestTypeUrl?: string;
          responseTypeUrl?: string;
          description?: string;
        }>;
      }) => ({
        scopeId: input.scopeId,
        displayName: input.displayName || 'orders-gagent',
        targetKind: 'gagent',
        targetName: input.agentKind || input.displayName || 'orders-gagent',
        revisionId: 'rev-gagent-1',
        expectedActorId: 'scope-gagent:scope-1:default:dep-1',
      }),
    ),
    getScopeBinding: jest.fn(async () => ({
      available: true,
      scopeId: 'scope-1',
      serviceId: 'default',
      displayName: 'workspace-demo',
      serviceKey: 'scope-1:default:default:default',
      defaultServingRevisionId: 'rev-2',
      activeServingRevisionId: 'rev-2',
      deploymentId: 'dep-2',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-default',
      updatedAt: '2026-03-26T08:00:00Z',
      revisions: [
        {
          revisionId: 'rev-2',
          implementationKind: 'workflow',
          status: 'Published',
          artifactHash: 'hash-2',
          failureReason: '',
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: 'Active',
          deploymentId: 'dep-2',
          primaryActorId: 'actor-default',
          createdAt: '2026-03-26T07:00:00Z',
          preparedAt: '2026-03-26T07:01:00Z',
          publishedAt: '2026-03-26T07:02:00Z',
          retiredAt: null,
          workflowName: 'workspace-demo',
          workflowDefinitionActorId: 'scope-workflow:scope-1:default',
          inlineWorkflowCount: 1,
          scriptId: '',
          scriptRevision: '',
          scriptDefinitionActorId: '',
          scriptSourceHash: '',
          staticAgentKind: '',
        },
        {
          revisionId: 'rev-1',
          implementationKind: 'workflow',
          status: 'Published',
          artifactHash: 'hash-1',
          failureReason: '',
          isDefaultServing: false,
          isActiveServing: false,
          isServingTarget: false,
          allocationWeight: 0,
          servingState: '',
          deploymentId: '',
          primaryActorId: '',
          createdAt: '2026-03-25T07:00:00Z',
          preparedAt: '2026-03-25T07:01:00Z',
          publishedAt: '2026-03-25T07:02:00Z',
          retiredAt: null,
          workflowName: 'workspace-demo-v1',
          workflowDefinitionActorId: 'scope-workflow:scope-1:default:v1',
          inlineWorkflowCount: 1,
          scriptId: '',
          scriptRevision: '',
          scriptDefinitionActorId: '',
          scriptSourceHash: '',
          staticAgentKind: '',
        },
      ],
    })),
    getDefaultRouteTarget: jest.fn(async () => ({
      available: true,
      scopeId: 'scope-1',
      serviceId: 'default',
      displayName: 'workspace-demo',
      serviceKey: 'scope-1:default:default:default',
      defaultServingRevisionId: 'rev-2',
      activeServingRevisionId: 'rev-2',
      deploymentId: 'dep-2',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-default',
      updatedAt: '2026-03-26T08:00:00Z',
      revisions: [
        {
          revisionId: 'rev-2',
          implementationKind: 'workflow',
          status: 'Published',
          artifactHash: 'hash-2',
          failureReason: '',
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: 'Active',
          deploymentId: 'dep-2',
          primaryActorId: 'actor-default',
          createdAt: '2026-03-26T07:30:00Z',
          preparedAt: '2026-03-26T07:35:00Z',
          publishedAt: '2026-03-26T07:40:00Z',
          retiredAt: null,
          workflowName: 'workspace-demo',
          workflowDefinitionActorId: 'definition://workspace-demo',
          inlineWorkflowCount: 1,
          scriptId: '',
          scriptRevision: '',
          scriptDefinitionActorId: '',
          scriptSourceHash: '',
          staticAgentKind: '',
        },
      ],
    })),
    activateScopeBindingRevision: jest.fn(
      async (input: { scopeId: string; revisionId: string }) => ({
        scopeId: input.scopeId,
        serviceId: 'default',
        displayName: 'workspace-demo',
        revisionId: input.revisionId,
      }),
    ),
    retireScopeBindingRevision: jest.fn(
      async (input: { scopeId: string; revisionId: string }) => ({
        scopeId: input.scopeId,
        serviceId: 'default',
        revisionId: input.revisionId,
        status: 'Retiring',
      }),
    ),
    stopExecution: jest.fn(async (executionId: string) => ({
      executionId,
      workflowName: 'workspace-demo',
      prompt: 'Run the demo workflow.',
      runtimeBaseUrl: 'https://aevatar-console-backend-api.aevatar.ai',
      status: 'stopped',
      startedAtUtc: '2026-03-18T00:00:00Z',
      completedAtUtc: '2026-03-18T00:07:00Z',
      actorId: 'actor-1',
      error: null,
      frames: [],
    })),
    getConnectorCatalog: jest.fn(async () => ({
      ...mockConnectorCatalog,
    })),
    getConnectorDraft: jest.fn(async () => ({
      ...mockConnectorDraftResponse,
    })),
    saveConnectorDraft: jest.fn(async (input: { draft: any }) => {
      mockConnectorDraftResponse = {
        ...mockConnectorDraftResponse,
        fileExists: true,
        updatedAtUtc: '2026-03-18T00:03:00Z',
        draft: input.draft,
      };
      return mockConnectorDraftResponse;
    }),
    deleteConnectorDraft: jest.fn(async () => {
      mockConnectorDraftResponse = {
        ...mockConnectorDraftResponse,
        fileExists: false,
        updatedAtUtc: null,
        draft: null,
      };
    }),
    saveConnectorCatalog: jest.fn(
      async (input: { connectors: typeof mockConnectorCatalog.connectors }) => {
        mockConnectorCatalog = {
          ...mockConnectorCatalog,
          connectors: input.connectors,
        };
        return mockConnectorCatalog;
      },
    ),
    importConnectorCatalog: jest.fn(async (file: File) => {
      mockConnectorCatalog = {
        ...mockConnectorCatalog,
        connectors: [
          {
            name: 'imported-search',
            type: 'http',
            enabled: true,
            timeoutMs: 15000,
            retry: 2,
            http: {
              baseUrl: 'https://imported.example.test',
              allowedMethods: ['POST'],
              allowedPaths: ['/catalog'],
              allowedInputKeys: ['query'],
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
          },
        ],
      };
      return {
        ...mockConnectorCatalog,
        sourceFilePath: file.name,
        sourceFileExists: true,
        importedCount: mockConnectorCatalog.connectors.length,
      };
    }),
    getRoleCatalog: jest.fn(async () => ({
      ...mockRoleCatalog,
    })),
    getRoleDraft: jest.fn(async () => ({
      ...mockRoleDraftResponse,
    })),
    saveRoleDraft: jest.fn(async (input: { draft: any }) => {
      mockRoleDraftResponse = {
        ...mockRoleDraftResponse,
        fileExists: true,
        updatedAtUtc: '2026-03-18T00:03:00Z',
        draft: input.draft,
      };
      return mockRoleDraftResponse;
    }),
    deleteRoleDraft: jest.fn(async () => {
      mockRoleDraftResponse = {
        ...mockRoleDraftResponse,
        fileExists: false,
        updatedAtUtc: null,
        draft: null,
      };
    }),
    saveRoleCatalog: jest.fn(
      async (input: { roles: typeof mockRoleCatalog.roles }) => {
        mockRoleCatalog = {
          ...mockRoleCatalog,
          roles: input.roles,
        };
        return mockRoleCatalog;
      },
    ),
    importRoleCatalog: jest.fn(async (file: File) => {
      mockRoleCatalog = {
        ...mockRoleCatalog,
        roles: [
          {
            id: 'reviewer',
            name: 'Reviewer',
            systemPrompt: 'Review imported workflow outputs carefully.',
            provider: 'tornado',
            model: 'gpt-review',
            connectors: ['imported-search'],
          },
        ],
      };
      return {
        ...mockRoleCatalog,
        sourceFilePath: file.name,
        sourceFileExists: true,
        importedCount: mockRoleCatalog.roles.length,
      };
    }),
    addWorkflowDirectory: jest.fn(async () => ({
      runtimeBaseUrl: 'https://aevatar-console-backend-api.aevatar.ai',
      directories: [
        {
          directoryId: 'dir-1',
          label: 'Workspace',
          path: '/tmp/workflows',
          isBuiltIn: false,
        },
      ],
    })),
    removeWorkflowDirectory: jest.fn(async () => undefined),
    authorWorkflow: jest.fn(mockAuthorWorkflowSuccess),
  },
}));

jest.mock('@/shared/studio/scriptsApi', () => ({
  scriptsApi: {
    listScripts: jest.fn(async () => []),
    listRuntimes: jest.fn(async () => []),
    getScriptCatalog: jest.fn(async () => ({
      scriptId: 'script-1',
      activeRevision: 'rev-1',
      activeDefinitionActorId: 'definition-1',
      activeSourceHash: 'hash-1',
      previousRevision: '',
      revisionHistory: ['rev-1'],
      lastProposalId: '',
      catalogActorId: 'catalog-1',
      scopeId: 'scope-1',
      updatedAt: '2026-03-18T00:00:00Z',
    })),
    getEvolutionDecision: jest.fn(async () => ({
      accepted: true,
      proposalId: 'proposal-1',
      scriptId: 'script-1',
      baseRevision: 'rev-1',
      candidateRevision: 'rev-2',
      status: 'accepted',
      failureReason: '',
      definitionActorId: 'definition-1',
      catalogActorId: 'catalog-1',
      validationReport: {
        isSuccess: true,
        diagnostics: [],
      },
    })),
    getRuntimeActivity: jest.fn(async () => ({
      actorId: 'runtime-1',
      scriptId: 'script-1',
      definitionActorId: 'definition-1',
      revision: 'rev-1',
      input: '',
      output: '',
      status: 'ok',
      lastCommandId: '',
      notes: [],
      stateVersion: 1,
      lastEventId: 'event-1',
      updatedAt: '2026-03-18T00:00:00Z',
    })),
    validateDraft: jest.fn(async () => ({
      success: true,
      scriptId: 'script-1',
      scriptRevision: 'draft-1',
      primarySourcePath: 'Behavior.cs',
      errorCount: 0,
      warningCount: 0,
      diagnostics: [],
    })),
    saveScript: jest.fn(),
    observeSaveScript: jest.fn(async () => ({
      scopeId: 'scope-1',
      scriptId: 'script-1',
      status: 'applied',
      message: 'applied',
      currentScript: null,
      isTerminal: true,
    })),
    runDraftScript: jest.fn(),
    proposeEvolution: jest.fn(),
    generateScript: jest.fn(),
  },
}));

jest.mock('./components/StudioBootstrapGate', () => ({
  __esModule: true,
  default: ({ children }: MockChildrenProps) => children,
}));

jest.mock('./components/StudioBuildPanels', () => {
  const mockReact = require('react');
  const StudioWorkflowBuildPanel = (props: any) => {
    mockLastWorkflowBuildPanelProps = props;
    const [detailsMode, setDetailsMode] = mockReact.useState('step');
    const [addStepType, setAddStepType] = mockReact.useState(
      props.availableStepTypes?.[0] || 'llm_call',
    );
    const selectedStep = mockReact.useMemo(() => {
      const selectedStepId = String(props.selectedGraphNodeId || '').replace(
        /^step:/,
        '',
      );
      return (
        props.workflowGraph?.steps?.find(
          (step: any) => step.id === selectedStepId,
        ) ||
        props.workflowGraph?.steps?.[0] ||
        null
      );
    }, [props.selectedGraphNodeId, props.workflowGraph?.steps]);
    const selectedStepDraftSeed = mockReact.useMemo(
      () => ({
        kind: 'step',
        capability: selectedStep?.capability ?? null,
        id: selectedStep?.id || '',
        type: selectedStep?.type || 'llm_call',
        targetRole: selectedStep?.targetRole || '',
        next: selectedStep?.next || '',
        parametersText: JSON.stringify(selectedStep?.parameters || {}, null, 2),
        branchesText: JSON.stringify(selectedStep?.branches || {}, null, 2),
      }),
      [
        selectedStep?.id,
        selectedStep?.capability,
        selectedStep?.type,
        selectedStep?.targetRole,
        selectedStep?.next,
        JSON.stringify(selectedStep?.parameters || {}),
        JSON.stringify(selectedStep?.branches || {}),
      ],
    );
    const [stepDraft, setStepDraft] = mockReact.useState(
      () => selectedStepDraftSeed,
    );
    const stepDraftRef = mockReact.useRef(stepDraft);

    const updateStepDraft = mockReact.useCallback((updater: any) => {
      const nextDraft =
        typeof updater === 'function' ? updater(stepDraftRef.current) : updater;
      stepDraftRef.current = nextDraft;
      setStepDraft(nextDraft);
    }, []);

    mockReact.useEffect(() => {
      updateStepDraft(selectedStepDraftSeed);
    }, [selectedStepDraftSeed]);

    return mockReact.createElement(
      'div',
      { 'data-testid': 'studio-workflow-build-panel' },
      [
        mockReact.createElement('div', { key: 'eyebrow' }, 'DAG Canvas'),
        mockReact.createElement('div', { key: 'provenance' }, 'canvas · live'),
        mockReact.createElement(
          'select',
          {
            key: 'add-step-type',
            'aria-label': 'Add step type',
            value: addStepType,
            onChange: (event: MockValueEvent) =>
              setAddStepType(event.target.value),
          },
          (props.availableStepTypes || ['llm_call']).map((stepType: string) =>
            mockReact.createElement(
              'option',
              { key: stepType, value: stepType },
              stepType,
            ),
          ),
        ),
        mockReact.createElement(
          'button',
          {
            key: 'add-step',
            type: 'button',
            onClick: () => props.onInsertStep?.(addStepType),
          },
          'Add step',
        ),
        mockReact.createElement(
          'button',
          {
            key: 'auto-layout',
            type: 'button',
            onClick: () => props.onAutoLayout?.(),
          },
          'Auto-layout',
        ),
        mockReact.createElement(
          'button',
          {
            key: 'yaml-toggle',
            type: 'button',
            onClick: () =>
              setDetailsMode((current: string) =>
                current === 'yaml' ? 'step' : 'yaml',
              ),
          },
          'YAML',
        ),
        mockReact.createElement(
          'div',
          { key: 'node-count', 'data-testid': 'workflow-graph-node-count' },
          String(props.workflowGraph?.nodes?.length ?? 0),
        ),
        mockReact.createElement(
          'div',
          { key: 'graph-steps', 'data-testid': 'mock-workflow-graph-steps' },
          (props.workflowGraph?.steps || []).map((step: any) =>
            mockReact.createElement(
              'button',
              {
                key: `graph-step-${step.id}`,
                type: 'button',
                onClick: () => props.onSelectGraphNode?.(`step:${step.id}`),
              },
              step.id,
            ),
          ),
        ),
        mockReact.createElement(
          'button',
          {
            key: 'canvas-delete-selected-step',
            type: 'button',
            onClick: () =>
              props.onDeleteWorkflowNodes?.(
                props.selectedGraphNodeId ? [props.selectedGraphNodeId] : [],
              ),
          },
          'Delete selected step on canvas',
        ),
        detailsMode === 'yaml'
          ? mockReact.createElement(
              'div',
              { key: 'yaml-title' },
              'Workflow YAML',
            )
          : mockReact.createElement(
              'div',
              { key: 'step-title' },
              'Step Detail',
            ),
        detailsMode === 'step'
          ? mockReact.createElement(
              mockReact.Fragment,
              { key: 'step-form' },
              mockReact.createElement('input', {
                'aria-label': 'Step ID',
                value: stepDraft.id,
                onChange: (event: MockValueEvent) =>
                  updateStepDraft((current: any) => ({
                    ...current,
                    id: event.target.value,
                  })),
              }),
              mockReact.createElement(
                'select',
                {
                  'aria-label': 'Step type',
                  value: stepDraft.type,
                  onChange: (event: MockValueEvent) =>
                    updateStepDraft((current: any) => ({
                      ...current,
                      type: event.target.value,
                    })),
                },
                (props.availableStepTypes || ['llm_call']).map(
                  (stepType: string) =>
                    mockReact.createElement(
                      'option',
                      { key: stepType, value: stepType },
                      stepType,
                    ),
                ),
              ),
              mockReact.createElement('input', {
                'aria-label': 'Target role',
                value: stepDraft.targetRole,
                onChange: (event: MockValueEvent) =>
                  updateStepDraft((current: any) => ({
                    ...current,
                    targetRole: event.target.value,
                  })),
              }),
              mockReact.createElement('input', {
                'aria-label': 'Next step',
                value: stepDraft.next,
                onChange: (event: MockValueEvent) =>
                  updateStepDraft((current: any) => ({
                    ...current,
                    next: event.target.value,
                  })),
              }),
              mockReact.createElement('textarea', {
                'aria-label': 'Step parameters',
                value: stepDraft.parametersText,
                onChange: (event: MockValueEvent) =>
                  updateStepDraft((current: any) => ({
                    ...current,
                    parametersText: event.target.value,
                  })),
              }),
              mockReact.createElement('textarea', {
                'aria-label': 'Step branches',
                value: stepDraft.branchesText,
                onChange: (event: MockValueEvent) =>
                  updateStepDraft((current: any) => ({
                    ...current,
                    branchesText: event.target.value,
                  })),
              }),
              mockReact.createElement(
                'button',
                {
                  type: 'button',
                  onClick: () => props.onApplyStepDraft?.(stepDraft),
                },
                'Apply changes',
              ),
              mockReact.createElement(
                'button',
                {
                  type: 'button',
                  onClick: () => props.onRemoveSelectedStep?.(),
                },
                'Delete step',
              ),
            )
          : null,
        props.saveNotice
          ? mockReact.createElement(
              'div',
              { key: 'save-notice' },
              props.saveNotice.message,
            )
          : null,
        mockReact.createElement('textarea', {
          key: 'yaml',
          'aria-label': '定义 YAML',
          value: props.draftYaml ?? '',
          onChange: (event: MockValueEvent) =>
            props.onSetDraftYaml?.(event.target.value),
        }),
        mockReact.createElement(
          'div',
          { key: 'dry-run-title' },
          'Workflow draft run',
        ),
        mockReact.createElement(
          'div',
          { key: 'dry-run-route', 'data-testid': 'workflow-dry-run-route' },
          props.dryRunRouteLabel || '',
        ),
        props.dryRunBlockedReason
          ? mockReact.createElement(
              'div',
              { key: 'dry-run-blocked', role: 'alert' },
              props.dryRunBlockedReason,
            )
          : null,
        mockReact.createElement('textarea', {
          key: 'run-input',
          'aria-label': 'Workflow dry run input',
          value: props.runPrompt ?? '',
          onChange: (event: MockValueEvent) =>
            props.onRunPromptChange?.(event.target.value),
        }),
        mockReact.createElement(
          'button',
          {
            key: 'save',
            type: 'button',
            disabled: !props.canSaveWorkflow,
            onClick: () => {
              const currentParametersText =
                (
                  globalThis.document.querySelector(
                    'textarea[aria-label="Step parameters"]',
                  ) as HTMLTextAreaElement | null
                )?.value ?? stepDraftRef.current.parametersText;
              const currentBranchesText =
                (
                  globalThis.document.querySelector(
                    'textarea[aria-label="Step branches"]',
                  ) as HTMLTextAreaElement | null
                )?.value ?? stepDraftRef.current.branchesText;
              const currentStepDraft = {
                ...stepDraftRef.current,
                parametersText: currentParametersText,
                branchesText: currentBranchesText,
              };
              const currentHasPendingStepDraft =
                currentStepDraft.id !== selectedStepDraftSeed.id ||
                currentStepDraft.type !== selectedStepDraftSeed.type ||
                currentStepDraft.targetRole !==
                  selectedStepDraftSeed.targetRole ||
                currentStepDraft.next !== selectedStepDraftSeed.next ||
                currentStepDraft.parametersText !==
                  selectedStepDraftSeed.parametersText ||
                currentStepDraft.branchesText !==
                  selectedStepDraftSeed.branchesText;
              props.onSaveDraft?.(
                currentHasPendingStepDraft
                  ? {
                      stepId: selectedStep?.id || '',
                      draft: currentStepDraft,
                    }
                  : null,
              );
            },
          },
          'Save draft',
        ),
        mockReact.createElement(
          'button',
          {
            key: 'bind',
            type: 'button',
            onClick: () =>
              props.onContinueToBind?.({
                agentKind: props.selectedAgentKind || 'Tests.OrdersGAgent',
                displayName: props.currentMemberLabel || 'orders-gagent',
                initialPrompt: 'You are the team member gagent.',
                persistenceMode: 'grain',
                role: 'intake-classifier',
                tools: ['classify_intent', 'detect_language'],
              }),
          },
          'Continue to Bind',
        ),
      ],
    );
  };

  const StudioScriptBuildPanel = (props: any) => {
    const [value, setValue] = mockReact.useState('using System;');
    const [dirty, setDirty] = mockReact.useState(false);
    const selectedScriptId = props.selectedScriptId || '';

    mockReact.useEffect(() => {
      props.onRegisterLeaveGuard?.(
        dirty ? jest.fn(async () => false) : jest.fn(async () => true),
      );

      return () => props.onRegisterLeaveGuard?.(null);
    }, [dirty, props.onRegisterLeaveGuard]);

    mockReact.useEffect(() => {
      if (!selectedScriptId) {
        props.onScriptBuildStateChange?.(null);
        return () => props.onScriptBuildStateChange?.(null);
      }

      props.onScriptBuildStateChange?.({
        scriptId: selectedScriptId,
        displayName: selectedScriptId,
        scriptRevision: 'rev-1',
        revisionId: 'rev-1',
        sourceHash: 'hash-1',
        definitionActorId: 'definition-1',
        dirty,
        validationStatus: dirty ? 'unknown' : 'valid',
        saveStatus: dirty ? 'idle' : 'applied',
      });
      return () => props.onScriptBuildStateChange?.(null);
    }, [dirty, props.onScriptBuildStateChange, selectedScriptId]);

    if (!selectedScriptId) {
      return mockReact.createElement(
        'div',
        { 'data-testid': 'studio-script-build-panel' },
        [
          mockReact.createElement('div', { key: 'title' }, 'Script source'),
          mockReact.createElement(
            'p',
            { key: 'empty-copy' },
            'No script is selected yet. Start a script draft to open the editor.',
          ),
          mockReact.createElement(
            'button',
            {
              key: 'add-script',
              type: 'button',
              onClick: () => props.onCreateScriptDraft?.(),
            },
            'Add script',
          ),
        ],
      );
    }

    return mockReact.createElement(
      'div',
      { 'data-testid': 'studio-script-build-panel' },
      [
        mockReact.createElement('div', { key: 'title' }, 'Script source'),
        mockReact.createElement(
          'div',
          { key: 'provenance' },
          'lints · partial',
        ),
        mockReact.createElement('input', {
          key: 'script-id',
          'aria-label': 'Script ID',
          value: selectedScriptId,
          onChange: (event: MockValueEvent) =>
            props.onSelectScriptId?.(event.target.value),
        }),
        mockReact.createElement('textarea', {
          key: 'editor',
          'aria-label': 'Script source editor',
          value,
          onChange: (event: MockValueEvent) => {
            setValue(event.target.value);
            setDirty(true);
          },
        }),
        mockReact.createElement(
          'div',
          { key: 'dry-run-title' },
          'Script draft run',
        ),
        mockReact.createElement('textarea', {
          key: 'run-input',
          'aria-label': 'Script dry run input',
          value: '{\n  "input": "fixture"\n}',
          readOnly: true,
        }),
        mockReact.createElement(
          'button',
          {
            key: 'save',
            type: 'button',
          },
          'Save draft',
        ),
        mockReact.createElement(
          'button',
          {
            key: 'bind',
            type: 'button',
            onClick: () => props.onContinueToBind?.(),
          },
          'Continue to Bind',
        ),
      ],
    );
  };

  const StudioGAgentBuildPanel = (props: any) => {
    mockReact.useEffect(() => {
      props.onBuildStateChange?.({
        agentKind: props.selectedAgentKind || 'Tests.OrdersGAgent',
        displayName: props.currentMemberLabel || 'orders-gagent',
        initialPrompt: 'You are the team member gagent.',
        persistenceMode: 'grain',
        role: 'intake-classifier',
        tools: ['classify_intent', 'detect_language'],
      });
    }, [
      props.currentMemberLabel,
      props.onBuildStateChange,
      props.selectedAgentKind,
    ]);

    return mockReact.createElement(
      'div',
      { 'data-testid': 'studio-gagent-build-panel' },
      [
        mockReact.createElement('div', { key: 'title' }, 'GAgent definition'),
        mockReact.createElement(
          'div',
          { key: 'provenance' },
          'template · seeded',
        ),
        mockReact.createElement('input', {
          key: 'type',
          'aria-label': 'GAgent type',
          value: props.selectedAgentKind || '',
          onChange: (event: MockValueEvent) =>
            props.onSelectAgentKind?.(event.target.value),
        }),
        mockReact.createElement('input', {
          key: 'display-name',
          'aria-label': 'Display name',
          defaultValue: 'orders-gagent',
        }),
        mockReact.createElement('input', {
          key: 'role',
          'aria-label': 'Role',
          defaultValue: 'intake-classifier',
        }),
        mockReact.createElement('textarea', {
          key: 'prompt',
          'aria-label': 'Initial prompt',
          defaultValue: 'You are the team member gagent.',
        }),
        mockReact.createElement('input', {
          key: 'tools',
          'aria-label': 'Tools',
          defaultValue: 'classify_intent, detect_language',
        }),
        mockReact.createElement('label', { key: 'grain' }, [
          mockReact.createElement('input', {
            key: 'grain-input',
            type: 'radio',
            name: 'gagent-persistence',
            defaultChecked: true,
          }),
          'Orleans grain',
        ]),
        mockReact.createElement('label', { key: 'ephemeral' }, [
          mockReact.createElement('input', {
            key: 'ephemeral-input',
            type: 'radio',
            name: 'gagent-persistence',
          }),
          'Ephemeral',
        ]),
        mockReact.createElement(
          'button',
          {
            key: 'bind',
            type: 'button',
            onClick: () => props.onContinueToBind?.(),
          },
          'Continue to Bind',
        ),
      ],
    );
  };

  return {
    __esModule: true,
    getDefaultBuildModeCards: (scriptsEnabled: boolean) => [
      {
        key: 'workflow',
        label: 'Workflow',
        description: 'Workflow description',
        hint: 'When · Multiple agents hand off predictably',
        disabled: false,
      },
      {
        key: 'script',
        label: 'Script',
        description: 'Script description',
        hint: scriptsEnabled
          ? 'When · You need code-level control'
          : '当前环境暂未启用脚本能力。',
        disabled: !scriptsEnabled,
      },
      {
        key: 'gagent',
        label: 'GAgent',
        description: 'GAgent description',
        hint: 'When · State lives with one agent',
        disabled: false,
      },
    ],
    StudioWorkflowBuildPanel,
    StudioScriptBuildPanel,
    StudioGAgentBuildPanel,
  };
});

jest.mock('./components/StudioShell', () => ({
  __esModule: true,
  default: ({
    alerts,
    children,
    contextBar,
    inventoryActions,
    lifecycleSteps = [],
    members = [],
    navItems = [],
    onSelectLifecycleStep,
    onSelectMember,
    onSelectPage,
    selectedMemberKey,
    showLifecycle = true,
    showMemberRail = true,
  }: any) => {
    const React = require('react');
    const filterOptions = [
      'all',
      ...Array.from(
        new Set(
          (members as any[]).map((member) => String(member.kind || 'unknown')),
        ),
      ),
    ];
    const filterLabels: Record<string, string> = {
      all: 'All',
      member: 'Member',
      workflow: 'Workflow',
      script: 'Script',
      gagent: 'GAgent',
      unknown: 'Unknown',
    };
    return React.createElement('div', null, [
      React.createElement('div', { key: 'workbench' }, 'Workbench'),
      contextBar
        ? React.createElement('div', { key: 'context-bar' }, contextBar)
        : null,
      alerts ? React.createElement('div', { key: 'alerts' }, alerts) : null,
      showMemberRail
        ? React.createElement(
            'div',
            { key: 'members', 'aria-label': 'Team members' },
            [
              React.createElement(
                'div',
                { key: 'member-filters' },
                filterOptions.map((key) =>
                  React.createElement(
                    'button',
                    {
                      key: `filter-${key}`,
                      type: 'button',
                    },
                    filterLabels[key] || key,
                  ),
                ),
              ),
              inventoryActions
                ? React.createElement(
                    'div',
                    { key: 'inventory-actions' },
                    inventoryActions,
                  )
                : null,
              ...members.map((member: any) =>
                React.createElement(
                  'div',
                  { key: `member-row-${member.key}` },
                  [
                    React.createElement(
                      'button',
                      {
                        key: `member-${member.key}`,
                        type: 'button',
                        'aria-current':
                          selectedMemberKey === member.key ? 'true' : undefined,
                        onClick: () => onSelectMember?.(member.key),
                      },
                      member.label,
                    ),
                  ],
                ),
              ),
            ],
          )
        : null,
      ...(showLifecycle ? lifecycleSteps : []).map((step: any) =>
        React.createElement(
          'button',
          {
            key: `step-${step.key}`,
            type: 'button',
            disabled: Boolean(step.disabled),
            onClick: () => onSelectLifecycleStep?.(step.key),
          },
          step.label,
        ),
      ),
      ...navItems.map((item: any) =>
        React.createElement(
          'button',
          {
            key: item.key,
            type: 'button',
            onClick: () => onSelectPage?.(item.key),
          },
          item.label,
        ),
      ),
      children,
    ]);
  },
}));

jest.mock('../scopes/components/ScopeServiceRuntimeWorkbench', () => ({
  __esModule: true,
  default: (props: any) => {
    const React = require('react');
    return React.createElement('div', null, [
      React.createElement(
        'div',
        { key: 'title', 'data-testid': 'studio-bind-surface' },
        'Runtime Workbench Mock',
      ),
      React.createElement(
        'div',
        { key: 'service' },
        props.initialServiceId || props.preferredServiceId || 'no-service',
      ),
      React.createElement(
        'button',
        {
          key: 'use-endpoint',
          type: 'button',
          onClick: () => props.onUseEndpoint?.('default', 'chat'),
        },
        'Use runtime endpoint',
      ),
    ]);
  },
}));

jest.mock('./components/bind/StudioMemberBindPanel', () => ({
  __esModule: true,
  default: (props: any) => {
    const React = require('react');
    const selectedServiceId =
      props.initialServiceId || props.preferredServiceId || '';
    const selectedService = Array.isArray(props.services)
      ? props.services.find(
          (service: any) => service.serviceId === selectedServiceId,
        )
      : null;
    const selectedEndpoint = selectedService?.endpoints?.[0] ?? null;
    const canContinue = Boolean(
      props.memberId && selectedService && selectedEndpoint,
    );
    return React.createElement('div', null, [
      React.createElement(
        'div',
        { key: 'title', 'data-testid': 'studio-bind-surface' },
        'Bind Surface Mock',
      ),
      React.createElement(
        'div',
        { key: 'service' },
        `service:${selectedServiceId || 'no-service'}`,
      ),
      React.createElement(
        'div',
        { key: 'services' },
        `services:${
          Array.isArray(props.services) && props.services.length > 0
            ? props.services.map((service: any) => service.serviceId).join(',')
            : 'none'
        }`,
      ),
      React.createElement(
        'div',
        { key: 'candidate' },
        props.pendingBindingCandidate
          ? `candidate:${props.pendingBindingCandidate.displayName}`
          : 'candidate:none',
      ),
      React.createElement(
        'div',
        { key: 'workflow-yamls' },
        `workflow-yamls:${props.buildWorkflowYamls ? 'present' : 'none'}`,
      ),
      React.createElement(
        'div',
        { key: 'member' },
        `member:${props.memberId || 'no-member'}`,
      ),
      !props.memberId
        ? React.createElement(
            'div',
            { key: 'member-warning' },
            'Select a Team member before using Invoke.',
          )
        : null,
      React.createElement(
        'button',
        {
          key: 'select-endpoint',
          type: 'button',
          onClick: () =>
            props.onSelectionChange?.({
              serviceId: 'default',
              endpointId: 'support-chat',
            }),
        },
        'Select bind endpoint',
      ),
      React.createElement(
        'button',
        {
          key: 'bind-candidate',
          type: 'button',
          onClick: () => void props.onBindPendingCandidate?.(),
        },
        'Bind current member',
      ),
      React.createElement(
        'button',
        {
          key: 'continue',
          type: 'button',
          disabled: !canContinue,
          onClick: () => {
            if (canContinue) {
              props.onContinueToInvoke?.(
                selectedService.serviceId,
                selectedEndpoint.endpointId,
              );
            }
          },
        },
        'Continue to Invoke',
      ),
    ]);
  },
}));

jest.mock('./components/StudioMemberInvokePanel', () => ({
  __esModule: true,
  default: (props: any) => {
    const React = require('react');
    return React.createElement('div', null, [
      React.createElement(
        'div',
        { key: 'title', 'data-testid': 'studio-invoke-surface' },
        'Invoke Surface Mock',
      ),
      React.createElement(
        'div',
        { key: 'service' },
        `service:${props.initialServiceId || 'no-service'}`,
      ),
      React.createElement(
        'div',
        { key: 'member' },
        `member:${props.selectedMemberLabel || 'no-member'}`,
      ),
      React.createElement(
        'div',
        { key: 'services' },
        `services:${
          Array.isArray(props.services) && props.services.length > 0
            ? props.services.map((service: any) => service.serviceId).join(',')
            : 'none'
        }`,
      ),
      React.createElement(
        'div',
        { key: 'endpoint' },
        `endpoint:${props.initialEndpointId || 'no-endpoint'}`,
      ),
      React.createElement(
        'button',
        {
          key: 'emit-observe-session',
          type: 'button',
          onClick: () => {
            const now = Date.now();
            const startedAtUtc = new Date(now - 1000).toISOString();
            const completedAtUtc = new Date(now).toISOString();
            props.onObserveSessionChange?.({
              actorId: 'actor-invoke',
              assistantText: 'Observed output',
              commandId: 'command-invoke',
              completedAtUtc,
              endpointId: props.initialEndpointId || 'chat',
              error: '',
              events: [
                {
                  name: 'aevatar.run.context',
                  timestamp: now - 1000,
                  type: 'CUSTOM',
                  value: {
                    actorId: 'actor-invoke',
                    commandId: 'command-invoke',
                  },
                },
                {
                  result: 'Observed output',
                  runId: 'invoke-run-1',
                  threadId: 'actor-invoke',
                  timestamp: now,
                  type: 'RUN_FINISHED',
                },
              ],
              finalOutput: 'Observed output',
              mode: 'stream',
              payloadBase64: '',
              payloadTypeUrl: '',
              prompt: 'Observe this invoke result.',
              runId: 'invoke-run-1',
              serviceId: props.initialServiceId || 'default',
              serviceLabel: props.selectedMemberLabel || 'workspace-demo',
              startedAtUtc,
              status: 'success',
            });
          },
        },
        'Emit Observe Session',
      ),
      props.emptyState
        ? React.createElement(
            'div',
            { key: 'empty' },
            `empty:${props.emptyState.message}`,
          )
        : null,
    ]);
  },
}));

jest.mock('./components/StudioFilesPage', () => ({
  __esModule: true,
  default: (props: any) => {
    const React = require('react');
    return React.createElement('div', null, [
      React.createElement('h2', { key: 'title' }, 'Files'),
      React.createElement(
        'div',
        { key: 'scope' },
        props.scopeId || 'workspace',
      ),
      React.createElement(
        'button',
        {
          key: 'settings',
          type: 'button',
          onClick: () => props.onOpenSettings?.(),
        },
        'Open Settings',
      ),
    ]);
  },
}));

jest.mock('./components/StudioWorkbenchSections', () => {
  const React = require('react');

  const dedupeStudioWorkflowSummaries = (workflows: readonly any[]) => {
    const deduped = new Map<string, any>();

    const readTimestamp = (value: string) => {
      const timestamp = Date.parse(value);
      return Number.isFinite(timestamp) ? timestamp : 0;
    };

    const comparePriority = (left: any, right: any) => {
      const updatedDelta =
        readTimestamp(right.updatedAtUtc) - readTimestamp(left.updatedAtUtc);
      if (updatedDelta !== 0) {
        return updatedDelta;
      }

      if (left.stepCount !== right.stepCount) {
        return right.stepCount - left.stepCount;
      }

      return String(left.workflowId ?? '').localeCompare(
        String(right.workflowId ?? ''),
      );
    };

    for (const workflow of workflows) {
      const key =
        String(workflow.name ?? '')
          .trim()
          .toLowerCase() ||
        String(workflow.workflowId ?? '')
          .trim()
          .toLowerCase();
      const current = deduped.get(key);
      if (!current || comparePriority(workflow, current) < 0) {
        deduped.set(key, workflow);
      }
    }

    return Array.from(deduped.values()).sort(comparePriority);
  };

  const renderNoticeTitle = (
    key: string,
    notice: MockNotice,
    successTitle: string,
    errorTitle: string,
  ) => {
    if (!notice) {
      return null;
    }

    return React.createElement(
      'div',
      { key },
      notice.type === 'error' ? errorTitle : successTitle,
    );
  };

  const StudioWorkflowsPage = (props: any) =>
    React.createElement('div', null, [
      React.createElement('h2', { key: 'title' }, '行为定义'),
      React.createElement('div', { key: 'draft' }, '当前定义'),
      React.createElement(
        'button',
        {
          key: 'open-editor',
          type: 'button',
          disabled: !props.activeWorkflowSourceKey,
          onClick: () => props.onOpenCurrentDraft?.(),
        },
        '进入编辑',
      ),
      React.createElement('input', {
        key: 'search',
        placeholder: '搜索定义',
        value: props.workflowSearch ?? '',
        onChange: (event: MockValueEvent) =>
          props.onSetWorkflowSearch?.(event.target.value),
      }),
      ...(props.workflows.data ?? []).map((workflow: any) =>
        React.createElement(
          'button',
          {
            key: workflow.workflowId,
            type: 'button',
            onClick: () => props.onOpenWorkflow?.(workflow.workflowId),
          },
          workflow.name,
        ),
      ),
      React.createElement(
        'button',
        {
          key: 'blank',
          type: 'button',
          onClick: () => props.onStartBlankDraft?.(),
        },
        '新建定义',
      ),
    ]);

  const StudioEditorPage = (props: any) => {
    const [askAiOpen, setAskAiOpen] = React.useState(false);
    const title = props.teamCreation?.teamName
      ? `创建团队：${props.teamCreation.teamName}`
      : props.draftMode === 'new'
        ? '新建草稿'
        : props.templateWorkflowName
          ? '模板定义'
          : '当前定义';
    const publishLabel = props.teamCreation
      ? '发布团队入口'
      : props.scopeBinding?.available
        ? '更新团队入口'
        : '绑定团队入口';

    return React.createElement(
      'div',
      null,
      [
        React.createElement('div', { key: 'title' }, title),
        React.createElement('div', { key: 'graph-title' }, '行为画布'),
        React.createElement(
          'div',
          {
            key: 'graph-count',
            'data-testid': 'workflow-graph-node-count',
          },
          String(props.workflowGraph?.nodes?.length ?? 0),
        ),
        renderNoticeTitle(
          'save-notice',
          props.saveNotice,
          '定义已保存',
          '定义保存失败',
        ),
        React.createElement(
          'div',
          {
            key: 'run-prompt-state',
            'data-testid': 'studio-run-prompt-state',
          },
          props.runPrompt ?? '',
        ),
        renderNoticeTitle(
          'ask-ai-notice',
          props.askAiNotice,
          'AI 已更新当前草稿',
          'AI 生成失败',
        ),
        props.askAiNotice
          ? React.createElement(
              'div',
              { key: 'ask-ai-notice-message' },
              props.askAiNotice.message,
            )
          : null,
        React.createElement('input', {
          key: 'workflow-name',
          'aria-label': '定义名称',
          value: props.draftWorkflowName ?? '',
          onChange: (event: MockValueEvent) =>
            props.onSetDraftWorkflowName?.(event.target.value),
        }),
        React.createElement('textarea', {
          key: 'workflow-yaml',
          'aria-label': '定义 YAML',
          value: props.draftYaml ?? '',
          onChange: (event: MockValueEvent) =>
            props.onSetDraftYaml?.(event.target.value),
        }),
        React.createElement(
          'button',
          {
            key: 'save',
            type: 'button',
            onClick: () => props.onSaveDraft?.(),
          },
          '保存定义',
        ),
        React.createElement(
          'button',
          {
            key: 'clear-directory',
            type: 'button',
            onClick: () => props.onSetDraftDirectoryId?.(''),
          },
          '清空目录',
        ),
        React.createElement(
          'button',
          {
            key: 'yaml',
            type: 'button',
            onClick: () => props.onSetInspectorTab?.('yaml'),
          },
          'YAML',
        ),
        props.inspectorTab === 'yaml'
          ? React.createElement('div', { key: 'yaml-panel' }, [
              React.createElement('div', { key: 'yaml-title' }, '已校验 YAML'),
              React.createElement('textarea', {
                key: 'yaml-view',
                'aria-label': '行为定义 YAML',
                readOnly: true,
                value: props.draftYaml ?? '',
              }),
            ])
          : null,
        React.createElement(
          'button',
          {
            key: 'publish',
            'data-testid': 'studio-publish-workflow-button',
            type: 'button',
            disabled: !props.resolvedScopeId || !props.canPublishWorkflow,
            onClick: () => props.onPublishWorkflow?.(),
          },
          publishLabel,
        ),
        props.scopeBinding?.available &&
        props.projectEntryReadyForCurrentWorkflow
          ? React.createElement(
              'button',
              {
                key: 'project-entry',
                type: 'button',
                onClick: () => props.onOpenProjectInvoke?.(),
              },
              '打开测试台',
            )
          : null,
        React.createElement(
          'button',
          {
            key: 'bind-gagent',
            type: 'button',
            disabled: !props.resolvedScopeId,
            onClick: () =>
              props.onBindGAgent?.({
                displayName: 'orders-gagent',
                agentKind: 'Tests.OrdersGAgent',
                endpointId: 'run',
                endpointDisplayName: 'Run',
                requestTypeUrl:
                  'type.googleapis.com/google.protobuf.StringValue',
                responseTypeUrl: 'type.googleapis.com/example.RunResult',
                description: 'Run the bound gagent.',
                prompt: 'Run the orders gagent',
              }),
          },
          '绑定团队入口',
        ),
        React.createElement(
          'button',
          {
            key: 'bind-gagent-runs',
            type: 'button',
            disabled: !props.resolvedScopeId,
            onClick: () =>
              props.onBindGAgent?.(
                {
                  displayName: 'orders-gagent',
                  agentKind: 'Tests.OrdersGAgent',
                  endpointId: 'run',
                  endpointDisplayName: 'Run',
                  requestTypeUrl:
                    'type.googleapis.com/google.protobuf.StringValue',
                  responseTypeUrl: 'type.googleapis.com/example.RunResult',
                  description: 'Run the bound gagent.',
                  prompt: 'Run the orders gagent',
                },
                { openRuns: true },
              ),
          },
          '绑定团队入口并打开测试运行',
        ),
        React.createElement(
          'button',
          {
            key: 'bind-gagent-chat-runs',
            type: 'button',
            disabled: !props.resolvedScopeId,
            onClick: () =>
              props.onBindGAgent?.(
                {
                  displayName: 'orders-gagent',
                  agentKind: 'Tests.OrdersGAgent',
                  endpoints: [
                    {
                      endpointId: 'run',
                      displayName: 'Run',
                      kind: 'command',
                      requestTypeUrl:
                        'type.googleapis.com/google.protobuf.StringValue',
                      responseTypeUrl: 'type.googleapis.com/example.RunResult',
                      description: 'Run the bound gagent.',
                    },
                    {
                      endpointId: 'support-chat',
                      displayName: 'Chat',
                      kind: 'chat',
                      requestTypeUrl: '',
                      responseTypeUrl: '',
                      description: 'Chat with the bound gagent.',
                    },
                  ],
                  openRunsEndpointId: 'support-chat',
                  prompt: 'Chat with the orders gagent',
                },
                { openRuns: true },
              ),
          },
          '绑定聊天入口并打开测试运行',
        ),
        React.createElement(
          'button',
          {
            key: 'open-runs',
            type: 'button',
            onClick: () => props.onRunInConsole?.(),
          },
          'Open runs',
        ),
        props.scopeBinding?.available
          ? React.createElement(
              'button',
              {
                key: 'activate-rev-1',
                type: 'button',
                onClick: () => props.onActivateBindingRevision?.('rev-1'),
              },
              'Activate rev-1',
            )
          : null,
        props.scopeBinding?.available
          ? React.createElement(
              'button',
              {
                key: 'retire-rev-1',
                type: 'button',
                onClick: () => props.onRetireBindingRevision?.('rev-1'),
              },
              'Retire rev-1',
            )
          : null,
        React.createElement(
          'button',
          {
            key: 'ask-ai-toggle',
            type: 'button',
            disabled: props.canAskAiGenerate === false,
            title: props.askAiUnavailableMessage ?? '',
            onClick: () => setAskAiOpen(true),
          },
          'Open Ask AI',
        ),
        askAiOpen
          ? React.createElement('div', { key: 'ask-ai-panel' }, [
              React.createElement('textarea', {
                key: 'ask-ai-prompt',
                'aria-label': 'Studio AI workflow prompt',
                value: props.askAiPrompt ?? '',
                onChange: (event: MockValueEvent) =>
                  props.onAskAiPromptChange?.(event.target.value),
              }),
              React.createElement(
                'button',
                {
                  key: 'ask-ai-submit',
                  type: 'button',
                  onClick: () => props.onAskAiGenerate?.(),
                },
                'Generate',
              ),
            ])
          : null,
      ].filter(Boolean),
    );
  };

  const StudioExecutionPage = (props: any) =>
    React.createElement(
      'div',
      null,
      [
        React.createElement('div', { key: 'logs' }, 'Logs'),
        React.createElement(
          'div',
          { key: 'member' },
          `observe-member:${props.selectedMemberLabel || 'no-member'}`,
        ),
        React.createElement(
          'div',
          { key: 'implementation' },
          `observe-implementation:${props.currentImplementationLabel || 'no-implementation'}`,
        ),
        React.createElement(
          'div',
          { key: 'runs' },
          `observe-runs:${
            Array.isArray(props.executions?.data) &&
            props.executions.data.length > 0
              ? props.executions.data
                  .map((item: any) => item.executionId)
                  .join(',')
              : 'none'
          }`,
        ),
        React.createElement(
          'div',
          { key: 'selected' },
          `observe-selected:${props.selectedExecution?.data?.executionId || 'none'}`,
        ),
        props.emptyState
          ? React.createElement(
              'div',
              { key: 'empty' },
              `observe-empty:${props.emptyState.title}`,
            )
          : null,
        renderNoticeTitle(
          'execution-notice',
          props.executionNotice,
          'Execution stop requested',
          'Execution stop failed',
        ),
        React.createElement(
          'button',
          {
            key: 'stop',
            type: 'button',
            onClick: () => props.onStopExecution?.(),
          },
          'Stop',
        ),
      ].filter(Boolean),
    );

  const StudioRolesPage = (props: any) => {
    const selectedRole =
      props.selectedRole ?? props.roleCatalogDraft?.[0] ?? null;
    return React.createElement(
      'div',
      null,
      [
        React.createElement('div', { key: 'label' }, 'Saved roles'),
        React.createElement(
          'button',
          {
            key: 'use',
            type: 'button',
            onClick: () => props.onApplyRoleToWorkflow?.(selectedRole?.key),
          },
          'Use',
        ),
        React.createElement(
          'button',
          {
            key: 'save',
            type: 'button',
            onClick: () => props.onSaveRoles?.(),
          },
          'Save',
        ),
      ].filter(Boolean),
    );
  };

  const StudioConnectorsPage = (_props: any) => {
    return React.createElement(
      'div',
      null,
      [React.createElement('div', { key: 'label' }, 'Connectors')].filter(
        Boolean,
      ),
    );
  };

  const StudioSettingsPage = (_props: any) =>
    React.createElement(
      'div',
      null,
      [
        React.createElement('div', { key: 'label' }, 'Provider settings'),
      ].filter(Boolean),
    );

  return {
    __esModule: true,
    dedupeStudioWorkflowSummaries,
    StudioConnectorsPage,
    StudioEditorPage,
    StudioExecutionPage,
    StudioRolesPage,
    StudioSettingsPage,
    StudioWorkspaceAlerts: () => null,
    StudioWorkflowsPage,
  };
});

function renderStudioPage(route = '/studio') {
  window.history.pushState({}, '', route);
  return renderWithQueryClient(React.createElement(StudioPage));
}

async function replaceStudioRoute(route: string) {
  await act(async () => {
    window.history.replaceState({}, '', route);
    window.dispatchEvent(
      new PopStateEvent('popstate', { state: window.history.state }),
    );
  });
}

describe('StudioPage', () => {
  afterEach(() => {
    cleanup();
    cleanupTestQueryClients();
  });

  beforeEach(() => {
    setLocale('en-US');
    window.history.pushState({}, '', '/studio');
    window.localStorage.clear();
    window.sessionStorage.clear();
    resetMockState();
    jest.clearAllMocks();
    mockEnsureActiveAuthSession.mockReset();
    mockEnsureActiveAuthSession.mockResolvedValue(null);
    mockRuntimeQueryApi.listPrimitives.mockResolvedValue([
      {
        name: 'llm_call',
        aliases: [],
        category: 'core',
        description: 'LLM call',
        parameters: [],
        exampleWorkflows: [],
      },
      {
        name: 'demo_template',
        aliases: ['render_template'],
        category: 'demo',
        description: 'Demo template primitive',
        parameters: [],
        exampleWorkflows: ['demo_template'],
      },
    ]);
    mockRuntimeGAgentApi.listKinds.mockResolvedValue([
      {
        agentKind: 'Tests.OrdersGAgent',
        displayName: 'Orders Assistant',
        diagnosticClrTypeName: 'Tests.OrdersGAgent, Tests',
        endpoints: [],
      },
    ]);
    mockRuntimeGAgentApi.listActors.mockResolvedValue([
      {
        agentKind: 'Tests.OrdersGAgent',
        actorIds: ['orders-gagent'],
      },
    ]);
    mockScopeRuntimeApi.listServices.mockReset();
    mockScopeRuntimeApi.listServices.mockResolvedValue([
      {
        serviceId: 'default',
        displayName: 'workspace-demo',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-default',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with the published workflow.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);
    mockScopeRuntimeApi.getServiceRevisions.mockReset();
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        mockBuildServiceRevisionCatalog({ serviceId }),
    );
    mockScopeRuntimeApi.listMemberRuns.mockReset();
    mockScopeRuntimeApi.listMemberRuns.mockImplementation(
      async (_scopeId: string, memberId: string) => ({
        scopeId: 'scope-1',
        serviceId: memberId,
        serviceKey: `scope-1:default:default:${memberId}`,
        displayName: 'workspace-demo',
        runs: [mockBuildServiceRunSummary({ serviceId: memberId })],
      }),
    );
    mockScopeRuntimeApi.listServiceRuns.mockReset();
    mockScopeRuntimeApi.listServiceRuns.mockImplementation(
      async (_scopeId: string, serviceId: string) => ({
        scopeId: 'scope-1',
        serviceId,
        serviceKey: `scope-1:default:default:${serviceId}`,
        displayName: 'workspace-demo',
        runs: [mockBuildServiceRunSummary({ serviceId })],
      }),
    );
    mockScopeRuntimeApi.getMemberRunAudit.mockReset();
    mockScopeRuntimeApi.getMemberRunAudit.mockImplementation(
      async (_scopeId: string, memberId: string, runId: string) =>
        mockBuildServiceRunAuditSnapshot({ serviceId: memberId, runId }),
    );
    mockScopeRuntimeApi.getServiceRunAudit.mockReset();
    mockScopeRuntimeApi.getServiceRunAudit.mockImplementation(
      async (_scopeId: string, serviceId: string, runId: string) =>
        mockBuildServiceRunAuditSnapshot({ serviceId, runId }),
    );
    mockRuntimeRunsApi.stop.mockReset();
    mockRuntimeRunsApi.stop.mockResolvedValue({
      accepted: true,
      runId: 'execution-1',
    });
    mockRuntimeRunsApi.streamTeamChat.mockReset();
    mockRuntimeRunsApi.streamTeamChat.mockResolvedValue({ ok: true });
    mockRuntimeRunsApi.resume.mockReset();
    mockRuntimeRunsApi.resume.mockResolvedValue({
      accepted: true,
      runId: 'execution-1',
    });
    mockRuntimeRunsApi.signal.mockReset();
    mockRuntimeRunsApi.signal.mockResolvedValue({
      accepted: true,
      runId: 'execution-1',
    });
    (studioApi.getAuthSession as jest.Mock).mockReset();
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue(
      mockCreateDefaultStudioAuthSession(),
    );
    (studioApi.getAppContext as jest.Mock).mockReset();
    (studioApi.getAppContext as jest.Mock).mockResolvedValue(
      mockCreateDefaultStudioAppContext(),
    );
    (studioApi.listMembers as jest.Mock).mockReset();
    (studioApi.listMembers as jest.Mock).mockImplementation(async () => ({
      scopeId: 'scope-1',
      members: mockStudioMembers,
      nextPageToken: null,
    }));
    (studioApi.listTeamMembers as jest.Mock).mockReset();
    (studioApi.listTeamMembers as jest.Mock).mockImplementation(
      async (_scopeId: string, teamId: string) => ({
        scopeId: 'scope-1',
        members: mockStudioMembers.filter((member) => member.teamId === teamId),
        nextPageToken: null,
      }),
    );
    (studioApi.getTeam as jest.Mock).mockReset();
    (studioApi.getTeam as jest.Mock).mockImplementation(
      async (_scopeId: string, teamId: string) =>
        mockCreateDefaultTeamSummary({
          scopeId: _scopeId,
          teamId,
          entryMemberId: 'workspace-demo',
        }),
    );
    (studioApi.setTeamEntryMember as jest.Mock).mockReset();
    (studioApi.setTeamEntryMember as jest.Mock).mockImplementation(
      async (_scopeId: string, _teamId: string, memberId: string) => ({
        ...mockCreateDefaultTeamSummary({ scopeId: _scopeId, teamId: _teamId }),
        scopeId: _scopeId,
        teamId: _teamId,
        entryMemberId: memberId,
      }),
    );
    (studioApi.clearTeamEntryMember as jest.Mock).mockReset();
    (studioApi.clearTeamEntryMember as jest.Mock).mockImplementation(
      async (_scopeId: string, _teamId: string) => ({
        ...mockCreateDefaultTeamSummary({ scopeId: _scopeId, teamId: _teamId }),
        scopeId: _scopeId,
        teamId: _teamId,
        entryMemberId: null,
      }),
    );
    (studioApi.getMember as jest.Mock).mockReset();
    (studioApi.getMember as jest.Mock).mockImplementation(
      async (_scopeId: string, memberId: string) => {
        const matchedMember =
          mockStudioMembers.find((member) => member.memberId === memberId) ??
          mockStudioMembers[0];
        return {
          summary: matchedMember,
          implementationRef:
            matchedMember?.implementationKind === 'workflow'
              ? {
                  implementationKind: 'workflow',
                  workflowId: matchedMember.displayName,
                  workflowRevision: matchedMember.lastBoundRevisionId,
                }
              : matchedMember?.implementationKind === 'script'
                ? {
                    implementationKind: 'script',
                    scriptId:
                      matchedMember.scriptId || matchedMember.displayName,
                    scriptRevision: matchedMember.lastBoundRevisionId,
                  }
                : {
                    implementationKind: 'gagent',
                    agentKind: matchedMember?.displayName || '',
                  },
          lastBinding: matchedMember?.lastBoundRevisionId
            ? {
                publishedServiceId: matchedMember.publishedServiceId,
                revisionId: matchedMember.lastBoundRevisionId,
                implementationKind: matchedMember.implementationKind,
                boundAt: matchedMember.updatedAt,
              }
            : null,
        };
      },
    );
    (studioApi.listWorkflows as jest.Mock).mockReset();
    (studioApi.listWorkflows as jest.Mock).mockResolvedValue(
      mockCreateDefaultWorkflowSummaries(),
    );
    (studioApi.getWorkflow as jest.Mock).mockReset();
    (studioApi.getWorkflow as jest.Mock).mockImplementation(
      async () => mockWorkflowFile,
    );
    (studioApi.authorWorkflow as jest.Mock).mockReset();
    (studioApi.authorWorkflow as jest.Mock).mockImplementation(
      mockAuthorWorkflowSuccess,
    );
    (studioApi.getScopeBinding as jest.Mock).mockReset();
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValue({
      available: true,
      scopeId: 'scope-1',
      serviceId: 'default',
      displayName: 'workspace-demo',
      serviceKey: 'scope-1:default:default:default',
      defaultServingRevisionId: 'rev-2',
      activeServingRevisionId: 'rev-2',
      deploymentId: 'dep-2',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-default',
      updatedAt: '2026-03-26T08:00:00Z',
      revisions: [
        {
          revisionId: 'rev-2',
          implementationKind: 'workflow',
          status: 'Published',
          artifactHash: 'hash-2',
          failureReason: '',
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: 'Active',
          deploymentId: 'dep-2',
        },
      ],
    });
    (studioApi.getMemberBindingRun as jest.Mock).mockReset();
    (studioApi.getMemberBindingRun as jest.Mock).mockImplementation(
      async (scopeId: string, memberId: string, bindingRunId: string) => ({
        bindingRunId,
        scopeId,
        memberId,
        status: 'succeeded',
        failure: null,
        updatedAt: '2026-04-27T08:15:01Z',
      }),
    );
    (studioApi.previewExplicitRequests as jest.Mock).mockReset();
    (studioApi.previewExplicitRequests as jest.Mock).mockImplementation(
      (input: { workflowId: string; revisionId: string }) => ({
        workflowId: input.workflowId,
        revisionId: input.revisionId,
        items: [],
      }),
    );
    (scriptsApi.listScripts as jest.Mock).mockReset();
    (scriptsApi.listScripts as jest.Mock).mockResolvedValue([]);
    (scriptsApi.observeSaveScript as jest.Mock).mockReset();
    (scriptsApi.observeSaveScript as jest.Mock).mockResolvedValue({
      scopeId: 'scope-1',
      scriptId: 'script-1',
      status: 'applied',
      message: 'applied',
      currentScript: null,
      isTerminal: true,
    });
  });

  it('loads workspace data and shows the workflow build workbench by default', async () => {
    renderStudioPage('/studio');

    await waitFor(() => {
      expect(studioApi.getAppContext).toHaveBeenCalled();
      expect(studioApi.getWorkspaceSettings).toHaveBeenCalled();
      expect(studioApi.listWorkflows).toHaveBeenCalled();
    });

    expect(await screen.findByTestId('studio-context-title')).toBeTruthy();
    expect(screen.getByText('Workbench')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId('studio-context-title')).toHaveTextContent(
        'workspace-demo',
      );
    });
    expect(screen.getByTestId('studio-context-meta')).toHaveTextContent(
      'workflow canvas',
    );
    expect(screen.getByTestId('studio-workflow-build-panel')).toBeTruthy();
    expect(screen.getByText('DAG Canvas')).toBeTruthy();
    expect(screen.getByText('Step Detail')).toBeTruthy();
    expect(screen.getByText('Workflow draft run')).toBeTruthy();
    expect(screen.queryByText('Workflow description')).toBeNull();
    expect(
      screen.queryByText(
        'The Build phase first determines which implementation method is used for the current member, and then directly completes authoring and dry-run in the same workbench.',
      ),
    ).toBeNull();
    expect(screen.getByRole('button', { name: /^Workflow/ })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(screen.getByRole('button', { name: /^Script/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: /^GAgent/ })).toHaveAttribute(
      'aria-pressed',
      'false',
    );
    expect(screen.queryByRole('button', { name: '定义库' })).toBeNull();
    expect(screen.queryByRole('button', { name: '工作流画布' })).toBeNull();

    fireEvent.click(
      screen.getByRole('button', { name: 'Open construction mode help' }),
    );
    expect(
      await screen.findByText(
        'The Build phase first determines which implementation method is used for the current member, and then directly completes authoring and dry-run in the same workbench.',
      ),
    ).toBeTruthy();
    expect(await screen.findByText('Workflow description')).toBeTruthy();
  });

  it('blocks a repair-required saved route without substituting Gateway', async () => {
    (studioApi.getUserLlmSettings as jest.Mock).mockResolvedValueOnce({
      savedSelection: {
        routeKind: 'nyx_id_user_service',
        routeValue: '/api/v1/proxy/s/stale-openai',
        nyxIdUserServiceId: 'us-stale-openai',
        serviceSlugSnapshot: 'stale-openai',
        modelSelection: {
          kind: 'explicit_model',
          modelId: 'gpt-5.4-mini',
        },
      },
      savedRouteLabel: '/api/v1/proxy/s/stale-openai',
      selectionStatus: 'needs_repair',
      catalogDiagnostic: 'route_not_ready',
      remediation: 'choose_replacement',
      catalogStatus: 'ready',
      capabilities: {
        canEditRoute: true,
        canEditModel: true,
        canSave: true,
        canRetryCatalog: false,
      },
      routeOptions: [
        {
          routeValue: '',
          label: 'Company LLM Gateway',
          source: 'gateway_provider',
          status: 'ready',
          allowed: true,
          ready: true,
          userServiceId: null,
          serviceSlug: null,
          modelCatalog: {
            certainty: 'enumerated',
            modelIds: ['gpt-4.1-mini', 'gpt-5.4-mini'],
            defaultModelId: 'gpt-4.1-mini',
            diagnostic: 'unspecified',
          },
          description: null,
        },
        {
          routeValue: '/api/v1/proxy/s/openai',
          label: 'OpenAI',
          source: 'user_service',
          status: 'ready',
          allowed: true,
          ready: true,
          userServiceId: 'us-openai',
          serviceSlug: 'openai',
          modelCatalog: {
            certainty: 'enumerated',
            modelIds: ['gpt-4.1-mini', 'gpt-5.4-mini'],
            defaultModelId: 'gpt-4.1-mini',
            diagnostic: 'unspecified',
          },
          description: null,
        },
      ],
      modelGroupsByRoute: [
        {
          routeValue: '',
          groupId: 'openai-gateway',
          label: 'OpenAI Gateway',
          models: ['gpt-5.4-mini'],
        },
      ],
    });

    renderStudioPage('/studio');

    const routeLabel = await screen.findByTestId('workflow-dry-run-route');
    await waitFor(() => {
      expect(routeLabel).toHaveTextContent('/api/v1/proxy/s/stale-openai');
    });
    expect(routeLabel).not.toHaveTextContent('Company LLM Gateway');
    expect(
      screen.getByText(
        'The saved LLM selection needs attention in Settings before this workflow can run.',
      ),
    ).toBeTruthy();
  });

  it('canonicalizes legacy service member params to real member ids', async () => {
    mockStudioMembers = [
      ...mockStudioMembers,
      {
        memberId: 'member-alpha',
        scopeId: 'scope-a',
        displayName: 'Member Alpha',
        description: 'Legacy service-backed member',
        implementationKind: 'workflow',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'service-alpha',
        lastBoundRevisionId: 'rev-alpha',
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    mockScopeRuntimeApi.listServices.mockResolvedValue([
      {
        serviceId: 'service-alpha',
        displayName: 'Member Alpha',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-alpha',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with Alpha.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(async () =>
      mockBuildServiceRevisionCatalog({
        serviceId: 'service-alpha',
        displayName: 'Member Alpha',
        workflowName: 'workspace-demo',
      }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-a&scopeLabel=%E5%9B%A2%E9%98%9F+A&memberId=service-alpha&memberLabel=%E6%88%90%E5%91%98+Alpha&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByRole('button', { name: 'Back to Team' }),
    ).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId('studio-context-meta')).toHaveTextContent(
        'service-alpha',
      );
      expect(window.location.search).toContain('member=member%3Amember-alpha');
    });
    expect(screen.getByTestId('studio-context-meta')).not.toHaveTextContent(
      '团队 A',
    );
    expect(screen.getByTestId('studio-context-meta')).not.toHaveTextContent(
      '成员 Alpha',
    );
    expect(screen.getByTestId('studio-workflow-build-panel')).toBeTruthy();

    await waitFor(() => {
      expect(window.location.pathname).toBe('/studio');
    });

    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get('scopeId')).toBe('scope-a');
    expect(searchParams.get('member')).toBe('member:member-alpha');
    expect(searchParams.get('memberId')).toBeNull();
    expect(searchParams.get('scopeLabel')).toBeNull();
    expect(searchParams.get('memberLabel')).toBeNull();
    expect(searchParams.get('focus')).toBe('workflow:workflow-1');
    expect(searchParams.get('tab')).toBe('studio');
    expect(studioApi.getMember).toHaveBeenCalledWith('scope-a', 'member-alpha');
    expect(studioApi.getMember).not.toHaveBeenCalledWith(
      'scope-a',
      'service-alpha',
    );
  });

  it('keeps direct member route keys as canonical member ids', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Aworkspace-demo&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByRole('button', { name: 'Back to Team' }),
    ).toBeTruthy();
    await waitFor(() => {
      expect(window.location.search).toContain(
        'member=member%3Aworkspace-demo',
      );
    });

    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get('member')).toBe('member:workspace-demo');
    expect(searchParams.get('memberId')).toBeNull();
    expect(studioApi.getMember).toHaveBeenCalledWith(
      'scope-1',
      'workspace-demo',
    );
  });

  it('resolves workflow member display names to stable draft ids before loading drafts', async () => {
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        memberId: 'untitled-member',
        displayName: 'Untitled member',
        publishedServiceId: '',
        lastBoundRevisionId: null,
      },
    ];
    mockWorkflowSummaries = [
      {
        ...mockWorkflowSummaries[0],
        workflowId: 'untitled-member',
        name: 'Untitled member',
        fileName: 'untitled-member.yaml',
        filePath: 'scope://scope-1/untitled-member.yaml',
      },
    ];
    mockWorkflowFile = {
      ...mockWorkflowFile,
      workflowId: 'untitled-member',
      name: 'Untitled member',
      fileName: 'untitled-member.yaml',
      filePath: 'scope://scope-1/untitled-member.yaml',
      yaml: 'name: Untitled member\nsteps: []\n',
      document: {
        ...mockParsedDocument,
        name: 'Untitled member',
      },
    };
    (studioApi.listWorkflows as jest.Mock).mockResolvedValue(
      mockWorkflowSummaries,
    );
    (studioApi.getWorkflow as jest.Mock).mockImplementation(
      async (workflowId: string) => {
        if (workflowId !== 'untitled-member') {
          throw new Error(`Unexpected workflow id: ${workflowId}`);
        }

        return mockWorkflowFile;
      },
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Auntitled-member&step=bind&tab=bindings',
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(studioApi.getWorkflow).toHaveBeenCalledWith(
        'untitled-member',
        'scope-1',
      );
      expect(studioApi.getWorkflow).not.toHaveBeenCalledWith(
        'Untitled member',
        'scope-1',
      );
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('member:untitled-member');
      expect(searchParams.get('focus')).toBe('workflow:untitled-member');
    });
  });

  it('matches workflow member display names before falling back to direct member ids', async () => {
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        memberId: 'm-1',
        displayName: 'Workflow One',
        publishedServiceId: '',
        lastBoundRevisionId: null,
      },
    ];
    mockWorkflowSummaries = [
      {
        ...mockWorkflowSummaries[0],
        workflowId: 'wf-1',
        name: 'Workflow One',
        fileName: 'workflow-one.yaml',
        filePath: 'scope://scope-1/workflow-one.yaml',
      },
    ];
    mockWorkflowFile = {
      ...mockWorkflowFile,
      workflowId: 'wf-1',
      name: 'Workflow One',
      fileName: 'workflow-one.yaml',
      filePath: 'scope://scope-1/workflow-one.yaml',
      yaml: 'name: Workflow One\nsteps: []\n',
      document: {
        ...mockParsedDocument,
        name: 'Workflow One',
      },
    };
    (studioApi.listWorkflows as jest.Mock).mockResolvedValue(
      mockWorkflowSummaries,
    );
    (studioApi.getWorkflow as jest.Mock).mockImplementation(
      async (workflowId: string) => {
        if (workflowId !== 'wf-1') {
          throw new Error(`Unexpected workflow id: ${workflowId}`);
        }

        return mockWorkflowFile;
      },
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Am-1&step=bind&tab=bindings',
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(studioApi.getWorkflow).toHaveBeenCalledWith('wf-1', 'scope-1');
      expect(studioApi.getWorkflow).not.toHaveBeenCalledWith('m-1', 'scope-1');
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('member:m-1');
      expect(searchParams.get('focus')).toBe('workflow:wf-1');
    });
  });

  it('canonicalizes a legacy service member link to the real backend member identity', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&memberId=default&step=invoke&tab=invoke',
    );

    expect(await screen.findByTestId('studio-invoke-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText('member:workspace-demo')).toBeTruthy();
      expect(screen.getByText('service:default')).toBeTruthy();
    });

    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get('scopeId')).toBe('scope-1');
    expect(searchParams.get('member')).toBe('member:workspace-demo');
    expect(searchParams.get('memberId')).toBeNull();
    expect(searchParams.get('step')).toBe('invoke');
    expect(searchParams.get('tab')).toBe('invoke');
    expect(studioApi.getMember).not.toHaveBeenCalledWith('scope-1', 'default');
  });

  it('resyncs the Studio state from legacy service params when the route changes after mount', async () => {
    mockStudioMembers = [
      ...mockStudioMembers,
      {
        memberId: 'member-alpha',
        scopeId: 'scope-a',
        displayName: 'Member Alpha',
        description: 'Legacy service-backed member',
        implementationKind: 'workflow',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'service-alpha',
        lastBoundRevisionId: 'rev-alpha',
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
      {
        memberId: 'member-beta',
        scopeId: 'scope-b',
        displayName: 'Member Beta',
        description: 'Legacy service-backed member',
        implementationKind: 'workflow',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'service-beta',
        lastBoundRevisionId: 'rev-beta',
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    mockScopeRuntimeApi.listServices.mockImplementation(
      async (scopeId: string) => [
        {
          serviceId: scopeId === 'scope-b' ? 'service-beta' : 'service-alpha',
          displayName: scopeId === 'scope-b' ? 'Member Beta' : 'Member Alpha',
          deploymentStatus: 'Active',
          primaryActorId: scopeId === 'scope-b' ? 'actor-beta' : 'actor-alpha',
          endpoints: [
            {
              endpointId: 'chat',
              displayName: 'Chat',
              kind: 'chat',
              description: 'Chat with the member.',
              requestTypeUrl: '',
              responseTypeUrl: '',
            },
          ],
        },
      ],
    );
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        mockBuildServiceRevisionCatalog({
          serviceId,
          displayName:
            serviceId === 'service-beta' ? 'Member Beta' : 'Member Alpha',
          workflowName: 'workspace-demo',
        }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-a&scopeLabel=%E5%9B%A2%E9%98%9F+A&memberId=service-alpha&memberLabel=%E6%88%90%E5%91%98+Alpha&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByRole('button', { name: 'Back to Team' }),
    ).toBeTruthy();
    await waitFor(() => {
      expect(window.location.search).toContain('member=member%3Amember-alpha');
    });

    await replaceStudioRoute(
      '/studio?scopeId=scope-b&scopeLabel=%E5%9B%A2%E9%98%9F+B&memberId=service-beta&memberLabel=%E6%88%90%E5%91%98+Beta&tab=workflows',
    );

    expect(
      await screen.findByRole('button', { name: 'Back to Team' }),
    ).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId('studio-context-meta')).toHaveTextContent(
        'service-beta',
      );
      expect(window.location.search).toContain('member=member%3Amember-beta');
    });
    expect(screen.getByTestId('studio-context-meta')).not.toHaveTextContent(
      '团队 B',
    );
    expect(screen.getByTestId('studio-context-meta')).not.toHaveTextContent(
      '成员 Beta',
    );
    expect(screen.getByTestId('studio-workflow-build-panel')).toBeTruthy();

    await waitFor(() => {
      expect(mockScopeRuntimeApi.listServices).toHaveBeenCalledWith(
        'scope-b',
        expect.objectContaining({
          appId: 'default',
        }),
      );
    });

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('member:member-beta');
      expect(searchParams.get('memberId')).toBeNull();
    });

    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get('scopeId')).toBe('scope-b');
    expect(searchParams.get('member')).toBe('member:member-beta');
    expect(searchParams.get('memberId')).toBeNull();
    expect(searchParams.get('scopeLabel')).toBeNull();
    expect(searchParams.get('memberLabel')).toBeNull();
    expect(searchParams.get('focus')).toBe('workflow:workflow-1');
    expect(searchParams.get('tab')).toBe('studio');
    expect(studioApi.getMember).toHaveBeenCalledWith('scope-b', 'member-beta');
    expect(studioApi.getMember).not.toHaveBeenCalledWith(
      'scope-b',
      'service-beta',
    );
  });

  it('ignores removed create-team route params and falls back to the explicit member-selection empty state', async () => {
    renderStudioPage(
      '/studio?draft=new&teamMode=create&teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3',
    );

    expect(
      await screen.findByRole('button', { name: 'Back to Team' }),
    ).toBeTruthy();
    expect(screen.queryByRole('button', { name: '返回创建页' })).toBeNull();
    expect(await screen.findByTestId('studio-empty-member-state')).toBeTruthy();
    expect(screen.queryByRole('button', { name: '发布团队入口' })).toBeNull();

    const rail = await screen.findByLabelText('Team members');
    expect(within(rail).queryByRole('button', { name: 'draft' })).toBeNull();

    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get('teamMode')).toBeNull();
    expect(searchParams.get('teamName')).toBeNull();
    expect(searchParams.get('entryName')).toBeNull();
    expect(searchParams.get('teamDraftWorkflowId')).toBeNull();
    expect(searchParams.get('teamDraftWorkflowName')).toBeNull();
    expect(searchParams.get('draft')).toBeNull();
    expect(searchParams.get('focus')).toBeNull();
  });

  it('resyncs the Studio deep link when the target workflow changes after mount', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(await screen.findByText('DAG Canvas')).toBeTruthy();

    await replaceStudioRoute(
      '/studio?focus=template%3Apublished-demo&prompt=Continue%20this%20workflow%20in%20Studio&tab=studio',
    );

    await waitFor(() => {
      expect(studioApi.getTemplateWorkflow).toHaveBeenCalledWith(
        'published-demo',
      );
    });

    expect(
      (await screen.findByLabelText(
        'Workflow dry run input',
      )) as HTMLTextAreaElement,
    ).toHaveValue('Continue this workflow in Studio');
  });

  it('falls back to the workflow build workbench when the removed files tab is requested', async () => {
    renderStudioPage('/studio?tab=files');

    expect(await screen.findByText('DAG Canvas')).toBeTruthy();
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('tab')).toBe('studio');
      expect(searchParams.get('member')).toBe('workflow:workflow-1');
      expect(searchParams.get('focus')).toBeNull();
    });
  });

  it('hydrates an editable blank draft when a scope workflow has no YAML source yet', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValue({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
      workflowStorageMode: 'scope',
    });
    mockStudioMembers = [
      ...mockStudioMembers,
      {
        memberId: 'joker',
        scopeId: 'scope-1',
        displayName: 'joker',
        description: 'Joker workflow member',
        implementationKind: 'workflow',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'joker',
        lastBoundRevisionId: 'rev-joker',
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    mockWorkflowFile = {
      ...mockWorkflowFile,
      name: 'scope-demo',
      directoryId: 'scope:scope-1',
      directoryLabel: 'scope-1',
      yaml: '',
      document: null,
      findings: [
        {
          level: 'error',
          path: '/',
          message: 'Workflow YAML is not available yet.',
        },
      ],
    };

    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(await screen.findByText('DAG Canvas')).toBeTruthy();
    expect(screen.queryByText('尚未加载定义')).toBeNull();
    await waitFor(() => {
      expect(
        screen.getByLabelText('定义 YAML') as HTMLTextAreaElement,
      ).toHaveValue('name: scope-demo\nsteps: []\n');
    });
  });

  it('switches the build stage into GAgent mode', async () => {
    renderStudioPage('/studio');

    expect(await screen.findByText('DAG Canvas')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /^GAgent/ }));

    expect(await screen.findByTestId('studio-gagent-build-panel')).toBeTruthy();
    expect(screen.getByTestId('studio-context-title')).toHaveTextContent(
      'GAgent Build',
    );

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('tab')).toBe('gagents');
      expect(searchParams.get('step')).toBe('build');
    });
  });

  it('shows the standalone GAgent definition fields inside Build', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });

    renderStudioPage('/studio');

    fireEvent.click(await screen.findByRole('button', { name: /^GAgent/ }));

    await waitFor(() => {
      expect(mockRuntimeGAgentApi.listKinds).toHaveBeenCalled();
    });

    expect(await screen.findByLabelText('GAgent type')).toBeTruthy();
    expect(screen.getByLabelText('Display name')).toBeTruthy();
    expect(screen.getByLabelText('Role')).toBeTruthy();
    expect(screen.getByLabelText('Initial prompt')).toBeTruthy();
    expect(screen.getByLabelText('Tools')).toBeTruthy();
    expect(screen.getByLabelText('Orleans grain')).toBeTruthy();
    expect(screen.getByLabelText('Ephemeral')).toBeTruthy();
  });

  it('opens the workflow build surface when a prompt is carried into Studio', async () => {
    renderStudioPage(
      '/studio?tab=workflows&prompt=Continue%20this%20workflow%20in%20Studio',
    );

    expect(await screen.findByText('DAG Canvas')).toBeTruthy();
    expect(screen.getByRole('button', { name: /^Workflow/ })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(
      (await screen.findByLabelText(
        'Workflow dry run input',
      )) as HTMLTextAreaElement,
    ).toHaveValue('Continue this workflow in Studio');

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('tab')).toBe('studio');
      expect(searchParams.get('member')).toBe('workflow:workflow-1');
      expect(searchParams.get('focus')).toBeNull();
      expect(searchParams.get('prompt')).toBe(
        'Continue this workflow in Studio',
      );
    });
  });

  it('tries to restore auth first and then loads Studio when the host session recovers', async () => {
    (studioApi.getAuthSession as jest.Mock)
      .mockResolvedValueOnce({
        enabled: true,
        authenticated: false,
        providerDisplayName: 'NyxID',
      })
      .mockResolvedValue({
        enabled: true,
        authenticated: true,
        providerDisplayName: 'NyxID',
      });
    mockEnsureActiveAuthSession.mockResolvedValue({
      tokens: {
        accessToken: 'token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    renderStudioPage('/studio?tab=studio');

    await waitFor(() => {
      expect(mockEnsureActiveAuthSession).toHaveBeenCalledTimes(1);
      expect(studioApi.getAppContext).toHaveBeenCalled();
    });

    await waitFor(() => {
      expect(window.location.pathname).toBe('/studio');
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('tab')).toBe('studio');
      expect(searchParams.get('member')).toBe('workflow:workflow-1');
      expect(searchParams.get('focus')).toBeNull();
      expect(searchParams.get('execution')).toBeNull();
    });
  });

  it('redirects to login when Studio auth stays unauthenticated after refresh', async () => {
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      enabled: true,
      authenticated: false,
      providerDisplayName: 'NyxID',
    });

    renderStudioPage('/studio?tab=studio&focus=workflow%3Aworkflow-1');

    await waitFor(() => {
      expect(mockEnsureActiveAuthSession).toHaveBeenCalledTimes(1);
      expect(window.location.pathname).toBe('/login');
    });

    expect(new URLSearchParams(window.location.search).get('redirect')).toBe(
      '/studio?step=build&focus=workflow%3Aworkflow-1&tab=studio',
    );
  });

  it('does not auto-redirect again after a previous Studio relogin attempt', async () => {
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      enabled: true,
      authenticated: false,
      providerDisplayName: 'NyxID',
    });
    window.sessionStorage.setItem(
      `${STUDIO_AUTO_RELOGIN_ATTEMPT_KEY}/studio`,
      '1',
    );

    renderStudioPage('/studio');

    await waitFor(() => {
      expect(studioApi.getAuthSession).toHaveBeenCalled();
    });

    expect(mockEnsureActiveAuthSession).not.toHaveBeenCalled();
    expect(window.location.pathname).toBe('/studio');
  });

  it('keeps the script build surface active when its leave guard blocks a lifecycle switch', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
      scriptStorageMode: 'scope',
      features: {
        ...defaultStudioAppContext.features,
        scripts: true,
      },
    });
    (scriptsApi.listScripts as jest.Mock).mockResolvedValueOnce([
      {
        available: true,
        scopeId: 'scope-1',
        script: {
          scopeId: 'scope-1',
          scriptId: 'script-alpha',
          catalogActorId: 'catalog-1',
          definitionActorId: 'definition-1',
          activeRevision: 'rev-script-1',
          activeSourceHash: 'hash-1',
          updatedAt: '2026-03-18T00:00:00Z',
        },
        source: {
          sourceText: 'using System;',
          definitionActorId: 'definition-1',
          revision: 'rev-script-1',
          sourceHash: 'hash-1',
        },
      },
    ]);

    renderStudioPage(
      '/studio?scopeId=scope-1&focus=script%3Ascript-alpha&tab=scripts',
    );

    await screen.findByLabelText('Script ID');
    fireEvent.change(screen.getByLabelText('Script source editor'), {
      target: {
        value: 'using System;\n// dirty',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Bind' }));

    await waitFor(() => {
      expect(screen.getByTestId('studio-script-build-panel')).toBeTruthy();
      expect(screen.queryByTestId('studio-bind-surface')).toBeNull();
    });
  });

  it('saves edited workflow drafts back to the Studio workspace API', async () => {
    renderStudioPage('/studio?focus=workflow%3Aworkflow-1&tab=studio');

    const editor = await screen.findByLabelText('定义 YAML');
    await waitFor(() => {
      expect(editor).toHaveValue(mockWorkflowFile.yaml);
    });
    fireEvent.change(editor, {
      target: {
        value: 'name: workspace-demo\nsteps:\n  - id: approve_step\n',
      },
    });
    await waitFor(() => {
      expect(editor).toHaveValue(
        'name: workspace-demo\nsteps:\n  - id: approve_step\n',
      );
    });

    const saveButton = screen.getByRole('button', { name: 'Save draft' });
    await waitFor(() => {
      expect(saveButton).toBeEnabled();
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowId: 'workflow-1',
          directoryId: 'dir-1',
          workflowName: 'workspace-demo',
          yaml: 'name: workspace-demo\nsteps:\n  - id: approve_step\n',
        }),
      );
    });

    await waitFor(() => {
      expect(mockConsoleToast.success).toHaveBeenCalledWith(
        'Saved to Workspace/workspace-demo.yaml.',
      );
    });
  });

  it('creates a workflow draft from the create-member inventory flow', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    const createButton = await screen.findByLabelText('Create member');
    await waitFor(() => {
      expect(createButton).not.toBeDisabled();
    });
    fireEvent.click(createButton);

    const createDialog = await screen.findByRole('dialog', {
      name: 'Create member',
    });

    expect(
      within(createDialog).getByRole('button', {
        name: 'Create Workflow member',
      }),
    ).toHaveAttribute('aria-pressed', 'true');

    const nameInput = within(createDialog).getByLabelText('Member name');
    expect(nameInput).toHaveValue('draft');
    fireEvent.change(nameInput, {
      target: {
        value: 'orders-draft',
      },
    });
    fireEvent.click(
      within(createDialog).getByRole('button', { name: 'Create member' }),
    );

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowName: 'orders-draft',
          fileName: 'orders-draft.yaml',
          yaml: 'name: orders-draft\nsteps: []\n',
        }),
      );
    });

    await waitFor(() => {
      expect(studioApi.createMember).toHaveBeenCalledWith({
        scopeId: 'scope-1',
        displayName: 'orders-draft',
        implementationKind: 'workflow',
      });
    });

    await waitFor(() => {
      expect(mockConsoleToast.success).toHaveBeenCalledWith(
        'Created member orders-draft and opened its workflow draft.',
      );
    });
  });

  it('passes route Team context when creating a backend member', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&tab=studio&intent=create-member',
    );

    const createDialog = await screen.findByRole('dialog', {
      name: 'Create member',
    });
    const nameInput = within(createDialog).getByLabelText('Member name');
    fireEvent.change(nameInput, {
      target: {
        value: 'team-worker',
      },
    });
    fireEvent.click(
      within(createDialog).getByRole('button', { name: 'Create member' }),
    );

    await waitFor(() => {
      expect(studioApi.createMember).toHaveBeenCalledWith({
        scopeId: 'scope-1',
        displayName: 'team-worker',
        implementationKind: 'workflow',
        teamId: 't-alpha',
      });
    });
  });

  it('shows a newly created Workflow member in the Team rail without refresh', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&tab=studio&intent=create-member',
    );

    const createDialog = await screen.findByRole('dialog', {
      name: 'Create member',
    });
    fireEvent.change(within(createDialog).getByLabelText('Member name'), {
      target: {
        value: 'fresh-workflow',
      },
    });
    fireEvent.click(
      within(createDialog).getByRole('button', { name: 'Create member' }),
    );

    const rail = await screen.findByLabelText('Team members');
    const createdMember = await within(rail).findByRole('button', {
      name: 'fresh-workflow',
    });
    expect(createdMember).toHaveAttribute('aria-current', 'true');
    expect(
      within(rail).getAllByRole('button', { name: 'fresh-workflow' }),
    ).toHaveLength(1);
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('member:fresh-workflow');
      expect(searchParams.get('focus')).toBe('workflow:workflow-2');
      expect(searchParams.get('tab')).toBe('studio');
    });
  });

  it('loads only the current Team roster for the Studio member rail', async () => {
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        memberId: 'alpha-member',
        displayName: 'Alpha member',
        teamId: 't-alpha',
      },
      {
        ...mockStudioMembers[0],
        memberId: 'beta-member',
        displayName: 'Beta member',
        teamId: 't-beta',
      },
    ];

    renderStudioPage('/studio?scopeId=scope-1&teamId=t-alpha&tab=studio');

    const rail = await screen.findByLabelText('Team members');
    expect(
      await within(rail).findByRole('button', { name: 'Alpha member' }),
    ).toBeTruthy();
    expect(
      within(rail).queryByRole('button', { name: 'Beta member' }),
    ).toBeNull();
    expect(studioApi.listTeamMembers).toHaveBeenCalledWith(
      'scope-1',
      't-alpha',
    );
    expect(studioApi.listMembers).not.toHaveBeenCalled();
  });

  it('sets the selected Team member as the Team entry from Studio inventory', async () => {
    (studioApi.getTeam as jest.Mock).mockResolvedValueOnce(
      mockCreateDefaultTeamSummary({ entryMemberId: null }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aworkspace-demo&tab=studio',
    );

    const rail = await screen.findByLabelText('Team members');
    const setEntryButton = await within(rail).findByRole('button', {
      name: 'Set workspace-demo as Team entry member',
    });
    fireEvent.click(setEntryButton);

    await waitFor(() => {
      expect(studioApi.setTeamEntryMember).toHaveBeenCalledWith(
        'scope-1',
        't-alpha',
        'workspace-demo',
      );
    });
    expect(mockConsoleToast.info).toHaveBeenCalledWith(
      'Team entry change submitted. Waiting for sync confirmation.',
    );
  });

  it('allows unbound workflow members to be configured as Team entry members', async () => {
    (studioApi.getTeam as jest.Mock).mockResolvedValueOnce(
      mockCreateDefaultTeamSummary({ entryMemberId: null }),
    );
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        lifecycleStage: 'created',
        lastBoundRevisionId: null,
        publishedServiceId: '',
      },
    ];

    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aworkspace-demo&tab=studio',
    );

    const rail = await screen.findByLabelText('Team members');
    expect(
      await within(rail).findByRole('button', { name: 'workspace-demo' }),
    ).toBeTruthy();
    fireEvent.click(
      await within(rail).findByRole('button', {
        name: 'Set workspace-demo as Team entry member',
      }),
    );

    await waitFor(() => {
      expect(studioApi.setTeamEntryMember).toHaveBeenCalledWith(
        'scope-1',
        't-alpha',
        'workspace-demo',
      );
    });
  });

  it('does not offer the Studio inventory entry action for members outside the current Team roster', async () => {
    (studioApi.getTeam as jest.Mock).mockResolvedValueOnce(
      mockCreateDefaultTeamSummary({ entryMemberId: null }),
    );
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        memberId: 'alpha-member',
        displayName: 'Alpha member',
        teamId: 't-alpha',
      },
      {
        ...mockStudioMembers[0],
        memberId: 'other-team-member',
        displayName: 'Other team member',
        teamId: 't-beta',
      },
    ];

    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aother-team-member&tab=studio',
    );

    const rail = await screen.findByLabelText('Team members');
    expect(
      await within(rail).findByRole('button', { name: 'Alpha member' }),
    ).toBeTruthy();
    expect(
      within(rail).queryByRole('button', {
        name: 'Set Other team member as Team entry member',
      }),
    ).toBeNull();
    expect(
      within(rail).queryByRole('button', {
        name: 'Set other-team-member as Team entry member',
      }),
    ).toBeNull();
    expect(studioApi.setTeamEntryMember).not.toHaveBeenCalled();
  });

  it('marks the selected Studio member when it is already the Team entry', async () => {
    (studioApi.getTeam as jest.Mock).mockResolvedValueOnce(
      mockCreateDefaultTeamSummary({ entryMemberId: 'workspace-demo' }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aworkspace-demo&tab=studio',
    );

    const rail = await screen.findByLabelText('Team members');
    await waitFor(() => {
      expect(rail).toHaveTextContent(/Entry member ·\s*workspace-demo/);
    });
    expect(
      within(rail).queryByRole('button', {
        name: 'Set workspace-demo as Team entry member',
      }),
    ).toBeNull();
  });

  it('does not hydrate Team Studio rail members from scope-level services or drafts', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      features: {
        ...defaultStudioAppContext.features,
        scripts: true,
      },
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        memberId: 'alpha-member',
        displayName: 'Alpha member',
        publishedServiceId: 'service-alpha',
        teamId: 't-alpha',
      },
      {
        ...mockStudioMembers[0],
        memberId: 'beta-member',
        displayName: 'Beta member',
        publishedServiceId: 'service-beta',
        teamId: 't-beta',
      },
    ];
    mockScopeRuntimeApi.listServices.mockResolvedValueOnce([
      {
        serviceId: 'service-alpha',
        displayName: 'Alpha service',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-alpha',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with alpha.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
      {
        serviceId: 'service-beta',
        displayName: 'Beta service',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-beta',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with beta.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
      {
        serviceId: 'global-service',
        displayName: 'Global service',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-global',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with global.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        mockBuildServiceRevisionCatalog({
          serviceId,
          displayName:
            serviceId === 'service-alpha'
              ? 'Alpha service'
              : serviceId === 'service-beta'
                ? 'Beta service'
                : 'Global service',
          workflowName:
            serviceId === 'service-alpha'
              ? 'workspace-demo'
              : serviceId === 'service-beta'
                ? 'beta-workflow'
                : 'global-workflow',
        }),
    );
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'workflow-1',
        name: 'workspace-demo',
        description: 'Workspace workflow',
        fileName: 'workspace-demo.yaml',
        filePath: '/tmp/workflows/workspace-demo.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 2,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
      {
        workflowId: 'workflow-beta',
        name: 'beta-workflow',
        description: 'Beta workflow',
        fileName: 'beta-workflow.yaml',
        filePath: '/tmp/workflows/beta-workflow.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
      {
        workflowId: 'workflow-draft',
        name: 'draft',
        description: 'Loose draft',
        fileName: 'draft.yaml',
        filePath: '/tmp/workflows/draft.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
    ]);
    (scriptsApi.listScripts as jest.Mock).mockResolvedValue([
      {
        available: true,
        scopeId: 'scope-1',
        script: {
          scopeId: 'scope-1',
          scriptId: 'script-1',
          catalogActorId: 'catalog-1',
          definitionActorId: 'definition-1',
          activeRevision: 'rev-script-1',
          activeSourceHash: 'hash-1',
          updatedAt: '2026-03-18T00:00:00Z',
        },
        source: {
          sourceText: 'using System;',
          definitionActorId: 'definition-1',
          revision: 'rev-script-1',
          sourceHash: 'hash-1',
        },
      },
    ]);

    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&focus=script%3Ascript-1&tab=scripts',
    );

    const rail = await screen.findByLabelText('Team members');
    expect(
      await within(rail).findByRole('button', { name: 'Alpha member' }),
    ).toBeTruthy();
    expect(
      within(rail).queryByRole('button', { name: 'Beta member' }),
    ).toBeNull();
    expect(
      within(rail).queryByRole('button', { name: 'Beta service' }),
    ).toBeNull();
    expect(
      within(rail).queryByRole('button', { name: 'Global service' }),
    ).toBeNull();
    expect(within(rail).queryByRole('button', { name: 'draft' })).toBeNull();
    expect(within(rail).queryByRole('button', { name: 'script-1' })).toBeNull();
    expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenCalledWith(
      'scope-1',
      'service-alpha',
    );
    expect(mockScopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalledWith(
      'scope-1',
      'service-beta',
    );
    expect(mockScopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalledWith(
      'scope-1',
      'global-service',
    );
  });

  it('returns to canonical Team detail when Studio has Team context', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aworkspace-demo&focus=workflow%3Aworkflow-1&tab=studio',
    );

    fireEvent.click(
      await screen.findByRole('button', { name: 'Back to Team' }),
    );

    expect(window.location.pathname).toBe('/scopes/scope-1/teams/t-alpha');
    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get('memberId')).toBe('workspace-demo');
    expect(searchParams.get('tab')).toBe('overview');
  });

  it('keeps the Studio Teams breadcrumb in the scoped Team collection', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aworkspace-demo&focus=workflow%3Aworkflow-1&tab=studio',
    );

    const breadcrumb = await screen.findByRole('navigation', {
      name: 'Breadcrumb',
    });
    const teamsBreadcrumbLink = within(breadcrumb).getByRole('link', {
      name: 'Teams',
    });
    expect(teamsBreadcrumbLink).toHaveAttribute(
      'href',
      '/scopes/scope-1/teams',
    );

    fireEvent.click(teamsBreadcrumbLink);

    expect(window.location.pathname).toBe('/scopes/scope-1/teams');
    expect(window.location.search).toBe('');
  });

  it('returns to the explicit Team handoff target after Studio settles its route', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aworkspace-demo&step=build&tab=studio&returnTo=%2Fscopes%2Fscope-1%2Fteams%2Ft-alpha%3FmemberId%3Dworkspace-demo%26tab%3Dmembers',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('returnTo')).toBe(
        '/scopes/scope-1/teams/t-alpha?memberId=workspace-demo&tab=members',
      );
    });

    fireEvent.click(
      await screen.findByRole('button', { name: 'Back to Team' }),
    );

    expect(window.location.pathname).toBe('/scopes/scope-1/teams/t-alpha');
    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get('memberId')).toBe('workspace-demo');
    expect(searchParams.get('tab')).toBe('members');
  });

  it('moves focus from a Script draft to the new Workflow member after create', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      features: {
        ...defaultStudioAppContext.features,
        scripts: true,
      },
      scopeId: 'scope-1',
      scopeResolved: true,
    });

    renderStudioPage(
      '/studio?scopeId=scope-1&focus=script%3Ascript-alpha&tab=scripts',
    );

    expect(await screen.findByTestId('studio-script-build-panel')).toBeTruthy();
    expect(screen.getByLabelText('Script ID')).toHaveValue('script-alpha');
    expect(screen.queryByText('Scripts Studio')).toBeNull();
    expect(screen.queryByText('Leave Scripts Studio?')).toBeNull();

    fireEvent.click(
      await screen.findByRole('button', { name: 'Create member' }),
    );
    const createDialog = await screen.findByRole('dialog', {
      name: 'Create member',
    });
    fireEvent.click(
      within(createDialog).getByRole('button', {
        name: 'Create Workflow member',
      }),
    );
    fireEvent.change(within(createDialog).getByLabelText('Member name'), {
      target: {
        value: 'orders-workflow',
      },
    });
    fireEvent.click(
      within(createDialog).getByRole('button', { name: 'Create member' }),
    );

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowName: 'orders-workflow',
        }),
      );
    });
    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();
    expect(screen.queryByTestId('studio-script-build-panel')).toBeNull();
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('tab')).toBe('studio');
      expect(searchParams.get('focus')).not.toBe('script:script-alpha');
    });
  });

  it('opens the create-member modal once from the typed Studio intent', async () => {
    renderStudioPage('/studio?tab=studio&intent=create-member');

    const createDialog = await screen.findByRole('dialog', {
      name: 'Create member',
    });
    expect(within(createDialog).getByLabelText('Member name')).toHaveValue(
      'draft',
    );
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();

    fireEvent.click(
      within(createDialog).getByRole('button', { name: 'Cancel' }),
    );

    await waitFor(() => {
      expect(
        screen.queryByRole('dialog', { name: 'Create member' }),
      ).toBeNull();
    });

    await waitFor(() => {
      expect(
        screen.queryByRole('dialog', { name: 'Create member' }),
      ).toBeNull();
    });
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
  });

  it('creates a named Script member authority and opens its draft before bind', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
      scriptStorageMode: 'scope',
      features: {
        ...defaultStudioAppContext.features,
        scripts: true,
      },
    });

    renderStudioPage('/studio?focus=workflow%3Aworkflow-1&tab=studio');

    fireEvent.click(
      await screen.findByRole('button', { name: 'Create member' }),
    );
    const createDialog = await screen.findByRole('dialog', {
      name: 'Create member',
    });

    const scriptChip = within(createDialog).getByRole('button', {
      name: 'Create Script member',
    });
    fireEvent.click(scriptChip);

    expect(scriptChip).toHaveAttribute('aria-pressed', 'true');
    expect(within(createDialog).queryByLabelText('Member name')).toBeNull();
    const scriptNameInput = within(createDialog).getByLabelText('Script name');
    expect(scriptNameInput).toHaveValue('script-1');
    fireEvent.change(scriptNameInput, {
      target: {
        value: 'Refund Handler',
      },
    });
    expect(
      screen.getByText(
        'Script creates a backend member and opens a stable script draft identity in Build. It becomes callable after Save script is catalog-applied and Bind succeeds.',
      ),
    ).toBeTruthy();
    expect(createDialog).toHaveTextContent('Script id:refund-handler');
    fireEvent.click(
      within(createDialog).getByRole('button', { name: 'Create member' }),
    );

    expect(await screen.findByTestId('studio-script-build-panel')).toBeTruthy();
    expect(screen.getByLabelText('Script ID')).toHaveValue('refund-handler');
    expect(
      window.localStorage.getItem('aevatar:studio:script-drafts:v1'),
    ).toContain('refund-handler');
    expect(studioApi.createMemberWithId).toHaveBeenCalledWith(
      expect.objectContaining({
        scopeId: 'scope-1',
        memberId: 'refund-handler',
        displayName: 'Refund Handler',
        implementationKind: 'script',
      }),
    );
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('tab')).toBe('scripts');
      expect(searchParams.get('step')).toBe('build');
      expect(searchParams.get('member')).toBe('member:refund-handler');
      expect(searchParams.get('focus')).toBe('script:refund-handler');
    });
  });

  it('keeps the Script create action disabled when the Script feature is off', async () => {
    renderStudioPage('/studio?focus=workflow%3Aworkflow-1&tab=studio');

    fireEvent.click(
      await screen.findByRole('button', { name: 'Create member' }),
    );
    const createDialog = await screen.findByRole('dialog', {
      name: 'Create member',
    });

    fireEvent.click(
      within(createDialog).getByRole('button', {
        name: 'Create Script member',
      }),
    );

    expect(
      within(createDialog).getByRole('button', { name: 'Create member' }),
    ).toBeDisabled();
    expect(screen.getByRole('dialog', { name: 'Create member' })).toBeTruthy();
  });

  it('opens the Script create flow from the empty Script build surface', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
      scriptStorageMode: 'scope',
      features: {
        ...defaultStudioAppContext.features,
        scripts: true,
      },
    });

    renderStudioPage('/studio?tab=scripts');

    expect(await screen.findByTestId('studio-script-build-panel')).toBeTruthy();
    expect(screen.getByTestId('studio-context-title')).toHaveTextContent(
      'Create a script',
    );
    expect(
      screen.getByText(
        'No script is selected yet. Start a script draft to open the editor.',
      ),
    ).toBeTruthy();
    expect(screen.queryByLabelText('Team members')).toBeNull();
    expect(screen.queryByTestId('studio-lifecycle-section')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Observe' })).toBeNull();
    expect(screen.queryByLabelText('Script ID')).toBeNull();
    expect(screen.queryByText('Script draft run')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Save draft' })).toBeNull();

    fireEvent.click(await screen.findByRole('button', { name: 'Add script' }));

    const createDialog = await screen.findByRole('dialog', {
      name: 'Create member',
    });
    expect(
      within(createDialog).getByRole('button', {
        name: 'Create Script member',
      }),
    ).toHaveAttribute('aria-pressed', 'true');
    expect(within(createDialog).getByLabelText('Script name')).toHaveValue(
      'script-1',
    );
    expect(
      within(createDialog).getByRole('button', { name: 'Create member' }),
    ).toBeEnabled();
  });

  it('creates a named GAgent member authority and opens GAgent Build', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });

    renderStudioPage('/studio?focus=workflow%3Aworkflow-1&tab=studio');

    fireEvent.click(
      await screen.findByRole('button', { name: 'Create member' }),
    );
    const createDialog = await screen.findByRole('dialog', {
      name: 'Create member',
    });

    const gagentChip = within(createDialog).getByRole('button', {
      name: 'Create GAgent member',
    });
    fireEvent.click(gagentChip);

    expect(gagentChip).toHaveAttribute('aria-pressed', 'true');
    expect(within(createDialog).queryByLabelText('Member name')).toBeNull();
    const gAgentNameInput = within(createDialog).getByLabelText('GAgent name');
    expect(gAgentNameInput).toHaveValue('gagent-1');
    fireEvent.change(gAgentNameInput, {
      target: {
        value: 'Orders Worker',
      },
    });
    expect(
      screen.getByText(
        'GAgent creates a backend member and opens Build > GAgent for actor type, role, prompt, tools, and persistence authoring.',
      ),
    ).toBeTruthy();
    fireEvent.click(
      within(createDialog).getByRole('button', { name: 'Create member' }),
    );

    expect(await screen.findByTestId('studio-gagent-build-panel')).toBeTruthy();
    expect(studioApi.createMember).toHaveBeenCalledWith(
      expect.objectContaining({
        scopeId: 'scope-1',
        displayName: 'Orders Worker',
        implementationKind: 'gagent',
      }),
    );
    await waitFor(() => {
      expect(mockConsoleToast.success).toHaveBeenCalledWith(
        'Created GAgent member Orders Worker and opened Build.',
      );
    });
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('tab')).toBe('gagents');
      expect(searchParams.get('step')).toBe('build');
      expect(searchParams.get('member')).toBe('member:orders-worker');
    });
  });

  it('restores an unbound GAgent member from the backend roster after refresh', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    mockStudioMembers = [
      {
        memberId: 'orders-worker',
        scopeId: 'scope-1',
        displayName: 'Orders Worker',
        description: 'Unbound GAgent member',
        implementationKind: 'gagent',
        lifecycleStage: 'created',
        publishedServiceId: 'member-orders-worker',
        lastBoundRevisionId: null,
        createdAt: '2026-04-27T08:10:00Z',
        updatedAt: '2026-04-27T08:10:00Z',
      },
      ...mockStudioMembers,
    ];

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Aorders-worker&step=build&tab=gagents',
    );

    expect(await screen.findByTestId('studio-gagent-build-panel')).toBeTruthy();
    const rail = await screen.findByLabelText('Team members');
    expect(
      await within(rail).findByRole('button', { name: 'Orders Worker' }),
    ).toBeTruthy();
    expect(screen.getByTestId('studio-context-title')).toHaveTextContent(
      'Orders Worker',
    );

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('member:orders-worker');
      expect(searchParams.get('tab')).toBe('gagents');
      expect(searchParams.get('step')).toBe('build');
      expect(searchParams.get('focus')).toBeNull();
    });
  });

  it('opens a routed GAgent member on the GAgent Build surface without requiring a gagents tab hint', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    mockStudioMembers = [
      {
        memberId: 'orders-worker',
        scopeId: 'scope-1',
        displayName: 'Orders Worker',
        description: 'Team entry GAgent member',
        implementationKind: 'gagent',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'member-orders-worker',
        lastBoundRevisionId: 'rev-gagent-1',
        teamId: 't-alpha',
        createdAt: '2026-04-27T08:10:00Z',
        updatedAt: '2026-04-27T08:15:00Z',
      },
      ...mockStudioMembers,
    ];

    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aorders-worker&step=build&tab=studio',
    );

    expect(await screen.findByTestId('studio-gagent-build-panel')).toBeTruthy();
    expect(screen.queryByTestId('studio-workflow-build-panel')).toBeNull();
    expect(screen.getByTestId('studio-context-title')).toHaveTextContent(
      'Orders Worker',
    );
    expect(screen.getByRole('button', { name: /^GAgent/ })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(screen.getByRole('button', { name: 'Workflow' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Script' })).toBeDisabled();
    expect(screen.getByRole('button', { name: /^GAgent/ })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Workflow' }));
    expect(screen.getByTestId('studio-gagent-build-panel')).toBeTruthy();
    expect(screen.queryByTestId('studio-workflow-build-panel')).toBeNull();
    expect(
      screen.getByRole('button', { name: 'Continue to Bind' }),
    ).toBeEnabled();

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('member:orders-worker');
      expect(searchParams.get('tab')).toBe('gagents');
      expect(searchParams.get('step')).toBe('build');
      expect(searchParams.get('focus')).toBeNull();
    });
  });

  it('binds an unbound GAgent member from Build and keeps the member route', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    mockStudioMembers = [
      {
        memberId: 'orders-worker',
        scopeId: 'scope-1',
        displayName: 'Orders Worker',
        description: 'Unbound GAgent member',
        implementationKind: 'gagent',
        lifecycleStage: 'created',
        publishedServiceId: 'member-orders-worker',
        lastBoundRevisionId: null,
        createdAt: '2026-04-27T08:10:00Z',
        updatedAt: '2026-04-27T08:10:00Z',
      },
      ...mockStudioMembers,
    ];
    mockScopeRuntimeApi.listServices.mockReset();
    mockScopeRuntimeApi.listServices
      .mockResolvedValueOnce([])
      .mockResolvedValue([
        {
          serviceId: 'member-orders-worker',
          displayName: 'Orders Worker',
          deploymentStatus: 'Active',
          primaryActorId: 'actor-orders-worker',
          endpoints: [
            {
              endpointId: 'run',
              displayName: 'Run',
              kind: 'command',
              description: 'Run the bound GAgent member.',
              requestTypeUrl: 'type.googleapis.com/google.protobuf.StringValue',
              responseTypeUrl: '',
            },
          ],
        },
      ]);

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Aorders-worker&step=build&tab=gagents',
    );

    expect(await screen.findByTestId('studio-gagent-build-panel')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Continue to Bind' }));

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText('candidate:Orders Worker')).toBeTruthy();
      expect(screen.getByText('service:no-service')).toBeTruthy();
      expect(screen.getByText('services:none')).toBeTruthy();
      expect(screen.getByText('member:orders-worker')).toBeTruthy();
    });

    fireEvent.click(
      screen.getByRole('button', { name: 'Bind current member' }),
    );

    await waitFor(() => {
      expect(studioApi.bindMemberGAgent).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: 'scope-1',
          memberId: 'orders-worker',
          displayName: 'Orders Worker',
          agentKind: 'Tests.OrdersGAgent',
          endpoints: expect.arrayContaining([
            expect.objectContaining({
              endpointId: 'run',
              kind: 'command',
            }),
          ]),
        }),
      );
    });
    expect(studioApi.bindScopeGAgent).not.toHaveBeenCalled();
    await waitFor(() => {
      expect(screen.getByText('service:member-orders-worker')).toBeTruthy();
      expect(screen.getByText('services:member-orders-worker')).toBeTruthy();
      expect(screen.getByText('candidate:none')).toBeTruthy();
    });

    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get('member')).toBe('member:orders-worker');
    expect(searchParams.get('step')).toBe('bind');
    expect(searchParams.get('tab')).toBe('bindings');
    expect(searchParams.get('focus')).toBeNull();
  });

  it('renames a workflow member from the inventory actions', async () => {
    jest.spyOn(window, 'prompt').mockReturnValue('orders-router');

    renderStudioPage('/studio?focus=workflow%3Aworkflow-1&tab=studio');

    fireEvent.click(await screen.findByLabelText('Rename workspace-demo'));

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowId: 'workflow-1',
          workflowName: 'orders-router',
          fileName: 'orders-router.yaml',
        }),
      );
    });

    await waitFor(() => {
      expect(mockConsoleToast.success).toHaveBeenCalledWith(
        'Renamed workflow member to orders-router.',
      );
    });
  });

  it('deletes a workflow member from the inventory rail', async () => {
    renderStudioPage('/studio?focus=workflow%3Aworkflow-1&tab=studio');

    fireEvent.click(await screen.findByLabelText('Delete workspace-demo'));

    await waitFor(() => {
      expect(Modal.confirm).toHaveBeenCalledWith(
        expect.objectContaining({
          title: 'Delete workflow member',
          okText: 'Delete member',
          cancelText: 'Keep member',
          autoFocusButton: 'cancel',
        }),
      );
    });

    const confirmConfig = (Modal.confirm as jest.Mock).mock.calls[0]?.[0];
    expect(confirmConfig.icon).toBeTruthy();
    await act(async () => {
      await confirmConfig.onOk();
    });

    await waitFor(() => {
      expect(studioApi.deleteWorkflow).toHaveBeenCalledWith(
        'workflow-1',
        undefined,
      );
    });

    await waitFor(() => {
      expect(mockConsoleToast.success).toHaveBeenCalledWith(
        'Deleted workflow member workspace-demo.',
      );
    });
  });

  it('deletes a synced Studio member from the inventory rail', async () => {
    (studioApi.deleteMember as jest.Mock).mockResolvedValue({
      status: 'delete_accepted',
      scopeId: 'scope-1',
      memberId: 'workspace-demo',
      ackedAt: '2026-07-09T08:12:00Z',
    });
    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aworkspace-demo&focus=workflow%3Aworkflow-1&tab=studio',
    );

    const deleteButton = await screen.findByLabelText('Delete workspace-demo');
    const memberReadsBeforeDelete = (studioApi.getMember as jest.Mock).mock
      .calls.length;
    let confirmRemoval: (() => void) | undefined;
    const removalObserved = new Promise<void>((resolve) => {
      confirmRemoval = resolve;
    });
    (studioApi.getMember as jest.Mock)
      .mockResolvedValueOnce({
        summary: mockStudioMembers[0],
        implementationRef: null,
        lastBinding: null,
      })
      .mockImplementationOnce(async () => {
        await removalObserved;
        throw createStudioApiStatusError(
          'Member not found',
          404,
          'STUDIO_MEMBER_NOT_FOUND',
        );
      });
    fireEvent.click(deleteButton);

    await waitFor(() => {
      expect(Modal.confirm).toHaveBeenCalledWith(
        expect.objectContaining({
          title: 'Delete Studio member',
          okText: 'Delete member',
          cancelText: 'Keep member',
          autoFocusButton: 'cancel',
        }),
      );
    });

    const confirmConfig = (Modal.confirm as jest.Mock).mock.calls[0]?.[0];
    expect(confirmConfig.icon).toBeTruthy();
    let deletePromise: Promise<void> | undefined;
    act(() => {
      deletePromise = confirmConfig.onOk();
    });

    await waitFor(() => {
      expect(studioApi.deleteMember).toHaveBeenCalledWith({
        scopeId: 'scope-1',
        memberId: 'workspace-demo',
      });
    });
    expect(studioApi.deleteWorkflow).not.toHaveBeenCalled();
    await waitFor(() => {
      expect(studioApi.getMember).toHaveBeenCalledTimes(
        memberReadsBeforeDelete + 2,
      );
    });
    expect(screen.getByLabelText('Delete workspace-demo')).toBeTruthy();
    expect(mockConsoleToast.info).toHaveBeenCalledWith(
      'Deletion submitted. Waiting for confirmation.',
    );
    expect(mockConsoleToast.success).not.toHaveBeenCalled();

    await act(async () => {
      confirmRemoval?.();
      await deletePromise;
    });

    await waitFor(() => {
      expect(mockConsoleToast.success).toHaveBeenCalledWith(
        'Deleted member workspace-demo.',
      );
    });

    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get('member')).toBeNull();
    expect(searchParams.get('focus')).toBeNull();
  });

  it('builds Studio member delete confirmation from the targeted member identity', async () => {
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        memberId: 'workspace-demo',
        displayName: 'workspace-demo',
        teamId: 't-alpha',
      },
      {
        ...mockStudioMembers[0],
        memberId: 'other-team-member',
        displayName: 'Other team member',
        teamId: 't-alpha',
      },
    ];

    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aworkspace-demo&focus=workflow%3Aworkflow-1&tab=studio',
    );

    fireEvent.click(await screen.findByLabelText('Delete workspace-demo'));

    await waitFor(() => {
      expect(Modal.confirm).toHaveBeenCalledWith(
        expect.objectContaining({
          title: 'Delete Studio member',
          okText: 'Delete member',
          cancelText: 'Keep member',
          autoFocusButton: 'cancel',
        }),
      );
    });

    const confirmConfig = (Modal.confirm as jest.Mock).mock.calls[0]?.[0];
    expect(confirmConfig.icon).toBeTruthy();
    expect(
      String(
        confirmConfig.content?.props?.children?.[0]?.props?.children?.[1]?.props
          ?.children,
      ),
    ).toBe('workspace-demo');

    (Modal.confirm as jest.Mock).mockClear();

    window.history.replaceState(
      {},
      '',
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aother-team-member&focus=workflow%3Aworkflow-1&tab=studio',
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aother-team-member&focus=workflow%3Aworkflow-1&tab=studio',
    );

    fireEvent.click(await screen.findByLabelText('Delete Other team member'));

    await waitFor(() => {
      expect(Modal.confirm).toHaveBeenCalledWith(
        expect.objectContaining({
          title: 'Delete Studio member',
        }),
      );
    });

    const otherConfirmConfig = (Modal.confirm as jest.Mock).mock.calls[0]?.[0];
    expect(
      String(
        otherConfirmConfig.content?.props?.children?.[0]?.props?.children?.[1]
          ?.props?.children,
      ),
    ).toBe('Other team member');

    (studioApi.getMember as jest.Mock).mockRejectedValueOnce(
      createStudioApiStatusError(
        'Member not found',
        404,
        'STUDIO_MEMBER_NOT_FOUND',
      ),
    );

    await act(async () => {
      await otherConfirmConfig.onOk();
    });

    await waitFor(() => {
      expect(studioApi.deleteMember).toHaveBeenCalledWith({
        scopeId: 'scope-1',
        memberId: 'other-team-member',
      });
    });

    await waitFor(() => {
      expect(mockConsoleToast.success).toHaveBeenCalledWith(
        'Deleted member Other team member.',
      );
    });
  });

  it('surfaces an unrelated Studio member delete 404', async () => {
    (studioApi.deleteMember as jest.Mock).mockRejectedValueOnce(
      createStudioApiStatusError(
        'Delete route not found',
        404,
        'ROUTE_NOT_FOUND',
      ),
    );
    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aworkspace-demo&focus=workflow%3Aworkflow-1&tab=studio',
    );

    fireEvent.click(await screen.findByLabelText('Delete workspace-demo'));
    await waitFor(() => {
      expect(Modal.confirm).toHaveBeenCalledWith(
        expect.objectContaining({
          title: 'Delete Studio member',
        }),
      );
    });

    const confirmConfig = (Modal.confirm as jest.Mock).mock.calls[0]?.[0];
    await act(async () => {
      await confirmConfig.onOk();
    });

    expect(mockConsoleToast.error).toHaveBeenCalledWith(
      'Failed to delete member.',
    );
    expect(mockConsoleToast.error).not.toHaveBeenCalledWith(
      'Delete route not found',
    );
    expect(mockConsoleToast.success).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Delete workspace-demo')).toBeTruthy();
  });

  it('treats a missing workflow draft as already deleted from the inventory rail', async () => {
    (studioApi.deleteWorkflow as jest.Mock).mockRejectedValueOnce(
      new Error('Not Found'),
    );

    renderStudioPage('/studio?focus=workflow%3Aworkflow-1&tab=studio');

    fireEvent.click(await screen.findByLabelText('Delete workspace-demo'));

    await waitFor(() => {
      expect(Modal.confirm).toHaveBeenCalledWith(
        expect.objectContaining({
          title: 'Delete workflow member',
        }),
      );
    });

    const confirmConfig = (Modal.confirm as jest.Mock).mock.calls[0]?.[0];
    await act(async () => {
      await expect(confirmConfig.onOk()).resolves.toBeUndefined();
    });

    expect(mockConsoleToast.error).not.toHaveBeenCalled();
    await waitFor(() => {
      expect(mockConsoleToast.success).toHaveBeenCalledWith(
        'Deleted workflow member workspace-demo.',
      );
    });
  });

  it('saves the workflow draft and continues to bind from the build page', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByLabelText('Step ID')).toHaveValue('draft_step');
    });

    fireEvent.click(screen.getByRole('button', { name: 'Add step' }));

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalled();
    });
    await waitFor(() => {
      expect(screen.getByLabelText('Step ID')).toHaveValue('llm_step');
    });

    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: 'scope-1',
          yaml: expect.stringMatching(/llm_step[\s\S]*llm_call/),
        }),
      );
    });

    fireEvent.click(screen.getByRole('button', { name: 'Continue to Bind' }));

    const bindSurface = await screen.findByTestId('studio-bind-surface');
    expect(bindSurface).toBeTruthy();
  });

  it('saves pending workflow step prompt edits without requiring Apply changes', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    await waitFor(() => {
      expect(mockLastWorkflowBuildPanelProps?.onSaveDraft).toEqual(
        expect.any(Function),
      );
      expect(mockLastWorkflowBuildPanelProps?.canSaveWorkflow).toBe(true);
    });
    const staleWorkflowDocument = mockCloneValue(mockWorkflowDocument) as any;
    staleWorkflowDocument.steps[0].parameters = {};
    const staleWorkflowResponse = {
      ...mockWorkflowFile,
      yaml: mockBuildWorkflowYaml(staleWorkflowDocument),
      document: staleWorkflowDocument,
    };
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValueOnce(
      staleWorkflowResponse,
    );
    (studioApi.serializeYaml as jest.Mock).mockClear();
    await act(async () => {
      await mockLastWorkflowBuildPanelProps.onSaveDraft({
        stepId: 'draft_step',
        draft: {
          kind: 'step',
          capability: null,
          id: 'draft_step',
          type: 'llm_call',
          targetRole: 'assistant',
          next: 'approve_step',
          parametersText: JSON.stringify(
            {
              prompt_prefix: 'Classify the refund request before answering.',
            },
            null,
            2,
          ),
          branchesText: '{}',
        },
      });
    });

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          document: expect.objectContaining({
            steps: expect.arrayContaining([
              expect.objectContaining({
                id: 'draft_step',
                parameters: expect.objectContaining({
                  prompt_prefix:
                    'Classify the refund request before answering.',
                }),
              }),
            ]),
          }),
        }),
      );
    });

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: 'scope-1',
          yaml: expect.stringContaining(
            'prompt_prefix: Classify the refund request before answering.',
          ),
        }),
      );
    });

    await waitFor(() => {
      expect(mockLastWorkflowBuildPanelProps?.draftYaml).toContain(
        'prompt_prefix: Classify the refund request before answering.',
      );
      expect(
        mockLastWorkflowBuildPanelProps?.workflowGraph.steps[0].parameters,
      ).toEqual(
        expect.objectContaining({
          prompt_prefix: 'Classify the refund request before answering.',
        }),
      );
    });
  });

  it('applies workflow step changes without requiring a manual graph selection first', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByLabelText('Step ID')).toHaveValue('draft_step');
    });

    (studioApi.serializeYaml as jest.Mock).mockClear();

    const stepIdInput = screen.getByLabelText('Step ID');
    fireEvent.change(stepIdInput, {
      target: { value: 'draft_step_updated' },
    });
    fireEvent.input(stepIdInput, {
      target: { value: 'draft_step_updated' },
    });
    expect(stepIdInput).toHaveValue('draft_step_updated');
    fireEvent.click(screen.getByRole('button', { name: 'Apply changes' }));

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalled();
      const serializedDocument = (
        studioApi.serializeYaml as jest.Mock
      ).mock.calls.at(-1)?.[0]?.document;
      expect(serializedDocument?.steps?.[0]?.id).toBe('draft_step_updated');
    });

    expect(
      screen.queryByText('Select a workflow step before applying changes.'),
    ).toBeNull();
  });

  it('carries the selected bind contract into invoke after continuing from build', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Aworkspace-demo&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Continue to Bind' }));
    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();

    await waitFor(() => {
      expect(
        screen.getByRole('button', { name: 'Continue to Invoke' }),
      ).not.toBeDisabled();
    });
    fireEvent.click(screen.getByRole('button', { name: 'Continue to Invoke' }));

    expect(await screen.findByTestId('studio-invoke-surface')).toBeTruthy();
    expect(screen.getByText('service:default')).toBeTruthy();
    expect(screen.getByText('services:default')).toBeTruthy();
    expect(screen.getByText('endpoint:chat')).toBeTruthy();
  });

  it('does not continue from Bind to Invoke without backend member identity', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Continue to Bind' }));
    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    expect(
      screen.getByText('Select a Team member before using Invoke.'),
    ).toBeTruthy();

    const continueButton = screen.getByRole('button', {
      name: 'Continue to Invoke',
    });
    expect(continueButton).toBeDisabled();
    fireEvent.click(continueButton);

    expect(screen.queryByTestId('studio-invoke-surface')).toBeNull();
    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get('step')).not.toBe('invoke');
  });

  it('pins Invoke to the selected member instead of exposing every runtime service', async () => {
    mockScopeRuntimeApi.listServices.mockResolvedValueOnce([
      {
        serviceId: 'default',
        displayName: 'workspace-demo',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-default',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with workspace-demo.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
      {
        serviceId: 'billing-api',
        displayName: 'Billing API',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-billing',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with billing.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);

    renderStudioPage(
      '/studio?scopeId=scope-1&memberId=default&step=invoke&tab=invoke',
    );

    expect(await screen.findByTestId('studio-invoke-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText('service:default')).toBeTruthy();
      expect(screen.getByText('member:workspace-demo')).toBeTruthy();
      expect(screen.getByText('services:default')).toBeTruthy();
    });
    expect(screen.queryByText('services:default,billing-api')).toBeNull();
  });

  it('keeps Invoke on the selected member when a stale bind selection exists', async () => {
    mockStudioMembers = [
      ...mockStudioMembers,
      {
        memberId: 'support-member',
        scopeId: 'scope-1',
        displayName: 'support-member',
        description: 'Support member',
        implementationKind: 'workflow',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'support-service',
        lastBoundRevisionId: 'rev-support',
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    mockScopeRuntimeApi.listServices.mockResolvedValue([
      {
        serviceId: 'default',
        displayName: 'workspace-demo',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-default',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with workspace-demo.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
      {
        serviceId: 'support-service',
        displayName: 'support-member',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-support',
        endpoints: [
          {
            endpointId: 'support-chat',
            displayName: 'Support chat',
            kind: 'chat',
            description: 'Chat with support.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Aworkspace-demo&step=bind',
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    fireEvent.click(
      screen.getByRole('button', { name: 'Select bind endpoint' }),
    );

    await replaceStudioRoute(
      '/studio?scopeId=scope-1&member=member%3Asupport-member&step=invoke',
    );

    expect(await screen.findByTestId('studio-invoke-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText('service:support-service')).toBeTruthy();
      expect(screen.getByText('member:support-member')).toBeTruthy();
      expect(screen.getByText('services:support-service')).toBeTruthy();
      expect(screen.getByText('endpoint:support-chat')).toBeTruthy();
    });
    expect(screen.queryByText('service:default')).toBeNull();
  });

  it('shows an invoke empty state when a bound member has no endpoint data', async () => {
    mockStudioMembers = [
      ...mockStudioMembers,
      {
        memberId: 'script-member',
        scopeId: 'scope-1',
        displayName: 'script-alpha',
        description: 'Script member with no endpoints',
        implementationKind: 'script',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'script-alpha',
        lastBoundRevisionId: 'rev-script-alpha',
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    mockScopeRuntimeApi.listServices.mockResolvedValueOnce([
      {
        serviceId: 'script-alpha',
        displayName: 'script-alpha',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-script-alpha',
        endpoints: [],
      },
    ]);

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Ascript-member&step=invoke&tab=invoke',
    );

    expect(await screen.findByTestId('studio-invoke-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText('service:script-alpha')).toBeTruthy();
      expect(screen.getByText('member:script-alpha')).toBeTruthy();
      expect(screen.getByText('services:none')).toBeTruthy();
      expect(screen.getByText('endpoint:no-endpoint')).toBeTruthy();
      expect(
        screen.getByText(/empty:script-alpha cannot be invoked directly yet\./),
      ).toBeTruthy();
    });
  });

  it('requires a fresh explicit-request confirmation before binding a workflow member', async () => {
    mockStudioMembers = mockStudioMembers.map((member) =>
      member.memberId === 'workspace-demo'
        ? { ...member, lastBoundRevisionId: 'rev-2' }
        : member,
    );
    (studioApi.previewExplicitRequests as jest.Mock).mockImplementation(
      (input: { workflowId: string; revisionId: string }) => ({
        workflowId: input.workflowId,
        revisionId: input.revisionId,
        items: [
          {
            callSiteId: 'wf-alpha/request-alpha',
            requestContractDigest: 'digest-alpha',
            userServiceId: 'usvc-alpha',
            method: 'post',
            pathTemplate: '/records/{id}',
            bodyMode: 'json',
            bodyRequired: true,
            responseMode: 'text',
            effectiveRisk: 'write',
            approvalRequired: true,
            allowedExecutionModes: ['interactive'],
          },
        ],
      }),
    );
    mockScopeRuntimeApi.listServices.mockReset();
    mockScopeRuntimeApi.listServices
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([
        {
          serviceId: 'default',
          displayName: 'workspace-demo',
          deploymentStatus: 'Active',
          primaryActorId: 'actor-default',
          endpoints: [
            {
              endpointId: 'chat',
              displayName: 'Chat',
              kind: 'chat',
              description: 'Chat with the published workflow.',
              requestTypeUrl: '',
              responseTypeUrl: '',
            },
          ],
        },
      ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Aworkspace-demo&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Continue to Bind' }));

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText('service:default')).toBeTruthy();
      expect(screen.getByText('candidate:none')).toBeTruthy();
    });

    await act(async () => {
      fireEvent.click(
        screen.getByRole('button', { name: 'Bind current member' }),
      );
    });

    await waitFor(() => {
      expect(studioApi.previewExplicitRequests).toHaveBeenCalledWith({
        scopeId: 'scope-1',
        workflowId: 'workflow-1',
        workflowYaml: expect.stringContaining('name: workspace-demo'),
        inlineWorkflowYamls: {},
        executionMode: 'interactive',
        revisionId: expect.stringMatching(/^rev-/),
      });
      expect(Modal.confirm).toHaveBeenCalledTimes(1);
    });
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();

    const cancelledConfirmation = (Modal.confirm as jest.Mock).mock
      .calls[0]?.[0];
    await act(async () => {
      cancelledConfirmation.onCancel();
    });
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();

    fireEvent.click(
      screen.getByRole('button', { name: 'Bind current member' }),
    );
    await waitFor(() => {
      expect(studioApi.previewExplicitRequests).toHaveBeenCalledTimes(2);
      expect(Modal.confirm).toHaveBeenCalledTimes(2);
    });
    const previewInput = (studioApi.previewExplicitRequests as jest.Mock).mock
      .calls[1]?.[0];
    const confirmedDialog = (Modal.confirm as jest.Mock).mock.calls[1]?.[0];
    await act(async () => {
      await confirmedDialog.onOk();
    });

    await waitFor(() => {
      expect(studioApi.bindMemberWorkflow).toHaveBeenCalledWith({
        scopeId: 'scope-1',
        memberId: 'workspace-demo',
        displayName: 'workspace-demo',
        workflowId: 'workflow-1',
        revisionId: previewInput.revisionId,
        workflowYamls: [previewInput.workflowYaml],
        explicitRequestConfirmations: [
          {
            workflowId: 'workflow-1',
            revisionId: previewInput.revisionId,
            callSiteId: 'wf-alpha/request-alpha',
            requestContractDigest: 'digest-alpha',
            attestedRisk: 'write',
          },
        ],
      });
    });
    expect(previewInput.revisionId).not.toBe('rev-2');
  });

  it('does not expose post-bind Team entry or Team test actions from Studio bind', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aworkspace-demo&step=bind&tab=bindings',
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    expect(
      screen.queryByRole('button', { name: '设为入口并测试 Team' }),
    ).toBeNull();
    expect(screen.queryByRole('button', { name: '测试 Team' })).toBeNull();
    await waitFor(() =>
      expect(screen.getByText('service:default')).toBeTruthy(),
    );
    expect(studioApi.setTeamEntryMember).not.toHaveBeenCalled();
    expect(window.location.pathname).toBe('/studio');
    expect(
      new URLSearchParams(window.location.search).get('testTeam'),
    ).toBeNull();
    expect(mockConsoleToast.warning).not.toHaveBeenCalledWith(
      expect.stringContaining('读模型还没有确认新入口成员'),
    );
  });

  it('keeps pending member binding from promoting the bind selection', async () => {
    mockScopeRuntimeApi.listServices.mockReset();
    mockScopeRuntimeApi.listServices
      .mockResolvedValueOnce([])
      .mockResolvedValue([
        {
          serviceId: 'member-workspace-demo',
          displayName: 'workspace-demo',
          deploymentStatus: 'Active',
          primaryActorId: 'actor-default',
          endpoints: [
            {
              endpointId: 'chat',
              displayName: 'Chat',
              kind: 'chat',
              description: 'Chat with workspace-demo.',
              requestTypeUrl: '',
              responseTypeUrl: '',
            },
          ],
        },
      ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);
    (studioApi.getMemberBindingRun as jest.Mock).mockResolvedValue({
      bindingRunId: 'bind-member-workflow-1',
      scopeId: 'scope-1',
      memberId: 'workspace-demo',
      status: 'platform_binding_pending',
      stateVersion: 11,
      failure: null,
      updatedAt: '2026-04-27T08:15:01Z',
    });

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Aworkspace-demo&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Continue to Bind' }));
    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText('member:workspace-demo')).toBeTruthy();
      expect(screen.getByText('service:no-service')).toBeTruthy();
      expect(screen.getByText('services:none')).toBeTruthy();
      expect(screen.getByText('candidate:workspace-demo')).toBeTruthy();
    });

    await act(async () => {
      fireEvent.click(
        screen.getByRole('button', { name: 'Bind current member' }),
      );
    });

    await waitFor(() => {
      expect(studioApi.getMemberBindingRun).toHaveBeenCalled();
    });
    expect(screen.getByText('service:no-service')).toBeTruthy();
    expect(screen.getByText('services:none')).toBeTruthy();
    expect(screen.queryByText('service:member-workspace-demo')).toBeNull();
    expect(screen.queryByText('services:member-workspace-demo')).toBeNull();

    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get('member')).toBe('member:workspace-demo');
  });

  it('keeps accepted member binding pending when the run readmodel is not visible yet', async () => {
    mockScopeRuntimeApi.listServices.mockReset();
    mockScopeRuntimeApi.listServices.mockResolvedValue([]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);
    const notFoundError = new Error('not found');
    notFoundError.name = 'StudioApiError';
    Object.assign(notFoundError, { status: 404 });
    (studioApi.getMemberBindingRun as jest.Mock).mockRejectedValue(
      notFoundError,
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Aworkspace-demo&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Continue to Bind' }));
    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText('candidate:workspace-demo')).toBeTruthy();
    });

    jest.useFakeTimers();
    try {
      await act(async () => {
        fireEvent.click(
          screen.getByRole('button', { name: 'Bind current member' }),
        );
        await jest.runAllTimersAsync();
      });

      expect(studioApi.getMemberBindingRun).toHaveBeenCalledTimes(8);
      expect(screen.getByText('service:no-service')).toBeTruthy();
      expect(screen.getByText('candidate:workspace-demo')).toBeTruthy();
    } finally {
      jest.useRealTimers();
    }
  });

  it('includes readmodel freshness in pending member binding notices', () => {
    expect(
      buildStudioMemberBindingPendingNotice('workspace-demo', {
        bindingRunId: 'bind-member-workflow-1',
        scopeId: 'scope-1',
        memberId: 'workspace-demo',
        status: 'platform_binding_pending',
        stateVersion: 11,
        failure: null,
        updatedAt: '2026-04-27T08:15:01Z',
      }).message,
    ).toContain('Read model observed v11.');

    expect(
      buildStudioMemberBindingPendingNotice('workspace-demo', null).message,
    ).toContain('Read model has not materialized this run yet.');
  });

  it('normalizes legacy workflow:default links and keeps the bound member contract when switching away and back', async () => {
    mockWorkflowFile = {
      ...mockWorkflowFile,
      workflowId: 'workflow-1',
      name: 'draft2',
      fileName: 'draft2.yaml',
      filePath: '/tmp/workflows/draft2.yaml',
      yaml: 'name: draft2\nsteps: []\n',
      document: {
        ...mockParsedDocument,
        name: 'draft2',
      },
    };
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'workflow-1',
        name: 'draft2',
        description: 'Current draft member',
        fileName: 'draft2.yaml',
        filePath: '/tmp/workflows/draft2.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
      {
        workflowId: 'workflow-2',
        name: 'draft1',
        description: 'Another draft member',
        fileName: 'draft1.yaml',
        filePath: '/tmp/workflows/draft1.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:10:00Z',
      },
    ]);
    const draft2Service = {
      serviceId: 'default',
      displayName: 'draft2',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-default',
      endpoints: [
        {
          endpointId: 'chat',
          displayName: 'Chat',
          kind: 'chat',
          description: 'Chat with the published workflow.',
          requestTypeUrl: '',
          responseTypeUrl: '',
        },
      ],
    };
    let serviceCatalogVisible = false;
    mockScopeRuntimeApi.listServices.mockImplementation(async () => {
      if (!serviceCatalogVisible) {
        serviceCatalogVisible = true;
        return [];
      }

      return [draft2Service];
    });
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);
    (studioApi.getMemberBindingRun as jest.Mock).mockResolvedValueOnce({
      bindingRunId: 'bind-member-workflow-1',
      scopeId: 'scope-1',
      memberId: 'workspace-demo',
      status: 'succeeded',
      result: {
        publishedServiceId: 'default',
        revisionId: 'rev-2',
        implementationKind: 'workflow',
        expectedActorId: 'actor-default',
      },
      failure: null,
      updatedAt: '2026-04-27T08:15:01Z',
    });
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(async () =>
      mockBuildServiceRevisionCatalog({
        serviceId: 'default',
        displayName: 'draft2',
        workflowName: 'draft2',
      }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&step=build&focus=workflow%3Adefault&tab=studio',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Continue to Bind' }));
    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText('candidate:draft2')).toBeTruthy();
    });

    await act(async () => {
      fireEvent.click(
        screen.getByRole('button', { name: 'Bind current member' }),
      );
    });

    await waitFor(() => {
      expect(screen.getByText('service:default')).toBeTruthy();
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('member:workspace-demo');
      expect(searchParams.get('focus')).toBeNull();
      expect(searchParams.get('step')).toBe('bind');
    });

    const rail = await screen.findByLabelText('Team members');
    fireEvent.click(within(rail).getByRole('button', { name: 'draft1' }));
    fireEvent.click(within(rail).getByRole('button', { name: 'draft2' }));

    await waitFor(() => {
      expect(screen.getByText('service:default')).toBeTruthy();
      expect(screen.queryByText('service:no-service')).toBeNull();
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('member:workspace-demo');
    });
  });

  it('keeps Bind pinned to the member that was just bound instead of the scope default binding target', async () => {
    mockWorkflowFile = {
      ...mockWorkflowFile,
      name: 'draft1',
      fileName: 'draft1.yaml',
      filePath: '/tmp/workflows/draft1.yaml',
      yaml: 'name: draft1\nsteps: []\n',
      document: {
        ...mockParsedDocument,
        name: 'draft1',
      },
    };
    mockStudioMembers = [
      ...mockStudioMembers,
      {
        memberId: 'draft1',
        scopeId: 'scope-1',
        displayName: 'draft1',
        description: 'Current draft member',
        implementationKind: 'workflow',
        lifecycleStage: 'created',
        publishedServiceId: 'draft1',
        lastBoundRevisionId: null,
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
      {
        memberId: 'joker',
        scopeId: 'scope-1',
        displayName: 'joker',
        description: 'Joker workflow member',
        implementationKind: 'workflow',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'joker',
        lastBoundRevisionId: 'rev-joker',
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'workflow-1',
        name: 'draft1',
        description: 'Current draft member',
        fileName: 'draft1.yaml',
        filePath: '/tmp/workflows/draft1.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
      {
        workflowId: 'workflow-2',
        name: 'draft2',
        description: 'Another draft member',
        fileName: 'draft2.yaml',
        filePath: '/tmp/workflows/draft2.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:10:00Z',
      },
    ]);
    let draft1ServicePublished = false;
    const draft2Service = {
      serviceId: 'draft2',
      displayName: 'draft2',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-draft2',
      endpoints: [
        {
          endpointId: 'chat',
          displayName: 'Chat',
          kind: 'chat',
          description: 'Chat with draft2.',
          requestTypeUrl: '',
          responseTypeUrl: '',
        },
      ],
    };
    const draft1Service = {
      serviceId: 'draft1',
      displayName: 'draft1',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-draft1',
      endpoints: [
        {
          endpointId: 'chat',
          displayName: 'Chat',
          kind: 'chat',
          description: 'Chat with draft1.',
          requestTypeUrl: '',
          responseTypeUrl: '',
        },
      ],
    };
    mockScopeRuntimeApi.listServices.mockImplementation(async () =>
      draft1ServicePublished ? [draft2Service, draft1Service] : [draft2Service],
    );
    (studioApi.bindMemberWorkflow as jest.Mock).mockImplementationOnce(
      async () => {
        draft1ServicePublished = true;
        return {
          status: 'accepted',
          bindingRunId: 'bind-draft1',
          scopeId: 'scope-1',
          memberId: 'draft1',
        };
      },
    );
    (studioApi.getMemberBindingRun as jest.Mock).mockResolvedValueOnce({
      bindingRunId: 'bind-draft1',
      scopeId: 'scope-1',
      memberId: 'draft1',
      status: 'succeeded',
      result: {
        publishedServiceId: 'draft1',
        revisionId: 'rev-draft1',
        implementationKind: 'workflow',
        expectedActorId: 'actor-draft1',
      },
      failure: null,
      updatedAt: '2026-04-27T08:15:01Z',
    });
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValue({
      available: true,
      scopeId: 'scope-1',
      serviceId: 'draft2',
      displayName: 'draft2',
      serviceKey: 'scope-1:default:draft2',
      defaultServingRevisionId: 'rev-draft2',
      activeServingRevisionId: 'rev-draft2',
      deploymentId: 'dep-draft2',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-draft2',
      updatedAt: '2026-03-26T08:00:00Z',
      revisions: [],
    });
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        mockBuildServiceRevisionCatalog({
          serviceId,
          displayName: serviceId,
          workflowName: serviceId,
          revisionId: serviceId === 'draft1' ? 'rev-draft1' : 'rev-draft2',
        }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Adraft1&focus=workflow%3Aworkflow-1&step=bind&tab=bindings',
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    expect(await screen.findByText('candidate:draft1')).toBeTruthy();
    expect(screen.queryByText('service:draft2')).toBeNull();
    expect(await screen.findByText('service:no-service')).toBeTruthy();

    await act(async () => {
      fireEvent.click(
        screen.getByRole('button', { name: 'Bind current member' }),
      );
    });

    await waitFor(() => {
      expect(studioApi.bindMemberWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: 'scope-1',
          memberId: 'draft1',
          displayName: 'draft1',
          workflowId: 'workflow-1',
        }),
      );
    });
    await waitFor(() => {
      expect(studioApi.getMemberBindingRun).toHaveBeenCalledWith(
        'scope-1',
        'draft1',
        'bind-draft1',
      );
      expect(
        mockScopeRuntimeApi.listServices.mock.calls.length,
      ).toBeGreaterThanOrEqual(2);
    });
    expect(studioApi.bindScopeWorkflow).not.toHaveBeenCalled();
    await waitFor(() => {
      expect(screen.getByTestId('studio-context-title')).toHaveTextContent(
        'draft1',
      );
      expect(screen.getByText('service:draft1')).toBeTruthy();
      expect(screen.getByText('services:draft1')).toBeTruthy();
      expect(screen.getByText('candidate:none')).toBeTruthy();
    });
    expect(screen.queryByText('service:draft2')).toBeNull();
  });

  it('does not infer a published service from a display name after a completed member bind', async () => {
    mockWorkflowFile = {
      ...mockWorkflowFile,
      workflowId: 'wf-draft-alpha',
      name: 'draft-alpha',
      fileName: 'draft-alpha.yaml',
      filePath: '/tmp/workflows/draft-alpha.yaml',
      yaml: 'name: draft-alpha\nsteps: []\n',
      document: {
        ...mockParsedDocument,
        name: 'draft-alpha',
      },
    };
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        memberId: 'm-draft-alpha',
        scopeId: 'scope-1',
        displayName: 'draft-alpha',
        description: 'Draft member awaiting a published service contract.',
        implementationKind: 'workflow',
        lifecycleStage: 'created',
        publishedServiceId: '',
        lastBoundRevisionId: null,
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'wf-draft-alpha',
        name: 'draft-alpha',
        description: 'Draft member awaiting a published service contract.',
        fileName: 'draft-alpha.yaml',
        filePath: '/tmp/workflows/draft-alpha.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
    ]);
    mockScopeRuntimeApi.listServices.mockResolvedValue([
      {
        serviceId: 'svc-same-display-name',
        displayName: 'draft-alpha',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-unrelated-service',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'An unrelated service with the same display name.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);
    (studioApi.getMemberBindingRun as jest.Mock).mockResolvedValueOnce({
      bindingRunId: 'bind-m-draft-alpha',
      scopeId: 'scope-1',
      memberId: 'm-draft-alpha',
      status: 'succeeded',
      failure: null,
      updatedAt: '2026-04-27T08:15:01Z',
    });
    (studioApi.bindMemberWorkflow as jest.Mock).mockResolvedValueOnce({
      status: 'accepted',
      bindingRunId: 'bind-m-draft-alpha',
      scopeId: 'scope-1',
      memberId: 'm-draft-alpha',
    });

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Am-draft-alpha&focus=workflow%3Awf-draft-alpha&step=bind&tab=bindings',
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    expect(await screen.findByText('candidate:draft-alpha')).toBeTruthy();
    expect(screen.getByText('service:no-service')).toBeTruthy();
    expect(screen.getByText('services:none')).toBeTruthy();

    await act(async () => {
      fireEvent.click(
        screen.getByRole('button', { name: 'Bind current member' }),
      );
    });

    await waitFor(() => {
      expect(studioApi.bindMemberWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: 'scope-1',
          memberId: 'm-draft-alpha',
          workflowId: 'wf-draft-alpha',
        }),
      );
      expect(studioApi.getMemberBindingRun).toHaveBeenCalledWith(
        'scope-1',
        'm-draft-alpha',
        'bind-m-draft-alpha',
      );
    });

    await waitFor(() => {
      expect(screen.getByText('service:no-service')).toBeTruthy();
      expect(screen.getByText('services:none')).toBeTruthy();
      expect(screen.getByText('candidate:draft-alpha')).toBeTruthy();
    });
    expect(screen.queryByText('service:svc-same-display-name')).toBeNull();
    const continueToInvokeButton = screen.getByRole('button', {
      name: 'Continue to Invoke',
    });
    expect(continueToInvokeButton).toBeDisabled();
    fireEvent.click(continueToInvokeButton);
    expect(screen.queryByTestId('studio-invoke-surface')).toBeNull();
    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get('member')).toBe('member:m-draft-alpha');
    expect(searchParams.get('focus')).toBe('workflow:wf-draft-alpha');
    expect(searchParams.get('step')).toBe('bind');
  });

  it('keeps a bound member pending until its API-returned service appears in the catalog', async () => {
    mockWorkflowFile = {
      ...mockWorkflowFile,
      workflowId: 'wf-catalog-delay',
      name: 'catalog-delay',
      fileName: 'catalog-delay.yaml',
      filePath: '/tmp/workflows/catalog-delay.yaml',
      yaml: 'name: catalog-delay\nsteps: []\n',
      document: {
        ...mockParsedDocument,
        name: 'catalog-delay',
      },
    };
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        memberId: 'm-catalog-delay',
        scopeId: 'scope-1',
        displayName: 'catalog-delay',
        description: 'Draft member awaiting service catalog materialization.',
        implementationKind: 'workflow',
        lifecycleStage: 'created',
        publishedServiceId: '',
        lastBoundRevisionId: null,
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'wf-catalog-delay',
        name: 'catalog-delay',
        description: 'Draft member awaiting service catalog materialization.',
        fileName: 'catalog-delay.yaml',
        filePath: '/tmp/workflows/catalog-delay.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
    ]);
    mockScopeRuntimeApi.listServices.mockResolvedValue([]);
    (studioApi.getMemberBindingRun as jest.Mock).mockResolvedValueOnce({
      bindingRunId: 'bind-m-catalog-delay',
      scopeId: 'scope-1',
      memberId: 'm-catalog-delay',
      status: 'succeeded',
      result: {
        publishedServiceId: 'svc-catalog-delay',
        revisionId: 'rev-catalog-delay',
        implementationKind: 'workflow',
        expectedActorId: 'actor-catalog-delay',
      },
      failure: null,
      updatedAt: '2026-04-27T08:15:01Z',
    });
    (studioApi.bindMemberWorkflow as jest.Mock).mockResolvedValueOnce({
      status: 'accepted',
      bindingRunId: 'bind-m-catalog-delay',
      scopeId: 'scope-1',
      memberId: 'm-catalog-delay',
    });

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Am-catalog-delay&focus=workflow%3Awf-catalog-delay&step=bind&tab=bindings',
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    expect(await screen.findByText('candidate:catalog-delay')).toBeTruthy();

    await act(async () => {
      fireEvent.click(
        screen.getByRole('button', { name: 'Bind current member' }),
      );
    });

    await waitFor(() => {
      expect(studioApi.getMemberBindingRun).toHaveBeenCalledWith(
        'scope-1',
        'm-catalog-delay',
        'bind-m-catalog-delay',
      );
    });

    await waitFor(() => {
      expect(screen.getByText('service:no-service')).toBeTruthy();
      expect(screen.getByText('services:none')).toBeTruthy();
      expect(screen.getByText('candidate:catalog-delay')).toBeTruthy();
    });
    expect(screen.queryByText('service:svc-catalog-delay')).toBeNull();
    const continueToInvokeButton = screen.getByRole('button', {
      name: 'Continue to Invoke',
    });
    expect(continueToInvokeButton).toBeDisabled();
    fireEvent.click(continueToInvokeButton);
    expect(screen.queryByTestId('studio-invoke-surface')).toBeNull();
  });

  it('keeps Bind pinned to the selected member after leaving a workflow build surface', async () => {
    mockWorkflowFile = {
      ...mockWorkflowFile,
      name: 'draft1',
      fileName: 'draft1.yaml',
      filePath: '/tmp/workflows/draft1.yaml',
      yaml: 'name: draft1\nsteps: []\n',
      document: {
        ...mockParsedDocument,
        name: 'draft1',
      },
    };
    mockStudioMembers = [
      ...mockStudioMembers,
      {
        memberId: 'joker',
        scopeId: 'scope-1',
        displayName: 'joker',
        description: 'Joker workflow member',
        implementationKind: 'workflow',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'joker',
        lastBoundRevisionId: 'rev-joker',
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'workflow-1',
        name: 'draft1',
        description: 'Current draft member',
        fileName: 'draft1.yaml',
        filePath: '/tmp/workflows/draft1.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
    ]);
    mockScopeRuntimeApi.listServices.mockResolvedValue([
      {
        serviceId: 'joker',
        displayName: 'joker',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-joker',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with joker.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValue({
      available: true,
      scopeId: 'scope-1',
      serviceId: 'joker',
      displayName: 'joker',
      serviceKey: 'scope-1:default:joker',
      defaultServingRevisionId: 'rev-joker',
      activeServingRevisionId: 'rev-joker',
      deploymentId: 'dep-joker',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-joker',
      updatedAt: '2026-03-26T08:00:00Z',
      revisions: [],
    });
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(async () =>
      mockBuildServiceRevisionCatalog({
        serviceId: 'joker',
        displayName: 'joker',
        workflowName: 'joker',
      }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Ajoker&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Bind' }));

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    const rail = await screen.findByLabelText('Team members');
    await waitFor(() => {
      expect(screen.getByTestId('studio-context-title')).toHaveTextContent(
        'joker',
      );
      expect(screen.getByText('service:joker')).toBeTruthy();
      expect(screen.getByText('services:joker')).toBeTruthy();
      expect(screen.getByText('candidate:none')).toBeTruthy();
      expect(
        within(rail).getByRole('button', { name: 'joker' }),
      ).toHaveAttribute('aria-current', 'true');
    });
    within(rail)
      .getAllByRole('button', { name: 'draft1' })
      .forEach((button) => {
        expect(button).not.toHaveAttribute('aria-current', 'true');
      });
  });

  it('keeps workflow build focus when continuing from Build to Bind', async () => {
    mockWorkflowFile = {
      ...mockWorkflowFile,
      workflowId: 'workflow-1',
      name: 'draft1',
      fileName: 'draft1.yaml',
      filePath: '/tmp/workflows/draft1.yaml',
      yaml: 'name: draft1\nsteps: []\n',
      document: {
        ...mockParsedDocument,
        name: 'draft1',
      },
    };
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        memberId: 'draft1',
        displayName: 'draft1',
        lifecycleStage: 'created',
        publishedServiceId: '',
        lastBoundRevisionId: null,
      },
    ];
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'workflow-1',
        name: 'draft1',
        description: 'Current draft member',
        fileName: 'draft1.yaml',
        filePath: '/tmp/workflows/draft1.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
    ]);
    mockScopeRuntimeApi.listServices.mockResolvedValue([]);

    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Adraft1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Continue to Bind' }));

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(window.location.search).toContain('focus=workflow%3Aworkflow-1');
      expect(screen.getByText('candidate:draft1')).toBeTruthy();
      expect(
        screen.getByRole('button', { name: 'Bind current member' }),
      ).toBeTruthy();
    });
  });

  it('keeps Bind active when selecting an unbound Workflow Team member from Bind', async () => {
    mockWorkflowFile = {
      ...mockWorkflowFile,
      workflowId: 'workflow-1',
      name: 'draft1',
      fileName: 'draft1.yaml',
      filePath: '/tmp/workflows/draft1.yaml',
      yaml: 'name: draft1\nsteps: []\n',
      document: {
        ...mockParsedDocument,
        name: 'draft1',
      },
    };
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        memberId: 'draft1',
        displayName: 'draft1',
        lifecycleStage: 'created',
        publishedServiceId: '',
        lastBoundRevisionId: null,
      },
      {
        ...mockStudioMembers[0],
        memberId: 'gagent-1',
        displayName: 'gagent-1',
        implementationKind: 'gagent',
        teamId: 't-alpha',
      },
    ];
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'workflow-1',
        name: 'draft1',
        description: 'Current draft member',
        fileName: 'draft1.yaml',
        filePath: '/tmp/workflows/draft1.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
    ]);
    mockScopeRuntimeApi.listServices.mockResolvedValue([]);

    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Agagent-1&step=bind&tab=bindings',
    );

    const rail = await screen.findByLabelText('Team members');
    fireEvent.click(
      await within(rail).findByRole('button', { name: 'draft1' }),
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('member:draft1');
      expect(searchParams.get('focus')).toBe('workflow:workflow-1');
      expect(searchParams.get('step')).toBe('bind');
      expect(searchParams.get('tab')).toBe('bindings');
      expect(screen.getByText('candidate:draft1')).toBeTruthy();
      expect(
        within(rail).getByRole('button', { name: 'draft1' }),
      ).toHaveAttribute('aria-current', 'true');
    });
  });

  it('keeps Bind active when selecting an unbound GAgent Team member from Bind', async () => {
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        memberId: 'draft1',
        displayName: 'draft1',
        lifecycleStage: 'created',
        publishedServiceId: '',
        lastBoundRevisionId: null,
      },
      {
        ...mockStudioMembers[0],
        memberId: 'gagent-1',
        displayName: 'gagent-1',
        implementationKind: 'gagent',
        lifecycleStage: 'created',
        publishedServiceId: '',
        lastBoundRevisionId: null,
        teamId: 't-alpha',
      },
    ];
    mockScopeRuntimeApi.listServices.mockResolvedValue([]);

    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Adraft1&step=bind&tab=bindings',
    );

    const rail = await screen.findByLabelText('Team members');
    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    fireEvent.click(
      await within(rail).findByRole('button', { name: 'gagent-1' }),
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('member:gagent-1');
      expect(searchParams.get('step')).toBe('bind');
      expect(searchParams.get('tab')).toBe('bindings');
      expect(searchParams.get('focus')).toBeNull();
      expect(screen.getByText('candidate:none')).toBeTruthy();
      expect(
        within(rail).getByRole('button', { name: 'gagent-1' }),
      ).toHaveAttribute('aria-current', 'true');
    });
    expect(screen.queryByTestId('studio-gagent-build-panel')).toBeNull();
  });

  it('keeps Build active when selecting a GAgent Team member from Build', async () => {
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        memberId: 'draft1',
        displayName: 'draft1',
        lifecycleStage: 'created',
        publishedServiceId: '',
        lastBoundRevisionId: null,
      },
      {
        ...mockStudioMembers[0],
        memberId: 'gagent-1',
        displayName: 'gagent-1',
        implementationKind: 'gagent',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'gagent-service',
        lastBoundRevisionId: 'rev-gagent-1',
        teamId: 't-alpha',
      },
    ];
    mockScopeRuntimeApi.listServices.mockResolvedValue([
      {
        serviceId: 'gagent-service',
        displayName: 'gagent-1',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-gagent',
        endpoints: [
          {
            endpointId: 'run',
            displayName: 'Run',
            kind: 'command',
            description: 'Run gagent-1.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);
    mockScopeRuntimeApi.getServiceRevisions.mockResolvedValue(
      mockBuildGAgentServiceRevisionCatalog({
        serviceId: 'gagent-service',
        displayName: 'gagent-1',
      }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Adraft1&focus=workflow%3Aworkflow-1&step=build&tab=studio',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();
    const rail = await screen.findByLabelText('Team members');
    fireEvent.click(
      await within(rail).findByRole('button', { name: 'gagent-1' }),
    );

    expect(await screen.findByTestId('studio-gagent-build-panel')).toBeTruthy();
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('member:gagent-1');
      expect(searchParams.get('step')).toBe('build');
      expect(searchParams.get('tab')).toBe('gagents');
      expect(searchParams.get('focus')).toBeNull();
      expect(
        within(rail).getByRole('button', { name: 'gagent-1' }),
      ).toHaveAttribute('aria-current', 'true');
    });
    expect(screen.queryByTestId('studio-bind-surface')).toBeNull();
  });

  it('switches from GAgent Build to a workflow-backed Team draft without persisting the stale GAgent tab', async () => {
    mockWorkflowFile = {
      ...mockWorkflowFile,
      workflowId: 'workflow-1',
      name: 'draft1',
      fileName: 'draft1.yaml',
      filePath: '/tmp/workflows/draft1.yaml',
      yaml: 'name: draft1\nsteps: []\n',
      document: {
        ...mockParsedDocument,
        name: 'draft1',
      },
    };
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        memberId: 'draft1',
        displayName: 'draft1',
        implementationKind: 'workflow',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'draft1-service',
        lastBoundRevisionId: 'rev-draft1',
        teamId: 't-alpha',
      },
      {
        ...mockStudioMembers[0],
        memberId: 'gagent-1',
        displayName: 'gagent-1',
        implementationKind: 'gagent',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'gagent-service',
        lastBoundRevisionId: 'rev-gagent-1',
        teamId: 't-alpha',
      },
    ];
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'workflow-1',
        name: 'draft1',
        description: 'Current draft member',
        fileName: 'draft1.yaml',
        filePath: '/tmp/workflows/draft1.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
    ]);
    mockScopeRuntimeApi.listServices.mockResolvedValue([
      {
        serviceId: 'draft1-service',
        displayName: 'draft1',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-draft1',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with draft1.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
      {
        serviceId: 'gagent-service',
        displayName: 'gagent-1',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-gagent',
        endpoints: [
          {
            endpointId: 'run',
            displayName: 'Run',
            kind: 'command',
            description: 'Run gagent-1.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        serviceId === 'gagent-service'
          ? mockBuildGAgentServiceRevisionCatalog({
              serviceId: 'gagent-service',
              displayName: 'gagent-1',
            })
          : mockBuildServiceRevisionCatalog({
              serviceId: 'draft1-service',
              displayName: 'draft1',
              workflowName: 'draft1',
            }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Agagent-1&step=build&tab=gagents',
    );

    expect(await screen.findByTestId('studio-gagent-build-panel')).toBeTruthy();
    const rail = await screen.findByLabelText('Team members');
    fireEvent.click(
      await within(rail).findByRole('button', { name: 'draft1' }),
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('workflow:workflow-1');
      expect(searchParams.get('tab')).toBe('studio');
      expect(screen.queryByTestId('studio-gagent-build-panel')).toBeNull();
    });
  });

  it('recovers a direct unbound Workflow Team member Bind link with workflow focus', async () => {
    mockWorkflowFile = {
      ...mockWorkflowFile,
      workflowId: 'workflow-1',
      name: 'draft1',
      fileName: 'draft1.yaml',
      filePath: '/tmp/workflows/draft1.yaml',
      yaml: 'name: draft1\nsteps: []\n',
      document: {
        ...mockParsedDocument,
        name: 'draft1',
      },
    };
    mockStudioMembers = [
      {
        ...mockStudioMembers[0],
        memberId: 'draft1',
        displayName: 'draft1',
        lifecycleStage: 'created',
        publishedServiceId: '',
        lastBoundRevisionId: null,
      },
    ];
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'workflow-1',
        name: 'draft1',
        description: 'Current draft member',
        fileName: 'draft1.yaml',
        filePath: '/tmp/workflows/draft1.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
    ]);
    mockScopeRuntimeApi.listServices.mockResolvedValue([]);

    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Adraft1&step=bind&tab=bindings',
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    const rail = await screen.findByLabelText('Team members');
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('member:draft1');
      expect(searchParams.get('focus')).toBe('workflow:workflow-1');
      expect(searchParams.get('step')).toBe('bind');
      expect(searchParams.get('tab')).toBe('bindings');
      expect(screen.getByText('candidate:draft1')).toBeTruthy();
      expect(
        within(rail).getByRole('button', { name: 'draft1' }),
      ).toHaveAttribute('aria-current', 'true');
    });
  });

  it('pins Bind to the selected published member instead of the scope default route target', async () => {
    mockStudioMembers = [
      ...mockStudioMembers,
      {
        memberId: 'joker',
        scopeId: 'scope-1',
        displayName: 'joker',
        description: 'Joker workflow member',
        implementationKind: 'workflow',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'joker',
        lastBoundRevisionId: 'rev-joker',
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    mockScopeRuntimeApi.listServices.mockResolvedValue([
      {
        serviceId: 'default',
        displayName: 'workspace-demo',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-default',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with workspace-demo.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
      {
        serviceId: 'joker',
        displayName: 'joker',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-joker',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with joker.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce({
      available: true,
      scopeId: 'scope-1',
      serviceId: 'default',
      displayName: 'workspace-demo',
      serviceKey: 'scope-1:default:workspace-demo',
      defaultServingRevisionId: 'rev-2',
      activeServingRevisionId: 'rev-2',
      deploymentId: 'dep-2',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-default',
      updatedAt: '2026-03-26T08:00:00Z',
      revisions: [],
    });
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        mockBuildServiceRevisionCatalog({
          serviceId,
          displayName: serviceId === 'joker' ? 'joker' : 'workspace-demo',
          workflowName: serviceId === 'joker' ? 'joker' : 'workspace-demo',
        }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Ajoker&step=bind&tab=bindings',
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId('studio-context-title')).toHaveTextContent(
        'joker',
      );
      expect(screen.getByText('service:joker')).toBeTruthy();
      expect(screen.getByText('services:joker')).toBeTruthy();
    });
    expect(screen.queryByText('services:default,joker')).toBeNull();
    expect(screen.getByText('candidate:none')).toBeTruthy();
  });

  it('resolves Bind to the published member contract when a workflow focus already maps to that member', async () => {
    mockWorkflowFile = {
      ...mockWorkflowFile,
      name: 'joker',
      fileName: 'joker.yaml',
      filePath: '/tmp/workflows/joker.yaml',
      yaml: 'name: joker\nsteps: []\n',
      document: {
        ...mockParsedDocument,
        name: 'joker',
      },
    };
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'workflow-1',
        name: 'joker',
        description: 'Current joker draft',
        fileName: 'joker.yaml',
        filePath: '/tmp/workflows/joker.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
    ]);
    mockScopeRuntimeApi.listServices.mockResolvedValueOnce([
      {
        serviceId: 'default',
        displayName: 'workspace-demo',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-default',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with workspace-demo.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
      {
        serviceId: 'joker',
        displayName: 'joker',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-joker',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with joker.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce({
      available: true,
      scopeId: 'scope-1',
      serviceId: 'default',
      displayName: 'workspace-demo',
      serviceKey: 'scope-1:default:workspace-demo',
      defaultServingRevisionId: 'rev-default',
      activeServingRevisionId: 'rev-default',
      deploymentId: 'dep-default',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-default',
      updatedAt: '2026-03-26T08:00:00Z',
      revisions: [],
    });
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        mockBuildServiceRevisionCatalog({
          serviceId,
          displayName: serviceId === 'joker' ? 'joker' : 'workspace-demo',
          workflowName: serviceId === 'joker' ? 'joker' : 'workspace-demo',
        }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&step=bind&tab=bindings',
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId('studio-context-title')).toHaveTextContent(
        'joker',
      );
      expect(screen.getByText('service:joker')).toBeTruthy();
      expect(screen.getByText('services:joker')).toBeTruthy();
      expect(screen.getByText('candidate:none')).toBeTruthy();
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('step')).toBe('bind');
      expect(searchParams.get('member')).toBe('workflow:workflow-1');
      expect(searchParams.get('memberId')).toBeNull();
      expect(searchParams.get('focus')).toBeNull();
    });
    expect(screen.queryByText('service:default')).toBeNull();
    expect(screen.queryByText('service:no-service')).toBeNull();
  });

  it('keeps the current bind surface active when switching members from the rail', async () => {
    mockStudioMembers = [
      ...mockStudioMembers,
      {
        memberId: 'joker',
        scopeId: 'scope-1',
        displayName: 'joker',
        description: 'Joker workflow member',
        implementationKind: 'workflow',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'joker',
        lastBoundRevisionId: 'rev-joker',
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    mockScopeRuntimeApi.listServices.mockResolvedValueOnce([
      {
        serviceId: 'default',
        displayName: 'workspace-demo',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-default',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with workspace-demo.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
      {
        serviceId: 'joker',
        displayName: 'joker',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-joker',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with joker.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce({
      available: true,
      scopeId: 'scope-1',
      serviceId: 'default',
      displayName: 'workspace-demo',
      serviceKey: 'scope-1:default:workspace-demo',
      defaultServingRevisionId: 'rev-2',
      activeServingRevisionId: 'rev-2',
      deploymentId: 'dep-2',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-default',
      updatedAt: '2026-03-26T08:00:00Z',
      revisions: [],
    });
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        mockBuildServiceRevisionCatalog({
          serviceId,
          displayName: serviceId === 'joker' ? 'joker' : 'workspace-demo',
          workflowName: serviceId === 'joker' ? 'joker' : 'workspace-demo',
        }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Aworkspace-demo&step=bind&tab=bindings',
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText('service:default')).toBeTruthy();
    });

    const rail = await screen.findByLabelText('Team members');
    fireEvent.click(within(rail).getByRole('button', { name: 'joker' }));

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId('studio-context-title')).toHaveTextContent(
        'joker',
      );
      expect(screen.getByText('service:joker')).toBeTruthy();
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('step')).toBe('bind');
      expect(searchParams.get('member')).toBe('member:joker');
      expect(searchParams.get('memberId')).toBeNull();
    });
    expect(screen.queryByTestId('studio-invoke-surface')).toBeNull();
  });

  it('keeps the current bind surface active when switching to a workflow draft from the rail', async () => {
    mockWorkflowFile = {
      ...mockWorkflowFile,
      name: 'draft1',
      fileName: 'draft1.yaml',
      filePath: '/tmp/workflows/draft1.yaml',
      yaml: 'name: draft1\nsteps: []\n',
      document: {
        ...mockParsedDocument,
        name: 'draft1',
      },
    };
    mockStudioMembers = [
      ...mockStudioMembers,
      {
        memberId: 'joker',
        scopeId: 'scope-1',
        displayName: 'joker',
        description: 'Joker workflow member',
        implementationKind: 'workflow',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'joker',
        lastBoundRevisionId: 'rev-joker',
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'workflow-1',
        name: 'draft1',
        description: 'Current draft member',
        fileName: 'draft1.yaml',
        filePath: '/tmp/workflows/draft1.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
    ]);
    mockScopeRuntimeApi.listServices.mockResolvedValueOnce([
      {
        serviceId: 'joker',
        displayName: 'joker',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-joker',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with joker.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce({
      available: true,
      scopeId: 'scope-1',
      serviceId: 'joker',
      displayName: 'joker',
      serviceKey: 'scope-1:default:joker',
      defaultServingRevisionId: 'rev-joker',
      activeServingRevisionId: 'rev-joker',
      deploymentId: 'dep-joker',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-joker',
      updatedAt: '2026-03-26T08:00:00Z',
      revisions: [],
    });
    mockScopeRuntimeApi.getServiceRevisions.mockImplementationOnce(async () =>
      mockBuildServiceRevisionCatalog({
        serviceId: 'joker',
        displayName: 'joker',
        workflowName: 'joker',
      }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Ajoker&step=bind&tab=bindings',
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText('member:joker')).toBeTruthy();
      expect(screen.getByText('service:joker')).toBeTruthy();
    });

    const rail = await screen.findByLabelText('Team members');
    fireEvent.click(within(rail).getByRole('button', { name: 'draft1' }));

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId('studio-context-title')).toHaveTextContent(
        'draft1',
      );
      expect(screen.getByText('service:no-service')).toBeTruthy();
      expect(screen.getByText('services:none')).toBeTruthy();
      expect(screen.getByText('candidate:draft1')).toBeTruthy();
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('step')).toBe('bind');
      expect(searchParams.get('member')).toBe('workflow:workflow-1');
      expect(searchParams.get('focus')).toBeNull();
      expect(searchParams.get('memberId')).toBeNull();
    });
    expect(screen.queryByTestId('studio-workflow-build-panel')).toBeNull();
  });

  it('does not resurrect a deleted workflow step when another node is selected afterwards', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'approve_step' })).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'approve_step' }));
    await waitFor(() => {
      expect(screen.getByLabelText('Step ID')).toHaveValue('approve_step');
    });

    fireEvent.click(screen.getByRole('button', { name: 'Delete step' }));

    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'approve_step' })).toBeNull();
      expect(screen.getByLabelText('Step ID')).toHaveValue('draft_step');
    });

    fireEvent.click(screen.getByRole('button', { name: 'draft_step' }));

    await waitFor(() => {
      expect(screen.getByLabelText('Step ID')).toHaveValue('draft_step');
      expect(screen.queryByRole('button', { name: 'approve_step' })).toBeNull();
    });
  });

  it('does not resurrect a canvas-deleted workflow step after adding another node', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'approve_step' })).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'approve_step' }));
    await waitFor(() => {
      expect(screen.getByLabelText('Step ID')).toHaveValue('approve_step');
    });

    fireEvent.click(
      screen.getByRole('button', { name: 'Delete selected step on canvas' }),
    );

    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'approve_step' })).toBeNull();
      expect(screen.getByLabelText('Step ID')).toHaveValue('draft_step');
    });

    fireEvent.click(screen.getByRole('button', { name: 'Add step' }));

    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'approve_step' })).toBeNull();
      expect(screen.getByRole('button', { name: 'llm_step' })).toBeTruthy();
    });
  });

  it('does not re-persist removed create-team draft params after saving', async () => {
    renderStudioPage(
      '/studio?focus=workflow%3Aworkflow-1&tab=studio&teamMode=create&teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3',
    );

    const editor = await screen.findByLabelText('定义 YAML');
    fireEvent.change(editor, {
      target: {
        value: 'name: workspace-demo\nsteps:\n  - id: approve_step\n',
      },
    });

    const saveButton = screen.getByRole('button', { name: 'Save draft' });
    await waitFor(() => {
      expect(saveButton).toBeEnabled();
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(window.location.pathname).toBe('/studio');
      expect(searchParams.get('member')).toBe('workflow:workflow-1');
      expect(searchParams.get('focus')).toBeNull();
      expect(searchParams.get('teamMode')).toBeNull();
      expect(searchParams.get('teamName')).toBeNull();
      expect(searchParams.get('entryName')).toBeNull();
      expect(searchParams.get('teamDraftWorkflowId')).toBeNull();
      expect(searchParams.get('teamDraftWorkflowName')).toBeNull();
    });
  });

  it('drops removed create-team params when the route switches to a different workflow', async () => {
    renderStudioPage(
      '/studio?focus=workflow%3Aworkflow-1&tab=studio&teamMode=create&teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3&teamDraftWorkflowId=workflow-1&teamDraftWorkflowName=workspace-demo',
    );

    expect(await screen.findByText('DAG Canvas')).toBeTruthy();

    await replaceStudioRoute(
      '/studio?focus=workflow%3Aworkflow-2&tab=studio&teamMode=create&teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3&teamDraftWorkflowId=workflow-1&teamDraftWorkflowName=workspace-demo',
    );

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('workflow:workflow-2');
      expect(searchParams.get('focus')).toBeNull();
      expect(searchParams.get('teamMode')).toBeNull();
      expect(searchParams.get('teamName')).toBeNull();
      expect(searchParams.get('entryName')).toBeNull();
      expect(searchParams.get('teamDraftWorkflowId')).toBeNull();
      expect(searchParams.get('teamDraftWorkflowName')).toBeNull();
    });
  });

  it('keeps Studio workflow saves pinned to the current scope route', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    const editor = await screen.findByLabelText('定义 YAML');
    fireEvent.change(editor, {
      target: {
        value: 'name: workspace-demo\nsteps:\n  - id: approve_step\n',
      },
    });

    const saveButton = screen.getByRole('button', { name: 'Save draft' });
    await waitFor(() => {
      expect(saveButton).toBeEnabled();
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowId: 'workflow-1',
          scopeId: 'scope-1',
          directoryId: 'dir-1',
          workflowName: 'workspace-demo',
        }),
      );
    });
  });

  it('keeps a backend Workflow member route selected after saving its draft', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aworkspace-demo&focus=workflow%3Aworkflow-1&tab=studio',
    );

    const editor = await screen.findByLabelText('定义 YAML');
    fireEvent.change(editor, {
      target: {
        value: 'name: workspace-demo\nsteps:\n  - id: approve_step\n',
      },
    });

    const saveButton = screen.getByRole('button', { name: 'Save draft' });
    await waitFor(() => {
      expect(saveButton).toBeEnabled();
    });
    fireEvent.click(saveButton);

    const rail = await screen.findByLabelText('Team members');
    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowId: 'workflow-1',
          scopeId: 'scope-1',
          directoryId: 'dir-1',
          workflowName: 'workspace-demo',
        }),
      );

      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('member:workspace-demo');
      expect(searchParams.get('focus')).toBe('workflow:workflow-1');
      expect(searchParams.get('step')).toBe('build');
      expect(searchParams.get('tab')).toBe('studio');
      expect(
        within(rail).getByRole('button', { name: 'workspace-demo' }),
      ).toHaveAttribute('aria-current', 'true');
    });
  });

  it('marks the first scoped save of a committed workflow as create-draft work', async () => {
    (studioApi.getWorkflow as jest.Mock).mockResolvedValueOnce({
      ...mockWorkflowFile,
      draftExists: false,
    });

    renderStudioPage('/studio?scopeId=scope-1&workflow=workflow-1&tab=studio');

    const editor = await screen.findByLabelText('定义 YAML');
    fireEvent.change(editor, {
      target: {
        value: 'name: workspace-demo\nsteps:\n  - id: approve_step\n',
      },
    });

    const saveButton = screen.getByRole('button', { name: 'Save draft' });
    await waitFor(() => {
      expect(saveButton).toBeEnabled();
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowId: 'workflow-1',
          draftExists: false,
          scopeId: 'scope-1',
          directoryId: 'dir-1',
          workflowName: 'workspace-demo',
        }),
      );
    });
  });

  it('keeps the toolbar save action enabled when the draft falls back to the default directory', async () => {
    (studioApi.getWorkflow as jest.Mock).mockResolvedValueOnce({
      ...mockWorkflowFile,
      directoryId: '',
      directoryLabel: '',
    });

    renderStudioPage('/studio?workflow=workflow-1&tab=studio');

    const editor = await screen.findByLabelText('定义 YAML');
    fireEvent.change(editor, {
      target: {
        value: 'name: workspace-demo\nsteps:\n  - id: approve_step\n',
      },
    });

    const toolbarSaveButton = screen.getByRole('button', {
      name: 'Save draft',
    });
    await waitFor(() => {
      expect(toolbarSaveButton).toBeEnabled();
    });
    fireEvent.click(toolbarSaveButton);

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowId: 'workflow-1',
          directoryId: 'dir-1',
          workflowName: 'workspace-demo',
        }),
      );
    });
  });

  it('falls back to the existing workflow when the removed draft route flag is present', async () => {
    renderStudioPage('/studio?draft=new');

    expect(await screen.findByText('DAG Canvas')).toBeTruthy();
    const rail = await screen.findByLabelText('Team members');
    expect(within(rail).queryByRole('button', { name: 'draft' })).toBeNull();

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('draft')).toBeNull();
      expect(searchParams.get('member')).toBe('workflow:workflow-1');
      expect(searchParams.get('focus')).toBeNull();
    });
  });

  it('does not auto-create a draft when Studio opens without any team members', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
      workflowStorageMode: 'scope',
    });
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);

    renderStudioPage('/studio?scopeId=scope-1&tab=studio');

    expect(await screen.findByTestId('studio-empty-member-state')).toBeTruthy();
    expect(screen.getByTestId('studio-context-title')).toHaveTextContent(
      'Select a member',
    );
    expect(
      screen.getByLabelText('Create member from empty state'),
    ).toBeTruthy();
    expect(screen.queryByText('DAG Canvas')).toBeNull();

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('draft')).toBeNull();
      expect(searchParams.get('focus')).toBeNull();
      expect(searchParams.get('tab')).toBe('studio');
    });
  });

  it('recovers to an explicit member-selection empty state when the route points at a missing workflow', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
      workflowStorageMode: 'scope',
    });
    (studioApi.listWorkflows as jest.Mock).mockResolvedValue([]);
    (studioApi.getWorkflow as jest.Mock).mockRejectedValueOnce(
      new Error('Not Found'),
    );
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);

    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Adraft&tab=studio',
    );

    await waitFor(() => {
      expect(studioApi.getWorkflow).toHaveBeenCalledWith('draft', 'scope-1');
    });

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('draft')).toBeNull();
      expect(searchParams.get('focus')).toBeNull();
      expect(searchParams.get('tab')).toBe('studio');
    });

    expect(await screen.findByTestId('studio-empty-member-state')).toBeTruthy();
    expect(screen.getByTestId('studio-context-title')).toHaveTextContent(
      'Select a member',
    );
    expect(screen.queryByText('DAG Canvas')).toBeNull();
  });

  it('ignores the legacy playground handoff route flag and opens the existing workflow workspace', async () => {
    renderStudioPage('/studio?draft=new&legacy=playground');

    expect(await screen.findByText('DAG Canvas')).toBeTruthy();
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('legacy')).toBeNull();
      expect(searchParams.get('draft')).toBeNull();
      expect(searchParams.get('member')).toBe('workflow:workflow-1');
      expect(searchParams.get('focus')).toBeNull();
    });
  });

  it('hydrates the workflow dry-run prompt from the route query', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    renderStudioPage(
      '/studio?focus=template%3Apublished-demo&prompt=Continue%20this%20workflow%20in%20Studio',
    );

    await waitFor(() => {
      expect(studioApi.getTemplateWorkflow).toHaveBeenCalledWith(
        'published-demo',
      );
    });

    expect(
      (await screen.findByLabelText(
        'Workflow dry run input',
      )) as HTMLTextAreaElement,
    ).toHaveValue('Continue this workflow in Studio');
  });

  it('runs workflow dry-run with pending step prompt edits', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(await screen.findByText('DAG Canvas')).toBeTruthy();
    await waitFor(() => {
      expect(mockLastWorkflowBuildPanelProps?.buildWorkflowYamls).toEqual(
        expect.any(Function),
      );
    });
    (studioApi.serializeYaml as jest.Mock).mockClear();

    const workflowYamls =
      await mockLastWorkflowBuildPanelProps.buildWorkflowYamls({
        stepId: 'draft_step',
        draft: {
          kind: 'step',
          capability: null,
          id: 'draft_step',
          type: 'llm_call',
          targetRole: 'assistant',
          next: 'approve_step',
          parametersText: JSON.stringify(
            {
              prompt_prefix: 'Translate the input to English.',
            },
            null,
            2,
          ),
          branchesText: '{}',
        },
      });

    expect(workflowYamls).toEqual([
      expect.stringContaining('prompt_prefix: Translate the input to English.'),
    ]);
    expect(studioApi.serializeYaml).toHaveBeenCalledTimes(1);
    expect(studioApi.serializeYaml).toHaveBeenCalledWith(
      expect.objectContaining({
        document: expect.objectContaining({
          steps: expect.arrayContaining([
            expect.objectContaining({
              id: 'draft_step',
              parameters: expect.objectContaining({
                prompt_prefix: 'Translate the input to English.',
              }),
            }),
          ]),
        }),
      }),
    );
  });

  it('shows the published template graph in the Studio editor', async () => {
    renderStudioPage('/studio?focus=template%3Apublished-demo&tab=workflows');

    await waitFor(() => {
      expect(studioApi.getTemplateWorkflow).toHaveBeenCalledWith(
        'published-demo',
      );
    });

    expect(await screen.findByText('DAG Canvas')).toBeTruthy();
    expect(screen.getByTestId('studio-context-title')).toHaveTextContent(
      'published-demo',
    );
    await waitFor(() => {
      expect(screen.getByTestId('workflow-graph-node-count')).toHaveTextContent(
        '2',
      );
    });
  });

  it('prefers the active scope binding workflow when Studio opens in a team context', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'workflow-2',
        name: 'other-workflow',
        description: 'Other workflow',
        fileName: 'other-workflow.yaml',
        filePath: '/tmp/workflows/other-workflow.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
      {
        workflowId: 'workflow-1',
        name: 'workspace-demo',
        description: 'Workspace workflow',
        fileName: 'workspace-demo.yaml',
        filePath: '/tmp/workflows/workspace-demo.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 2,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
    ]);

    renderStudioPage('/studio?scopeId=scope-1&tab=studio');

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('workflow:workflow-1');
      expect(searchParams.get('focus')).toBeNull();
      expect(searchParams.get('tab')).toBe('studio');
    });
  });

  it('keeps team members in recent-first order when selecting from the rail', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'workflow-2',
        name: 'other-workflow',
        description: 'Other workflow',
        fileName: 'other-workflow.yaml',
        filePath: '/tmp/workflows/other-workflow.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
      {
        workflowId: 'workflow-1',
        name: 'workspace-demo',
        description: 'Workspace workflow',
        fileName: 'workspace-demo.yaml',
        filePath: '/tmp/workflows/workspace-demo.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 2,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
    ]);

    renderStudioPage('/studio?scopeId=scope-1&tab=studio');

    const rail = await screen.findByLabelText('Team members');
    await within(rail).findByText('other-workflow');
    const workspaceButtonsBefore = within(rail).getAllByRole('button', {
      name: 'workspace-demo',
    });
    const workspaceButtonBefore = workspaceButtonsBefore[0];
    const otherWorkflowButtonBefore = within(rail).getByRole('button', {
      name: 'other-workflow',
    });

    expect(workspaceButtonBefore).toBeTruthy();
    expect(workspaceButtonsBefore).toHaveLength(1);
    expect(otherWorkflowButtonBefore).toBeTruthy();
    if (!workspaceButtonBefore || !otherWorkflowButtonBefore) {
      throw new Error(
        'Expected both workflow buttons before checking their order.',
      );
    }
    expect(
      workspaceButtonBefore.compareDocumentPosition(otherWorkflowButtonBefore) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();

    fireEvent.click(otherWorkflowButtonBefore);

    await waitFor(() => {
      expect(
        within(rail).getAllByRole('button', { name: 'workspace-demo' }),
      ).toHaveLength(1);
      expect(
        within(rail).getByRole('button', { name: 'other-workflow' }),
      ).toBeTruthy();
    });

    const workspaceButtonsAfter = within(rail).getAllByRole('button', {
      name: 'workspace-demo',
    });
    const workspaceButtonAfter = workspaceButtonsAfter[0];
    const otherWorkflowButtonAfter = within(rail).getByRole('button', {
      name: 'other-workflow',
    });
    const railButtonsAfter = within(rail).getAllByRole('button');

    expect(otherWorkflowButtonAfter).toBeTruthy();
    expect(workspaceButtonAfter).toBeTruthy();
    expect(workspaceButtonsAfter).toHaveLength(1);
    expect(railButtonsAfter.indexOf(otherWorkflowButtonAfter)).toBeLessThan(
      railButtonsAfter.indexOf(workspaceButtonAfter),
    );
  });

  it('highlights the newly selected workflow instead of keeping the previous service selected', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'workflow-2',
        name: 'other-workflow',
        description: 'Other workflow',
        fileName: 'other-workflow.yaml',
        filePath: '/tmp/workflows/other-workflow.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
      {
        workflowId: 'workflow-1',
        name: 'workspace-demo',
        description: 'Workspace workflow',
        fileName: 'workspace-demo.yaml',
        filePath: '/tmp/workflows/workspace-demo.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 2,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
    ]);

    renderStudioPage(
      '/studio?scopeId=scope-1&memberId=default&focus=workflow%3Aworkflow-1&tab=studio',
    );

    const rail = await screen.findByLabelText('Team members');
    const workspaceButton = await within(rail).findByRole('button', {
      name: 'workspace-demo',
    });
    const otherWorkflowButton = within(rail).getByRole('button', {
      name: 'other-workflow',
    });

    fireEvent.click(otherWorkflowButton);

    await waitFor(() => {
      expect(otherWorkflowButton).toHaveAttribute('aria-current', 'true');
      expect(workspaceButton).not.toHaveAttribute('aria-current', 'true');
    });
  });

  it('surfaces the currently selected workflow member ahead of older published members', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: 'workflow-1',
        name: 'draft1',
        description: 'Current draft member',
        fileName: 'draft1.yaml',
        filePath: '/tmp/workflows/draft1.yaml',
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      },
    ]);
    mockScopeRuntimeApi.listServices.mockResolvedValueOnce([
      {
        serviceId: 'joker',
        displayName: 'joker',
        deploymentStatus: 'Idle',
        primaryActorId: 'actor-joker',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with joker.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);
    mockScopeRuntimeApi.getServiceRevisions.mockImplementationOnce(async () =>
      mockBuildServiceRevisionCatalog({
        serviceId: 'joker',
        displayName: 'joker',
        workflowName: 'joker',
        deploymentStatus: 'Idle',
      }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    const rail = await screen.findByLabelText('Team members');
    const draftButton = await within(rail).findByRole('button', {
      name: 'draft1',
    });
    const jokerButton = within(rail).getByRole('button', {
      name: 'joker',
    });

    expect(
      draftButton.compareDocumentPosition(jokerButton) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });

  it('does not hide a workflow draft when the matching service revision cannot be loaded', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);
    mockScopeRuntimeApi.listServices.mockResolvedValueOnce([
      {
        serviceId: 'default',
        displayName: 'workspace-demo',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-default',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with workspace-demo.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);
    mockScopeRuntimeApi.getServiceRevisions.mockRejectedValueOnce(
      new Error('revision service unavailable'),
    );

    renderStudioPage('/studio?scopeId=scope-1&tab=studio');

    const rail = await screen.findByLabelText('Team members');
    await waitFor(() => {
      expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenCalledWith(
        'scope-1',
        'default',
      );
      expect(
        within(rail).getAllByRole('button', { name: 'workspace-demo' }),
      ).toHaveLength(2);
    });
  });

  it('fetches published revisions for each service on initial Studio rail render', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    mockScopeRuntimeApi.listServices.mockResolvedValueOnce([
      {
        serviceId: 'default',
        displayName: 'workspace-demo',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-default',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with workspace-demo.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
      {
        serviceId: 'billing-api',
        displayName: 'Billing API',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-billing',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'Chat',
            kind: 'chat',
            description: 'Chat with billing.',
            requestTypeUrl: '',
            responseTypeUrl: '',
          },
        ],
      },
    ]);

    renderStudioPage('/studio?scopeId=scope-1&tab=studio');

    const rail = await screen.findByLabelText('Team members');
    expect(
      await within(rail).findByRole('button', { name: 'Billing API' }),
    ).toBeTruthy();

    await waitFor(() => {
      expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenCalledWith(
        'scope-1',
        'default',
      );
      expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenCalledWith(
        'scope-1',
        'billing-api',
      );
    });
  });

  it('does not truncate the team member rail when more than eight members are available', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce(
      Array.from({ length: 9 }, (_, index) => ({
        workflowId: `workflow-${index + 1}`,
        name: `member-${index + 1}`,
        description: `Workflow ${index + 1}`,
        fileName: `member-${index + 1}.yaml`,
        filePath: `/tmp/workflows/member-${index + 1}.yaml`,
        directoryId: 'dir-1',
        directoryLabel: 'Workspace',
        stepCount: index + 1,
        hasLayout: true,
        updatedAtUtc: '2026-03-18T00:00:00Z',
      })),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    const rail = await screen.findByLabelText('Team members');
    expect(
      await within(rail).findByRole('button', { name: 'member-1' }),
    ).toBeTruthy();
    expect(within(rail).getByRole('button', { name: 'member-8' })).toBeTruthy();
    expect(within(rail).getByRole('button', { name: 'member-9' })).toBeTruthy();
  });

  it('opens the scripts workspace when the route only carries a script id', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      features: {
        ...defaultStudioAppContext.features,
        scripts: true,
      },
      scopeId: 'scope-1',
      scopeResolved: true,
    });

    renderStudioPage('/studio?focus=script%3Ascript-alpha');

    expect(await screen.findByLabelText('Script ID')).toBeTruthy();
    expect(screen.getByTestId('studio-script-build-panel')).toBeTruthy();
    expect(screen.getByText('Script source')).toBeTruthy();
  });

  it('treats legacy script member routes as script focus only', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      features: {
        ...defaultStudioAppContext.features,
        scripts: true,
      },
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    (scriptsApi.listScripts as jest.Mock).mockResolvedValue([
      {
        available: true,
        scopeId: 'scope-1',
        script: {
          scopeId: 'scope-1',
          scriptId: 'script-alpha',
          catalogActorId: 'catalog-1',
          definitionActorId: 'definition-1',
          activeRevision: 'rev-1',
          activeSourceHash: 'hash-1',
          updatedAt: '2026-03-18T00:00:00Z',
        },
        source: {
          sourceText: 'using System;',
          definitionActorId: 'definition-1',
          revision: 'rev-1',
          sourceHash: 'hash-1',
        },
      },
    ]);

    renderStudioPage(
      '/studio?scopeId=scope-1&member=script%3Ascript-alpha&tab=scripts',
    );

    expect(await screen.findByTestId('studio-script-build-panel')).toBeTruthy();
    expect(screen.getByLabelText('Script ID')).toHaveValue('script-alpha');

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBeNull();
      expect(searchParams.get('focus')).toBe('script:script-alpha');
      expect(searchParams.get('tab')).toBe('scripts');
    });
  });

  it('does not open Bind from Script Build without a member subject', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      features: {
        ...defaultStudioAppContext.features,
        scripts: true,
      },
      scopeId: 'scope-1',
      scopeResolved: true,
    });

    renderStudioPage(
      '/studio?scopeId=scope-1&focus=script%3Ascript-alpha&tab=scripts',
    );

    expect(await screen.findByTestId('studio-script-build-panel')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Continue to Bind' }));

    expect(screen.queryByTestId('studio-bind-surface')).toBeNull();
    expect(studioApi.bindMemberScript).not.toHaveBeenCalled();
    expect(studioApi.bindScopeScript).not.toHaveBeenCalled();
    expect(mockConsoleToast.warning).toHaveBeenCalledWith(
      'Select or create a member before opening Bind for this Script.',
    );
    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get('member')).toBeNull();
    expect(searchParams.get('focus')).toBe('script:script-alpha');
    expect(searchParams.get('tab')).toBe('scripts');
  });

  it('does not duplicate the selected Script member and its script artifact in the rail', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      features: {
        ...defaultStudioAppContext.features,
        scripts: true,
      },
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    (scriptsApi.listScripts as jest.Mock).mockResolvedValue([
      {
        available: true,
        scopeId: 'scope-1',
        script: {
          scopeId: 'scope-1',
          scriptId: 'script-1',
          catalogActorId: 'catalog-1',
          definitionActorId: 'definition-1',
          activeRevision: 'rev-script-1',
          activeSourceHash: 'hash-1',
          updatedAt: '2026-03-18T00:00:00Z',
        },
        source: {
          sourceText: 'using System;',
          definitionActorId: 'definition-1',
          revision: 'rev-script-1',
          sourceHash: 'hash-1',
        },
      },
    ]);

    renderStudioPage(
      '/studio?scopeId=scope-1&focus=script%3Ascript-1&tab=scripts',
    );

    const rail = await screen.findByLabelText('Team members');
    await waitFor(() => {
      expect(
        within(rail).getAllByRole('button', { name: 'script-1' }),
      ).toHaveLength(1);
    });
  });

  it('returns from Bind to the selected Script build surface', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      features: {
        ...defaultStudioAppContext.features,
        scripts: true,
      },
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    mockStudioMembers = [
      ...mockStudioMembers,
      {
        memberId: 'script-member',
        scopeId: 'scope-1',
        displayName: 'draft-test',
        description: 'Script member',
        implementationKind: 'script',
        scriptId: 'script-alpha',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'service-script-alpha',
        lastBoundRevisionId: 'rev-script-1',
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    (scriptsApi.listScripts as jest.Mock).mockResolvedValue([
      {
        available: true,
        scopeId: 'scope-1',
        script: {
          scopeId: 'scope-1',
          scriptId: 'script-alpha',
          catalogActorId: 'catalog-1',
          definitionActorId: 'definition-1',
          activeRevision: 'rev-script-1',
          activeSourceHash: 'hash-1',
          updatedAt: '2026-03-18T00:00:00Z',
        },
        source: {
          sourceText: 'using System;',
          definitionActorId: 'definition-1',
          revision: 'rev-script-1',
          sourceHash: 'hash-1',
        },
      },
    ]);
    mockScopeRuntimeApi.listServices.mockResolvedValue([
      {
        serviceId: 'service-script-alpha',
        displayName: 'draft-test',
        deploymentStatus: 'Active',
        primaryActorId: 'actor-script-alpha',
        endpoints: [
          {
            endpointId: 'script-command',
            displayName: 'Script command',
            kind: 'command',
            description: 'Invoke the script command.',
            requestTypeUrl: 'type.googleapis.com/example.ScriptCommand',
            responseTypeUrl: 'type.googleapis.com/example.ScriptResult',
          },
        ],
      },
    ]);
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        serviceId === 'service-script-alpha'
          ? mockBuildScriptServiceRevisionCatalog({
              serviceId,
              scriptId: 'script-alpha',
            })
          : mockBuildServiceRevisionCatalog({ serviceId }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Ascript-member&step=bind&tab=bindings',
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Build' }));

    expect(await screen.findByTestId('studio-script-build-panel')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByLabelText('Script ID')).toHaveValue('script-alpha');
    });
    expect(screen.queryByTestId('studio-workflow-build-panel')).toBeNull();
  });

  it('binds a catalog-applied Script member through the member binding API', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      features: {
        ...defaultStudioAppContext.features,
        scripts: true,
      },
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    mockStudioMembers = [
      ...mockStudioMembers,
      {
        memberId: 'script-member',
        scopeId: 'scope-1',
        displayName: 'script-alpha',
        description: 'Script member',
        implementationKind: 'script',
        lifecycleStage: 'created',
        publishedServiceId: 'member-script-member',
        lastBoundRevisionId: null,
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    (scriptsApi.listScripts as jest.Mock).mockResolvedValue([
      {
        available: true,
        scopeId: 'scope-1',
        script: {
          scopeId: 'scope-1',
          scriptId: 'script-alpha',
          catalogActorId: 'catalog-1',
          definitionActorId: 'definition-1',
          activeRevision: 'rev-1',
          activeSourceHash: 'hash-1',
          updatedAt: '2026-03-18T00:00:00Z',
        },
        source: {
          sourceText: 'using System;',
          definitionActorId: 'definition-1',
          revision: 'rev-1',
          sourceHash: 'hash-1',
        },
      },
    ]);
    mockScopeRuntimeApi.listServices
      .mockResolvedValueOnce([])
      .mockResolvedValue([
        {
          serviceId: 'member-script-member',
          displayName: 'script-alpha',
          deploymentStatus: 'Active',
          primaryActorId: 'actor-script-alpha',
          endpoints: [
            {
              endpointId: 'script-command',
              displayName: 'Script command',
              kind: 'command',
              description: 'Invoke the script command.',
              requestTypeUrl: 'type.googleapis.com/example.ScriptCommand',
              responseTypeUrl: 'type.googleapis.com/example.ScriptResult',
            },
          ],
        },
      ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        mockBuildScriptServiceRevisionCatalog({
          serviceId,
          displayName: 'script-alpha',
          scriptId: 'script-alpha',
          revisionId: 'rev-script-binding',
        }),
    );

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Ascript-member&focus=script%3Ascript-alpha&tab=scripts',
    );

    expect(await screen.findByTestId('studio-script-build-panel')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Continue to Bind' }));

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText('candidate:script-alpha')).toBeTruthy();
      expect(screen.getByText('service:no-service')).toBeTruthy();
      expect(screen.getByText('services:none')).toBeTruthy();
      expect(screen.getByText('member:script-member')).toBeTruthy();
    });

    fireEvent.click(
      screen.getByRole('button', { name: 'Bind current member' }),
    );

    await waitFor(() => {
      expect(studioApi.bindMemberScript).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: 'scope-1',
          memberId: 'script-member',
          displayName: 'script-alpha',
          scriptId: 'script-alpha',
          scriptRevision: 'rev-1',
        }),
      );
    });
    expect(studioApi.bindScopeScript).not.toHaveBeenCalled();
    await waitFor(() => {
      expect(screen.getByText('services:member-script-member')).toBeTruthy();
      expect(screen.getByText('candidate:none')).toBeTruthy();
    });
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get('member')).toBe('member:script-member');
      expect(searchParams.get('focus')).toBe('script:script-alpha');
      expect(searchParams.get('step')).toBe('bind');
      expect(searchParams.get('tab')).toBe('bindings');
    });
  });

  it('keeps a saved Script member on the pending bind panel until a published contract exists', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      features: {
        ...defaultStudioAppContext.features,
        scripts: true,
      },
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    mockStudioMembers = [
      ...mockStudioMembers,
      {
        memberId: 'm-script-alpha',
        scopeId: 'scope-1',
        displayName: 'script-alpha',
        description: 'Saved Script member without a published contract yet.',
        implementationKind: 'script',
        lifecycleStage: 'created',
        publishedServiceId: 'member-m-script-alpha',
        lastBoundRevisionId: null,
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
    ];
    (scriptsApi.listScripts as jest.Mock).mockResolvedValue([
      {
        available: true,
        scopeId: 'scope-1',
        script: {
          scopeId: 'scope-1',
          scriptId: 'script-alpha',
          catalogActorId: 'catalog-1',
          definitionActorId: 'definition-1',
          activeRevision: 'rev-1',
          activeSourceHash: 'hash-1',
          updatedAt: '2026-03-18T00:00:00Z',
        },
        source: {
          sourceText: 'using System;',
          definitionActorId: 'definition-1',
          revision: 'rev-1',
          sourceHash: 'hash-1',
        },
      },
    ]);
    mockScopeRuntimeApi.listServices.mockResolvedValue([]);

    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Am-script-alpha&focus=script%3Ascript-alpha&tab=scripts',
    );

    expect(await screen.findByTestId('studio-script-build-panel')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Continue to Bind' }));

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText('candidate:script-alpha')).toBeTruthy();
      expect(screen.getByText('services:none')).toBeTruthy();
      expect(screen.getByText('service:no-service')).toBeTruthy();
      expect(screen.getByText('member:m-script-alpha')).toBeTruthy();
    });
    expect(screen.queryByText('services:member-m-script-alpha')).toBeNull();
  });

  it('loads discovered GAgent types and the published service revision catalog', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    renderStudioPage('/studio?focus=workflow%3Aworkflow-1&tab=studio');

    await waitFor(() => {
      expect(mockRuntimeGAgentApi.listKinds).toHaveBeenCalled();
    });
    await waitFor(() => {
      expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenCalledWith(
        'scope-1',
        'default',
      );
    });
  });

  it('stops the selected member run from the observe view', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&memberId=default&step=observe&tab=executions&execution=execution-1',
    );

    expect(await screen.findByText('Logs')).toBeTruthy();
    expect(
      await screen.findByText('observe-selected:execution-1'),
    ).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Stop' }));

    await waitFor(() => {
      expect(mockRuntimeRunsApi.stop).toHaveBeenCalledWith(
        'scope-1',
        {
          actorId: 'actor-1',
          runId: 'execution-1',
          reason: 'user requested stop',
        },
        {
          memberId: 'workspace-demo',
          serviceId: 'default',
        },
      );
    });

    expect(await screen.findByText('Execution stop requested')).toBeTruthy();
  });

  it('falls back to the build editor when a removed roles tab still carries a workflow draft', async () => {
    renderStudioPage('/studio?focus=workflow%3Aworkflow-1&tab=roles');

    expect(await screen.findByText('DAG Canvas')).toBeTruthy();
    expect(screen.queryByText('Saved roles')).toBeNull();
  });

  it('switches the Studio lifecycle stepper into the bind surface', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio',
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Bind' }));

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
  });

  it('shows published members in the left rail even when the workflow inventory is empty', async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: 'scope-1',
      scopeResolved: true,
    });
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([]);

    renderStudioPage('/studio?scopeId=scope-1&tab=studio');

    const rail = await screen.findByLabelText('Team members');
    expect(
      await within(rail).findByRole('button', { name: 'workspace-demo' }),
    ).toBeTruthy();
    expect(
      screen.queryByText(
        'No team members yet. Create a member to start building in Studio.',
      ),
    ).toBeNull();
  });

  it('keeps the Team members rail focused on member inventory instead of implementation filters', async () => {
    renderStudioPage('/studio?scopeId=scope-1&tab=studio');

    const rail = await screen.findByLabelText('Team members');
    expect(
      await within(rail).findAllByRole('button', { name: 'workspace-demo' }),
    ).not.toHaveLength(0);
    expect(within(rail).getByRole('button', { name: 'All' })).toBeTruthy();
    expect(within(rail).getByRole('button', { name: 'Member' })).toBeTruthy();
    expect(within(rail).queryByRole('button', { name: 'Workflow' })).toBeNull();
    expect(within(rail).queryByRole('button', { name: 'Script' })).toBeNull();
    expect(within(rail).queryByRole('button', { name: 'GAgent' })).toBeNull();
  });

  it('keeps Invoke available once the selected member already has a published endpoint', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&memberId=default&focus=workflow%3Aworkflow-1&tab=studio',
    );

    const invokeButton = await screen.findByRole('button', { name: 'Invoke' });
    await waitFor(() => {
      expect(invokeButton).toBeEnabled();
    });

    fireEvent.click(invokeButton);

    expect(await screen.findByTestId('studio-invoke-surface')).toBeTruthy();
    expect(screen.getByText('service:default')).toBeTruthy();
  });

  it('shows a clear invoke fallback when no selected member is available', async () => {
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);

    renderStudioPage('/studio?scopeId=scope-1&step=invoke&tab=invoke');

    expect(await screen.findByTestId('studio-invoke-surface')).toBeTruthy();
    expect(screen.getByText('service:no-service')).toBeTruthy();
    expect(screen.getByText('services:none')).toBeTruthy();
    expect(screen.getByText('empty:Select a member to invoke.')).toBeTruthy();
  });

  it('opens the Studio invoke surface from the bind surface endpoint action', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Aworkspace-demo&focus=workflow%3Aworkflow-1&tab=studio',
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Bind' }));
    await waitFor(() => {
      expect(
        screen.getByRole('button', { name: 'Continue to Invoke' }),
      ).not.toBeDisabled();
    });
    fireEvent.click(screen.getByRole('button', { name: 'Continue to Invoke' }));

    expect(await screen.findByTestId('studio-invoke-surface')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText('service:default')).toBeTruthy();
      expect(screen.getByText('endpoint:chat')).toBeTruthy();
    });

    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get('teamId')).toBe('t-alpha');
    expect(searchParams.get('member')).toBe('member:workspace-demo');
    expect(searchParams.get('step')).toBe('invoke');
  });

  it('pins Observe to the selected member service and corrects stale run selection', async () => {
    mockScopeRuntimeApi.listServiceRuns.mockResolvedValueOnce({
      scopeId: 'scope-1',
      serviceId: 'default',
      serviceKey: 'scope-1:default:default:default',
      displayName: 'workspace-demo',
      runs: [
        mockBuildServiceRunSummary({
          runId: 'execution-1',
          actorId: 'actor-1',
          workflowName: 'workspace-demo',
        }),
      ],
    });

    renderStudioPage(
      '/studio?scopeId=scope-1&memberId=default&step=observe&tab=executions&execution=execution-2',
    );

    expect(await screen.findByText('Logs')).toBeTruthy();

    await waitFor(() => {
      expect(mockScopeRuntimeApi.listServiceRuns).toHaveBeenCalledWith(
        'scope-1',
        'default',
        {
          take: 12,
        },
      );
      expect(screen.getByText('observe-member:workspace-demo')).toBeTruthy();
      expect(screen.getByText('observe-runs:execution-1')).toBeTruthy();
      expect(screen.getByText('observe-selected:execution-1')).toBeTruthy();
    });

    expect(
      screen.queryByText('observe-runs:execution-1,execution-2'),
    ).toBeNull();
    expect(screen.queryByText('observe-selected:execution-2')).toBeNull();
  });

  it('keeps Observe populated with the latest invoke session while runtime runs warm up', async () => {
    mockScopeRuntimeApi.listServiceRuns.mockResolvedValue({
      scopeId: 'scope-1',
      serviceId: 'default',
      serviceKey: 'scope-1:default:default:default',
      displayName: 'workspace-demo',
      runs: [],
    });

    renderStudioPage(
      '/studio?scopeId=scope-1&memberId=default&step=invoke&tab=invoke',
    );

    expect(await screen.findByTestId('studio-invoke-surface')).toBeTruthy();

    fireEvent.click(
      screen.getByRole('button', { name: 'Emit Observe Session' }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Observe' }));

    expect(await screen.findByText('Logs')).toBeTruthy();

    await waitFor(() => {
      expect(screen.getByText('observe-runs:invoke-run-1')).toBeTruthy();
      expect(screen.getByText('observe-selected:invoke-run-1')).toBeTruthy();
      expect(
        screen.queryByText('observe-empty:No runs for workspace-demo yet.'),
      ).toBeNull();
    });
  });

  it('rehydrates Observe from the persisted invoke session after refresh', async () => {
    const now = Date.now();
    mockScopeRuntimeApi.listServiceRuns.mockResolvedValue({
      scopeId: 'scope-1',
      serviceId: 'default',
      serviceKey: 'scope-1:default:default:default',
      displayName: 'workspace-demo',
      runs: [],
    });

    saveStudioObserveSessionSeed({
      scopeId: 'scope-1',
      session: {
        actorId: 'actor-invoke',
        assistantText: 'Observed output',
        commandId: 'command-invoke',
        correlationId: '',
        completedAtUtc: new Date(now).toISOString(),
        endpointId: 'chat',
        error: '',
        errorCode: '',
        events: [
          {
            name: 'aevatar.run.context',
            timestamp: now - 1000,
            type: 'CUSTOM',
            value: {
              actorId: 'actor-invoke',
              commandId: 'command-invoke',
            },
          },
          {
            result: 'Observed output',
            runId: 'invoke-run-2',
            timestamp: now,
            threadId: 'actor-invoke',
            type: 'RUN_FINISHED',
          },
        ],
        finalOutput: 'Observed output',
        mode: 'stream',
        payloadBase64: '',
        payloadTypeUrl: '',
        prompt: 'Observe after refresh.',
        runId: 'invoke-run-2',
        serviceId: 'default',
        serviceLabel: 'workspace-demo',
        startedAtUtc: new Date(now - 1000).toISOString(),
        status: 'success',
      },
    });

    renderStudioPage(
      '/studio?scopeId=scope-1&memberId=default&step=observe&tab=executions',
    );

    expect(await screen.findByText('Logs')).toBeTruthy();

    await waitFor(() => {
      expect(screen.getByText('observe-runs:invoke-run-2')).toBeTruthy();
      expect(screen.getByText('observe-selected:invoke-run-2')).toBeTruthy();
      expect(
        screen.queryByText('observe-empty:No runs for workspace-demo yet.'),
      ).toBeNull();
    });
  });

  it('shows a clear observe fallback when no selected member is available', async () => {
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);

    renderStudioPage('/studio?scopeId=scope-1&step=observe&tab=executions');

    expect(await screen.findByText('Logs')).toBeTruthy();
    expect(screen.getByText('observe-runs:none')).toBeTruthy();
    expect(
      screen.getByText('observe-empty:Select a member to observe.'),
    ).toBeTruthy();
  });

  it('walks the lifecycle flow from build to bind to invoke to observe', async () => {
    renderStudioPage(
      '/studio?scopeId=scope-1&member=member%3Aworkspace-demo&focus=workflow%3Aworkflow-1&tab=studio',
    );

    expect(
      await screen.findByTestId('studio-workflow-build-panel'),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Continue to Bind' }));
    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();

    await waitFor(() => {
      expect(
        screen.getByRole('button', { name: 'Continue to Invoke' }),
      ).not.toBeDisabled();
    });
    fireEvent.click(screen.getByRole('button', { name: 'Continue to Invoke' }));
    expect(await screen.findByTestId('studio-invoke-surface')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Observe' }));
    expect(await screen.findByText('Logs')).toBeTruthy();
  });
});
