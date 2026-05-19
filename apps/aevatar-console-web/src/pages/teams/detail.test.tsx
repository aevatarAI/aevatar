import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { message } from "antd";
import React from "react";
import { scopesApi } from "@/shared/api/scopesApi";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { runtimeActorsApi } from "@/shared/api/runtimeActorsApi";
import { runtimeGAgentApi } from "@/shared/api/runtimeGAgentApi";
import { history } from "@/shared/navigation/history";
import { studioApi } from "@/shared/studio/api";
import { loadDraftRunPayload } from "@/shared/runs/draftRunSession";
import {
  createTestQueryClient,
  renderWithQueryClient,
} from "../../../tests/reactQueryTestUtils";
import TeamDetailPage from "./detail";

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
        description: "真实 Team roster 成员",
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

function mockCreateTeamSummary() {
  return {
    teamId: "t-alpha",
    scopeId: "scope-1",
    displayName: "Alpha Support Team",
    description: "Team authority summary",
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
    listServices: jest.fn(async () => [
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
    ]),
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
      scopeId: "scope-1",
      teamId: "t-alpha",
      commandId: "cmd-update",
      ackStage: "accepted",
      acceptedAtUtc: "2026-05-01T08:06:00Z",
    })),
    archiveTeam: jest.fn(async () => ({
      scopeId: "scope-1",
      teamId: "t-alpha",
      commandId: "cmd-archive",
      ackStage: "accepted",
      acceptedAtUtc: "2026-05-01T08:07:00Z",
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
    window.history.replaceState({}, "", "/teams/scope-1/t-alpha");
    (scopesApi.listWorkflows as jest.Mock).mockClear();
    (scopesApi.listScripts as jest.Mock).mockClear();
    (runtimeGAgentApi.listActors as jest.Mock).mockClear();
    (runtimeActorsApi.getActorGraphEnriched as jest.Mock).mockClear();
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
      scopeId: "scope-1",
      teamId: "t-alpha",
      commandId: "cmd-update",
      ackStage: "accepted",
      acceptedAtUtc: "2026-05-01T08:06:00Z",
    }));
    (studioApi.archiveTeam as jest.Mock).mockReset();
    (studioApi.archiveTeam as jest.Mock).mockImplementation(async () => ({
      scopeId: "scope-1",
      teamId: "t-alpha",
      commandId: "cmd-archive",
      ackStage: "accepted",
      acceptedAtUtc: "2026-05-01T08:07:00Z",
    }));
    (studioApi.listTeamMembers as jest.Mock).mockReset();
    (studioApi.listTeamMembers as jest.Mock).mockImplementation(
      async () => mockCreateTeamMembersCatalog(),
    );
  });

  it("renders no-team-selected state without detail data flows for scope-only links", async () => {
    window.history.replaceState({}, "", "/teams/scope-1");

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("未选择团队")).toBeTruthy();
    expect(
      screen.getByText("当前链接只有工作区上下文，没有具体 Team 标识。返回团队列表后选择一个团队。"),
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
        return node?.textContent === "Aevatar / Teams / 团队详情 / 概览";
      }),
    ).toBeTruthy();
    expect(screen.getByRole("link", { name: "Aevatar" })).toBeTruthy();
    expect(screen.getByRole("link", { name: "Teams" })).toBeTruthy();
    expect(screen.getByText("scopeId")).toBeTruthy();
    expect(screen.getByText("scope-1")).toBeTruthy();
    const currentPostureHeading = screen.getByText("当前态势");
    const teamAuthorityHeading = screen.getByText("Team authority");
    const trustHeading = screen.getByText("信任态势");
    const governanceHeading = screen.getByText("治理快照");
    const compareHeading = screen.getByText("Run Compare / Change Diff");
    expect(screen.getByText("团队构成")).toBeTruthy();
    expect(screen.getByText("运行摘要")).toBeTruthy();
    expect(teamAuthorityHeading).toBeTruthy();
    expect(currentPostureHeading).toBeTruthy();
    expect(trustHeading).toBeTruthy();
    expect(
      await screen.findByText("Comparing run run-current against baseline run-good."),
    ).toBeTruthy();
    expect(screen.getByText("需要人工处理后继续")).toBeTruthy();
    expect(screen.getByText("等待人工")).toBeTruthy();
    expect(governanceHeading).toBeTruthy();
    expect(compareHeading).toBeTruthy();
    expect(
      currentPostureHeading.compareDocumentPosition(trustHeading) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(
      governanceHeading.compareDocumentPosition(compareHeading) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(screen.getByText("Runtime deltas")).toBeTruthy();
    expect(await screen.findByText("Step deltas")).toBeTruthy();
    expect(await screen.findByText("Handoff deltas")).toBeTruthy();
    expect(screen.getByRole("button", { name: "处理等待 Run" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "服务映射" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "治理绑定" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "部署记录" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "高级编辑" })).toBeTruthy();
    expect(studioApi.getTeam).toHaveBeenCalledWith("scope-1", "t-alpha");
  });

  it("keeps compare honest when no successful baseline exists", async () => {
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockResolvedValueOnce({
      ...mockCreateRunsCatalog(),
      runs: [mockCreateRunsCatalog().runs[0]],
    });

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("信任态势")).toBeTruthy();
    expect(
      (await screen.findAllByText("No successful baseline is available yet.")).length,
    ).toBeGreaterThan(0);
    expect(screen.getByText("等待基线")).toBeTruthy();
    expect(screen.getByText("无基线")).toBeTruthy();
    expect(screen.getByText("暂无成功基线运行")).toBeTruthy();
  });

  it("keeps selected run facts aligned without inventing a failed baseline", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha?runId=run-good",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(
      (await screen.findAllByText("No successful baseline is available yet.")).length,
    ).toBeGreaterThan(0);
    expect(
      screen.queryByText("Comparing run run-current against baseline run-good."),
    ).toBeNull();

    await waitFor(() => {
      const auditedRunIds = (scopeRuntimeApi.getServiceRunAudit as jest.Mock).mock.calls.map(
        (call) => call[2],
      );
      expect(auditedRunIds).toContain("run-good");
      expect(auditedRunIds).not.toContain("run-current");
    });
  });

  it("uses the Team authority display name for the team heading", async () => {
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
    expect(screen.getByText("scopeId")).toBeTruthy();
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

  it("shows full raw identifiers inside overview tooltips", async () => {
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

    await screen.findByText("运行摘要");

    const revisionNote = await screen.findByText(/revisionId ·/);

    fireEvent.mouseEnter(revisionNote);

    expect(await screen.findByText(`revisionId · ${longRevisionId}`)).toBeTruthy();
  });

  it("returns to the teams list when clicking the breadcrumb teams link", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("link", { name: "Teams" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams");
      expect(window.location.search).toContain("scopeId=scope-1");
    });
  });

  it("returns to the teams list when clicking the breadcrumb aevatar link", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("link", { name: "Aevatar" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams");
      expect(window.location.search).toContain("scopeId=scope-1");
    });
  });

  it("switches tabs inside the detail page", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "团队成员" }));

    expect(await screen.findByText("真实 Team roster")).toBeTruthy();
    expect(window.location.search).toContain("tab=members");
    expect(window.location.search).not.toContain("step=bind");
  });

  it("canonicalizes legacy service deep links into member-first detail routes", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha?serviceId=default&tab=events",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("当前任务事件流")).toBeTruthy();

    await waitFor(() => {
      const params = new URLSearchParams(window.location.search);
      expect(params.get("memberId")).toBe("member-support");
      expect(params.get("serviceId")).toBe("default");
      expect(params.get("tab")).toBe("events");
    });

    await waitFor(() => {
      expect(scopeRuntimeApi.listMemberRuns).toHaveBeenCalledWith(
        "scope-1",
        "member-support",
        expect.objectContaining({ take: 12 }),
      );
    });
  });

  it("shows configuration details in the overview", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });

    expect(await screen.findByText("配置明细")).toBeTruthy();
    expect(screen.getAllByText("绑定方式").length).toBeGreaterThan(0);
    expect(screen.getAllByText("连接器引用").length).toBeGreaterThan(0);
  });

  it("shows a readable team members view", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "团队成员" }));

    expect(await screen.findByText("参与者结构")).toBeTruthy();
    expect(screen.getByText("运行时参与者身份")).toBeTruthy();
    expect(screen.getByText("当前焦点")).toBeTruthy();
    expect(screen.getByText("可见 Actor")).toBeTruthy();
    expect(screen.getAllByText("actorId").length).toBeGreaterThan(0);
    expect(screen.getByText("actor-intake")).toBeTruthy();
    expect(screen.getAllByText("serviceId").length).toBeGreaterThan(0);
    expect(screen.getAllByText("default").length).toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "打开 Services" })).toBeTruthy();
  });

  it("uses the real Team roster when teamId is selected", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha?tab=members",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("真实 Team roster")).toBeTruthy();
    expect(await screen.findByText("Team Alpha Operator")).toBeTruthy();
    expect(screen.getByText("真实 Team roster 成员")).toBeTruthy();
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
    expect((await screen.findAllByText("Team authority summary")).length).toBeGreaterThan(0);
    expect(screen.getAllByText("3 个成员").length).toBeGreaterThan(0);
    expect(screen.getAllByText("来自 Team authority 更新时间").length).toBeGreaterThan(0);
    expect(screen.getByText("Team 更新时间")).toBeTruthy();
    expect(screen.getByText("生命周期")).toBeTruthy();

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
    fireEvent.click(screen.getByRole("button", { name: "Edit Team" }));

    const nameInput = await screen.findByLabelText("Edit team name");
    expect(nameInput).toHaveValue("Alpha Support Team");
    fireEvent.change(nameInput, {
      target: { value: " Alpha Ops Team " },
    });
    fireEvent.change(screen.getByLabelText("Edit team description"), {
      target: { value: "   " },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save Team" }));

    await waitFor(() => {
      expect(studioApi.updateTeam).toHaveBeenCalledWith({
        scopeId: "scope-1",
        teamId: "t-alpha",
        displayName: "Alpha Ops Team",
        description: null,
      });
    });
    expect(message.success).toHaveBeenCalledWith("Team updated.");
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
    fireEvent.click(screen.getByRole("button", { name: "Edit Team" }));
    fireEvent.change(await screen.findByLabelText("Edit team name"), {
      target: { value: "   " },
    });

    expect(screen.getByRole("button", { name: "Save Team" })).toBeDisabled();
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
    fireEvent.click(screen.getByRole("button", { name: "Archive Team" }));
    expect(await screen.findByText("Archive this Team?")).toBeTruthy();
    expect(
      screen.getByText(
        "This marks the Team as archived and de-emphasizes it in the active roster. You can still edit its configuration and view its history.",
      ),
    ).toBeTruthy();
    fireEvent.click(
      within(screen.getByRole("dialog", { name: "Archive this Team?" })).getByRole(
        "button",
        { name: "Archive Team" },
      ),
    );

    await waitFor(() => {
      expect(studioApi.archiveTeam).toHaveBeenCalledWith("scope-1", "t-alpha");
    });
    expect(message.success).toHaveBeenCalledWith("Team archived.");
    expect(screen.getByRole("button", { name: "Edit Team" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: "Archive Team" })).toBeNull();
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
    expect(screen.getByRole("button", { name: "Edit Team" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: "Archive Team" })).toBeNull();
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

    expect((await screen.findAllByText("Team summary 不可用")).length).toBeGreaterThan(0);
    expect(screen.getByText("Team summary 暂不可用")).toBeTruthy();
    expect(
      screen.getByText("当前仍会显示运行时视图；Team authority summary 暂时无法读取。"),
    ).toBeTruthy();
    expect(await screen.findByText("当前态势")).toBeTruthy();
    expect(await screen.findByText("信任态势")).toBeTruthy();
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
    expect(await screen.findByText("Team roster 正在同步")).toBeTruthy();

    await act(async () => {
      jest.advanceTimersByTime(500);
    });

    expect(await screen.findByText("Team Alpha Operator")).toBeTruthy();
    expect(studioApi.getTeam).toHaveBeenCalledTimes(2);
    expect(studioApi.listTeamMembers).toHaveBeenCalledTimes(2);

    jest.useRealTimers();
  });

  it("shows a team-first event stream with member mapping", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "事件流" }));

    expect(await screen.findByText("当前任务事件流")).toBeTruthy();
    expect(screen.getByText("本次 Run 成员映射")).toBeTruthy();
    expect(await screen.findByText("切换 Run")).toBeTruthy();
    expect(screen.getByRole("button", { name: "打开完整审计" })).toBeTruthy();
    expect((await screen.findAllByText(/risk_review/)).length).toBeGreaterThan(0);
  });

  it("switches runs inside the event stream", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "事件流" }));
    await screen.findByText("当前任务事件流");

    fireEvent.click(await screen.findByRole("button", { name: "切换到 run-good" }));

    await waitFor(() => {
      expect(window.location.search).toContain("runId=run-good");
    });
    expect(await screen.findByText("LLM_CALL")).toBeTruthy();
  });

  it("syncs tab and run state when the route changes after mount", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });

    act(() => {
      history.push("/teams/scope-1/t-alpha?tab=events&runId=run-good");
    });

    expect(await screen.findByText("当前任务事件流")).toBeTruthy();
    expect(await screen.findByText("LLM_CALL")).toBeTruthy();
  });

  it("surfaces team signal failures without leaking raw runtime errors", async () => {
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockRejectedValueOnce(
      new Error("No stub for /api/scopes/scope-1/services/default/runs"),
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByRole("button", { name: "服务映射" })).toBeTruthy();
    expect(screen.queryByText("部分团队信号暂不可用")).toBeNull();
    expect(screen.queryByText("最近团队运行信号暂时无法加载。")).toBeNull();
    expect(
      screen.queryByText("No stub for /api/scopes/scope-1/services/default/runs"),
    ).toBeNull();
  });

  it("opens a playback run replay with observed session context", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "事件流" }));
    await screen.findAllByText(/risk_review/);
    fireEvent.click(screen.getAllByRole("button", { name: "处理等待 Run" })[0]);

    await waitFor(() => {
      expect(window.location.pathname).toBe("/runtime/runs");
    });
    const runsParams = new URLSearchParams(window.location.search);
    expect(runsParams.get("runId")).toBe("run-current");
    expect(runsParams.get("scopeId")).toBe("scope-1");
    expect(runsParams.get("serviceOverrideId")).toBe("default");
    const draftKey = runsParams.get("draftKey");
    expect(draftKey).toBeTruthy();
    expect(loadDraftRunPayload(draftKey)).toMatchObject({
      kind: "observed_run_session",
      actorId: "actor-intake",
      endpointId: "chat",
      routeName: "support-triage",
      runId: "run-current",
      scopeId: "scope-1",
      serviceOverrideId: "default",
    });
  });

  it("opens runtime explorer from the service mapping action", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "事件拓扑" }));
    await screen.findByText("团队事件路径");
    fireEvent.click(screen.getAllByRole("button", { name: "服务映射" })[0]);

    await waitFor(() => {
      expect(window.location.pathname).toBe("/runtime/explorer/detail");
    });
    const params = new URLSearchParams(window.location.search);
    expect(params.get("actorId")).toBe("actor-intake");
    expect(params.get("runId")).toBe("run-current");
    expect(params.get("scopeId")).toBe("scope-1");
    expect(params.get("serviceId")).toBe("default");
  });

  it("opens Platform governance and deployments from top attention actions", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "治理绑定" })).toBeEnabled();
      expect(screen.getByRole("button", { name: "部署记录" })).toBeEnabled();
    });

    fireEvent.click(screen.getByRole("button", { name: "治理绑定" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/governance");
    });
    let params = new URLSearchParams(window.location.search);
    expect(params.get("tenantId")).toBe("scope-1");
    expect(params.get("appId")).toBe("default");
    expect(params.get("namespace")).toBe("default");
    expect(params.get("serviceId")).toBe("default");
    expect(params.get("revisionId")).toBe("rev-2");
    expect(params.get("view")).toBe("bindings");

    act(() => {
      history.push("/teams/scope-1/t-alpha");
    });
    await screen.findByRole("button", { name: "部署记录" });
    fireEvent.click(screen.getByRole("button", { name: "部署记录" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/deployments");
    });
    params = new URLSearchParams(window.location.search);
    expect(params.get("tenantId")).toBe("scope-1");
    expect(params.get("appId")).toBe("default");
    expect(params.get("namespace")).toBe("default");
    expect(params.get("serviceId")).toBe("default");
    expect(params.get("deploymentId")).toBe("dep-2");
  });

  it("opens Mission Control from the team event stream with run context", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "事件流" }));
    await screen.findByText("当前任务事件流");
    await screen.findByText(/Current playback is centered on risk_review/);
    fireEvent.click(await screen.findByRole("button", { name: "打开 Mission Control" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/runtime/mission-control");
    });
    const params = new URLSearchParams(window.location.search);
    expect(params.get("actorId")).toBe("actor-intake");
    expect(params.get("autoStream")).toBe("true");
    expect(params.get("prompt")).toBe("hello");
    expect(params.get("runId")).toBe("run-current");
    expect(params.get("scopeId")).toBe("scope-1");
    expect(params.get("serviceId")).toBe("default");
  });

  it("updates the topology depth selection when the focus member is available", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "事件拓扑" }));
    await screen.findByText("团队事件路径");

    const nearButton = screen.getByRole("button", { name: "近邻" });
    const expandButton = screen.getByRole("button", { name: "扩展" });
    const panoramaButton = screen.getByRole("button", { name: "全景" });

    expect(expandButton).toHaveAttribute("aria-pressed", "true");
    expect(nearButton).toHaveAttribute("aria-pressed", "false");
    expect(panoramaButton).toHaveAttribute("aria-pressed", "false");

    fireEvent.click(panoramaButton);

    expect(panoramaButton).toHaveAttribute("aria-pressed", "true");
    expect(expandButton).toHaveAttribute("aria-pressed", "false");
  });

  it("disables topology controls when the current team has no focus member yet", async () => {
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce({
      available: false,
      scopeId: "scope-1",
      serviceId: "",
      displayName: "",
      serviceKey: "",
      defaultServingRevisionId: "",
      activeServingRevisionId: "",
      deploymentId: "",
      deploymentStatus: "",
      primaryActorId: "",
      updatedAt: "2026-04-09T09:00:00Z",
      revisions: [],
    });
    (scopeRuntimeApi.listServices as jest.Mock).mockResolvedValueOnce([]);
    (runtimeGAgentApi.listActors as jest.Mock).mockResolvedValueOnce([]);

    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "事件拓扑" }));
    await screen.findByText("团队事件路径");

    expect(
      screen.getByText("当前还没有可用的团队成员焦点，待成员或运行信号可见后再切换视角。"),
    ).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "服务映射" })[0]).toBeDisabled();
    expect(screen.getByRole("button", { name: "近邻" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "扩展" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "全景" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "打开平台拓扑" })).toBeDisabled();
    expect(
      screen.getByText("当前还没有可用的团队成员焦点，所以暂时没有可展开的事件拓扑关系。"),
    ).toBeTruthy();
  });

  it("opens platform workbenches from members", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "团队成员" }));
    await screen.findByText("运行时参与者身份");
    fireEvent.click(screen.getByRole("button", { name: "打开 Services" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/services");
    });
    expect(window.location.search).toContain("tenantId=scope-1");
    expect(window.location.search).toContain("serviceId=default");
  });

  it("opens Studio in the current team context from the top actions", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha?workflowId=workflow-1",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    await screen.findByRole("heading", {
      level: 1,
      name: "Alpha Support Team",
    });
    await waitFor(() => {
      expect(studioApi.listTeamMembers).toHaveBeenCalledWith("scope-1", "t-alpha");
    });
    await waitFor(() => {
      expect(scopesApi.getWorkflowDetail).toHaveBeenCalledWith("scope-1", "workflow-1");
    });

    const pushSpy = jest.spyOn(history, "push").mockImplementation(() => undefined);
    fireEvent.click(screen.getByRole("button", { name: "高级编辑" }));

    expect(pushSpy).toHaveBeenCalled();
    const pushedHref = pushSpy.mock.calls.at(-1)?.[0] ?? "";
    const pushedUrl = new URL(pushedHref, window.location.origin);
    expect(pushedUrl.pathname).toBe("/studio");
    const params = pushedUrl.searchParams;
    expect(params.get("scopeId")).toBe("scope-1");
    expect(params.get("teamId")).toBe("t-alpha");
    expect(params.get("member")).toBe("member:member-team-alpha");
    expect(params.get("memberId")).toBeNull();
    expect(params.get("focus")).toBe("workflow:workflow-1");
    expect(params.get("tab")).toBe("studio");
    pushSpy.mockRestore();
  });

  it("keeps an explicit Team member when opening Studio from top actions", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha?memberId=member-support&workflowId=workflow-1",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    await waitFor(() => {
      expect(studioApi.listTeamMembers).toHaveBeenCalledWith("scope-1", "t-alpha");
    });

    const pushSpy = jest.spyOn(history, "push").mockImplementation(() => undefined);
    fireEvent.click(screen.getByRole("button", { name: "高级编辑" }));

    expect(pushSpy).toHaveBeenCalled();
    const pushedHref = pushSpy.mock.calls.at(-1)?.[0] ?? "";
    const pushedUrl = new URL(pushedHref, window.location.origin);
    expect(pushedUrl.pathname).toBe("/studio");
    const params = pushedUrl.searchParams;
    expect(params.get("scopeId")).toBe("scope-1");
    expect(params.get("teamId")).toBe("t-alpha");
    expect(params.get("member")).toBe("member:member-support");
    expect(params.get("memberId")).toBeNull();
    expect(params.get("focus")).toBe("workflow:workflow-1");
    expect(params.get("tab")).toBe("studio");
    pushSpy.mockRestore();
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
