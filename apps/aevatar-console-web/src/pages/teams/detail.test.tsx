import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { setLocale } from "@umijs/max";
import { message } from "antd";
import React from "react";
import { scopesApi } from "@/shared/api/scopesApi";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { runtimeActorsApi } from "@/shared/api/runtimeActorsApi";
import { runtimeGAgentApi } from "@/shared/api/runtimeGAgentApi";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { studioApi } from "@/shared/studio/api";
import {
  createTestQueryClient,
  renderWithQueryClient,
} from "../../../tests/reactQueryTestUtils";
import TeamDetailPage from "./detail";

async function openTeamTestDialog() {
  fireEvent.click(await screen.findByRole("button", { name: "测试团队" }));
  await screen.findByLabelText("测试 Prompt");
  return screen.getByTestId("team-test-modal-body");
}

jest.mock("@/shared/graphs/GraphCanvas", () => ({
  __esModule: true,
  default: () => {
    const React = require("react");
    return React.createElement("div", null, "Graph canvas");
  },
}));

jest.mock("antd", () => {
  const actual = jest.requireActual("antd");
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
  };
});

function mockCreateRunsCatalog() {
  return {
    scopeId: "scope-1",
    serviceId: "default",
    serviceKey: "scope-1:default",
    displayName: "Support Runtime",
    runs: [
      {
        scopeId: "scope-1",
        serviceId: "default",
        runId: "run-current",
        actorId: "actor-intake",
        definitionActorId: "definition://support-triage",
        revisionId: "rev-2",
        deploymentId: "dep-2",
        workflowName: "support-triage",
        completionStatus: "waiting_approval",
        stateVersion: 2,
        lastEventId: "evt-2",
        lastUpdatedAt: "2026-04-09T09:05:00Z",
        boundAt: "2026-04-09T09:00:00Z",
        bindingUpdatedAt: "2026-04-09T09:00:00Z",
        lastSuccess: false,
        totalSteps: 4,
        completedSteps: 2,
        roleReplyCount: 1,
        lastOutput: "",
        lastError: "Waiting on approval",
      },
      {
        scopeId: "scope-1",
        serviceId: "default",
        runId: "run-good",
        actorId: "actor-intake-v1",
        definitionActorId: "definition://support-triage-v1",
        revisionId: "rev-1",
        deploymentId: "dep-1",
        workflowName: "support-triage-v1",
        completionStatus: "completed",
        stateVersion: 1,
        lastEventId: "evt-1",
        lastUpdatedAt: "2026-04-09T08:55:00Z",
        boundAt: "2026-04-09T08:50:00Z",
        bindingUpdatedAt: "2026-04-09T08:50:00Z",
        lastSuccess: true,
        totalSteps: 3,
        completedSteps: 3,
        roleReplyCount: 1,
        lastOutput: "Resolved",
        lastError: "",
      },
    ],
  };
}

function mockCreateServiceRevisionCatalog(overrides?: Record<string, any>) {
  return {
    scopeId: "scope-1",
    serviceId: "default",
    serviceKey: "scope-1:default",
    displayName: "Support Escalation Triage",
    defaultServingRevisionId: "rev-2",
    activeServingRevisionId: "rev-2",
    deploymentId: "dep-2",
    deploymentStatus: "Active",
    primaryActorId: "actor-intake",
    catalogStateVersion: 2,
    catalogLastEventId: "evt-catalog-2",
    updatedAt: "2026-04-09T09:00:00Z",
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
        primaryActorId: "actor-intake",
        createdAt: "2026-04-09T08:00:00Z",
        preparedAt: "2026-04-09T08:01:00Z",
        publishedAt: "2026-04-09T08:02:00Z",
        retiredAt: null,
        workflowName: "support-triage",
        workflowDefinitionActorId: "definition://support-triage",
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
        primaryActorId: "actor-intake-v1",
        createdAt: "2026-04-08T08:00:00Z",
        preparedAt: "2026-04-08T08:01:00Z",
        publishedAt: "2026-04-08T08:02:00Z",
        retiredAt: null,
        workflowName: "support-triage-v1",
        workflowDefinitionActorId: "definition://support-triage-v1",
        inlineWorkflowCount: 1,
        scriptId: "",
        scriptRevision: "",
        scriptDefinitionActorId: "",
        scriptSourceHash: "",
        staticActorTypeName: "",
      },
    ],
    ...overrides,
  };
}

function mockCreateServiceCatalog() {
  return [
    {
      serviceKey: "scope-1:default",
      tenantId: "scope-1",
      appId: "default",
      namespace: "default",
      serviceId: "default",
      displayName: "Support Runtime",
      defaultServingRevisionId: "rev-2",
      activeServingRevisionId: "rev-2",
      deploymentId: "dep-2",
      primaryActorId: "actor-intake",
      deploymentStatus: "Active",
      endpoints: [],
      policyIds: [],
      updatedAt: "2026-04-09T09:00:00Z",
    },
    {
      serviceKey: "scope-1:alpha-service",
      tenantId: "scope-1",
      appId: "default",
      namespace: "default",
      serviceId: "alpha-service",
      displayName: "Team Alpha Runtime",
      defaultServingRevisionId: "rev-2",
      activeServingRevisionId: "rev-2",
      deploymentId: "dep-2",
      primaryActorId: "actor-intake",
      deploymentStatus: "Active",
      endpoints: [],
      policyIds: [],
      updatedAt: "2026-04-09T09:00:00Z",
    },
  ];
}

function mockCreateMembersCatalog() {
  return {
    scopeId: "scope-1",
    members: [
      {
        memberId: "member-support",
        scopeId: "scope-1",
        displayName: "Support Escalation Triage",
        description: "负责处理升级工单",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "default",
        lastBoundRevisionId: "rev-2",
        createdAt: "2026-04-09T08:00:00Z",
        updatedAt: "2026-04-09T09:00:00Z",
      },
    ],
    nextPageToken: null,
  };
}

function mockCreateTeamMembersCatalog() {
  return {
    scopeId: "scope-1",
    members: [
      {
        memberId: "member-team-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        displayName: "Team Alpha Operator",
        description: "负责处理升级工单",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "alpha-service",
        lastBoundRevisionId: "rev-alpha",
        createdAt: "2026-04-09T08:00:00Z",
        updatedAt: "2026-04-09T09:00:00Z",
      },
    ],
    nextPageToken: null,
  };
}

function mockCreateTeamMembersCatalogWithUnpublishedReadyMember() {
  return {
    scopeId: "scope-1",
    members: [
      ...mockCreateTeamMembersCatalog().members,
      {
        memberId: "member-unpublished",
        scopeId: "scope-1",
        teamId: "t-alpha",
        displayName: "Unpublished Ready Member",
        description: "Lifecycle is ready but published service is missing",
        implementationKind: "workflow",
        lifecycleStage: "bind_ready",
        publishedServiceId: "",
        lastBoundRevisionId: "rev-unpublished",
        createdAt: "2026-04-09T08:00:00Z",
        updatedAt: "2026-04-09T09:00:00Z",
      },
    ],
    nextPageToken: null,
  };
}

function mockCreateTeamSummary() {
  return {
    teamId: "t-alpha",
    scopeId: "scope-1",
    displayName: "Alpha Support Team",
    description: "Team summary",
    entryMemberId: "member-team-alpha",
    lifecycleStage: "active",
    memberCount: 3,
    createdAt: "2026-05-01T08:00:00Z",
    updatedAt: "2026-05-01T08:05:00Z",
  };
}

function mockCreateRunAudit(scopeId: string, runId: string) {
  return {
    summary: {
      scopeId,
      serviceId: "default",
      runId,
      actorId: "actor-intake",
      definitionActorId: "definition://support-triage",
      revisionId: runId === "run-current" ? "rev-2" : "rev-1",
      deploymentId: runId === "run-current" ? "dep-2" : "dep-1",
      workflowName: "support-triage",
      completionStatus: runId === "run-current" ? "waiting_approval" : "completed",
      stateVersion: 2,
      lastEventId: "evt-2",
      lastUpdatedAt: "2026-04-09T09:05:00Z",
      boundAt: "2026-04-09T09:00:00Z",
      bindingUpdatedAt: "2026-04-09T09:00:00Z",
      lastSuccess: runId !== "run-current",
      totalSteps: 4,
      completedSteps: runId === "run-current" ? 2 : 4,
      roleReplyCount: 1,
      lastOutput: runId === "run-current" ? "" : "Resolved",
      lastError: runId === "run-current" ? "Waiting on approval" : "",
    },
    audit: {
      reportVersion: "1",
      projectionScope: "service",
      topologySource: "audit",
      completionStatus: runId === "run-current" ? "waiting_approval" : "completed",
      workflowName: "support-triage",
      rootActorId: "actor-intake",
      commandId: "cmd-1",
      stateVersion: 2,
      lastEventId: "evt-2",
      createdAt: "2026-04-09T09:00:00Z",
      updatedAt: "2026-04-09T09:05:00Z",
      startedAt: "2026-04-09T09:00:00Z",
      endedAt: null,
      durationMs: 1000,
      success: runId !== "run-current",
      input: "hello",
      finalOutput: runId === "run-current" ? "" : "Resolved",
      finalError: runId === "run-current" ? "Waiting on approval" : "",
      topology:
        runId === "run-current"
          ? [
              {
                parent: "actor-intake",
                child: "actor-risk",
              },
              {
                parent: "actor-risk",
                child: "actor-ops",
              },
            ]
          : [
              {
                parent: "actor-intake-v1",
                child: "actor-risk",
              },
            ],
      steps: [
        {
          stepId: "risk_review",
          stepType: runId === "run-current" ? "human_approval" : "llm_call",
          targetRole: "operator",
          requestedAt: "2026-04-09T09:01:00Z",
          completedAt: runId === "run-current" ? null : "2026-04-09T09:02:00Z",
          success: runId !== "run-current",
          workerId: "actor-intake",
          outputPreview: "",
          error: "",
          requestParameters: {},
          completionAnnotations: {},
          nextStepId: "",
          branchKey: "",
          assignedVariable: "",
          assignedValue: "",
          suspensionType: runId === "run-current" ? "human_approval" : "",
          suspensionPrompt: runId === "run-current" ? "Approve escalation" : "",
          suspensionTimeoutSeconds: null,
          requestedVariableName: "",
          durationMs: null,
        },
      ],
      roleReplies:
        runId === "run-current"
          ? [
              {
                timestamp: "2026-04-09T09:02:30Z",
                roleId: "operator",
                sessionId: "session-1",
                content: "Escalation needs approval from on-call.",
                contentLength: 39,
              },
            ]
          : [],
      timeline:
        runId === "run-current"
          ? [
              {
                timestamp: "2026-04-09T09:01:30Z",
                stage: "human_gate",
                message: "Approval requested from operator",
                agentId: "actor-intake",
                stepId: "risk_review",
                stepType: "human_approval",
                eventType: "suspension_requested",
                data: {},
              },
            ]
          : [],
      summary: {
        totalSteps: 4,
        requestedSteps: 2,
        completedSteps: runId === "run-current" ? 2 : 4,
        roleReplyCount: 1,
        stepTypeCounts: {},
      },
    },
  };
}

jest.mock("@/shared/api/scopesApi", () => ({
  scopesApi: {
    listWorkflows: jest.fn(async () => [
      {
        scopeId: "scope-1",
        workflowId: "workflow-1",
        displayName: "Support Escalation Triage",
        serviceKey: "scope-1:default",
        workflowName: "support-triage",
        actorId: "actor-intake",
        activeRevisionId: "rev-2",
        deploymentId: "dep-2",
        deploymentStatus: "Active",
        updatedAt: "2026-04-09T09:00:00Z",
      },
      {
        scopeId: "scope-1",
        workflowId: "workflow-2",
        displayName: "Support Escalation Triage v1",
        serviceKey: "scope-1:default",
        workflowName: "support-triage-v1",
        actorId: "actor-intake-v1",
        activeRevisionId: "rev-1",
        deploymentId: "dep-1",
        deploymentStatus: "Retired",
        updatedAt: "2026-04-08T09:00:00Z",
      },
    ]),
    getWorkflowDetail: jest.fn(async () => ({
      available: true,
      scopeId: "scope-1",
      workflow: {
        scopeId: "scope-1",
        workflowId: "workflow-1",
        displayName: "Support Escalation Triage",
        serviceKey: "scope-1:default",
        workflowName: "support-triage",
        actorId: "actor-intake",
        activeRevisionId: "rev-2",
        deploymentId: "dep-2",
        deploymentStatus: "Active",
        updatedAt: "2026-04-09T09:00:00Z",
      },
      source: {
        workflowYaml: "name: support-triage",
        definitionActorId: "definition://support-triage",
        inlineWorkflowYamls: null,
      },
    })),
    listScripts: jest.fn(async () => [
      {
        scriptId: "script-1",
      },
    ]),
  },
}));

jest.mock("@/shared/api/runtimeGAgentApi", () => ({
  runtimeGAgentApi: {
    listActors: jest.fn(async () => [
      {
        gAgentType: "IntakeAgent",
        actorIds: ["actor-intake"],
      },
      {
        gAgentType: "RiskReviewAgent",
        actorIds: ["actor-risk"],
      },
    ]),
  },
}));

jest.mock("@/shared/api/runtimeActorsApi", () => ({
  runtimeActorsApi: {
    getActorGraphEnriched: jest.fn(async () => ({
      snapshot: {
        actorId: "actor-intake",
        workflowName: "support-triage",
        lastCommandId: "cmd-1",
        completionStatusValue: 1,
        stateVersion: 2,
        lastEventId: "evt-2",
        lastUpdatedAt: "2026-04-09T09:05:00Z",
        lastSuccess: false,
        lastOutput: "",
        lastError: "Waiting on approval",
        totalSteps: 4,
        requestedSteps: 2,
        completedSteps: 2,
        roleReplyCount: 1,
      },
      subgraph: {
        rootNodeId: "actor-intake",
        nodes: [
          {
            nodeId: "actor-intake",
            nodeType: "actor",
            updatedAt: "2026-04-09T09:05:00Z",
            properties: {
              role: "triage lead",
            },
          },
          {
            nodeId: "actor-risk",
            nodeType: "actor",
            updatedAt: "2026-04-09T09:05:00Z",
            properties: {
              role: "risk review",
            },
          },
        ],
        edges: [
          {
            edgeId: "edge-1",
            fromNodeId: "actor-intake",
            toNodeId: "actor-risk",
            edgeType: "handoff",
            updatedAt: "2026-04-09T09:05:00Z",
            properties: {},
          },
        ],
      },
    })),
  },
}));

jest.mock("@/shared/api/scopeRuntimeApi", () => ({
  scopeRuntimeApi: {
    listServices: jest.fn(async () => mockCreateServiceCatalog()),
    getServiceRevisions: jest.fn(async () => mockCreateServiceRevisionCatalog()),
    listMemberRuns: jest.fn(async () => mockCreateRunsCatalog()),
    listServiceRuns: jest.fn(async () => mockCreateRunsCatalog()),
    getMemberRunAudit: jest.fn(async (scopeId: string, _memberId: string, runId: string) =>
      mockCreateRunAudit(scopeId, runId),
    ),
    getServiceRunAudit: jest.fn(async (scopeId: string, _serviceId: string, runId: string) =>
      mockCreateRunAudit(scopeId, runId),
    ),
  },
}));

jest.mock("@/shared/agui/sseFrameNormalizer", () => ({
  parseBackendSSEStream: jest.fn(async function* () {
    yield {
      type: "TEXT_MESSAGE_CONTENT",
      delta: "Team response",
      timestamp: 1,
    };
    yield {
      type: "RUN_FINISHED",
      result: { output: "Team response" },
      runId: "team-run-1",
      timestamp: 2,
    };
  }),
}));

jest.mock("@/shared/api/runtimeRunsApi", () => ({
  runtimeRunsApi: {
    streamTeamChat: jest.fn(async () => ({
      ok: true,
      body: {
        getReader: () => ({
          read: async () => ({ done: true, value: undefined }),
          releaseLock: () => undefined,
        }),
      },
    })),
  },
}));

jest.mock("@/shared/studio/api", () => ({
  isStudioApiStatus: (error: unknown, status: number) =>
    typeof error === "object" &&
    error !== null &&
    "status" in error &&
    (error as { status?: unknown }).status === status,
  studioApi: {
    getScopeBinding: jest.fn(async () => ({
      available: true,
      scopeId: "scope-1",
      serviceId: "default",
      displayName: "Support Escalation Triage",
      serviceKey: "scope-1:default",
      defaultServingRevisionId: "rev-2",
      activeServingRevisionId: "rev-2",
      deploymentId: "dep-2",
      deploymentStatus: "Active",
      primaryActorId: "actor-intake",
      updatedAt: "2026-04-09T09:00:00Z",
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
          primaryActorId: "actor-intake",
          createdAt: "2026-04-09T08:00:00Z",
          preparedAt: "2026-04-09T08:01:00Z",
          publishedAt: "2026-04-09T08:02:00Z",
          retiredAt: null,
          workflowName: "support-triage",
          workflowDefinitionActorId: "definition://support-triage",
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
          primaryActorId: "actor-intake-v1",
          createdAt: "2026-04-08T08:00:00Z",
          preparedAt: "2026-04-08T08:01:00Z",
          publishedAt: "2026-04-08T08:02:00Z",
          retiredAt: null,
          workflowName: "support-triage-v1",
          workflowDefinitionActorId: "definition://support-triage-v1",
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
      displayName: "Support Escalation Triage",
      serviceKey: "scope-1:default",
      defaultServingRevisionId: "rev-2",
      activeServingRevisionId: "rev-2",
      deploymentId: "dep-2",
      deploymentStatus: "Active",
      primaryActorId: "actor-intake",
      updatedAt: "2026-04-09T09:00:00Z",
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
          primaryActorId: "actor-intake",
          createdAt: "2026-04-09T08:00:00Z",
          preparedAt: "2026-04-09T08:01:00Z",
          publishedAt: "2026-04-09T08:02:00Z",
          retiredAt: null,
          workflowName: "support-triage",
          workflowDefinitionActorId: "definition://support-triage",
          inlineWorkflowCount: 1,
          scriptId: "",
          scriptRevision: "",
          scriptDefinitionActorId: "",
          scriptSourceHash: "",
          staticActorTypeName: "",
        },
      ],
    })),
    getWorkspaceSettings: jest.fn(async () => ({
      runtimeBaseUrl: "https://runtime.aevatar.test",
      directories: [
        {
          directoryId: "default",
          label: "Default",
          path: "/tmp/workflows",
          isBuiltIn: false,
        },
      ],
    })),
    getConnectorCatalog: jest.fn(async () => ({
      homeDirectory: "actor://connector-catalog",
      filePath: "actor://connector-catalog/connectors",
      fileExists: true,
      connectors: [
        {
          name: "web-search",
          type: "http",
          enabled: true,
          timeoutMs: 30000,
          retry: 1,
          http: {
            baseUrl: "https://search.example.com",
            allowedMethods: ["GET"],
            allowedPaths: ["/search"],
            allowedInputKeys: ["query"],
            defaultHeaders: {},
          },
        },
        {
          name: "ops-terminal",
          type: "cli",
          enabled: false,
          timeoutMs: 30000,
          retry: 0,
          cli: {
            command: "opsctl",
            fixedArguments: ["tickets"],
            allowedOperations: ["lookup"],
            allowedInputKeys: ["ticket"],
            workingDirectory: "/tmp",
            environment: {},
          },
        },
      ],
    })),
    listMembers: jest.fn(async () => mockCreateMembersCatalog()),
    getTeam: jest.fn(async () => mockCreateTeamSummary()),
    updateTeam: jest.fn(async () => ({
      ...mockCreateTeamSummary(),
      displayName: "Alpha Ops Team",
      description: "",
    })),
    setTeamEntryMember: jest.fn(async (_scopeId: string, _teamId: string, memberId: string) => ({
      ...mockCreateTeamSummary(),
      entryMemberId: memberId,
    })),
    clearTeamEntryMember: jest.fn(async () => ({
      ...mockCreateTeamSummary(),
      entryMemberId: null,
    })),
    archiveTeam: jest.fn(async () => ({
      ...mockCreateTeamSummary(),
      lifecycleStage: "archived",
    })),
    listTeamMembers: jest.fn(async () => mockCreateTeamMembersCatalog()),
    parseYaml: jest.fn(async () => ({
      document: {
        name: "support-triage",
        roles: [
          {
            id: "triage_operator",
            name: "triage_operator",
            connectors: ["web-search", "crm-sync"],
          },
        ],
      },
      graph: null,
      findings: [],
    })),
  },
}));

function createStudioApiStatusError(message: string, status: number): Error & { status: number } {
  const error = new Error(message) as Error & { status: number };
  error.status = status;
  return error;
}

describe("TeamDetailPage", () => {
  beforeEach(() => {
    setLocale("zh-CN", false);
    window.history.replaceState({}, "", "/teams/scope-1/t-alpha");
    (scopesApi.listWorkflows as jest.Mock).mockClear();
    (scopesApi.listScripts as jest.Mock).mockClear();
    (runtimeGAgentApi.listActors as jest.Mock).mockClear();
    (runtimeActorsApi.getActorGraphEnriched as jest.Mock).mockClear();
    (scopeRuntimeApi.listServices as jest.Mock).mockReset();
    (scopeRuntimeApi.listServices as jest.Mock).mockImplementation(
      async () => mockCreateServiceCatalog(),
    );
    (scopeRuntimeApi.getServiceRevisions as jest.Mock).mockReset();
    (scopeRuntimeApi.getServiceRevisions as jest.Mock).mockImplementation(
      async () => mockCreateServiceRevisionCatalog(),
    );
    (scopeRuntimeApi.listMemberRuns as jest.Mock).mockReset();
    (scopeRuntimeApi.listMemberRuns as jest.Mock).mockImplementation(
      async () => mockCreateRunsCatalog(),
    );
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockReset();
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockImplementation(
      async () => mockCreateRunsCatalog(),
    );
    (scopeRuntimeApi.getMemberRunAudit as jest.Mock).mockReset();
    (scopeRuntimeApi.getMemberRunAudit as jest.Mock).mockImplementation(
      async (scopeId: string, _memberId: string, runId: string) =>
        mockCreateRunAudit(scopeId, runId),
    );
    (scopeRuntimeApi.getServiceRunAudit as jest.Mock).mockReset();
    (scopeRuntimeApi.getServiceRunAudit as jest.Mock).mockImplementation(
      async (scopeId: string, _serviceId: string, runId: string) =>
        mockCreateRunAudit(scopeId, runId),
    );
    (studioApi.listMembers as jest.Mock).mockReset();
    (studioApi.listMembers as jest.Mock).mockImplementation(
      async () => mockCreateMembersCatalog(),
    );
    (studioApi.getTeam as jest.Mock).mockReset();
    (studioApi.getTeam as jest.Mock).mockImplementation(
      async () => mockCreateTeamSummary(),
    );
    (studioApi.updateTeam as jest.Mock).mockReset();
    (studioApi.updateTeam as jest.Mock).mockImplementation(async () => ({
      ...mockCreateTeamSummary(),
      displayName: "Alpha Ops Team",
      description: "",
    }));
    (studioApi.setTeamEntryMember as jest.Mock).mockReset();
    (studioApi.setTeamEntryMember as jest.Mock).mockImplementation(
      async (_scopeId: string, _teamId: string, memberId: string) => ({
        ...mockCreateTeamSummary(),
        entryMemberId: memberId,
      }),
    );
    (studioApi.clearTeamEntryMember as jest.Mock).mockReset();
    (studioApi.clearTeamEntryMember as jest.Mock).mockImplementation(async () => ({
      ...mockCreateTeamSummary(),
      entryMemberId: null,
    }));
    (studioApi.archiveTeam as jest.Mock).mockReset();
    (studioApi.archiveTeam as jest.Mock).mockImplementation(async () => ({
      ...mockCreateTeamSummary(),
      lifecycleStage: "archived",
    }));
    (studioApi.listTeamMembers as jest.Mock).mockReset();
    (studioApi.listTeamMembers as jest.Mock).mockImplementation(
      async () => mockCreateTeamMembersCatalog(),
    );
    (runtimeRunsApi.streamTeamChat as jest.Mock).mockClear();
  });

  it("renders no-team-selected state without detail data flows for scope-only links", async () => {
    window.history.replaceState({}, "", "/teams/scope-1");

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("未选择团队")).toBeTruthy();
    expect(
      screen.getByText("当前链接只有工作区上下文，没有具体团队标识。返回团队列表后选择一个团队。"),
    ).toBeTruthy();
    expect(screen.queryByText("Team authority")).toBeNull();

    await waitFor(() => {
      expect(studioApi.getTeam).not.toHaveBeenCalled();
      expect(studioApi.listTeamMembers).not.toHaveBeenCalled();
      expect(studioApi.getWorkspaceSettings).not.toHaveBeenCalled();
      expect(studioApi.getConnectorCatalog).not.toHaveBeenCalled();
      expect(studioApi.listMembers).not.toHaveBeenCalled();
      expect(scopesApi.listWorkflows).not.toHaveBeenCalled();
      expect(scopesApi.listScripts).not.toHaveBeenCalled();
      expect(scopeRuntimeApi.listServices).not.toHaveBeenCalled();
      expect(runtimeGAgentApi.listActors).not.toHaveBeenCalled();
      expect(scopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();
      expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalled();
      expect(scopeRuntimeApi.listMemberRuns).not.toHaveBeenCalled();
      expect(scopeRuntimeApi.getServiceRunAudit).not.toHaveBeenCalled();
      expect(scopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalled();
      expect(runtimeActorsApi.getActorGraphEnriched).not.toHaveBeenCalled();
    });
  });

  it("renders the chinese team-first overview shell", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(
      await screen.findByText((_, node) => {
        return node?.textContent === "Aevatar / 团队 / 团队详情 / 概览";
      }),
    ).toBeTruthy();
    expect(screen.getByRole("link", { name: "Aevatar" })).toBeTruthy();
    expect(screen.getByRole("link", { name: "团队" })).toBeTruthy();
    expect(screen.getByText("工作区 ID")).toBeTruthy();
    expect(screen.getByText("scope-1")).toBeTruthy();
    const currentPostureHeading = screen.getByText("启动状态");
    const compositionHeading = screen.getByText("团队构成");
    const configurationHeading = screen.getByText("配置明细");
    expect(screen.getByRole("button", { name: "测试团队" })).toBeTruthy();
    expect(currentPostureHeading).toBeTruthy();
    expect(await screen.findByText(/ReadModel ·/)).toBeTruthy();
    expect(await screen.findByText("版本 · 运行中")).toBeTruthy();
    expect(await screen.findByText("运行 · 等待处理")).toBeTruthy();
    expect(compositionHeading).toBeTruthy();
    expect(configurationHeading).toBeTruthy();
    expect(screen.queryByLabelText("Team test prompt")).toBeNull();
    expect(
      currentPostureHeading.compareDocumentPosition(compositionHeading) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(
      compositionHeading.compareDocumentPosition(configurationHeading) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(screen.queryByText("Team authority")).toBeNull();
    expect(screen.queryByText("信任态势")).toBeNull();
    expect(screen.queryByText("治理快照")).toBeNull();
    expect(screen.queryByText("Run Compare / Change Diff")).toBeNull();
    expect(screen.queryByText("运行摘要")).toBeNull();
    expect(screen.queryByText("连接器引用")).toBeNull();
    expect(screen.queryByText("服务能力")).toBeNull();
    expect(await screen.findByRole("button", { name: "编辑团队" })).toBeEnabled();
    expect(await screen.findByRole("button", { name: "团队更多操作" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "服务映射" })).toBeNull();
    expect(screen.queryByRole("button", { name: "高级编辑" })).toBeNull();
    expect(screen.queryByRole("button", { name: "归档团队" })).toBeNull();
    expect(screen.queryByRole("button", { name: "事件流" })).toBeNull();
    expect(screen.queryByRole("button", { name: "事件拓扑" })).toBeNull();
    expect(screen.queryByRole("button", { name: "处理等待 Run" })).toBeNull();
    expect(screen.queryByRole("button", { name: "治理绑定" })).toBeNull();
    expect(screen.queryByRole("button", { name: "部署记录" })).toBeNull();
    expect(screen.queryByText("稳定")).toBeNull();
    expect(studioApi.getTeam).toHaveBeenCalledWith("scope-1", "t-alpha");
  });

  it("keeps compare diagnostics out of the overview when no successful baseline exists", async () => {
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockResolvedValueOnce({
      ...mockCreateRunsCatalog(),
      runs: [mockCreateRunsCatalog().runs[0]],
    });

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("启动状态")).toBeTruthy();
    expect(screen.getByText("团队构成")).toBeTruthy();
    expect(screen.getByText("配置明细")).toBeTruthy();
    expect(screen.queryByText("信任态势")).toBeNull();
    expect(screen.queryByText("No successful baseline is available yet.")).toBeNull();
    expect(screen.queryByText("等待基线")).toBeNull();
    expect(screen.queryByText("无基线")).toBeNull();
    expect(screen.queryByText("暂无成功基线运行")).toBeNull();
  });

  it("keeps selected run facts aligned without loading run audit diagnostics", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha?runId=run-good",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("当前态势")).toBeTruthy();
    expect(await screen.findByText("已完成")).toBeTruthy();
    expect(await screen.findByText("版本 · 运行中")).toBeTruthy();
    expect(await screen.findByText(/ReadModel ·/)).toBeTruthy();
    expect(screen.queryByText("No successful baseline is available yet.")).toBeNull();
    expect(screen.queryByText("Comparing run run-current against baseline run-good.")).toBeNull();
    expect(scopeRuntimeApi.getServiceRunAudit).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalled();
  });

  it("uses the Team display name for the team heading", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(
      await screen.findByRole("heading", {
        level: 1,
        name: "Alpha Support Team",
      }),
    ).toBeTruthy();
  });

  it("demotes machine-generated long scope ids into compact team metadata", async () => {
    const longScopeId = "1626c177-917b-4fcc-a5ee-aa74a171b0d6";

    window.history.replaceState(
      {},
      "",
      `/teams/${longScopeId}/t-alpha`,
    );
    (scopesApi.listWorkflows as jest.Mock).mockResolvedValueOnce([]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(
      await screen.findByRole("heading", { level: 1, name: "当前团队" }),
    ).toBeTruthy();
    expect(screen.queryByText(`Team ${longScopeId}`)).toBeNull();
    expect(screen.getByText("工作区 ID")).toBeTruthy();
    expect(screen.getByText("1626c177...71b0d6")).toBeTruthy();
  });

  it("falls back to workflowName when Team display name is unavailable and the workflow display name is only the workflow id", async () => {
    (studioApi.getTeam as jest.Mock).mockResolvedValueOnce({
      ...mockCreateTeamSummary(),
      displayName: "",
    });
    (scopesApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        scopeId: "scope-1",
        workflowId: "workflow-opaque-id",
        displayName: "workflow-opaque-id",
        serviceKey: "scope-1:default",
        workflowName: "support-triage",
        actorId: "actor-intake",
        activeRevisionId: "rev-2",
        deploymentId: "dep-2",
        deploymentStatus: "Active",
        updatedAt: "2026-04-09T09:00:00Z",
      },
    ]);
    (scopesApi.getWorkflowDetail as jest.Mock).mockResolvedValueOnce({
      available: true,
      scopeId: "scope-1",
      workflow: {
        scopeId: "scope-1",
        workflowId: "workflow-opaque-id",
        displayName: "workflow-opaque-id",
        serviceKey: "scope-1:default",
        workflowName: "support-triage",
        actorId: "actor-intake",
        activeRevisionId: "rev-2",
        deploymentId: "dep-2",
        deploymentStatus: "Active",
        updatedAt: "2026-04-09T09:00:00Z",
      },
      source: {
        workflowYaml: "name: support-triage",
        definitionActorId: "definition://support-triage",
        inlineWorkflowYamls: null,
      },
    });

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(
      await screen.findByRole("heading", {
        level: 1,
        name: "support-triage",
      }),
    ).toBeTruthy();
    expect(
      screen.queryByRole("heading", {
        level: 1,
        name: "workflow-opaque-id",
      }),
    ).toBeNull();
  });

  it("keeps long raw identifiers compact inside overview configuration details", async () => {
    const longRevisionId =
      "rev-20260414154556-4d89bc2a3bf347f8b3bde41d716964f3";

    (scopeRuntimeApi.getServiceRevisions as jest.Mock).mockResolvedValueOnce(
      mockCreateServiceRevisionCatalog({
        defaultServingRevisionId: longRevisionId,
        activeServingRevisionId: longRevisionId,
        revisions: [
          {
            ...mockCreateServiceRevisionCatalog().revisions[0],
            revisionId: longRevisionId,
          },
        ],
      }),
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByText("配置明细");

    expect(await screen.findByText("revisionId: rev-20260414…716964f3")).toBeTruthy();
    expect(screen.queryByText(`revisionId: ${longRevisionId}`)).toBeNull();
  });

  it("returns to the teams list when clicking the breadcrumb teams link", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "编辑团队" });
    fireEvent.click(screen.getByRole("link", { name: "团队" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams");
      expect(window.location.search).toContain("scopeId=scope-1");
    });
  });

  it("returns to the teams list when clicking the breadcrumb aevatar link", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "编辑团队" });
    fireEvent.click(screen.getByRole("link", { name: "Aevatar" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams");
      expect(window.location.search).toContain("scopeId=scope-1");
    });
  });

  it("switches tabs inside the detail page", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "编辑团队" });
    fireEvent.click(screen.getByRole("button", { name: "团队成员" }));

    expect(await screen.findByText("Team Alpha Operator")).toBeTruthy();
    expect(window.location.search).toContain("tab=members");
    expect(window.location.search).not.toContain("step=bind");
  });

  it("falls legacy event deep links back to the overview tab", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha?serviceId=default&tab=events",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("启动状态")).toBeTruthy();
    expect(screen.queryByText("当前任务事件流")).toBeNull();

    await waitFor(() => {
      const params = new URLSearchParams(window.location.search);
      expect(params.get("memberId")).toBe("member-support");
      expect(params.get("serviceId")).toBe("default");
      expect(params.get("tab")).toBeNull();
    });

    await waitFor(() => {
      expect(scopeRuntimeApi.listServiceRuns).toHaveBeenCalledWith(
        "scope-1",
        "default",
        expect.objectContaining({ take: 12 }),
      );
    });
    expect(scopeRuntimeApi.listMemberRuns).not.toHaveBeenCalled();
  });

  it("shows configuration details in the overview", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("配置明细")).toBeTruthy();
    expect(screen.getAllByText("团队 Workflow").length).toBeGreaterThan(0);
    expect(screen.getAllByText("绑定方式").length).toBeGreaterThan(0);
    expect(screen.getAllByText("主服务入口").length).toBeGreaterThan(0);
    expect(screen.getAllByText("版本标识").length).toBeGreaterThan(0);
    expect(screen.queryByText("连接器引用")).toBeNull();
    expect(screen.queryByText("服务能力")).toBeNull();
  });

  it("shows a readable team members view", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "编辑团队" });
    fireEvent.click(screen.getByRole("button", { name: "团队成员" }));

    expect(await screen.findByText("Team Alpha Operator")).toBeTruthy();
    expect(
      screen.getByText(
        /设置入口决定团队测试从哪里开始，Workflow 调试入口用于补齐成员实现和绑定/,
      ),
    ).toBeTruthy();
    expect(screen.getByText("负责处理升级工单")).toBeTruthy();
    expect(screen.getByText("member-team-alpha")).toBeTruthy();
    expect(screen.getByText("入口成员")).toBeTruthy();
    expect(screen.getByRole("link", { name: "编辑工作流" })).toBeTruthy();
    expect(screen.getByRole("link", { name: "调试工作流" })).toBeTruthy();
    expect(screen.queryByRole("link", { name: "Test member" })).toBeNull();
    expect(screen.queryByRole("link", { name: "View runs" })).toBeNull();
    expect(screen.queryByText("参与者结构")).toBeNull();
    expect(screen.queryByText("运行时参与者身份")).toBeNull();
    expect(screen.queryByRole("button", { name: "打开 Services" })).toBeNull();
  });

  it("marks the route-selected member in the members tab", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha?memberId=member-team-alpha&tab=members",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("Team Alpha Operator")).toBeTruthy();
    expect(screen.getByText("当前选中")).toBeTruthy();
  });

  it("shows the route-selected member in the overview status cards", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha?memberId=member-team-alpha",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("当前成员")).toBeTruthy();
    expect(screen.getAllByText("member-team-alpha").length).toBeGreaterThan(0);
    expect(screen.getByText("memberId · member-team-alpha")).toBeTruthy();
  });

  it("guides the user to test the Team when no run is visible yet", async () => {
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockResolvedValueOnce({
      ...mockCreateRunsCatalog(),
      runs: [],
    });
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha?memberId=member-team-alpha",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("启动状态")).toBeTruthy();
    expect(screen.getAllByText("等待首次测试").length).toBeGreaterThan(0);
    expect(screen.getByText("下一步 · 测试团队")).toBeTruthy();
    expect(
      screen.getByText("团队入口已就绪，但还没有可见运行。点击“测试团队”生成第一条运行。"),
    ).toBeTruthy();
    expect(screen.getByText("测试团队后会在这里显示最新运行。")).toBeTruthy();
    expect(screen.queryByText("暂无可见运行")).toBeNull();
    expect(screen.queryByText("暂无近期可见运行")).toBeNull();
    expect(scopeRuntimeApi.listMemberRuns).not.toHaveBeenCalled();
  });

  it("does not query runs for member-like service ids that are missing from the service catalog", async () => {
    (studioApi.getTeam as jest.Mock).mockResolvedValueOnce({
      ...mockCreateTeamSummary(),
      entryMemberId: "member-untitled-member1",
    });
    (studioApi.listTeamMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-1",
      members: [
        {
          memberId: "member-untitled-member1",
          scopeId: "scope-1",
          teamId: "t-alpha",
          displayName: "Untitled Member",
          description: "Service id points at a missing member-derived service",
          implementationKind: "workflow",
          lifecycleStage: "bind_ready",
          publishedServiceId: "member-untitled-member1",
          lastBoundRevisionId: "rev-missing",
          createdAt: "2026-04-09T08:00:00Z",
          updatedAt: "2026-04-09T09:00:00Z",
        },
      ],
      nextPageToken: null,
    });

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect((await screen.findAllByText("Untitled Member")).length).toBeGreaterThan(0);

    await waitFor(() => {
      expect(scopeRuntimeApi.listServices).toHaveBeenCalledWith("scope-1", {
        appId: "default",
      });
    });
    expect(scopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalledWith(
      "scope-1",
      "member-untitled-member1",
    );
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalledWith(
      "scope-1",
      "member-untitled-member1",
      expect.anything(),
    );
    expect(scopeRuntimeApi.listMemberRuns).not.toHaveBeenCalled();
  });

  it("shows the configured Team entry member without treating it as the service target", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await waitFor(() => {
      expect(screen.getAllByText("入口成员").length).toBeGreaterThan(0);
    });
    await waitFor(() => {
      expect(screen.getAllByText("Team Alpha Operator").length).toBeGreaterThan(0);
    });
    expect(screen.getByText("调用这支团队时会先路由到这个成员。")).toBeTruthy();
    expect(screen.getByRole("button", { name: "清除入口成员" })).toBeEnabled();

    fireEvent.click(screen.getByRole("button", { name: "团队成员" }));

    expect(await screen.findByText("入口成员")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "设为入口成员" })).toBeNull();
    expect(screen.getByText("alph...vice")).toBeTruthy();
  });

  it("sets a Team entry member from the members tab", async () => {
    (studioApi.listTeamMembers as jest.Mock).mockImplementation(async () => ({
      scopeId: "scope-1",
      members: [
        ...mockCreateTeamMembersCatalog().members,
        {
          memberId: "member-team-beta",
          scopeId: "scope-1",
          teamId: "t-alpha",
          displayName: "Team Beta Operator",
          description: "负责处理二线升级",
          implementationKind: "workflow",
          lifecycleStage: "bind_ready",
          publishedServiceId: "beta-service",
          lastBoundRevisionId: "rev-beta",
          createdAt: "2026-04-09T08:00:00Z",
          updatedAt: "2026-04-09T09:00:00Z",
        },
      ],
      nextPageToken: null,
    }));

    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "编辑团队" });
    fireEvent.click(screen.getByRole("button", { name: "团队成员" }));
    expect(await screen.findByText("Team Beta Operator")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "设为入口成员" }));

    await waitFor(() => {
      expect(studioApi.setTeamEntryMember).toHaveBeenCalledWith(
        "scope-1",
        "t-alpha",
        "member-team-beta",
      );
    });
    expect(message.info).toHaveBeenCalledWith(
      "团队入口变更已提交，正在等待同步确认。",
    );
    await waitFor(() => {
      expect(studioApi.getTeam).toHaveBeenCalledTimes(2);
      expect(studioApi.listTeamMembers).toHaveBeenCalledTimes(2);
    });
  });

  it("clears the Team entry member from the overview", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    fireEvent.click(await screen.findByRole("button", { name: "清除入口成员" }));

    await waitFor(() => {
      expect(studioApi.clearTeamEntryMember).toHaveBeenCalledWith(
        "scope-1",
        "t-alpha",
      );
    });
    expect(message.info).toHaveBeenCalledWith(
      "团队入口清除已提交，正在等待同步确认。",
    );
    await waitFor(() => {
      expect(studioApi.getTeam).toHaveBeenCalledTimes(2);
      expect(studioApi.listTeamMembers).toHaveBeenCalledTimes(2);
    });
  });

  it("streams Team Test through the team runtime endpoint", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    const dialog = await openTeamTestDialog();
    fireEvent.change(within(dialog).getByLabelText("测试 Prompt"), {
      target: { value: "Can this Team handle refunds?" },
    });
    fireEvent.click(within(dialog).getByRole("button", { name: "开始测试" }));

    await waitFor(() => {
      expect(runtimeRunsApi.streamTeamChat).toHaveBeenCalledWith(
        "scope-1",
        "t-alpha",
        expect.objectContaining({
          prompt: "Can this Team handle refunds?",
          metadata: expect.objectContaining({
            source: "team-detail",
            teamId: "t-alpha",
          }),
        }),
        expect.any(AbortSignal),
      );
    });
    expect(await screen.findByText("Team response")).toBeTruthy();
    expect(await screen.findByText(/上次测试/)).toBeTruthy();
  });

  it("explains when Team Test uses the entry member instead of the selected member", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha?memberId=member-support&testTeam=1",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    const dialog = await screen.findByTestId("team-test-modal-body");

    expect(
      within(dialog).getByText(
        "当前页面选中的是 member-support，团队测试仍通过入口成员发起。",
      ),
    ).toBeTruthy();
  });

  it("auto-opens Team Test from the Team Detail route intent", async () => {
    window.history.replaceState({}, "", "/teams/scope-1/t-alpha?testTeam=1");

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByTestId("team-test-modal-body")).toBeTruthy();
    expect(screen.getByLabelText("测试 Prompt")).toBeTruthy();
  });

  it("does not treat bind-ready members without a published service as Team entry candidates", async () => {
    (studioApi.getTeam as jest.Mock).mockImplementation(async () => ({
      ...mockCreateTeamSummary(),
      entryMemberId: null,
    }));
    (studioApi.listTeamMembers as jest.Mock).mockImplementation(
      async () => mockCreateTeamMembersCatalogWithUnpublishedReadyMember(),
    );
    renderWithQueryClient(React.createElement(TeamDetailPage));

    const dialog = await openTeamTestDialog();

    expect(await within(dialog).findByText("Unpublished Ready Member")).toBeTruthy();
    const unpublishedRow = within(dialog)
      .getByText("Unpublished Ready Member")
      .closest("div");
    expect(unpublishedRow).toBeTruthy();
    expect(
      within(unpublishedRow as HTMLElement).queryByRole("button", {
        name: "设为入口并测试",
      }),
    ).toBeNull();
    expect(within(dialog).getByRole("link", { name: "先 Build / Bind" }))
      .toHaveAttribute("href", expect.stringContaining("member-unpublished"));
  });

  it("sets a ready member as entry before testing when the Team has no entry", async () => {
    (studioApi.getTeam as jest.Mock)
      .mockResolvedValueOnce({
        ...mockCreateTeamSummary(),
        entryMemberId: null,
      })
      .mockResolvedValueOnce({
        ...mockCreateTeamSummary(),
        entryMemberId: null,
      })
      .mockResolvedValueOnce({
        ...mockCreateTeamSummary(),
        entryMemberId: "member-team-alpha",
      });
    (studioApi.setTeamEntryMember as jest.Mock).mockResolvedValueOnce(undefined);

    renderWithQueryClient(React.createElement(TeamDetailPage));

    const dialog = await openTeamTestDialog();
    fireEvent.change(within(dialog).getByLabelText("测试 Prompt"), {
      target: { value: "Route this customer question" },
    });
    fireEvent.click(within(dialog).getByRole("button", { name: "设为入口并测试" }));

    await waitFor(() => {
      expect(studioApi.setTeamEntryMember).toHaveBeenCalledWith(
        "scope-1",
        "t-alpha",
        "member-team-alpha",
      );
    });
    await waitFor(() => {
      expect(studioApi.getTeam).toHaveBeenCalledTimes(3);
      expect(runtimeRunsApi.streamTeamChat).toHaveBeenCalledWith(
        "scope-1",
        "t-alpha",
        expect.objectContaining({
          prompt: "Route this customer question",
        }),
        expect.any(AbortSignal),
      );
    });
  });

  it("waits for the entry read model before invoking Team Test", async () => {
    (studioApi.getTeam as jest.Mock)
      .mockResolvedValueOnce({
        ...mockCreateTeamSummary(),
        entryMemberId: null,
      })
      .mockResolvedValueOnce({
        ...mockCreateTeamSummary(),
        entryMemberId: null,
      })
      .mockResolvedValueOnce({
        ...mockCreateTeamSummary(),
        entryMemberId: null,
      })
      .mockResolvedValueOnce({
        ...mockCreateTeamSummary(),
        entryMemberId: "member-team-alpha",
      });
    (studioApi.setTeamEntryMember as jest.Mock).mockResolvedValueOnce(undefined);

    renderWithQueryClient(React.createElement(TeamDetailPage));

    const dialog = await openTeamTestDialog();
    fireEvent.change(within(dialog).getByLabelText("测试 Prompt"), {
      target: { value: "Route after projection catches up" },
    });
    fireEvent.click(within(dialog).getByRole("button", { name: "设为入口并测试" }));

    await waitFor(() => {
      expect(studioApi.getTeam).toHaveBeenCalledTimes(4);
      expect(runtimeRunsApi.streamTeamChat).toHaveBeenCalledWith(
        "scope-1",
        "t-alpha",
        expect.objectContaining({
          prompt: "Route after projection catches up",
        }),
        expect.any(AbortSignal),
      );
    });
  });

  it("does not invoke Team Test while the entry read model is still syncing", async () => {
    (studioApi.getTeam as jest.Mock).mockResolvedValue({
      ...mockCreateTeamSummary(),
      entryMemberId: null,
    });
    (studioApi.setTeamEntryMember as jest.Mock).mockResolvedValueOnce(undefined);

    renderWithQueryClient(React.createElement(TeamDetailPage));

    const dialog = await openTeamTestDialog();
    fireEvent.change(within(dialog).getByLabelText("测试 Prompt"), {
      target: { value: "Route before projection catches up" },
    });
    fireEvent.click(within(dialog).getByRole("button", { name: "设为入口并测试" }));

    expect(await screen.findByText("团队入口正在同步")).toBeTruthy();
    expect(
      screen.getAllByText(
        "团队入口已被后端受理，但读模型还没有确认新入口成员。请稍后重试测试团队。",
      ).length,
    ).toBeGreaterThan(0);
    expect(runtimeRunsApi.streamTeamChat).not.toHaveBeenCalled();
  });

  it("shows backend unsupported for Team Test 404 responses", async () => {
    const unsupportedError = new Error("Not Found") as Error & { status: number };
    unsupportedError.status = 404;
    (runtimeRunsApi.streamTeamChat as jest.Mock).mockRejectedValueOnce(
      unsupportedError,
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    const dialog = await openTeamTestDialog();
    fireEvent.change(within(dialog).getByLabelText("测试 Prompt"), {
      target: { value: "Try unsupported backend" },
    });
    fireEvent.click(within(dialog).getByRole("button", { name: "开始测试" }));

    expect(await screen.findByText("后端暂不支持团队测试")).toBeTruthy();
    expect(
      screen.getAllByText(/当前后端还没有部署团队入口成员或团队调用接口/).length,
    ).toBeGreaterThan(0);
  });

  it("shows Team entry configuration errors from the router backend", async () => {
    const entryError = new Error("team 't-alpha' has no entry member configured.") as
      Error & { code: string; status: number };
    entryError.code = "TEAM_ENTRY_MEMBER_NOT_CONFIGURED";
    entryError.status = 409;
    (runtimeRunsApi.streamTeamChat as jest.Mock).mockRejectedValueOnce(entryError);

    renderWithQueryClient(React.createElement(TeamDetailPage));

    const dialog = await openTeamTestDialog();
    fireEvent.change(within(dialog).getByLabelText("测试 Prompt"), {
      target: { value: "Try missing entry" },
    });
    fireEvent.click(within(dialog).getByRole("button", { name: "开始测试" }));

    expect(await screen.findByText("未设置入口成员")).toBeTruthy();
    expect(
      screen.getAllByText(
        "这支团队还没有入口成员，请先选择一个已绑定的成员作为入口。",
      ).length,
    ).toBeGreaterThan(0);
    expect(screen.queryByText("后端暂不支持团队测试")).toBeNull();
  });

  it("does not treat router Team not found as an undeployed backend", async () => {
    const teamError = new Error("team 't-missing' not found.") as Error & {
      code: string;
      status: number;
    };
    teamError.code = "TEAM_NOT_FOUND";
    teamError.status = 404;
    (runtimeRunsApi.streamTeamChat as jest.Mock).mockRejectedValueOnce(teamError);

    renderWithQueryClient(React.createElement(TeamDetailPage));

    const dialog = await openTeamTestDialog();
    fireEvent.change(within(dialog).getByLabelText("测试 Prompt"), {
      target: { value: "Try missing team" },
    });
    fireEvent.click(within(dialog).getByRole("button", { name: "开始测试" }));

    expect(await screen.findByText("团队不存在")).toBeTruthy();
    expect(
      screen.getAllByText(
        "这支团队在当前工作区中不可见，请返回团队列表重新选择。",
      ).length,
    ).toBeGreaterThan(0);
    expect(screen.queryByText("后端暂不支持团队测试")).toBeNull();
  });

  it("routes workflow member build actions into the new workflow studio", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "编辑团队" });
    fireEvent.click(screen.getByRole("button", { name: "团队成员" }));
    fireEvent.click(await screen.findByRole("link", { name: "调试工作流" }));

    expect(window.location.pathname).toBe(
      "/teams/scope-1/t-alpha/members/member-team-alpha/workflow",
    );
  });

  it("disables non-workflow member edit actions while only workflow members are supported", async () => {
    (studioApi.listTeamMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-1",
      members: [
        {
          memberId: "member-agent-alpha",
          scopeId: "scope-1",
          teamId: "t-alpha",
          displayName: "Agent Alpha",
          description: "Agent member",
          implementationKind: "gagent",
          lifecycleStage: "bind_ready",
          publishedServiceId: "agent-service",
          lastBoundRevisionId: "rev-agent",
          createdAt: "2026-04-09T08:00:00Z",
          updatedAt: "2026-04-09T09:00:00Z",
        },
      ],
      nextPageToken: null,
    });

    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "编辑团队" });
    fireEvent.click(screen.getByRole("button", { name: "团队成员" }));

    expect(await screen.findAllByRole("button", { name: "仅支持 Workflow" }))
      .toHaveLength(2);
    expect(screen.queryByRole("link", { name: "在 Studio 中编辑" })).toBeNull();
  });

  it("shows workflow-only affordance for a non-workflow Team Test entry member", async () => {
    (studioApi.getTeam as jest.Mock).mockResolvedValue({
      ...mockCreateTeamSummary(),
      entryMemberId: "member-agent-alpha",
    });
    (studioApi.listTeamMembers as jest.Mock).mockResolvedValue({
      scopeId: "scope-1",
      members: [
        {
          memberId: "member-agent-alpha",
          scopeId: "scope-1",
          teamId: "t-alpha",
          displayName: "Agent Alpha",
          description: "Agent member",
          implementationKind: "gagent",
          lifecycleStage: "bind_ready",
          publishedServiceId: "agent-service",
          lastBoundRevisionId: "rev-agent",
          createdAt: "2026-04-09T08:00:00Z",
          updatedAt: "2026-04-09T09:00:00Z",
        },
      ],
      nextPageToken: null,
    });

    renderWithQueryClient(React.createElement(TeamDetailPage));

    const dialog = await openTeamTestDialog();

    const workflowOnlyButtons = await within(dialog).findAllByRole("button", {
      name: "仅支持 Workflow",
    });
    expect(workflowOnlyButtons).toHaveLength(1);
    expect(workflowOnlyButtons[0]).toBeDisabled();
    expect(within(dialog).queryByRole("link", { name: "在 Studio 中编辑" }))
      .toBeNull();
  });

  it("routes create-member actions into the workflow member studio", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "编辑团队" });
    fireEvent.click(screen.getByRole("button", { name: "团队成员" }));
    fireEvent.click(await screen.findByRole("link", { name: "创建工作流成员" }));

    expect(window.location.pathname).toBe(
      "/teams/scope-1/t-alpha/members/new/workflow",
    );
  });

  it("routes empty-roster create-member actions into the workflow member studio", async () => {
    (studioApi.listTeamMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-1",
      members: [],
      nextPageToken: null,
    });

    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "编辑团队" });
    fireEvent.click(screen.getByRole("button", { name: "团队成员" }));
    fireEvent.click(await screen.findByRole("link", { name: "创建第一个工作流成员" }));

    expect(window.location.pathname).toBe(
      "/teams/scope-1/t-alpha/members/new/workflow",
    );
  });

  it("does not assign an unrelated scope service to a Team with no members", async () => {
    (studioApi.getTeam as jest.Mock).mockResolvedValueOnce({
      ...mockCreateTeamSummary(),
      displayName: "test03",
      memberCount: 0,
    });
    (studioApi.listTeamMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-1",
      members: [],
      nextPageToken: null,
    });
    (scopeRuntimeApi.listServices as jest.Mock).mockResolvedValueOnce([
      {
        serviceKey: "scope-1:gagent-1",
        tenantId: "scope-1",
        appId: "default",
        namespace: "default",
        serviceId: "member-m-9833881c18e14c19aab60b2b9c7e998f",
        displayName: "gagent-1",
        defaultServingRevisionId: "rev-gagent",
        activeServingRevisionId: "rev-gagent",
        deploymentId: "dep-gagent",
        primaryActorId: "actor-gagent",
        deploymentStatus: "Active",
        endpoints: [],
        policyIds: [],
        updatedAt: "2026-05-21T09:00:00Z",
      },
    ]);

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(
      await screen.findByRole("heading", { level: 1, name: "test03" }),
    ).toBeTruthy();
    expect(await screen.findByText("暂无团队构成")).toBeTruthy();
    expect(screen.getByText("服务待配置")).toBeTruthy();
    expect(screen.getByText("当前还没有匹配到主服务入口")).toBeTruthy();
    expect(screen.queryByText("gagent-1")).toBeNull();
    expect(scopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalled();
  });

  it("uses the real Team roster when teamId is selected", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha?tab=members",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("Team Alpha Operator")).toBeTruthy();
    expect(await screen.findByText("Team Alpha Operator")).toBeTruthy();
    expect(screen.getByText("负责处理升级工单")).toBeTruthy();
    expect(screen.getByText("member-team-alpha")).toBeTruthy();
    expect(screen.getByText("alph...vice")).toBeTruthy();

    await waitFor(() => {
      expect(studioApi.listTeamMembers).toHaveBeenCalledWith("scope-1", "t-alpha");
    });
  });

  it("uses the real Team summary when teamId is selected", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(
      await screen.findByRole("heading", {
        level: 1,
        name: "Alpha Support Team",
      }),
    ).toBeTruthy();
    expect(screen.getByText("已启用")).toBeTruthy();
    expect((await screen.findAllByText("Team summary")).length).toBeGreaterThan(0);
    expect(screen.getAllByText("3 个成员").length).toBeGreaterThan(0);
    expect(screen.getAllByText("来自团队更新时间").length).toBeGreaterThan(0);
    expect(screen.getByText("启动状态")).toBeTruthy();
    expect(screen.getByText("团队构成")).toBeTruthy();
    expect(screen.getByText("配置明细")).toBeTruthy();
    expect(screen.queryByText("团队更新时间")).toBeNull();
    expect(screen.queryByText("生命周期")).toBeNull();

    await waitFor(() => {
      expect(studioApi.getTeam).toHaveBeenCalledWith("scope-1", "t-alpha");
    });
  });

  it("updates the real Team summary from the detail header", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("heading", {
      level: 1,
      name: "Alpha Support Team",
    });
    fireEvent.click(screen.getByRole("button", { name: "编辑团队" }));

    const nameInput = await screen.findByLabelText("编辑团队名称");
    expect(nameInput).toHaveValue("Alpha Support Team");
    fireEvent.change(nameInput, {
      target: { value: " Alpha Ops Team " },
    });
    fireEvent.change(screen.getByLabelText("编辑团队说明"), {
      target: { value: "   " },
    });
    fireEvent.click(screen.getByRole("button", { name: "保存团队" }));

    await waitFor(() => {
      expect(studioApi.updateTeam).toHaveBeenCalledWith({
        scopeId: "scope-1",
        teamId: "t-alpha",
        displayName: "Alpha Ops Team",
        description: null,
      });
    });
    expect(message.success).toHaveBeenCalledWith("团队已更新。");
    await waitFor(() => {
      expect(studioApi.getTeam).toHaveBeenCalledTimes(2);
    });
  });

  it("does not submit an empty Team name", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("heading", {
      level: 1,
      name: "Alpha Support Team",
    });
    fireEvent.click(screen.getByRole("button", { name: "编辑团队" }));
    fireEvent.change(await screen.findByLabelText("编辑团队名称"), {
      target: { value: "   " },
    });

    expect(screen.getByRole("button", { name: "保存团队" })).toBeDisabled();
    expect(studioApi.updateTeam).not.toHaveBeenCalled();
  });

  it("archives the Team without making archived Teams read-only", async () => {
    (studioApi.getTeam as jest.Mock)
      .mockResolvedValueOnce(mockCreateTeamSummary())
      .mockResolvedValue({
        ...mockCreateTeamSummary(),
        lifecycleStage: "archived",
      });
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect((await screen.findAllByRole("heading", { name: "Alpha Support Team" })).length).toBeGreaterThan(0);
    fireEvent.click(await screen.findByRole("button", { name: "团队更多操作" }));
    fireEvent.click(await screen.findByRole("menuitem", { name: "归档团队" }));
    expect(await screen.findByText("归档这支团队？")).toBeTruthy();
    expect(
      screen.getByText(
        "归档后，这支团队会从活跃成员清单中降权显示，但你仍然可以继续编辑配置并查看历史。",
      ),
    ).toBeTruthy();
    fireEvent.click(
      within(screen.getByRole("dialog", { name: "归档这支团队？" })).getByRole(
        "button",
        { name: "归档团队" },
      ),
    );

    await waitFor(() => {
      expect(studioApi.archiveTeam).toHaveBeenCalledWith("scope-1", "t-alpha");
    });
    expect(message.success).toHaveBeenCalledWith("团队已归档。");
    expect(screen.getByRole("button", { name: "编辑团队" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: "团队更多操作" })).toBeNull();
    expect(screen.queryByRole("menuitem", { name: "归档团队" })).toBeNull();
  });

  it("keeps archived Teams maintainable on first load", async () => {
    (studioApi.getTeam as jest.Mock).mockResolvedValueOnce({
      ...mockCreateTeamSummary(),
      lifecycleStage: "archived",
    });
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect((await screen.findAllByRole("heading", { name: "Alpha Support Team" })).length).toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "编辑团队" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: "团队更多操作" })).toBeNull();
    expect(screen.queryByRole("button", { name: "归档团队" })).toBeNull();
  });

  it("keeps the runtime overview when Team summary fails", async () => {
    (studioApi.getTeam as jest.Mock).mockRejectedValueOnce(
      new Error("Team summary failed"),
    );
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("启动状态")).toBeTruthy();
    expect(await screen.findByText("团队构成")).toBeTruthy();
    expect(await screen.findByText("配置明细")).toBeTruthy();
    expect(screen.queryByText("Team summary 暂不可用")).toBeNull();
    expect(
      screen.queryByText("当前仍会显示运行时视图；Team summary 暂时无法读取。"),
    ).toBeNull();
    expect(screen.queryByText("信任态势")).toBeNull();
    expect(screen.getByText("Support Escalation Triage")).toBeTruthy();

    await waitFor(() => {
      expect(studioApi.getTeam).toHaveBeenCalledWith("scope-1", "t-alpha");
    });
  });

  it("treats a just-created Team 404 as projection syncing and retries", async () => {
    jest.useFakeTimers();
    window.history.replaceState({}, "", "/teams/scope-1/t-alpha?tab=members");
    (studioApi.getTeam as jest.Mock)
      .mockRejectedValueOnce(createStudioApiStatusError("Not Found", 404))
      .mockResolvedValueOnce(mockCreateTeamSummary());
    (studioApi.listTeamMembers as jest.Mock)
      .mockRejectedValueOnce(createStudioApiStatusError("Not Found", 404))
      .mockResolvedValueOnce(mockCreateTeamMembersCatalog());

    const queryClient = createTestQueryClient();
    queryClient.setQueryData(
      ["teams", "team-summary", "scope-1", "t-alpha"],
      mockCreateTeamSummary(),
    );
    renderWithQueryClient(React.createElement(TeamDetailPage), queryClient);

    expect(await screen.findByText("Alpha Support Team")).toBeTruthy();
    expect(await screen.findByText("成员清单正在同步")).toBeTruthy();

    await act(async () => {
      jest.advanceTimersByTime(500);
    });

    expect(await screen.findByText("Team Alpha Operator")).toBeTruthy();
    expect(studioApi.getTeam).toHaveBeenCalledTimes(2);
    expect(studioApi.listTeamMembers).toHaveBeenCalledTimes(2);

    jest.useRealTimers();
  });

  it("drops stale service and run hints in favor of the requested workflow truth", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha?workflowId=workflow-1&serviceId=stale-service&runId=stale-run",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(
      await screen.findByRole("heading", {
        level: 1,
        name: "Alpha Support Team",
      }),
    ).toBeTruthy();
    expect(screen.queryByText("路由上下文已自动校正")).toBeNull();
  });

  it("falls back gracefully when the requested workflow is no longer visible", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha?workflowId=workflow-missing",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(
      await screen.findByRole("heading", { level: 1, name: "当前团队" }),
    ).toBeTruthy();
    expect(screen.queryByText("路由上下文已自动校正")).toBeNull();
  });
});
