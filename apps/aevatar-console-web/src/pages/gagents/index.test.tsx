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
    listTypes: jest.fn(),
    listActors: jest.fn(),
    getScopeBinding: jest.fn(),
    bindScopeGAgent: jest.fn(),
    activateScopeBindingRevision: jest.fn(),
    retireScopeBindingRevision: jest.fn(),
    addActor: jest.fn(),
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
    listTypes: jest.Mock;
    listActors: jest.Mock;
    getScopeBinding: jest.Mock;
    bindScopeGAgent: jest.Mock;
    activateScopeBindingRevision: jest.Mock;
    retireScopeBindingRevision: jest.Mock;
    addActor: jest.Mock;
    removeActor: jest.Mock;
    streamDraftRun: jest.Mock;
  };
  const mockedStudioApi = studioApi as unknown as {
    getAuthSession: jest.Mock;
  };
  let actorGroupsState: Array<{
    gAgentType: string;
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
    mockedRuntimeGAgentApi.listTypes.mockResolvedValue([
      {
        typeName: "OrdersGAgent",
        fullName: "Tests.OrdersGAgent",
        assemblyName: "Tests",
      },
      {
        typeName: "PlannerGAgent",
        fullName: "Tests.PlannerGAgent",
        assemblyName: "Tests",
      },
    ]);
    actorGroupsState = [
      {
        gAgentType: "Tests.OrdersGAgent",
        actorIds: ["orders-1"],
      },
      {
        gAgentType: "Tests.PlannerGAgent",
        actorIds: ["planner-1"],
      },
    ];
    mockedRuntimeGAgentApi.getScopeBinding.mockResolvedValue({
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
        actorTypeName: "Tests.OrdersGAgent, Tests",
        preferredActorId: "orders-1",
      },
    });
    mockedRuntimeGAgentApi.activateScopeBindingRevision.mockResolvedValue({
      scopeId: "scope-a",
      serviceId: "service-orders",
      displayName: "Orders Assistant",
      revisionId: "rev-2",
    });
    mockedRuntimeGAgentApi.retireScopeBindingRevision.mockResolvedValue({
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
    mockedRuntimeGAgentApi.addActor.mockImplementation(
      async (_scopeId: string, gAgentType: string, actorId: string) => {
        const existingGroup = actorGroupsState.find(
          (group) => group.gAgentType === gAgentType
        );
        if (existingGroup) {
          if (!existingGroup.actorIds.includes(actorId)) {
            existingGroup.actorIds = [...existingGroup.actorIds, actorId];
          }
          return;
        }

        actorGroupsState = [
          ...actorGroupsState,
          {
            gAgentType,
            actorIds: [actorId],
          },
        ];
      }
    );
    mockedRuntimeGAgentApi.removeActor.mockImplementation(
      async (_scopeId: string, gAgentType: string, actorId: string) => {
        actorGroupsState = actorGroupsState
          .map((group) =>
            group.gAgentType === gAgentType
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

  it("switches existing actor suggestions to the clicked GAgent type", async () => {
    window.history.replaceState(
      {},
      "",
      "/runtime/gagents?scopeId=scope-a&actorId=orders-1"
    );

    renderWithQueryClient(React.createElement(GAgentsPage));

    expect(
      (await screen.findAllByText("OrdersGAgent (Tests)")).length
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

  it("adds and removes saved actors from the registry", async () => {
    window.history.replaceState(
      {},
      "",
      "/runtime/gagents?scopeId=scope-a&type=Tests.OrdersGAgent,%20Tests"
    );

    renderWithQueryClient(React.createElement(GAgentsPage));

    expect(
      (await screen.findAllByText("OrdersGAgent (Tests)")).length
    ).toBeGreaterThan(0);
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Manage actors" })).not.toBeDisabled();
    });
    fireEvent.click(await screen.findByRole("button", { name: "Manage actors" }));
    expect((await screen.findAllByText("Actor Registry")).length).toBeGreaterThan(0);
    fireEvent.change(screen.getByLabelText("Registry actor id"), {
      target: { value: "orders-2" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save actor" }));

    await waitFor(() => {
      expect(mockedRuntimeGAgentApi.addActor).toHaveBeenCalledWith(
        "scope-a",
        "Tests.OrdersGAgent",
        "orders-2"
      );
    });
    expect(await screen.findByDisplayValue("orders-2")).toBeTruthy();

    fireEvent.click(screen.getAllByRole("button", { name: "Remove" })[1]);

    await waitFor(() => {
      expect(mockedRuntimeGAgentApi.removeActor).toHaveBeenCalledWith(
        "scope-a",
        "Tests.OrdersGAgent",
        "orders-2"
      );
    });
    await waitFor(() => {
      expect(screen.queryByDisplayValue("orders-2")).toBeNull();
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
      (await screen.findAllByText("OrdersGAgent (Tests)")).length
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
          actorTypeName: "Tests.OrdersGAgent, Tests",
          prompt: "hello agent",
          preferredActorId: undefined,
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
        routeName: "OrdersGAgent",
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
    mockedRuntimeGAgentApi.getScopeBinding.mockResolvedValue({
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

    renderWithQueryClient(React.createElement(GAgentsPage));

    fireEvent.click(await screen.findByRole("tab", { name: "Serving" }));
    expect((await screen.findAllByText("Orders Assistant")).length).toBeGreaterThan(0);
    expect(await screen.findByText("Active binding")).toBeTruthy();
    expect((await screen.findAllByText("Tests.OrdersGAgent, Tests")).length).toBeGreaterThan(0);
    expect((await screen.findAllByText("rev-1")).length).toBeGreaterThan(0);
  });

  it("requires acknowledgement before replacing a published binding and then publishes the revision", async () => {
    mockedRuntimeGAgentApi.getScopeBinding.mockResolvedValue({
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
        "Acknowledge the replacement impact before publishing a new binding revision."
      )
    ).toBeTruthy();
    expect(mockedRuntimeGAgentApi.bindScopeGAgent).not.toHaveBeenCalled();

    fireEvent.click(
      screen.getByRole("checkbox", {
        name: "I understand this changes the team's published default service.",
      })
    );
    fireEvent.click(screen.getByRole("button", { name: "Publish binding" }));

    await waitFor(() => {
      expect(mockedRuntimeGAgentApi.bindScopeGAgent).toHaveBeenCalledWith({
        scopeId: "scope-a",
        displayName: "Orders Assistant",
        actorTypeName: "Tests.OrdersGAgent, Tests",
        preferredActorId: undefined,
        endpoints: [
          {
            endpointId: "run",
            displayName: "Run",
            kind: "command",
            requestTypeUrl:
              "type.googleapis.com/google.protobuf.StringValue",
            responseTypeUrl: undefined,
            description: "Run the published team entry.",
          },
        ],
      });
    });

    expect(
      await screen.findByText("Published Orders Assistant on revision rev-2.")
    ).toBeTruthy();
  });

  it("activates and retires a selectable binding revision", async () => {
    mockedRuntimeGAgentApi.getScopeBinding.mockResolvedValue({
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
        mockedRuntimeGAgentApi.activateScopeBindingRevision
      ).toHaveBeenCalledWith("scope-a", "rev-2");
    });
    expect(
      await screen.findByText("Team scope-a is now serving revision rev-2.")
    ).toBeTruthy();

    const retireButton = screen
      .getAllByRole("button", { name: "Retire" })
      .find((button) => !button.hasAttribute("disabled"));
    expect(retireButton).toBeTruthy();
    fireEvent.click(retireButton as HTMLElement);

    await waitFor(() => {
      expect(
        mockedRuntimeGAgentApi.retireScopeBindingRevision
      ).toHaveBeenCalledWith("scope-a", "rev-2");
    });
    expect(
      await screen.findByText("Revision rev-2 was accepted for retirement.")
    ).toBeTruthy();
  });
});
