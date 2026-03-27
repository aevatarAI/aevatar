import { fireEvent, render, screen } from "@testing-library/react";
import React from "react";
import type { WorkflowActorSnapshot } from "@/shared/models/runtime/actors";
import type { RunEventRow } from "../runEventPresentation";
import type {
  HumanInputRecord,
  RunFocusRecord,
  RunSummaryRecord,
  SelectedWorkflowRecord,
} from "../runWorkbenchConfig";
import RunsInspectorPane from "./RunsInspectorPane";

describe("RunsInspectorPane", () => {
  it("renders the digest, selection, and runtime sidecars sections", () => {
    const onOpenInspector = jest.fn();
    const runFocus: RunFocusRecord = {
      alertType: "warning",
      description: "Operator approval is required before the workflow can continue.",
      label: "Awaiting approval on review-step",
      status: "human_approval",
      title: "Approval required",
    };
    const runSummaryRecord: RunSummaryRecord = {
      activeSteps: ["review-step"],
      actorId: "actor-1",
      commandId: "cmd-1",
      eventCount: 8,
      endpointId: "chat",
      focusLabel: "Awaiting approval on review-step",
      focusStatus: "human_approval",
      lastEventAt: "2026-03-25 10:02:00",
      messageCount: 3,
      runId: "run-1",
      status: "running",
      transport: "ws",
      workflowName: "release_gate",
    };
    const selectedTraceItem: RunEventRow = {
      agentId: "actor-1",
      description: "Step request: review-step",
      eventCategory: "lifecycle",
      eventStatus: "processing",
      eventType: "CUSTOM · StepRequest",
      key: "trace-1",
      payloadPreview: '{"stepId":"review-step"}',
      payloadText: '{"stepId":"review-step"}',
      stepId: "review-step",
      stepType: "approval",
      timelineKey: "step:review-step",
      timelineLabel: "Step · review-step",
      timestamp: "2026-03-25 10:01:00",
    };
    const selectedWorkflowRecord: SelectedWorkflowRecord = {
      description: "A release workflow that pauses for explicit human approval.",
      groupLabel: "Release",
      llmStatus: "processing",
      sourceLabel: "Built-in",
      workflowName: "release_gate",
    };
    const actorSnapshot: WorkflowActorSnapshot = {
      actorId: "actor-1",
      completedSteps: 2,
      lastCommandId: "cmd-1",
      lastError: "",
      lastEventId: "evt-9",
      lastOutput: "Approval summary generated successfully.",
      lastSuccess: true,
      lastUpdatedAt: "2026-03-25 10:02:05",
      requestedSteps: 3,
      roleReplyCount: 4,
      stateVersion: 12,
      totalSteps: 3,
      workflowName: "release_gate",
    };
    const humanInputRecord: HumanInputRecord = {
      prompt: "Approve the release based on the generated summary.",
      runId: "run-1",
      stepId: "review-step",
      suspensionType: "human_approval",
      timeoutSeconds: 600,
    };

    render(
      <RunsInspectorPane
        actorSnapshot={actorSnapshot}
        actorSnapshotLoading={false}
        humanInputRecord={humanInputRecord}
        latestMessagePreview="Approval summary ready for review."
        onOpenInspector={onOpenInspector}
        runFocus={runFocus}
        runSummaryRecord={runSummaryRecord}
        selectedTraceItem={selectedTraceItem}
        selectedWorkflowPrimitives={["approval", "summary", "release"]}
        selectedWorkflowRecord={selectedWorkflowRecord}
      />
    );

    expect(screen.getByText("Run digest")).toBeInTheDocument();
    expect(screen.getByText("Selection")).toBeInTheDocument();
    expect(screen.getByText("Runtime sidecars")).toBeInTheDocument();
    expect(screen.getByText("Latest message")).toBeInTheDocument();
    expect(screen.getByText("Step · review-step")).toBeInTheDocument();
    expect(screen.getByText("Workflow snapshot")).toBeInTheDocument();
    expect(screen.getByText("Actor snapshot")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Open inspector" }));

    expect(onOpenInspector).toHaveBeenCalledTimes(1);
  });
});
