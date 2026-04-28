import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { Modal, message } from "antd";
import React from "react";
import { ensureActiveAuthSession } from "@/shared/auth/client";
import { runtimeGAgentApi } from "@/shared/api/runtimeGAgentApi";
import { runtimeQueryApi } from "@/shared/api/runtimeQueryApi";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { studioApi } from "@/shared/studio/api";
import { saveStudioObserveSessionSeed } from "@/shared/studio/observeSession";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import StudioPage from "./index";

jest.mock("antd", () => {
  const actual = jest.requireActual("antd");
  const modal = actual.Modal;
  return {
    ...actual,
    message: {
      ...actual.message,
      success: jest.fn(),
      info: jest.fn(),
      warning: jest.fn(),
      error: jest.fn(),
      destroy: jest.fn(),
    },
    Modal: Object.assign(modal, {
      confirm: jest.fn(),
    }),
  };
});

const STUDIO_AUTO_RELOGIN_ATTEMPT_KEY =
  "aevatar-console:studio:auto-relogin:";

type MockChildrenProps = {
  readonly children?: any;
};

type MockNotice =
  | {
      readonly type: "success" | "info" | "warning" | "error";
    }
  | null
  | undefined;

type MockValueEvent = {
  readonly target: {
    readonly value: string;
  };
};

const mockWorkflowDocument = {
  name: "workspace-demo",
  description: "Workspace workflow",
  roles: [
    {
      id: "assistant",
      name: "Assistant",
      systemPrompt: "Help the operator.",
      provider: "tornado",
      model: "gpt-test",
      connectors: ["web-search"],
    },
  ],
  steps: [
    {
      id: "draft_step",
      type: "llm_call",
      targetRole: "assistant",
      parameters: {
        prompt_prefix: "Draft the response",
      },
      next: "approve_step",
      branches: {},
    },
    {
      id: "approve_step",
      type: "human_approval",
      targetRole: "",
      parameters: {
        reviewer: "operator",
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
    if (step.next) {
      lines.push(`    next: ${step.next}`);
    }
    return lines;
  });

  return [
    `name: ${document.name}`,
    "roles:",
    ...roleLines,
    "steps:",
    ...stepLines,
    "",
  ].join("\n");
}

function mockBuildServiceRevisionCatalog(
  overrides?: Partial<{
    scopeId: string;
    serviceId: string;
    displayName: string;
    workflowName: string;
    revisionId: string;
    deploymentStatus: string;
  }>
) {
  const scopeId = overrides?.scopeId ?? "scope-1";
  const serviceId = overrides?.serviceId ?? "default";
  const displayName = overrides?.displayName ?? "workspace-demo";
  const workflowName = overrides?.workflowName ?? displayName;
  const revisionId = overrides?.revisionId ?? "rev-2";
  const deploymentStatus = overrides?.deploymentStatus ?? "Active";

  return {
    scopeId,
    serviceId,
    serviceKey: `${scopeId}:default:default:${serviceId}`,
    displayName,
    defaultServingRevisionId: revisionId,
    activeServingRevisionId: revisionId,
    deploymentId: "dep-2",
    deploymentStatus,
    primaryActorId: "actor-default",
    catalogStateVersion: 2,
    catalogLastEventId: "event-2",
    updatedAt: "2026-03-26T08:00:00Z",
    revisions: [
      {
        revisionId,
        implementationKind: "workflow",
        status: "Published",
        artifactHash: "hash-2",
        failureReason: "",
        isDefaultServing: true,
        isActiveServing: true,
        isServingTarget: true,
        allocationWeight: 100,
        servingState: "Active",
        deploymentId: "dep-2",
        primaryActorId: "actor-default",
        createdAt: "2026-03-26T07:00:00Z",
        preparedAt: "2026-03-26T07:01:00Z",
        publishedAt: "2026-03-26T07:02:00Z",
        retiredAt: null,
        workflowName,
        workflowDefinitionActorId: "scope-workflow:scope-1:default",
        inlineWorkflowCount: 1,
        scriptId: "",
        scriptRevision: "",
        scriptDefinitionActorId: "",
        scriptSourceHash: "",
        staticActorTypeName: "",
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
  }>
) {
  const scopeId = overrides?.scopeId ?? "scope-1";
  const serviceId = overrides?.serviceId ?? "default";
  const runId = overrides?.runId ?? "execution-1";
  const actorId = overrides?.actorId ?? "actor-1";
  const workflowName = overrides?.workflowName ?? "workspace-demo";
  const completionStatus = overrides?.completionStatus ?? "running";
  const lastUpdatedAt =
    overrides?.lastUpdatedAt ?? "2026-03-18T00:00:30Z";
  const lastError = overrides?.lastError ?? "";

  return {
    scopeId,
    serviceId,
    runId,
    actorId,
    definitionActorId: `definition:${workflowName}`,
    revisionId: "rev-2",
    deploymentId: "dep-2",
    workflowName,
    completionStatus,
    stateVersion: 2,
    lastEventId: `event:${runId}`,
    lastUpdatedAt,
    boundAt: "2026-03-18T00:00:00Z",
    bindingUpdatedAt: "2026-03-18T00:00:00Z",
    lastSuccess: completionStatus === "completed" ? true : null,
    totalSteps: 2,
    completedSteps: completionStatus === "running" ? 1 : 2,
    roleReplyCount: 0,
    lastOutput: completionStatus === "completed" ? "Completed output" : "",
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
  }>
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
    summary.completionStatus === "completed" ||
    summary.completionStatus === "failed" ||
    summary.completionStatus === "stopped";

  return {
    summary,
    audit: {
      reportVersion: "1",
      projectionScope: "current-state",
      topologySource: "projection",
      completionStatus: summary.completionStatus,
      workflowName: summary.workflowName,
      rootActorId: summary.actorId,
      commandId: `command:${summary.runId}`,
      stateVersion: summary.stateVersion,
      lastEventId: summary.lastEventId,
      createdAt: "2026-03-18T00:00:00Z",
      updatedAt: summary.lastUpdatedAt,
      startedAt: "2026-03-18T00:00:00Z",
      endedAt: completed ? summary.lastUpdatedAt : null,
      durationMs: completed ? 30000 : 0,
      success: summary.completionStatus === "completed" ? true : null,
      input: overrides?.input ?? "Run the demo workflow.",
      finalOutput:
        overrides?.finalOutput ??
        (summary.completionStatus === "completed" ? "Completed output" : ""),
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
let mockSettings: any;
const defaultStudioAppContext = {
  mode: "proxy",
  scopeId: null,
  scopeResolved: false,
  scopeSource: "",
  workflowStorageMode: "workspace",
  scriptStorageMode: "draft",
  features: {
    publishedWorkflows: true,
    scripts: false,
  },
  scriptContract: {
    inputType: "type.googleapis.com/example.Command",
    readModelFields: ["input", "output"],
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
    providerDisplayName: "NyxID",
  };
}

function mockCreateDefaultWorkflowSummaries() {
  return [
    {
      workflowId: "workflow-1",
      name: "workspace-demo",
      description: "Workspace workflow",
      fileName: "workspace-demo.yaml",
      filePath: "/tmp/workflows/workspace-demo.yaml",
      directoryId: "dir-1",
      directoryLabel: "Workspace",
      stepCount: 2,
      hasLayout: true,
      updatedAtUtc: "2026-03-18T00:00:00Z",
    },
  ];
}

function mockCreateDefaultStudioMembers() {
  return [
    {
      memberId: "workspace-demo",
      scopeId: "scope-1",
      displayName: "workspace-demo",
      description: "Workspace workflow member",
      implementationKind: "workflow",
      lifecycleStage: "bind_ready",
      publishedServiceId: "default",
      lastBoundRevisionId: "rev-2",
      createdAt: "2026-04-27T08:00:00Z",
      updatedAt: "2026-04-27T08:05:00Z",
    },
  ];
}

async function mockAuthorWorkflowSuccess(
  _input: { prompt: string },
  options?: {
    onText?: (text: string) => void;
    onReasoning?: (text: string) => void;
  }
) {
  options?.onReasoning?.("Thinking through the workflow structure.");
  options?.onText?.("name: ai-generated\nsteps: []\n");
  return "name: ai-generated\nsteps: []\n";
}

function resetMockState(): void {
  mockParsedDocument = mockCloneValue(mockWorkflowDocument);
  mockWorkflowSummaries = mockCreateDefaultWorkflowSummaries();
  mockStudioMembers = mockCreateDefaultStudioMembers();
  mockWorkflowFile = {
    workflowId: "workflow-1",
    name: "workspace-demo",
    fileName: "workspace-demo.yaml",
    filePath: "/tmp/workflows/workspace-demo.yaml",
    directoryId: "dir-1",
    directoryLabel: "Workspace",
    yaml: mockBuildWorkflowYaml(mockParsedDocument),
    findings: [],
    draftExists: true,
    updatedAtUtc: "2026-03-18T00:00:00Z",
    document: mockParsedDocument,
  };
  mockConnectorCatalog = {
    homeDirectory: "actor://connector-catalog",
    filePath: "actor://connector-catalog/connectors",
    fileExists: true,
    connectors: [
      {
        name: "web-search",
        type: "http",
        enabled: true,
        timeoutMs: 10000,
        retry: 1,
        http: {
          baseUrl: "https://example.test",
          allowedMethods: ["GET"],
          allowedPaths: ["/search"],
          allowedInputKeys: ["query"],
          defaultHeaders: {},
        },
        cli: {
          command: "",
          fixedArguments: [],
          allowedOperations: [],
          allowedInputKeys: [],
          workingDirectory: "",
          environment: {},
        },
        mcp: {
          serverName: "",
          command: "",
          arguments: [],
          environment: {},
          defaultTool: "",
          allowedTools: [],
          allowedInputKeys: [],
        },
      },
    ],
  };
  mockConnectorDraftResponse = {
    homeDirectory: "actor://connector-catalog",
    filePath: "actor://connector-catalog/connectors/draft",
    fileExists: false,
    updatedAtUtc: null,
    draft: null,
  };
  mockRoleCatalog = {
    homeDirectory: "actor://role-catalog",
    filePath: "actor://role-catalog/roles",
    fileExists: true,
    roles: [
      {
        id: "assistant",
        name: "Assistant",
        systemPrompt: "Help the operator.",
        provider: "tornado",
        model: "gpt-test",
        connectors: ["web-search"],
      },
    ],
  };
  mockRoleDraftResponse = {
    homeDirectory: "actor://role-catalog",
    filePath: "actor://role-catalog/roles/draft",
    fileExists: false,
    updatedAtUtc: null,
    draft: null,
  };
  mockSettings = {
    runtimeBaseUrl: "https://aevatar-console-backend-api.aevatar.ai",
    defaultProviderName: "tornado",
    providerTypes: [
      {
        id: "openai",
        displayName: "OpenAI",
        category: "llm",
        description: "OpenAI compatible provider",
        recommended: true,
        defaultEndpoint: "https://api.openai.test",
        defaultModel: "gpt-4.1-mini",
      },
    ],
    providers: [
      {
        providerName: "tornado",
        providerType: "openai",
        displayName: "Tornado",
        category: "llm",
        description: "Local provider",
        model: "gpt-test",
        endpoint: "https://aevatar-console-backend-api.aevatar.ai",
        apiKey: "",
        apiKeyConfigured: true,
      },
    ],
  };
}

jest.mock("@/shared/auth/client", () => ({
  ensureActiveAuthSession: jest.fn(async () => null),
}));

jest.mock("@/shared/api/runtimeQueryApi", () => ({
  runtimeQueryApi: {
    listPrimitives: jest.fn(async () => [
      {
        name: "llm_call",
        aliases: [],
        category: "core",
        description: "LLM call",
        parameters: [],
        exampleWorkflows: [],
      },
      {
        name: "demo_template",
        aliases: ["render_template"],
        category: "demo",
        description: "Demo template primitive",
        parameters: [],
        exampleWorkflows: ["demo_template"],
      },
    ]),
  },
}));

jest.mock("@/shared/api/runtimeGAgentApi", () => ({
  runtimeGAgentApi: {
    listTypes: jest.fn(async () => [
      {
        typeName: "OrdersGAgent",
        fullName: "Tests.OrdersGAgent",
        assemblyName: "Tests",
      },
    ]),
    listActors: jest.fn(async () => [
      {
        gAgentType: "Tests.OrdersGAgent",
        actorIds: ["orders-gagent"],
      },
    ]),
  },
}));

jest.mock("@/shared/api/servicesApi", () => ({
  servicesApi: {
    listServices: jest.fn(async () => [
      {
        serviceId: "default",
        displayName: "workspace-demo",
        deploymentStatus: "Active",
        primaryActorId: "actor-default",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with the published workflow.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
    ]),
  },
}));

jest.mock("@/shared/api/scopeRuntimeApi", () => ({
  scopeRuntimeApi: {
    getServiceRevisions: jest.fn(async (_scopeId: string, serviceId: string) =>
      mockBuildServiceRevisionCatalog({ serviceId })
    ),
    listMemberRuns: jest.fn(async (_scopeId: string, memberId: string) => ({
      scopeId: "scope-1",
      serviceId: memberId,
      serviceKey: `scope-1:default:default:${memberId}`,
      displayName: "workspace-demo",
      runs: [mockBuildServiceRunSummary({ serviceId: memberId })],
    })),
    listServiceRuns: jest.fn(async (_scopeId: string, serviceId: string) => ({
      scopeId: "scope-1",
      serviceId,
      serviceKey: `scope-1:default:default:${serviceId}`,
      displayName: "workspace-demo",
      runs: [mockBuildServiceRunSummary({ serviceId })],
    })),
    getMemberRunAudit: jest.fn(
      async (_scopeId: string, memberId: string, runId: string) =>
        mockBuildServiceRunAuditSnapshot({ serviceId: memberId, runId })
    ),
    getServiceRunAudit: jest.fn(
      async (_scopeId: string, serviceId: string, runId: string) =>
        mockBuildServiceRunAuditSnapshot({ serviceId, runId })
    ),
  },
}));

jest.mock("@/shared/api/runtimeRunsApi", () => ({
  runtimeRunsApi: {
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
  listTypes: jest.Mock;
  listActors: jest.Mock;
};
const mockServicesApi = servicesApi as unknown as {
  listServices: jest.Mock;
};
const mockScopeRuntimeApi = scopeRuntimeApi as unknown as {
  getServiceRevisions: jest.Mock;
  listMemberRuns: jest.Mock;
  listServiceRuns: jest.Mock;
  getMemberRunAudit: jest.Mock;
  getServiceRunAudit: jest.Mock;
};
const mockRuntimeRunsApi = runtimeRunsApi as unknown as {
  stop: jest.Mock;
  resume: jest.Mock;
  signal: jest.Mock;
};

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAppContext: jest.fn(async () => mockCreateDefaultStudioAppContext()),
    getAuthSession: jest.fn(async () => mockCreateDefaultStudioAuthSession()),
    getWorkspaceSettings: jest.fn(async () => ({
      runtimeBaseUrl: "https://aevatar-console-backend-api.aevatar.ai",
      directories: [
        {
          directoryId: "dir-1",
          label: "Workspace",
          path: "/tmp/workflows",
          isBuiltIn: false,
        },
      ],
    })),
    getUserConfig: jest.fn(async () => ({
      defaultModel: "gpt-4.1-mini",
      runtimeBaseUrl: "",
    })),
    saveUserConfig: jest.fn(async (input: { defaultModel: string; runtimeBaseUrl: string }) => input),
    getUserConfigModels: jest.fn(async () => ({
      providers: [
        {
          providerSlug: "openai",
          providerName: "OpenAI",
          status: "ready",
          proxyUrl: "https://nyx-api.example/openai",
        },
      ],
      gatewayUrl: "https://nyx-api.example/gateway",
      supportedModels: ["gpt-4.1-mini", "gpt-5.4-mini"],
    })),
    listMembers: jest.fn(async () => ({
      scopeId: "scope-1",
      members: mockStudioMembers,
      nextPageToken: null,
    })),
    getMember: jest.fn(async (_scopeId: string, memberId: string) => {
      const matchedMember =
        mockStudioMembers.find((member) => member.memberId === memberId) ??
        mockStudioMembers[0];
      return {
        summary: matchedMember,
        implementationRef:
          matchedMember?.implementationKind === "workflow"
            ? {
                implementationKind: "workflow",
                workflowId: matchedMember.displayName,
                workflowRevision: matchedMember.lastBoundRevisionId,
              }
            : matchedMember?.implementationKind === "script"
              ? {
                  implementationKind: "script",
                  scriptId: matchedMember.displayName,
                  scriptRevision: matchedMember.lastBoundRevisionId,
                }
              : {
                  implementationKind: "gagent",
                  actorTypeName: matchedMember?.displayName || "",
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
        implementationKind: "workflow" | "script" | "gagent";
        description?: string | null;
        memberId?: string | null;
      }) => {
        const nextMemberId =
          input.memberId?.trim() ||
          input.displayName.trim().toLowerCase().replace(/[^a-z0-9_-]+/g, "-");
        const nextMember = {
          memberId: nextMemberId,
          scopeId: input.scopeId,
          displayName: input.displayName.trim(),
          description: input.description?.trim() || "",
          implementationKind: input.implementationKind,
          lifecycleStage: "created",
          publishedServiceId: `member-${nextMemberId}`,
          lastBoundRevisionId: null,
          createdAt: "2026-04-27T08:10:00Z",
          updatedAt: "2026-04-27T08:10:00Z",
        };
        mockStudioMembers = [nextMember, ...mockStudioMembers];
        return nextMember;
      }
    ),
    getSkillsHealth: jest.fn(async () => ({
      baseUrl: "https://ornn.chrono-ai.fun",
      reachable: true,
      message: "Connected to Ornn.",
    })),
    searchSkills: jest.fn(async () => ({
      baseUrl: "https://ornn.chrono-ai.fun",
      total: 1,
      totalPages: 1,
      page: 1,
      pageSize: 100,
      items: [
        {
          guid: "skill-1",
          name: "ornn-search",
          description: "Search Ornn for reusable skills.",
          isPrivate: false,
        },
      ],
    })),
    listWorkflows: jest.fn(async () => mockWorkflowSummaries),
    getTemplateWorkflow: jest.fn(async () => ({
      catalog: {
        name: "published-demo",
        description: "Published demo workflow",
      },
      yaml: [
        "name: published-demo",
        "description: Published demo workflow",
        "roles:",
        "  - id: reviewer",
        "    name: Reviewer",
        "steps:",
        "  - id: step_prepare",
        "    type: llm_call",
        "    targetRole: reviewer",
        "    next: step_finish",
        "  - id: step_finish",
        "    type: emit",
        "    targetRole: reviewer",
        "",
      ].join("\n"),
      definition: {
        name: "published-demo",
        description: "Published demo workflow",
        closedWorldMode: false,
        roles: [
          {
            id: "reviewer",
            name: "Reviewer",
            systemPrompt: "Review the published flow.",
            provider: "tornado",
            model: "gpt-review",
            temperature: 0.1,
            maxTokens: 512,
            maxToolRounds: 2,
            maxHistoryMessages: 6,
            streamBufferCapacity: 4,
            eventModules: [],
            eventRoutes: "",
            connectors: [],
          },
        ],
        steps: [
          {
            id: "step_prepare",
            type: "llm_call",
            targetRole: "reviewer",
            parameters: {
              prompt: "{{prompt}}",
            },
            next: "step_finish",
            branches: {},
            children: [],
          },
          {
            id: "step_finish",
            type: "emit",
            targetRole: "reviewer",
            parameters: {},
            next: "",
            branches: {},
            children: [],
          },
        ],
      },
      edges: [{ from: "step_prepare", to: "step_finish", label: "next" }],
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
          updatedAtUtc: "2026-03-18T00:05:00Z",
          document: {
            ...mockWorkflowFile.document,
            name: input.workflowName,
          },
        };
        const existingSummaryIndex = mockWorkflowSummaries.findIndex(
          (workflow) => workflow.workflowId === resolvedWorkflowId
        );
        const nextSummary = {
          workflowId: resolvedWorkflowId,
          name: input.workflowName,
          description: "Workspace workflow",
          fileName: mockWorkflowFile.fileName,
          filePath: mockWorkflowFile.filePath,
          directoryId: input.directoryId,
          directoryLabel: "Workspace",
          stepCount: 0,
          hasLayout: true,
          updatedAtUtc: "2026-03-18T00:05:00Z",
        };
        if (existingSummaryIndex >= 0) {
          mockWorkflowSummaries[existingSummaryIndex] = nextSummary;
        } else {
          mockWorkflowSummaries = [nextSummary, ...mockWorkflowSummaries];
        }

        return mockWorkflowFile;
      }
    ),
    deleteWorkflow: jest.fn(async (workflowId: string) => {
      mockWorkflowSummaries = mockWorkflowSummaries.filter(
        (workflow) => workflow.workflowId !== workflowId
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
    parseYaml: jest.fn(async (input: { yaml: string }) => ({
      document: input.yaml.includes("name: legacy_draft")
        ? {
            name: "legacy_draft",
            description: "",
            roles: [],
            steps: [],
          }
        : input.yaml.includes("name: published-demo")
        ? {
            name: "published-demo",
            description: "Published demo workflow",
            roles: [],
            steps: [],
          }
        : input.yaml.includes("name: draft")
        ? {
            name: "draft",
            description: "",
            roles: [],
            steps: [],
          }
        : input.yaml.includes("name: ai-generated")
        ? {
            name: "ai-generated",
            description: "Generated by Studio AI",
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
      }
    ),
    listExecutions: jest.fn(async () => [
      {
        executionId: "execution-1",
        workflowName: "workspace-demo",
        prompt: "Run the demo workflow.",
        status: "running",
        startedAtUtc: "2026-03-18T00:00:00Z",
        completedAtUtc: null,
        actorId: "actor-1",
        error: null,
      },
    ]),
    getExecution: jest.fn(async (executionId: string) => ({
      executionId,
      workflowName: "workspace-demo",
      prompt:
        executionId === "execution-2"
          ? "Run the active draft from Studio."
          : "Run the demo workflow.",
      status: "running",
      startedAtUtc:
        executionId === "execution-2"
          ? "2026-03-18T00:06:00Z"
          : "2026-03-18T00:00:00Z",
      completedAtUtc: null,
      actorId: executionId === "execution-2" ? "actor-2" : "actor-1",
      error: null,
      frames: [],
    })),
    startExecution: jest.fn(
      async (input: { workflowName: string; prompt: string }) => ({
        executionId: "execution-2",
        workflowName: input.workflowName,
        prompt: input.prompt,
        runtimeBaseUrl: "https://aevatar-console-backend-api.aevatar.ai",
        status: "running",
        startedAtUtc: "2026-03-18T00:06:00Z",
        completedAtUtc: null,
        actorId: "actor-2",
        error: null,
        frames: [
          {
            receivedAtUtc: "2026-03-18T00:06:01Z",
            payload: '{"event":"started"}',
          },
        ],
      })
    ),
    bindScopeWorkflow: jest.fn(async (input: {
      scopeId: string;
      displayName?: string;
      workflowYamls: string[];
    }) => ({
      scopeId: input.scopeId,
      serviceId: "default",
      displayName: input.displayName || "workspace-demo",
      targetKind: "workflow",
      targetName: input.displayName || "workspace-demo",
      revisionId: "rev-2",
      workflowName: input.displayName || "workspace-demo",
      definitionActorIdPrefix: "scope-workflow:scope-1:default",
      expectedActorId: "scope-workflow:scope-1:default:dep-1",
    })),
    bindMemberWorkflow: jest.fn(async (input: {
      scopeId: string;
      memberId: string;
      displayName?: string;
      workflowYamls: string[];
    }) => {
      mockStudioMembers = mockStudioMembers.map((member) =>
        member.memberId === input.memberId
          ? {
              ...member,
              lifecycleStage: "bind_ready",
              lastBoundRevisionId: "rev-2",
              updatedAt: "2026-04-27T08:15:00Z",
            }
          : member
      );

      return {
        scopeId: input.scopeId,
        serviceId: "default",
        displayName: input.displayName || "workspace-demo",
        targetKind: "workflow",
        targetName: input.displayName || "workspace-demo",
        revisionId: "rev-2",
        workflowName: input.displayName || "workspace-demo",
        definitionActorIdPrefix: "scope-workflow:scope-1:default",
        expectedActorId: "scope-workflow:scope-1:default:dep-1",
      };
    }),
    bindScopeGAgent: jest.fn(async (input: {
      scopeId: string;
      displayName?: string;
      actorTypeName: string;
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
      displayName: input.displayName || "orders-gagent",
      targetKind: "gagent",
      targetName: input.actorTypeName || input.displayName || "orders-gagent",
      revisionId: "rev-gagent-1",
      expectedActorId: "scope-gagent:scope-1:default:dep-1",
    })),
    getScopeBinding: jest.fn(async () => ({
      available: true,
      scopeId: "scope-1",
      serviceId: "default",
      displayName: "workspace-demo",
      serviceKey: "scope-1:default:default:default",
      defaultServingRevisionId: "rev-2",
      activeServingRevisionId: "rev-2",
      deploymentId: "dep-2",
      deploymentStatus: "Active",
      primaryActorId: "actor-default",
      updatedAt: "2026-03-26T08:00:00Z",
      revisions: [
        {
          revisionId: "rev-2",
          implementationKind: "workflow",
          status: "Published",
          artifactHash: "hash-2",
          failureReason: "",
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: "Active",
          deploymentId: "dep-2",
          primaryActorId: "actor-default",
          createdAt: "2026-03-26T07:00:00Z",
          preparedAt: "2026-03-26T07:01:00Z",
          publishedAt: "2026-03-26T07:02:00Z",
          retiredAt: null,
          workflowName: "workspace-demo",
          workflowDefinitionActorId: "scope-workflow:scope-1:default",
          inlineWorkflowCount: 1,
          scriptId: "",
          scriptRevision: "",
          scriptDefinitionActorId: "",
          scriptSourceHash: "",
          staticActorTypeName: "",
        },
        {
          revisionId: "rev-1",
          implementationKind: "workflow",
          status: "Published",
          artifactHash: "hash-1",
          failureReason: "",
          isDefaultServing: false,
          isActiveServing: false,
          isServingTarget: false,
          allocationWeight: 0,
          servingState: "",
          deploymentId: "",
          primaryActorId: "",
          createdAt: "2026-03-25T07:00:00Z",
          preparedAt: "2026-03-25T07:01:00Z",
          publishedAt: "2026-03-25T07:02:00Z",
          retiredAt: null,
          workflowName: "workspace-demo-v1",
          workflowDefinitionActorId: "scope-workflow:scope-1:default:v1",
          inlineWorkflowCount: 1,
          scriptId: "",
          scriptRevision: "",
          scriptDefinitionActorId: "",
          scriptSourceHash: "",
          staticActorTypeName: "",
        },
      ],
    })),
    getDefaultRouteTarget: jest.fn(async () => ({
      available: true,
      scopeId: "scope-1",
      serviceId: "default",
      displayName: "workspace-demo",
      serviceKey: "scope-1:default:default:default",
      defaultServingRevisionId: "rev-2",
      activeServingRevisionId: "rev-2",
      deploymentId: "dep-2",
      deploymentStatus: "Active",
      primaryActorId: "actor-default",
      updatedAt: "2026-03-26T08:00:00Z",
      revisions: [
        {
          revisionId: "rev-2",
          implementationKind: "workflow",
          status: "Published",
          artifactHash: "hash-2",
          failureReason: "",
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: "Active",
          deploymentId: "dep-2",
          primaryActorId: "actor-default",
          createdAt: "2026-03-26T07:30:00Z",
          preparedAt: "2026-03-26T07:35:00Z",
          publishedAt: "2026-03-26T07:40:00Z",
          retiredAt: null,
          workflowName: "workspace-demo",
          workflowDefinitionActorId: "definition://workspace-demo",
          inlineWorkflowCount: 1,
          scriptId: "",
          scriptRevision: "",
          scriptDefinitionActorId: "",
          scriptSourceHash: "",
          staticActorTypeName: "",
        },
      ],
    })),
    activateScopeBindingRevision: jest.fn(async (input: {
      scopeId: string;
      revisionId: string;
    }) => ({
      scopeId: input.scopeId,
      serviceId: "default",
      displayName: "workspace-demo",
      revisionId: input.revisionId,
    })),
    retireScopeBindingRevision: jest.fn(async (input: {
      scopeId: string;
      revisionId: string;
    }) => ({
      scopeId: input.scopeId,
      serviceId: "default",
      revisionId: input.revisionId,
      status: "Retiring",
    })),
    stopExecution: jest.fn(async (executionId: string) => ({
      executionId,
      workflowName: "workspace-demo",
      prompt: "Run the demo workflow.",
      runtimeBaseUrl: "https://aevatar-console-backend-api.aevatar.ai",
      status: "stopped",
      startedAtUtc: "2026-03-18T00:00:00Z",
      completedAtUtc: "2026-03-18T00:07:00Z",
      actorId: "actor-1",
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
        updatedAtUtc: "2026-03-18T00:03:00Z",
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
      }
    ),
    importConnectorCatalog: jest.fn(async (file: File) => {
      mockConnectorCatalog = {
        ...mockConnectorCatalog,
        connectors: [
          {
            name: "imported-search",
            type: "http",
            enabled: true,
            timeoutMs: 15000,
            retry: 2,
            http: {
              baseUrl: "https://imported.example.test",
              allowedMethods: ["POST"],
              allowedPaths: ["/catalog"],
              allowedInputKeys: ["query"],
              defaultHeaders: {},
            },
            cli: {
              command: "",
              fixedArguments: [],
              allowedOperations: [],
              allowedInputKeys: [],
              workingDirectory: "",
              environment: {},
            },
            mcp: {
              serverName: "",
              command: "",
              arguments: [],
              environment: {},
              defaultTool: "",
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
        updatedAtUtc: "2026-03-18T00:03:00Z",
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
      }
    ),
    importRoleCatalog: jest.fn(async (file: File) => {
      mockRoleCatalog = {
        ...mockRoleCatalog,
        roles: [
          {
            id: "reviewer",
            name: "Reviewer",
            systemPrompt: "Review imported workflow outputs carefully.",
            provider: "tornado",
            model: "gpt-review",
            connectors: ["imported-search"],
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
    getSettings: jest.fn(async () => ({
      ...mockSettings,
    })),
    saveSettings: jest.fn(
      async (input: {
        runtimeBaseUrl?: string;
        defaultProviderName?: string;
        providers?: typeof mockSettings.providers;
      }) => {
        mockSettings = {
          ...mockSettings,
          runtimeBaseUrl: input.runtimeBaseUrl || mockSettings.runtimeBaseUrl,
          defaultProviderName:
            input.defaultProviderName || mockSettings.defaultProviderName,
          providers: input.providers || mockSettings.providers,
        };
        return mockSettings;
      }
    ),
    testRuntimeConnection: jest.fn(
      async (input: { runtimeBaseUrl?: string }) => ({
        runtimeBaseUrl: input.runtimeBaseUrl || mockSettings.runtimeBaseUrl,
        reachable: true,
        checkedUrl: `${
          input.runtimeBaseUrl || mockSettings.runtimeBaseUrl
        }/health`,
        statusCode: 200,
        message: "Runtime responded with 200 OK.",
      })
    ),
    addWorkflowDirectory: jest.fn(async () => ({
      runtimeBaseUrl: "https://aevatar-console-backend-api.aevatar.ai",
      directories: [
        {
          directoryId: "dir-1",
          label: "Workspace",
          path: "/tmp/workflows",
          isBuiltIn: false,
        },
      ],
    })),
    removeWorkflowDirectory: jest.fn(async () => undefined),
    authorWorkflow: jest.fn(mockAuthorWorkflowSuccess),
  },
}));

jest.mock("@/shared/studio/scriptsApi", () => ({
  scriptsApi: {
    listScripts: jest.fn(async () => []),
    listRuntimes: jest.fn(async () => []),
    getScriptCatalog: jest.fn(async () => ({
      scriptId: "script-1",
      activeRevision: "rev-1",
      activeDefinitionActorId: "definition-1",
      activeSourceHash: "hash-1",
      previousRevision: "",
      revisionHistory: ["rev-1"],
      lastProposalId: "",
      catalogActorId: "catalog-1",
      scopeId: "scope-1",
      updatedAt: "2026-03-18T00:00:00Z",
    })),
    getEvolutionDecision: jest.fn(async () => ({
      accepted: true,
      proposalId: "proposal-1",
      scriptId: "script-1",
      baseRevision: "rev-1",
      candidateRevision: "rev-2",
      status: "accepted",
      failureReason: "",
      definitionActorId: "definition-1",
      catalogActorId: "catalog-1",
      validationReport: {
        isSuccess: true,
        diagnostics: [],
      },
    })),
    getRuntimeReadModel: jest.fn(async () => ({
      actorId: "runtime-1",
      scriptId: "script-1",
      definitionActorId: "definition-1",
      revision: "rev-1",
      readModelTypeUrl: "type.googleapis.com/example.ReadModel",
      readModelPayloadJson: '{"status":"ok"}',
      stateVersion: 1,
      lastEventId: "event-1",
      updatedAt: "2026-03-18T00:00:00Z",
    })),
    validateDraft: jest.fn(async () => ({
      success: true,
      scriptId: "script-1",
      scriptRevision: "draft-1",
      primarySourcePath: "Behavior.cs",
      errorCount: 0,
      warningCount: 0,
      diagnostics: [],
    })),
    saveScript: jest.fn(),
    runDraftScript: jest.fn(),
    proposeEvolution: jest.fn(),
    generateScript: jest.fn(),
  },
}));

jest.mock("./components/StudioBootstrapGate", () => ({
  __esModule: true,
  default: ({ children }: MockChildrenProps) => children,
}));

jest.mock("./components/StudioBuildPanels", () => {
  const mockReact = require("react");
  const StudioWorkflowBuildPanel = (props: any) => {
    const [detailsMode, setDetailsMode] = mockReact.useState("step");
    const [addStepType, setAddStepType] = mockReact.useState(
      props.availableStepTypes?.[0] || "llm_call"
    );
    const selectedStep = mockReact.useMemo(() => {
      const selectedStepId = String(props.selectedGraphNodeId || "").replace(/^step:/, "");
      return (
        props.workflowGraph?.steps?.find((step: any) => step.id === selectedStepId) ||
        props.workflowGraph?.steps?.[0] ||
        null
      );
    }, [props.selectedGraphNodeId, props.workflowGraph?.steps]);
    const [stepDraft, setStepDraft] = mockReact.useState(() => ({
      id: selectedStep?.id || "",
      type: selectedStep?.type || "llm_call",
      targetRole: selectedStep?.targetRole || "",
      next: selectedStep?.next || "",
      parametersText: JSON.stringify(selectedStep?.parameters || {}, null, 2),
      branchesText: JSON.stringify(selectedStep?.branches || {}, null, 2),
    }));

    mockReact.useEffect(() => {
      setStepDraft({
        id: selectedStep?.id || "",
        type: selectedStep?.type || "llm_call",
        targetRole: selectedStep?.targetRole || "",
        next: selectedStep?.next || "",
        parametersText: JSON.stringify(selectedStep?.parameters || {}, null, 2),
        branchesText: JSON.stringify(selectedStep?.branches || {}, null, 2),
      });
    }, [selectedStep]);

    return mockReact.createElement("div", { "data-testid": "studio-workflow-build-panel" }, [
      mockReact.createElement("div", { key: "eyebrow" }, "DAG Canvas"),
      mockReact.createElement("div", { key: "provenance" }, "canvas · live"),
      mockReact.createElement(
        "select",
        {
          key: "add-step-type",
          "aria-label": "Add step type",
          value: addStepType,
          onChange: (event: MockValueEvent) => setAddStepType(event.target.value),
        },
        (props.availableStepTypes || ["llm_call"]).map((stepType: string) =>
          mockReact.createElement("option", { key: stepType, value: stepType }, stepType)
        )
      ),
      mockReact.createElement(
        "button",
        {
          key: "add-step",
          type: "button",
          onClick: () => props.onInsertStep?.(addStepType),
        },
        "Add step"
      ),
      mockReact.createElement(
        "button",
        {
          key: "auto-layout",
          type: "button",
          onClick: () => props.onAutoLayout?.(),
        },
        "Auto-layout"
      ),
      mockReact.createElement(
        "button",
        {
          key: "yaml-toggle",
          type: "button",
          onClick: () =>
            setDetailsMode((current: string) => (current === "yaml" ? "step" : "yaml")),
        },
        "YAML"
      ),
      mockReact.createElement(
        "div",
        { key: "node-count", "data-testid": "workflow-graph-node-count" },
        String(props.workflowGraph?.nodes?.length ?? 0)
      ),
      mockReact.createElement(
        "div",
        { key: "graph-steps", "data-testid": "mock-workflow-graph-steps" },
        (props.workflowGraph?.steps || []).map((step: any) =>
          mockReact.createElement(
            "button",
            {
              key: `graph-step-${step.id}`,
              type: "button",
              onClick: () => props.onSelectGraphNode?.(`step:${step.id}`),
            },
            step.id
          )
        )
      ),
      mockReact.createElement(
        "button",
        {
          key: "canvas-delete-selected-step",
          type: "button",
          onClick: () =>
            props.onDeleteWorkflowNodes?.(
              props.selectedGraphNodeId ? [props.selectedGraphNodeId] : []
            ),
        },
        "Delete selected step on canvas"
      ),
      detailsMode === "yaml"
        ? mockReact.createElement("div", { key: "yaml-title" }, "Workflow YAML")
        : mockReact.createElement("div", { key: "step-title" }, "Step Detail"),
      detailsMode === "step"
        ? mockReact.createElement(
            mockReact.Fragment,
            { key: "step-form" },
            mockReact.createElement("input", {
              "aria-label": "Step ID",
              value: stepDraft.id,
              onChange: (event: MockValueEvent) =>
                setStepDraft((current: any) => ({ ...current, id: event.target.value })),
            }),
            mockReact.createElement(
              "select",
              {
                "aria-label": "Step type",
                value: stepDraft.type,
                onChange: (event: MockValueEvent) =>
                  setStepDraft((current: any) => ({ ...current, type: event.target.value })),
              },
              (props.availableStepTypes || ["llm_call"]).map((stepType: string) =>
                mockReact.createElement("option", { key: stepType, value: stepType }, stepType)
              )
            ),
            mockReact.createElement("input", {
              "aria-label": "Target role",
              value: stepDraft.targetRole,
              onChange: (event: MockValueEvent) =>
                setStepDraft((current: any) => ({
                  ...current,
                  targetRole: event.target.value,
                })),
            }),
            mockReact.createElement("input", {
              "aria-label": "Next step",
              value: stepDraft.next,
              onChange: (event: MockValueEvent) =>
                setStepDraft((current: any) => ({ ...current, next: event.target.value })),
            }),
            mockReact.createElement("textarea", {
              "aria-label": "Step parameters",
              value: stepDraft.parametersText,
              onChange: (event: MockValueEvent) =>
                setStepDraft((current: any) => ({
                  ...current,
                  parametersText: event.target.value,
                })),
            }),
            mockReact.createElement("textarea", {
              "aria-label": "Step branches",
              value: stepDraft.branchesText,
              onChange: (event: MockValueEvent) =>
                setStepDraft((current: any) => ({
                  ...current,
                  branchesText: event.target.value,
                })),
            }),
            mockReact.createElement(
              "button",
              {
                type: "button",
                onClick: () => props.onApplyStepDraft?.(stepDraft),
              },
              "Apply changes"
            ),
            mockReact.createElement(
              "button",
              {
                type: "button",
                onClick: () => props.onRemoveSelectedStep?.(),
              },
              "Delete step"
            )
          )
        : null,
      props.saveNotice
        ? mockReact.createElement("div", { key: "save-notice" }, props.saveNotice.message)
        : null,
      mockReact.createElement("textarea", {
        key: "yaml",
        "aria-label": "定义 YAML",
        value: props.draftYaml ?? "",
        onChange: (event: MockValueEvent) => props.onSetDraftYaml?.(event.target.value),
      }),
      mockReact.createElement("div", { key: "dry-run-title" }, "Workflow draft run"),
      mockReact.createElement(
        "div",
        { key: "dry-run-route", "data-testid": "workflow-dry-run-route" },
        props.dryRunRouteLabel || ""
      ),
      mockReact.createElement("textarea", {
        key: "run-input",
        "aria-label": "Workflow dry run input",
        value: props.runPrompt ?? "",
        onChange: (event: MockValueEvent) =>
          props.onRunPromptChange?.(event.target.value),
      }),
      mockReact.createElement(
        "button",
        {
          key: "save",
          type: "button",
          disabled: !props.canSaveWorkflow,
          onClick: () => props.onSaveDraft?.(),
        },
        "Save draft"
      ),
      mockReact.createElement(
        "button",
        {
          key: "bind",
          type: "button",
          onClick: () => props.onContinueToBind?.(),
        },
        "Continue to Bind"
      ),
    ]);
  };

  const StudioScriptBuildPanel = (props: any) => {
    const [value, setValue] = mockReact.useState("using System;");
    const [dirty, setDirty] = mockReact.useState(false);

    mockReact.useEffect(() => {
      props.onRegisterLeaveGuard?.(
        dirty ? jest.fn(async () => false) : jest.fn(async () => true)
      );

      return () => props.onRegisterLeaveGuard?.(null);
    }, [dirty, props]);

    return mockReact.createElement("div", { "data-testid": "studio-script-build-panel" }, [
      mockReact.createElement("div", { key: "title" }, "Script source"),
      mockReact.createElement("div", { key: "provenance" }, "lints · partial"),
      mockReact.createElement("input", {
        key: "script-id",
        "aria-label": "Script ID",
        value: props.selectedScriptId || "script-1",
        onChange: (event: MockValueEvent) => props.onSelectScriptId?.(event.target.value),
      }),
      mockReact.createElement("textarea", {
        key: "editor",
        "aria-label": "Script source editor",
        value,
        onChange: (event: MockValueEvent) => {
          setValue(event.target.value);
          setDirty(true);
        },
      }),
      mockReact.createElement("div", { key: "dry-run-title" }, "Script draft run"),
      mockReact.createElement("textarea", {
        key: "run-input",
        "aria-label": "Script dry run input",
        value: "{\n  \"input\": \"fixture\"\n}",
        readOnly: true,
      }),
      mockReact.createElement(
        "button",
        {
          key: "save",
          type: "button",
        },
        "Save draft"
      ),
      mockReact.createElement(
        "button",
        {
          key: "bind",
          type: "button",
          onClick: () => props.onContinueToBind?.(),
        },
        "Continue to Bind"
      ),
    ]);
  };

  const StudioGAgentBuildPanel = (props: any) =>
    mockReact.createElement("div", { "data-testid": "studio-gagent-build-panel" }, [
      mockReact.createElement("div", { key: "title" }, "GAgent definition"),
      mockReact.createElement("div", { key: "provenance" }, "template · seeded"),
      mockReact.createElement("input", {
        key: "type",
        "aria-label": "GAgent type",
        value: props.selectedGAgentTypeName || "",
        onChange: (event: MockValueEvent) =>
          props.onSelectGAgentTypeName?.(event.target.value),
      }),
      mockReact.createElement("input", {
        key: "display-name",
        "aria-label": "Display name",
        defaultValue: "orders-gagent",
      }),
      mockReact.createElement("input", {
        key: "role",
        "aria-label": "Role",
        defaultValue: "intake-classifier",
      }),
      mockReact.createElement("textarea", {
        key: "prompt",
        "aria-label": "Initial prompt",
        defaultValue: "You are the team member gagent.",
      }),
      mockReact.createElement("input", {
        key: "tools",
        "aria-label": "Tools",
        defaultValue: "classify_intent, detect_language",
      }),
      mockReact.createElement(
        "label",
        { key: "grain" },
        [
          mockReact.createElement("input", {
            key: "grain-input",
            type: "radio",
            name: "gagent-persistence",
            defaultChecked: true,
          }),
          "Orleans grain",
        ]
      ),
      mockReact.createElement(
        "label",
        { key: "ephemeral" },
        [
          mockReact.createElement("input", {
            key: "ephemeral-input",
            type: "radio",
            name: "gagent-persistence",
          }),
          "Ephemeral",
        ]
      ),
      mockReact.createElement(
        "button",
        {
          key: "bind",
          type: "button",
          onClick: () => props.onContinueToBind?.(),
        },
        "Continue to Bind"
      ),
    ]);

  return {
    __esModule: true,
    getDefaultBuildModeCards: (scriptsEnabled: boolean) => [
      {
        key: "workflow",
        label: "Workflow",
        description: "Workflow description",
        hint: "When · Multiple agents hand off predictably",
        disabled: false,
      },
      {
        key: "script",
        label: "Script",
        description: "Script description",
        hint: scriptsEnabled ? "When · You need code-level control" : "当前环境暂未启用脚本能力。",
        disabled: !scriptsEnabled,
      },
      {
        key: "gagent",
        label: "GAgent",
        description: "GAgent description",
        hint: "When · State lives with one agent",
        disabled: false,
      },
    ],
    StudioWorkflowBuildPanel,
    StudioScriptBuildPanel,
    StudioGAgentBuildPanel,
  };
});

jest.mock("./components/StudioShell", () => ({
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
  }: any) => {
    const React = require("react");
    const filterOptions = [
      "all",
      ...Array.from(
        new Set(
          (members as any[]).map((member) => String(member.kind || "unknown"))
        )
      ),
    ];
    const filterLabels: Record<string, string> = {
      all: "All",
      member: "Member",
      workflow: "Workflow",
      script: "Script",
      gagent: "GAgent",
      unknown: "Unknown",
    };
    return React.createElement(
      "div",
      null,
      [
        React.createElement("div", { key: "workbench" }, "Workbench"),
        contextBar ? React.createElement("div", { key: "context-bar" }, contextBar) : null,
        alerts ? React.createElement("div", { key: "alerts" }, alerts) : null,
        React.createElement(
          "div",
          { key: "members", "aria-label": "Team members" },
          [
            React.createElement(
              "div",
              { key: "member-filters" },
              filterOptions.map((key) =>
                React.createElement(
                  "button",
                  {
                    key: `filter-${key}`,
                    type: "button",
                  },
                  filterLabels[key] || key
                )
              )
            ),
            inventoryActions
              ? React.createElement("div", { key: "inventory-actions" }, inventoryActions)
              : null,
            ...members.map((member: any) =>
              React.createElement(
                "div",
                { key: `member-row-${member.key}` },
                [
                  React.createElement(
                    "button",
                    {
                      key: `member-${member.key}`,
                      type: "button",
                      "aria-current": selectedMemberKey === member.key ? "true" : undefined,
                      onClick: () => onSelectMember?.(member.key),
                    },
                    member.label
                  ),
                ]
              )
            ),
          ]
        ),
        ...lifecycleSteps.map((step: any) =>
          React.createElement(
            "button",
            {
              key: `step-${step.key}`,
              type: "button",
              disabled: Boolean(step.disabled),
              onClick: () => onSelectLifecycleStep?.(step.key),
            },
            step.label
          )
        ),
        ...navItems.map((item: any) =>
          React.createElement(
            "button",
            {
              key: item.key,
              type: "button",
              onClick: () => onSelectPage?.(item.key),
            },
            item.label
          )
        ),
        children,
      ]
    );
  },
}));

jest.mock("../scopes/components/ScopeServiceRuntimeWorkbench", () => ({
  __esModule: true,
  default: (props: any) => {
    const React = require("react");
    return React.createElement("div", null, [
      React.createElement(
        "div",
        { key: "title", "data-testid": "studio-bind-surface" },
        "Runtime Workbench Mock"
      ),
      React.createElement(
        "div",
        { key: "service" },
        props.initialServiceId || props.preferredServiceId || "no-service"
      ),
      React.createElement(
        "button",
        {
          key: "use-endpoint",
          type: "button",
          onClick: () => props.onUseEndpoint?.("default", "chat"),
        },
        "Use runtime endpoint"
      ),
    ]);
  },
}));

jest.mock("./components/bind/StudioMemberBindPanel", () => ({
  __esModule: true,
  default: (props: any) => {
    const React = require("react");
    return React.createElement("div", null, [
      React.createElement(
        "div",
        { key: "title", "data-testid": "studio-bind-surface" },
        "Bind Surface Mock"
      ),
      React.createElement(
        "div",
        { key: "service" },
        `service:${props.initialServiceId || props.preferredServiceId || "no-service"}`
      ),
      React.createElement(
        "div",
        { key: "services" },
        `services:${
          Array.isArray(props.services) && props.services.length > 0
            ? props.services.map((service: any) => service.serviceId).join(",")
            : "none"
        }`
      ),
      React.createElement(
        "div",
        { key: "candidate" },
        props.pendingBindingCandidate
          ? `candidate:${props.pendingBindingCandidate.displayName}`
          : "candidate:none"
      ),
      React.createElement(
        "button",
        {
          key: "select-endpoint",
          type: "button",
          onClick: () =>
            props.onSelectionChange?.({
              serviceId: "default",
              endpointId: "support-chat",
            }),
        },
        "Select bind endpoint"
      ),
      React.createElement(
        "button",
        {
          key: "bind-candidate",
          type: "button",
          onClick: () => void props.onBindPendingCandidate?.(),
        },
        "Bind current member"
      ),
      React.createElement(
        "button",
        {
          key: "continue",
          type: "button",
          onClick: () => props.onContinueToInvoke?.("default", "support-chat"),
        },
        "Continue to Invoke"
      ),
    ]);
  },
}));

jest.mock("./components/StudioMemberInvokePanel", () => ({
  __esModule: true,
  default: (props: any) => {
    const React = require("react");
    return React.createElement("div", null, [
      React.createElement(
        "div",
        { key: "title", "data-testid": "studio-invoke-surface" },
        "Invoke Surface Mock"
      ),
      React.createElement(
        "div",
        { key: "service" },
        `service:${props.initialServiceId || "no-service"}`
      ),
      React.createElement(
        "div",
        { key: "member" },
        `member:${props.selectedMemberLabel || "no-member"}`
      ),
      React.createElement(
        "div",
        { key: "services" },
        `services:${
          Array.isArray(props.services) && props.services.length > 0
            ? props.services.map((service: any) => service.serviceId).join(",")
            : "none"
        }`
      ),
      React.createElement(
        "div",
        { key: "endpoint" },
        `endpoint:${props.initialEndpointId || "no-endpoint"}`
      ),
      React.createElement(
        "button",
        {
          key: "emit-observe-session",
          type: "button",
          onClick: () => {
            const now = Date.now();
            const startedAtUtc = new Date(now - 1000).toISOString();
            const completedAtUtc = new Date(now).toISOString();
            props.onObserveSessionChange?.({
              actorId: "actor-invoke",
              assistantText: "Observed output",
              commandId: "command-invoke",
              completedAtUtc,
              endpointId: props.initialEndpointId || "chat",
              error: "",
              events: [
                {
                  name: "aevatar.run.context",
                  timestamp: now - 1000,
                  type: "CUSTOM",
                  value: {
                    actorId: "actor-invoke",
                    commandId: "command-invoke",
                  },
                },
                {
                  result: "Observed output",
                  runId: "invoke-run-1",
                  threadId: "actor-invoke",
                  timestamp: now,
                  type: "RUN_FINISHED",
                },
              ],
              finalOutput: "Observed output",
              mode: "stream",
              payloadBase64: "",
              payloadTypeUrl: "",
              prompt: "Observe this invoke result.",
              runId: "invoke-run-1",
              serviceId: props.initialServiceId || "default",
              serviceLabel: props.selectedMemberLabel || "workspace-demo",
              startedAtUtc,
              status: "success",
            });
          },
        },
        "Emit Observe Session"
      ),
      props.emptyState
        ? React.createElement(
            "div",
            { key: "empty" },
            `empty:${props.emptyState.message}`
          )
        : null,
    ]);
  },
}));

jest.mock("./components/StudioFilesPage", () => ({
  __esModule: true,
  default: (props: any) => {
    const React = require("react");
    return React.createElement("div", null, [
      React.createElement("h2", { key: "title" }, "Files"),
      React.createElement("div", { key: "scope" }, props.scopeId || "workspace"),
      React.createElement(
        "button",
        {
          key: "settings",
          type: "button",
          onClick: () => props.onOpenSettings?.(),
        },
        "Open Settings"
      ),
    ]);
  },
}));

jest.mock("./components/StudioWorkbenchSections", () => {
  const React = require("react");

  const dedupeStudioWorkflowSummaries = (
    workflows: readonly any[]
  ) => {
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

      return String(left.workflowId ?? "").localeCompare(
        String(right.workflowId ?? "")
      );
    };

    for (const workflow of workflows) {
      const key =
        String(workflow.name ?? "").trim().toLowerCase() ||
        String(workflow.workflowId ?? "").trim().toLowerCase();
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
    errorTitle: string
  ) => {
    if (!notice) {
      return null;
    }

    return React.createElement(
      "div",
      { key },
      notice.type === "error" ? errorTitle : successTitle
    );
  };

  const StudioWorkflowsPage = (props: any) =>
    React.createElement("div", null, [
      React.createElement("h2", { key: "title" }, "行为定义"),
      React.createElement("div", { key: "draft" }, "当前定义"),
      React.createElement(
        "button",
        {
          key: "open-editor",
          type: "button",
          disabled: !props.activeWorkflowSourceKey,
          onClick: () => props.onOpenCurrentDraft?.(),
        },
        "进入编辑"
      ),
      React.createElement("input", {
        key: "search",
        placeholder: "搜索定义",
        value: props.workflowSearch ?? "",
        onChange: (event: MockValueEvent) =>
          props.onSetWorkflowSearch?.(event.target.value),
      }),
      ...(props.workflows.data ?? []).map((workflow: any) =>
        React.createElement(
          "button",
          {
            key: workflow.workflowId,
            type: "button",
            onClick: () => props.onOpenWorkflow?.(workflow.workflowId),
          },
          workflow.name
        )
      ),
      React.createElement(
        "button",
        {
          key: "blank",
          type: "button",
          onClick: () => props.onStartBlankDraft?.(),
        },
        "新建定义"
      ),
    ]);

  const StudioEditorPage = (props: any) => {
    const [runOpen, setRunOpen] = React.useState(false);
    const [askAiOpen, setAskAiOpen] = React.useState(false);
    const title =
      props.teamCreation?.teamName
        ? `创建团队：${props.teamCreation.teamName}`
        : props.draftMode === "new"
        ? "新建草稿"
        : props.templateWorkflowName
        ? "模板定义"
        : "当前定义";
    const publishLabel = props.teamCreation
      ? "发布团队入口"
      : props.scopeBinding?.available
      ? "更新团队入口"
      : "绑定团队入口";

    return React.createElement(
      "div",
      null,
      [
        React.createElement("div", { key: "title" }, title),
        React.createElement("div", { key: "graph-title" }, "行为画布"),
        React.createElement(
          "div",
          {
            key: "graph-count",
            "data-testid": "workflow-graph-node-count",
          },
          String(props.workflowGraph?.nodes?.length ?? 0)
        ),
        renderNoticeTitle(
          "save-notice",
          props.saveNotice,
          "定义已保存",
          "定义保存失败"
        ),
        renderNoticeTitle(
          "run-notice",
          props.runNotice,
          "测试运行已启动",
          "测试运行失败"
        ),
        React.createElement(
          "div",
          {
            key: "run-prompt-state",
            "data-testid": "studio-run-prompt-state",
          },
          props.runPrompt ?? ""
        ),
        renderNoticeTitle(
          "ask-ai-notice",
          props.askAiNotice,
          "AI 已更新当前草稿",
          "AI 生成失败"
        ),
        props.askAiNotice
          ? React.createElement(
              "div",
              { key: "ask-ai-notice-message" },
              props.askAiNotice.message
            )
          : null,
        React.createElement("input", {
          key: "workflow-name",
          "aria-label": "定义名称",
          value: props.draftWorkflowName ?? "",
          onChange: (event: MockValueEvent) =>
            props.onSetDraftWorkflowName?.(event.target.value),
        }),
        React.createElement("textarea", {
          key: "workflow-yaml",
          "aria-label": "定义 YAML",
          value: props.draftYaml ?? "",
          onChange: (event: MockValueEvent) =>
            props.onSetDraftYaml?.(event.target.value),
        }),
        React.createElement(
          "button",
          {
            key: "save",
            type: "button",
            onClick: () => props.onSaveDraft?.(),
          },
          "保存定义"
        ),
        React.createElement(
          "button",
          {
            key: "clear-directory",
            type: "button",
            onClick: () => props.onSetDraftDirectoryId?.(""),
          },
          "清空目录"
        ),
        React.createElement(
          "button",
          {
            key: "yaml",
            type: "button",
            onClick: () => props.onSetInspectorTab?.("yaml"),
          },
          "YAML"
        ),
        props.inspectorTab === "yaml"
          ? React.createElement("div", { key: "yaml-panel" }, [
              React.createElement(
                "div",
                { key: "yaml-title" },
                "已校验 YAML"
              ),
              React.createElement("textarea", {
                key: "yaml-view",
                "aria-label": "行为定义 YAML",
                readOnly: true,
                value: props.draftYaml ?? "",
              }),
            ])
          : null,
        props.recentPromptHistory?.length
          ? React.createElement("div", { key: "recent-prompts" }, [
              React.createElement("div", { key: "label" }, "Recent prompts"),
              React.createElement(
                "button",
                {
                  key: "reuse",
                  type: "button",
                  onClick: () =>
                    props.onReusePrompt?.(props.recentPromptHistory[0].prompt),
                },
                "Reuse prompt"
              ),
            ])
          : null,
        React.createElement(
          "button",
          {
            key: "run-toggle",
            "data-testid": "studio-editor-run-button",
            type: "button",
            disabled: !props.canOpenRunWorkflow,
            onClick: () => setRunOpen(true),
          },
          "测试运行"
        ),
        React.createElement(
          "button",
          {
            key: "publish",
            "data-testid": "studio-publish-workflow-button",
            type: "button",
            disabled: !props.resolvedScopeId || !props.canPublishWorkflow,
            onClick: () => props.onPublishWorkflow?.(),
          },
          publishLabel
        ),
        props.scopeBinding?.available &&
        props.projectEntryReadyForCurrentWorkflow
          ? React.createElement(
              "button",
              {
                key: "project-entry",
                type: "button",
                onClick: () => props.onOpenProjectInvoke?.(),
              },
              "打开测试台"
            )
          : null,
        React.createElement(
          "button",
          {
            key: "bind-gagent",
            type: "button",
            disabled: !props.resolvedScopeId,
            onClick: () =>
              props.onBindGAgent?.({
                displayName: "orders-gagent",
                actorTypeName: "Tests.OrdersGAgent, Tests",
                endpointId: "run",
                endpointDisplayName: "Run",
                requestTypeUrl:
                  "type.googleapis.com/google.protobuf.StringValue",
                responseTypeUrl: "type.googleapis.com/example.RunResult",
                description: "Run the bound gagent.",
                prompt: "Run the orders gagent",
              }),
          },
          "绑定团队入口"
        ),
        React.createElement(
          "button",
          {
            key: "bind-gagent-runs",
            type: "button",
            disabled: !props.resolvedScopeId,
            onClick: () =>
              props.onBindGAgent?.(
                {
                  displayName: "orders-gagent",
                  actorTypeName: "Tests.OrdersGAgent, Tests",
                  endpointId: "run",
                  endpointDisplayName: "Run",
                  requestTypeUrl:
                    "type.googleapis.com/google.protobuf.StringValue",
                  responseTypeUrl: "type.googleapis.com/example.RunResult",
                  description: "Run the bound gagent.",
                  prompt: "Run the orders gagent",
                },
                { openRuns: true }
              ),
          },
          "绑定团队入口并打开测试运行"
        ),
        React.createElement(
          "button",
          {
            key: "bind-gagent-chat-runs",
            type: "button",
            disabled: !props.resolvedScopeId,
            onClick: () =>
              props.onBindGAgent?.(
                {
                  displayName: "orders-gagent",
                  actorTypeName: "Tests.OrdersGAgent, Tests",
                  endpoints: [
                    {
                      endpointId: "run",
                      displayName: "Run",
                      kind: "command",
                      requestTypeUrl:
                        "type.googleapis.com/google.protobuf.StringValue",
                      responseTypeUrl:
                        "type.googleapis.com/example.RunResult",
                      description: "Run the bound gagent.",
                    },
                    {
                      endpointId: "support-chat",
                      displayName: "Chat",
                      kind: "chat",
                      requestTypeUrl: "",
                      responseTypeUrl: "",
                      description: "Chat with the bound gagent.",
                    },
                  ],
                  openRunsEndpointId: "support-chat",
                  prompt: "Chat with the orders gagent",
                },
                { openRuns: true }
              ),
          },
          "绑定聊天入口并打开测试运行"
        ),
        React.createElement(
          "button",
          {
            key: "open-runs",
            type: "button",
            onClick: () => props.onRunInConsole?.(),
          },
          "Open runs"
        ),
        props.scopeBinding?.available
          ? React.createElement(
              "button",
              {
                key: "activate-rev-1",
                type: "button",
                onClick: () => props.onActivateBindingRevision?.("rev-1"),
              },
              "Activate rev-1"
            )
          : null,
        props.scopeBinding?.available
          ? React.createElement(
              "button",
              {
                key: "retire-rev-1",
                type: "button",
                onClick: () => props.onRetireBindingRevision?.("rev-1"),
              },
              "Retire rev-1"
            )
          : null,
        (runOpen || props.canOpenRunWorkflow)
          ? React.createElement("div", { key: "run-dialog" }, [
              React.createElement("textarea", {
                key: "run-prompt",
                "data-testid": "studio-run-prompt-input",
                "aria-label": "Studio 测试运行输入",
                value: props.runPrompt ?? "",
                onChange: (event: MockValueEvent) =>
                  props.onRunPromptChange?.(event.target.value),
              }),
              React.createElement(
                "button",
                {
                  key: "run-submit",
                  "data-testid": "studio-run-submit-button",
                  type: "button",
                  disabled: !props.canRunWorkflow,
                  onClick: () => props.onStartExecution?.(),
                },
                "打开测试运行"
              ),
            ])
          : null,
        React.createElement(
          "button",
          {
            key: "ask-ai-toggle",
            type: "button",
            disabled: props.canAskAiGenerate === false,
            title: props.askAiUnavailableMessage ?? "",
            onClick: () => setAskAiOpen(true),
          },
          "Open Ask AI"
        ),
        askAiOpen
          ? React.createElement("div", { key: "ask-ai-panel" }, [
              React.createElement("textarea", {
                key: "ask-ai-prompt",
                "aria-label": "Studio AI workflow prompt",
                value: props.askAiPrompt ?? "",
                onChange: (event: MockValueEvent) =>
                  props.onAskAiPromptChange?.(event.target.value),
              }),
              React.createElement(
                "button",
                {
                  key: "ask-ai-submit",
                  type: "button",
                  onClick: () => props.onAskAiGenerate?.(),
                },
                "Generate"
              ),
            ])
          : null,
      ].filter(Boolean)
    );
  };

  const StudioExecutionPage = (props: any) =>
    React.createElement(
      "div",
      null,
      [
        React.createElement("div", { key: "logs" }, "Logs"),
        React.createElement(
          "div",
          { key: "member" },
          `observe-member:${props.selectedMemberLabel || "no-member"}`
        ),
        React.createElement(
          "div",
          { key: "implementation" },
          `observe-implementation:${props.currentImplementationLabel || "no-implementation"}`
        ),
        React.createElement(
          "div",
          { key: "runs" },
          `observe-runs:${
            Array.isArray(props.executions?.data) && props.executions.data.length > 0
              ? props.executions.data.map((item: any) => item.executionId).join(",")
              : "none"
          }`
        ),
        React.createElement(
          "div",
          { key: "selected" },
          `observe-selected:${props.selectedExecution?.data?.executionId || "none"}`
        ),
        props.emptyState
          ? React.createElement(
              "div",
              { key: "empty" },
              `observe-empty:${props.emptyState.title}`
            )
          : null,
        renderNoticeTitle(
          "execution-notice",
          props.executionNotice,
          "Execution stop requested",
          "Execution stop failed"
        ),
        React.createElement(
          "button",
          {
            key: "stop",
            type: "button",
            onClick: () => props.onStopExecution?.(),
          },
          "Stop"
        ),
      ].filter(Boolean)
    );

  const StudioRolesPage = (props: any) => {
    const selectedRole =
      props.selectedRole ?? props.roleCatalogDraft?.[0] ?? null;
    return React.createElement(
      "div",
      null,
      [
        React.createElement("div", { key: "label" }, "Saved roles"),
        React.createElement(
          "button",
          {
            key: "use",
            type: "button",
            onClick: () => props.onApplyRoleToWorkflow?.(selectedRole?.key),
          },
          "Use"
        ),
        React.createElement(
          "button",
          {
            key: "save",
            type: "button",
            onClick: () => props.onSaveRoles?.(),
          },
          "Save"
        ),
      ].filter(Boolean)
    );
  };

  const StudioConnectorsPage = (_props: any) => {
    return React.createElement(
      "div",
      null,
      [
        React.createElement("div", { key: "label" }, "Connectors"),
      ].filter(Boolean)
    );
  };

  const StudioSettingsPage = (_props: any) =>
    React.createElement(
      "div",
      null,
      [
        React.createElement("div", { key: "label" }, "Provider settings"),
      ].filter(Boolean)
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

function renderStudioPage(route = "/studio") {
  window.history.pushState({}, "", route);
  return renderWithQueryClient(React.createElement(StudioPage));
}

async function replaceStudioRoute(route: string) {
  await act(async () => {
    window.history.replaceState({}, "", route);
    window.dispatchEvent(
      new PopStateEvent("popstate", { state: window.history.state })
    );
  });
}

describe("StudioPage", () => {
  beforeEach(() => {
    window.history.pushState({}, "", "/studio");
    window.localStorage.clear();
    window.sessionStorage.clear();
    resetMockState();
    jest.clearAllMocks();
    mockEnsureActiveAuthSession.mockReset();
    mockEnsureActiveAuthSession.mockResolvedValue(null);
    mockRuntimeQueryApi.listPrimitives.mockResolvedValue([
      {
        name: "llm_call",
        aliases: [],
        category: "core",
        description: "LLM call",
        parameters: [],
        exampleWorkflows: [],
      },
      {
        name: "demo_template",
        aliases: ["render_template"],
        category: "demo",
        description: "Demo template primitive",
        parameters: [],
        exampleWorkflows: ["demo_template"],
      },
    ]);
    mockRuntimeGAgentApi.listTypes.mockResolvedValue([
      {
        typeName: "OrdersGAgent",
        fullName: "Tests.OrdersGAgent",
        assemblyName: "Tests",
      },
    ]);
    mockRuntimeGAgentApi.listActors.mockResolvedValue([
      {
        gAgentType: "Tests.OrdersGAgent",
        actorIds: ["orders-gagent"],
      },
    ]);
    mockServicesApi.listServices.mockResolvedValue([
      {
        serviceId: "default",
        displayName: "workspace-demo",
        deploymentStatus: "Active",
        primaryActorId: "actor-default",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with the published workflow.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
    ]);
    mockScopeRuntimeApi.getServiceRevisions.mockReset();
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        mockBuildServiceRevisionCatalog({ serviceId })
    );
    mockScopeRuntimeApi.listMemberRuns.mockReset();
    mockScopeRuntimeApi.listMemberRuns.mockImplementation(
      async (_scopeId: string, memberId: string) => ({
        scopeId: "scope-1",
        serviceId: memberId,
        serviceKey: `scope-1:default:default:${memberId}`,
        displayName: "workspace-demo",
        runs: [mockBuildServiceRunSummary({ serviceId: memberId })],
      })
    );
    mockScopeRuntimeApi.listServiceRuns.mockReset();
    mockScopeRuntimeApi.listServiceRuns.mockImplementation(
      async (_scopeId: string, serviceId: string) => ({
        scopeId: "scope-1",
        serviceId,
        serviceKey: `scope-1:default:default:${serviceId}`,
        displayName: "workspace-demo",
        runs: [mockBuildServiceRunSummary({ serviceId })],
      })
    );
    mockScopeRuntimeApi.getMemberRunAudit.mockReset();
    mockScopeRuntimeApi.getMemberRunAudit.mockImplementation(
      async (_scopeId: string, memberId: string, runId: string) =>
        mockBuildServiceRunAuditSnapshot({ serviceId: memberId, runId })
    );
    mockScopeRuntimeApi.getServiceRunAudit.mockReset();
    mockScopeRuntimeApi.getServiceRunAudit.mockImplementation(
      async (_scopeId: string, serviceId: string, runId: string) =>
        mockBuildServiceRunAuditSnapshot({ serviceId, runId })
    );
    mockRuntimeRunsApi.stop.mockReset();
    mockRuntimeRunsApi.stop.mockResolvedValue({
      accepted: true,
      runId: "execution-1",
    });
    mockRuntimeRunsApi.resume.mockReset();
    mockRuntimeRunsApi.resume.mockResolvedValue({
      accepted: true,
      runId: "execution-1",
    });
    mockRuntimeRunsApi.signal.mockReset();
    mockRuntimeRunsApi.signal.mockResolvedValue({
      accepted: true,
      runId: "execution-1",
    });
    (studioApi.getAuthSession as jest.Mock).mockReset();
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue(
      mockCreateDefaultStudioAuthSession()
    );
    (studioApi.getAppContext as jest.Mock).mockReset();
    (studioApi.getAppContext as jest.Mock).mockResolvedValue(
      mockCreateDefaultStudioAppContext()
    );
    (studioApi.listWorkflows as jest.Mock).mockReset();
    (studioApi.listWorkflows as jest.Mock).mockResolvedValue(
      mockCreateDefaultWorkflowSummaries()
    );
    (studioApi.getWorkflow as jest.Mock).mockReset();
    (studioApi.getWorkflow as jest.Mock).mockImplementation(
      async () => mockWorkflowFile
    );
    (studioApi.authorWorkflow as jest.Mock).mockReset();
    (studioApi.authorWorkflow as jest.Mock).mockImplementation(
      mockAuthorWorkflowSuccess
    );
  });

  it("loads workspace data and shows the workflow build workbench by default", async () => {
    renderStudioPage("/studio");

    await waitFor(() => {
      expect(studioApi.getAppContext).toHaveBeenCalled();
      expect(studioApi.getWorkspaceSettings).toHaveBeenCalled();
      expect(studioApi.listWorkflows).toHaveBeenCalled();
    });

    expect(await screen.findByTestId("studio-context-title")).toBeTruthy();
    expect(screen.getByText("Workbench")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId("studio-context-title")).toHaveTextContent(
        "workspace-demo"
      );
    });
    expect(screen.getByTestId("studio-context-meta")).toHaveTextContent(
      "workflow canvas"
    );
    expect(screen.getByTestId("studio-context-bar")).toHaveStyle({
      gap: "12px",
      padding: "8px 16px 4px",
    });
    expect(screen.getByTestId("studio-build-mode-switcher")).toHaveStyle({
      gap: "4px",
    });
    expect(screen.getByTestId("studio-workflow-build-panel")).toBeTruthy();
    expect(screen.getByText("DAG Canvas")).toBeTruthy();
    expect(screen.getByText("Step Detail")).toBeTruthy();
    expect(screen.getByText("Workflow draft run")).toBeTruthy();
    expect(screen.queryByText("Workflow description")).toBeNull();
    expect(
      screen.queryByText(
        "Build 阶段先确定当前 member 采用哪种实现方式，然后在同一块 workbench 里直接完成 authoring 和 dry-run。"
      )
    ).toBeNull();
    expect(screen.getByRole("button", { name: /^Workflow/ })).toHaveAttribute(
      "aria-pressed",
      "true"
    );
    expect(screen.getByRole("button", { name: /^Workflow/ })).toHaveStyle({
      height: "28px",
      fontSize: "11px",
    });
    expect(screen.getByRole("button", { name: /^Script/ })).toBeDisabled();
    expect(screen.getByRole("button", { name: /^GAgent/ })).toHaveAttribute(
      "aria-pressed",
      "false"
    );
    expect(screen.queryByRole("button", { name: "定义库" })).toBeNull();
    expect(screen.queryByRole("button", { name: "工作流画布" })).toBeNull();

    fireEvent.click(
      screen.getByRole("button", { name: "Open construction mode help" })
    );
    expect(
      await screen.findByText(
        "Build 阶段先确定当前 member 采用哪种实现方式，然后在同一块 workbench 里直接完成 authoring 和 dry-run。"
      )
    ).toBeTruthy();
    expect(await screen.findByText("Workflow description")).toBeTruthy();
  });

  it("falls back to a ready route when the preferred workflow dry-run route is stale", async () => {
    (studioApi.getUserConfig as jest.Mock).mockResolvedValueOnce({
      defaultModel: "gpt-4.1-mini",
      preferredLlmRoute: "/api/v1/proxy/s/stale-openai",
      runtimeBaseUrl: "",
    });
    (studioApi.getUserConfigModels as jest.Mock).mockResolvedValueOnce({
      providers: [
        {
          providerSlug: "openai",
          providerName: "OpenAI",
          status: "ready",
          proxyUrl: "https://nyx-api.example/gateway/openai",
          source: "gateway_provider",
        },
      ],
      gatewayUrl: "https://nyx-api.example/gateway",
      modelsByProvider: {
        openai: ["gpt-5.4-mini"],
      },
      supportedModels: ["gpt-5.4-mini"],
    });

    renderStudioPage("/studio");

    const routeLabel = await screen.findByTestId("workflow-dry-run-route");
    await waitFor(() => {
      expect(routeLabel).toHaveTextContent("NyxID Gateway");
    });
  });

  it("strips legacy label params while preserving stable scope and member ids", async () => {
    renderStudioPage(
      "/studio?scopeId=scope-a&scopeLabel=%E5%9B%A2%E9%98%9F+A&memberId=member-alpha&memberLabel=%E6%88%90%E5%91%98+Alpha&focus=workflow%3Aworkflow-1&tab=studio"
    );

    expect(await screen.findByRole("button", { name: "返回团队" })).toBeTruthy();
    expect(screen.getByTestId("studio-context-meta")).not.toHaveTextContent("团队 A");
    expect(screen.getByTestId("studio-context-meta")).not.toHaveTextContent("成员 Alpha");
    expect(screen.getByTestId("studio-workflow-build-panel")).toBeTruthy();

    await waitFor(() => {
      expect(window.location.pathname).toBe("/studio");
    });

    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get("scopeId")).toBe("scope-a");
    expect(searchParams.get("member")).toBeNull();
    expect(searchParams.get("memberId")).toBe("member-alpha");
    expect(searchParams.get("scopeLabel")).toBeNull();
    expect(searchParams.get("memberLabel")).toBeNull();
    expect(searchParams.get("focus")).toBe("workflow:workflow-1");
    expect(searchParams.get("tab")).toBe("studio");
  });

  it("resyncs the Studio state from stable scope and member ids when the route changes after mount", async () => {
    renderStudioPage(
      "/studio?scopeId=scope-a&scopeLabel=%E5%9B%A2%E9%98%9F+A&memberId=member-alpha&memberLabel=%E6%88%90%E5%91%98+Alpha&focus=workflow%3Aworkflow-1&tab=studio"
    );

    expect(await screen.findByRole("button", { name: "返回团队" })).toBeTruthy();

    await replaceStudioRoute(
      "/studio?scopeId=scope-b&scopeLabel=%E5%9B%A2%E9%98%9F+B&memberId=member-beta&memberLabel=%E6%88%90%E5%91%98+Beta&tab=workflows"
    );

    expect(await screen.findByRole("button", { name: "返回团队" })).toBeTruthy();
    expect(screen.getByTestId("studio-context-title")).toHaveTextContent(
      "workspace-demo"
    );
    expect(screen.getByTestId("studio-context-meta")).not.toHaveTextContent("团队 B");
    expect(screen.getByTestId("studio-context-meta")).not.toHaveTextContent("成员 Beta");
    expect(screen.getByTestId("studio-workflow-build-panel")).toBeTruthy();

    await waitFor(() => {
      expect(mockServicesApi.listServices).toHaveBeenCalledWith(
        expect.objectContaining({
          tenantId: "scope-b",
        })
      );
    });

    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get("scopeId")).toBe("scope-b");
    expect(searchParams.get("member")).toBeNull();
    expect(searchParams.get("memberId")).toBe("member-beta");
    expect(searchParams.get("scopeLabel")).toBeNull();
    expect(searchParams.get("memberLabel")).toBeNull();
    expect(searchParams.get("focus")).toBe("workflow:workflow-1");
    expect(searchParams.get("tab")).toBe("studio");
  });

  it("ignores removed create-team route params and falls back to the explicit member-selection empty state", async () => {
    renderStudioPage(
      "/studio?draft=new&teamMode=create&teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3"
    );

    expect(await screen.findByRole("button", { name: "返回团队" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "返回创建页" })).toBeNull();
    expect(await screen.findByTestId("studio-empty-member-state")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "发布团队入口" })).toBeNull();

    const rail = await screen.findByLabelText("Team members");
    expect(within(rail).queryByRole("button", { name: "draft" })).toBeNull();

    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get("teamMode")).toBeNull();
    expect(searchParams.get("teamName")).toBeNull();
    expect(searchParams.get("entryName")).toBeNull();
    expect(searchParams.get("teamDraftWorkflowId")).toBeNull();
    expect(searchParams.get("teamDraftWorkflowName")).toBeNull();
    expect(searchParams.get("draft")).toBeNull();
    expect(searchParams.get("focus")).toBeNull();
  });

  it("resyncs the Studio deep link when the target workflow changes after mount", async () => {
    renderStudioPage("/studio?focus=workflow%3Aworkflow-1&tab=studio");

    expect(await screen.findByText("DAG Canvas")).toBeTruthy();

    await replaceStudioRoute(
      "/studio?focus=template%3Apublished-demo&prompt=Continue%20this%20workflow%20in%20Studio&tab=studio"
    );

    await waitFor(() => {
      expect(studioApi.getTemplateWorkflow).toHaveBeenCalledWith(
        "published-demo"
      );
    });

    expect(
      (await screen.findByLabelText("Workflow dry run input")) as HTMLTextAreaElement
    ).toHaveValue("Continue this workflow in Studio");
  });

  it("canonicalizes conflicting workflow member and focus params to the current build focus", async () => {
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "joker",
        name: "joker",
        description: "Current workflow focus",
        fileName: "joker.yaml",
        filePath: "/tmp/workflows/joker.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
      {
        workflowId: "draft2",
        name: "draft2",
        description: "Stale workflow member token",
        fileName: "draft2.yaml",
        filePath: "/tmp/workflows/draft2.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);

    renderStudioPage(
      "/studio?scopeId=scope-1&member=workflow%3Adraft2&step=build&focus=workflow%3Ajoker&tab=studio"
    );

    expect(await screen.findByTestId("studio-workflow-build-panel")).toBeTruthy();

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("focus")).toBe("workflow:joker");
      expect(searchParams.get("step")).toBe("build");
    });
  });

  it("falls back to the workflow build workbench when the removed files tab is requested", async () => {
    renderStudioPage("/studio?tab=files");

    expect(await screen.findByText("DAG Canvas")).toBeTruthy();
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("tab")).toBe("studio");
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("focus")).toBe("workflow:workflow-1");
    });
  });

  it("hydrates an editable blank draft when a scope workflow has no YAML source yet", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValue({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
      workflowStorageMode: "scope",
    });
    mockWorkflowFile = {
      ...mockWorkflowFile,
      name: "scope-demo",
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      yaml: "",
      document: null,
      findings: [
        {
          level: "error",
          path: "/",
          message: "Workflow YAML is not available yet.",
        },
      ],
    };

    renderStudioPage("/studio?focus=workflow%3Aworkflow-1&tab=studio");

    expect(await screen.findByText("DAG Canvas")).toBeTruthy();
    expect(screen.queryByText("尚未加载定义")).toBeNull();
    await waitFor(() => {
      expect(
        (screen.getByLabelText("定义 YAML")) as HTMLTextAreaElement
      ).toHaveValue("name: scope-demo\nsteps: []\n");
    });
  });

  it("switches the build stage into GAgent mode", async () => {
    renderStudioPage("/studio");

    expect(await screen.findByText("DAG Canvas")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /^GAgent/ }));

    expect(await screen.findByTestId("studio-gagent-build-panel")).toBeTruthy();
    expect(screen.getByTestId("studio-context-title")).toHaveTextContent(
      "GAgent 构建"
    );

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("tab")).toBe("gagents");
      expect(searchParams.get("step")).toBe("build");
    });
  });

  it("shows the standalone GAgent definition fields inside Build", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
    });

    renderStudioPage("/studio");

    fireEvent.click(await screen.findByRole("button", { name: /^GAgent/ }));

    await waitFor(() => {
      expect(mockRuntimeGAgentApi.listTypes).toHaveBeenCalled();
    });

    expect(await screen.findByLabelText("GAgent type")).toBeTruthy();
    expect(screen.getByLabelText("Display name")).toBeTruthy();
    expect(screen.getByLabelText("Role")).toBeTruthy();
    expect(screen.getByLabelText("Initial prompt")).toBeTruthy();
    expect(screen.getByLabelText("Tools")).toBeTruthy();
    expect(screen.getByLabelText("Orleans grain")).toBeTruthy();
    expect(screen.getByLabelText("Ephemeral")).toBeTruthy();
  });

  it("opens the workflow build surface when a prompt is carried into Studio", async () => {
    renderStudioPage(
      "/studio?tab=workflows&prompt=Continue%20this%20workflow%20in%20Studio"
    );

    expect(await screen.findByText("DAG Canvas")).toBeTruthy();
    expect(screen.getByRole("button", { name: /^Workflow/ })).toHaveAttribute(
      "aria-pressed",
      "true"
    );
    expect(
      (await screen.findByLabelText("Workflow dry run input")) as HTMLTextAreaElement
    ).toHaveValue("Continue this workflow in Studio");

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("tab")).toBe("studio");
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("focus")).toBe("workflow:workflow-1");
      expect(searchParams.get("prompt")).toBe("Continue this workflow in Studio");
    });
  });

  it("tries to restore auth first and then loads Studio when the host session recovers", async () => {
    (studioApi.getAuthSession as jest.Mock)
      .mockResolvedValueOnce({
        enabled: true,
        authenticated: false,
        providerDisplayName: "NyxID",
      })
      .mockResolvedValue({
        enabled: true,
        authenticated: true,
        providerDisplayName: "NyxID",
      });
    mockEnsureActiveAuthSession.mockResolvedValue({
      tokens: {
        accessToken: "token",
        tokenType: "Bearer",
        expiresIn: 3600,
        expiresAt: Date.now() + 3600_000,
      },
      user: {
        sub: "user-1",
      },
    });

    renderStudioPage("/studio?tab=studio");

    await waitFor(() => {
      expect(mockEnsureActiveAuthSession).toHaveBeenCalledTimes(1);
      expect(studioApi.getAppContext).toHaveBeenCalled();
    });

    await waitFor(() => {
      expect(window.location.pathname).toBe("/studio");
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("tab")).toBe("studio");
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("focus")).toBe("workflow:workflow-1");
      expect(searchParams.get("execution")).toBeNull();
    });
  });

  it("redirects to login when Studio auth stays unauthenticated after refresh", async () => {
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      enabled: true,
      authenticated: false,
      providerDisplayName: "NyxID",
    });

    renderStudioPage("/studio?tab=studio&focus=workflow%3Aworkflow-1");

    await waitFor(() => {
      expect(mockEnsureActiveAuthSession).toHaveBeenCalledTimes(1);
      expect(window.location.pathname).toBe("/login");
    });

    expect(new URLSearchParams(window.location.search).get("redirect")).toBe(
      "/studio?step=build&focus=workflow%3Aworkflow-1&tab=studio"
    );
  });

  it("does not auto-redirect again after a previous Studio relogin attempt", async () => {
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      enabled: true,
      authenticated: false,
      providerDisplayName: "NyxID",
    });
    window.sessionStorage.setItem(
      `${STUDIO_AUTO_RELOGIN_ATTEMPT_KEY}/studio`,
      "1"
    );

    renderStudioPage("/studio");

    await waitFor(() => {
      expect(studioApi.getAuthSession).toHaveBeenCalled();
    });

    expect(mockEnsureActiveAuthSession).not.toHaveBeenCalled();
    expect(window.location.pathname).toBe("/studio");
  });

  it("keeps the script build surface active when its leave guard blocks a lifecycle switch", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
      scriptStorageMode: "scope",
      features: {
        ...defaultStudioAppContext.features,
        scripts: true,
      },
    });

    renderStudioPage("/studio?tab=scripts");

    await screen.findByLabelText("Script ID");
    fireEvent.change(screen.getByLabelText("Script source editor"), {
      target: {
        value: "using System;\n// dirty",
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "Bind" }));

    await waitFor(() => {
      expect(screen.getByTestId("studio-script-build-panel")).toBeTruthy();
      expect(screen.queryByTestId("studio-bind-surface")).toBeNull();
    });
  });

  it("saves edited workflow drafts back to the Studio workspace API", async () => {
    renderStudioPage("/studio?focus=workflow%3Aworkflow-1&tab=studio");

    const editor = await screen.findByLabelText("定义 YAML");
    await waitFor(() => {
      expect(editor).toHaveValue(mockWorkflowFile.yaml);
    });
    fireEvent.change(editor, {
      target: {
        value: "name: workspace-demo\nsteps:\n  - id: approve_step\n",
      },
    });
    await waitFor(() => {
      expect(editor).toHaveValue("name: workspace-demo\nsteps:\n  - id: approve_step\n");
    });

    const saveButton = screen.getByRole("button", { name: "Save draft" });
    await waitFor(() => {
      expect(saveButton).toBeEnabled();
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowId: "workflow-1",
          directoryId: "dir-1",
          workflowName: "workspace-demo",
          yaml: "name: workspace-demo\nsteps:\n  - id: approve_step\n",
        })
      );
    });

    await waitFor(() => {
      expect(message.success).toHaveBeenCalledWith(
        "已保存到 Workspace/workspace-demo.yaml。",
      );
    });
  });

  it("creates a workflow draft from the create-member inventory flow", async () => {
    renderStudioPage(
      "/studio?scopeId=scope-1&memberId=workspace-demo&focus=workflow%3Aworkflow-1&tab=studio",
    );

    const createButton = await screen.findByLabelText("Create member");
    await waitFor(() => {
      expect(createButton).not.toBeDisabled();
    });
    fireEvent.click(createButton);

    const createDialog = await screen.findByRole("dialog", { name: "Create member" });

    expect(
      within(createDialog).getByRole("button", { name: "Create Workflow member" }),
    ).toHaveAttribute("aria-pressed", "true");

    const nameInput = within(createDialog).getByLabelText("Member name");
    expect(nameInput).toHaveValue("draft");
    fireEvent.change(nameInput, {
      target: {
        value: "orders-draft",
      },
    });
    fireEvent.click(within(createDialog).getByRole("button", { name: "Create member" }));

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowName: "orders-draft",
          fileName: "orders-draft.yaml",
          yaml: "name: orders-draft\nsteps: []\n",
        })
      );
    });

    await waitFor(() => {
      expect(studioApi.createMember).toHaveBeenCalledWith({
        scopeId: "scope-1",
        displayName: "orders-draft",
        implementationKind: "workflow",
        memberId: "orders-draft",
      });
    });

    await waitFor(() => {
      expect(message.success).toHaveBeenCalledWith(
        "Created member orders-draft and opened its workflow draft.",
      );
    });
  });

  it("opens the create-member modal once from the typed Studio intent", async () => {
    renderStudioPage("/studio?tab=studio&intent=create-member");

    const createDialog = await screen.findByRole("dialog", { name: "Create member" });
    expect(within(createDialog).getByLabelText("Member name")).toHaveValue("draft");
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();

    fireEvent.click(within(createDialog).getByRole("button", { name: "Cancel" }));

    await waitFor(() => {
      expect(screen.queryByRole("dialog", { name: "Create member" })).toBeNull();
    });

    await waitFor(() => {
      expect(screen.queryByRole("dialog", { name: "Create member" })).toBeNull();
    });
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
  });

  it("shows script and gagent as member kinds before their create APIs land", async () => {
    renderStudioPage("/studio?focus=workflow%3Aworkflow-1&tab=studio");

    fireEvent.click(await screen.findByLabelText("Create member"));
    const createDialog = await screen.findByRole("dialog", { name: "Create member" });

    const scriptChip = within(createDialog).getByRole("button", {
      name: "Create Script member",
    });
    fireEvent.click(scriptChip);

    expect(scriptChip).toHaveAttribute("aria-pressed", "true");
    expect(
      screen.getByText(
        "Script member authority exists on backend, but this modal still hands off through Build > Script for implementation editing.",
      ),
    ).toBeTruthy();
    expect(within(createDialog).getByRole("button", { name: "Create member" })).toBeDisabled();

    const gagentChip = within(createDialog).getByRole("button", {
      name: "Create GAgent member",
    });
    fireEvent.click(gagentChip);

    expect(gagentChip).toHaveAttribute("aria-pressed", "true");
    expect(
      screen.getByText(
        "GAgent member authority exists on backend, but this modal still hands off through Build > GAgent for implementation editing.",
      ),
    ).toBeTruthy();
  });

  it("renames a workflow member from the inventory actions", async () => {
    jest.spyOn(window, "prompt").mockReturnValue("orders-router");

    renderStudioPage("/studio?focus=workflow%3Aworkflow-1&tab=studio");

    fireEvent.click(await screen.findByLabelText("Rename workspace-demo"));

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowId: "workflow-1",
          workflowName: "orders-router",
          fileName: "orders-router.yaml",
        })
      );
    });

    await waitFor(() => {
      expect(message.success).toHaveBeenCalledWith(
        "Renamed workflow member to orders-router.",
      );
    });
  });

  it("deletes a workflow member from the inventory rail", async () => {
    renderStudioPage("/studio?focus=workflow%3Aworkflow-1&tab=studio");

    fireEvent.click(await screen.findByLabelText("Delete workspace-demo"));

    await waitFor(() => {
      expect(Modal.confirm).toHaveBeenCalledWith(
        expect.objectContaining({
          title: "Delete workflow member",
          okText: "Delete member",
          cancelText: "Keep member",
          autoFocusButton: "cancel",
        })
      );
    });

    const confirmConfig = (Modal.confirm as jest.Mock).mock.calls[0]?.[0];
    expect(confirmConfig.icon).toBeTruthy();
    await act(async () => {
      await confirmConfig.onOk();
    });

    await waitFor(() => {
      expect(studioApi.deleteWorkflow).toHaveBeenCalledWith(
        "workflow-1",
        undefined,
      );
    });

    await waitFor(() => {
      expect(message.success).toHaveBeenCalledWith(
        "Deleted workflow member workspace-demo.",
      );
    });
  });

  it("saves the workflow draft and continues to bind from the build page", async () => {
    renderStudioPage("/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio");

    expect(await screen.findByTestId("studio-workflow-build-panel")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByLabelText("Step ID")).toHaveValue("draft_step");
    });

    fireEvent.click(screen.getByRole("button", { name: "Add step" }));

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalled();
    });
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "llm_call" })).toBeTruthy();
    });

    fireEvent.click(screen.getByRole("button", { name: "Save draft" }));

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: "scope-1",
          yaml: expect.stringContaining("llm_call"),
        }),
      );
    });

    const continueToBindButton = screen.getByRole("button", {
      name: "Continue to Bind",
    });
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("memberId")).toBe("workspace-demo");
      expect(searchParams.get("focus")).toBe("workflow:workflow-1");
      expect(continueToBindButton).toBeEnabled();
    });
    fireEvent.click(continueToBindButton);

    const bindSurface = await screen.findByTestId("studio-bind-surface");
    expect(bindSurface).toBeTruthy();
    expect(bindSurface.parentElement).not.toHaveStyle({
      height: "100%",
    });
    expect(bindSurface.parentElement).not.toHaveStyle({
      overflow: "hidden",
    });
  });

  it("applies workflow step changes without requiring a manual graph selection first", async () => {
    renderStudioPage("/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio");

    expect(await screen.findByTestId("studio-workflow-build-panel")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByLabelText("Step ID")).toHaveValue("draft_step");
    });

    (studioApi.serializeYaml as jest.Mock).mockClear();

    const stepIdInput = screen.getByLabelText("Step ID");
    fireEvent.change(stepIdInput, {
      target: { value: "draft_step_updated" },
    });
    fireEvent.input(stepIdInput, {
      target: { value: "draft_step_updated" },
    });
    expect(stepIdInput).toHaveValue("draft_step_updated");
    fireEvent.click(screen.getByRole("button", { name: "Apply changes" }));

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalled();
      const serializedDocument = (studioApi.serializeYaml as jest.Mock).mock.calls.at(-1)?.[0]
        ?.document;
      expect(serializedDocument?.steps?.[0]?.id).toBe("draft_step_updated");
    });

    expect(
      screen.queryByText("Select a workflow step before applying changes."),
    ).toBeNull();
  });

  it("carries the selected bind contract into invoke after continuing from build", async () => {
    renderStudioPage(
      "/studio?scopeId=scope-1&memberId=workspace-demo&focus=workflow%3Aworkflow-1&step=bind&tab=bindings",
    );
    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();

    const continueToInvokeButton = screen.getByRole("button", {
      name: "Continue to Invoke",
    });
    await waitFor(() => {
      expect(continueToInvokeButton).toBeEnabled();
    });
    fireEvent.click(continueToInvokeButton);

    expect(await screen.findByTestId("studio-invoke-surface")).toBeTruthy();
    expect(screen.getByText("service:default")).toBeTruthy();
    expect(screen.getByText("services:default")).toBeTruthy();
    expect(screen.getByText("endpoint:support-chat")).toBeTruthy();
  });

  it("pins Invoke to the selected member instead of exposing every runtime service", async () => {
    mockServicesApi.listServices.mockResolvedValueOnce([
      {
        serviceId: "default",
        displayName: "workspace-demo",
        deploymentStatus: "Active",
        primaryActorId: "actor-default",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with workspace-demo.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
      {
        serviceId: "billing-api",
        displayName: "Billing API",
        deploymentStatus: "Active",
        primaryActorId: "actor-billing",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with billing.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
    ]);

    renderStudioPage("/studio?scopeId=scope-1&memberId=default&step=invoke&tab=invoke");

    expect(await screen.findByTestId("studio-invoke-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText("service:default")).toBeTruthy();
      expect(screen.getByText("member:workspace-demo")).toBeTruthy();
      expect(screen.getByText("services:default")).toBeTruthy();
    });
    expect(screen.queryByText("services:default,billing-api")).toBeNull();
  });

  it("surfaces the current workflow as a bind candidate before any published service exists", async () => {
    mockServicesApi.listServices
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([
        {
          serviceId: "default",
          displayName: "workspace-demo",
          deploymentStatus: "Active",
          primaryActorId: "actor-default",
          endpoints: [
            {
              endpointId: "chat",
              displayName: "Chat",
              kind: "chat",
              description: "Chat with the published workflow.",
              requestTypeUrl: "",
              responseTypeUrl: "",
            },
          ],
        },
      ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);

    renderStudioPage(
      "/studio?scopeId=scope-1&memberId=workspace-demo&focus=workflow%3Aworkflow-1&step=bind&tab=bindings",
    );

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText("candidate:workspace-demo")).toBeTruthy();
    });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Bind current member" }));
    });

    await waitFor(() => {
      expect(studioApi.bindMemberWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: "scope-1",
          memberId: "workspace-demo",
          displayName: "workspace-demo",
          workflowYamls: expect.arrayContaining([expect.stringContaining("name: workspace-demo")]),
        }),
      );
    });
    expect(studioApi.bindScopeWorkflow).not.toHaveBeenCalled();
    await waitFor(() => {
      expect(screen.getByText("service:default")).toBeTruthy();
      expect(screen.getByText("services:default")).toBeTruthy();
      expect(screen.getByText("candidate:workspace-demo")).toBeTruthy();
    });
    expect(screen.queryByText("service:no-service")).toBeNull();
    expect(screen.queryByText("services:none")).toBeNull();

    const rail = await screen.findByLabelText("Team members");
    await waitFor(() => {
      expect(
        within(rail).getAllByRole("button", { name: "workspace-demo" })
      ).toHaveLength(1);
    });
  });

  it("keeps a successful bind on the member route even before the service catalog catches up", async () => {
    mockStudioMembers = [
      {
        memberId: "joker",
        scopeId: "scope-1",
        displayName: "joker",
        description: "Workflow member",
        implementationKind: "workflow",
        lifecycleStage: "created",
        publishedServiceId: "member-joker",
        lastBoundRevisionId: "",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
    ];
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "default",
        name: "joker",
        description: "Current workflow draft",
        fileName: "joker.yaml",
        filePath: "/tmp/workflows/joker.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);
    mockServicesApi.listServices.mockResolvedValueOnce([]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);
    (studioApi.bindMemberWorkflow as jest.Mock).mockImplementationOnce(
      async (input: {
        scopeId: string;
        memberId: string;
        displayName?: string;
        workflowYamls: string[];
      }) => {
        mockStudioMembers = mockStudioMembers.map((member) =>
          member.memberId === input.memberId
            ? {
                ...member,
                lifecycleStage: "bind_ready",
                lastBoundRevisionId: "rev-joker",
                updatedAt: "2026-04-27T08:15:00Z",
              }
            : member,
        );

        return {
          scopeId: input.scopeId,
          serviceId: "",
          displayName: input.displayName || "joker",
          targetKind: "workflow",
          targetName: input.displayName || "joker",
          revisionId: "rev-joker",
          workflowName: input.displayName || "joker",
          definitionActorIdPrefix: "scope-workflow:scope-1:member-joker",
          expectedActorId: "scope-workflow:scope-1:member-joker:dep-1",
        };
      },
    );

    renderStudioPage(
      "/studio?scopeId=scope-1&memberId=joker&focus=workflow%3Ajoker&step=bind&tab=bindings",
    );

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText("service:no-service")).toBeTruthy();
      expect(screen.getByText("services:none")).toBeTruthy();
      expect(screen.getByText("candidate:joker")).toBeTruthy();
    });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Bind current member" }));
    });

    await waitFor(() => {
      expect(studioApi.bindScopeWorkflow).not.toHaveBeenCalled();
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("memberId")).toBe("joker");
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("step")).toBe("bind");
      expect(searchParams.get("focus")).toBeNull();
    });
  });

  it("binds a workflow-backed member from the current member authority even when the bind route still carries a workflow id", async () => {
    mockStudioMembers = [
      {
        memberId: "joker",
        scopeId: "scope-1",
        displayName: "joker",
        description: "Workflow member",
        implementationKind: "workflow",
        lifecycleStage: "created",
        publishedServiceId: "member-joker",
        lastBoundRevisionId: "",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
    ];
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "workflow-1",
        name: "joker",
        description: "Current workflow draft",
        fileName: "joker.yaml",
        filePath: "/tmp/workflows/joker.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);
    mockServicesApi.listServices.mockResolvedValueOnce([]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);
    (studioApi.bindMemberWorkflow as jest.Mock).mockImplementationOnce(
      async (input: {
        scopeId: string;
        memberId: string;
        displayName?: string;
        workflowYamls: string[];
      }) => ({
        scopeId: input.scopeId,
        serviceId: "",
        displayName: input.displayName || "joker",
        targetKind: "workflow",
        targetName: input.displayName || "joker",
        revisionId: "rev-joker",
        workflowName: input.displayName || "joker",
        definitionActorIdPrefix: "scope-workflow:scope-1:member-joker",
        expectedActorId: "scope-workflow:scope-1:member-joker:dep-1",
      }),
    );

    renderStudioPage(
      "/studio?scopeId=scope-1&memberId=joker&focus=workflow%3Aworkflow-1&step=bind&tab=bindings",
    );

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText("service:no-service")).toBeTruthy();
      expect(screen.getByText("services:none")).toBeTruthy();
      expect(screen.getByText("candidate:joker")).toBeTruthy();
    });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Bind current member" }));
    });

    await waitFor(() => {
      expect(studioApi.bindMemberWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: "scope-1",
          memberId: "joker",
          displayName: "joker",
        }),
      );
    });
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("memberId")).toBe("joker");
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("step")).toBe("bind");
      expect(searchParams.get("focus")).toBeNull();
    });
  });

  it("canonicalizes a workflow-backed bind refresh back to the real member route", async () => {
    mockStudioMembers = [
      {
        memberId: "joker",
        scopeId: "scope-1",
        displayName: "joker",
        description: "Workflow member",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "member-joker",
        lastBoundRevisionId: "rev-joker",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:15:00Z",
      },
    ];
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "default",
        name: "joker",
        description: "Current workflow draft",
        fileName: "joker.yaml",
        filePath: "/tmp/workflows/joker.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);
    mockServicesApi.listServices.mockResolvedValueOnce([]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);

    renderStudioPage("/studio?scopeId=scope-1&member=workflow%3Ajoker&step=bind&tab=bindings");

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("memberId")).toBe("joker");
      expect(searchParams.get("member")).toBeNull();
      expect(screen.getByText("service:member-joker")).toBeTruthy();
      expect(screen.getByText("services:member-joker")).toBeTruthy();
      expect(screen.getByText("candidate:none")).toBeTruthy();
    });
  });

  it("binds from the selected member authority on a member-first bind route", async () => {
    mockStudioMembers = [
      {
        memberId: "joker",
        scopeId: "scope-1",
        displayName: "joker",
        description: "Workflow member",
        implementationKind: "workflow",
        lifecycleStage: "created",
        publishedServiceId: "member-joker",
        lastBoundRevisionId: "",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
    ];
    mockWorkflowFile = {
      ...mockWorkflowFile,
      workflowId: "workflow-1",
      name: "joker",
      fileName: "joker.yaml",
      filePath: "/tmp/workflows/joker.yaml",
      yaml: "name: joker\nsteps: []\n",
      document: {
        ...mockParsedDocument,
        name: "joker",
      },
    };
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "workflow-1",
        name: "joker",
        description: "Current workflow member",
        fileName: "joker.yaml",
        filePath: "/tmp/workflows/joker.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);
    mockServicesApi.listServices.mockResolvedValueOnce([]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);

    renderStudioPage(
      "/studio?scopeId=scope-1&member=member%3Ajoker&step=bind&focus=workflow%3Aworkflow-1",
    );

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId("studio-context-title")).toHaveTextContent("joker");
      expect(screen.getByText("service:no-service")).toBeTruthy();
      expect(screen.getByText("candidate:joker")).toBeTruthy();
    });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Bind current member" }));
    });

    await waitFor(() => {
      expect(studioApi.bindMemberWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: "scope-1",
          memberId: "joker",
          displayName: "joker",
        }),
      );
    });
    expect(studioApi.bindScopeWorkflow).not.toHaveBeenCalled();
  });

  it("binds from a selected member route without requiring a workflow-shaped focus token", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
      workflowStorageMode: "scope",
    });
    mockStudioMembers = [
      {
        memberId: "joker",
        scopeId: "scope-1",
        displayName: "joker",
        description: "Workflow-backed member",
        implementationKind: "workflow",
        lifecycleStage: "build_ready",
        publishedServiceId: "member-joker",
        lastBoundRevisionId: null,
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
    ];
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "workflow-1",
        name: "joker",
        description: "Joker workflow",
        fileName: "joker.yaml",
        filePath: "/tmp/workflows/joker.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);
    (studioApi.getMember as jest.Mock).mockImplementationOnce(
      async (_scopeId: string, memberId: string) => ({
        summary: mockStudioMembers.find((member) => member.memberId === memberId),
        implementationRef: {
          implementationKind: "workflow",
          workflowId: "workflow-1",
          workflowRevision: null,
        },
        lastBinding: null,
      }),
    );
    mockWorkflowFile = {
      ...mockWorkflowFile,
      workflowId: "workflow-1",
      name: "joker",
      fileName: "joker.yaml",
      filePath: "/tmp/workflows/joker.yaml",
      yaml: "name: joker\nsteps: []\n",
      document: {
        ...mockParsedDocument,
        name: "joker",
      },
    };

    renderStudioPage("/studio?scopeId=scope-1&member=member%3Ajoker&step=bind");

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText("candidate:joker")).toBeTruthy();
      expect(screen.getByText("service:no-service")).toBeTruthy();
    });
    fireEvent.click(
      await screen.findByRole("button", { name: "Bind current member" }),
    );

    await waitFor(() => {
      expect(studioApi.bindMemberWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: "scope-1",
          memberId: "joker",
          displayName: "joker",
        }),
      );
    });
    expect(studioApi.bindScopeWorkflow).not.toHaveBeenCalled();
  });

  it("normalizes legacy workflow:default links and keeps the bound member contract when switching away and back", async () => {
    mockStudioMembers = [
      {
        memberId: "draft2",
        scopeId: "scope-1",
        displayName: "draft2",
        description: "Current draft member",
        implementationKind: "workflow",
        lifecycleStage: "created",
        publishedServiceId: "default",
        lastBoundRevisionId: "",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
      {
        memberId: "draft1",
        scopeId: "scope-1",
        displayName: "draft1",
        description: "Another draft member",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "draft1",
        lastBoundRevisionId: "rev-draft1",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
    ];
    mockWorkflowFile = {
      ...mockWorkflowFile,
      workflowId: "workflow-1",
      name: "draft2",
      fileName: "draft2.yaml",
      filePath: "/tmp/workflows/draft2.yaml",
      yaml: "name: draft2\nsteps: []\n",
      document: {
        ...mockParsedDocument,
        name: "draft2",
      },
    };
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "workflow-1",
        name: "draft2",
        description: "Current draft member",
        fileName: "draft2.yaml",
        filePath: "/tmp/workflows/draft2.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
      {
        workflowId: "workflow-2",
        name: "draft1",
        description: "Another draft member",
        fileName: "draft1.yaml",
        filePath: "/tmp/workflows/draft1.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:10:00Z",
      },
    ]);
    mockServicesApi.listServices
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([
        {
          serviceId: "default",
          displayName: "draft2",
          deploymentStatus: "Active",
          primaryActorId: "actor-default",
          endpoints: [
            {
              endpointId: "chat",
              displayName: "Chat",
              kind: "chat",
              description: "Chat with the published workflow.",
              requestTypeUrl: "",
              responseTypeUrl: "",
            },
          ],
        },
      ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async () =>
        mockBuildServiceRevisionCatalog({
          serviceId: "default",
          displayName: "draft2",
          workflowName: "draft2",
        })
    );

    renderStudioPage("/studio?scopeId=scope-1&step=build&focus=workflow%3Adefault&tab=studio");

    expect(await screen.findByTestId("studio-workflow-build-panel")).toBeTruthy();

    const continueToBindButton = screen.getByRole("button", {
      name: "Continue to Bind",
    });
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("memberId")).toBe("draft2");
      expect(searchParams.get("focus")).toBe("workflow:workflow-1");
      expect(continueToBindButton).toBeEnabled();
    });
    fireEvent.click(continueToBindButton);
    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText("candidate:draft2")).toBeTruthy();
    });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Bind current member" }));
    });

    await waitFor(() => {
      expect(screen.getByText("service:default")).toBeTruthy();
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("memberId")).toBe("draft2");
      expect(searchParams.get("focus")).toBeNull();
      expect(searchParams.get("step")).toBe("bind");
    });

    const rail = await screen.findByLabelText("Team members");
    fireEvent.click(within(rail).getByRole("button", { name: "draft1" }));
    fireEvent.click(within(rail).getByRole("button", { name: "draft2" }));

    await waitFor(() => {
      expect(screen.getByText("service:default")).toBeTruthy();
      expect(screen.queryByText("service:no-service")).toBeNull();
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("memberId")).toBe("draft2");
    });
  });

  it("keeps Bind pinned to the member that was just bound instead of the scope default binding target", async () => {
    mockStudioMembers = [
      {
        memberId: "draft1",
        scopeId: "scope-1",
        displayName: "draft1",
        description: "Current draft member",
        implementationKind: "workflow",
        lifecycleStage: "created",
        publishedServiceId: "draft1",
        lastBoundRevisionId: "",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
      {
        memberId: "draft2",
        scopeId: "scope-1",
        displayName: "draft2",
        description: "Default route target member",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "draft2",
        lastBoundRevisionId: "rev-draft2",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
    ];
    mockWorkflowFile = {
      ...mockWorkflowFile,
      name: "draft1",
      fileName: "draft1.yaml",
      filePath: "/tmp/workflows/draft1.yaml",
      yaml: "name: draft1\nsteps: []\n",
      document: {
        ...mockParsedDocument,
        name: "draft1",
      },
    };
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "workflow-1",
        name: "draft1",
        description: "Current draft member",
        fileName: "draft1.yaml",
        filePath: "/tmp/workflows/draft1.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
      {
        workflowId: "workflow-2",
        name: "draft2",
        description: "Another draft member",
        fileName: "draft2.yaml",
        filePath: "/tmp/workflows/draft2.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:10:00Z",
      },
    ]);
    mockServicesApi.listServices
      .mockResolvedValueOnce([
        {
          serviceId: "draft2",
          displayName: "draft2",
          deploymentStatus: "Active",
          primaryActorId: "actor-draft2",
          endpoints: [
            {
              endpointId: "chat",
              displayName: "Chat",
              kind: "chat",
              description: "Chat with draft2.",
              requestTypeUrl: "",
              responseTypeUrl: "",
            },
          ],
        },
      ])
      .mockResolvedValueOnce([
        {
          serviceId: "draft2",
          displayName: "draft2",
          deploymentStatus: "Active",
          primaryActorId: "actor-draft2",
          endpoints: [
            {
              endpointId: "chat",
              displayName: "Chat",
              kind: "chat",
              description: "Chat with draft2.",
              requestTypeUrl: "",
              responseTypeUrl: "",
            },
          ],
        },
        {
          serviceId: "draft1",
          displayName: "draft1",
          deploymentStatus: "Active",
          primaryActorId: "actor-draft1",
          endpoints: [
            {
              endpointId: "chat",
              displayName: "Chat",
              kind: "chat",
              description: "Chat with draft1.",
              requestTypeUrl: "",
              responseTypeUrl: "",
            },
          ],
        },
      ]);
    (studioApi.bindMemberWorkflow as jest.Mock).mockImplementationOnce(
      async (input: {
        scopeId: string;
        memberId: string;
        displayName?: string;
        workflowYamls: string[];
      }) => ({
        scopeId: input.scopeId,
        serviceId: "draft1",
        displayName: input.displayName || "draft1",
        targetKind: "workflow",
        targetName: input.displayName || "draft1",
        revisionId: "rev-draft1",
        workflowName: input.displayName || "draft1",
        definitionActorIdPrefix: "scope-workflow:scope-1:draft1",
        expectedActorId: "scope-workflow:scope-1:draft1:dep-1",
      })
    );
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValue({
      available: true,
      scopeId: "scope-1",
      serviceId: "draft2",
      displayName: "draft2",
      serviceKey: "scope-1:default:draft2",
      defaultServingRevisionId: "rev-draft2",
      activeServingRevisionId: "rev-draft2",
      deploymentId: "dep-draft2",
      deploymentStatus: "Active",
      primaryActorId: "actor-draft2",
      updatedAt: "2026-03-26T08:00:00Z",
      revisions: [],
    });
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        mockBuildServiceRevisionCatalog({
          serviceId,
          displayName: serviceId,
          workflowName: serviceId,
          revisionId: serviceId === "draft1" ? "rev-draft1" : "rev-draft2",
        })
    );

    renderStudioPage("/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&step=bind&tab=bindings");

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText("candidate:draft1")).toBeTruthy();
      expect(screen.getByText("service:no-service")).toBeTruthy();
    });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Bind current member" }));
    });

    await waitFor(() => {
      expect(studioApi.bindMemberWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: "scope-1",
          memberId: "draft1",
          displayName: "draft1",
        })
      );
    });
    await waitFor(() => {
      expect(screen.getByTestId("studio-context-title")).toHaveTextContent("draft1");
      expect(screen.getByText("service:draft1")).toBeTruthy();
      expect(screen.getByText("services:draft1")).toBeTruthy();
      expect(screen.getByText("candidate:draft1")).toBeTruthy();
    });
    expect(screen.queryByText("service:draft2")).toBeNull();
  });

  it("keeps Bind pinned to the selected member after leaving a workflow build surface", async () => {
    mockStudioMembers = [
      {
        memberId: "joker",
        scopeId: "scope-1",
        displayName: "joker",
        description: "Published workflow member",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "joker",
        lastBoundRevisionId: "rev-joker",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
    ];
    mockWorkflowFile = {
      ...mockWorkflowFile,
      name: "draft1",
      fileName: "draft1.yaml",
      filePath: "/tmp/workflows/draft1.yaml",
      yaml: "name: draft1\nsteps: []\n",
      document: {
        ...mockParsedDocument,
        name: "draft1",
      },
    };
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "workflow-1",
        name: "draft1",
        description: "Current draft member",
        fileName: "draft1.yaml",
        filePath: "/tmp/workflows/draft1.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);
    mockServicesApi.listServices.mockResolvedValueOnce([
      {
        serviceId: "joker",
        displayName: "joker",
        deploymentStatus: "Active",
        primaryActorId: "actor-joker",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with joker.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
    ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce({
      available: true,
      scopeId: "scope-1",
      serviceId: "joker",
      displayName: "joker",
      serviceKey: "scope-1:default:joker",
      defaultServingRevisionId: "rev-joker",
      activeServingRevisionId: "rev-joker",
      deploymentId: "dep-joker",
      deploymentStatus: "Active",
      primaryActorId: "actor-joker",
      updatedAt: "2026-03-26T08:00:00Z",
      revisions: [],
    });
    mockScopeRuntimeApi.getServiceRevisions.mockImplementationOnce(
      async () =>
        mockBuildServiceRevisionCatalog({
          serviceId: "joker",
          displayName: "joker",
          workflowName: "joker",
        })
    );

    renderStudioPage(
      "/studio?scopeId=scope-1&memberId=joker&focus=workflow%3Aworkflow-1&tab=studio"
    );

    expect(await screen.findByTestId("studio-workflow-build-panel")).toBeTruthy();

    const bindButton = screen.getByRole("button", { name: "Bind" });
    await waitFor(() => {
      expect(bindButton).toBeEnabled();
    });
    fireEvent.click(bindButton);

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    const rail = await screen.findByLabelText("Team members");
    await waitFor(() => {
      expect(screen.getByTestId("studio-context-title")).toHaveTextContent("joker");
      expect(screen.getByText("service:joker")).toBeTruthy();
      expect(screen.getByText("services:joker")).toBeTruthy();
      expect(screen.getByText("candidate:joker")).toBeTruthy();
      expect(within(rail).getByRole("button", { name: "joker" })).toHaveAttribute(
        "aria-current",
        "true"
      );
    });
    within(rail)
      .getAllByRole("button", { name: "draft1" })
      .forEach((button) => {
        expect(button).not.toHaveAttribute("aria-current", "true");
      });
  });

  it("pins Bind to the selected published member instead of the scope default route target", async () => {
    mockStudioMembers = [
      {
        memberId: "joker",
        scopeId: "scope-1",
        displayName: "joker",
        description: "Published workflow member",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "joker",
        lastBoundRevisionId: "rev-joker",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
    ];
    mockServicesApi.listServices.mockResolvedValueOnce([
      {
        serviceId: "default",
        displayName: "workspace-demo",
        deploymentStatus: "Active",
        primaryActorId: "actor-default",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with workspace-demo.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
      {
        serviceId: "joker",
        displayName: "joker",
        deploymentStatus: "Active",
        primaryActorId: "actor-joker",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with joker.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
    ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce({
      available: true,
      scopeId: "scope-1",
      serviceId: "default",
      displayName: "workspace-demo",
      serviceKey: "scope-1:default:workspace-demo",
      defaultServingRevisionId: "rev-2",
      activeServingRevisionId: "rev-2",
      deploymentId: "dep-2",
      deploymentStatus: "Active",
      primaryActorId: "actor-default",
      updatedAt: "2026-03-26T08:00:00Z",
      revisions: [],
    });
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        mockBuildServiceRevisionCatalog({
          serviceId,
          displayName: serviceId === "joker" ? "joker" : "workspace-demo",
          workflowName: serviceId === "joker" ? "joker" : "workspace-demo",
        })
    );

    renderStudioPage("/studio?scopeId=scope-1&memberId=joker&step=bind&tab=bindings");

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId("studio-context-title")).toHaveTextContent("joker");
      expect(screen.getByText("service:joker")).toBeTruthy();
      expect(screen.getByText("services:joker")).toBeTruthy();
    });
    expect(screen.queryByText("services:default,joker")).toBeNull();
    expect(screen.getByText("candidate:none")).toBeTruthy();
  });

  it("resolves Bind to the published member contract when a workflow focus already maps to that member", async () => {
    mockStudioMembers = [
      {
        memberId: "joker",
        scopeId: "scope-1",
        displayName: "joker",
        description: "Published workflow member",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "joker",
        lastBoundRevisionId: "rev-2",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
      ...mockStudioMembers,
    ];
    mockWorkflowFile = {
      ...mockWorkflowFile,
      name: "joker",
      fileName: "joker.yaml",
      filePath: "/tmp/workflows/joker.yaml",
      yaml: "name: joker\nsteps: []\n",
      document: {
        ...mockParsedDocument,
        name: "joker",
      },
    };
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "workflow-1",
        name: "joker",
        description: "Current joker draft",
        fileName: "joker.yaml",
        filePath: "/tmp/workflows/joker.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);
    mockServicesApi.listServices.mockResolvedValueOnce([
      {
        serviceId: "default",
        displayName: "workspace-demo",
        deploymentStatus: "Active",
        primaryActorId: "actor-default",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with workspace-demo.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
      {
        serviceId: "joker",
        displayName: "joker",
        deploymentStatus: "Active",
        primaryActorId: "actor-joker",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with joker.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
    ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce({
      available: true,
      scopeId: "scope-1",
      serviceId: "default",
      displayName: "workspace-demo",
      serviceKey: "scope-1:default:workspace-demo",
      defaultServingRevisionId: "rev-default",
      activeServingRevisionId: "rev-default",
      deploymentId: "dep-default",
      deploymentStatus: "Active",
      primaryActorId: "actor-default",
      updatedAt: "2026-03-26T08:00:00Z",
      revisions: [],
    });
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        mockBuildServiceRevisionCatalog({
          serviceId,
          displayName: serviceId === "joker" ? "joker" : "workspace-demo",
          workflowName: serviceId === "joker" ? "joker" : "workspace-demo",
        })
    );

    renderStudioPage("/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&step=bind&tab=bindings");

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId("studio-context-title")).toHaveTextContent("joker");
      expect(screen.getByText("service:joker")).toBeTruthy();
      expect(screen.getByText("services:joker")).toBeTruthy();
      expect(screen.getByText("candidate:joker")).toBeTruthy();
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("step")).toBe("bind");
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("memberId")).toBe("joker");
      expect(searchParams.get("focus")).toBeNull();
    });
    expect(screen.queryByText("service:default")).toBeNull();
    expect(screen.queryByText("service:no-service")).toBeNull();
  });

  it("keeps the current bind surface active when switching members from the rail", async () => {
    mockServicesApi.listServices.mockResolvedValueOnce([
      {
        serviceId: "default",
        displayName: "workspace-demo",
        deploymentStatus: "Active",
        primaryActorId: "actor-default",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with workspace-demo.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
      {
        serviceId: "joker",
        displayName: "joker",
        deploymentStatus: "Active",
        primaryActorId: "actor-joker",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with joker.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
    ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce({
      available: true,
      scopeId: "scope-1",
      serviceId: "default",
      displayName: "workspace-demo",
      serviceKey: "scope-1:default:workspace-demo",
      defaultServingRevisionId: "rev-2",
      activeServingRevisionId: "rev-2",
      deploymentId: "dep-2",
      deploymentStatus: "Active",
      primaryActorId: "actor-default",
      updatedAt: "2026-03-26T08:00:00Z",
      revisions: [],
    });
    mockScopeRuntimeApi.getServiceRevisions.mockImplementation(
      async (_scopeId: string, serviceId: string) =>
        mockBuildServiceRevisionCatalog({
          serviceId,
          displayName: serviceId === "joker" ? "joker" : "workspace-demo",
          workflowName: serviceId === "joker" ? "joker" : "workspace-demo",
        })
    );

    renderStudioPage("/studio?scopeId=scope-1&memberId=default&step=bind&tab=bindings");

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText("service:default")).toBeTruthy();
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("memberId")).toBe("workspace-demo");
    });

    const rail = await screen.findByLabelText("Team members");
    fireEvent.click(within(rail).getByRole("button", { name: "joker" }));

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId("studio-context-title")).toHaveTextContent("joker");
      expect(screen.getByText("service:joker")).toBeTruthy();
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("step")).toBe("bind");
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("memberId")).toBe("joker");
    });
    expect(screen.queryByTestId("studio-invoke-surface")).toBeNull();
  });

  it("canonicalizes a legacy default-service bind route back to the real member id", async () => {
    mockStudioMembers = [
      {
        memberId: "draft2",
        scopeId: "scope-1",
        displayName: "draft2",
        description: "Published workflow member",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "member-draft2",
        lastBoundRevisionId: "rev-draft2",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
      {
        memberId: "draft1",
        scopeId: "scope-1",
        displayName: "draft1",
        description: "Another workflow member",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "member-draft1",
        lastBoundRevisionId: "",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
      {
        memberId: "joker",
        scopeId: "scope-1",
        displayName: "joker",
        description: "Third workflow member",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "member-joker",
        lastBoundRevisionId: "",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
    ];
    mockServicesApi.listServices.mockResolvedValueOnce([
      {
        serviceId: "default",
        displayName: "draft2",
        deploymentStatus: "Active",
        primaryActorId: "actor-draft2",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with draft2.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
    ]);
    mockScopeRuntimeApi.getServiceRevisions.mockImplementationOnce(
      async () =>
        mockBuildServiceRevisionCatalog({
          serviceId: "default",
          displayName: "draft2",
          workflowName: "draft2",
          revisionId: "rev-draft2",
        })
    );

    renderStudioPage("/studio?scopeId=scope-1&member=member%3Adefault&step=bind");

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId("studio-context-title")).toHaveTextContent("draft2");
      expect(screen.getByText("service:default")).toBeTruthy();
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("memberId")).toBe("draft2");
    });
  });

  it("keeps the current bind surface active when switching to a workflow draft from the rail", async () => {
    mockStudioMembers = [
      {
        memberId: "joker",
        scopeId: "scope-1",
        displayName: "joker",
        description: "Published workflow member",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "joker",
        lastBoundRevisionId: "rev-2",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
      ...mockStudioMembers,
    ];
    mockWorkflowFile = {
      ...mockWorkflowFile,
      name: "draft1",
      fileName: "draft1.yaml",
      filePath: "/tmp/workflows/draft1.yaml",
      yaml: "name: draft1\nsteps: []\n",
      document: {
        ...mockParsedDocument,
        name: "draft1",
      },
    };
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "workflow-1",
        name: "draft1",
        description: "Current draft member",
        fileName: "draft1.yaml",
        filePath: "/tmp/workflows/draft1.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);
    mockServicesApi.listServices.mockResolvedValueOnce([
      {
        serviceId: "joker",
        displayName: "joker",
        deploymentStatus: "Active",
        primaryActorId: "actor-joker",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with joker.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
    ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce({
      available: true,
      scopeId: "scope-1",
      serviceId: "joker",
      displayName: "joker",
      serviceKey: "scope-1:default:joker",
      defaultServingRevisionId: "rev-joker",
      activeServingRevisionId: "rev-joker",
      deploymentId: "dep-joker",
      deploymentStatus: "Active",
      primaryActorId: "actor-joker",
      updatedAt: "2026-03-26T08:00:00Z",
      revisions: [],
    });
    mockScopeRuntimeApi.getServiceRevisions.mockImplementationOnce(
      async () =>
        mockBuildServiceRevisionCatalog({
          serviceId: "joker",
          displayName: "joker",
          workflowName: "joker",
        })
    );

    renderStudioPage("/studio?scopeId=scope-1&memberId=joker&step=bind&tab=bindings");

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText("service:joker")).toBeTruthy();
    });

    const rail = await screen.findByLabelText("Team members");
    fireEvent.click(within(rail).getByRole("button", { name: "draft1" }));

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId("studio-context-title")).toHaveTextContent("joker");
      expect(screen.getByText("service:joker")).toBeTruthy();
      expect(screen.getByText("services:joker")).toBeTruthy();
      expect(screen.getByText("candidate:joker")).toBeTruthy();
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("step")).toBe("bind");
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("focus")).toBeNull();
      expect(searchParams.get("memberId")).toBe("joker");
    });
    expect(message.error).toHaveBeenCalledWith(
      "Studio could not resolve a stable member authority for the current draft. Re-open the member from Team members, or create/register a backend member before continuing to Bind.",
    );
    expect(screen.queryByTestId("studio-workflow-build-panel")).toBeNull();
  });

  it("does not resurrect a deleted workflow step when another node is selected afterwards", async () => {
    renderStudioPage("/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio");

    expect(await screen.findByTestId("studio-workflow-build-panel")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "approve_step" })).toBeTruthy();
    });

    fireEvent.click(screen.getByRole("button", { name: "approve_step" }));
    await waitFor(() => {
      expect(screen.getByLabelText("Step ID")).toHaveValue("approve_step");
    });

    fireEvent.click(screen.getByRole("button", { name: "Delete step" }));

    await waitFor(() => {
      expect(screen.queryByRole("button", { name: "approve_step" })).toBeNull();
      expect(screen.getByLabelText("Step ID")).toHaveValue("draft_step");
    });

    fireEvent.click(screen.getByRole("button", { name: "draft_step" }));

    await waitFor(() => {
      expect(screen.getByLabelText("Step ID")).toHaveValue("draft_step");
      expect(screen.queryByRole("button", { name: "approve_step" })).toBeNull();
    });
  });

  it("does not resurrect a canvas-deleted workflow step after adding another node", async () => {
    renderStudioPage("/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio");

    expect(await screen.findByTestId("studio-workflow-build-panel")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "approve_step" })).toBeTruthy();
    });

    fireEvent.click(screen.getByRole("button", { name: "approve_step" }));
    await waitFor(() => {
      expect(screen.getByLabelText("Step ID")).toHaveValue("approve_step");
    });

    fireEvent.click(screen.getByRole("button", { name: "Delete selected step on canvas" }));

    await waitFor(() => {
      expect(screen.queryByRole("button", { name: "approve_step" })).toBeNull();
      expect(screen.getByLabelText("Step ID")).toHaveValue("draft_step");
    });

    fireEvent.click(screen.getByRole("button", { name: "Add step" }));

    await waitFor(() => {
      expect(screen.queryByRole("button", { name: "approve_step" })).toBeNull();
      expect(screen.getByRole("button", { name: "llm_call" })).toBeTruthy();
    });
  });

  it("does not re-persist removed create-team draft params after saving", async () => {
    renderStudioPage(
      "/studio?focus=workflow%3Aworkflow-1&tab=studio&teamMode=create&teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3"
    );

    const editor = await screen.findByLabelText("定义 YAML");
    fireEvent.change(editor, {
      target: {
        value: "name: workspace-demo\nsteps:\n  - id: approve_step\n",
      },
    });

    const saveButton = screen.getByRole("button", { name: "Save draft" });
    await waitFor(() => {
      expect(saveButton).toBeEnabled();
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(window.location.pathname).toBe("/studio");
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("focus")).toBe("workflow:workflow-1");
      expect(searchParams.get("teamMode")).toBeNull();
      expect(searchParams.get("teamName")).toBeNull();
      expect(searchParams.get("entryName")).toBeNull();
      expect(searchParams.get("teamDraftWorkflowId")).toBeNull();
      expect(searchParams.get("teamDraftWorkflowName")).toBeNull();
    });
  });

  it("drops removed create-team params when the route switches to a different workflow", async () => {
    renderStudioPage(
      "/studio?focus=workflow%3Aworkflow-1&tab=studio&teamMode=create&teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3&teamDraftWorkflowId=workflow-1&teamDraftWorkflowName=workspace-demo"
    );

    expect(await screen.findByText("DAG Canvas")).toBeTruthy();

    await replaceStudioRoute(
      "/studio?focus=workflow%3Aworkflow-2&tab=studio&teamMode=create&teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3&teamDraftWorkflowId=workflow-1&teamDraftWorkflowName=workspace-demo"
    );

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("focus")).toBe("workflow:workflow-2");
      expect(searchParams.get("teamMode")).toBeNull();
      expect(searchParams.get("teamName")).toBeNull();
      expect(searchParams.get("entryName")).toBeNull();
      expect(searchParams.get("teamDraftWorkflowId")).toBeNull();
      expect(searchParams.get("teamDraftWorkflowName")).toBeNull();
    });
  });

  it("keeps Studio workflow saves pinned to the current scope route", async () => {
    renderStudioPage("/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio");

    const editor = await screen.findByLabelText("定义 YAML");
    fireEvent.change(editor, {
      target: {
        value: "name: workspace-demo\nsteps:\n  - id: approve_step\n",
      },
    });

    const saveButton = screen.getByRole("button", { name: "Save draft" });
    await waitFor(() => {
      expect(saveButton).toBeEnabled();
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowId: "workflow-1",
          scopeId: "scope-1",
          directoryId: "dir-1",
          workflowName: "workspace-demo",
        })
      );
    });
  });

  it("marks the first scoped save of a committed workflow as create-draft work", async () => {
    (studioApi.getWorkflow as jest.Mock).mockResolvedValueOnce({
      ...mockWorkflowFile,
      draftExists: false,
    });

    renderStudioPage("/studio?scopeId=scope-1&workflow=workflow-1&tab=studio");

    const editor = await screen.findByLabelText("定义 YAML");
    fireEvent.change(editor, {
      target: {
        value: "name: workspace-demo\nsteps:\n  - id: approve_step\n",
      },
    });

    const saveButton = screen.getByRole("button", { name: "Save draft" });
    await waitFor(() => {
      expect(saveButton).toBeEnabled();
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowId: "workflow-1",
          draftExists: false,
          scopeId: "scope-1",
          directoryId: "dir-1",
          workflowName: "workspace-demo",
        })
      );
    });
  });

  it("keeps the toolbar save action enabled when the draft falls back to the default directory", async () => {
    (studioApi.getWorkflow as jest.Mock).mockResolvedValueOnce({
      ...mockWorkflowFile,
      directoryId: "",
      directoryLabel: "",
    });

    renderStudioPage("/studio?workflow=workflow-1&tab=studio");

    const editor = await screen.findByLabelText("定义 YAML");
    fireEvent.change(editor, {
      target: {
        value: "name: workspace-demo\nsteps:\n  - id: approve_step\n",
      },
    });

    const toolbarSaveButton = screen.getByRole("button", { name: "Save draft" });
    await waitFor(() => {
      expect(toolbarSaveButton).toBeEnabled();
    });
    fireEvent.click(toolbarSaveButton);

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowId: "workflow-1",
          directoryId: "dir-1",
          workflowName: "workspace-demo",
        })
      );
    });
  });

  it("falls back to the existing workflow when the removed draft route flag is present", async () => {
    renderStudioPage("/studio?draft=new");

    expect(await screen.findByText("DAG Canvas")).toBeTruthy();
    const rail = await screen.findByLabelText("Team members");
    expect(within(rail).queryByRole("button", { name: "draft" })).toBeNull();

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("draft")).toBeNull();
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("focus")).toBe("workflow:workflow-1");
    });
  });

  it("does not auto-create a draft when Studio opens without any team members", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
      workflowStorageMode: "scope",
    });
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);

    renderStudioPage("/studio?scopeId=scope-1&tab=studio");

    expect(await screen.findByTestId("studio-empty-member-state")).toBeTruthy();
    expect(screen.getByTestId("studio-context-title")).toHaveTextContent(
      "Select a member"
    );
    expect(screen.getByLabelText("Create member from empty state")).toBeTruthy();
    expect(screen.queryByText("DAG Canvas")).toBeNull();

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("draft")).toBeNull();
      expect(searchParams.get("focus")).toBeNull();
      expect(searchParams.get("tab")).toBe("studio");
    });
  });

  it("recovers to an explicit member-selection empty state when the route points at a missing workflow", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
      workflowStorageMode: "scope",
    });
    (studioApi.listWorkflows as jest.Mock).mockResolvedValue([]);
    (studioApi.getWorkflow as jest.Mock).mockRejectedValueOnce(
      new Error("Not Found")
    );
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);

    renderStudioPage("/studio?scopeId=scope-1&focus=workflow%3Adraft&tab=studio");

    await waitFor(() => {
      expect(studioApi.getWorkflow).toHaveBeenCalledWith("draft", "scope-1");
    });

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("draft")).toBeNull();
      expect(searchParams.get("focus")).toBeNull();
      expect(searchParams.get("tab")).toBe("studio");
    });

    expect(await screen.findByTestId("studio-empty-member-state")).toBeTruthy();
    expect(screen.getByTestId("studio-context-title")).toHaveTextContent(
      "Select a member"
    );
    expect(screen.queryByText("DAG Canvas")).toBeNull();
  });

  it("ignores the legacy playground handoff route flag and opens the existing workflow workspace", async () => {
    renderStudioPage("/studio?draft=new&legacy=playground");

    expect(await screen.findByText("DAG Canvas")).toBeTruthy();
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("legacy")).toBeNull();
      expect(searchParams.get("draft")).toBeNull();
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("focus")).toBe("workflow:workflow-1");
    });
  });

  it("hydrates the workflow dry-run prompt from the route query", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
    });
    renderStudioPage(
      "/studio?focus=template%3Apublished-demo&prompt=Continue%20this%20workflow%20in%20Studio"
    );

    await waitFor(() => {
      expect(studioApi.getTemplateWorkflow).toHaveBeenCalledWith(
        "published-demo"
      );
    });

    expect(
      (await screen.findByLabelText("Workflow dry run input")) as HTMLTextAreaElement
    ).toHaveValue("Continue this workflow in Studio");
  });

  it("shows the published template graph in the Studio editor", async () => {
    renderStudioPage("/studio?focus=template%3Apublished-demo&tab=workflows");

    await waitFor(() => {
      expect(studioApi.getTemplateWorkflow).toHaveBeenCalledWith(
        "published-demo"
      );
    });

    expect(await screen.findByText("DAG Canvas")).toBeTruthy();
    expect(screen.getByTestId("studio-context-title")).toHaveTextContent(
      "published-demo"
    );
    await waitFor(() => {
      expect(screen.getByTestId("workflow-graph-node-count")).toHaveTextContent("2");
    });
  });

  it("prefers the active scope binding workflow when Studio opens in a team context", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
    });
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "workflow-2",
        name: "other-workflow",
        description: "Other workflow",
        fileName: "other-workflow.yaml",
        filePath: "/tmp/workflows/other-workflow.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
      {
        workflowId: "workflow-1",
        name: "workspace-demo",
        description: "Workspace workflow",
        fileName: "workspace-demo.yaml",
        filePath: "/tmp/workflows/workspace-demo.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 2,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);

    renderStudioPage("/studio?scopeId=scope-1&tab=studio");

    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("member")).toBeNull();
      expect(searchParams.get("memberId")).toBe("workspace-demo");
      expect(searchParams.get("focus")).toBe("workflow:workflow-1");
      expect(searchParams.get("tab")).toBe("studio");
    });
  });

  it("keeps team members in recent-first order when selecting from the rail", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
    });
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "workflow-2",
        name: "other-workflow",
        description: "Other workflow",
        fileName: "other-workflow.yaml",
        filePath: "/tmp/workflows/other-workflow.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
      {
        workflowId: "workflow-1",
        name: "workspace-demo",
        description: "Workspace workflow",
        fileName: "workspace-demo.yaml",
        filePath: "/tmp/workflows/workspace-demo.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 2,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);

    renderStudioPage("/studio?scopeId=scope-1&tab=studio");

    const rail = await screen.findByLabelText("Team members");
    await within(rail).findByText("other-workflow");
    const workspaceButtonsBefore = within(rail).getAllByRole("button", {
      name: "workspace-demo",
    });
    const workspaceButtonBefore = workspaceButtonsBefore[0];
    const otherWorkflowButtonBefore = within(rail).getByRole("button", {
      name: "other-workflow",
    });

    expect(workspaceButtonBefore).toBeTruthy();
    expect(workspaceButtonsBefore).toHaveLength(1);
    expect(otherWorkflowButtonBefore).toBeTruthy();
    expect(
      workspaceButtonBefore!.compareDocumentPosition(otherWorkflowButtonBefore!) &
        Node.DOCUMENT_POSITION_FOLLOWING
    ).toBeTruthy();

    fireEvent.click(otherWorkflowButtonBefore);

    await waitFor(() => {
      expect(
        within(rail).getAllByRole("button", { name: "workspace-demo" })
      ).toHaveLength(1);
      expect(within(rail).getByRole("button", { name: "other-workflow" })).toBeTruthy();
    });

    const workspaceButtonsAfter = within(rail).getAllByRole("button", {
      name: "workspace-demo",
    });
    const workspaceButtonAfter = workspaceButtonsAfter[0];
    const otherWorkflowButtonAfter = within(rail).getByRole("button", {
      name: "other-workflow",
    });
    const railButtonsAfter = within(rail).getAllByRole("button");

    expect(otherWorkflowButtonAfter).toBeTruthy();
    expect(workspaceButtonAfter).toBeTruthy();
    expect(workspaceButtonsAfter).toHaveLength(1);
    const searchParams = new URLSearchParams(window.location.search);
    expect(searchParams.get("focus")).toContain("workflow:");
  });

  it("highlights the newly selected workflow instead of keeping the previous service selected", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
    });
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "workflow-2",
        name: "other-workflow",
        description: "Other workflow",
        fileName: "other-workflow.yaml",
        filePath: "/tmp/workflows/other-workflow.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
      {
        workflowId: "workflow-1",
        name: "workspace-demo",
        description: "Workspace workflow",
        fileName: "workspace-demo.yaml",
        filePath: "/tmp/workflows/workspace-demo.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 2,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);

    renderStudioPage(
      "/studio?scopeId=scope-1&memberId=workspace-demo&focus=workflow%3Aworkflow-1&tab=studio"
    );

    const rail = await screen.findByLabelText("Team members");
    const workspaceButton = await within(rail).findByRole("button", {
      name: "workspace-demo",
    });
    const otherWorkflowButton = within(rail).getByRole("button", {
      name: "other-workflow",
    });

    fireEvent.click(otherWorkflowButton);

    await waitFor(() => {
      expect(otherWorkflowButton).toHaveAttribute("aria-current", "true");
      expect(workspaceButton).not.toHaveAttribute("aria-current", "true");
    });
  });

  it("surfaces the currently selected workflow member ahead of older published members", async () => {
    mockStudioMembers = [
      {
        memberId: "joker",
        scopeId: "scope-1",
        displayName: "joker",
        description: "Published workflow member",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "joker",
        lastBoundRevisionId: "rev-2",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
      ...mockStudioMembers,
    ];
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
    });
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "workflow-1",
        name: "draft1",
        description: "Current draft member",
        fileName: "draft1.yaml",
        filePath: "/tmp/workflows/draft1.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);
    mockServicesApi.listServices.mockResolvedValueOnce([
      {
        serviceId: "joker",
        displayName: "joker",
        deploymentStatus: "Idle",
        primaryActorId: "actor-joker",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with joker.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
    ]);
    mockScopeRuntimeApi.getServiceRevisions.mockImplementationOnce(
      async () =>
        mockBuildServiceRevisionCatalog({
          serviceId: "joker",
          displayName: "joker",
          workflowName: "joker",
          deploymentStatus: "Idle",
        })
    );

    renderStudioPage("/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio");

    const rail = await screen.findByLabelText("Team members");
    const draftButton = await within(rail).findByRole("button", {
      name: "draft1",
    });
    const jokerButton = within(rail).getByRole("button", {
      name: "joker",
    });

    expect(
      draftButton.compareDocumentPosition(jokerButton) &
        Node.DOCUMENT_POSITION_FOLLOWING
    ).toBeTruthy();
  });

  it("does not render a duplicate member card when a legacy default service aliases the selected draft", async () => {
    mockStudioMembers = [
      {
        memberId: "draft2",
        scopeId: "scope-1",
        displayName: "draft2",
        description: "Selected workflow member",
        implementationKind: "workflow",
        lifecycleStage: "created",
        publishedServiceId: "member-draft2",
        lastBoundRevisionId: "",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
      {
        memberId: "draft1",
        scopeId: "scope-1",
        displayName: "draft1",
        description: "Sibling workflow member",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "member-draft1",
        lastBoundRevisionId: "",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
      {
        memberId: "joker",
        scopeId: "scope-1",
        displayName: "joker",
        description: "Third workflow member",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "member-joker",
        lastBoundRevisionId: "",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
    ];
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
    });
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "draft2",
        name: "draft2",
        description: "Selected workflow draft",
        fileName: "draft2.yaml",
        filePath: "/tmp/workflows/draft2.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);
    mockServicesApi.listServices.mockResolvedValueOnce([
      {
        serviceId: "default",
        displayName: "draft2",
        deploymentStatus: "Idle",
        primaryActorId: "actor-draft2",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with draft2.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
    ]);
    mockScopeRuntimeApi.getServiceRevisions.mockImplementationOnce(
      async () =>
        mockBuildServiceRevisionCatalog({
          serviceId: "default",
          displayName: "draft2",
          workflowName: "draft2",
          deploymentStatus: "Idle",
          revisionId: "rev-draft2",
        })
    );

    renderStudioPage(
      "/studio?scopeId=scope-1&member=workflow%3Adraft2&step=build&tab=studio"
    );

    const rail = await screen.findByLabelText("Team members");
    expect(await within(rail).findByRole("button", { name: "draft2" })).toBeTruthy();
    expect(within(rail).getAllByRole("button", { name: "draft2" })).toHaveLength(1);
    expect(within(rail).getByRole("button", { name: "draft1" })).toBeTruthy();
    expect(within(rail).getByRole("button", { name: "joker" })).toBeTruthy();
  });

  it("keeps a workflow draft visible when an orphan default service aliases it", async () => {
    mockStudioMembers = [
      {
        memberId: "joker",
        scopeId: "scope-1",
        displayName: "joker",
        description: "Published workflow member",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "member-joker",
        lastBoundRevisionId: "rev-joker",
        createdAt: "2026-04-27T08:00:00Z",
        updatedAt: "2026-04-27T08:05:00Z",
      },
    ];
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
    });
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        workflowId: "draft1",
        name: "draft1",
        description: "Selected workflow draft",
        fileName: "draft1.yaml",
        filePath: "/tmp/workflows/draft1.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
      {
        workflowId: "draft2",
        name: "draft2",
        description: "Second workflow draft",
        fileName: "draft2.yaml",
        filePath: "/tmp/workflows/draft2.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]);
    mockServicesApi.listServices.mockResolvedValueOnce([
      {
        serviceId: "default",
        displayName: "draft2",
        deploymentStatus: "Idle",
        primaryActorId: "actor-draft2",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with draft2.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
      {
        serviceId: "member-joker",
        displayName: "joker",
        deploymentStatus: "Idle",
        primaryActorId: "actor-joker",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with joker.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
    ]);
    mockScopeRuntimeApi.getServiceRevisions
      .mockImplementationOnce(async () =>
        mockBuildServiceRevisionCatalog({
          serviceId: "default",
          displayName: "draft2",
          workflowName: "draft2",
          deploymentStatus: "Idle",
          revisionId: "rev-draft2",
        }),
      )
      .mockImplementationOnce(async () =>
        mockBuildServiceRevisionCatalog({
          serviceId: "member-joker",
          displayName: "joker",
          workflowName: "joker",
          deploymentStatus: "Idle",
          revisionId: "rev-joker",
        }),
      );

    renderStudioPage(
      "/studio?scopeId=scope-1&member=workflow%3Adraft1&step=build&tab=studio",
    );

    const rail = await screen.findByLabelText("Team members");
    expect(await within(rail).findByRole("button", { name: "draft1" })).toBeTruthy();
    expect(within(rail).getByRole("button", { name: "draft2" })).toBeTruthy();
    expect(within(rail).getAllByRole("button", { name: "draft2" })).toHaveLength(1);
    expect(within(rail).getByRole("button", { name: "joker" })).toBeTruthy();
  });

  it("does not hide a workflow draft when the matching service revision cannot be loaded", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
    });
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);
    mockServicesApi.listServices.mockResolvedValueOnce([
      {
        serviceId: "default",
        displayName: "workspace-demo",
        deploymentStatus: "Active",
        primaryActorId: "actor-default",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with workspace-demo.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
    ]);
    mockScopeRuntimeApi.getServiceRevisions.mockRejectedValueOnce(
      new Error("revision service unavailable"),
    );

    renderStudioPage("/studio?scopeId=scope-1&tab=studio");

    const rail = await screen.findByLabelText("Team members");
    await waitFor(() => {
      expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenCalledWith(
        "scope-1",
        "default",
      );
      expect(
        within(rail).getAllByRole("button", { name: "workspace-demo" }),
      ).toHaveLength(1);
    });
  });

  it("fetches published revisions for each service on initial Studio rail render", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
    });
    mockServicesApi.listServices.mockResolvedValueOnce([
      {
        serviceId: "default",
        displayName: "workspace-demo",
        deploymentStatus: "Active",
        primaryActorId: "actor-default",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with workspace-demo.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
      {
        serviceId: "billing-api",
        displayName: "Billing API",
        deploymentStatus: "Active",
        primaryActorId: "actor-billing",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            description: "Chat with billing.",
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        ],
      },
    ]);

    renderStudioPage("/studio?scopeId=scope-1&tab=studio");

    const rail = await screen.findByLabelText("Team members");
    expect(await within(rail).findByRole("button", { name: "Billing API" })).toBeTruthy();

    await waitFor(() => {
      expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenCalledWith(
        "scope-1",
        "default"
      );
      expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenCalledWith(
        "scope-1",
        "billing-api"
      );
    });
  });

  it("does not truncate the team member rail when more than eight members are available", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
    });
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce(
      Array.from({ length: 9 }, (_, index) => ({
        workflowId: `workflow-${index + 1}`,
        name: `member-${index + 1}`,
        description: `Workflow ${index + 1}`,
        fileName: `member-${index + 1}.yaml`,
        filePath: `/tmp/workflows/member-${index + 1}.yaml`,
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: index + 1,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      }))
    );

    renderStudioPage("/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio");

    const rail = await screen.findByLabelText("Team members");
    expect(await within(rail).findByRole("button", { name: "member-1" })).toBeTruthy();
    expect(within(rail).getByRole("button", { name: "member-8" })).toBeTruthy();
    expect(within(rail).getByRole("button", { name: "member-9" })).toBeTruthy();
  });

  it("opens the scripts workspace when the route only carries a script id", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      features: {
        ...defaultStudioAppContext.features,
        scripts: true,
      },
      scopeId: "scope-1",
      scopeResolved: true,
    });

    renderStudioPage("/studio?focus=script%3Ascript-alpha");

    expect(await screen.findByLabelText("Script ID")).toBeTruthy();
    expect(screen.getByTestId("studio-script-build-panel")).toBeTruthy();
    expect(screen.getByText("Script source")).toBeTruthy();
  });

  it("loads discovered GAgent types and the published service revision catalog", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
    });
    renderStudioPage("/studio?focus=workflow%3Aworkflow-1&tab=studio");

    await waitFor(() => {
      expect(mockRuntimeGAgentApi.listTypes).toHaveBeenCalled();
    });
    await waitFor(() => {
      expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenCalledWith(
        "scope-1",
        "default"
      );
    });
  });

  it("stops the selected member run from the observe view", async () => {
    renderStudioPage(
      "/studio?scopeId=scope-1&memberId=default&step=observe&tab=executions&execution=execution-1"
    );

    expect(await screen.findByText("Logs")).toBeTruthy();
    expect(await screen.findByText("observe-selected:execution-1")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Stop" }));

    await waitFor(() => {
      expect(mockRuntimeRunsApi.stop).toHaveBeenCalledWith(
        "scope-1",
        {
          actorId: "actor-1",
          runId: "execution-1",
          reason: "user requested stop",
        },
        {
          memberId: "workspace-demo",
          serviceId: "default",
        }
      );
    });

    expect(await screen.findByText("Execution stop requested")).toBeTruthy();
  });

  it("falls back to the build editor when a removed roles tab still carries a workflow draft", async () => {
    renderStudioPage("/studio?focus=workflow%3Aworkflow-1&tab=roles");

    expect(await screen.findByText("DAG Canvas")).toBeTruthy();
    expect(screen.queryByText("Saved roles")).toBeNull();
  });

  it("switches the Studio lifecycle stepper into the bind surface", async () => {
    renderStudioPage("/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio");

    const bindButton = await screen.findByRole("button", { name: "Bind" });
    await waitFor(() => {
      expect(bindButton).toBeEnabled();
    });
    fireEvent.click(bindButton);

    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();
  });

  it("shows published members in the left rail even when the workflow inventory is empty", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      ...defaultStudioAppContext,
      scopeId: "scope-1",
      scopeResolved: true,
    });
    (studioApi.listWorkflows as jest.Mock).mockResolvedValueOnce([]);

    renderStudioPage("/studio?scopeId=scope-1&tab=studio");

    const rail = await screen.findByLabelText("Team members");
    expect(await within(rail).findByRole("button", { name: "workspace-demo" })).toBeTruthy();
    expect(screen.queryByText("No team members yet. Create a member to start building in Studio.")).toBeNull();
  });

  it("keeps the Team members rail focused on member inventory instead of implementation filters", async () => {
    renderStudioPage("/studio?scopeId=scope-1&tab=studio");

    const rail = await screen.findByLabelText("Team members");
    expect(await within(rail).findByRole("button", { name: "workspace-demo" })).toBeTruthy();
    expect(within(rail).getByRole("button", { name: "All" })).toBeTruthy();
    expect(within(rail).getByRole("button", { name: "Member" })).toBeTruthy();
    expect(within(rail).queryByRole("button", { name: "Workflow" })).toBeNull();
    expect(within(rail).queryByRole("button", { name: "Script" })).toBeNull();
    expect(within(rail).queryByRole("button", { name: "GAgent" })).toBeNull();
  });

  it("keeps Invoke available once the selected member already has a published endpoint", async () => {
    renderStudioPage(
      "/studio?scopeId=scope-1&memberId=default&focus=workflow%3Aworkflow-1&tab=studio"
    );

    const invokeButton = await screen.findByRole("button", { name: "Invoke" });
    await waitFor(() => {
      expect(invokeButton).toBeEnabled();
    });

    fireEvent.click(invokeButton);

    expect(await screen.findByTestId("studio-invoke-surface")).toBeTruthy();
    expect(screen.getByText("service:default")).toBeTruthy();
  });

  it("shows a clear invoke fallback when no selected member is available", async () => {
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);

    renderStudioPage("/studio?scopeId=scope-1&step=invoke&tab=invoke");

    expect(await screen.findByTestId("studio-invoke-surface")).toBeTruthy();
    expect(screen.getByText("service:no-service")).toBeTruthy();
    expect(screen.getByText("services:none")).toBeTruthy();
    expect(screen.getByText("empty:请选择要调用的成员。")).toBeTruthy();
  });

  it("opens the Studio invoke surface from the bind surface endpoint action", async () => {
    renderStudioPage("/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio");

    const bindButton = await screen.findByRole("button", { name: "Bind" });
    await waitFor(() => {
      expect(bindButton).toBeEnabled();
    });
    fireEvent.click(bindButton);
    const continueToInvokeButton = await screen.findByRole("button", {
      name: "Continue to Invoke",
    });
    await waitFor(() => {
      expect(continueToInvokeButton).toBeEnabled();
    });
    fireEvent.click(continueToInvokeButton);

    expect(await screen.findByTestId("studio-invoke-surface")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText("service:default")).toBeTruthy();
      expect(screen.getByText("endpoint:support-chat")).toBeTruthy();
    });
  });

  it("pins Observe to the selected member service and corrects stale run selection", async () => {
    mockScopeRuntimeApi.listMemberRuns.mockResolvedValueOnce({
      scopeId: "scope-1",
      serviceId: "default",
      serviceKey: "scope-1:default:default:default",
      displayName: "workspace-demo",
      runs: [
        mockBuildServiceRunSummary({
          runId: "execution-1",
          actorId: "actor-1",
          workflowName: "workspace-demo",
        }),
      ],
    });

    renderStudioPage(
      "/studio?scopeId=scope-1&memberId=default&step=observe&tab=executions&execution=execution-2"
    );

    expect(await screen.findByText("Logs")).toBeTruthy();

    await waitFor(() => {
      expect(mockScopeRuntimeApi.listMemberRuns).toHaveBeenCalledWith(
        "scope-1",
        "workspace-demo",
        {
          take: 12,
        }
      );
      expect(screen.getByText("observe-member:workspace-demo")).toBeTruthy();
      expect(screen.getByText("observe-runs:execution-1")).toBeTruthy();
      expect(screen.getByText("observe-selected:execution-1")).toBeTruthy();
    });

    expect(screen.queryByText("observe-runs:execution-1,execution-2")).toBeNull();
    expect(screen.queryByText("observe-selected:execution-2")).toBeNull();
  });

  it("keeps Observe populated with the latest invoke session while runtime runs warm up", async () => {
    mockScopeRuntimeApi.listMemberRuns.mockResolvedValue({
      scopeId: "scope-1",
      serviceId: "default",
      serviceKey: "scope-1:default:default:default",
      displayName: "workspace-demo",
      runs: [],
    });

    renderStudioPage("/studio?scopeId=scope-1&memberId=default&step=invoke&tab=invoke");

    expect(await screen.findByTestId("studio-invoke-surface")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Emit Observe Session" }));
    fireEvent.click(screen.getByRole("button", { name: "Observe" }));

    expect(await screen.findByText("Logs")).toBeTruthy();

    await waitFor(() => {
      expect(screen.getByText("observe-runs:invoke-run-1")).toBeTruthy();
      expect(screen.getByText("observe-selected:invoke-run-1")).toBeTruthy();
      expect(screen.queryByText("observe-empty:No runs for workspace-demo yet.")).toBeNull();
    });
  });

  it("rehydrates Observe from the persisted invoke session after refresh", async () => {
    const now = Date.now();
    mockScopeRuntimeApi.listMemberRuns.mockResolvedValue({
      scopeId: "scope-1",
      serviceId: "default",
      serviceKey: "scope-1:default:default:default",
      displayName: "workspace-demo",
      runs: [],
    });

    saveStudioObserveSessionSeed({
      scopeId: "scope-1",
      session: {
        actorId: "actor-invoke",
        assistantText: "Observed output",
        commandId: "command-invoke",
        completedAtUtc: new Date(now).toISOString(),
        endpointId: "chat",
        error: "",
        events: [
          {
            name: "aevatar.run.context",
            timestamp: now - 1000,
            type: "CUSTOM",
            value: {
              actorId: "actor-invoke",
              commandId: "command-invoke",
            },
          },
          {
            result: "Observed output",
            runId: "invoke-run-2",
            timestamp: now,
            threadId: "actor-invoke",
            type: "RUN_FINISHED",
          },
        ],
        finalOutput: "Observed output",
        mode: "stream",
        payloadBase64: "",
        payloadTypeUrl: "",
        prompt: "Observe after refresh.",
        runId: "invoke-run-2",
        serviceId: "default",
        serviceLabel: "workspace-demo",
        startedAtUtc: new Date(now - 1000).toISOString(),
        status: "success",
      },
    });

    renderStudioPage("/studio?scopeId=scope-1&memberId=default&step=observe&tab=executions");

    expect(await screen.findByText("Logs")).toBeTruthy();

    await waitFor(() => {
      expect(screen.getByText("observe-runs:invoke-run-2")).toBeTruthy();
      expect(screen.getByText("observe-selected:invoke-run-2")).toBeTruthy();
      expect(screen.queryByText("observe-empty:No runs for workspace-demo yet.")).toBeNull();
    });
  });

  it("shows a clear observe fallback when no selected member is available", async () => {
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);

    renderStudioPage("/studio?scopeId=scope-1&step=observe&tab=executions");

    expect(await screen.findByText("Logs")).toBeTruthy();
    expect(screen.getByText("observe-runs:none")).toBeTruthy();
    expect(screen.getByText("observe-empty:Select a member to observe.")).toBeTruthy();
  });

  it("walks the lifecycle flow from build to bind to invoke to observe", async () => {
    renderStudioPage("/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio");

    expect(await screen.findByTestId("studio-workflow-build-panel")).toBeTruthy();

    const continueToBindButton = screen.getByRole("button", {
      name: "Continue to Bind",
    });
    await waitFor(() => {
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("memberId")).toBe("workspace-demo");
      expect(searchParams.get("focus")).toBe("workflow:workflow-1");
      expect(continueToBindButton).toBeEnabled();
    });
    fireEvent.click(continueToBindButton);
    expect(await screen.findByTestId("studio-bind-surface")).toBeTruthy();

    const continueToInvokeButton = screen.getByRole("button", {
      name: "Continue to Invoke",
    });
    await waitFor(() => {
      expect(continueToInvokeButton).toBeEnabled();
    });
    fireEvent.click(continueToInvokeButton);
    expect(await screen.findByTestId("studio-invoke-surface")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Observe" }));
    expect(await screen.findByText("Logs")).toBeTruthy();
  });
});
