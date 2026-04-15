import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { studioApi } from "@/shared/studio/api";
import { loadDraftRunPayload } from "@/shared/runs/draftRunSession";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import TeamDetailPage from "./detail";

jest.mock("@/shared/graphs/GraphCanvas", () => ({
  __esModule: true,
  default: () => {
    const React = require("react");
    return React.createElement("div", null, "Graph canvas");
  },
}));

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
  beforeEach(() => {
    window.history.replaceState({}, "", "/teams/scope-1?scopeId=scope-1");
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockReset();
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockImplementation(
      async () => mockCreateRunsCatalog(),
    );
    (scopeRuntimeApi.getServiceRunAudit as jest.Mock).mockReset();
    (scopeRuntimeApi.getServiceRunAudit as jest.Mock).mockImplementation(
      async (scopeId: string, _serviceId: string, runId: string) =>
        mockCreateRunAudit(scopeId, runId),
    );
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
    expect(screen.getByText("Team scope-1")).toBeTruthy();
    expect(screen.getByText("团队构成")).toBeTruthy();
    expect(screen.getByText("运行摘要")).toBeTruthy();
    expect(screen.getByText("当前态势")).toBeTruthy();
    expect(screen.getByRole("button", { name: "运行记录" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "服务映射" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "高级编辑" })).toBeTruthy();
  });

  it("shows full raw identifiers inside overview tooltips", async () => {
    const longRevisionId =
      "rev-20260414154556-4d89bc2a3bf347f8b3bde41d716964f3";

    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce({
      available: true,
      scopeId: "scope-1",
      serviceId: "default",
      displayName: "Support Escalation Triage",
      serviceKey: "scope-1:default",
      defaultServingRevisionId: longRevisionId,
      activeServingRevisionId: longRevisionId,
      deploymentId: "dep-2",
      deploymentStatus: "Active",
      primaryActorId: "actor-intake",
      updatedAt: "2026-04-09T09:00:00Z",
      revisions: [
        {
          revisionId: longRevisionId,
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
    });

    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByText("运行摘要");

    const revisionNote = await screen.findByText((_, node) => {
      return node?.tagName === "SPAN" && (node.textContent || "").includes("revisionId ·");
    });

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
    fireEvent.click(screen.getByRole("button", { name: "Bindings" }));

    expect(await screen.findByText("当前绑定与治理摘要")).toBeTruthy();
    expect(screen.getByText("Bindings 与连接能力")).toBeTruthy();
    expect(screen.getByText("当前选中绑定")).toBeTruthy();
    expect(screen.getByRole("button", { name: "选择绑定 web-search" })).toBeTruthy();
    expect(window.location.search).toContain("tab=bindings");
  });

  it("shows the team asset view with workflow and script entries", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "Assets" }));

    expect(await screen.findByText("当前 Team 资产")).toBeTruthy();
    expect(screen.getAllByText("Workflow 资产").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Script 资产").length).toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "打开 workflow Support Escalation Triage" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "打开 script script-1" })).toBeTruthy();
  });

  it("shows a team-first configuration view", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "配置" }));

    expect(await screen.findByText("当前配置主线")).toBeTruthy();
    expect(screen.getByText("当前配置明细")).toBeTruthy();
    expect(screen.getByText("继续调整这支团队")).toBeTruthy();
    expect(screen.getAllByText("绑定方式").length).toBeGreaterThan(0);
    expect(screen.getAllByText("连接器引用").length).toBeGreaterThan(0);
    expect(screen.getAllByRole("button", { name: "高级编辑" }).length).toBeGreaterThan(0);
  });

  it("shows a readable team members view", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "团队成员" }));

    expect(await screen.findByText("参与者结构")).toBeTruthy();
    expect(screen.getByText("运行时参与者身份")).toBeTruthy();
    expect(screen.getByText("当前焦点")).toBeTruthy();
    expect(screen.getByText("可见 Actor")).toBeTruthy();
    expect(screen.getByText("actorId · actor-intake")).toBeTruthy();
    expect(screen.getAllByText("serviceId · default").length).toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "打开 Services" })).toBeTruthy();
  });

  it("shows a team-first event stream with member mapping", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "事件流" }));

    expect(await screen.findByText("当前任务事件流")).toBeTruthy();
    expect(screen.getByText("本次 Run 成员映射")).toBeTruthy();
    expect(await screen.findByText("切换 Run")).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "运行记录" }).length).toBeGreaterThan(0);
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
    fireEvent.click(screen.getAllByRole("button", { name: "本次对话" })[0]);

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

  it("opens runtime explorer from the service mapping action", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "事件拓扑" }));
    await screen.findByText("团队事件路径");
    fireEvent.click(screen.getAllByRole("button", { name: "服务映射" })[0]);

    await waitFor(() => {
      expect(window.location.pathname).toBe("/runtime/explorer");
    });
    const params = new URLSearchParams(window.location.search);
    expect(params.get("actorId")).toBe("actor-intake");
    expect(params.get("runId")).toBe("run-current");
    expect(params.get("scopeId")).toBe("scope-1");
    expect(params.get("serviceId")).toBe("default");
  });

  it("opens platform workbenches from members and bindings", async () => {
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

    cleanup();
    window.history.replaceState({}, "", "/teams/scope-1?scopeId=scope-1");
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "Bindings" }));
    await screen.findByText("当前绑定与治理摘要");
    fireEvent.click(screen.getByRole("button", { name: "打开 Governance" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/governance");
    });
    expect(window.location.search).toContain("serviceId=default");
    expect(window.location.search).toContain("view=bindings");
  });

  it("opens Studio in the current team context from the top actions", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "高级编辑" }));

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

  it("opens workflow and script Studio deep links from assets with scope context", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("button", { name: "服务映射" });
    fireEvent.click(screen.getByRole("button", { name: "Assets" }));
    await screen.findByText("当前 Team 资产");

    fireEvent.click(screen.getByRole("button", { name: "打开 workflow Support Escalation Triage" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/studio");
    });
    expect(window.location.search).toContain("scopeId=scope-1");
    expect(window.location.search).toContain("workflow=workflow-1");

    cleanup();
    window.history.replaceState({}, "", "/teams/scope-1?scopeId=scope-1&tab=assets");
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByText("当前 Team 资产");
    fireEvent.click(await screen.findByRole("button", { name: "打开 script script-1" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/studio");
    });
    expect(window.location.search).toContain("scopeId=scope-1");
    expect(window.location.search).toContain("script=script-1");
    expect(window.location.search).toContain("tab=scripts");
  });

  it("drops stale service and run hints in favor of the requested workflow truth", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1?workflowId=workflow-1&serviceId=stale-service&runId=stale-run",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("Support Escalation Triage")).toBeTruthy();
    expect(screen.queryByText("路由上下文已自动校正")).toBeNull();
  });

  it("falls back gracefully when the requested workflow is no longer visible", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1?workflowId=workflow-missing",
    );

    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("Support Escalation Triage")).toBeTruthy();
    expect(screen.queryByText("路由上下文已自动校正")).toBeNull();
  });
});
