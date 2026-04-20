import { fireEvent, screen, waitFor } from "@testing-library/react";
import { Modal } from "antd";
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

const mockActiveServiceSnapshot = {
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
};

let mockCurrentServiceSnapshots = [mockActiveServiceSnapshot];

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
    listServices: jest.fn(async () => mockCurrentServiceSnapshots),
    getService: jest.fn(async (serviceId: string) =>
      mockCurrentServiceSnapshots.find((service) => service.serviceId === serviceId) ?? null,
    ),
    deactivateDeployment: jest.fn(async (serviceId: string, deploymentId: string) => {
      mockCurrentServiceSnapshots = mockCurrentServiceSnapshots.map((service) =>
        service.serviceId === serviceId && service.deploymentId === deploymentId
          ? {
              ...service,
              deploymentStatus: "Deactivated",
            }
          : service,
      );
      return {
        commandId: "cmd-deactivate",
        correlationId: `${serviceId}:${deploymentId}`,
        targetActorId: "deployment-actor",
      };
    }),
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
    retireScopeBindingRevision: jest.fn(async () => ({
      accepted: true,
      revisionId: "rev-2",
      scopeId: "scope-a",
    })),
  },
}));

describe("TeamsHomePage", () => {
  beforeEach(() => {
    window.history.replaceState({}, "", "/teams?scopeId=scope-a");
    clearStoredAuthSession();
    mockCurrentServiceSnapshots = [{ ...mockActiveServiceSnapshot }];
    jest.clearAllMocks();
  });

  afterEach(() => {
    Modal.destroyAll();
  });

  it("renders the team homepage around the current scope-backed team preview", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "NyxID Chat" })).toBeTruthy();
    expect(screen.getByText("Aevatar / Teams")).toBeTruthy();
    expect(screen.getByText("我的 AI 团队")).toBeTruthy();
    expect(screen.getByText("当前 Scope")).toBeTruthy();
    expect(screen.getAllByText("团队入口").length).toBeGreaterThan(0);
    expect(screen.getByText("运行正常")).toBeTruthy();
    expect(screen.getByText("需要处理")).toBeTruthy();
    expect(screen.getByRole("button", { name: "组建新团队" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "切换 Scope" })).toBeTruthy();
    expect(screen.getByLabelText("团队卡片视图")).toBeTruthy();
    expect(screen.queryByText("草稿团队")).toBeNull();
    expect(screen.getByText("还有草稿待整理")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "更多" }));

    expect(await screen.findByText("查看运行")).toBeTruthy();
    expect(screen.getByText("事件拓扑")).toBeTruthy();
    expect(screen.getByText("编辑")).toBeTruthy();
  });

  it("does not show the roster view toggle when the homepage only has one visible team", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    await screen.findByRole("heading", { level: 3, name: "NyxID Chat" });
    expect(screen.getByLabelText("团队卡片视图")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "切换到列表视图" })).toBeNull();
    expect(screen.queryByRole("button", { name: "切换到卡片视图" })).toBeNull();
  });

  it("keeps the homepage visible when runtime sampling partially fails", async () => {
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockRejectedValueOnce(
      new Error("No stub for /api/scopes/scope-a/services/service-alpha/runs"),
    );

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "NyxID Chat" })).toBeTruthy();
    expect(
      screen.queryByText("部分团队信号暂时不可见"),
    ).toBeNull();
    expect(
      screen.queryByText("No stub for /api/scopes/scope-a/services/service-alpha/runs"),
    ).toBeNull();
  });

  it("recovers from a transient service catalog failure after retrying", async () => {
    let attempts = 0;
    (servicesApi.listServices as jest.Mock).mockImplementation(async () => {
      attempts += 1;
      if (attempts === 1) {
        throw new Error("temporary catalog outage");
      }

      return mockCurrentServiceSnapshots;
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(
      await screen.findByRole(
        "heading",
        { level: 3, name: "NyxID Chat" },
        { timeout: 2_000 },
      ),
    ).toBeTruthy();
    expect(attempts).toBeGreaterThan(1);
    expect(
      screen.queryByText("当前 Scope 的团队入口暂时无法加载。"),
    ).toBeNull();
  });

  it("opens the scope-backed team detail handoff from the primary card action", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    await screen.findByRole("heading", { level: 3, name: "NyxID Chat" });
    await waitFor(() => {
      expect(new URLSearchParams(window.location.search).get("scopeId")).toBe("scope-a");
    });

    fireEvent.click(screen.getByRole("button", { name: "查看团队" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams/scope-a");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("serviceId")).toBe("service-alpha");
    expect(params.get("workflowId")).toBe("workflow-alpha");
    expect(params.get("runId")).toBe("run-latest");
  });

  it("archives a published default team by deactivating its live deployment without deleting the underlying workflow", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "NyxID Chat" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "更多" }));
    fireEvent.click(await screen.findByText("归档团队"));

    expect((await screen.findAllByText("归档团队“NyxID Chat”？")).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: "归档团队" }));

    await waitFor(() => {
      expect(servicesApi.deactivateDeployment).toHaveBeenCalledWith(
        "service-alpha",
        "deploy-1",
        {
          appId: "default",
          namespace: "default",
          tenantId: "scope-a",
        },
      );
    });
    await waitFor(() => {
      expect(studioApi.retireScopeBindingRevision).toHaveBeenCalledWith({
        scopeId: "scope-a",
        revisionId: "rev-2",
      });
    });
    await waitFor(() => {
      expect(
        screen.queryByRole("heading", { level: 3, name: "NyxID Chat" }),
      ).toBeNull();
    });
  });

  it("does not count an archived workflow as published after the live service disappears", async () => {
    (servicesApi.deactivateDeployment as jest.Mock).mockImplementationOnce(
      async (serviceId: string, deploymentId: string) => {
        mockCurrentServiceSnapshots = mockCurrentServiceSnapshots.filter(
          (service) =>
            !(
              service.serviceId === serviceId &&
              service.deploymentId === deploymentId
            ),
        );
        return {
          commandId: "cmd-deactivate",
          correlationId: `${serviceId}:${deploymentId}`,
          targetActorId: "deployment-actor",
        };
      },
    );

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(
      await screen.findByRole("heading", { level: 3, name: "NyxID Chat" }),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "更多" }));
    fireEvent.click(await screen.findByText("归档团队"));
    expect((await screen.findAllByText("归档团队“NyxID Chat”？")).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: "归档团队" }));

    await waitFor(() => {
      expect(
        screen.queryByRole("heading", { level: 3, name: "NyxID Chat" }),
      ).toBeNull();
    });
    expect(
      screen.getByText("当前 Scope 里还有 1 个已保存的 workflow，但它们还没有形成首页团队入口。"),
    ).toBeTruthy();
  });

  it("keeps archive team visible when a published service only exposes a default revision", async () => {
    (servicesApi.listServices as jest.Mock).mockResolvedValueOnce([
      {
        serviceKey: "scope-a:alpha",
        tenantId: "scope-a",
        appId: "default",
        namespace: "default",
        serviceId: "service-alpha",
        displayName: "客服运行时",
        defaultServingRevisionId: "rev-2",
        activeServingRevisionId: "",
        deploymentId: "deploy-1",
        primaryActorId: "actor://workflow-alpha",
        deploymentStatus: "Active",
        endpoints: [],
        policyIds: [],
        updatedAt: "2026-04-13T10:01:00Z",
      },
    ]);

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "NyxID Chat" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "更多" }));

    expect(await screen.findByText("归档团队")).toBeTruthy();
  });

  it("opens Create Team with the current scope context", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    await screen.findByRole("heading", { level: 3, name: "NyxID Chat" });

    fireEvent.click(screen.getByRole("button", { name: "组建新团队" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams/new");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("scopeId")).toBe("scope-a");
    expect(params.get("scopeLabel")).toBe("scope-a");
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

    expect(await screen.findByRole("heading", { level: 3, name: "NyxID Chat" })).toBeTruthy();
    await waitFor(() => {
      expect(studioApi.getAuthSession).toHaveBeenCalledTimes(2);
    });
    expect(
      screen.queryByText("当前登录态校验失败，已回退到本地 Scope"),
    ).toBeNull();

    await waitFor(() => {
      expect(new URLSearchParams(window.location.search).get("scopeId")).toBe("scope-a");
    });
  });

  it("does not treat workflow-only entries as published teams before a live service exists", async () => {
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

    await screen.findByText("当前 Scope 里还有 1 个已保存的 workflow，但它们还没有形成首页团队入口。");
    expect(
      screen.queryByRole("heading", { level: 3, name: "客服团队" }),
    ).toBeNull();
    expect(screen.getAllByRole("button", { name: "打开 Studio" }).length).toBeGreaterThan(0);
  });

  it("does not query runs for the default service when the scope binding is unavailable", async () => {
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

  it("lists every published team in the current scope and marks the default entry", async () => {
    (scopesApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
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
        workflowId: "workflow-orders",
        displayName: "订单团队",
        serviceKey: "scope-a:orders",
        workflowName: "order-intake",
        actorId: "actor://workflow-orders",
        activeRevisionId: "rev-9",
        deploymentStatus: "Active",
        deploymentId: "deploy-9",
        updatedAt: "2026-04-13T11:00:00Z",
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
    ]);
    (servicesApi.listServices as jest.Mock).mockResolvedValueOnce([
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
      {
        serviceKey: "scope-a:orders",
        tenantId: "scope-a",
        appId: "default",
        namespace: "default",
        serviceId: "service-orders",
        displayName: "订单运行时",
        defaultServingRevisionId: "rev-9",
        activeServingRevisionId: "rev-9",
        deploymentId: "deploy-9",
        primaryActorId: "actor://workflow-orders",
        deploymentStatus: "Active",
        endpoints: [],
        policyIds: [],
        updatedAt: "2026-04-13T11:01:00Z",
      },
    ]);
    (studioApi.getScopeBinding as jest.Mock).mockResolvedValueOnce({
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
    });
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockImplementation(
      async (_scopeId: string, serviceId: string) => ({
        scopeId: "scope-a",
        serviceId,
        serviceKey: serviceId === "service-alpha" ? "scope-a:alpha" : "scope-a:orders",
        displayName: serviceId === "service-alpha" ? "客服运行时" : "订单运行时",
        runs:
          serviceId === "service-alpha"
            ? [
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
              ]
            : [
                {
                  scopeId: "scope-a",
                  serviceId: "service-orders",
                  runId: "run-orders",
                  actorId: "actor://workflow-orders",
                  definitionActorId: "definition://workflow-orders",
                  revisionId: "rev-9",
                  deploymentId: "deploy-9",
                  workflowName: "order-intake",
                  completionStatus: "completed",
                  stateVersion: 7,
                  lastEventId: "evt-9",
                  lastUpdatedAt: "2026-04-13T11:05:00Z",
                  boundAt: "2026-04-13T11:00:00Z",
                  bindingUpdatedAt: "2026-04-13T11:00:00Z",
                  lastSuccess: true,
                  totalSteps: 3,
                  completedSteps: 3,
                  roleReplyCount: 1,
                  lastOutput: "",
                  lastError: "",
                },
              ],
      }),
    );

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "NyxID Chat" })).toBeTruthy();
    expect(screen.getByRole("heading", { level: 3, name: "订单团队" })).toBeTruthy();
    expect(screen.getByText("当前默认入口")).toBeTruthy();
    expect(screen.getByRole("button", { name: "切换到列表视图" })).toBeTruthy();
    expect(screen.queryByText("草稿团队")).toBeNull();
    expect(screen.getAllByRole("button", { name: "查看团队" })).toHaveLength(2);
  });

  it("hides legacy self-named duplicate services when the same workflow already has a default entry", async () => {
    (scopesApi.listWorkflows as jest.Mock).mockResolvedValueOnce([
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
        workflowId: "customer-support-triage",
        displayName: "客服团队旧入口",
        serviceKey: "scope-a:shadow",
        workflowName: "customer-support-triage",
        actorId: "actor://workflow-alpha-shadow",
        activeRevisionId: "rev-shadow",
        deploymentStatus: "Active",
        deploymentId: "deploy-shadow",
        updatedAt: "2026-04-13T09:30:00Z",
      },
    ]);
    (servicesApi.listServices as jest.Mock).mockResolvedValueOnce([
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
      {
        serviceKey: "scope-a:shadow",
        tenantId: "scope-a",
        appId: "default",
        namespace: "default",
        serviceId: "customer-support-triage",
        displayName: "客服团队旧入口",
        defaultServingRevisionId: "rev-shadow",
        activeServingRevisionId: "rev-shadow",
        deploymentId: "deploy-shadow",
        primaryActorId: "actor://workflow-alpha-shadow",
        deploymentStatus: "Active",
        endpoints: [],
        policyIds: [],
        updatedAt: "2026-04-13T09:31:00Z",
      },
    ]);
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockImplementation(
      async (_scopeId: string, serviceId: string) => ({
        scopeId: "scope-a",
        serviceId,
        serviceKey: serviceId === "service-alpha" ? "scope-a:alpha" : "scope-a:shadow",
        displayName: serviceId === "service-alpha" ? "客服运行时" : "客服团队旧入口",
        runs: [],
      }),
    );

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect((await screen.findAllByRole("heading", { level: 3, name: "NyxID Chat" })).length).toBeGreaterThan(0);
    expect(screen.queryByRole("heading", { level: 3, name: "客服团队旧入口" })).toBeNull();
    expect(screen.getAllByRole("button", { name: "查看团队" })).toHaveLength(1);
  });
});
