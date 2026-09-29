import { render, screen, waitFor } from "@testing-library/react";
import React from "react";
import { WorkflowReplayCanvas } from "./WorkflowReplayCanvas";

type MissionWallWorkflowGraph = import("../models").MissionWallWorkflowGraph;

const mockReactFlowRender = jest.fn();
const mockFitView = jest.fn();
let mockFrameId = 0;
const mockRequestAnimationFrame = jest.fn((callback: FrameRequestCallback): number => {
  callback(0);
  mockFrameId += 1;
  return mockFrameId;
});
const FIT_VIEW_ATTEMPT_COUNT = 4;

jest.mock("@xyflow/react", () => {
  const React = require("react");

  return {
    __esModule: true,
    Background: () => null,
    BackgroundVariant: {
      Lines: "lines",
    },
    Controls: () => null,
    Handle: () => null,
    MarkerType: {
      ArrowClosed: "arrowclosed",
    },
    Position: {
      Left: "left",
      Right: "right",
    },
    ReactFlow: (props: any) => {
      mockReactFlowRender(props);
      React.useEffect(() => {
        props.onInit?.({
          fitView: mockFitView,
        });
      }, []);
      return React.createElement(
        "div",
        null,
        props.nodes?.map((node: any) =>
          React.createElement("div", { key: node.id }, node.data.node.stepId),
        ),
      );
    },
  };
});

function graphFixture(): MissionWallWorkflowGraph {
  return {
    edges: [
      {
        focused: false,
        fromStepId: "validate_input",
        id: "edge:validate_input:normalize_input:next",
        kind: "next",
        toStepId: "normalize_input",
        traversed: true,
      },
      {
        focused: true,
        fromStepId: "normalize_input",
        id: "edge:normalize_input:capture_brief:next",
        kind: "next",
        toStepId: "capture_brief",
        traversed: false,
      },
      {
        focused: true,
        fromStepId: "capture_brief",
        id: "edge:capture_brief:validate_report:next",
        kind: "next",
        toStepId: "validate_report",
        traversed: false,
      },
    ],
    layout: {
      direction: "right",
      engine: "manual",
      stepOverview: [
        { index: 0, status: "completed", stepId: "validate_input" },
        { index: 1, status: "completed", stepId: "normalize_input" },
        { index: 2, status: "active", stepId: "capture_brief" },
        { index: 3, status: "failed", stepId: "validate_report" },
      ],
      totalSteps: 4,
      viewportStepIds: [
        "validate_input",
        "normalize_input",
        "capture_brief",
        "validate_report",
      ],
      windowEndIndex: 3,
      windowStartIndex: 0,
    },
    nodes: [
      {
        focused: false,
        id: "step:validate_input",
        runId: "run-alpha",
        status: "completed",
        stepId: "validate_input",
        stepType: "guard",
      },
      {
        focused: false,
        id: "step:normalize_input",
        runId: "run-alpha",
        status: "completed",
        stepId: "normalize_input",
        stepType: "transform",
      },
      {
        focused: true,
        id: "step:capture_brief",
        runId: "run-alpha",
        status: "active",
        stepId: "capture_brief",
        stepType: "assign",
      },
      {
        error: "max length exceeded",
        focused: false,
        id: "step:validate_report",
        runId: "run-alpha",
        status: "failed",
        stepId: "validate_report",
        stepType: "guard",
      },
    ],
    selectedStepId: "capture_brief",
  };
}

describe("WorkflowReplayCanvas", () => {
  beforeEach(() => {
    jest.useRealTimers();
    Object.defineProperty(window, "requestAnimationFrame", {
      configurable: true,
      value: mockRequestAnimationFrame,
    });
    Object.defineProperty(window, "cancelAnimationFrame", {
      configurable: true,
      value: jest.fn(),
    });
    mockFitView.mockClear();
    mockReactFlowRender.mockClear();
    mockRequestAnimationFrame.mockClear();
    mockFrameId = 0;
  });

  afterEach(() => {
    jest.useRealTimers();
  });

  it("renders the workflow flow with directional runtime edges", () => {
    render(React.createElement(WorkflowReplayCanvas, { graph: graphFixture() }));

    expect(screen.getByText("validate_input")).toBeInTheDocument();
    expect(screen.getByText("validate_report")).toBeInTheDocument();
    expect(screen.queryByText(/Focused steps/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/readmodel/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/viewport steps/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/current execution/i)).not.toBeInTheDocument();

    const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;
    expect(reactFlowProps.nodes).toHaveLength(4);
    expect(reactFlowProps.edges).toHaveLength(3);
    expect(reactFlowProps.nodeTypes).toHaveProperty("missionWallWorkflowStep");
    const nodeIds = new Set(reactFlowProps.nodes.map((node: any) => node.id));
    expect(
      reactFlowProps.edges.every(
        (edge: any) => nodeIds.has(edge.source) && nodeIds.has(edge.target),
      ),
    ).toBe(true);

    const focusedEdge = reactFlowProps.edges.find(
      (edge: any) => edge.id === "edge:normalize_input:capture_brief:next",
    );
    expect(focusedEdge.animated).toBe(false);
    expect(focusedEdge.className).toBe("mission-wall-flow-edge--focused");
    expect(focusedEdge.markerEnd.type).toBe("arrowclosed");
    expect(focusedEdge.style.stroke).toBe("#2dd4bf");

    const failedEdge = reactFlowProps.edges.find(
      (edge: any) => edge.id === "edge:capture_brief:validate_report:next",
    );
    expect(failedEdge.style.stroke).toBe("#f87171");
    expect(failedEdge.markerEnd.color).toBe("#f87171");
  });

  it("refits after audit refreshes so nodes cannot remain stranded off-screen", async () => {
    const { rerender } = render(
      React.createElement(WorkflowReplayCanvas, { graph: graphFixture() }),
    );

    await waitFor(() =>
      expect(mockFitView).toHaveBeenCalledTimes(FIT_VIEW_ATTEMPT_COUNT),
    );

    const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;
    expect(reactFlowProps.fitView).toBe(true);

    reactFlowProps.onMove?.({ type: "mousemove" }, { x: 120, y: 0, zoom: 1 });

    const refreshedGraph = {
      ...graphFixture(),
      nodes: graphFixture().nodes.map((node) =>
        node.stepId === "capture_brief"
          ? {
              ...node,
              latencyMs: 1240,
              outputPreview: "refreshed output",
            }
          : node,
      ),
    };

    rerender(
      React.createElement(WorkflowReplayCanvas, { graph: refreshedGraph }),
    );

    expect(mockFitView).toHaveBeenCalledTimes(FIT_VIEW_ATTEMPT_COUNT * 2);
  });

  it("refits the graph when the focused step changes after a viewport move", async () => {
    const { rerender } = render(
      React.createElement(WorkflowReplayCanvas, { graph: graphFixture() }),
    );

    await waitFor(() =>
      expect(mockFitView).toHaveBeenCalledTimes(FIT_VIEW_ATTEMPT_COUNT),
    );

    const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;
    reactFlowProps.onMove?.({ type: "mousemove" }, { x: 120, y: 0, zoom: 1 });

    rerender(
      React.createElement(WorkflowReplayCanvas, {
        graph: {
          ...graphFixture(),
          selectedStepId: "validate_report",
        },
      }),
    );

    expect(mockFitView).toHaveBeenCalledTimes(FIT_VIEW_ATTEMPT_COUNT * 2);
  });

  it("retries the initial fit across several animation frames so late node measurement cannot leave a blank grid", async () => {
    render(React.createElement(WorkflowReplayCanvas, { graph: graphFixture() }));

    await waitFor(() =>
      expect(mockFitView).toHaveBeenCalledTimes(FIT_VIEW_ATTEMPT_COUNT),
    );

    expect(mockRequestAnimationFrame).toHaveBeenCalledTimes(FIT_VIEW_ATTEMPT_COUNT);
    expect(mockFitView).toHaveBeenLastCalledWith(
      expect.objectContaining({
        nodes: expect.arrayContaining([
          expect.objectContaining({ id: "step:validate_input" }),
          expect.objectContaining({ id: "step:capture_brief" }),
        ]),
      }),
    );
  });

  it("keeps React Flow auto-fit queued for the focused window", () => {
    render(React.createElement(WorkflowReplayCanvas, { graph: graphFixture() }));

    const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;

    expect(reactFlowProps.fitView).toBe(true);
    expect(reactFlowProps.fitViewOptions).toEqual({
      duration: 0,
      maxZoom: 1.05,
      minZoom: 0.36,
      nodes: expect.arrayContaining([
        expect.objectContaining({ id: "step:validate_input" }),
        expect.objectContaining({ id: "step:capture_brief" }),
      ]),
      padding: 0.24,
    });
  });

});
