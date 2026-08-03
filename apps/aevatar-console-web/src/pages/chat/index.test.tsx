import { fireEvent, screen, waitFor } from "@testing-library/react";
import * as React from "react";
import { authFetch } from "@/shared/auth/fetch";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import { chatHistoryApi } from "./chatHistoryApi";
import {
  createNyxIdCatalogKey,
  listNyxIdConnectors,
} from "./nyxIdServiceApi";
import ChatPage, { hydrateStoredMessages } from "./index";

jest.mock("@/shared/auth/fetch", () => ({ authFetch: jest.fn() }));
jest.mock("./chatHistoryApi", () => ({
  chatHistoryApi: {
    deleteConversation: jest.fn(),
    listConversationMetas: jest.fn(),
    loadConversation: jest.fn(),
    loadConversationState: jest.fn(),
  },
}));
jest.mock("./nyxIdServiceApi", () => ({
  buildNyxIdConnectUrl: jest.fn(() => "https://nyx.example/keys?slug=api-github"),
  createNyxIdCatalogKey: jest.fn(),
  listNyxIdConnectors: jest.fn(),
  matchNewUserServiceId: jest.requireActual("./nyxIdServiceApi")
    .matchNewUserServiceId,
  matchingUserServiceIds: jest.requireActual("./nyxIdServiceApi")
    .matchingUserServiceIds,
}));
jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(),
  },
}));
jest.mock("@/shared/navigation/history", () => ({
  history: { push: jest.fn() },
}));
jest.mock("@/shared/ui/aevatarPageShells", () => {
  const mockReact = require("react");
  return {
    AevatarPageShell: ({ children, title }: any) =>
      mockReact.createElement(
        "section",
        null,
        title ? mockReact.createElement("h1", null, title) : null,
        children
      ),
  };
});

const serverConversation = {
  createdAt: "2026-08-04T02:30:00+00:00",
  id: "conversation-alpha",
  messageCount: 1,
  title: "Canonical conversation",
  updatedAt: "2026-08-04T02:35:00+00:00",
};

function currentState(overrides: Record<string, unknown> = {}) {
  return {
    status: "current",
    stateVersion: 7,
    turnId: "turn-alpha",
    snapshot: {
      actorId: "conversation-alpha",
      scopeId: "scope-alpha",
      stateVersion: 7,
      progressSequence: 7,
      activeTurn: null,
      latestTurn: {
        turnId: "turn-alpha",
        taskId: "task-alpha",
        status: "succeeded",
      },
      recentTerminalTurns: [],
      activeTask: null,
      pendingInput: null,
      pendingApproval: null,
      pendingActions: [],
      ...overrides,
    },
  };
}

function sseResponse(frames: readonly unknown[]): Response {
  const encoder = new TextEncoder();
  return {
    body: new ReadableStream({
      start(controller) {
        controller.enqueue(
          encoder.encode(
            frames.map((frame) => `data: ${JSON.stringify(frame)}\n\n`).join("")
          )
        );
        controller.close();
      },
    }),
    ok: true,
    status: 200,
  } as Response;
}

function runStarted(
  conversationId = "conversation-alpha",
  turnId = "turn-alpha"
) {
  return {
    type: "RUN_STARTED",
    actorId: conversationId,
    turnId,
    runStarted: { threadId: conversationId, runId: turnId },
  };
}

function completedStream(
  output: string,
  conversationId = "conversation-alpha",
  turnId = "turn-alpha",
  extra: readonly unknown[] = []
): Response {
  return sseResponse([
    runStarted(conversationId, turnId),
    ...extra,
    {
      type: "TEXT_MESSAGE_CONTENT",
      textMessageContent: { delta: output },
    },
    { type: "RUN_FINISHED", runFinished: { runId: turnId } },
  ]);
}

function requestBodies(): Record<string, unknown>[] {
  return (authFetch as jest.Mock).mock.calls
    .filter(([path]) => path === "/api/chat")
    .map(([, request]) => JSON.parse(request.body));
}

async function sendPrompt(prompt: string): Promise<void> {
  await screen.findByText("Scope scope-alpha");
  const input = await screen.findByPlaceholderText(
    "Describe the workflow you want, or ask about the current setup..."
  );
  fireEvent.change(input, { target: { value: prompt } });
  await waitFor(() =>
    expect(screen.getByRole("button", { name: "Send" })).toBeEnabled()
  );
  fireEvent.click(screen.getByRole("button", { name: "Send" }));
}

describe("ChatPage canonical NyxID Assistant", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    window.history.replaceState({}, "", "/chat");
    window.sessionStorage.clear();
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      authenticated: true,
      enabled: true,
      scopeId: "scope-alpha",
      scopeSource: "nyxid",
    });
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue({
      messages: [],
      stateVersion: 0,
    });
    (chatHistoryApi.loadConversationState as jest.Mock).mockResolvedValue({
      status: "not_found",
    });
    (chatHistoryApi.deleteConversation as jest.Mock).mockResolvedValue(undefined);
    (listNyxIdConnectors as jest.Mock).mockResolvedValue({
      connected: [],
      available: [],
    });
  });

  it("sends typed first and continuation turns using RUN_STARTED identity", async () => {
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(completedStream("First answer"))
      .mockResolvedValueOnce(
        completedStream("Second answer", "conversation-alpha", "turn-beta")
      );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Create a workflow");
    expect(await screen.findByText("First answer")).toBeInTheDocument();
    await sendPrompt("Continue safely");
    expect(await screen.findByText("Second answer")).toBeInTheDocument();

    const [first, second] = requestBodies();
    expect(first).toEqual({
      type: "text",
      prompt: "Create a workflow",
      clientRequestId: expect.any(String),
    });
    expect(second).toEqual({
      type: "text",
      conversationId: "conversation-alpha",
      prompt: "Continue safely",
      clientRequestId: expect.any(String),
    });
    expect(second.clientRequestId).not.toBe(first.clientRequestId);
    expect(first).not.toHaveProperty("scopeId");
    expect(first).not.toHaveProperty("sessionId");
    expect(first).not.toHaveProperty("workflow");
    expect(first).not.toHaveProperty("conversation");
  });

  it("restores transcript and current state from canonical conversation resources", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue({
      messages: [
        {
          id: "turn-alpha:user",
          turnId: "turn-alpha",
          role: "user",
          content: "Hello",
          timestamp: 1,
          status: "complete",
        },
        {
          id: "turn-alpha:assistant",
          turnId: "turn-alpha",
          role: "assistant",
          content: "Restored answer",
          timestamp: 2,
          status: "complete",
        },
      ],
      stateVersion: 7,
    });
    (chatHistoryApi.loadConversationState as jest.Mock).mockResolvedValue(
      currentState()
    );

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Canonical conversation" })
    );

    expect(await screen.findByText("Restored answer")).toBeInTheDocument();
    expect(chatHistoryApi.listConversationMetas).toHaveBeenCalledWith();
    expect(chatHistoryApi.loadConversation).toHaveBeenCalledWith(
      "conversation-alpha"
    );
    expect(chatHistoryApi.loadConversationState).toHaveBeenCalledWith(
      "conversation-alpha"
    );
  });

  it("dispatches pending input and actor-authored step controls with exact identities", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversationState as jest.Mock).mockResolvedValue(
      currentState({
        activeTurn: {
          turnId: "turn-alpha",
          taskId: "task-alpha",
          status: "active",
        },
        activeTask: {
          taskId: "task-alpha",
          turnId: "turn-alpha",
          status: "active",
          steps: [
            {
              stepId: "step-retry",
              order: 1,
              description: "Fetch repository",
              availableActions: { retry: true, skip: false, stop: true },
              operation: { operationGeneration: 3 },
            },
          ],
        },
        pendingInput: {
          requestId: "input-alpha",
          turnId: "turn-alpha",
          taskId: "task-alpha",
          stepId: "step-input",
          prompt: "Select a region",
          options: [{ optionId: "option-sg", label: "Singapore" }],
          allowFreeText: false,
          multiSelect: false,
        },
      })
    );
    (authFetch as jest.Mock).mockResolvedValue({ ok: true, status: 202 });

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Canonical conversation" })
    );
    fireEvent.click(await screen.findByRole("radio", { name: "Singapore" }));
    fireEvent.click(screen.getByRole("button", { name: "Submit answer" }));
    await waitFor(() => expect(requestBodies()).toHaveLength(1));
    expect(requestBodies()[0]).toEqual({
      type: "input.resolve",
      conversationId: "conversation-alpha",
      requestId: "input-alpha",
      clientRequestId: expect.any(String),
      answer: { selectedOptionIds: ["option-sg"] },
      expectedStateVersion: 7,
    });

    const retry = screen.getByRole("button", {
      name: "Retry Fetch repository",
    });
    await waitFor(() => expect(retry).toBeEnabled());
    fireEvent.click(retry);
    await waitFor(() => expect(requestBodies()).toHaveLength(2));
    expect(requestBodies()[1]).toEqual({
      type: "step.retry",
      conversationId: "conversation-alpha",
      turnId: "turn-alpha",
      taskId: "task-alpha",
      stepId: "step-retry",
      retryRequestId: expect.any(String),
      clientRequestId: expect.any(String),
      expectedOperationGeneration: 3,
      expectedStateVersion: 7,
    });
    expect(await screen.findByText("Request accepted")).toBeInTheDocument();
  });

  it("reports exactly one newly connected UserService through action.continue", async () => {
    const action = {
      schemaVersion: 4,
      actorId: "conversation-alpha",
      originTurnId: "turn-alpha",
      taskId: "task-alpha",
      stepId: "step-connect",
      actionRequestId: "action-alpha",
      action: "service.connect",
      params: { catalogService: { serviceSlug: "api-github" } },
    };
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(
        completedStream("Connect GitHub", "conversation-alpha", "turn-alpha", [
          {
            type: "CUSTOM",
            sequence: 4,
            custom: { name: "nyxid.action.request", payload: action },
          },
        ])
      )
      .mockResolvedValueOnce(
        completedStream("Connection reported", "conversation-alpha", "turn-beta")
      );
    (chatHistoryApi.loadConversationState as jest.Mock).mockResolvedValue(
      currentState({
        pendingActions: [
          {
            schemaVersion: 4,
            originTurnId: "turn-alpha",
            taskId: "task-alpha",
            stepId: "step-connect",
            actionRequestId: "action-alpha",
            action: "service.connect",
            reports: [],
            postconditionResult: null,
          },
        ],
      })
    );
    (listNyxIdConnectors as jest.Mock)
      .mockResolvedValueOnce({ connected: [], available: [] })
      .mockResolvedValueOnce({
        connected: [
          {
            slug: "api-github",
            name: "GitHub",
            description: "",
            authKind: "oauth",
            userServices: [
              {
                userServiceId: "user-service-new",
                apiKeyId: "api-key-not-resource",
                endpointUrl: "https://api.github.com",
                label: "GitHub",
              },
            ],
          },
        ],
        available: [],
      });
    const open = jest.spyOn(window, "open").mockReturnValue({
      focus: jest.fn(),
    } as unknown as Window);

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Connect GitHub");
    fireEvent.click(
      await screen.findByRole("button", { name: "Open NyxID connection" })
    );
    await waitFor(() => expect(open).toHaveBeenCalled());
    fireEvent.click(screen.getByRole("button", { name: "Refresh connection" }));
    await waitFor(() => expect(requestBodies()).toHaveLength(2));

    expect(requestBodies()[1]).toEqual({
      type: "action.continue",
      conversationId: "conversation-alpha",
      originTurnId: "turn-alpha",
      clientRequestId: expect.any(String),
      actions: [
        {
          actionRequestId: "action-alpha",
          originTurnId: "turn-alpha",
          disposition: "completed",
          resource: {
            userService: { userServiceId: "user-service-new" },
          },
        },
      ],
    });
    expect(JSON.stringify(requestBodies()[1])).not.toContain("api-key-not-resource");
  });

  it("connects a catalog credential directly to NyxID without persisting it", async () => {
    const action = {
      schemaVersion: 4,
      actorId: "conversation-alpha",
      originTurnId: "turn-alpha",
      taskId: "task-alpha",
      stepId: "step-connect",
      actionRequestId: "action-alpha",
      action: "service.connect",
      params: { catalogService: { serviceSlug: "api-github" } },
    };
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(
        completedStream("Connect GitHub", "conversation-alpha", "turn-alpha", [
          {
            type: "CUSTOM",
            sequence: 4,
            custom: { name: "nyxid.action.request", payload: action },
          },
        ])
      )
      .mockResolvedValueOnce(
        completedStream("Credential reported", "conversation-alpha", "turn-beta")
      );
    (createNyxIdCatalogKey as jest.Mock).mockResolvedValue("user-service-created");

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Connect GitHub");
    const credential = await screen.findByLabelText("api-github credential");
    fireEvent.change(credential, { target: { value: "secret-value" } });
    fireEvent.click(
      screen.getByRole("button", { name: "Connect api-github" })
    );
    await waitFor(() =>
      expect(createNyxIdCatalogKey).toHaveBeenCalledWith({
        serviceSlug: "api-github",
        credential: "secret-value",
        label: "api-github",
      })
    );
    await waitFor(() => expect(requestBodies()).toHaveLength(2));
    expect(JSON.stringify(requestBodies()[1])).not.toContain("secret-value");
    expect(window.sessionStorage.getItem("secret-value")).toBeNull();
    expect(credential).toHaveValue("");
  });

  it("does not present a rejected action report as accepted", async () => {
    const action = {
      schemaVersion: 4,
      actorId: "conversation-alpha",
      originTurnId: "turn-alpha",
      taskId: "task-alpha",
      stepId: "step-connect",
      actionRequestId: "action-alpha",
      action: "service.connect",
      params: { catalogService: { serviceSlug: "api-github" } },
    };
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(
        completedStream("Connect GitHub", "conversation-alpha", "turn-alpha", [
          {
            type: "CUSTOM",
            sequence: 4,
            custom: { name: "nyxid.action.request", payload: action },
          },
        ])
      )
      .mockRejectedValueOnce(new Error("network unavailable"));

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Connect GitHub");
    fireEvent.click(await screen.findByRole("button", { name: "Decline" }));

    expect(
      await screen.findByText("Action report was not accepted.")
    ).toBeInTheDocument();
    expect(
      screen.queryByText("Reported; waiting for actor verification")
    ).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Decline" })).toBeInTheDocument();
  });

  it("submits canonical delete and never infers approval from assistant prose", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (authFetch as jest.Mock).mockResolvedValue(
      completedStream("Please confirm this explanation only.")
    );

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Delete Canonical conversation" })
    );
    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    await waitFor(() =>
      expect(chatHistoryApi.deleteConversation).toHaveBeenCalledWith(
        "conversation-alpha"
      )
    );

    await sendPrompt("Explain confirmation");
    expect(
      await screen.findByText("Please confirm this explanation only.")
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Confirm and create" })
    ).not.toBeInTheDocument();
  });
});

describe("hydrateStoredMessages", () => {
  it("preserves extensible roles and maps stored errors to presentation errors", () => {
    expect(
      hydrateStoredMessages([
        {
          authorName: "Automation",
          content: "Queued",
          id: "observer",
          role: "observer",
          status: "queued",
          timestamp: 1,
        },
        {
          content: "",
          error: "Stopped",
          id: "assistant",
          role: "assistant",
          status: "complete",
          timestamp: 2,
        },
      ])
    ).toEqual([
      expect.objectContaining({ role: "observer", status: "queued" }),
      expect.objectContaining({ role: "assistant", status: "error" }),
    ]);
  });
});
