import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { scopesApi } from "@/shared/api/scopesApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { history } from "@/shared/navigation/history";
import { buildTeamDetailHref } from "@/shared/navigation/teamRoutes";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import ScopeOverviewPage from "./overview";

jest.mock("@/shared/api/scopesApi", () => ({
  scopesApi: {
    listWorkflows: jest.fn(),
    listScripts: jest.fn(),
    getWorkflowDetail: jest.fn(),
    getScriptDetail: jest.fn(),
  },
}));

jest.mock("@/shared/api/servicesApi", () => ({
  servicesApi: {
    listServices: jest.fn(),
  },
}));

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(),
    getScopeBinding: jest.fn(),
    activateScopeBindingRevision: jest.fn(),
    retireScopeBindingRevision: jest.fn(),
  },
}));

type Deferred<T> = {
  promise: Promise<T>;
  reject: (error?: unknown) => void;
  resolve: (value: T) => void;
};

const mockedScopesApi = scopesApi as {
  getScriptDetail: jest.Mock;
  getWorkflowDetail: jest.Mock;
  listScripts: jest.Mock;
  listWorkflows: jest.Mock;
};
const mockedServicesApi = servicesApi as {
  listServices: jest.Mock;
};
const mockedStudioApi = studioApi as {
  activateScopeBindingRevision: jest.Mock;
  getAuthSession: jest.Mock;
  getScopeBinding: jest.Mock;
  retireScopeBindingRevision: jest.Mock;
};

function createDeferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error?: unknown) => void;
  const promise = new Promise<T>((nextResolve, nextReject) => {
    resolve = nextResolve;
    reject = nextReject;
  });

  return { promise, reject, resolve };
}

function createAuthSession(overrides = {}) {
  return {
    authenticated: true,
    email: "alpha@example.com",
    enabled: true,
    name: "Alpha User",
    scopeId: "scope-a",
    scopeSource: "nyxid",
    ...overrides,
  };
}

function createBinding(
  scopeId = "scope-a",
  overrides = {},
) {
  return {
    activeServingRevisionId: "rev-1",
    available: true,
    defaultServingRevisionId: "rev-1",
    deploymentId: "deploy-1",
    deploymentStatus: "Ready",
    displayName: `Team ${scopeId}`,
    primaryActorId: `actor://${scopeId}/default`,
    revisions: [
      {
        allocationWeight: 100,
        artifactHash: "hash-1",
        createdAt: "2026-04-09T07:00:00Z",
        deploymentId: "deploy-1",
        failureReason: "",
        implementationKind: "workflow",
        inlineWorkflowCount: 1,
        isActiveServing: true,
        isDefaultServing: true,
        isServingTarget: true,
        preparedAt: "2026-04-09T07:05:00Z",
        primaryActorId: `actor://${scopeId}/default`,
        publishedAt: "2026-04-09T07:10:00Z",
        retiredAt: null,
        revisionId: "rev-1",
        scriptDefinitionActorId: "",
        scriptId: "",
        scriptRevision: "",
        scriptSourceHash: "",
        servingState: "Ready",
        staticActorTypeName: "",
        status: "Published",
        workflowDefinitionActorId: `definition://${scopeId}/workflow`,
        workflowName: `${scopeId}-workflow`,
      },
    ],
    scopeId,
    serviceId: "service-alpha",
    serviceKey: `${scopeId}:default`,
    updatedAt: "2026-04-09T08:00:00Z",
    ...overrides,
  };
}

function renderPage(route = "/teams") {
  window.history.replaceState({}, "", route);
  return renderWithQueryClient(React.createElement(ScopeOverviewPage));
}

async function waitForScopeQueries(scopeId: string) {
  await waitFor(() => {
    expect(mockedStudioApi.getScopeBinding).toHaveBeenCalledWith(scopeId);
    expect(mockedScopesApi.listWorkflows).toHaveBeenCalledWith(scopeId);
    expect(mockedScopesApi.listScripts).toHaveBeenCalledWith(scopeId);
    expect(mockedServicesApi.listServices).toHaveBeenCalledWith({
      appId: "default",
      namespace: "default",
      tenantId: scopeId,
    });
  });
}

describe("ScopeOverviewPage", () => {
  beforeEach(() => {
    jest.clearAllMocks();

    mockedStudioApi.getAuthSession.mockResolvedValue(createAuthSession());
    mockedStudioApi.getScopeBinding.mockResolvedValue(createBinding());
    mockedStudioApi.activateScopeBindingRevision.mockResolvedValue({
      revisionId: "rev-1",
      scopeId: "scope-a",
      serviceId: "service-alpha",
    });
    mockedStudioApi.retireScopeBindingRevision.mockResolvedValue({
      revisionId: "rev-1",
      scopeId: "scope-a",
      status: "Retiring",
      serviceId: "service-alpha",
    });
    mockedScopesApi.listWorkflows.mockResolvedValue([]);
    mockedScopesApi.listScripts.mockResolvedValue([]);
    mockedScopesApi.getWorkflowDetail.mockResolvedValue({
      available: false,
      scopeId: "scope-a",
      source: null,
      workflow: null,
    });
    mockedScopesApi.getScriptDetail.mockResolvedValue({
      available: false,
      scopeId: "scope-a",
      script: null,
      source: null,
    });
    mockedServicesApi.listServices.mockResolvedValue([]);
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  it("auto-loads the resolved team from the current session", async () => {
    renderPage("/teams");

    await waitForScopeQueries("scope-a");

    expect(await screen.findByDisplayValue("scope-a")).toBeTruthy();
    expect(screen.getByText("已解析团队")).toBeTruthy();
    expect(screen.getByText("当前会话已通过 nyxid 解析出这个团队")).toBeTruthy();
    expect(screen.getAllByText("Team scope-a").length).toBeGreaterThan(0);
  });

  it("shows auth and binding loading/error states while team context is resolving", async () => {
    const authSessionDeferred = createDeferred<any>();
    const bindingDeferred = createDeferred<any>();
    const consoleErrorSpy = jest.spyOn(console, "error").mockImplementation(() => {});

    mockedStudioApi.getAuthSession.mockReturnValue(authSessionDeferred.promise);
    mockedStudioApi.getScopeBinding.mockReturnValue(bindingDeferred.promise);

    renderPage("/teams");

    expect(
      await screen.findByText("正在解析当前会话的团队上下文。"),
    ).toBeTruthy();
    expect(mockedStudioApi.getScopeBinding).not.toHaveBeenCalled();

    authSessionDeferred.resolve(createAuthSession());

    await waitFor(() => {
      expect(mockedStudioApi.getScopeBinding).toHaveBeenCalledWith("scope-a");
    });
    expect(await screen.findByText("正在加载团队状态。")).toBeTruthy();

    bindingDeferred.reject(new Error("Binding load failed"));

    await waitFor(() => {
      expect(screen.getByText("加载团队状态失败。")).toBeTruthy();
      expect(screen.getByText("Binding load failed")).toBeTruthy();
    });

    consoleErrorSpy.mockRestore();
  });

  it("falls back to manual scope input when no team is resolved from the session", async () => {
    mockedStudioApi.getAuthSession.mockResolvedValue(
      createAuthSession({
        scopeId: null,
        scopeSource: null,
      }),
    );
    mockedStudioApi.getScopeBinding.mockImplementation(async (scopeId) =>
      createBinding(scopeId),
    );

    renderPage("/teams");

    expect(
      await screen.findByText("当前会话里没有自动解析出团队。请手动输入一个 scopeId。"),
    ).toBeTruthy();
    expect(mockedStudioApi.getScopeBinding).not.toHaveBeenCalled();

    fireEvent.change(screen.getByPlaceholderText("输入团队 scopeId"), {
      target: { value: "scope-manual" },
    });
    fireEvent.click(screen.getByRole("button", { name: "加载团队状态" }));

    await waitForScopeQueries("scope-manual");

    expect(await screen.findByDisplayValue("scope-manual")).toBeTruthy();
    expect(screen.getAllByText("Team scope-manual").length).toBeGreaterThan(0);
  });

  it("navigates team CTAs with buildTeamDetailHref", async () => {
    const pushSpy = jest.spyOn(history, "push").mockImplementation(() => {});

    renderPage("/teams");
    await waitForScopeQueries("scope-a");

    fireEvent.click(await screen.findByRole("button", { name: "打开团队详情" }));
    fireEvent.click(screen.getByRole("button", { name: "打开高级编辑" }));

    expect(pushSpy).toHaveBeenNthCalledWith(
      1,
      buildTeamDetailHref({
        scopeId: "scope-a",
      }),
    );
    expect(pushSpy).toHaveBeenNthCalledWith(
      2,
      buildTeamDetailHref({
        scopeId: "scope-a",
        tab: "advanced",
      }),
    );
  });
});
