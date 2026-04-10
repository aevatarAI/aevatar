import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { history } from "@/shared/navigation/history";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import TeamDetailPage from "./index";

jest.mock("@/shared/api/runtimeGAgentApi", () => ({
  runtimeGAgentApi: {
    getScopeBinding: jest.fn(),
    listActors: jest.fn(),
  },
}));

jest.mock("@/shared/api/servicesApi", () => ({
  servicesApi: {
    listServices: jest.fn(),
  },
}));

jest.mock("@/shared/api/scopesApi", () => ({
  scopesApi: {
    listWorkflows: jest.fn(),
    listScripts: jest.fn(),
  },
}));

jest.mock("@/shared/api/runtimeActorsApi", () => ({
  runtimeActorsApi: {
    getActorGraphEnriched: jest.fn(),
  },
}));

jest.mock("@/shared/api/scopeRuntimeApi", () => ({
  scopeRuntimeApi: {
    listServiceRuns: jest.fn(),
    getServiceRunAudit: jest.fn(),
    getServiceBindings: jest.fn(),
  },
}));

jest.mock("@/shared/graphs/GraphCanvas", () => ({
  __esModule: true,
  default: () => {
    const mockReact = require("react");
    return mockReact.createElement("div", null, "GraphCanvas");
  },
}));

import { runtimeActorsApi } from "@/shared/api/runtimeActorsApi";
import { runtimeGAgentApi } from "@/shared/api/runtimeGAgentApi";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { scopesApi } from "@/shared/api/scopesApi";
import { servicesApi } from "@/shared/api/servicesApi";

describe("TeamDetailPage", () => {
  beforeEach(() => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-a?tab=advanced&serviceId=service-alpha&runId=run-1",
    );
    jest.clearAllMocks();

    (runtimeGAgentApi.getScopeBinding as jest.Mock).mockResolvedValue({
      available: true,
      scopeId: "scope-a",
      serviceId: "service-alpha",
      displayName: "Alpha Team",
      serviceKey: "scope-a/default",
      defaultServingRevisionId: "rev-1",
      activeServingRevisionId: "rev-1",
      deploymentId: "deploy-1",
      deploymentStatus: "Ready",
      primaryActorId: "actor://team-alpha",
      updatedAt: "2026-04-09T08:00:00Z",
      revisions: [
        {
          revisionId: "rev-1",
          implementationKind: "workflow",
          status: "Ready",
          artifactHash: "artifact-1",
          failureReason: "",
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: "Ready",
          deploymentId: "deploy-1",
          primaryActorId: "actor://team-alpha",
          createdAt: "2026-04-09T07:00:00Z",
          preparedAt: "2026-04-09T07:05:00Z",
          publishedAt: "2026-04-09T07:10:00Z",
          retiredAt: null,
          workflowName: "workflow-alpha",
          workflowDefinitionActorId: "definition://workflow-alpha",
          inlineWorkflowCount: 1,
          scriptId: "",
          scriptRevision: "",
          scriptDefinitionActorId: "",
          scriptSourceHash: "",
          staticActorTypeName: "",
        },
      ],
    });
    (runtimeGAgentApi.listActors as jest.Mock).mockResolvedValue([
      {
        gAgentType: "Tests.WorkflowMember",
        actorIds: ["actor://team-alpha", "actor://helper"],
      },
    ]);
    (servicesApi.listServices as jest.Mock).mockResolvedValue([
      {
        serviceId: "service-alpha",
        serviceKey: "scope-a/default",
        displayName: "Alpha Assistant",
        deploymentStatus: "Ready",
        updatedAt: "2026-04-09T08:00:00Z",
        endpoints: [{ endpointId: "chat" }],
        primaryActorId: "actor://team-alpha",
        activeServingRevisionId: "rev-1",
        defaultServingRevisionId: "rev-1",
      },
    ]);
    (scopesApi.listWorkflows as jest.Mock).mockResolvedValue([
      {
        scopeId: "scope-a",
        workflowId: "workflow-alpha",
        displayName: "Alpha Workflow",
        workflowName: "workflow-alpha",
        serviceKey: "scope-a/default",
        actorId: "actor://team-alpha",
        activeRevisionId: "rev-1",
        deploymentStatus: "Published",
        deploymentId: "deploy-1",
        updatedAt: "2026-04-09T08:00:00Z",
      },
    ]);
    (scopesApi.listScripts as jest.Mock).mockResolvedValue([
      {
        scopeId: "scope-a",
        scriptId: "script-alpha",
        catalogActorId: "catalog://script-alpha",
        definitionActorId: "definition://script-alpha",
        activeRevision: "rev-1",
        activeSourceHash: "hash-1",
        updatedAt: "2026-04-09T08:00:00Z",
      },
    ]);
    (runtimeActorsApi.getActorGraphEnriched as jest.Mock).mockResolvedValue({
      snapshot: {
        actorId: "actor://team-alpha",
      },
      subgraph: {
        rootNodeId: "actor://team-alpha",
        nodes: [
          {
            nodeId: "actor://team-alpha",
            nodeType: "WorkflowRun",
            properties: {
              workflowName: "workflow-alpha",
              stepId: "",
              stepType: "",
              targetRole: "",
            },
          },
        ],
        edges: [],
      },
    });
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockResolvedValue({
      runs: [
        {
          runId: "run-1",
          actorId: "actor://team-alpha",
          completionStatus: "Completed",
        },
      ],
    });
    (scopeRuntimeApi.getServiceRunAudit as jest.Mock).mockResolvedValue({
      audit: {
        summary: {
          totalSteps: 3,
          requestedSteps: 3,
          completedSteps: 3,
          roleReplyCount: 2,
        },
        timeline: [
          {
            timestamp: "2026-04-09T08:01:00Z",
            eventType: "RunStarted",
            agentId: "actor://team-alpha",
            stepId: "step-1",
            message: "Run started",
          },
        ],
      },
    });
    (scopeRuntimeApi.getServiceBindings as jest.Mock).mockResolvedValue({
      bindings: [
        {
          bindingId: "binding-1",
          displayName: "Search Connector",
          retired: false,
          connectorRef: {
            connectorType: "http",
            connectorId: "search",
          },
          targetKind: "workflow",
          targetName: "workflow-alpha",
        },
      ],
    });
  });

  it("renders the team detail tabs and aggregated team content", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("高级编辑")).toBeTruthy();
    expect(screen.getByText("团队构建器入口")).toBeTruthy();
    expect(
      screen.getAllByRole("button", { name: "打开团队构建器" }).length,
    ).toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "行为定义" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "脚本行为" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Agent 角色" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "集成" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "测试运行" })).toBeTruthy();

    await waitFor(() => {
      expect(runtimeGAgentApi.getScopeBinding).toHaveBeenCalledWith("scope-a");
      expect(servicesApi.listServices).toHaveBeenCalled();
      expect(scopeRuntimeApi.getServiceBindings).toHaveBeenCalledWith(
        "scope-a",
        "service-alpha",
      );
    });
  });

  it("opens Studio workflow definitions with preserved team context", async () => {
    const pushSpy = jest.spyOn(history, "push");
    renderWithQueryClient(React.createElement(TeamDetailPage));

    fireEvent.click(await screen.findByRole("button", { name: "行为定义" }));

    await waitFor(() => {
      expect(pushSpy).toHaveBeenCalledWith(
        "/studio?scopeId=scope-a&scopeLabel=scope-a&tab=workflows",
      );
    });
  });
});
