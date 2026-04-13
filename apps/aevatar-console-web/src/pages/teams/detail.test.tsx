import { Grid } from "antd";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { loadDraftRunPayload } from "@/shared/runs/draftRunSession";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import TeamDetailPage from "./detail";

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

jest.mock("@/shared/api/servicesApi", () => ({
  servicesApi: {
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
    listServiceRuns: jest.fn(async () => mockCreateRunsCatalog()),
    getServiceRunAudit: jest.fn(async (scopeId: string, _serviceId: string, runId: string) =>
      mockCreateRunAudit(scopeId, runId),
    ),
  },
}));

jest.mock("@/shared/studio/api", () => ({
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
      homeDirectory: "/tmp/.aevatar",
      filePath: "/tmp/.aevatar/connectors.json",
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

describe("TeamDetailPage", () => {
  let useBreakpointSpy: jest.SpyInstance | null = null;

  beforeEach(() => {
    window.history.replaceState({}, "", "/teams/scope-1?scopeId=scope-1");
    useBreakpointSpy = jest.spyOn(Grid, "useBreakpoint").mockReturnValue({
      xs: false,
      sm: true,
      md: true,
      lg: true,
      xl: true,
      xxl: true,
    } as any);
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockImplementation(
      async () => mockCreateRunsCatalog(),
    );
    (scopeRuntimeApi.getServiceRunAudit as jest.Mock).mockImplementation(
      async (scopeId: string, _serviceId: string, runId: string) =>
        mockCreateRunAudit(scopeId, runId),
    );
  });

  afterEach(() => {
    useBreakpointSpy?.mockRestore();
    useBreakpointSpy = null;
  });

  it("renders the Team-first shell with health, compare, and governance modules", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("Team Health")).toBeTruthy();
    expect(await screen.findByText("What Changed")).toBeTruthy();
    expect(await screen.findByText("Human Handoff")).toBeTruthy();
    expect(await screen.findByText("Connected Systems")).toBeTruthy();
    expect(await screen.findByText("Trust Summary")).toBeTruthy();
    expect(await screen.findByText("Collaboration Canvas")).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "Open Team Builder" }).length).toBeGreaterThan(0);
    await waitFor(() => {
      expect(screen.getAllByText("Blocked").length).toBeGreaterThan(0);
      expect(screen.getByText("Human intervention is visible in the current run.")).toBeTruthy();
      expect(screen.getByText("Step deltas")).toBeTruthy();
      expect(screen.getByText("Approve escalation")).toBeTruthy();
      expect(screen.getByText("Recent runtime events")).toBeTruthy();
      expect(screen.getByText("From focus")).toBeTruthy();
      expect(screen.getByText("web-search")).toBeTruthy();
      expect(
        screen.getByText("2 team-scoped connector references across 1 workflow roles"),
      ).toBeTruthy();
      expect(
        screen.getByText("Used by current team roles triage_operator"),
      ).toBeTruthy();
      expect(screen.getByText("Referenced but undefined")).toBeTruthy();
      expect(screen.getByText("crm-sync")).toBeTruthy();
      expect(screen.getByRole("button", { name: "Open current run replay" })).toBeTruthy();
      expect(screen.getByRole("button", { name: "Inspect root actor" })).toBeTruthy();
      expect(screen.getAllByText("Delayed").length).toBeGreaterThan(0);
      expect(screen.getAllByText("Live").length).toBeGreaterThan(0);
    });
  });

  it("keeps the collaboration canvas ahead of the auxiliary segmented panel on narrow screens", async () => {
    useBreakpointSpy?.mockRestore();
    useBreakpointSpy = jest.spyOn(Grid, "useBreakpoint").mockReturnValue({
      xs: true,
      sm: true,
      md: false,
      lg: false,
      xl: false,
      xxl: false,
    } as any);

    renderWithQueryClient(React.createElement(TeamDetailPage));

    const collaborationHeading = await screen.findByText("Collaboration Canvas");
    const segmentedActivity = await screen.findByText("Activity · Delayed");

    expect(
      collaborationHeading.compareDocumentPosition(segmentedActivity) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(screen.getByText("Recent Activity")).toBeTruthy();
    expect(screen.queryByText("Team Health")).toBeNull();

    fireEvent.click(screen.getByText("Details · Delayed"));

    expect(await screen.findByText("Team Health")).toBeTruthy();
    expect(screen.queryByText("Recent Activity")).toBeNull();
  });

  it("surfaces team signal failures without leaking raw runtime errors", async () => {
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockRejectedValueOnce(
      new Error("No stub for /api/scopes/scope-1/services/default/runs"),
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(
      await screen.findByText("Some team signals are currently unavailable"),
    ).toBeTruthy();
    expect(
      screen.getByText("Recent team activity could not be loaded."),
    ).toBeTruthy();
    expect(screen.getByText("Activity unavailable")).toBeTruthy();
    expect(
      screen.getByText("Recent team activity could not be loaded for this team."),
    ).toBeTruthy();
    expect(
      screen.queryByText("No stub for /api/scopes/scope-1/services/default/runs"),
    ).toBeNull();
  });

  it("opens a playback run replay with observed session context", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByText("Approve escalation");
    fireEvent.click(screen.getByRole("button", { name: "Open current run replay" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/runtime/runs");
    });
    const draftKey = new URLSearchParams(window.location.search).get("draftKey");
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

  it("opens runtime explorer from the playback root actor action", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByText("Approve escalation");
    fireEvent.click(screen.getByRole("button", { name: "Inspect root actor" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/runtime/explorer");
    });
    const params = new URLSearchParams(window.location.search);
    expect(params.get("actorId")).toBe("actor-intake");
    expect(params.get("runId")).toBe("run-current");
    expect(params.get("scopeId")).toBe("scope-1");
    expect(params.get("serviceId")).toBe("default");
  });

  it("opens Team Builder in the current team context from the team detail actions", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByText("Trust Summary");
    fireEvent.click(screen.getAllByRole("button", { name: "Open Team Builder" })[0]);

    await waitFor(() => {
      expect(window.location.pathname).toBe("/studio");
    });
    const params = new URLSearchParams(window.location.search);
    expect(params.get("scopeId")).toBe("scope-1");
    if (params.get("workflow")) {
      expect(params.get("workflow")).toBe("workflow-1");
      expect(params.get("tab")).toBe("studio");
    } else {
      expect(params.get("tab")).toBe("workflows");
    }
  });

  it("lets member selection drive the inspector and explorer focus", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByText("Member Focus");
    await screen.findByText("actor-risk");
    fireEvent.click(
      screen.getByRole("button", {
        name: "Focus member RiskReviewAgent actor-risk",
      }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Open Topology" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/runtime/explorer");
    });
    const params = new URLSearchParams(window.location.search);
    expect(params.get("actorId")).toBe("actor-risk");
    expect(params.get("scopeId")).toBe("scope-1");
  });
});
