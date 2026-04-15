import { act, fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { scopesApi } from "@/shared/api/scopesApi";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import ScopeOverviewPage from "./overview";

jest.mock("@/shared/api/scopesApi", () => ({
  scopesApi: {
    listWorkflows: jest.fn(async (scopeId: string) => {
      if (scopeId?.trim() === "scope-b") {
        return [
          {
            scopeId: "scope-b",
            workflowId: "workflow-beta",
            displayName: "运营团队",
            serviceKey: "scope-b:beta",
            workflowName: "ops-command",
            actorId: "actor://workflow-beta",
            activeRevisionId: "rev-7",
            deploymentStatus: "Active",
            deploymentId: "deploy-7",
            updatedAt: "2026-04-14T10:00:00Z",
          },
        ];
      }

      return [
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
      ];
    }),
  },
}));

jest.mock("@/shared/api/servicesApi", () => ({
  servicesApi: {
    listServices: jest.fn(async ({ tenantId }: { tenantId?: string }) => {
      if (tenantId?.trim() === "scope-b") {
        return [
          {
            serviceKey: "scope-b:beta",
            tenantId: "scope-b",
            appId: "default",
            namespace: "default",
            serviceId: "service-beta",
            displayName: "运营运行时",
            defaultServingRevisionId: "rev-7",
            activeServingRevisionId: "rev-7",
            deploymentId: "deploy-7",
            primaryActorId: "actor://workflow-beta",
            deploymentStatus: "Active",
            endpoints: [],
            policyIds: [],
            updatedAt: "2026-04-14T10:01:00Z",
          },
        ];
      }

      return [
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
    }),
  },
}));

jest.mock("@/shared/api/scopeRuntimeApi", () => ({
  scopeRuntimeApi: {
    listServiceRuns: jest.fn(async (scopeId: string, serviceId: string) => {
      if (scopeId?.trim() === "scope-b") {
        return {
          scopeId: "scope-b",
          serviceId,
          serviceKey: "scope-b:beta",
          displayName: "运营运行时",
          runs: [
            {
              scopeId: "scope-b",
              serviceId,
              runId: "run-beta",
              actorId: "actor://workflow-beta",
              definitionActorId: "definition://workflow-beta",
              revisionId: "rev-7",
              deploymentId: "deploy-7",
              workflowName: "ops-command",
              completionStatus: "completed",
              stateVersion: 7,
              lastEventId: "evt-7",
              lastUpdatedAt: "2026-04-14T10:05:00Z",
              boundAt: "2026-04-14T10:00:00Z",
              bindingUpdatedAt: "2026-04-14T10:00:00Z",
              lastSuccess: true,
              totalSteps: 6,
              completedSteps: 6,
              roleReplyCount: 2,
              lastOutput: "",
              lastError: "",
            },
          ],
        };
      }

      return {
        scopeId: "scope-a",
        serviceId,
        serviceKey: "scope-a:alpha",
        displayName: "客服运行时",
        runs: [
          {
            scopeId: "scope-a",
            serviceId,
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
    }),
  },
}));

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(async () => ({
      enabled: false,
      scopeId: "scope-a",
      scopeSource: "nyxid",
    })),
    getScopeBinding: jest.fn(async (scopeId: string) => {
      if (scopeId?.trim() === "scope-b") {
        return {
          available: true,
          scopeId: "scope-b",
          serviceId: "service-beta",
          displayName: "运营团队",
          serviceKey: "scope-b:beta",
          defaultServingRevisionId: "rev-7",
          activeServingRevisionId: "rev-7",
          deploymentId: "deploy-7",
          deploymentStatus: "Active",
          primaryActorId: "actor://workflow-beta",
          updatedAt: "2026-04-14T10:00:00Z",
          revisions: [
            {
              revisionId: "rev-7",
              implementationKind: "workflow",
              status: "Published",
              artifactHash: "hash-7",
              failureReason: "",
              isDefaultServing: true,
              isActiveServing: true,
              isServingTarget: true,
              allocationWeight: 100,
              servingState: "Active",
              deploymentId: "deploy-7",
              primaryActorId: "actor://workflow-beta",
              createdAt: "2026-04-14T09:00:00Z",
              preparedAt: "2026-04-14T09:01:00Z",
              publishedAt: "2026-04-14T09:02:00Z",
              retiredAt: null,
              workflowName: "ops-command",
              workflowDefinitionActorId: "definition://workflow-beta",
              inlineWorkflowCount: 1,
              scriptId: "",
              scriptRevision: "",
              scriptDefinitionActorId: "",
              scriptSourceHash: "",
              staticActorTypeName: "",
            },
          ],
        };
      }

      return {
        available: true,
        scopeId: "scope-a",
        serviceId: "service-alpha",
        displayName: "客服团队",
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
      };
    }),
  },
}));

import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";

describe("ScopeOverviewPage", () => {
  beforeEach(() => {
    window.history.replaceState({}, "", "/teams?scopeId=scope-a");
    jest.clearAllMocks();
  });

  it("renders the team homepage in the new summary-plus-cards layout", async () => {
    renderWithQueryClient(React.createElement(ScopeOverviewPage));

    expect(await screen.findByText("Aevatar / Teams")).toBeTruthy();
    expect(await screen.findByText("我的 AI 团队")).toBeTruthy();
    expect(await screen.findByText("客服团队")).toBeTruthy();
    expect(screen.getByText("活跃团队")).toBeTruthy();
    expect(screen.getByText("运行中成员")).toBeTruthy();
    expect(screen.getByText("健康团队率")).toBeTruthy();
    expect(screen.getByRole("button", { name: "组建新团队" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "切换到卡片视图" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "切换到列表视图" })).toBeTruthy();
    expect(screen.getByLabelText("团队卡片视图")).toBeTruthy();
    expect(screen.getByRole("button", { name: "查看团队" })).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "更多" }).length).toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "显示草稿团队 (1)" })).toBeTruthy();
    expect(screen.queryByText("草稿团队")).toBeNull();

    fireEvent.click(screen.getAllByRole("button", { name: "更多" })[0]);

    expect(await screen.findByText("查看运行")).toBeTruthy();
    expect(screen.getByText("进入 Builder")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "显示草稿团队 (1)" }));

    expect(await screen.findByText("草稿团队")).toBeTruthy();
    fireEvent.click(screen.getAllByRole("button", { name: "更多" })[0]);
    expect(screen.getAllByText("进入 Builder").length).toBeGreaterThan(0);
    expect(
      screen.queryByText(
        "先看到“我有哪些团队、这些团队在做什么、哪里需要我关注”，而不是先看到工程术语和底层模块。",
      ),
    ).toBeNull();
    expect(screen.queryByText("产品意图：")).toBeNull();
  });

  it("lets the user switch from cards to the compact roster manually", async () => {
    renderWithQueryClient(React.createElement(ScopeOverviewPage));

    await screen.findByText("客服团队");
    fireEvent.click(screen.getByRole("button", { name: "切换到列表视图" }));

    expect(await screen.findByLabelText("团队紧凑视图")).toBeTruthy();
    expect(screen.queryByLabelText("团队卡片视图")).toBeNull();
  });

  it("keeps the homepage visible when runtime sampling partially fails", async () => {
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockRejectedValueOnce(
      new Error("No stub for /api/scopes/scope-a/services/service-alpha/runs"),
    );

    renderWithQueryClient(React.createElement(ScopeOverviewPage));

    expect(await screen.findByText("客服团队")).toBeTruthy();
    expect(screen.getByText("部分团队信号暂时不可见")).toBeTruthy();
    expect(
      screen.queryByText("No stub for /api/scopes/scope-a/services/service-alpha/runs"),
    ).toBeNull();
  });

  it("opens the workflow-focused team detail handoff from the primary card action", async () => {
    renderWithQueryClient(React.createElement(ScopeOverviewPage));

    fireEvent.click(await screen.findByRole("button", { name: "查看团队" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams/scope-a");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("workflowId")).toBe("workflow-alpha");
    expect(params.get("serviceId")).toBe("service-alpha");
    expect(params.get("runId")).toBe("run-latest");
  });

  it("resyncs the homepage data when the URL scope changes after mount", async () => {
    renderWithQueryClient(React.createElement(ScopeOverviewPage));

    expect(await screen.findByText("客服团队")).toBeTruthy();

    await act(async () => {
      window.history.replaceState({}, "", "/teams?scopeId=scope-b");
      window.dispatchEvent(
        new PopStateEvent("popstate", { state: window.history.state }),
      );
    });

    expect(await screen.findByText("运营团队")).toBeTruthy();
    expect(screen.queryByText("客服团队")).toBeNull();

    await waitFor(() => {
      expect(scopesApi.listWorkflows).toHaveBeenCalledWith("scope-b");
    });
  });

  it("switches to the compact roster when many teams are visible", async () => {
    (scopesApi.listWorkflows as jest.Mock).mockResolvedValueOnce(
      Array.from({ length: 7 }, (_, index) => ({
        scopeId: "scope-a",
        workflowId: `workflow-${index + 1}`,
        displayName: `团队 ${index + 1}`,
        serviceKey: "scope-a:alpha",
        workflowName: `team-${index + 1}`,
        actorId: `actor://workflow-${index + 1}`,
        activeRevisionId: "rev-2",
        deploymentStatus: "Active",
        deploymentId: "deploy-1",
        updatedAt: `2026-04-13T10:0${Math.min(index, 5)}:00Z`,
      })),
    );

    renderWithQueryClient(React.createElement(ScopeOverviewPage));

    expect(await screen.findByLabelText("团队紧凑视图")).toBeTruthy();
    expect(screen.getByText("团队 1")).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "查看团队" }).length).toBe(7);
  });
});
