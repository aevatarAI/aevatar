import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { loadDraftRunPayload } from "@/shared/runs/draftRunSession";
import { runtimeGAgentApi } from "@/shared/api/runtimeGAgentApi";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import GAgentsPage from "./index";

jest.mock("@aevatar-react-sdk/agui", () => ({
  parseCustomEvent: jest.fn((event: Record<string, unknown>) => ({
    name: event.name,
    data: event.value,
  })),
}));

jest.mock("@aevatar-react-sdk/types", () => ({
  AGUIEventType: {
    CUSTOM: "CUSTOM",
    RUN_ERROR: "RUN_ERROR",
    RUN_STARTED: "RUN_STARTED",
    TEXT_MESSAGE_CONTENT: "TEXT_MESSAGE_CONTENT",
  },
  CustomEventName: {
    RunContext: "aevatar.run.context",
  },
}));

jest.mock("@/shared/agui/sseFrameNormalizer", () => ({
  parseBackendSSEStream: jest.fn(),
}));

jest.mock("@/shared/api/runtimeGAgentApi", () => ({
  runtimeGAgentApi: {
    listKinds: jest.fn(),
    listActors: jest.fn(),
    getScopeBinding: jest.fn(),
    getDefaultRouteTarget: jest.fn(),
    bindScopeGAgent: jest.fn(),
    activateScopeBindingRevision: jest.fn(),
    activateMemberBindingRevision: jest.fn(),
    retireScopeBindingRevision: jest.fn(),
    retireMemberBindingRevision: jest.fn(),
    removeActor: jest.fn(),
    streamDraftRun: jest.fn(),
  },
}));

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(),
  },
}));

jest.mock("@/shared/ui/aevatarPageShells", () => ({
  AevatarContextDrawer: ({ children, open, title }: any) => {
    const mockReact = require("react");
    return open
      ? mockReact.createElement(
          "section",
          null,
          title ? mockReact.createElement("h2", null, title) : null,
          children
        )
      : null;
  },
  AevatarInspectorEmpty: ({ description }: any) => {
    const mockReact = require("react");
    return mockReact.createElement("div", null, description);
  },
  AevatarPageShell: ({ children, title }: any) => {
    const mockReact = require("react");
    return mockReact.createElement(
      "section",
      null,
      mockReact.createElement("h1", null, title),
      children
    );
  },
  AevatarPanel: ({ children, title }: any) => {
    const mockReact = require("react");
    return mockReact.createElement(
      "div",
      null,
      title ? mockReact.createElement("h2", null, title) : null,
      children
    );
  },
  AevatarStatusTag: ({ status }: any) => {
    const mockReact = require("react");
    return mockReact.createElement("span", null, status);
  },
  AevatarWorkbenchLayout: ({ rail, stage, stageAside }: any) => {
    const mockReact = require("react");
    return mockReact.createElement(
      "div",
      null,
      rail,
      stage,
      stageAside ?? null
    );
  },
}));

import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";

describe("GAgentsPage", () => {
  const mockedRuntimeGAgentApi = runtimeGAgentApi as unknown as {
    listKinds: jest.Mock;
    listActors: jest.Mock;
    getScopeBinding: jest.Mock;
    getDefaultRouteTarget: jest.Mock;
    bindScopeGAgent: jest.Mock;
    activateScopeBindingRevision: jest.Mock;
    activateMemberBindingRevision: jest.Mock;
    retireScopeBindingRevision: jest.Mock;
    retireMemberBindingRevision: jest.Mock;
    removeActor: jest.Mock;
    streamDraftRun: jest.Mock;
  };
  const mockedStudioApi = studioApi as unknown as {
    getAuthSession: jest.Mock;
  };
  let actorGroupsState: Array<{
    agentKind: string;
    actorIds: string[];
  }>;

  beforeEach(() => {
    window.history.replaceState({}, "", "/runtime/gagents?scopeId=scope-a");
    window.localStorage.clear();
    window.sessionStorage.clear();
    jest.clearAllMocks();

    mockedStudioApi.getAuthSession.mockResolvedValue({
      enabled: true,
      scopeId: "scope-a",
      scopeSource: "nyxid",
    });
    mockedRuntimeGAgentApi.listKinds.mockResolvedValue([
      {
        agentKind: "Tests.OrdersGAgent",
        displayName: "Orders Assistant",
        diagnosticClrTypeName: "Tests.OrdersGAgent, Tests",
        endpoints: [],
      },
      {
        agentKind: "Tests.PlannerGAgent",
        displayName: "Planner Assistant",
        diagnosticClrTypeName: "Tests.PlannerGAgent, Tests",
        endpoints: [],
      },
    ]);
    actorGroupsState = [
      {
        agentKind: "Tests.OrdersGAgent",
        actorIds: ["orders-1"],
      },
      {
        agentKind: "Tests.PlannerGAgent",
        actorIds: ["planner-1"],
      },
    ];
    mockedRuntimeGAgentApi.getDefaultRouteTarget.mockResolvedValue({
      available: false,
      scopeId: "scope-a",
      serviceId: "",
      displayName: "",
      serviceKey: "",
      defaultServingRevisionId: "",
      activeServingRevisionId: "",
      deploymentId: "",
      deploymentStatus: "",
      primaryActorId: "",
      updatedAt: null,
      revisions: [],
    });
    mockedRuntimeGAgentApi.bindScopeGAgent.mockResolvedValue({
      scopeId: "scope-a",
      serviceId: "service-orders",
      displayName: "Orders Assistant",
      revisionId: "rev-2",
      implementationKind: "gagent",
      targetName: "Orders Assistant",
      expectedActorId: "orders-1",
      gAgent: {
        agentKind: "Tests.OrdersGAgent",
        preferredActorId: "orders-1",
      },
    });
    mockedRuntimeGAgentApi.activateMemberBindingRevision.mockResolvedValue({
      scopeId: "scope-a",
      serviceId: "service-orders",
      displayName: "Orders Assistant",
      revisionId: "rev-2",
    });
    mockedRuntimeGAgentApi.retireMemberBindingRevision.mockResolvedValue({
      scopeId: "scope-a",
      serviceId: "service-orders",
      revisionId: "rev-2",
      status: "Retired",
    });
    mockedRuntimeGAgentApi.listActors.mockImplementation(
      async () =>
        actorGroupsState.map((group) => ({
          ...group,
          actorIds: [...group.actorIds],
        }))
    );
    mockedRuntimeGAgentApi.removeActor.mockImplementation(
      async (_scopeId: string, agentKind: string, actorId: string) => {
        actorGroupsState = actorGroupsState
          .map((group) =>
            group.agentKind === agentKind
              ? {
                  ...group,
                  actorIds: group.actorIds.filter((entry) => entry !== actorId),
                }
              : group
          )
          .filter((group) => group.actorIds.length > 0);
      }
    );
    mockedRuntimeGAgentApi.streamDraftRun.mockResolvedValue({
      ok: true,
    });
    (parseBackendSSEStream as jest.Mock).mockImplementation(async function* () {
      yield {
        type: "RUN_STARTED",
        runId: "run-1",
        threadId: "thread-1",
        timestamp: Date.now(),
      };
      yield {
        type: "CUSTOM",
        name: "aevatar.run.context",
        value: {
          actorId: "orders-1",
          commandId: "cmd-1",
        },
        timestamp: Date.now(),
      };
      yield {
        type: "TEXT_MESSAGE_CONTENT",
        delta: "hello from gagent",
        messageId: "msg-1",
        timestamp: Date.now(),
      };
    });
  });

  it("renders stack skeletons for the initial kind and actor inventories", async () => {
    let resolveKinds: (value: unknown[]) => void = () => {};
    mockedRuntimeGAgentApi.listKinds.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveKinds = resolve;
        }),
    );
    mockedRuntimeGAgentApi.listActors.mockImplementationOnce(
      () => new Promise(() => {}),
    );

    renderWithQueryClient(React.createElement(GAgentsPage));

    const loadingKinds = await screen.findByRole("status");
    expect(loadingKinds).toHaveAttribute("data-list-layout", "stack");
    expect(loadingKinds).toHaveAttribute("data-variant", "list");
    expect(screen.queryByText("Loading runtime GAgent kinds.")).toBeNull();

    resolveKinds([
      {
        agentKind: "Tests.OrdersGAgent",
        displayName: "Orders Assistant",
        diagnosticClrTypeName: "Tests.OrdersGAgent, Tests",
        endpoints: [],
      },
    ]);

    await waitFor(() => {
      expect(
        screen.getAllByRole("button", { name: "Manage actors" })[0],
      ).not.toBeDisabled();
    });
    const manageActors = screen.getAllByRole("button", {
      name: "Manage actors",
    });
    fireEvent.click(manageActors[0]);

    const loadingRegistry = await screen.findByRole("status");
    expect(loadingRegistry).toHaveAttribute("data-list-layout", "stack");
    expect(loadingRegistry).toHaveAttribute("data-variant", "list");
    expect(screen.queryByText("Loading actor registry.")).toBeNull();
  });

  it("switches existing actor suggestions to the clicked GAgent type", async () => {
    window.history.replaceState(
      {},
      "",
      "/runtime/gagents?scopeId=scope-a&actorId=orders-1"
    );

    renderWithQueryClient(React.createElement(GAgentsPage));

    expect(
      (await screen.findAllByText("Orders Assistant")).length
    ).toBeGreaterThan(0);
    await waitFor(() => {
      expect(mockedRuntimeGAgentApi.listActors).toHaveBeenCalledWith("scope-a");
    });

    const preferredActorInput = await screen.findByLabelText("Preferred actor id");
    fireEvent.change(preferredActorInput, {
      target: { value: "" },
    });
    fireEvent.click(
      screen.getByRole("button", { name: /PlannerGAgent/i })
    );

    expect((await screen.findAllByDisplayValue("planner-1")).length).toBeGreaterThan(0);
  });

  it("does not expose direct actor registration from the registry drawer", async () => {
    window.history.replaceState(
      {},
      "",
      "/runtime/gagents?scopeId=scope-a&type=Tests.OrdersGAgent,%20Tests"
    );

    renderWithQueryClient(React.createElement(GAgentsPage));

    expect(
      (await screen.findAllByText("Orders Assistant")).length
    ).toBeGreaterThan(0);
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Manage actors" })).not.toBeDisabled();
    });
    fireEvent.click(await screen.findByRole("button", { name: "Manage actors" }));
    expect((await screen.findAllByText("Actor Registry")).length).toBeGreaterThan(0);

    expect(screen.queryByRole("button", { name: "Save actor" })).toBeNull();
    expect(screen.queryByLabelText("Registry actor id")).toBeNull();

    fireEvent.click(screen.getAllByRole("button", { name: "Remove" })[0]);

    await waitFor(() => {
      expect(mockedRuntimeGAgentApi.removeActor).toHaveBeenCalledWith(
        "scope-a",
        "Tests.OrdersGAgent",
        "orders-1"
      );
    });
    await waitFor(() => {
      expect(screen.queryByDisplayValue("orders-1")).toBeNull();
    });
  });

  it("streams a direct GAgent draft run and hands it off to Runs", async () => {
    window.history.replaceState(
      {},
      "",
      "/runtime/gagents?scopeId=scope-a&type=Tests.OrdersGAgent,%20Tests"
    );

    renderWithQueryClient(React.createElement(GAgentsPage));

    expect(
      (await screen.findAllByText("Orders Assistant")).length
    ).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("tab", { name: "Draft Run" }));
    fireEvent.change(screen.getByLabelText("Draft prompt"), {
      target: { value: "hello agent" },
    });
    fireEvent.click(screen.getByRole("button", { name: /Run draft prompt/i }));

    await waitFor(() => {
      expect(mockedRuntimeGAgentApi.streamDraftRun).toHaveBeenCalledWith(
        "scope-a",
        {
          agentKind: "Tests.OrdersGAgent",
          prompt: "hello agent",
          preferredActorId: undefined,
          timeoutMs: 30000,
        },
        expect.any(AbortSignal)
      );
    });

    expect((await screen.findAllByText("hello from gagent")).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: /Continue in Runs/i }));

    expect(window.location.pathname).toBe("/runtime/runs");
    const draftKey = new URLSearchParams(window.location.search).get("draftKey");
    expect(draftKey).toBeTruthy();
    expect(loadDraftRunPayload(draftKey)).toEqual(
      expect.objectContaining({
        kind: "observed_run_session",
        scopeId: "scope-a",
        routeName: "Orders Assistant",
        endpointId: "chat",
        prompt: "hello agent",
        actorId: "orders-1",
        commandId: "cmd-1",
        runId: "run-1",
        events: [
          expect.objectContaining({
            type: "RUN_STARTED",
            runId: "run-1",
          }),
          expect.objectContaining({
            type: "CUSTOM",
            name: "aevatar.run.context",
          }),
          expect.objectContaining({
            type: "TEXT_MESSAGE_CONTENT",
            delta: "hello from gagent",
          }),
        ],
      })
    );
  });

  it("surfaces the current binding and active binding type in the workbench", async () => {
    mockedRuntimeGAgentApi.getDefaultRouteTarget.mockResolvedValue({
      available: true,
      scopeId: "scope-a",
      serviceId: "service-orders",
      displayName: "Orders Assistant",
      serviceKey: "default",
      defaultServingRevisionId: "rev-1",
      activeServingRevisionId: "rev-1",
      deploymentId: "deploy-1",
      deploymentStatus: "Ready",
      primaryActorId: "orders-1",
      updatedAt: "2026-03-31T08:00:00Z",
      revisions: [
        {
          revisionId: "rev-1",
          implementationKind: "gagent",
          status: "Ready",
          artifactHash: "artifact-1",
          failureReason: "",
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: "Ready",
          deploymentId: "deploy-1",
          primaryActorId: "orders-1",
          createdAt: "2026-03-31T07:00:00Z",
          preparedAt: "2026-03-31T07:05:00Z",
          publishedAt: "2026-03-31T07:10:00Z",
          retiredAt: null,
          workflowName: "",
          workflowDefinitionActorId: "",
          inlineWorkflowCount: 0,
          scriptId: "",
          scriptRevision: "",
          scriptDefinitionActorId: "",
          scriptSourceHash: "",
          staticActorTypeName: "Tests.OrdersGAgent, Tests",
          staticAgentKind: "Tests.OrdersGAgent",
          staticPreferredActorId: "orders-1",
        },
      ],
    });

    renderWithQueryClient(React.createElement(GAgentsPage));

    fireEvent.click(await screen.findByRole("tab", { name: "Serving" }));
    expect((await screen.findAllByText("Orders Assistant")).length).toBeGreaterThan(0);
    expect(await screen.findByText("Active binding")).toBeTruthy();
    expect((await screen.findAllByText("Tests.OrdersGAgent, Tests")).length).toBeGreaterThan(0);
    expect((await screen.findAllByText("rev-1")).length).toBeGreaterThan(0);
  });

  it("requires acknowledgement before replacing a published binding and then publishes the revision", async () => {
    mockedRuntimeGAgentApi.getDefaultRouteTarget.mockResolvedValue({
      available: true,
      scopeId: "scope-a",
      serviceId: "service-orders",
      displayName: "Orders Assistant",
      serviceKey: "default",
      defaultServingRevisionId: "rev-1",
      activeServingRevisionId: "rev-1",
      deploymentId: "deploy-1",
      deploymentStatus: "Ready",
      primaryActorId: "orders-1",
      updatedAt: "2026-03-31T08:00:00Z",
      revisions: [
        {
          revisionId: "rev-1",
          implementationKind: "gagent",
          status: "Ready",
          artifactHash: "artifact-1",
          failureReason: "",
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: "Ready",
          deploymentId: "deploy-1",
          primaryActorId: "orders-1",
          createdAt: "2026-03-31T07:00:00Z",
          preparedAt: "2026-03-31T07:05:00Z",
          publishedAt: "2026-03-31T07:10:00Z",
          retiredAt: null,
          workflowName: "",
          workflowDefinitionActorId: "",
          inlineWorkflowCount: 0,
          scriptId: "",
          scriptRevision: "",
          scriptDefinitionActorId: "",
          scriptSourceHash: "",
          staticActorTypeName: "Tests.OrdersGAgent, Tests",
          staticPreferredActorId: "orders-1",
        },
      ],
    });
    window.history.replaceState(
      {},
      "",
      "/runtime/gagents?scopeId=scope-a&type=Tests.OrdersGAgent,%20Tests"
    );

    renderWithQueryClient(React.createElement(GAgentsPage));

    fireEvent.click(await screen.findByRole("tab", { name: "Publish" }));
    fireEvent.change(await screen.findByLabelText("Binding display name"), {
      target: { value: "Orders Assistant" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Publish binding" }));

    expect(
      await screen.findByText(
        "Binding action could not be completed. Try again."
      )
    ).toBeTruthy();
    expect(
      screen.queryByText(
        "Acknowledge the replacement impact before publishing a new binding revision."
      )
    ).toBeNull();
    expect(mockedRuntimeGAgentApi.bindScopeGAgent).not.toHaveBeenCalled();

    fireEvent.click(
      screen.getByRole("checkbox", {
        name: "I understand this changes the workspace's published default service.",
      })
    );
    fireEvent.click(screen.getByRole("button", { name: "Publish binding" }));

    await waitFor(() => {
      expect(mockedRuntimeGAgentApi.bindScopeGAgent).toHaveBeenCalledWith({
        scopeId: "scope-a",
        displayName: "Orders Assistant",
        agentKind: "Tests.OrdersGAgent",
        preferredActorId: undefined,
        endpoints: [
          {
            endpointId: "run",
            displayName: "Run",
            kind: "command",
            requestTypeUrl:
              "type.googleapis.com/google.protobuf.StringValue",
            responseTypeUrl: undefined,
            description: "Run the published GAgent.",
          },
        ],
      });
    });

    expect(await screen.findByText("Published Orders Assistant.")).toBeTruthy();
    expect(screen.queryByText("Published Orders Assistant on revision rev-2.")).toBeNull();
  });

  it("activates and retires a selectable binding revision", async () => {
    mockedRuntimeGAgentApi.getDefaultRouteTarget.mockResolvedValue({
      available: true,
      scopeId: "scope-a",
      serviceId: "service-orders",
      displayName: "Orders Assistant",
      serviceKey: "default",
      defaultServingRevisionId: "rev-1",
      activeServingRevisionId: "rev-1",
      deploymentId: "deploy-1",
      deploymentStatus: "Ready",
      primaryActorId: "orders-1",
      updatedAt: "2026-03-31T08:00:00Z",
      revisions: [
        {
          revisionId: "rev-1",
          implementationKind: "gagent",
          status: "Ready",
          artifactHash: "artifact-1",
          failureReason: "",
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: "Ready",
          deploymentId: "deploy-1",
          primaryActorId: "orders-1",
          createdAt: "2026-03-31T07:00:00Z",
          preparedAt: "2026-03-31T07:05:00Z",
          publishedAt: "2026-03-31T07:10:00Z",
          retiredAt: null,
          workflowName: "",
          workflowDefinitionActorId: "",
          inlineWorkflowCount: 0,
          scriptId: "",
          scriptRevision: "",
          scriptDefinitionActorId: "",
          scriptSourceHash: "",
          staticActorTypeName: "Tests.OrdersGAgent, Tests",
          staticPreferredActorId: "orders-1",
        },
        {
          revisionId: "rev-2",
          implementationKind: "gagent",
          status: "Prepared",
          artifactHash: "artifact-2",
          failureReason: "",
          isDefaultServing: false,
          isActiveServing: false,
          isServingTarget: false,
          allocationWeight: 0,
          servingState: "Prepared",
          deploymentId: "deploy-2",
          primaryActorId: "",
          createdAt: "2026-03-31T08:00:00Z",
          preparedAt: "2026-03-31T08:05:00Z",
          publishedAt: null,
          retiredAt: null,
          workflowName: "",
          workflowDefinitionActorId: "",
          inlineWorkflowCount: 0,
          scriptId: "",
          scriptRevision: "",
          scriptDefinitionActorId: "",
          scriptSourceHash: "",
          staticActorTypeName: "Tests.OrdersGAgent, Tests",
          staticPreferredActorId: "",
        },
      ],
    });

    renderWithQueryClient(React.createElement(GAgentsPage));

    fireEvent.click(await screen.findByRole("tab", { name: "Serving" }));
    fireEvent.click(await screen.findByRole("button", { name: "Activate" }));
    await waitFor(() => {
      expect(
        mockedRuntimeGAgentApi.activateMemberBindingRevision
      ).toHaveBeenCalledWith("scope-a", "rev-2");
    });
    expect(
      await screen.findByText("Workspace is now serving the selected revision.")
    ).toBeTruthy();
    expect(screen.queryByText("Workspace scope-a is now serving revision rev-2.")).toBeNull();

    const retireButton = screen
      .getAllByRole("button", { name: "Retire" })
      .find((button) => !button.hasAttribute("disabled"));
    expect(retireButton).toBeTruthy();
    fireEvent.click(retireButton as HTMLElement);

    await waitFor(() => {
      expect(
        mockedRuntimeGAgentApi.retireMemberBindingRevision
      ).toHaveBeenCalledWith("scope-a", "rev-2");
    });
    expect(await screen.findByText("Revision was accepted for retirement.")).toBeTruthy();
    expect(screen.queryByText("Revision rev-2 was accepted for retirement.")).toBeNull();
  });
});
