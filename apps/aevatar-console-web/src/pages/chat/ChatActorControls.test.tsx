import { fireEvent, render, screen } from "@testing-library/react";
import React from "react";
import {
  chatActionIdentityKey,
  createChatActorProjection,
} from "./chatActorState";
import { ChatActorControls } from "./ChatActorControls";

function projectionFixture() {
  const projection = createChatActorProjection("conversation-alpha");
  projection.stateVersion = 7;
  projection.activeTurn = {
    turnId: "turn-alpha",
    taskId: "task-alpha",
    status: "active",
  };
  projection.task = {
    taskId: "task-alpha",
    turnId: "turn-alpha",
    status: "active",
  };
  projection.pendingInput = {
    requestId: "input-alpha",
    turnId: "turn-alpha",
    taskId: "task-alpha",
    stepId: "step-input",
    prompt: "Select a region",
    options: [
      { optionId: "option-sg", label: "Singapore" },
      { optionId: "option-fra", label: "Frankfurt" },
    ],
    allowFreeText: false,
    multiSelect: false,
  };
  projection.steps.set("step-retry", {
    stepId: "step-retry",
    order: 1,
    description: "Connect repository",
    availableActions: { retry: true, skip: false, stop: true },
    operation: { operationGeneration: 2 },
  });
  projection.steps.set("step-skip", {
    stepId: "step-skip",
    order: 2,
    description: "Optional summary",
    availableActions: { retry: false, skip: true, stop: false },
    operation: { operationGeneration: 0 },
  });
  return projection;
}

function callbacks() {
  return {
    onActionOpen: jest.fn(),
    onActionConnectCredential: jest.fn(),
    onActionRefresh: jest.fn(),
    onActionReport: jest.fn(),
    onApprovalResolve: jest.fn(),
    onInputResolve: jest.fn(),
    onRetry: jest.fn(),
    onSkip: jest.fn(),
    onSteer: jest.fn(),
    onStop: jest.fn(),
  };
}

describe("ChatActorControls", () => {
  it("submits actor option IDs and only actor-authored step controls", () => {
    const handlers = callbacks();
    render(
      <ChatActorControls projection={projectionFixture()} {...handlers} />
    );

    fireEvent.click(screen.getByRole("radio", { name: "Singapore" }));
    fireEvent.click(screen.getByRole("button", { name: "Submit answer" }));
    expect(handlers.onInputResolve).toHaveBeenCalledWith(
      { selectedOptionIds: ["option-sg"] },
      expect.objectContaining({ requestId: "input-alpha" })
    );

    fireEvent.click(
      screen.getByRole("button", { name: "Retry Connect repository" })
    );
    fireEvent.click(
      screen.getByRole("button", { name: "Skip Optional summary" })
    );
    expect(handlers.onRetry).toHaveBeenCalledWith(
      expect.objectContaining({ stepId: "step-retry" })
    );
    expect(handlers.onSkip).toHaveBeenCalledWith(
      expect.objectContaining({ stepId: "step-skip" })
    );
    expect(
      screen.queryByRole("button", { name: "Skip Connect repository" })
    ).not.toBeInTheDocument();
  });

  it("renders approval, stop, and steering only from current actor facts", () => {
    const projection = projectionFixture();
    projection.pendingInput = null;
    projection.pendingApproval = {
      approvalRequestId: "approval-alpha",
      turnId: "turn-alpha",
      taskId: "task-alpha",
      stepId: "step-approval",
      toolName: "deploy",
      action: "Deploy",
      target: "production",
      reversibility: "irreversible",
    };
    const handlers = callbacks();
    const { rerender } = render(
      <ChatActorControls projection={projection} {...handlers} />
    );

    fireEvent.click(screen.getByRole("button", { name: "Approve" }));
    expect(handlers.onApprovalResolve).toHaveBeenCalledWith(
      true,
      expect.objectContaining({ approvalRequestId: "approval-alpha" }),
      undefined
    );
    fireEvent.change(screen.getByLabelText("Steering instruction"), {
      target: { value: "Use read-only access" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Steer task" }));
    expect(handlers.onSteer).toHaveBeenCalledWith("Use read-only access");
    fireEvent.click(screen.getByRole("button", { name: "Stop task" }));
    expect(handlers.onStop).toHaveBeenCalledTimes(1);

    rerender(
      <ChatActorControls
        projection={createChatActorProjection("conversation-alpha")}
        {...handlers}
      />
    );
    expect(screen.queryByRole("button", { name: "Stop task" })).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Steering instruction")).not.toBeInTheDocument();
  });

  it("keeps browser completion pending until exact actor proof arrives", () => {
    const projection = createChatActorProjection("conversation-alpha");
    const request = {
      schemaVersion: 4 as const,
      actorId: "conversation-alpha",
      originTurnId: "turn-alpha",
      taskId: "task-alpha",
      stepId: "step-connect",
      actionRequestId: "action-alpha",
      action: "service.connect" as const,
      params: { catalogService: { serviceSlug: "api-github" } },
    };
    projection.actions.set("action-alpha", {
      ...request,
      request,
      reports: [],
      postconditionResult: null,
    });
    const handlers = callbacks();
    const journeys = new Map([
      [
        chatActionIdentityKey("conversation-alpha", "action-alpha"),
        {
          report: {
            actionRequestId: "action-alpha",
            originTurnId: "turn-alpha",
            disposition: "completed" as const,
            resource: {
              userService: { userServiceId: "user-service-alpha" },
            },
          },
        },
      ],
    ]);
    const { rerender } = render(
      <ChatActorControls
        actionJourneys={journeys}
        projection={projection}
        {...handlers}
      />
    );

    expect(screen.getByText("Reported; waiting for actor verification")).toBeInTheDocument();
    expect(screen.queryByText("Actor verified")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Refresh connection" }));
    expect(handlers.onActionRefresh).toHaveBeenCalledWith(request);

    const action = projection.actions.get("action-alpha");
    if (!action) throw new Error("Expected the action fixture to exist.");
    projection.actions.set("action-alpha", {
      ...action,
      postconditionResult: {
        actionRequestId: "action-alpha",
        disposition: "completed",
        verified: true,
        resource: {
          userServiceId: "wrong-shape-is-not-proof",
        },
      },
    });
    rerender(
      <ChatActorControls
        actionJourneys={journeys}
        projection={projection}
        {...handlers}
      />
    );
    expect(screen.queryByText("Actor verified")).not.toBeInTheDocument();

    projection.actions.set("action-alpha", {
      ...action,
      postconditionResult: null,
    });
    projection.steps.set("step-proof", {
      stepId: "step-proof",
      actionRequestId: "action-alpha",
      kind: "postcondition",
      status: "done",
      externalEffect: "confirmed",
    });
    rerender(
      <ChatActorControls
        actionJourneys={journeys}
        projection={projection}
        {...handlers}
      />
    );
    expect(screen.getByText("Actor verified")).toBeInTheDocument();

    projection.steps.clear();

    projection.actions.set("action-alpha", {
      ...action,
      postconditionResult: {
        actionRequestId: "action-alpha",
        disposition: "completed",
        verified: true,
        resource: {
          userService: { userServiceId: "user-service-alpha" },
        },
      },
    });
    rerender(
      <ChatActorControls
        actionJourneys={journeys}
        projection={projection}
        {...handlers}
      />
    );
    expect(screen.getByText("Actor verified")).toBeInTheDocument();
  });

  it("restores a verified browser action from actor-owned report facts", () => {
    const projection = createChatActorProjection("conversation-alpha");
    const request = {
      schemaVersion: 4 as const,
      actorId: "conversation-alpha",
      originTurnId: "turn-alpha",
      taskId: "task-alpha",
      stepId: "step-connect",
      actionRequestId: "action-alpha",
      action: "service.connect" as const,
      params: { catalogService: { serviceSlug: "api-github" } },
    };
    projection.actions.set("action-alpha", {
      ...request,
      request,
      reports: [
        {
          actionRequestId: "action-alpha",
          originTurnId: "turn-alpha",
          disposition: "completed",
          resource: { userServiceId: "user-service-alpha" },
        },
      ],
      postconditionResult: {
        actionRequestId: "action-alpha",
        disposition: "completed",
        verified: true,
        resource: { userServiceId: "user-service-alpha" },
      },
    });

    render(
      <ChatActorControls projection={projection} {...callbacks()} />
    );

    expect(screen.getByText("Actor verified")).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Open NyxID connection" })
    ).not.toBeInTheDocument();
    expect(screen.queryByLabelText("api-github credential")).not.toBeInTheDocument();
  });

  it("reads browser journey state only from the matching conversation actor", () => {
    const projection = createChatActorProjection("conversation-beta");
    const request = {
      schemaVersion: 4 as const,
      actorId: "conversation-beta",
      originTurnId: "turn-beta",
      taskId: "task-beta",
      stepId: "step-connect",
      actionRequestId: "action-shared",
      action: "service.connect" as const,
      params: { catalogService: { serviceSlug: "api-github" } },
    };
    projection.actions.set(request.actionRequestId, {
      ...request,
      request,
      reports: [],
      postconditionResult: null,
    });

    render(
      <ChatActorControls
        actionJourneys={
          new Map([
            [
              chatActionIdentityKey("conversation-alpha", "action-shared"),
              { error: "Wrong conversation journey" },
            ],
            [
              chatActionIdentityKey("conversation-beta", "action-shared"),
              { error: "Current conversation journey" },
            ],
          ])
        }
        projection={projection}
        {...callbacks()}
      />
    );

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Current conversation journey"
    );
    expect(screen.queryByText("Wrong conversation journey")).not.toBeInTheDocument();
  });

  it("disables a browser journey whose action identity conflicted", () => {
    const projection = createChatActorProjection("conversation-alpha");
    const request = {
      schemaVersion: 4 as const,
      actorId: "conversation-alpha",
      originTurnId: "turn-alpha",
      taskId: "task-alpha",
      stepId: "step-connect",
      actionRequestId: "action-alpha",
      action: "service.connect" as const,
      params: { catalogService: { serviceSlug: "api-github" } },
    };
    projection.actions.set(request.actionRequestId, {
      ...request,
      conflicted: true,
      request,
      reports: [],
      postconditionResult: null,
    });

    render(<ChatActorControls projection={projection} {...callbacks()} />);

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Action identity conflict; this browser journey is disabled."
    );
    expect(
      screen.queryByRole("button", { name: "Open NyxID connection" })
    ).not.toBeInTheDocument();
    expect(screen.queryByLabelText("api-github credential")).not.toBeInTheDocument();
  });
});
