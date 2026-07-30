import { act, fireEvent, screen, waitFor } from "@testing-library/react";
import { setLocale } from "@umijs/max";
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
    listServiceRuns: jest.fn(),
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
    entryMemberId: "member-alpha",
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

function buildServiceRunCatalog(serviceId: string) {
  if (serviceId === "service-alpha") {
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

  if (serviceId === "service-joker") {
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
    displayName: serviceId,
    runs: [],
  };
}

describe("TeamsHomePage", () => {
  beforeEach(() => {
    window.history.replaceState({}, "", "/scopes/scope-a/teams");
    window.sessionStorage.clear();
    clearStoredAuthSession();
    jest.clearAllMocks();
    setLocale("zh-CN", false);

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
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockImplementation(
      async (_scopeId: string, serviceId: string) => buildServiceRunCatalog(serviceId),
    );
  });

  it("renders the team homepage around real Team roster with member runtime hints", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("button", { name: "查看团队" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "查看成员" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "调试入口工作流" })).toBeNull();
    expect(screen.queryByRole("button", { name: "调试工作流" })).toBeNull();
    expect(screen.getByRole("navigation", { name: "面包屑" })).toHaveTextContent(
      "团队",
    );
    expect(screen.getByText("我的 AI 团队")).toBeTruthy();
    expect(screen.queryByText("当前工作空间")).toBeNull();
    expect(screen.getByText("AI 团队总数")).toBeTruthy();
    expect(screen.getByText("待启动团队")).toBeTruthy();
    expect(screen.getByText("已有稳定运行")).toBeTruthy();
    expect(screen.queryByText("运行稳定")).toBeNull();
    expect(screen.getByText("团队列表")).toBeTruthy();
    expect(
      screen.getByText("按团队聚合成员与最近运行信号，优先处理异常或待关注项。"),
    ).toBeTruthy();
    expect(screen.queryByText("运行正常")).toBeNull();
    expect(screen.queryByText("需要处理")).toBeNull();
    expect(screen.getByRole("button", { name: "组建新团队" })).toBeTruthy();
    expect(screen.getByRole("heading", { level: 3, name: "客服团队" })).toBeTruthy();
    expect(screen.queryByText("ID：t-support")).toBeNull();
    expect(screen.getByText("客服运行时")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "切换到列表视图" })).toBeNull();
    expect(screen.queryByRole("button", { name: "更多" })).toBeNull();
  });

  it("renders the Teams homepage from the English catalog when the locale changes", async () => {
    setLocale("en-US", false);

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(
      await screen.findByRole("button", { name: "View team" }, { timeout: 3000 }),
    ).toBeTruthy();
    expect(screen.getByRole("button", { name: "View members" })).toBeTruthy();
    expect(
      screen.queryByRole("button", { name: "Debug entry workflow" }),
    ).toBeNull();
    expect(screen.queryByRole("button", { name: "Debug workflow" })).toBeNull();
    expect(screen.getByText("My AI teams")).toBeTruthy();
    expect(screen.getByText("Total AI teams")).toBeTruthy();
    expect(screen.getByText("Teams needing action")).toBeTruthy();
    expect(screen.getByText("Team list")).toBeTruthy();
    expect(screen.queryByText("ID: t-support")).toBeNull();
    expect(screen.queryByText("我的 AI 团队")).toBeNull();
    expect(screen.queryByText("组建新团队")).toBeNull();
  });

  it("keeps team card actions scoped to team navigation", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    await screen.findByRole("button", { name: "查看团队" });
    expect(screen.getByRole("button", { name: "查看团队" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "查看成员" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "调试入口工作流" })).toBeNull();
    expect(screen.queryByRole("button", { name: "调试工作流" })).toBeNull();
    expect(screen.queryByRole("button", { name: "更多" })).toBeNull();
    expect(screen.queryByText("进入 Studio")).toBeNull();
    expect(screen.queryByText("新增成员")).toBeNull();
    expect(screen.queryByText("查看默认成员运行")).toBeNull();
  });

  it("shows completed run status as completion, not stability", async () => {
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockResolvedValueOnce({
      ...buildServiceRunCatalog("service-alpha"),
      runs: [
        {
          ...buildServiceRunCatalog("service-alpha").runs[0],
          completionStatus: "completed",
          lastSuccess: true,
        },
      ],
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByText("已完成")).toBeTruthy();
    expect(screen.queryByText("稳定")).toBeNull();
  });

  it("routes Create Team to the real create-team page", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    fireEvent.click(await screen.findByRole("button", { name: "组建新团队" }));

    expect(window.location.pathname).toBe("/scopes/scope-a/teams/new");
  });

  it("does not show the roster view toggle when only one Team is visible", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    await screen.findByRole("heading", { level: 3, name: "客服团队" });
    expect(screen.queryByRole("button", { name: "切换到列表视图" })).toBeNull();
    expect(screen.queryByRole("button", { name: "切换到卡片视图" })).toBeNull();
  });

  it("excludes archived Teams from the roster, summary counts, and runtime sampling", async () => {
    (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      teams: [
        defaultTeams[0],
        {
          teamId: "t-archived",
          scopeId: "scope-a",
          displayName: "已归档团队",
          description: "不再参与当前 Team roster",
          lifecycleStage: "archived",
          entryMemberId: "member-archived",
          memberCount: 1,
          createdAt: "2026-05-01T09:00:00Z",
          updatedAt: "2026-05-01T10:03:00Z",
        },
      ],
      nextPageToken: null,
    });
    (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      members: [
        ...defaultMembers,
        {
          ...defaultMembers[0],
          memberId: "member-archived",
          displayName: "归档团队成员",
          publishedServiceId: "service-archived",
          teamId: "t-archived",
        },
      ],
      nextPageToken: null,
    });
    (scopeRuntimeApi.listServices as jest.Mock).mockResolvedValueOnce([
      ...defaultServices,
      {
        ...defaultServices[0],
        serviceId: "service-archived",
        displayName: "归档团队运行时",
      },
    ]);

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(
      await screen.findByRole("heading", { level: 3, name: "客服团队" }),
    ).toBeTruthy();
    expect(
      screen.queryByRole("heading", { level: 3, name: "已归档团队" }),
    ).toBeNull();
    expect(
      screen.getByText("AI 团队总数").previousElementSibling,
    ).toHaveTextContent("1");
    expect(
      screen.getByText("待启动团队").previousElementSibling,
    ).toHaveTextContent("1");
    expect(
      screen.getByText("已有稳定运行").previousElementSibling,
    ).toHaveTextContent("0");
    await waitFor(() => {
      expect(scopeRuntimeApi.listServiceRuns).toHaveBeenCalledTimes(1);
    });
    expect(scopeRuntimeApi.listServiceRuns).toHaveBeenCalledWith(
      "scope-a",
      "service-alpha",
      { take: 1 },
    );
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalledWith(
      "scope-a",
      "service-archived",
      { take: 1 },
    );
  });

  it("shows the empty roster when every Team is archived", async () => {
    (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      teams: [
        {
          teamId: "t-archived",
          scopeId: "scope-a",
          displayName: "已归档团队",
          description: "不再参与当前 Team roster",
          lifecycleStage: "archived",
          entryMemberId: "member-archived",
          memberCount: 1,
          createdAt: "2026-05-01T09:00:00Z",
          updatedAt: "2026-05-01T10:03:00Z",
        },
      ],
      nextPageToken: null,
    });
    (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      members: [
        {
          ...defaultMembers[0],
          memberId: "member-archived",
          displayName: "归档团队成员",
          publishedServiceId: "service-archived",
          teamId: "t-archived",
        },
      ],
      nextPageToken: null,
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(
      await screen.findByText(
        "当前账号还没有创建任何团队。创建后，这里会展示你的 AI 团队列表。",
      ),
    ).toBeTruthy();
    expect(
      screen.queryByRole("heading", { level: 3, name: "已归档团队" }),
    ).toBeNull();
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalled();
  });

  it("keeps the homepage visible without warning on sampled runtime failures", async () => {
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockRejectedValueOnce(
      new Error("No stub for /api/scopes/scope-a/services/service-alpha/runs"),
    );

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "客服团队" })).toBeTruthy();
    expect(screen.queryByText("部分团队信号暂时不可见")).toBeNull();
    expect(
      screen.queryByText("No stub for /api/scopes/scope-a/services/service-alpha/runs"),
    ).toBeNull();
  });

  it("loads only the configured entry member service run summary for the homepage signal", async () => {
    const members = Array.from({ length: 13 }, (_, index) => ({
      ...defaultMembers[0],
      memberId: `member-${index + 1}`,
      displayName: `成员 ${index + 1}`,
      publishedServiceId: `service-${index + 1}`,
      teamId: "t-support",
    }));
    const entryMemberId = "member-7";
    (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      teams: [
        {
          ...defaultTeams[0],
          entryMemberId,
          memberCount: members.length,
        },
      ],
      nextPageToken: null,
    });
    (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      members,
      nextPageToken: null,
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(
      await screen.findByText("成员已绑定服务。下一步：进入团队详情后测试团队，生成第一条可见运行。"),
    ).toBeTruthy();
    await waitFor(() => {
      expect(scopeRuntimeApi.listServiceRuns).toHaveBeenCalledTimes(1);
    });
    expect(scopeRuntimeApi.listServiceRuns).toHaveBeenCalledWith(
      "scope-a",
      "service-7",
      { take: 1 },
    );
    expect(screen.queryByText("部分 Team 的运行状态仍在同步")).toBeNull();
    expect(screen.queryByText("状态同步中")).toBeNull();
    expect(
      screen.queryByText(/首页暂未同步到最近运行状态/),
    ).toBeNull();
    expect(screen.queryByText(/绑定事实/)).toBeNull();
    expect(screen.queryByText(/帮助你快速判断是否需要处理/)).toBeNull();
  });

  it("keeps the team runtime signal scoped to the entry member when other bound members are not sampled", async () => {
    (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      teams: [
        {
          ...defaultTeams[0],
          entryMemberId: "member-entry",
          memberCount: 2,
        },
      ],
      nextPageToken: null,
    });
    (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      members: [
        {
          ...defaultMembers[0],
          memberId: "member-entry",
          displayName: "入口成员",
          publishedServiceId: "service-entry",
        },
        {
          ...defaultMembers[0],
          memberId: "member-secondary",
          displayName: "普通成员",
          publishedServiceId: "service-secondary",
        },
      ],
      nextPageToken: null,
    });
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      serviceId: "service-entry",
      serviceKey: "scope-a:entry",
      displayName: "入口运行时",
      runs: [
        {
          ...buildServiceRunCatalog("service-alpha").runs[0],
          serviceId: "service-entry",
          runId: "run-entry-latest",
          completionStatus: "completed",
          lastSuccess: true,
        },
      ],
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByText("已完成")).toBeTruthy();
    expect(screen.getByText("运行中")).toBeTruthy();
    expect(
      screen.getByText("最近一次成员运行正常，可继续进入详情查看。"),
    ).toBeTruthy();
    expect(screen.queryByText("待运行")).toBeNull();
    expect(
      screen.queryByText("成员已绑定服务。下一步：进入团队详情后测试团队，生成第一条可见运行。"),
    ).toBeNull();
    await waitFor(() => {
      expect(scopeRuntimeApi.listServiceRuns).toHaveBeenCalledTimes(1);
    });
    expect(scopeRuntimeApi.listServiceRuns).toHaveBeenCalledWith(
      "scope-a",
      "service-entry",
      { take: 1 },
    );
  });

  it("keeps long member and service summaries compact while preserving full text in titles", async () => {
    (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      teams: [
        {
          teamId: "t-long",
          scopeId: "scope-a",
          displayName: "超长展示团队",
          description: "需要压缩展示",
          lifecycleStage: "active",
          memberCount: 3,
          createdAt: "2026-05-01T09:10:00Z",
          updatedAt: "2026-05-01T10:10:00Z",
        },
      ],
      nextPageToken: null,
    });
    (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      members: [
        {
          ...defaultMembers[0],
          memberId: "member-long-1",
          displayName: "gagent-2 / member-m-d168d2df4f434004993f4ed475534497",
          publishedServiceId: "service-long-1",
          teamId: "t-long",
        },
        {
          ...defaultMembers[0],
          memberId: "member-long-2",
          displayName: "另一个非常长的成员名字用于完整悬停展示",
          publishedServiceId: "service-long-2",
          teamId: "t-long",
        },
        {
          ...defaultMembers[0],
          memberId: "member-long-3",
          displayName: "第三个成员",
          publishedServiceId: "service-long-3",
          teamId: "t-long",
        },
      ],
      nextPageToken: null,
    });
    (scopeRuntimeApi.listServices as jest.Mock).mockResolvedValueOnce([
      {
        ...defaultServices[0],
        serviceId: "service-long-1",
        displayName: "gagent-2 / member-m-d168d2df4f434004993f4ed475534497",
      },
      {
        ...defaultServices[0],
        serviceId: "service-long-2",
        displayName: "另一个非常长的服务名用于验证 hover 全量展示",
      },
      {
        ...defaultServices[0],
        serviceId: "service-long-3",
        displayName: "第三个服务",
      },
    ]);

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "超长展示团队" })).toBeTruthy();
    expect(screen.getByText(/等 3 个成员/).closest("[title]")).toHaveAttribute(
      "title",
      expect.stringContaining("另一个非常长的成员名字用于完整悬停展示"),
    );
    expect(
      screen.getByText("关联服务").previousElementSibling,
    ).toHaveAttribute(
      "title",
      expect.stringContaining("另一个非常长的服务名用于验证 hover 全量展示"),
    );
    expect(screen.queryByText("ID：t-long")).toBeNull();
  });

  it("does not query service runs for a Team without an entry member", async () => {
    (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      teams: [
        {
          ...defaultTeams[0],
          entryMemberId: null,
        },
      ],
      nextPageToken: null,
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("heading", { level: 3, name: "客服团队" })).toBeTruthy();
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalled();
  });

  it("keeps long Team titles compact while preserving the full title in a tooltip", async () => {
    (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      teams: [
        {
          teamId: "t-wide",
          scopeId: "scope-a",
          displayName:
            "这是一个非常长的 Team 名称，用来验证首页标题不会把整张卡片撑到失控",
          description: "需要保持稳定层级",
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
        {
          ...defaultMembers[0],
          memberId: "member-wide",
          displayName: "标题验证成员",
          publishedServiceId: "service-wide",
          teamId: "t-wide",
        },
      ],
      nextPageToken: null,
    });
    (scopeRuntimeApi.listServices as jest.Mock).mockResolvedValueOnce([
      {
        ...defaultServices[0],
        serviceId: "service-wide",
        displayName: "标题验证服务",
      },
    ]);

    renderWithQueryClient(React.createElement(TeamsHomePage));

    const heading = await screen.findByRole("heading", {
      level: 3,
      name: "这是一个非常长的 Team 名称，用来验证首页标题不会把整张卡片撑到失控",
    });
    expect(heading.parentElement).toHaveAttribute(
      "title",
      "这是一个非常长的 Team 名称，用来验证首页标题不会把整张卡片撑到失控",
    );
  });

  it("keeps the list view primary action near the Team summary instead of after every fact block", async () => {
    const teams = Array.from({ length: 7 }, (_, index) => ({
      ...defaultTeams[0],
      teamId: `t-list-${index + 1}`,
      displayName: `列表团队 ${index + 1}`,
      memberCount: 1,
      updatedAt: `2026-05-0${Math.min(index + 1, 9)}T10:02:00Z`,
    }));
    const members = Array.from({ length: 7 }, (_, index) => ({
      ...defaultMembers[0],
      memberId: `member-list-${index + 1}`,
      displayName: `列表成员 ${index + 1}`,
      publishedServiceId: `service-list-${index + 1}`,
      teamId: `t-list-${index + 1}`,
    }));
    const services = Array.from({ length: 7 }, (_, index) => ({
      ...defaultServices[0],
      serviceId: `service-list-${index + 1}`,
      displayName: `列表服务 ${index + 1}`,
    }));

    (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      teams,
      nextPageToken: null,
    });
    (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      members,
      nextPageToken: null,
    });
    (scopeRuntimeApi.listServices as jest.Mock).mockResolvedValueOnce(services);

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("list", { name: "团队紧凑视图" })).toBeTruthy();
    const article = screen
      .getByRole("heading", { level: 4, name: "列表团队 1" })
      .closest("article");
    expect(article?.firstElementChild?.textContent).not.toContain("调试入口工作流");
    expect(article?.firstElementChild?.textContent).not.toContain("调试工作流");
    expect(article?.firstElementChild?.textContent).toContain("查看团队");
    expect(article?.firstElementChild?.textContent).toContain("查看成员");
  });

  it("keeps Team detail available as a team-level action", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    fireEvent.click(await screen.findByRole("button", { name: "查看团队" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/scopes/scope-a/teams/t-support");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("memberId")).toBe("member-alpha");
    expect(params.get("serviceId")).toBe("service-alpha");
    expect(params.get("runId")).toBe("run-latest");
  });

  it("opens the Team members tab from a team-level action", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    fireEvent.click(await screen.findByRole("button", { name: "查看成员" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/scopes/scope-a/teams/t-support");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("tab")).toBe("members");
    expect(params.get("memberId")).toBeNull();
    expect(params.get("serviceId")).toBeNull();
    expect(params.get("runId")).toBeNull();
  });

  it("hides the duplicate view-members action when manage members is already the handoff", async () => {
    (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      teams: [
        {
          ...defaultTeams[0],
          memberCount: 2,
        },
      ],
      nextPageToken: null,
    });
    (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
      scopeId: "scope-a",
      members: [
        {
          ...defaultMembers[0],
          memberId: "member-alpha",
          displayName: "普通成员 A",
          implementationKind: "gagent",
        },
        {
          ...defaultMembers[0],
          memberId: "member-beta",
          displayName: "普通成员 B",
          implementationKind: "gagent",
        },
      ],
      nextPageToken: null,
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByRole("button", { name: "管理成员" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "查看团队" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "查看成员" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "管理成员" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/scopes/scope-a/teams/t-support");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("tab")).toBe("members");
  });

  it("does not load roster data from a locally restored scope when server auth fails", async () => {
    window.history.replaceState({}, "", "/scopes");
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
    expect(studioApi.listTeams).not.toHaveBeenCalled();
    expect(studioApi.listMembers).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.listServices).not.toHaveBeenCalled();
    expect(screen.queryByText("部分团队信号暂时不可见")).toBeNull();
    expect(screen.queryByText("团队列表暂时无法加载。")).toBeNull();
    expect(screen.queryByText("AI 团队总数")).toBeNull();

    await waitFor(() => {
      expect(window.location.pathname).toBe("/scopes/scope-a/teams");
      expect(new URLSearchParams(window.location.search).get("scopeId")).toBeNull();
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
    expect(screen.queryByText("ID：t-joker")).toBeNull();
    expect(screen.queryByRole("button", { name: "调试入口工作流" })).toBeNull();
    expect(screen.queryByRole("button", { name: "调试工作流" })).toBeNull();
    expect(screen.getAllByRole("button", { name: "查看团队" })).toHaveLength(2);
    expect(screen.getAllByRole("button", { name: "查看成员" })).toHaveLength(2);
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
    expect(screen.queryByText("ID：t-support")).toBeNull();
    expect(screen.queryByRole("heading", { level: 3, name: "未归队成员" })).toBeNull();
  });

  it("shows an empty Team roster state without querying service runs", async () => {
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
        "当前账号还没有创建任何团队。创建后，这里会展示你的 AI 团队列表。",
      ),
    ).toBeTruthy();
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalled();
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
    expect(screen.queryByText("ID：t-new")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "创建工作流成员" }));
    await waitFor(() => {
      expect(window.location.pathname).toBe(
        "/scopes/scope-a/teams/t-new/members/new/workflow",
      );
    });
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

    expect(window.location.pathname).toBe("/scopes/scope-a/teams/new");
  });

  it("shows the home skeleton while the route scope waits for server auth confirmation", async () => {
    let resolveAuthSession: (value: unknown) => void = () => undefined;
    (studioApi.getAuthSession as jest.Mock).mockImplementationOnce(
      () =>
        new Promise<unknown>((resolve) => {
          resolveAuthSession = resolve;
        }),
    );

    const { unmount } = renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByTestId("teams-home-skeleton")).toBeTruthy();
    expect(screen.getAllByTestId("teams-home-summary-skeleton")).toHaveLength(3);
    expect(screen.getAllByTestId("teams-home-card-skeleton")).toHaveLength(3);
    expect(screen.queryByText("AI 团队总数")).toBeNull();
    expect(studioApi.listTeams).not.toHaveBeenCalled();
    expect(studioApi.listMembers).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.listServices).not.toHaveBeenCalled();

    unmount();
    await act(async () => {
      resolveAuthSession({
        enabled: false,
        scopeId: "scope-a",
        scopeSource: "nyxid",
      });
    });
  });

  it("shows the home skeleton while the Team roster loads", async () => {
    let resolveTeams: (value: unknown) => void = () => undefined;
    (studioApi.listTeams as jest.Mock).mockImplementationOnce(
      () =>
        new Promise<unknown>((resolve) => {
          resolveTeams = resolve;
        }),
    );

    const { unmount } = renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByTestId("teams-home-skeleton")).toBeTruthy();
    expect(screen.getAllByTestId("teams-home-summary-skeleton")).toHaveLength(3);
    expect(screen.getAllByTestId("teams-home-card-skeleton")).toHaveLength(3);
    expect(screen.queryByText("AI 团队总数")).toBeNull();
    expect(screen.queryByText("当前账号还没有创建团队")).toBeNull();

    unmount();
    await act(async () => {
      resolveTeams({
        scopeId: "scope-a",
        teams: defaultTeams,
        nextPageToken: null,
      });
    });
  });
});
