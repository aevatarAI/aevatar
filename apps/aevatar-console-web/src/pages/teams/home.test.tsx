import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { scopesApi } from "@/shared/api/scopesApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import TeamsHomePage from "./home";

jest.mock("@/shared/api/scopesApi", () => ({
  scopesApi: {
    listWorkflows: jest.fn(async () => [
      {
        scopeId: "scope-a",
        workflowId: "workflow-alpha",
        displayName: "客服团队",
        serviceKey: "scope-a:alpha",
        workflowName: "customer-support-triage",
        actorId: "actor://workflow-alpha",
        activeRevisionId: "rev-2",
        deploymentStatus: "Active",
        deploymentId: "deploy-1",
        updatedAt: "2026-04-13T10:00:00Z",
      },
      {
        scopeId: "scope-a",
        workflowId: "workflow-draft",
        displayName: "草稿团队",
        serviceKey: "",
        workflowName: "draft-team",
        actorId: "actor://workflow-draft",
        activeRevisionId: "rev-draft",
        deploymentStatus: "Draft",
        deploymentId: "",
        updatedAt: "2026-04-13T09:00:00Z",
      },
    ]),
  },
}));

jest.mock("@/shared/api/servicesApi", () => ({
  servicesApi: {
    listServices: jest.fn(async () => [
      {
        serviceKey: "scope-a:alpha",
        tenantId: "scope-a",
        appId: "default",
        namespace: "default",
        serviceId: "service-alpha",
        displayName: "客服运行时",
        defaultServingRevisionId: "rev-2",
        activeServingRevisionId: "rev-2",
        deploymentId: "deploy-1",
        primaryActorId: "actor://workflow-alpha",
        deploymentStatus: "Active",
        endpoints: [],
        policyIds: [],
        updatedAt: "2026-04-13T10:01:00Z",
      },
    ]),
  },
}));

jest.mock("@/shared/api/scopeRuntimeApi", () => ({
  scopeRuntimeApi: {
    listServiceRuns: jest.fn(async () => ({
      scopeId: "scope-a",
      serviceId: "service-alpha",
      serviceKey: "scope-a:alpha",
      displayName: "客服运行时",
      runs: [
        {
          scopeId: "scope-a",
          serviceId: "service-alpha",
          runId: "run-latest",
          actorId: "actor://workflow-alpha",
          definitionActorId: "definition://workflow-alpha",
          revisionId: "rev-2",
          deploymentId: "deploy-1",
          workflowName: "customer-support-triage",
          completionStatus: "waiting_approval",
          stateVersion: 2,
          lastEventId: "evt-2",
          lastUpdatedAt: "2026-04-13T10:05:00Z",
          boundAt: "2026-04-13T10:00:00Z",
          bindingUpdatedAt: "2026-04-13T10:00:00Z",
          lastSuccess: false,
          totalSteps: 4,
          completedSteps: 2,
          roleReplyCount: 1,
          lastOutput: "",
          lastError: "Waiting on approval",
        },
      ],
    })),
  },
}));

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(async () => ({
      enabled: false,
      scopeId: "scope-a",
      scopeSource: "nyxid",
    })),
    getScopeBinding: jest.fn(async () => ({
      available: true,
      scopeId: "scope-a",
      serviceId: "service-alpha",
      displayName: "NyxID Chat",
      serviceKey: "scope-a:alpha",
      defaultServingRevisionId: "rev-2",
      activeServingRevisionId: "rev-2",
      deploymentId: "deploy-1",
      deploymentStatus: "Active",
      primaryActorId: "actor://workflow-alpha",
      updatedAt: "2026-04-13T10:00:00Z",
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
          deploymentId: "deploy-1",
          primaryActorId: "actor://workflow-alpha",
          createdAt: "2026-04-13T09:00:00Z",
          preparedAt: "2026-04-13T09:01:00Z",
          publishedAt: "2026-04-13T09:02:00Z",
          retiredAt: null,
          workflowName: "customer-support-triage",
          workflowDefinitionActorId: "definition://workflow-alpha",
          inlineWorkflowCount: 1,
          scriptId: "",
          scriptRevision: "",
          scriptDefinitionActorId: "",
          scriptSourceHash: "",
          staticActorTypeName: "",
        },
      ],
    })),
  },
}));

describe("TeamsHomePage", () => {
  beforeEach(() => {
    window.history.replaceState({}, "", "/teams?scopeId=scope-a");
    jest.clearAllMocks();
  });

  it("renders the team homepage around the current scope-backed team preview", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "NyxID Chat" })).toBeTruthy();
    expect(screen.getByText("Aevatar / Teams")).toBeTruthy();
    expect(screen.getByText("我的 AI 团队")).toBeTruthy();
    expect(screen.getByText("当前 Team")).toBeTruthy();
    expect(screen.getByText("当前可见团队")).toBeTruthy();
    expect(screen.getByText("可见运行信号")).toBeTruthy();
    expect(screen.getByText("草稿条目")).toBeTruthy();
    expect(screen.getByRole("button", { name: "组建新团队" })).toBeTruthy();
    expect(screen.getByLabelText("团队卡片视图")).toBeTruthy();
    expect(screen.queryByText("草稿团队")).toBeNull();
    expect(screen.queryByRole("button", { name: "显示草稿团队 (1)" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "更多" }));

    expect(await screen.findByText("查看运行")).toBeTruthy();
    expect(screen.getByText("进入 Studio")).toBeTruthy();
  });

  it("lets the user switch from cards to the compact roster manually", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    await screen.findByRole("heading", { level: 3, name: "NyxID Chat" });
    fireEvent.click(screen.getByRole("button", { name: "切换到列表视图" }));

    expect(await screen.findByLabelText("团队紧凑视图")).toBeTruthy();
    expect(screen.queryByLabelText("团队卡片视图")).toBeNull();
  });

  it("keeps the homepage visible when runtime sampling partially fails", async () => {
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockRejectedValueOnce(
      new Error("No stub for /api/scopes/scope-a/services/service-alpha/runs"),
    );

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "NyxID Chat" })).toBeTruthy();
    expect(screen.getByText("部分团队信号暂时不可见")).toBeTruthy();
    expect(
      screen.queryByText("No stub for /api/scopes/scope-a/services/service-alpha/runs"),
    ).toBeNull();
  });

  it("opens the scope-backed team detail handoff from the primary card action", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    fireEvent.click(await screen.findByRole("button", { name: "查看团队" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams/scope-a");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("serviceId")).toBe("service-alpha");
    expect(params.get("workflowId")).toBeNull();
    expect(params.get("runId")).toBe("run-latest");
  });

  it("does not turn saved workflows into homepage teams before the current scope has an entry", async () => {
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce(null);
    (servicesApi.listServices as jest.Mock).mockResolvedValueOnce([]);
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      serviceId: "service-alpha",
      serviceKey: "scope-a:alpha",
      displayName: "客服运行时",
      runs: [],
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("button", { name: "打开 Studio" })).toBeTruthy();
    expect(screen.getByText(/已保存的行为定义/)).toBeTruthy();
    expect(screen.queryByText("客服团队")).toBeNull();
    expect(screen.queryByText("草稿团队")).toBeNull();
    expect(screen.queryByRole("button", { name: "查看团队" })).toBeNull();
  });
});
