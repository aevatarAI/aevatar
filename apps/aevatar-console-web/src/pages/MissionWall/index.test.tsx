import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import MissionWallPage from "./index";

type ScopeServiceRunAuditSnapshot =
  import("@/shared/models/runtime/scopeServices").ScopeServiceRunAuditSnapshot;
type ScopeServiceRunCatalogSnapshot =
  import("@/shared/models/runtime/scopeServices").ScopeServiceRunCatalogSnapshot;
type ScopeServiceRunAuditReport =
  import("@/shared/models/runtime/scopeServices").ScopeServiceRunAuditReport;
type ScopeServiceRunAuditStep =
  import("@/shared/models/runtime/scopeServices").ScopeServiceRunAuditStep;
type ScopeServiceRunSummary =
  import("@/shared/models/runtime/scopeServices").ScopeServiceRunSummary;
type ServiceCatalogSnapshot =
  import("@/shared/models/services").ServiceCatalogSnapshot;
type StudioAuthSession = import("@/shared/studio/models").StudioAuthSession;
type StudioMemberRoster = import("@/shared/studio/models").StudioMemberRoster;
type StudioMemberSummary = import("@/shared/studio/models").StudioMemberSummary;
type StudioTeamRoster = import("@/shared/studio/models").StudioTeamRoster;

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

function serviceCatalog(input: {
  readonly displayName: string;
  readonly serviceId: string;
}): ServiceCatalogSnapshot {
  return {
    activeServingRevisionId: `revision-${input.serviceId}`,
    appId: "scope-real",
    defaultServingRevisionId: `revision-${input.serviceId}`,
    deploymentId: `deployment-${input.serviceId}`,
    deploymentStatus: "active",
    displayName: input.displayName,
    endpoints: [],
    namespace: "default",
    policyIds: [],
    primaryActorId: `actor-${input.serviceId}`,
    serviceId: input.serviceId,
    serviceKey: `scope-real:${input.serviceId}`,
    tenantId: "tenant-real",
    updatedAt: "2026-06-30T04:55:00.000Z",
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
  };

  return {
    audit,
    summary,
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
  const scriptMember: StudioMemberSummary = {
    ...workflowMember({
      displayName: "Script member",
      memberId: "m-script",
      publishedServiceId: "svc-script",
      workflowId: "wf-script",
    }),
    implementationKind: "script",
    implementationRef: {
      implementationKind: "script",
      scriptId: "script-1",
    },
  };
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
  const staleRosterMember = workflowMember({
    displayName: "Stale roster member",
    memberId: "untitled-member-4",
    publishedServiceId: "member-untitled-member4",
    teamId: "team-alpha",
    workflowId: "wf-stale-draft",
  });
  const riskService = serviceCatalog({
    displayName: "Risk runtime service",
    serviceId: "svc-risk",
  });
  const billingService = serviceCatalog({
    displayName: "Billing runtime service",
    serviceId: "svc-billing",
  });
  const idleMember = workflowMember({
    displayName: "Idle member",
    memberId: "m-idle",
    publishedServiceId: "svc-idle",
    teamId: "team-alpha",
    workflowId: "wf-idle-draft",
  });
  const idleService = serviceCatalog({
    displayName: "Idle runtime service",
    serviceId: "svc-idle",
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
    (studioApi.listMembers as jest.Mock).mockResolvedValue({
      members: [
        riskMember,
        billingMember,
        idleMember,
        scriptMember,
        staleRosterMember,
      ],
      scopeId: "scope-real",
    } satisfies StudioMemberRoster);
    (studioApi.listTeams as jest.Mock).mockResolvedValue({
      scopeId: "scope-real",
      teams: [
        {
          createdAt: "2026-06-29T00:00:00.000Z",
          description: "",
          displayName: "Alpha Team",
          entryMemberId: "m-risk",
          lifecycleStage: "active",
          memberCount: 2,
          scopeId: "scope-real",
          teamId: "team-alpha",
          updatedAt: "2026-06-29T00:00:00.000Z",
        },
      ],
    } satisfies StudioTeamRoster);
    (scopeRuntimeApi.listServices as jest.Mock).mockResolvedValue([
      riskService,
      billingService,
      idleService,
    ] satisfies ServiceCatalogSnapshot[]);
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockImplementation(
      async (
        _scopeId: string,
        serviceId: string,
      ): Promise<ScopeServiceRunCatalogSnapshot> => {
        if (serviceId === "svc-risk") {
          return {
            displayName: "Risk runtime service",
            runs: [riskRun],
            scopeId: "scope-real",
            serviceId,
            serviceKey: "scope-real:svc-risk",
          };
        }

        if (serviceId === "svc-billing") {
          return {
            displayName: "Billing runtime service",
            runs: [billingRun],
            scopeId: "scope-real",
            serviceId,
            serviceKey: "scope-real:svc-billing",
          };
        }

        if (serviceId === "svc-idle") {
          return {
            displayName: "Idle runtime service",
            runs: [],
            scopeId: "scope-real",
            serviceId,
            serviceKey: "scope-real:svc-idle",
          };
        }

        return {
          displayName: "Unknown service",
          runs: [],
          scopeId: "scope-real",
          serviceId,
          serviceKey: `scope-real:${serviceId}`,
        };
      },
    );
    (scopeRuntimeApi.getServiceRunAudit as jest.Mock).mockImplementation(
      async (_scopeId: string, serviceId: string, runId: string) => {
        if (serviceId === "svc-risk" && runId === "run-risk") {
          return auditSnapshot(riskRun, [
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

        return auditSnapshot(billingRun, [
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

  it("loads workflow members and published runs from existing runtime APIs", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    expect(await screen.findByText("Live Risk Workflow")).toBeInTheDocument();
    expect(screen.getByText("Billing Workflow")).toBeInTheDocument();
    expect(screen.queryByText("Script member")).not.toBeInTheDocument();

    await waitFor(() => {
      expect(scopeRuntimeApi.listServices).toHaveBeenCalledWith("scope-real", {
        take: 200,
      });
      expect(scopeRuntimeApi.listServiceRuns).toHaveBeenCalledWith(
        "scope-real",
        "svc-risk",
        { take: 50 },
      );
      expect(scopeRuntimeApi.listServiceRuns).toHaveBeenCalledWith(
        "scope-real",
        "svc-billing",
        { take: 50 },
      );
      expect(scopeRuntimeApi.listServiceRuns).toHaveBeenCalledWith(
        "scope-real",
        "svc-idle",
        { take: 50 },
      );
    });

    expect(scopeRuntimeApi.listMemberRuns).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalledWith(
      "scope-real",
      "wf-risk-draft",
      expect.anything(),
    );
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalledWith(
      "scope-real",
      "m-risk",
      expect.anything(),
    );
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalledWith(
      "scope-real",
      "member-untitled-member4",
      expect.anything(),
    );
  });

  it("shows the selected service run audit in the right workflow graph", async () => {
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
      expect(scopeRuntimeApi.getServiceRunAudit).toHaveBeenCalledWith(
        "scope-real",
        "svc-billing",
        "run-billing",
        { actorId: "actor-billing-run" },
      );
    });
    expect(scopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.getServiceRunAudit).not.toHaveBeenCalledWith(
      "scope-real",
      "wf-billing-draft",
      "run-billing",
      expect.anything(),
    );
    expect(scopeRuntimeApi.getServiceRunAudit).not.toHaveBeenCalledWith(
      "scope-real",
      "m-billing",
      "run-billing",
      expect.anything(),
    );
  });

  it("keeps every workflow node in the graph while focusing the default big-screen view", async () => {
    (scopeRuntimeApi.getServiceRunAudit as jest.Mock).mockImplementation(
      async (_scopeId: string, serviceId: string, runId: string) => {
        if (serviceId === "svc-risk" && runId === "run-risk") {
          return auditSnapshot(riskRun, auditSteps(7));
        }

        return auditSnapshot(billingRun, auditSteps(2));
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
