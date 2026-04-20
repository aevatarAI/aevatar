import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import React from "react";
import { runtimeActorsApi } from "@/shared/api/runtimeActorsApi";
import { runtimeQueryApi } from "@/shared/api/runtimeQueryApi";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import ActorsPage from "./index";

jest.mock("@/shared/api/runtimeActorsApi", () => ({
  runtimeActorsApi: {
    getActorSnapshot: jest.fn(),
    getActorTimeline: jest.fn(),
    getActorGraphEnriched: jest.fn(),
    getActorGraphEdges: jest.fn(),
    getActorGraphSubgraph: jest.fn(),
  },
}));

jest.mock("@/shared/api/runtimeQueryApi", () => ({
  runtimeQueryApi: {
    listAgents: jest.fn(async () => []),
  },
}));

jest.mock("@/shared/graphs/GraphCanvas", () => ({
  __esModule: true,
  default: () => {
    const React = require("react");
    return React.createElement("div", null, "GraphCanvas");
  },
}));

const actorCatalog = [
  {
    description: "WorkflowRunGAgent[SupportRoot]",
    id: "actor://workflow/customer-support/root-supervisor",
    type: "WorkflowRunGAgent",
  },
  {
    description: "WorkflowRunGAgent[SupportPlanner]",
    id: "actor://workflow/customer-support/planner",
    type: "WorkflowRunGAgent",
  },
];

function buildActorSnapshot(actorId: string) {
  return {
    actorId,
    completedSteps: actorId.endsWith("/planner") ? 4 : 2,
    completionStatusValue: actorId.endsWith("/planner") ? 1 : 0,
    lastCommandId: "cmd-customer-support",
    lastError: "",
    lastEventId: "evt-customer-support",
    lastOutput: actorId.endsWith("/planner")
      ? "Planner completed routing."
      : "Supervisor is waiting for downstream checks.",
    lastSuccess: actorId.endsWith("/planner"),
    lastUpdatedAt: "2026-04-16T05:40:12Z",
    requestedSteps: 4,
    roleReplyCount: actorId.endsWith("/planner") ? 2 : 1,
    stateVersion: actorId.endsWith("/planner") ? 7 : 5,
    totalSteps: 4,
    workflowName: actorId.endsWith("/planner") ? "SupportPlanner" : "SupportRoot",
  };
}

function buildActorTimeline(actorId: string) {
  return [
    {
      agentId: actorId,
      data: {},
      eventType: "StepCompleted",
      message: actorId.endsWith("/planner")
        ? "Classification completed and plan published."
        : "Supervisor received escalation request.",
      stage: actorId.endsWith("/planner") ? "workflow.completed" : "workflow.running",
      stepId: actorId.endsWith("/planner") ? "plan" : "receive-request",
      stepType: actorId.endsWith("/planner") ? "tool_call" : "message_ingress",
      timestamp: "2026-04-16T05:40:12Z",
    },
  ];
}

function buildActorGraph(actorId: string) {
  return {
    snapshot: {
      actorId,
    },
    subgraph: {
      edges: [
        {
          edgeId: "edge-owns",
          edgeType: "OWNS",
          fromNodeId: actorId,
          toNodeId: "run://customer-support/current",
          updatedAt: "2026-04-16T05:40:12Z",
        },
      ],
      nodes: [
        {
          nodeId: actorId,
          nodeType: "Actor",
          properties: {
            role: actorId.endsWith("/planner") ? "planner" : "supervisor",
            workflowName: actorId.endsWith("/planner")
              ? "CustomerSupportPlanner"
              : "CustomerSupportTriage",
          },
          updatedAt: "2026-04-16T05:40:12Z",
        },
        {
          nodeId: "run://customer-support/current",
          nodeType: "WorkflowRun",
          properties: {
            commandId: "cmd-customer-support",
            workflowName: "CustomerSupportTriage",
          },
          updatedAt: "2026-04-16T05:40:12Z",
        },
      ],
      rootNodeId: actorId,
    },
  };
}

describe("ActorsPage", () => {
  const findActorRow = (needle: string) =>
    screen.getAllByRole("row").find((row) => row.textContent?.includes(needle)) ?? null;

  beforeEach(() => {
    window.localStorage.clear();
    window.history.replaceState({}, "", "/runtime/explorer");
    (runtimeQueryApi.listAgents as jest.Mock).mockReset();
    (runtimeActorsApi.getActorSnapshot as jest.Mock).mockReset();
    (runtimeActorsApi.getActorTimeline as jest.Mock).mockReset();
    (runtimeActorsApi.getActorGraphEnriched as jest.Mock).mockReset();

    (runtimeQueryApi.listAgents as jest.Mock).mockResolvedValue(actorCatalog);
    (runtimeActorsApi.getActorSnapshot as jest.Mock).mockImplementation(
      async (actorId: string) => buildActorSnapshot(actorId),
    );
    (runtimeActorsApi.getActorTimeline as jest.Mock).mockImplementation(
      async (actorId: string) => buildActorTimeline(actorId),
    );
    (runtimeActorsApi.getActorGraphEnriched as jest.Mock).mockImplementation(
      async (actorId: string) => buildActorGraph(actorId),
    );
  });

  it("renders the live runtime explorer shell and actor list", async () => {
    const { container } = renderWithQueryClient(React.createElement(ActorsPage));

    await screen.findByText("SupportRoot");

    expect(container.textContent).toContain("Aevatar / Platform");
    expect(container.textContent).toContain("Topology");
    expect(container.textContent).toContain(
      "Topology 是 Platform 的运行关系追查台，围绕后端真实 workflow run actor 还原 graph、timeline、edge 和 snapshot 证据。",
    );
    expect(container.textContent).toContain("真实数据");
    expect(screen.getByPlaceholderText("输入 Actor ID")).toBeTruthy();
    expect(screen.getByPlaceholderText("筛选 Actor")).toBeTruthy();
    expect(screen.getByRole("button", { name: "刷新列表" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "打开追查详情" })).toBeTruthy();
    expect(screen.queryByText("示例数据")).toBeNull();
    expect(container.textContent).toContain("SupportRoot");
    expect(screen.getAllByRole("button", { name: "查看概览" }).length).toBeGreaterThan(0);
  });

  it("opens the dedicated detail page when an actor id is entered directly", async () => {
    renderWithQueryClient(React.createElement(ActorsPage));

    fireEvent.change(screen.getByPlaceholderText("输入 Actor ID"), {
      target: { value: "actor://workflow/customer-support/planner" },
    });
    fireEvent.click(screen.getByRole("button", { name: "打开追查详情" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/runtime/explorer/detail");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("actorId")).toBe("actor://workflow/customer-support/planner");
  });

  it("opens a live preview drawer from the real actor list", async () => {
    renderWithQueryClient(React.createElement(ActorsPage));

    await screen.findByText("SupportPlanner");
    const plannerRow = findActorRow("SupportPlanner");

    expect(plannerRow).toBeTruthy();

    fireEvent.click(
      within(plannerRow as HTMLElement).getByRole("button", { name: "查看概览" }),
    );

    expect(await screen.findByText("对象快速概览")).toBeTruthy();
    await waitFor(() => {
      expect(runtimeActorsApi.getActorSnapshot).toHaveBeenCalledWith(
        "actor://workflow/customer-support/planner",
      );
    });
  });

  it("shows a dedicated unavailable message when preview actor snapshot is gone", async () => {
    (runtimeActorsApi.getActorSnapshot as jest.Mock).mockRejectedValueOnce(
      new Error("HTTP 404 Not Found"),
    );
    (runtimeActorsApi.getActorGraphEnriched as jest.Mock).mockRejectedValueOnce(
      new Error("HTTP 404 Not Found"),
    );

    renderWithQueryClient(React.createElement(ActorsPage));

    await screen.findByText("SupportPlanner");
    const plannerRow = findActorRow("SupportPlanner");

    expect(plannerRow).toBeTruthy();

    fireEvent.click(
      within(plannerRow as HTMLElement).getByRole("button", { name: "查看概览" }),
    );

    expect(await screen.findByText("这个 actor 当前不可预览")).toBeTruthy();
    expect(screen.getByText("当前后端还能引用这个 actor，但已经查不到它的 snapshot。常见原因是后端重启、运行态已清理，或这是历史绑定残留。")).toBeTruthy();
  });

  it("keeps the list page route without a detail actor selection", async () => {
    renderWithQueryClient(React.createElement(ActorsPage));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/runtime/explorer");
    });
    expect(window.location.search).toBe("");
  });
});
