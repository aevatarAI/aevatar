import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import {
  clearStoredAuthSession,
  persistAuthSession,
} from "@/shared/auth/session";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import MissionWallPage from "./index";
import { MISSION_WALL_STALE_SNAPSHOT_FALLBACK_MS } from "./hooks/useMissionWallData";

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
  readonly currentNode?: NonNullable<
    StudioWorkflowBoardSnapshot["teams"][number]["members"][number]["currentNode"]
  > | null;
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
  const currentNodeName =
    input.currentNodeStatus === "waiting" || input.executionStatus === "waiting"
      ? "approval_gate"
      : "Current";

  return {
    actorId: input.actorId,
    completedNodes: Array.from({ length: input.completedSteps }, (_, index) => ({
      completedAt: "2026-06-30T04:58:20.000Z",
      durationMs: 1000,
      name: `Completed ${index + 1}`,
      nodeId: `completed_${index + 1}`,
    })),
    currentExecutionId: input.runId,
    currentNode:
      input.currentNode === null
        ? null
        : input.currentNode ?? {
            durationMs: input.durationMs,
            name: currentNodeName,
            nodeId: currentNodeName,
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
              name: "record_validation",
              nodeId: "record_validation",
            },
          ]
        : [],
    lastNodeUpdatedAt: input.lastNodeUpdatedAt,
    memberId: input.member.memberId,
    pendingNodes:
      input.currentNodeStatus === "waiting" || input.executionStatus === "waiting"
        ? [
            {
              name: "approval_gate",
              nodeId: "approval_gate",
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
  const alphaMember = workflowMember({
    displayName: "Alpha member",
    memberId: "m-alpha",
    publishedServiceId: "svc-alpha",
    teamId: "team-alpha",
    workflowId: "wf-alpha-draft",
  });
  const betaMember = workflowMember({
    displayName: "Beta member",
    memberId: "m-beta",
    publishedServiceId: "svc-beta",
    teamId: "team-alpha",
    workflowId: "wf-beta-draft",
  });
  const idleMember = workflowMember({
    displayName: "Idle member",
    memberId: "m-idle",
    publishedServiceId: "svc-idle",
    teamId: "team-alpha",
    workflowId: "wf-idle-draft",
  });

  beforeEach(() => {
    clearStoredAuthSession();
    jest.clearAllMocks();
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
          actorId: "actor-alpha-run",
          completedSteps: 1,
          currentNodeStatus: "waiting",
          executionStatus: "running",
          lastNodeUpdatedAt: "2026-06-30T04:59:20.000Z",
          member: alphaMember,
          runId: "run-alpha",
          totalSteps: 3,
          workflowName: "Workflow Alpha",
        }),
        workflowBoardMember({
          actorId: "actor-beta-run",
          completedSteps: 2,
          executionStatus: "failed",
          lastNodeUpdatedAt: "2026-06-29T21:53:00.000Z",
          member: betaMember,
          runId: "run-beta",
          totalSteps: 3,
          workflowName: "Workflow Beta",
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
  });

  afterEach(() => {
    jest.useRealTimers();
    jest.restoreAllMocks();
    clearStoredAuthSession();
  });

  it("renders the shared language switch and authenticated user entry in fullscreen mode", async () => {
    persistAuthSession({
      tokens: {
        accessToken: "token",
        expiresAt: Date.now() + 60_000,
        expiresIn: 60,
        tokenType: "Bearer",
      },
      user: {
        email: "abigail@example.com",
        name: "Abigail Deng",
        picture: "https://example.com/avatar.png",
        sub: "user-abigail",
      },
    });

    renderWithQueryClient(React.createElement(MissionWallPage));

    expect(await screen.findByText("Workflow Alpha")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Switch language" }),
    ).toBeInTheDocument();
    expect(screen.getByText("English")).toBeInTheDocument();
    expect(screen.getByText("Abigail Deng")).toBeInTheDocument();
  });

  it("themes the fullscreen header actions with mission wall colors", async () => {
    persistAuthSession({
      tokens: {
        accessToken: "token",
        expiresAt: Date.now() + 60_000,
        expiresIn: 60,
        tokenType: "Bearer",
      },
      user: {
        email: "abigail@example.com",
        name: "Abigail Deng",
        picture: "https://example.com/avatar.png",
        sub: "user-abigail",
      },
    });

    renderWithQueryClient(React.createElement(MissionWallPage));

    expect(await screen.findByText("Workflow Alpha")).toBeInTheDocument();
    const actions = document.querySelector(".mission-wall-header-actions");
    expect(actions).toBeInstanceOf(HTMLElement);
    expect(actions).toHaveAttribute(
      "data-dropdown-root-class-name",
      "mission-wall-header-menu",
    );

    const missionWallStyle = Array.from(document.querySelectorAll("style"))
      .map((style) => style.textContent ?? "")
      .join("\n");

    const dropdownRootRule =
      missionWallStyle.match(/\.mission-wall-header-menu\s*{[^}]*}/)?.[0] ??
      "";
    expect(dropdownRootRule).toContain("--wall-text: #f8faf8;");
    expect(dropdownRootRule).toContain("--wall-live: #2dd4bf;");
    expect(missionWallStyle).toContain(
      ".mission-wall-header-actions .console-header-actions__language",
    );
    expect(missionWallStyle).toContain("rgba(45, 212, 191, 0.14)");
    expect(missionWallStyle).toContain("var(--wall-live)");
  });

  it("loads one latest execution row per workflow member from the backend snapshot", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    expect(await screen.findByText("Workflow Alpha")).toBeInTheDocument();
    expect(screen.getByText("Workflow Beta")).toBeInTheDocument();
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

  it("does not render a refresh freshness metric in the fullscreen header", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    expect(await screen.findByText("Workflow Alpha")).toBeInTheDocument();
    const topStrip = document.querySelector(".mission-wall-top-strip");
    expect(topStrip).toBeInstanceOf(HTMLElement);
    expect(within(topStrip as HTMLElement).queryByText("Fresh")).toBeNull();
  });

  it("does not expand multiple service catalog runs into duplicate member rows", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    expect(await screen.findByText("Workflow Alpha")).toBeInTheDocument();

    const list = screen.getByTestId("mission-wall-run-list");
    expect(within(list).getAllByText("Workflow Alpha")).toHaveLength(1);
    expect(within(list).queryByText("Older Workflow Alpha")).toBeNull();
    expect(within(list).queryByText("run-alpha-old")).toBeNull();
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalled();
  });

  it("filters the backend snapshot by route team without submitting member ids", async () => {
    window.history.replaceState(
      {},
      "",
      "/runtime/mission-wall?scopeId=scope-real&teamId=team-alpha",
    );

    renderWithQueryClient(React.createElement(MissionWallPage));

    expect(await screen.findByText("Workflow Alpha")).toBeInTheDocument();

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

    expect(await screen.findByText("Workflow Alpha")).toBeInTheDocument();
    expect(
      await screen.findByText(/Workflow Alpha · Step Flow/),
    ).toBeInTheDocument();

    const root = document.querySelector(".mission-wall");
    expect(root).toBeTruthy();

    const clippedLabels = Array.from(
      root!.querySelectorAll(
        [
          ".mission-wall-brand__title",
          ".mission-wall-run-card__name",
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

  it("does not repeat team and member context inside run cards", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    const alphaCard = (await screen.findByText("Workflow Alpha")).closest(
      "button",
    );
    expect(alphaCard).toBeTruthy();
    expect(alphaCard).not.toHaveTextContent("Alpha Team · Alpha member");
    expect(
      alphaCard!.querySelector(".mission-wall-run-card__team"),
    ).not.toBeInTheDocument();
  });

  it("highlights the selected run card without adding another shadow layer", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    const alphaCard = (await screen.findByText("Workflow Alpha")).closest(
      "button",
    );
    expect(alphaCard).toBeTruthy();
    expect(alphaCard).toHaveClass("mission-wall-run-card--focus");

    const missionWallStyle = Array.from(document.querySelectorAll("style"))
      .map((style) => style.textContent ?? "")
      .join("\n");
    const focusRule =
      missionWallStyle.match(/\.mission-wall-run-card--focus\s*{[^}]*}/)?.[0] ??
      "";

    expect(focusRule).toContain("outline");
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
    let alphaExecutionStatus: "running" | "completed" = "running";
    (studioApi.getWorkflowBoardSnapshot as jest.Mock).mockImplementation(
      async () =>
        workflowBoardSnapshot([
          workflowBoardMember({
            actorId: "actor-alpha-run",
            completedSteps: alphaExecutionStatus === "completed" ? 3 : 1,
            currentNodeStatus:
              alphaExecutionStatus === "completed" ? "completed" : "waiting",
            executionStatus: alphaExecutionStatus,
            lastNodeUpdatedAt: "2026-06-30T04:59:20.000Z",
            member: alphaMember,
            runId: "run-alpha",
            totalSteps: 3,
            workflowName: "Workflow Alpha",
          }),
          workflowBoardMember({
            actorId: "actor-beta-run",
            completedSteps: 2,
            executionStatus: "failed",
            lastNodeUpdatedAt: "2026-06-29T21:53:00.000Z",
            member: betaMember,
            runId: "run-beta",
            totalSteps: 3,
            workflowName: "Workflow Beta",
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

    expect(await screen.findByText("Workflow Alpha")).toBeInTheDocument();
    expect(screen.queryByText("Fresh Workflow")).not.toBeInTheDocument();

    includeFreshMember = true;
    await queryClient.invalidateQueries({ queryKey: ["mission-wall"] });

    expect(await screen.findByText("Fresh Workflow")).toBeInTheDocument();

    const list = screen.getByTestId("mission-wall-run-list");
    const cards = within(list).getAllByRole("button");
    expect(cards[0]).toHaveTextContent("Fresh Workflow");
    expect(cards[0]).toHaveTextContent("0 / 15 steps");
    expect(
      await screen.findByText(/Fresh Workflow · Step Flow/),
    ).toBeInTheDocument();
    expect(cards[0]).toHaveAttribute("aria-pressed", "true");
    expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalled();

    alphaExecutionStatus = "completed";
    await queryClient.invalidateQueries({ queryKey: ["mission-wall"] });

    expect(
      await screen.findByText(/Fresh Workflow · Step Flow/),
    ).toBeInTheDocument();
    expect(cards[0]).toHaveAttribute("aria-pressed", "true");
  });

  it("keeps the last visible workflow board when a refetch briefly returns an empty snapshot", async () => {
    let returnEmptySnapshot = false;
    (studioApi.getWorkflowBoardSnapshot as jest.Mock).mockImplementation(
      async () =>
        returnEmptySnapshot
          ? workflowBoardSnapshot([], {
              generatedAt: "2026-06-30T05:00:05.000Z",
              lastNodeUpdatedAt: "2026-06-30T05:00:05.000Z",
            })
          : workflowBoardSnapshot([
              workflowBoardMember({
                actorId: "actor-alpha-run",
                completedSteps: 1,
                currentNodeStatus: "waiting",
                executionStatus: "running",
                lastNodeUpdatedAt: "2026-06-30T04:59:20.000Z",
                member: alphaMember,
                runId: "run-alpha",
                totalSteps: 3,
                workflowName: "Workflow Alpha",
              }),
            ]),
    );

    const { queryClient } = renderWithQueryClient(
      React.createElement(MissionWallPage),
    );

    expect(await screen.findByText("Workflow Alpha")).toBeInTheDocument();
    expect(await screen.findAllByText("approval_gate")).not.toHaveLength(0);

    returnEmptySnapshot = true;
    await queryClient.invalidateQueries({ queryKey: ["mission-wall"] });

    expect(await screen.findByText("Workflow Alpha")).toBeInTheDocument();
    expect(await screen.findAllByText("approval_gate")).not.toHaveLength(0);
    expect(screen.getByText("Live").closest(".mission-wall-metric"))
      .toHaveTextContent("Degraded");
    expect(screen.getByTestId("mission-wall-run-list"))
      .toHaveTextContent("Workflow Alpha");
    expect(screen.queryByText("No focus run")).not.toBeInTheDocument();
  });

  it("expires the stale workflow board after repeated refetch errors exceed the fallback window", async () => {
    jest.useFakeTimers();
    let nowMs = Date.parse(NOW);
    jest.setSystemTime(nowMs);
    jest.spyOn(Date, "now").mockImplementation(() => nowMs);
    let failSnapshot = false;
    (studioApi.getWorkflowBoardSnapshot as jest.Mock).mockImplementation(
      async () => {
        if (failSnapshot) {
          throw new Error("workflow board unavailable");
        }

        return workflowBoardSnapshot([
          workflowBoardMember({
            actorId: "actor-alpha-run",
            completedSteps: 1,
            currentNodeStatus: "waiting",
            executionStatus: "running",
            lastNodeUpdatedAt: "2026-06-30T04:59:20.000Z",
            member: alphaMember,
            runId: "run-alpha",
            totalSteps: 3,
            workflowName: "Workflow Alpha",
          }),
        ]);
      },
    );

    const { queryClient } = renderWithQueryClient(
      React.createElement(MissionWallPage),
    );

    expect(await screen.findByText("Workflow Alpha")).toBeInTheDocument();

    failSnapshot = true;
    nowMs += 1_000;
    jest.setSystemTime(nowMs);
    await queryClient.invalidateQueries({ queryKey: ["mission-wall"] });

    expect(await screen.findByText("Workflow Alpha")).toBeInTheDocument();

    nowMs += MISSION_WALL_STALE_SNAPSHOT_FALLBACK_MS + 1_000;
    jest.setSystemTime(nowMs);
    await jest.advanceTimersByTimeAsync(
      MISSION_WALL_STALE_SNAPSHOT_FALLBACK_MS + 1_000,
    );
    await waitFor(() => {
      expect(screen.queryByText("Workflow Alpha")).not.toBeInTheDocument();
    });
    expect(screen.getByText("Live").closest(".mission-wall-metric"))
      .toHaveTextContent("Disconnected");
  });

  it("auto-focuses a newly observed workflow run so its topology appears without a page reload", async () => {
    const freshMember = workflowMember({
      displayName: "Fresh workflow member",
      memberId: "m-fresh",
      publishedServiceId: "svc-fresh",
      teamId: "team-alpha",
      workflowId: "wf-fresh-draft",
    });
    let includeFreshMember = false;
    (studioApi.getWorkflowBoardSnapshot as jest.Mock).mockImplementation(
      async () =>
        workflowBoardSnapshot([
          workflowBoardMember({
            actorId: "actor-alpha-run",
            completedSteps: 1,
            currentNodeStatus: "waiting",
            executionStatus: "running",
            lastNodeUpdatedAt: "2026-06-30T04:59:20.000Z",
            member: alphaMember,
            runId: "run-alpha",
            totalSteps: 3,
            workflowName: "Workflow Alpha",
          }),
          ...(includeFreshMember
            ? [
                workflowBoardMember({
                  actorId: "actor-fresh-run",
                  completedSteps: 0,
                  currentNodeStatus: "running",
                  executionStatus: "running",
                  lastNodeUpdatedAt: "2026-06-30T04:59:58.000Z",
                  member: freshMember,
                  runId: "run-fresh",
                  totalSteps: 4,
                  workflowName: "Fresh Workflow",
                }),
              ]
            : []),
        ]),
    );

    const { queryClient } = renderWithQueryClient(
      React.createElement(MissionWallPage),
    );

    expect(
      await screen.findByText(/Workflow Alpha · Step Flow/),
    ).toBeInTheDocument();

    includeFreshMember = true;
    await queryClient.invalidateQueries({ queryKey: ["mission-wall"] });

    expect(
      await screen.findByText(/Fresh Workflow · Step Flow/),
    ).toBeInTheDocument();

    const list = screen.getByTestId("mission-wall-run-list");
    const cards = within(list).getAllByRole("button");
    expect(cards[0]).toHaveTextContent("Fresh Workflow");
    expect(cards[0]).toHaveAttribute("aria-pressed", "true");
    expect(await screen.findAllByText("Current")).not.toHaveLength(0);
  });

  it("keeps a manually selected run focused when another workflow run appears", async () => {
    const freshMember = workflowMember({
      displayName: "Fresh workflow member",
      memberId: "m-fresh",
      publishedServiceId: "svc-fresh",
      teamId: "team-alpha",
      workflowId: "wf-fresh-draft",
    });
    let includeFreshMember = false;
    (studioApi.getWorkflowBoardSnapshot as jest.Mock).mockImplementation(
      async () =>
        workflowBoardSnapshot([
          workflowBoardMember({
            actorId: "actor-alpha-run",
            completedSteps: 1,
            currentNodeStatus: "waiting",
            executionStatus: "running",
            lastNodeUpdatedAt: "2026-06-30T04:59:20.000Z",
            member: alphaMember,
            runId: "run-alpha",
            totalSteps: 3,
            workflowName: "Workflow Alpha",
          }),
          ...(includeFreshMember
            ? [
                workflowBoardMember({
                  actorId: "actor-fresh-run",
                  completedSteps: 0,
                  currentNodeStatus: "running",
                  executionStatus: "running",
                  lastNodeUpdatedAt: "2026-06-30T04:59:58.000Z",
                  member: freshMember,
                  runId: "run-fresh",
                  totalSteps: 4,
                  workflowName: "Fresh Workflow",
                }),
              ]
            : []),
        ]),
    );

    const { queryClient } = renderWithQueryClient(
      React.createElement(MissionWallPage),
    );

    const alphaCard = (await screen.findByText("Workflow Alpha")).closest(
      "button",
    );
    expect(alphaCard).toBeTruthy();
    fireEvent.click(alphaCard as HTMLButtonElement);

    includeFreshMember = true;
    await queryClient.invalidateQueries({ queryKey: ["mission-wall"] });

    expect(await screen.findByText("Fresh Workflow")).toBeInTheDocument();
    expect(
      await screen.findByText(/Workflow Alpha · Step Flow/),
    ).toBeInTheDocument();
    expect(alphaCard).toHaveAttribute("aria-pressed", "true");
  });

  it("shows the selected member snapshot nodes in the right workflow graph", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    const alphaCard = (await screen.findByText("Workflow Alpha")).closest(
      "button",
    );
    expect(alphaCard).toBeTruthy();

    fireEvent.click(alphaCard as HTMLButtonElement);

    expect(
      await screen.findByText(/Workflow Alpha · Step Flow/),
    ).toBeInTheDocument();
    expect(await screen.findAllByText("approval_gate")).not.toHaveLength(0);

    const betaCard = screen.getByText("Workflow Beta").closest("button");
    expect(betaCard).toBeTruthy();

    fireEvent.click(betaCard as HTMLButtonElement);

    expect(
      await screen.findByText(/Workflow Beta · Step Flow/),
    ).toBeInTheDocument();
    expect(await screen.findAllByText("record_validation")).not.toHaveLength(0);
    expect(betaCard).toHaveTextContent("2 / 3 steps");
    expect(betaCard).not.toHaveTextContent("0 / 0 steps");
    expect(scopeRuntimeApi.getServiceRunAudit).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalled();
  });

  it("uses the workflow-board snapshot for card progress and duration", async () => {
    (studioApi.getWorkflowBoardSnapshot as jest.Mock).mockResolvedValue(
      workflowBoardSnapshot([
        workflowBoardMember({
          actorId: "actor-probe-run",
          completedSteps: 5,
          durationMs: 8000,
          executionStatus: "completed",
          lastNodeUpdatedAt: "2026-06-30T04:58:36.000Z",
          member: alphaMember,
          runId: "run-probe",
          totalSteps: 5,
          workflowName: "Mission Wall Probe",
        }),
        workflowBoardMember({
          actorId: "actor-beta-run",
          completedSteps: 2,
          executionStatus: "failed",
          lastNodeUpdatedAt: "2026-06-29T21:53:00.000Z",
          member: betaMember,
          runId: "run-beta",
          totalSteps: 3,
          workflowName: "Workflow Beta",
        }),
      ]),
    );
    window.history.replaceState(
      {},
      "",
      "/runtime/mission-wall?focusRunId=run-beta",
    );

    renderWithQueryClient(React.createElement(MissionWallPage));

    const probeCard = (
      await screen.findByText("Mission Wall Probe")
    ).closest("button");
    expect(probeCard).toBeTruthy();
    expect(probeCard).toHaveAttribute("aria-pressed", "false");
    expect(
      await screen.findByText(/Workflow Beta · Step Flow/),
    ).toBeInTheDocument();

    expect(probeCard).toHaveTextContent("5 / 5 steps");
    expect(probeCard).toHaveTextContent("00:08");
    expect(probeCard).toHaveTextContent("DONE");
    expect(probeCard).not.toHaveTextContent("0 / 0 steps");
    expect(probeCard).not.toHaveTextContent("00:00");
    expect(scopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalled();
  });

  it("uses completed node durations when completed workflow-board snapshots omit current node duration", async () => {
    const completedMember = workflowBoardMember({
      actorId: "actor-probe-run",
      completedSteps: 5,
      currentNode: null,
      executionStatus: "completed",
      lastNodeUpdatedAt: "2026-07-07T12:58:22.000Z",
      member: alphaMember,
      runId: "run-probe",
      totalSteps: 5,
      workflowName: "weekly_report_five_nodes",
    });

    (studioApi.getWorkflowBoardSnapshot as jest.Mock).mockResolvedValue(
      workflowBoardSnapshot([completedMember]),
    );

    renderWithQueryClient(React.createElement(MissionWallPage));

    const probeCard = (
      await screen.findByText("weekly_report_five_nodes")
    ).closest("button");
    expect(probeCard).toBeTruthy();
    expect(probeCard).toHaveTextContent("5 / 5 steps");
    expect(probeCard).toHaveTextContent("00:05");
    expect(probeCard).not.toHaveTextContent("--");
  });

  it("keeps a run card duration stable when focus moves between workflows", async () => {
    (studioApi.getWorkflowBoardSnapshot as jest.Mock).mockResolvedValue(
      workflowBoardSnapshot([
        workflowBoardMember({
          actorId: "actor-extract-run",
          completedSteps: 1,
          durationMs: 1000,
          executionStatus: "completed",
          lastNodeUpdatedAt: "2026-06-30T04:58:36.000Z",
          member: alphaMember,
          runId: "run-extract",
          totalSteps: 1,
          workflowName: "Document Extract Run",
        }),
        workflowBoardMember({
          actorId: "actor-probe-run",
          completedSteps: 15,
          durationMs: 24_000,
          executionStatus: "completed",
          lastNodeUpdatedAt: "2026-06-30T04:58:40.000Z",
          member: betaMember,
          runId: "run-probe",
          totalSteps: 15,
          workflowName: "Mission Wall Probe",
        }),
      ]),
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

    expect(extractCard).toHaveTextContent("1 / 1 steps");
    expect(extractCard).toHaveTextContent("00:01");
    expect(probeCard).toHaveTextContent("15 / 15 steps");
    expect(probeCard).toHaveTextContent("00:24");

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
    expect(scopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalled();
  });

  it("keeps every workflow node in the graph while focusing the default big-screen view", async () => {
    (studioApi.getWorkflowBoardSnapshot as jest.Mock).mockResolvedValue(
      workflowBoardSnapshot([
        workflowBoardMember({
          actorId: "actor-alpha-run",
          completedSteps: 7,
          currentNodeStatus: "waiting",
          executionStatus: "running",
          lastNodeUpdatedAt: "2026-06-30T04:59:20.000Z",
          member: alphaMember,
          runId: "run-alpha",
          totalSteps: 8,
          workflowName: "Workflow Alpha",
        }),
        workflowBoardMember({
          actorId: "actor-beta-run",
          completedSteps: 2,
          executionStatus: "failed",
          lastNodeUpdatedAt: "2026-06-29T21:53:00.000Z",
          member: betaMember,
          runId: "run-beta",
          totalSteps: 3,
          workflowName: "Workflow Beta",
        }),
      ]),
    );

    renderWithQueryClient(React.createElement(MissionWallPage));

    const alphaCard = (await screen.findByText("Workflow Alpha")).closest(
      "button",
    );
    expect(alphaCard).toBeTruthy();

    fireEvent.click(alphaCard as HTMLButtonElement);

    expect(
      await screen.findByText(/Workflow Alpha · Step Flow/),
    ).toBeInTheDocument();

    const graph = screen.getByTestId("mission-wall-graph");
    expect(within(graph).getByText("completed_1")).toBeInTheDocument();
    expect(within(graph).getByText("completed_7")).toBeInTheDocument();
    expect(scopeRuntimeApi.getMemberRunAudit).not.toHaveBeenCalled();
    expect(screen.queryByText(/Focused steps/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/current execution/i)).not.toBeInTheDocument();
  });

  it("keeps published workflow members visible even when their latest run is outside the focus window", async () => {
    renderWithQueryClient(React.createElement(MissionWallPage));

    expect(await screen.findByText("Workflow Alpha")).toBeInTheDocument();

    const list = screen.getByTestId("mission-wall-run-list");
    expect(within(list).getByText("Workflow Beta")).toBeInTheDocument();
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

    expect(await screen.findByText("Workflow Alpha")).toBeInTheDocument();

    const viewport = screen.getByTestId("mission-wall-run-window-viewport");
    const list = screen.getByTestId("mission-wall-run-list");

    expect(viewport.className).toContain("mission-wall-run-window__viewport");
    expect(list.className).toContain("mission-wall-run-list");
    expect(within(list).getAllByText("Workflow Alpha")).toHaveLength(1);
    expect(within(list).getAllByText("Workflow Beta")).toHaveLength(1);
    expect(within(list).getAllByText("Idle member")).toHaveLength(1);

    const cards = within(list).getAllByRole("button");
    expect(cards[0]).toHaveTextContent("Workflow Alpha");
    expect(cards[1]).toHaveTextContent("Workflow Beta");
    expect(cards[2]).toHaveTextContent("Idle member");
  });
});
