import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { runtimeCatalogApi } from "@/shared/api/runtimeCatalogApi";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import TeamMemberPublishedRunsPage from "./index";

jest.mock("@/shared/api/runtimeCatalogApi", () => ({
  runtimeCatalogApi: {
    listWorkflowCatalog: jest.fn(async () => []),
  },
}));

jest.mock("@/shared/api/scopeRuntimeApi", () => ({
  scopeRuntimeApi: {
    getMemberRunAudit: jest.fn(),
    listMemberRuns: jest.fn(),
  },
}));

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getMember: jest.fn(),
  },
}));

jest.mock("@/shared/graphs/GraphCanvas", () => ({
  __esModule: true,
  default: (props: {
    nodes?: Array<{ id?: string; data?: { stepId?: string } }>;
    onNodeSelect?: (nodeId: string) => void;
  }) => {
    const React = require("react");
    return React.createElement(
      "div",
      { "data-testid": "published-run-graph" },
      props.nodes?.map((node) =>
        React.createElement(
          "button",
          {
            key: node.id,
            onClick: () => props.onNodeSelect?.(String(node.id ?? "")),
            type: "button",
          },
          `node:${node.data?.stepId ?? node.id}`,
        ),
      ),
    );
  },
}));

describe("TeamMemberPublishedRunsPage", () => {
  const mockedRuntimeCatalogApi = runtimeCatalogApi as unknown as {
    listWorkflowCatalog: jest.Mock;
  };
  const mockedScopeRuntimeApi = scopeRuntimeApi as unknown as {
    getMemberRunAudit: jest.Mock;
    listMemberRuns: jest.Mock;
  };
  const mockedStudioApi = studioApi as unknown as {
    getMember: jest.Mock;
  };

  function createDeferred<T>() {
    let resolve!: (value: T) => void;
    const promise = new Promise<T>((nextResolve) => {
      resolve = nextResolve;
    });
    return { promise, resolve };
  }

  beforeEach(() => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/team-1/members/m-alpha/runs",
    );
    jest.clearAllMocks();
    mockedRuntimeCatalogApi.listWorkflowCatalog.mockResolvedValue([]);
    mockedStudioApi.getMember.mockResolvedValue({
      summary: {
        createdAt: "2026-06-22T00:00:00Z",
        description: "",
        displayName: "Alpha Workflow",
        implementationKind: "workflow",
        implementationRef: {
          implementationKind: "workflow",
          workflowId: "wf-alpha",
          workflowRevision: null,
        },
        lastBoundRevisionId: "rev-alpha",
        lifecycleStage: "bind_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "team-1",
        updatedAt: "2026-06-22T00:00:00Z",
      },
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
        workflowRevision: null,
      },
      currentBindingRun: null,
      lastBinding: null,
    });
    mockedScopeRuntimeApi.listMemberRuns.mockResolvedValue({
      displayName: "Alpha Workflow",
      memberId: "m-alpha",
      publishedServiceId: "svc-alpha",
      publishedServiceKey: "scope-1:default:default:svc-alpha",
      runs: [],
      scopeId: "scope-1",
    });
    mockedScopeRuntimeApi.getMemberRunAudit.mockResolvedValue({
      summary: {
        actorId: "actor://scope-1/run-1",
        bindingUpdatedAt: "2026-06-22T01:00:00Z",
        boundAt: "2026-06-22T01:00:00Z",
        completedSteps: 2,
        completionStatus: "completed",
        definitionActorId: "definition://alpha",
        deploymentId: "dep-alpha",
        lastError: "",
        lastEventId: "evt-2",
        lastOutput: "Done",
        lastSuccess: true,
        lastUpdatedAt: "2026-06-22T01:00:02Z",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        revisionId: "rev-alpha",
        roleReplyCount: 0,
        runId: "run-1",
        scopeId: "scope-1",
        stateVersion: 2,
        totalSteps: 2,
        workflowName: "Alpha Workflow",
      },
      audit: {
        commandId: "cmd-1",
        completionStatus: "completed",
        createdAt: "2026-06-22T01:00:00Z",
        durationMs: 0,
        endedAt: "2026-06-22T01:00:02Z",
        finalError: "",
        finalOutput: "Done",
        input: "hello",
        lastEventId: "evt-2",
        projectionScope: "run_isolated",
        reportVersion: "1.0",
        roleReplies: [],
        rootActorId: "actor://scope-1/run-1",
        startedAt: "2026-06-22T01:00:00Z",
        stateVersion: 2,
        steps: [
          {
            assignedValue: "",
            assignedVariable: "",
            branchKey: "",
            completedAt: "2026-06-22T01:00:01Z",
            completionAnnotations: { status: "Ok" },
            durationMs: 1000,
            error: "",
            nextStepId: "answer",
            outputPreview: "Config ok",
            requestParameters: { prompt: "hello" },
            requestedAt: "2026-06-22T01:00:00Z",
            requestedVariableName: "",
            stepId: "config",
            stepType: "transform",
            success: true,
            suspensionPrompt: "",
            suspensionTimeoutSeconds: null,
            suspensionType: "",
            targetRole: "",
            workerId: "worker-1",
          },
          {
            assignedValue: "",
            assignedVariable: "",
            branchKey: "",
            completedAt: "2026-06-22T01:00:02Z",
            completionAnnotations: {},
            durationMs: 1000,
            error: "",
            nextStepId: "",
            outputPreview: "Done",
            requestParameters: {},
            requestedAt: "2026-06-22T01:00:01Z",
            requestedVariableName: "",
            stepId: "answer",
            stepType: "llm_call",
            success: true,
            suspensionPrompt: "",
            suspensionTimeoutSeconds: null,
            suspensionType: "",
            targetRole: "assistant",
            workerId: "worker-2",
          },
        ],
        success: true,
        summary: {
          completedSteps: 2,
          requestedSteps: 2,
          roleReplyCount: 0,
          stepTypeCounts: { llm_call: 1, transform: 1 },
          totalSteps: 2,
        },
        timeline: [
          {
            agentId: "worker-1",
            data: {},
            eventType: "completed",
            message: "Config ok",
            stage: "completed",
            stepId: "config",
            stepType: "transform",
            timestamp: "2026-06-22T01:00:01Z",
          },
        ],
        topology: [{ child: "answer", parent: "config" }],
        topologySource: "committed_projection",
        updatedAt: "2026-06-22T01:00:02Z",
        workflowName: "Alpha Workflow",
      },
    });
  });

  it("renders skeleton panels while published runs are loading", async () => {
    const memberRuns = createDeferred<{
      displayName: string;
      memberId: string;
      publishedServiceId: string;
      publishedServiceKey: string;
      runs: [];
      scopeId: string;
    }>();
    mockedScopeRuntimeApi.listMemberRuns.mockReturnValueOnce(memberRuns.promise);

    renderWithQueryClient(React.createElement(TeamMemberPublishedRunsPage));

    expect(await screen.findByTestId("member-published-runs-replay")).toBeTruthy();
    expect(screen.getByTestId("member-published-runs-list-skeleton")).toBeTruthy();
    expect(screen.getByTestId("member-published-runs-graph-skeleton")).toBeTruthy();
    expect(screen.getByTestId("member-published-runs-details-skeleton")).toBeTruthy();
    expect(screen.queryByText("No published runs yet.")).toBeNull();

    await waitFor(() => {
      expect(mockedScopeRuntimeApi.listMemberRuns).toHaveBeenCalledWith(
        "scope-1",
        "m-alpha",
        { take: 200 },
      );
    });

    memberRuns.resolve({
      displayName: "Alpha Workflow",
      memberId: "m-alpha",
      publishedServiceId: "svc-alpha",
      publishedServiceKey: "scope-1:default:default:svc-alpha",
      runs: [],
      scopeId: "scope-1",
    });
    expect(await screen.findByText("No published runs yet.")).toBeTruthy();
  });

  it("renders a schedule-filtered member run history", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/team-1/members/m-alpha/runs?scheduleId=sch-alpha",
    );
    mockedScopeRuntimeApi.listMemberRuns.mockResolvedValueOnce({
      displayName: "Alpha Workflow",
      memberId: "m-alpha",
      publishedServiceId: "svc-alpha",
      publishedServiceKey: "scope-1:default:default:svc-alpha",
      runs: [],
      scopeId: "scope-1",
    });

    renderWithQueryClient(React.createElement(TeamMemberPublishedRunsPage));

    expect(await screen.findByTestId("member-published-runs-replay")).toBeTruthy();
    expect(screen.getByTestId("member-published-runs-schedule-filter")).toHaveTextContent(
      "Schedule filter",
    );
    expect(screen.getByTestId("member-published-runs-schedule-filter")).toHaveTextContent(
      "sch-alpha",
    );
    expect(
      await screen.findByText(
        "No runs for this schedule yet. Accepted manual runs may take a moment to appear.",
      ),
    ).toBeTruthy();
    await waitFor(() => {
      expect(mockedScopeRuntimeApi.listMemberRuns).toHaveBeenCalledWith(
        "scope-1",
        "m-alpha",
        { scheduleId: "sch-alpha", take: 200 },
      );
    });
    expect(mockedScopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalled();
  });

  it("redirects to the console home when the routed member does not exist", async () => {
    const missingMemberError = Object.assign(
      new Error("member 'm-missing' not found in scope 'scope-1'."),
      {
        code: "STUDIO_MEMBER_NOT_FOUND",
        status: 404,
      },
    );
    mockedStudioApi.getMember.mockRejectedValueOnce(missingMemberError);

    renderWithQueryClient(React.createElement(TeamMemberPublishedRunsPage));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/scopes");
    });
    expect(window.location.search).toBe("");
    expect(mockedScopeRuntimeApi.listMemberRuns).not.toHaveBeenCalled();
    expect(mockedScopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalled();
  });

  it("does not refetch audit for a runId-only route when the catalog has no matching run", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/team-1/members/m-alpha/runs?runId=run-missing",
    );
    mockedScopeRuntimeApi.listMemberRuns.mockResolvedValueOnce({
      displayName: "Alpha Workflow",
      memberId: "m-alpha",
      publishedServiceId: "svc-alpha",
      publishedServiceKey: "scope-1:default:default:svc-alpha",
      runs: [],
      scopeId: "scope-1",
    });

    renderWithQueryClient(React.createElement(TeamMemberPublishedRunsPage));

    expect(await screen.findByTestId("member-published-runs-replay")).toBeTruthy();
    await waitFor(() => {
      expect(mockedScopeRuntimeApi.listMemberRuns).toHaveBeenCalledWith(
        "scope-1",
        "m-alpha",
        { take: 200 },
      );
    });
    expect(mockedScopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalled();

    fireEvent.click(screen.getAllByRole("button", { name: "Refresh" }).at(-1)!);

    await waitFor(() => {
      expect(mockedScopeRuntimeApi.listMemberRuns).toHaveBeenCalledTimes(2);
    });
    expect(mockedScopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalled();
  });

  it("renders member published run history from the canonical Team member route", async () => {
    const backendRunId = "scope-workflow:scope-1:wf-alpha:dep-alpha";
    window.history.replaceState(
      {},
      "",
      `/scopes/scope-1/teams/team-1/members/m-alpha/runs?runId=${encodeURIComponent(
        backendRunId,
      )}`,
    );
    mockedScopeRuntimeApi.listMemberRuns.mockResolvedValueOnce({
      displayName: "Alpha Workflow",
      memberId: "m-alpha",
      publishedServiceId: "svc-alpha",
      publishedServiceKey: "scope-1:default:default:svc-alpha",
      runs: [
        {
          actorId: "actor://scope-1/run-1",
          bindingUpdatedAt: "2026-06-22T01:00:00Z",
          boundAt: "2026-06-22T01:00:00Z",
          completedSteps: 2,
          completionStatus: "completed",
          definitionActorId: "definition://alpha",
          deploymentId: "dep-alpha",
          lastError: "",
          lastEventId: "evt-2",
          lastOutput: "Done",
          lastSuccess: true,
          lastUpdatedAt: "2026-06-22T01:00:02Z",
          memberId: "m-alpha",
          publishedServiceId: "svc-alpha",
          revisionId: "rev-alpha",
          roleReplyCount: 0,
          runId: backendRunId,
          scopeId: "scope-1",
          stateVersion: 2,
          totalSteps: 2,
          workflowName: "Alpha Workflow",
        },
      ],
      scopeId: "scope-1",
    });

    const { container } = renderWithQueryClient(
      React.createElement(TeamMemberPublishedRunsPage),
    );

    expect(await screen.findByTestId("member-published-runs-replay")).toBeTruthy();
    expect(await screen.findByText("node:config")).toBeTruthy();
    expect(container.textContent).toContain("Alpha Workflow");
    expect(container.textContent).toContain("Config ok");
    expect(screen.getByRole("navigation", {
      name: "Published runs navigation",
    })).toBeTruthy();
    expect(screen.getByRole("link", { name: "Teams" })).toHaveAttribute(
      "href",
      "/scopes/scope-1/teams/team-1?tab=overview",
    );
    expect(screen.getByRole("link", { name: "Alpha Workflow" })).toHaveAttribute(
      "href",
      "/scopes/scope-1/teams/team-1?memberId=m-alpha&tab=members",
    );
    expect(screen.queryByLabelText("Search published runs")).toBeNull();
    expect(screen.queryByText("Auto refresh")).toBeNull();
    expect(screen.queryByText("Raw")).toBeNull();
    expect(container.textContent).not.toContain("steps -");
    expect(container.textContent).not.toContain(backendRunId);
    expect(container.textContent).not.toContain("scope-workflow:");
    expect(container.textContent).toContain("2.00s");
    expect(screen.queryByText("0ms")).toBeNull();
    expect(mockedRuntimeCatalogApi.listWorkflowCatalog).not.toHaveBeenCalled();
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Open editor" })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole("button", { name: "Open editor" }));
    expect(window.location.pathname).toBe(
      "/scopes/scope-1/teams/team-1/members/m-alpha/workflow",
    );
    expect(new URLSearchParams(window.location.search).get("workflowId")).toBe(
      "wf-alpha",
    );
    expect(mockedStudioApi.getMember).toHaveBeenCalledWith("scope-1", "m-alpha");
    expect(mockedScopeRuntimeApi.listMemberRuns).toHaveBeenCalledWith(
      "scope-1",
      "m-alpha",
      { take: 200 },
    );
    expect(mockedScopeRuntimeApi.getMemberRunAudit).toHaveBeenCalledWith(
      "scope-1",
      "m-alpha",
      backendRunId,
      { actorId: "actor://scope-1/run-1" },
    );
  });

  it("uses the completed audit status when the run catalog is still running", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/team-1/members/m-alpha/runs?runId=run-1",
    );
    mockedScopeRuntimeApi.listMemberRuns.mockResolvedValueOnce({
      displayName: "Alpha Workflow",
      memberId: "m-alpha",
      publishedServiceId: "svc-alpha",
      publishedServiceKey: "scope-1:default:default:svc-alpha",
      runs: [
        {
          actorId: "actor://scope-1/run-1",
          bindingUpdatedAt: "2026-06-22T01:00:00Z",
          boundAt: "2026-06-22T01:00:00Z",
          completedSteps: 0,
          completionStatus: "running",
          definitionActorId: "definition://alpha",
          deploymentId: "dep-alpha",
          lastError: "",
          lastEventId: "evt-1",
          lastOutput: "",
          lastSuccess: null,
          lastUpdatedAt: "2026-06-22T01:00:00Z",
          memberId: "m-alpha",
          publishedServiceId: "svc-alpha",
          revisionId: "rev-alpha",
          roleReplyCount: 0,
          runId: "run-1",
          scopeId: "scope-1",
          stateVersion: 1,
          totalSteps: 2,
          workflowName: "Alpha Workflow",
        },
      ],
      scopeId: "scope-1",
    });

    renderWithQueryClient(React.createElement(TeamMemberPublishedRunsPage));

    expect(await screen.findByText("node:config")).toBeTruthy();
    expect(screen.queryByText("Running")).toBeNull();
    expect(screen.getAllByText("Completed").length).toBeGreaterThanOrEqual(2);
  });

  it("returns from published runs to the Team members tab", async () => {
    renderWithQueryClient(React.createElement(TeamMemberPublishedRunsPage));

    expect(await screen.findByTestId("member-published-runs-replay")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Back to team members" }));

    expect(window.location.pathname).toBe("/scopes/scope-1/teams/team-1");
    expect(window.location.search).toBe("?memberId=m-alpha&tab=members");
  });

  it("loads the selected member published run audit before the run catalog is materialized", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/team-1/members/m-alpha/runs?scheduleId=sch-alpha&runId=run-1&actorId=actor%3A%2F%2Fscope-1%2Frun-1",
    );
    mockedScopeRuntimeApi.listMemberRuns.mockResolvedValueOnce({
      displayName: "Alpha Workflow",
      memberId: "m-alpha",
      publishedServiceId: "svc-alpha",
      publishedServiceKey: "scope-1:default:default:svc-alpha",
      runs: [],
      scopeId: "scope-1",
    });

    const { container } = renderWithQueryClient(
      React.createElement(TeamMemberPublishedRunsPage),
    );

    expect(await screen.findByTestId("member-published-runs-replay")).toBeTruthy();
    expect(await screen.findByText("node:config")).toBeTruthy();
    expect(container.textContent).toContain("Alpha Workflow");
    expect(container.textContent).toContain("Config ok");
    expect(container.textContent).not.toContain("No published runs yet.");
    expect(mockedScopeRuntimeApi.listMemberRuns).toHaveBeenCalledWith(
      "scope-1",
      "m-alpha",
      { scheduleId: "sch-alpha", take: 200 },
    );
    expect(mockedScopeRuntimeApi.getMemberRunAudit).toHaveBeenCalledWith(
      "scope-1",
      "m-alpha",
      "run-1",
      { actorId: "actor://scope-1/run-1" },
    );
    expect(mockedRuntimeCatalogApi.listWorkflowCatalog).not.toHaveBeenCalled();
  });
});
