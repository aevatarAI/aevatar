import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { servicesApi } from "@/shared/api/servicesApi";
import {
  clearStoredAuthSession,
  persistAuthSession,
} from "@/shared/auth/session";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import TeamsHomePage from "./home";

jest.mock("@/shared/api/servicesApi", () => ({
  servicesApi: {
    listServices: jest.fn(),
  },
}));

jest.mock("@/shared/api/scopeRuntimeApi", () => ({
  scopeRuntimeApi: {
    listMemberRuns: jest.fn(),
  },
}));

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(),
    listMembers: jest.fn(),
  },
}));

const defaultMembers = [
  {
    memberId: "member-alpha",
    scopeId: "scope-a",
    displayName: "客服团队",
    description: "负责处理用户问题",
    implementationKind: "workflow",
    lifecycleStage: "bind_ready",
    publishedServiceId: "service-alpha",
    lastBoundRevisionId: "rev-2",
    createdAt: "2026-04-13T09:00:00Z",
    updatedAt: "2026-04-13T10:02:00Z",
  },
];

const defaultServices = [
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
];

function buildMemberRunCatalog(memberId: string) {
  if (memberId === "member-alpha") {
    return {
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
    };
  }

  if (memberId === "member-joker") {
    return {
      scopeId: "scope-a",
      serviceId: "service-joker",
      serviceKey: "scope-a:joker",
      displayName: "joker",
      runs: [],
    };
  }

  return {
    scopeId: "scope-a",
    serviceId: "",
    serviceKey: "",
    displayName: memberId,
    runs: [],
  };
}

describe("TeamsHomePage", () => {
  beforeEach(() => {
    window.history.replaceState({}, "", "/teams?scopeId=scope-a");
    clearStoredAuthSession();
    jest.clearAllMocks();

    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      enabled: false,
      scopeId: "scope-a",
      scopeSource: "nyxid",
    });
    (studioApi.listMembers as jest.Mock).mockResolvedValue({
      scopeId: "scope-a",
      members: defaultMembers,
      nextPageToken: null,
    });
    (servicesApi.listServices as jest.Mock).mockResolvedValue(defaultServices);
    (scopeRuntimeApi.listMemberRuns as jest.Mock).mockImplementation(
      async (_scopeId: string, memberId: string) => buildMemberRunCatalog(memberId),
    );
  });

  it("renders the team homepage around member-specific runtime facts", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("button", { name: "查看团队" })).toBeTruthy();
    expect(screen.getByText("Aevatar / Teams")).toBeTruthy();
    expect(screen.getByText("我的 AI 团队")).toBeTruthy();
    expect(screen.getByText("当前 Scope")).toBeTruthy();
    expect(screen.getAllByText("团队成员").length).toBeGreaterThan(0);
    expect(screen.getByText("运行正常")).toBeTruthy();
    expect(screen.getByText("需要处理")).toBeTruthy();
    expect(screen.getByRole("button", { name: "组建新团队" })).toBeTruthy();
    expect(screen.getByText("客服团队")).toBeTruthy();
    expect(screen.getByText("成员标识：member-alpha")).toBeTruthy();
    expect(screen.getByText("客服运行时")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "切换到列表视图" })).toBeNull();
  });

  it("opens Studio from the member card without falling back to service-shaped member routes", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    fireEvent.click(await screen.findByRole("button", { name: "更多" }));
    fireEvent.click(await screen.findByText("进入 Studio"));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/studio");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("scopeId")).toBe("scope-a");
    expect(params.get("member")).toBe("member:member-alpha");
    expect(params.get("tab")).toBe("studio");
  });

  it("routes Create Team directly into Studio member creation", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    fireEvent.click(await screen.findByRole("button", { name: "组建新团队" }));

    expect(window.location.pathname).toBe("/studio");
    const params = new URLSearchParams(window.location.search);
    expect(params.get("scopeId")).toBe("scope-a");
    expect(params.get("tab")).toBe("studio");
    expect(params.get("intent")).toBe("create-member");
  });

  it("does not show the roster view toggle when only one member is visible", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    await screen.findByText("客服团队");
    expect(screen.queryByRole("button", { name: "切换到列表视图" })).toBeNull();
    expect(screen.queryByRole("button", { name: "切换到卡片视图" })).toBeNull();
  });

  it("keeps the homepage visible when member runtime sampling partially fails", async () => {
    (scopeRuntimeApi.listMemberRuns as jest.Mock).mockRejectedValueOnce(
      new Error("No stub for /api/scopes/scope-a/members/member-alpha/runs"),
    );

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByText("客服团队")).toBeTruthy();
    expect(screen.getByText("部分团队信号暂时不可见")).toBeTruthy();
    expect(
      screen.queryByText("No stub for /api/scopes/scope-a/members/member-alpha/runs"),
    ).toBeNull();
  });

  it("opens the bound member detail handoff from the primary action", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    fireEvent.click(await screen.findByRole("button", { name: "查看团队" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams/scope-a");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("memberId")).toBe("member-alpha");
    expect(params.get("serviceId")).toBe("service-alpha");
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

    expect(await screen.findByText("当前登录态校验失败，已回退到本地 Scope")).toBeTruthy();
    expect(
      screen.getByText(
        "登录状态暂时不可用，请刷新后重试。 当前已回退到本地会话里的 Scope scope-a。",
      ),
    ).toBeTruthy();

    await waitFor(() => {
      expect(new URLSearchParams(window.location.search).get("scopeId")).toBe("scope-a");
    });
  });

  it("renders one card per member instead of collapsing the homepage into a scope singleton", async () => {
    (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      members: [
        ...defaultMembers,
        {
          memberId: "member-joker",
          scopeId: "scope-a",
          displayName: "joker",
          description: "讽刺评论成员",
          implementationKind: "workflow",
          lifecycleStage: "bind_ready",
          publishedServiceId: "service-joker",
          lastBoundRevisionId: "rev-joker",
          createdAt: "2026-04-13T09:10:00Z",
          updatedAt: "2026-04-13T10:10:00Z",
        },
      ],
      nextPageToken: null,
    });
    (servicesApi.listServices as jest.Mock).mockResolvedValueOnce([
      ...defaultServices,
      {
        serviceKey: "scope-a:joker",
        tenantId: "scope-a",
        appId: "default",
        namespace: "default",
        serviceId: "service-joker",
        displayName: "joker",
        defaultServingRevisionId: "rev-joker",
        activeServingRevisionId: "rev-joker",
        deploymentId: "deploy-joker",
        primaryActorId: "actor://workflow-joker",
        deploymentStatus: "Active",
        endpoints: [],
        policyIds: [],
        updatedAt: "2026-04-13T10:09:00Z",
      },
    ]);

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "客服团队" })).toBeTruthy();
    expect(screen.getByRole("heading", { level: 3, name: "joker" })).toBeTruthy();
    expect(screen.getByText("成员标识：member-joker")).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "查看团队" })).toHaveLength(2);
  });

  it("shows an empty member roster state without querying member runs", async () => {
    (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      members: [],
      nextPageToken: null,
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(
      await screen.findByText(
        "当前 Scope 下还没有创建任何 member。进入 Studio 创建成员后，这里会按成员逐个展示。",
      ),
    ).toBeTruthy();
    expect(scopeRuntimeApi.listMemberRuns).not.toHaveBeenCalled();
  });

  it("opens Studio from the empty member roster state", async () => {
    (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      members: [],
      nextPageToken: null,
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    fireEvent.click(await screen.findByRole("button", { name: "打开 Studio" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/studio");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("scopeId")).toBe("scope-a");
    expect(params.get("tab")).toBe("studio");
  });
});
