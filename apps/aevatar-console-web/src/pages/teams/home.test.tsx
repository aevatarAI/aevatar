import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import {
  clearStoredAuthSession,
  persistAuthSession,
} from "@/shared/auth/session";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import TeamsHomePage from "./home";
import { rememberPendingTeamRosterSummary } from "./pendingTeamRoster";

jest.mock("@/shared/api/scopeRuntimeApi", () => ({
  scopeRuntimeApi: {
    listServices: jest.fn(),
    listMemberRuns: jest.fn(),
  },
}));

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(),
    listTeams: jest.fn(),
    listMembers: jest.fn(),
  },
}));

const defaultTeams = [
  {
    teamId: "t-support",
    scopeId: "scope-a",
    displayName: "客服团队",
    description: "负责处理用户问题",
    lifecycleStage: "active",
    memberCount: 1,
    createdAt: "2026-05-01T09:00:00Z",
    updatedAt: "2026-05-01T10:02:00Z",
  },
];

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
    teamId: "t-support",
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
    window.sessionStorage.clear();
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
    (studioApi.listTeams as jest.Mock).mockResolvedValue({
      scopeId: "scope-a",
      teams: defaultTeams,
      nextPageToken: null,
    });
    (scopeRuntimeApi.listServices as jest.Mock).mockResolvedValue(defaultServices);
    (scopeRuntimeApi.listMemberRuns as jest.Mock).mockImplementation(
      async (_scopeId: string, memberId: string) => buildMemberRunCatalog(memberId),
    );
  });

  it("renders the team homepage around real Team roster with member runtime hints", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("button", { name: "查看团队" })).toBeTruthy();
    expect(screen.getByText("Aevatar / Teams")).toBeTruthy();
    expect(screen.getByText("我的 AI 团队")).toBeTruthy();
    expect(screen.queryByText("当前工作空间")).toBeNull();
    expect(screen.getByText("AI Team")).toBeTruthy();
    expect(screen.getByText("团队列表")).toBeTruthy();
    expect(screen.queryByText("运行正常")).toBeNull();
    expect(screen.queryByText("需要处理")).toBeNull();
    expect(screen.getByRole("button", { name: "组建新团队" })).toBeTruthy();
    expect(screen.getByRole("heading", { level: 3, name: "客服团队" })).toBeTruthy();
    expect(screen.getByText("Team 标识：t-support")).toBeTruthy();
    expect(screen.getByText("客服运行时")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "切换到列表视图" })).toBeNull();
    expect(screen.queryByRole("button", { name: "更多" })).toBeNull();
  });

  it("keeps team card actions focused on the Team detail page", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    await screen.findByRole("button", { name: "查看团队" });
    expect(screen.queryByRole("button", { name: "更多" })).toBeNull();
    expect(screen.queryByText("进入 Studio")).toBeNull();
    expect(screen.queryByText("新增成员")).toBeNull();
    expect(screen.queryByText("查看默认成员运行")).toBeNull();
  });

  it("routes Create Team to the real create-team page", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    fireEvent.click(await screen.findByRole("button", { name: "组建新团队" }));

    expect(window.location.pathname).toBe("/teams/new");
  });

  it("does not show the roster view toggle when only one Team is visible", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    await screen.findByRole("heading", { level: 3, name: "客服团队" });
    expect(screen.queryByRole("button", { name: "切换到列表视图" })).toBeNull();
    expect(screen.queryByRole("button", { name: "切换到卡片视图" })).toBeNull();
  });

  it("keeps the homepage visible without warning on sampled runtime failures", async () => {
    (scopeRuntimeApi.listMemberRuns as jest.Mock).mockRejectedValueOnce(
      new Error("No stub for /api/scopes/scope-a/members/member-alpha/runs"),
    );

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "客服团队" })).toBeTruthy();
    expect(screen.queryByText("部分团队信号暂时不可见")).toBeNull();
    expect(
      screen.queryByText("No stub for /api/scopes/scope-a/members/member-alpha/runs"),
    ).toBeNull();
  });

  it("opens the bound member detail handoff from the primary action", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    fireEvent.click(await screen.findByRole("button", { name: "查看团队" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams/scope-a/t-support");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("teamId")).toBeNull();
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

    expect(await screen.findByText("当前登录态校验失败，已使用本地登录信息")).toBeTruthy();
    expect(
      screen.getByText(
        "登录状态暂时不可用，请刷新后重试。 已使用本地登录信息继续加载团队。",
      ),
    ).toBeTruthy();

    await waitFor(() => {
      expect(new URLSearchParams(window.location.search).get("scopeId")).toBe("scope-a");
    });
  });

  it("renders one card per Team instead of using member cards as teams", async () => {
    (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      teams: [
        ...defaultTeams,
        {
          teamId: "t-joker",
          scopeId: "scope-a",
          displayName: "joker",
          description: "讽刺评论 Team",
          lifecycleStage: "active",
          memberCount: 1,
          createdAt: "2026-05-01T09:10:00Z",
          updatedAt: "2026-05-01T10:10:00Z",
        },
      ],
      nextPageToken: null,
    });
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
          teamId: "t-joker",
          createdAt: "2026-04-13T09:10:00Z",
          updatedAt: "2026-04-13T10:10:00Z",
        },
      ],
      nextPageToken: null,
    });
    (scopeRuntimeApi.listServices as jest.Mock).mockResolvedValueOnce([
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
    expect(screen.getByText("Team 标识：t-joker")).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "查看团队" })).toHaveLength(2);
  });

  it("hides unassigned members instead of surfacing implementation alerts", async () => {
    (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      members: [
        {
          memberId: "member-loose",
          scopeId: "scope-a",
          displayName: "未归队成员",
          description: "还没有 Team",
          implementationKind: "workflow",
          lifecycleStage: "active",
          publishedServiceId: "",
          lastBoundRevisionId: null,
          teamId: null,
          createdAt: "2026-04-13T09:10:00Z",
          updatedAt: "2026-04-13T10:10:00Z",
        },
      ],
      nextPageToken: null,
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByText("团队列表")).toBeTruthy();
    expect(screen.queryByText("存在未归队成员")).toBeNull();
    expect(screen.getByText("Team 标识：t-support")).toBeTruthy();
    expect(screen.queryByRole("heading", { level: 3, name: "未归队成员" })).toBeNull();
  });

  it("shows an empty Team roster state without querying member runs", async () => {
    (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      teams: [],
      nextPageToken: null,
    });
    (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      members: [],
      nextPageToken: null,
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(
      await screen.findByText(
        "当前账号还没有创建任何 Team。创建后，这里会展示你的 AI 团队列表。",
      ),
    ).toBeTruthy();
    expect(scopeRuntimeApi.listMemberRuns).not.toHaveBeenCalled();
  });

  it("keeps a just-created Team visible while the roster projection catches up", async () => {
    (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      teams: [],
      nextPageToken: null,
    });
    (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      members: [],
      nextPageToken: null,
    });
    rememberPendingTeamRosterSummary({
      teamId: "t-new",
      scopeId: "scope-a",
      displayName: "刚创建的团队",
      description: "roster 投影还没追上",
      lifecycleStage: "active",
      memberCount: 0,
      createdAt: "2026-05-19T09:00:00Z",
      updatedAt: "2026-05-19T09:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(
      await screen.findByRole("heading", { level: 3, name: "刚创建的团队" }),
    ).toBeTruthy();
    expect(screen.getByText("Team 标识：t-new")).toBeTruthy();
    expect(
      screen.queryByText(
        "当前工作空间还没有创建任何 Team。创建 Team 后，这里会按后端 roster 展示真实团队。",
      ),
    ).toBeNull();
  });

  it("opens the real create-team page from the empty Team roster state", async () => {
    (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      teams: [],
      nextPageToken: null,
    });
    (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      members: [],
      nextPageToken: null,
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    fireEvent.click(await screen.findByRole("button", { name: "组建新团队" }));

    expect(window.location.pathname).toBe("/teams/new");
  });
});
