import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import MissionWallPage from "./index";

type ScopeServiceRunAuditSnapshot =
  import("@/shared/models/runtime/scopeServices").ScopeServiceRunAuditSnapshot;
type ScopeMemberRunAuditSnapshot =
  import("@/shared/models/runtime/scopeServices").ScopeMemberRunAuditSnapshot;
type ScopeMemberRunSummary =
  import("@/shared/models/runtime/scopeServices").ScopeMemberRunSummary;
type ScopeServiceRunAuditReport =
  import("@/shared/models/runtime/scopeServices").ScopeServiceRunAuditReport;
type ScopeServiceRunAuditStep =
  import("@/shared/models/runtime/scopeServices").ScopeServiceRunAuditStep;
type ScopeServiceRunSummary =
  import("@/shared/models/runtime/scopeServices").ScopeServiceRunSummary;
type StudioAuthSession = import("@/shared/studio/models").StudioAuthSession;
type StudioMemberSummary = import("@/shared/studio/models").StudioMemberSummary;
type StudioWorkflowBoardSnapshot =
  import("@/shared/studio/models").StudioWorkflowBoardSnapshot;

jest.mock("@/shared/api/scopeRuntimeApi", () => ({
  scopeRuntimeApi: {
    getMemberRunAudit: jest.fn(),
    getServiceRunAudit: jest.fn(),
    listMemberRuns: jest.fn(),
    listServiceRuns: jest.fn(),
    listServices: jest.fn(),
  },
}));

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(),
    getWorkflowBoardSnapshot: jest.fn(),
    listMembers: jest.fn(),
    listTeams: jest.fn(),
  },
}));

const NOW = "2026-06-30T05:00:00.000Z";

function workflowMember(input: {
  readonly displayName: string;
  readonly memberId: string;
  readonly publishedServiceId: string;
  readonly teamId?: string;
  readonly workflowId: string;
}): StudioMemberSummary {
  return {
    createdAt: "2026-06-29T01:00:00.000Z",
    description: "",
    displayName: input.displayName,
    implementationKind: "workflow",
    implementationRef: {
      implementationKind: "workflow",
      workflowId: input.workflowId,
      workflowRevision: "wf-rev-1",
    },
    lastBoundRevisionId: "member-rev-1",
    lifecycleStage: "bind_ready",
    memberId: input.memberId,
    publishedServiceId: input.publishedServiceId,
    scopeId: "scope-real",
    teamId: input.teamId,
    updatedAt: "2026-06-29T01:15:00.000Z",
  };
}

function runSummary(input: {
  readonly actorId: string;
  readonly completedSteps: number;
  readonly completionStatus: string;
  readonly lastUpdatedAt: string;
  readonly runId: string;
  readonly serviceId: string;
  readonly totalSteps: number;
  readonly workflowName: string;
}): ScopeServiceRunSummary {
  return {
    actorId: input.actorId,
    bindingUpdatedAt: "2026-06-30T04:40:00.000Z",
    boundAt: "2026-06-30T04:40:00.000Z",
    completedSteps: input.completedSteps,
    completionStatus: input.completionStatus,
    definitionActorId: `definition-${input.runId}`,
    deploymentId: `deployment-${input.runId}`,
    lastError: "",
    lastEventId: `event-${input.runId}`,
    lastOutput: "",
    lastSuccess: input.completionStatus === "completed",
    lastUpdatedAt: input.lastUpdatedAt,
    revisionId: `revision-${input.runId}`,
    roleReplyCount: input.completedSteps,
    runId: input.runId,
    scopeId: "scope-real",
    serviceId: input.serviceId,
    stateVersion: input.completedSteps + 10,
    totalSteps: input.totalSteps,
    workflowName: input.workflowName,
  };
}

function memberRunSummary(
  run: ScopeServiceRunSummary,
  member: StudioMemberSummary,
): ScopeMemberRunSummary {
  return {
    actorId: run.actorId,
    bindingUpdatedAt: run.bindingUpdatedAt,
    boundAt: run.boundAt,
    completedSteps: run.completedSteps,
    completionStatus: run.completionStatus,
    definitionActorId: run.definitionActorId,
    deploymentId: run.deploymentId,
    lastError: run.lastError,
    lastEventId: run.lastEventId,
    lastOutput: run.lastOutput,
    lastSuccess: run.lastSuccess,
    lastUpdatedAt: run.lastUpdatedAt,
    memberId: member.memberId,
    publishedServiceId: member.publishedServiceId,
    revisionId: run.revisionId,
    roleReplyCount: run.roleReplyCount,
    runId: run.runId,
    scopeId: run.scopeId,
    stateVersion: run.stateVersion,
    totalSteps: run.totalSteps,
    workflowName: run.workflowName,
  };
}

function auditStep(input: {
  readonly completedAt?: string | null;
  readonly nextStepId?: string;
  readonly requestedAt: string;
  readonly stepId: string;
  readonly stepType: string;
  readonly success?: boolean | null;
  readonly targetRole: string;
}): ScopeServiceRunAuditStep {
  return {
    assignedValue: "",
    assignedVariable: "",
    branchKey: "",
    completedAt: input.completedAt ?? null,
    completionAnnotations: {},
    durationMs: input.completedAt ? 1000 : null,
    error: "",
    nextStepId: input.nextStepId ?? "",
    outputPreview: input.success ? `${input.stepId} output` : "",
    requestParameters: {
      prompt: `${input.stepId} prompt`,
    },
    requestedAt: input.requestedAt,
    requestedVariableName: "",
    stepId: input.stepId,
    stepType: input.stepType,
    success: input.success ?? null,
    suspensionPrompt: "",
    suspensionTimeoutSeconds: null,
    suspensionType: "",
    targetRole: input.targetRole,
    workerId: `${input.targetRole}-worker`,
  };
}

function auditSteps(count: number): ScopeServiceRunAuditStep[] {
  return Array.from({ length: count }, (_, index) => {
    const stepNumber = index + 1;
    return auditStep({
      completedAt:
        stepNumber < count ? "2026-06-30T04:58:20.000Z" : undefined,
      nextStepId: stepNumber < count ? `flow_step_${stepNumber + 1}` : undefined,
      requestedAt: `2026-06-30T04:${String(50 + index).padStart(2, "0")}:00.000Z`,
      stepId: `flow_step_${stepNumber}`,
      stepType: "role_task",
      success: stepNumber < count ? true : null,
      targetRole: "analyst",
    });
  });
}

function auditSnapshot(
  summary: ScopeServiceRunSummary,
  steps: readonly ScopeServiceRunAuditStep[],
  overrides?: Partial<ScopeServiceRunAuditReport>,
): ScopeServiceRunAuditSnapshot {
  const audit: ScopeServiceRunAuditReport = {
    commandId: `command-${summary.runId}`,
    completionStatus: summary.completionStatus,
    createdAt: summary.boundAt,
    durationMs: 24_000,
    endedAt: null,
    finalError: "",
    finalOutput: "",
    input: "",
    lastEventId: summary.lastEventId,
    projectionScope: "scope",
    reportVersion: "1",
    roleReplies: [],
    rootActorId: summary.actorId,
    startedAt: summary.boundAt,
    stateVersion: summary.stateVersion,
    steps,
    success: summary.lastSuccess,
    summary: {
      completedSteps: summary.completedSteps,
      requestedSteps: steps.length,
      roleReplyCount: summary.roleReplyCount,
      stepTypeCounts: {},
      totalSteps: summary.totalSteps,
    },
    timeline: [],
    topology: [],
    topologySource: "readmodel",
    updatedAt: summary.lastUpdatedAt,
    workflowName: summary.workflowName,
    ...overrides,
  };

  return {
    audit,
    summary,
  };
}

function memberAuditSnapshot(
  summary: ScopeServiceRunSummary,
  member: StudioMemberSummary,
  steps: readonly ScopeServiceRunAuditStep[],
  overrides?: Partial<ScopeServiceRunAuditReport>,
): ScopeMemberRunAuditSnapshot {
  const serviceAudit = auditSnapshot(summary, steps, overrides);
  return {
    audit: serviceAudit.audit,
    summary: memberRunSummary(serviceAudit.summary, member),
  };
}

function workflowBoardSnapshot(
  members: readonly StudioWorkflowBoardSnapshot["teams"][number]["members"][number][],
  overrides?: Partial<StudioWorkflowBoardSnapshot>,
): StudioWorkflowBoardSnapshot {
  return {
    counts: {
      completed: members.filter((member) => member.executionStatus === "completed")
        .length,
      failed: members.filter((member) => member.executionStatus === "failed")
        .length,
      retrying: members.filter((member) => member.executionStatus === "retrying")
        .length,
      running: members.filter((member) => member.executionStatus === "running")
        .length,
      waiting: members.filter((member) => member.executionStatus === "waiting")
        .length,
    },
    generatedAt: NOW,
    lastNodeUpdatedAt: "2026-06-30T04:59:20.000Z",
    scopeId: "scope-real",
    teams: [
      {
        members,
        teamId: "team-alpha",
        teamName: "Alpha Team",
        totalMemberCount: 8,
      },
    ],
    watermark: "workflow-board:v2:test:facts",
    ...overrides,
  };
}

function workflowBoardCurrentNodeStatus(
  status: StudioWorkflowBoardSnapshot["teams"][number]["members"][number]["executionStatus"],
): NonNullable<
  StudioWorkflowBoardSnapshot["teams"][number]["members"][number]["currentNode"]
>["status"] {
  switch (status) {
    case "completed":
    case "failed":
    case "running":
    case "waiting":
      return status;
    default:
      return "unknown";
  }
}

function workflowBoardMember(input: {
  readonly actorId: string;
  readonly completedSteps: number;
  readonly currentNodeStatus?: NonNullable<
    StudioWorkflowBoardSnapshot["teams"][number]["members"][number]["currentNode"]
  >["status"];
  readonly durationMs?: number;
  readonly executionStatus: StudioWorkflowBoardSnapshot["teams"][number]["members"][number]["executionStatus"];
  readonly lastNodeUpdatedAt: string;
  readonly member: StudioMemberSummary;
  readonly runId: string;
  readonly totalSteps: number;
  readonly workflowName: string;
}): StudioWorkflowBoardSnapshot["teams"][number]["members"][number] {
  return {
    actorId: input.actorId,
    completedNodes: Array.from({ length: input.completedSteps }, (_, index) => ({
      completedAt: "2026-06-30T04:58:20.000Z",
      durationMs: 1000,
      name: `Completed ${index + 1}`,
      nodeId: `completed_${index + 1}`,
    })),
    currentExecutionId: input.runId,
    currentNode: {
      durationMs: input.durationMs,
      name: input.executionStatus === "waiting" ? "risk_gate" : "Current",
      nodeId: input.executionStatus === "waiting" ? "risk_gate" : "current",
      startedAt: "2026-06-30T04:58:00.000Z",
      status:
        input.currentNodeStatus ??
        workflowBoardCurrentNodeStatus(input.executionStatus),
      updatedAt: input.lastNodeUpdatedAt,
    },
    displayName: input.member.displayName,
    executionAvailability: "available",
    executionStatus: input.executionStatus,
    failedNodes:
      input.executionStatus === "failed"
        ? [
            {
              failedAt: input.lastNodeUpdatedAt,
              name: "invoice_match",
              nodeId: "invoice_match",
            },
          ]
        : [],
    lastNodeUpdatedAt: input.lastNodeUpdatedAt,
    memberId: input.member.memberId,
    pendingNodes:
      input.executionStatus === "waiting"
        ? [
            {
              name: "risk_gate",
              nodeId: "risk_gate",
              reason: "waiting for input",
              status: "waiting",
            },
          ]
        : [],
    progress: {
      completedSteps: input.completedSteps,
      totalSteps: input.totalSteps,
    },
    publishedServiceId: input.member.publishedServiceId,
    roleSummary: input.member.displayName,
    workflowId: input.member.implementationRef?.workflowId,
    workflowName: input.workflowName,
  };
}

describe("MissionWallPage", () => {
  const riskMember = workflowMember({
    displayName: "Risk desk member",
    memberId: "m-risk",
    publishedServiceId: "svc-risk",
    teamId: "team-alpha",
    workflowId: "wf-risk-draft",
  });
  const billingMember = workflowMember({
    displayName: "Billing member",
    memberId: "m-billing",
    publishedServiceId: "svc-billing",
    teamId: "team-alpha",
    workflowId: "wf-billing-draft",
  });
  const riskRun = runSummary({
    actorId: "actor-risk-run",
    completedSteps: 1,
    completionStatus: "running",
    lastUpdatedAt: "2026-06-30T04:59:20.000Z",
    runId: "run-risk",
    serviceId: "svc-risk",
    totalSteps: 3,
    workflowName: "Live Risk Workflow",
  });
  const billingRun = runSummary({
    actorId: "actor-billing-run",
    completedSteps: 2,
    completionStatus: "failed",
    lastUpdatedAt: "2026-06-29T21:53:00.000Z",
    runId: "run-billing",
    serviceId: "svc-billing",
    totalSteps: 3,
    workflowName: "Billing Workflow",
  });
  const idleMember = workflowMember({
    displayName: "Idle member",
    memberId: "m-idle",
    publishedServiceId: "svc-idle",
    teamId: "team-alpha",
    workflowId: "wf-idle-draft",
  });

  beforeEach(() => {
    window.history.replaceState({}, "", "/runtime/mission-wall");
    jest.spyOn(Date, "now").mockReturnValue(Date.parse(NOW));
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      authenticated: true,
      enabled: true,
      scopeId: "scope-real",
      scopeSource: "session",
    } satisfies StudioAuthSession);
    (studioApi.getWorkflowBoardSnapshot as jest.Mock).mockResolvedValue(
      workflowBoardSnapshot([
        workflowBoardMember({
          actorId: "actor-risk-run",
          completedSteps: 1,
          currentNodeStatus: "waiting",
          executionStatus: "running",
          lastNodeUpdatedAt: "2026-06-30T04:59:20.000Z",
          member: riskMember,
          runId: "run-risk",
          totalSteps: 3,
          workflowName: "Live Risk Workflow",
        }),
        workflowBoardMember({
          actorId: "actor-billing-run",
          completedSteps: 2,
          executionStatus: "failed",
          lastNodeUpdatedAt: "2026-06-29T21:53:00.000Z",
          member: billingMember,
          runId: "run-billing",
          totalSteps: 3,
          workflowName: "Billing Workflow",
        }),
        {
          actorId: undefined,
          completedNodes: [],
          currentExecutionId: undefined,
          currentNode: undefined,
          displayName: "Idle member",
          executionAvailability: "unavailable",
          executionStatus: "unknown",
          failedNodes: [],
          lastNodeUpdatedAt: "2026-06-29T01:15:00.000Z",
          memberId: idleMember.memberId,
          pendingNodes: [],
          progress: {
            completedSteps: 0,
            totalSteps: 0,
          },
          publishedServiceId: idleMember.publishedServiceId,
          roleSummary: idleMember.displayName,
          workflowId: idleMember.implementationRef?.workflowId,
          workflowName: "Idle member",
        },
      ]),
    );
    (scopeRuntimeApi.getMemberRunAudit as jest.Mock).mockImplementation(
      async (_scopeId: string, memberId: string, runId: string) => {
        if (memberId === "m-risk" && runId === "run-risk") {
          return memberAuditSnapshot(riskRun, riskMember, [
            auditStep({
              completedAt: "2026-06-30T04:58:20.000Z",
              nextStepId: "risk_gate",
              requestedAt: "2026-06-30T04:58:10.000Z",
              stepId: "risk_collect",
              stepType: "role_task",
              success: true,
              targetRole: "analyst",
            }),
            auditStep({
              requestedAt: "2026-06-30T04:59:10.000Z",
              stepId: "risk_gate",
              stepType: "human_approval",
              targetRole: "approver",
            }),
          ]);
        }

        return memberAuditSnapshot(billingRun, billingMember, [
          auditStep({
            completedAt: "2026-06-30T04:57:30.000Z",
            nextStepId: "invoice_match",
            requestedAt: "2026-06-30T04:57:00.000Z",
            stepId: "ledger_lookup",
            stepType: "connector_call",
            success: true,
            targetRole: "ledger",
          }),
          auditStep({
            requestedAt: "2026-06-30T04:58:00.000Z",
            stepId: "invoice_match",
            stepType: "role_task",
            targetRole: "billing_agent",
          }),
        ]);
      },
    );
  });

  it("loads one latest execution row per workflow member from the backend snapshot", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    expect(await screen.findByText("Live Risk Workflow")).toBeInTheDocument();
    expect(screen.getByText("Billing Workflow")).toBeInTheDocument();
    expect(screen.queryByText("Script member")).not.toBeInTheDocument();

    await waitFor(() => {
      expect(studioApi.getWorkflowBoardSnapshot).toHaveBeenCalledWith(
        "scope-real",
        {
          take: 100,
        },
      );
    });

    expect(scopeRuntimeApi.listServices).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.listMemberRuns).not.toHaveBeenCalled();
  });

  it("does not expand multiple service catalog runs into duplicate member rows", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    expect(await screen.findByText("Live Risk Workflow")).toBeInTheDocument();

    const list = screen.getByTestId("mission-wall-run-list");
    expect(within(list).getAllByText("Live Risk Workflow")).toHaveLength(1);
    expect(within(list).queryByText("Older Risk Workflow")).toBeNull();
    expect(within(list).queryByText("run-risk-old")).toBeNull();
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalled();
  });

  it("filters the backend snapshot by route team without submitting member ids", async () => {
    window.history.replaceState(
      {},
      "",
      "/runtime/mission-wall?scopeId=scope-real&teamId=team-alpha",
    );

    renderWithQueryClient(React.createElement(MissionWallPage));

    expect(await screen.findByText("Live Risk Workflow")).toBeInTheDocument();

    await waitFor(() => {
      expect(studioApi.getWorkflowBoardSnapshot).toHaveBeenCalledWith(
        "scope-real",
        {
          take: 100,
          teamId: "team-alpha",
        },
      );
    });
  });

  it("keeps ellipsized mission wall labels passive without full-text reveal affordances", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    expect(await screen.findByText("Live Risk Workflow")).toBeInTheDocument();
    expect(
      await screen.findByText(/Live Risk Workflow · Step Flow/),
    ).toBeInTheDocument();

    const root = document.querySelector(".mission-wall");
    expect(root).toBeTruthy();

    const clippedLabels = Array.from(
      root!.querySelectorAll(
        [
          ".mission-wall-brand__title",
          ".mission-wall-run-card__name",
          ".mission-wall-run-card__team",
          ".mission-wall-run-card__stage",
          ".mission-wall-stage-title",
          ".mission-wall-stage-subtitle",
          ".mission-wall-step-node__name",
          ".mission-wall-step-node__type",
          ".mission-wall-step-node__meta span",
        ].join(", "),
      ),
    );

    expect(clippedLabels.length).toBeGreaterThan(6);

    for (const label of clippedLabels) {
      expect(label).not.toHaveAttribute("title");
      expect(label).not.toHaveAttribute("aria-expanded");
      expect(label).not.toHaveAttribute("aria-controls");
      expect(window.getComputedStyle(label).pointerEvents).toBe("none");
      expect(window.getComputedStyle(label).userSelect).toBe("none");
    }
  });

  it("keeps selected run cards visually aligned to their status tone", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    const riskCard = (await screen.findByText("Live Risk Workflow")).closest(
      "button",
    );
    expect(riskCard).toBeTruthy();
    expect(riskCard).toHaveClass("mission-wall-run-card--focus");

    const missionWallStyle = Array.from(document.querySelectorAll("style"))
      .map((style) => style.textContent ?? "")
      .join("\n");
    const focusRule =
      missionWallStyle.match(/\.mission-wall-run-card--focus\s*{[^}]*}/)?.[0] ??
      "";

    expect(focusRule).not.toContain("border-color");
    expect(focusRule).not.toContain("box-shadow");
  });

  it("refreshes the left run window when a new workflow member appears", async () => {
    const freshMember = workflowMember({
      displayName: "Fresh workflow member",
      memberId: "m-fresh",
      publishedServiceId: "svc-fresh",
      teamId: "team-alpha",
      workflowId: "wf-fresh-draft",
    });
    let includeFreshMember = false;
    let riskExecutionStatus: "running" | "completed" = "running";
    (studioApi.getWorkflowBoardSnapshot as jest.Mock).mockImplementation(
      async () =>
        workflowBoardSnapshot([
          workflowBoardMember({
            actorId: "actor-risk-run",
            completedSteps: riskExecutionStatus === "completed" ? 3 : 1,
            currentNodeStatus:
              riskExecutionStatus === "completed" ? "completed" : "waiting",
            executionStatus: riskExecutionStatus,
            lastNodeUpdatedAt: "2026-06-30T04:59:20.000Z",
            member: riskMember,
            runId: "run-risk",
            totalSteps: 3,
            workflowName: "Live Risk Workflow",
          }),
          workflowBoardMember({
            actorId: "actor-billing-run",
            completedSteps: 2,
            executionStatus: "failed",
            lastNodeUpdatedAt: "2026-06-29T21:53:00.000Z",
            member: billingMember,
            runId: "run-billing",
            totalSteps: 3,
            workflowName: "Billing Workflow",
          }),
          ...(includeFreshMember
            ? [
                workflowBoardMember({
                  actorId: "actor-fresh-run",
                  completedSteps: 0,
                  executionStatus: "running",
                  lastNodeUpdatedAt: "2026-06-30T04:59:58.000Z",
                  member: freshMember,
                  runId: "run-fresh",
                  totalSteps: 15,
                  workflowName: "Fresh Workflow",
                }),
              ]
            : []),
        ]),
    );

    const { queryClient } = renderWithQueryClient(
      React.createElement(MissionWallPage),
    );

    expect(await screen.findByText("Live Risk Workflow")).toBeInTheDocument();
    expect(screen.queryByText("Fresh Workflow")).not.toBeInTheDocument();

    includeFreshMember = true;
    await queryClient.invalidateQueries({ queryKey: ["mission-wall"] });

    expect(await screen.findByText("Fresh Workflow")).toBeInTheDocument();

    const list = screen.getByTestId("mission-wall-run-list");
    const cards = within(list).getAllByRole("button");
    expect(cards[0]).toHaveTextContent("Fresh Workflow");
    expect(cards[0]).toHaveTextContent("0 / 15 steps");
    expect(
      await screen.findByText(/Live Risk Workflow · Step Flow/),
    ).toBeInTheDocument();
    expect(cards[0]).toHaveAttribute("aria-pressed", "false");
    await waitFor(() => {
      expect(scopeRuntimeApi.getMemberRunAudit).toHaveBeenCalledWith(
        "scope-real",
        "m-fresh",
        "run-fresh",
        { actorId: "actor-fresh-run" },
      );
    });
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalledWith(
      "scope-real",
      "wf-fresh-draft",
      "run-fresh",
      expect.anything(),
    );
    expect(scopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalledWith(
      "scope-real",
      "svc-fresh",
      "run-fresh",
      expect.anything(),
    );

    riskExecutionStatus = "completed";
    await queryClient.invalidateQueries({ queryKey: ["mission-wall"] });

    expect(
      await screen.findByText(/Fresh Workflow · Step Flow/),
    ).toBeInTheDocument();
    expect(cards[0]).toHaveAttribute("aria-pressed", "true");
  });

  it("shows the selected member run audit in the right workflow graph", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    const riskCard = (await screen.findByText("Live Risk Workflow")).closest(
      "button",
    );
    expect(riskCard).toBeTruthy();

    fireEvent.click(riskCard as HTMLButtonElement);

    expect(
      await screen.findByText(/Live Risk Workflow · Step Flow/),
    ).toBeInTheDocument();
    expect(await screen.findAllByText("risk_gate")).not.toHaveLength(0);

    const billingCard = screen.getByText("Billing Workflow").closest("button");
    expect(billingCard).toBeTruthy();

    fireEvent.click(billingCard as HTMLButtonElement);

    expect(
      await screen.findByText(/Billing Workflow · Step Flow/),
    ).toBeInTheDocument();
    expect(await screen.findAllByText("ledger_lookup")).not.toHaveLength(0);
    expect(billingCard).toHaveTextContent("2 / 3 steps");
    expect(billingCard).not.toHaveTextContent("0 / 0 steps");
    await waitFor(() => {
      expect(scopeRuntimeApi.getMemberRunAudit).toHaveBeenCalledWith(
        "scope-real",
        "m-billing",
        "run-billing",
        { actorId: "actor-billing-run" },
      );
    });
    expect(scopeRuntimeApi.getServiceRunAudit).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalledWith(
      "scope-real",
      "wf-billing-draft",
      "run-billing",
      expect.anything(),
    );
    expect(scopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalledWith(
      "scope-real",
      "svc-billing",
      "run-billing",
      expect.anything(),
    );
  });

  it("uses each run audit for card progress when the catalog omits progress and duration", async () => {
    const probeRun = runSummary({
      actorId: "actor-probe-run",
      completedSteps: 0,
      completionStatus: "DONE",
      lastUpdatedAt: "2026-06-30T04:58:36.000Z",
      runId: "run-probe",
      serviceId: "svc-risk",
      totalSteps: 0,
      workflowName: "Mission Wall Probe",
    });
    const auditStepsWithDurations = auditSteps(5).map((step, index) => ({
      ...step,
      completedAt: `2026-06-30T04:58:${String(10 + index).padStart(2, "0")}.000Z`,
      durationMs: 1300 + index * 100,
      success: true,
    }));
    const baseProbeAudit = auditSnapshot(probeRun, auditStepsWithDurations);
    const probeAudit: ScopeServiceRunAuditSnapshot = {
      ...baseProbeAudit,
      audit: {
        ...baseProbeAudit.audit,
        durationMs: 0,
        endedAt: null,
        summary: {
          completedSteps: 0,
          requestedSteps: 0,
          roleReplyCount: 0,
          stepTypeCounts: {},
          totalSteps: 0,
        },
      },
    };

    (studioApi.getWorkflowBoardSnapshot as jest.Mock).mockResolvedValue(
      workflowBoardSnapshot([
        workflowBoardMember({
          actorId: "actor-probe-run",
          completedSteps: 0,
          executionStatus: "completed",
          lastNodeUpdatedAt: "2026-06-30T04:58:36.000Z",
          member: riskMember,
          runId: "run-probe",
          totalSteps: 0,
          workflowName: "Mission Wall Probe",
        }),
        workflowBoardMember({
          actorId: "actor-billing-run",
          completedSteps: 2,
          executionStatus: "failed",
          lastNodeUpdatedAt: "2026-06-29T21:53:00.000Z",
          member: billingMember,
          runId: "run-billing",
          totalSteps: 3,
          workflowName: "Billing Workflow",
        }),
      ]),
    );
    (scopeRuntimeApi.getMemberRunAudit as jest.Mock).mockImplementation(
      async (_scopeId: string, memberId: string, runId: string) => {
        if (memberId === "m-risk" && runId === "run-probe") {
          return {
            audit: probeAudit.audit,
            summary: memberRunSummary(probeAudit.summary, riskMember),
          } satisfies ScopeMemberRunAuditSnapshot;
        }

        return memberAuditSnapshot(billingRun, billingMember, [
          auditStep({
            completedAt: "2026-06-30T04:57:30.000Z",
            nextStepId: "invoice_match",
            requestedAt: "2026-06-30T04:57:00.000Z",
            stepId: "ledger_lookup",
            stepType: "connector_call",
            success: true,
            targetRole: "ledger",
          }),
          auditStep({
            requestedAt: "2026-06-30T04:58:00.000Z",
            stepId: "invoice_match",
            stepType: "role_task",
            targetRole: "billing_agent",
          }),
        ]);
      },
    );
    window.history.replaceState(
      {},
      "",
      "/runtime/mission-wall?focusRunId=run-billing",
    );

    renderWithQueryClient(React.createElement(MissionWallPage));

    const probeCard = (
      await screen.findByText("Mission Wall Probe")
    ).closest("button");
    expect(probeCard).toBeTruthy();
    expect(probeCard).toHaveAttribute("aria-pressed", "false");
    expect(
      await screen.findByText(/Billing Workflow · Step Flow/),
    ).toBeInTheDocument();

    await waitFor(() => {
      expect(probeCard).toHaveTextContent("5 / 5 steps");
      expect(probeCard).toHaveTextContent("00:08");
    });
    expect(probeCard).toHaveTextContent("DONE");
    expect(probeCard).not.toHaveTextContent("0 / 0 steps");
    expect(probeCard).not.toHaveTextContent("00:00");
    expect(scopeRuntimeApi.getMemberRunAudit).toHaveBeenCalledWith(
      "scope-real",
      "m-risk",
      "run-probe",
      { actorId: "actor-probe-run" },
    );
    expect(scopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalledWith(
      "scope-real",
      "wf-risk-draft",
      "run-probe",
      expect.anything(),
    );
    expect(scopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalledWith(
      "scope-real",
      "svc-risk",
      "run-probe",
      expect.anything(),
    );
  });

  it("keeps a run card duration stable when focus moves between workflows", async () => {
    const extractRun = runSummary({
      actorId: "actor-extract-run",
      completedSteps: 0,
      completionStatus: "DONE",
      lastUpdatedAt: "2026-06-30T04:58:36.000Z",
      runId: "run-extract",
      serviceId: "svc-risk",
      totalSteps: 0,
      workflowName: "Document Extract Run",
    });
    const probeRun = runSummary({
      actorId: "actor-probe-run",
      completedSteps: 0,
      completionStatus: "DONE",
      lastUpdatedAt: "2026-06-30T04:58:40.000Z",
      runId: "run-probe",
      serviceId: "svc-billing",
      totalSteps: 0,
      workflowName: "Mission Wall Probe",
    });
    const extractAudit = auditSnapshot(
      extractRun,
      [
        auditStep({
          completedAt: "2026-06-30T04:58:02.000Z",
          requestedAt: "2026-06-30T04:58:01.000Z",
          stepId: "extract_file",
          stepType: "tool_call",
          success: true,
          targetRole: "extractor",
        }),
      ],
      {
        durationMs: 1000,
        summary: {
          completedSteps: 1,
          requestedSteps: 1,
          roleReplyCount: 1,
          stepTypeCounts: {},
          totalSteps: 1,
        },
      },
    );
    const probeAudit = auditSnapshot(
      probeRun,
      auditSteps(15).map((step, index) => ({
        ...step,
        completedAt: `2026-06-30T04:58:${String(10 + index).padStart(2, "0")}.000Z`,
        durationMs: 1600,
        success: true,
      })),
      {
        durationMs: 24_000,
        summary: {
          completedSteps: 15,
          requestedSteps: 15,
          roleReplyCount: 15,
          stepTypeCounts: {},
          totalSteps: 15,
        },
      },
    );

    (studioApi.getWorkflowBoardSnapshot as jest.Mock).mockResolvedValue(
      workflowBoardSnapshot([
        workflowBoardMember({
          actorId: "actor-extract-run",
          completedSteps: 0,
          executionStatus: "completed",
          lastNodeUpdatedAt: "2026-06-30T04:58:36.000Z",
          member: riskMember,
          runId: "run-extract",
          totalSteps: 0,
          workflowName: "Document Extract Run",
        }),
        workflowBoardMember({
          actorId: "actor-probe-run",
          completedSteps: 0,
          executionStatus: "completed",
          lastNodeUpdatedAt: "2026-06-30T04:58:40.000Z",
          member: billingMember,
          runId: "run-probe",
          totalSteps: 0,
          workflowName: "Mission Wall Probe",
        }),
      ]),
    );
    (scopeRuntimeApi.getMemberRunAudit as jest.Mock).mockImplementation(
      async (_scopeId: string, memberId: string, runId: string) => {
        if (memberId === "m-risk" && runId === "run-extract") {
          return {
            audit: extractAudit.audit,
            summary: memberRunSummary(extractAudit.summary, riskMember),
          } satisfies ScopeMemberRunAuditSnapshot;
        }

        if (memberId === "m-billing" && runId === "run-probe") {
          return {
            audit: probeAudit.audit,
            summary: memberRunSummary(probeAudit.summary, billingMember),
          } satisfies ScopeMemberRunAuditSnapshot;
        }

        throw new Error(`Unexpected audit lookup ${memberId}/${runId}`);
      },
    );
    window.history.replaceState(
      {},
      "",
      "/runtime/mission-wall?focusRunId=run-extract",
    );

    renderWithQueryClient(React.createElement(MissionWallPage));

    const extractCard = (
      await screen.findByText("Document Extract Run")
    ).closest("button");
    const probeCard = (
      await screen.findByText("Mission Wall Probe")
    ).closest("button");
    expect(extractCard).toBeTruthy();
    expect(probeCard).toBeTruthy();

    await waitFor(() => {
      expect(extractCard).toHaveTextContent("1 / 1 steps");
      expect(extractCard).toHaveTextContent("00:01");
      expect(probeCard).toHaveTextContent("15 / 15 steps");
      expect(probeCard).toHaveTextContent("00:24");
    });

    fireEvent.click(probeCard as HTMLButtonElement);

    expect(
      await screen.findByText(/Mission Wall Probe · Step Flow/),
    ).toBeInTheDocument();
    expect(extractCard).toHaveTextContent("1 / 1 steps");
    expect(extractCard).toHaveTextContent("00:01");

    fireEvent.click(extractCard as HTMLButtonElement);

    expect(
      await screen.findByText(/Document Extract Run · Step Flow/),
    ).toBeInTheDocument();
    expect(extractCard).toHaveTextContent("1 / 1 steps");
    expect(extractCard).toHaveTextContent("00:01");
    expect(probeCard).toHaveTextContent("15 / 15 steps");
    expect(probeCard).toHaveTextContent("00:24");
  });

  it("keeps every workflow node in the graph while focusing the default big-screen view", async () => {
    (scopeRuntimeApi.getMemberRunAudit as jest.Mock).mockImplementation(
      async (_scopeId: string, memberId: string, runId: string) => {
        if (memberId === "m-risk" && runId === "run-risk") {
          return memberAuditSnapshot(riskRun, riskMember, auditSteps(7));
        }

        return memberAuditSnapshot(billingRun, billingMember, auditSteps(2));
      },
    );

    renderWithQueryClient(React.createElement(MissionWallPage));

    const riskCard = (await screen.findByText("Live Risk Workflow")).closest(
      "button",
    );
    expect(riskCard).toBeTruthy();

    fireEvent.click(riskCard as HTMLButtonElement);

    expect(
      await screen.findByText(/Live Risk Workflow · Step Flow/),
    ).toBeInTheDocument();

    const graph = screen.getByTestId("mission-wall-graph");
    expect(within(graph).getByText("flow_step_1")).toBeInTheDocument();
    expect(within(graph).getByText("flow_step_7")).toBeInTheDocument();
    expect(screen.queryByText(/Focused steps/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/current execution/i)).not.toBeInTheDocument();
  });

  it("keeps published workflow members visible even when their latest run is outside the focus window", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    expect(await screen.findByText("Live Risk Workflow")).toBeInTheDocument();

    const list = screen.getByTestId("mission-wall-run-list");
    expect(within(list).getByText("Billing Workflow")).toBeInTheDocument();
    expect(within(list).getByText("Idle member")).toBeInTheDocument();
    expect(
      screen.getByText("Failed").closest(".mission-wall-metric"),
    ).toHaveTextContent("1");

    const idleCard = within(list).getByText("Idle member").closest("button");
    expect(idleCard).toBeTruthy();

    fireEvent.click(idleCard as HTMLButtonElement);

    expect(
      await screen.findByText(/Idle member · Step Flow/),
    ).toBeInTheDocument();
    expect(
      await screen.findAllByText("No visible run"),
    ).not.toHaveLength(0);
    expect(scopeRuntimeApi.getServiceRunAudit).not.toHaveBeenCalledWith(
      "scope-real",
      "svc-idle",
      "published:svc-idle",
      expect.anything(),
    );
  });

  it("renders the published run window as one stable manually scrollable list", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    expect(await screen.findByText("Live Risk Workflow")).toBeInTheDocument();

    const viewport = screen.getByTestId("mission-wall-run-window-viewport");
    const list = screen.getByTestId("mission-wall-run-list");

    expect(viewport.className).toContain("mission-wall-run-window__viewport");
    expect(list.className).toContain("mission-wall-run-list");
    expect(within(list).getAllByText("Live Risk Workflow")).toHaveLength(1);
    expect(within(list).getAllByText("Billing Workflow")).toHaveLength(1);
    expect(within(list).getAllByText("Idle member")).toHaveLength(1);

    const cards = within(list).getAllByRole("button");
    expect(cards[0]).toHaveTextContent("Live Risk Workflow");
    expect(cards[1]).toHaveTextContent("Billing Workflow");
    expect(cards[2]).toHaveTextContent("Idle member");
  });
});
