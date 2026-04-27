import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { scopesApi } from "@/shared/api/scopesApi";
import { servicesApi } from "@/shared/api/servicesApi";
import {
  clearStoredAuthSession,
  persistAuthSession,
} from "@/shared/auth/session";
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
    getDefaultRouteTarget: jest.fn(async () => ({
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
          artifactHash: "hash-home",
          failureReason: "",
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: "Active",
          deploymentId: "deploy-1",
          primaryActorId: "actor://workflow-alpha",
          createdAt: "2026-04-13T09:00:00Z",
          preparedAt: "2026-04-13T09:05:00Z",
          publishedAt: "2026-04-13T09:10:00Z",
          retiredAt: null,
          workflowName: "support-escalation",
          workflowDefinitionActorId: "workflow-def://support-escalation",
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
    clearStoredAuthSession();
    jest.clearAllMocks();
  });

  it("renders the team homepage around the current scope-backed team preview", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "当前团队" })).toBeTruthy();
    expect(screen.getByText("Aevatar / Teams")).toBeTruthy();
    expect(screen.getByText("我的 AI 团队")).toBeTruthy();
    expect(screen.getByText("当前 Scope")).toBeTruthy();
    expect(screen.getAllByText("团队入口").length).toBeGreaterThan(0);
    expect(screen.getByText("运行正常")).toBeTruthy();
    expect(screen.getByText("需要处理")).toBeTruthy();
    expect(screen.getByRole("button", { name: "组建新团队" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "切换 Scope" })).toBeNull();
    expect(screen.getByLabelText("团队卡片视图")).toBeTruthy();
    expect(screen.queryByText("草稿团队")).toBeNull();
    expect(screen.getByText("还有草稿待整理")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "更多" }));

    expect(await screen.findByText("查看运行")).toBeTruthy();
    expect(screen.getByText("进入 Studio")).toBeTruthy();
  });

  it("opens Studio from the scope-backed team preview without legacy label params", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    fireEvent.click(await screen.findByRole("button", { name: "更多" }));
    fireEvent.click(await screen.findByText("进入 Studio"));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/studio");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("scopeId")).toBe("scope-a");
    expect(params.get("member")).toBe("workflow:workflow-alpha");
    expect(params.get("focus")).toBeNull();
    expect(params.get("tab")).toBe("studio");
    expect(params.get("scopeLabel")).toBeNull();
  });

  it("routes Create Team directly into Studio member creation", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    fireEvent.click(await screen.findByRole("button", { name: "组建新团队" }));

    expect(window.location.pathname).toBe("/studio");
    const params = new URLSearchParams(window.location.search);
    expect(params.get("scopeId")).toBe("scope-a");
    expect(params.get("tab")).toBe("studio");
    expect(params.get("intent")).toBe("create-member");
    expect(params.get("teamName")).toBeNull();
    expect(params.get("entryName")).toBeNull();
  });

  it("does not show the roster view toggle when the homepage only has one visible team", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    await screen.findByRole("heading", { level: 3, name: "当前团队" });
    expect(screen.getByLabelText("团队卡片视图")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "切换到列表视图" })).toBeNull();
    expect(screen.queryByRole("button", { name: "切换到卡片视图" })).toBeNull();
  });

  it("keeps the homepage visible when runtime sampling partially fails", async () => {
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockRejectedValueOnce(
      new Error("No stub for /api/scopes/scope-a/services/service-alpha/runs"),
    );

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "当前团队" })).toBeTruthy();
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

  it("falls back to the locally stored auth scope when the live session lookup fails", async () => {
    window.history.replaceState({}, "", "/teams");
    persistAuthSession({
      tokens: {
        accessToken: "access-token",
        tokenType: "Bearer",
        expiresIn: 3600,
        expiresAt: Date.now() + 3600_000,
        refreshToken: "refresh-token",
      },
      user: {
        sub: "scope-a",
        name: "Abigail Deng",
      },
    });
    (studioApi.getAuthSession as jest.Mock).mockRejectedValueOnce(
      new Error("Error occurred while trying to proxy: localhost:5173/api/auth/me"),
    );

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "当前团队" })).toBeTruthy();
    expect(screen.getByText("当前登录态校验失败，已回退到本地 Scope")).toBeTruthy();
    expect(
      screen.getByText(
        "登录状态暂时不可用，请刷新后重试。 当前已回退到本地会话里的 Scope scope-a。",
      ),
    ).toBeTruthy();

    await waitFor(() => {
      expect(new URLSearchParams(window.location.search).get("scopeId")).toBe("scope-a");
    });
  });

  it("keeps the homepage team title stable when the current bound member changes", async () => {
    (scopesApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
      {
        scopeId: "scope-a",
        workflowId: "workflow-joker",
        displayName: "joker",
        serviceKey: "scope-a:default:joker",
        workflowName: "joker",
        actorId: "actor://workflow-joker",
        activeRevisionId: "rev-joker",
        deploymentStatus: "Active",
        deploymentId: "deploy-joker",
        updatedAt: "2026-04-24T19:20:00Z",
      },
    ]);
    (servicesApi.listServices as jest.Mock).mockResolvedValueOnce([
      {
        serviceKey: "scope-a:default:joker",
        tenantId: "scope-a",
        appId: "default",
        namespace: "default",
        serviceId: "joker",
        displayName: "joker",
        defaultServingRevisionId: "rev-joker",
        activeServingRevisionId: "rev-joker",
        deploymentId: "deploy-joker",
        primaryActorId: "actor://workflow-joker",
        deploymentStatus: "Active",
        endpoints: [],
        policyIds: [],
        updatedAt: "2026-04-24T19:21:00Z",
      },
    ]);
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      serviceId: "joker",
      serviceKey: "scope-a:default:joker",
      displayName: "joker",
      runs: [],
    });
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce({
      available: true,
      scopeId: "scope-a",
      serviceId: "joker",
      displayName: "joker",
      serviceKey: "scope-a:default:joker",
      defaultServingRevisionId: "rev-joker",
      activeServingRevisionId: "rev-joker",
      deploymentId: "deploy-joker",
      deploymentStatus: "Active",
      primaryActorId: "actor://workflow-joker",
      updatedAt: "2026-04-24T19:21:00Z",
      revisions: [
        {
          revisionId: "rev-joker",
          implementationKind: "workflow",
          status: "Published",
          artifactHash: "hash-joker",
          failureReason: "",
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: "Active",
          deploymentId: "deploy-joker",
          primaryActorId: "actor://workflow-joker",
          createdAt: "2026-04-24T19:20:00Z",
          preparedAt: "2026-04-24T19:20:30Z",
          publishedAt: "2026-04-24T19:21:00Z",
          retiredAt: null,
          workflowName: "joker",
          workflowDefinitionActorId: "definition://workflow-joker",
          inlineWorkflowCount: 1,
          scriptId: "",
          scriptRevision: "",
          scriptDefinitionActorId: "",
          scriptSourceHash: "",
          staticActorTypeName: "",
        },
      ],
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "当前团队" })).toBeTruthy();
    expect(screen.getByText("默认入口：joker")).toBeTruthy();
    expect(screen.getByText("joker")).toBeTruthy();
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

    const openStudioButtons = await screen.findAllByRole("button", { name: "打开 Studio" });
    expect(openStudioButtons.length).toBeGreaterThan(0);
    expect(screen.getByText(/已保存的行为定义/)).toBeTruthy();
    expect(screen.queryByText("客服团队")).toBeNull();
    expect(screen.queryByText("草稿团队")).toBeNull();
    expect(screen.queryByRole("button", { name: "查看团队" })).toBeNull();
  });

  it("opens Studio from the no-entry empty state without legacy label params", async () => {
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

    const openStudioButtons = await screen.findAllByRole("button", { name: "打开 Studio" });
    fireEvent.click(openStudioButtons[0]);

    await waitFor(() => {
      expect(window.location.pathname).toBe("/studio");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("scopeId")).toBe("scope-a");
    expect(params.get("tab")).toBe("studio");
    expect(params.get("scopeLabel")).toBeNull();
  });

  it("does not query runs for the default service when the default entry is unavailable", async () => {
    (scopesApi.listWorkflows as jest.Mock).mockResolvedValueOnce([]);
    (servicesApi.listServices as jest.Mock).mockResolvedValueOnce([]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce({
      available: false,
      scopeId: "scope-a",
      serviceId: "default",
      displayName: "",
      serviceKey: "",
      defaultServingRevisionId: "",
      activeServingRevisionId: "",
      deploymentId: "",
      deploymentStatus: "",
      primaryActorId: "",
      updatedAt: null,
      revisions: [],
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(
      await screen.findByText("当前 Scope 还没有默认团队入口"),
    ).toBeTruthy();
    expect(
      screen.getByText(
        "没有找到已发布的默认入口服务，所以首页暂时没有运行信号。去 Studio 发布团队后，这里会自动出现。",
      ),
    ).toBeTruthy();
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalled();
    expect(screen.queryByText("部分团队信号暂时不可见")).toBeNull();
  });
});
