import { act, fireEvent, screen, waitFor } from "@testing-library/react";
import * as React from "react";
import { authFetch } from "@/shared/auth/fetch";
import { history } from "@/shared/navigation/history";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import { chatHistoryApi } from "./chatHistoryApi";
import ChatPage, { hydrateStoredMessages } from "./index";

const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

jest.mock("@/shared/auth/fetch", () => ({
  authFetch: jest.fn(),
}));

jest.mock("./chatHistoryApi", () => ({
  ChatHistoryApiError: class MockChatHistoryApiError extends Error {
    code?: string;
    status: number;

    constructor(message: string, status: number, code?: string) {
      super(message);
      this.code = code;
      this.status = status;
    }
  },
  ChatHistoryContractError: class MockChatHistoryContractError extends Error {
    code = "INVALID_CHAT_HISTORY_RESPONSE";
    path: string;

    constructor(path: string, expectation: string) {
      super(`Invalid Chat History response at ${path}: expected ${expectation}.`);
      this.path = path;
    }
  },
  chatHistoryApi: {
    deleteConversation: jest.fn(),
    listConversationMetas: jest.fn(),
    loadConversation: jest.fn(),
    recoverCreate: jest.fn(),
  },
}));

jest.mock("@/shared/navigation/history", () => ({
  history: {
    push: jest.fn(),
  },
}));

jest.mock("@/shared/ui/ConsoleToast", () => ({
  useConsoleToast: () => mockConsoleToast,
}));

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(async () => ({
      authenticated: true,
      enabled: true,
      scopeId: "scope-a",
      scopeSource: "nyxid",
    })),
  },
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

const CHAT_HISTORY_CONTEXT_TYPE =
  "type.googleapis.com/aevatar.workflow.runs.WorkflowChatContextPayload";

const serverConversation = {
  createdAt: "2026-07-17T02:30:00+00:00",
  id: "conversation-a",
  messageCount: 2,
  serviceId: "",
  serviceKind: "",
  title: "Server conversation",
  updatedAt: "2026-07-17T02:35:00+00:00",
};

const serverMessages = [
  {
    content: "Create a support workflow",
    id: "turn-a:user",
    role: "user" as const,
    status: "complete" as const,
    timestamp: 1784255700000,
    turnId: "turn-a",
  },
  {
    content: "The support workflow is ready.",
    id: "turn-a:assistant",
    role: "assistant" as const,
    status: "complete" as const,
    timestamp: 1784255700000,
    turnId: "turn-a",
  },
];

type StoredMessageFixture = {
  authorName?: string;
  content: string;
  error?: string;
  id: string;
  role: string;
  status: string;
  thinking?: string;
  timestamp: number;
  turnId?: string;
};

function conversationDetail(
  messages: StoredMessageFixture[] = serverMessages,
  stateVersion = 7
): { messages: StoredMessageFixture[]; stateVersion: number } {
  return { messages, stateVersion };
}

function chatContextFrame(
  conversationId = "conversation-a",
  turnId = "turn-a",
  scopeId = "scope-a",
  stateVersion = 7
): unknown {
  return {
    custom: {
      name: "aevatar.chat.context",
      payload: {
        "@type": CHAT_HISTORY_CONTEXT_TYPE,
        conversationId,
        scopeId,
        stateVersion,
        turnId,
      },
    },
    timestamp: 1784255700000,
  };
}

function createSseResponse(frames: readonly unknown[]): Response {
  const encoder = new TextEncoder();
  const body = frames
    .map((frame) => `data: ${JSON.stringify(frame)}\n\n`)
    .join("");

  return {
    body: new ReadableStream({
      start(controller) {
        controller.enqueue(encoder.encode(body));
        controller.close();
      },
    }),
    ok: true,
  } as Response;
}

function historyReservationUnavailableResponse(): Response {
  return {
    ok: false,
    status: 503,
    statusText: "Service Unavailable",
    text: jest.fn().mockResolvedValue(
      JSON.stringify({
        code: "CHAT_HISTORY_RESERVATION_UNAVAILABLE",
        message: "Conversation history is still materializing.",
      })
    ),
  } as unknown as Response;
}

function createControlledSseResponse(): {
  close: () => void;
  enqueue: (frame: unknown) => void;
  fail: (error: Error) => void;
  response: Response;
} {
  const encoder = new TextEncoder();
  let streamController: ReadableStreamDefaultController<Uint8Array> | null = null;
  const response = {
    body: new ReadableStream({
      start(controller) {
        streamController = controller;
      },
    }),
    ok: true,
  } as Response;

  return {
    close: () => streamController?.close(),
    enqueue: (frame: unknown) => {
      streamController?.enqueue(
        encoder.encode(`data: ${JSON.stringify(frame)}\n\n`)
      );
    },
    fail: (error: Error) => streamController?.error(error),
    response,
  };
}

function setNativeTextareaValue(element: HTMLElement, value: string): void {
  const prototype = Object.getPrototypeOf(element);
  const valueSetter = Object.getOwnPropertyDescriptor(element, "value")?.set;
  const prototypeValueSetter = Object.getOwnPropertyDescriptor(
    prototype,
    "value"
  )?.set;

  if (prototypeValueSetter && valueSetter !== prototypeValueSetter) {
    prototypeValueSetter.call(element, value);
  } else {
    valueSetter?.call(element, value);
  }
}

async function sendPrompt(prompt: string): Promise<void> {
  const composer = await screen.findByPlaceholderText(
    "Describe the workflow you want, or ask about the current setup..."
  );
  setNativeTextareaValue(composer, prompt);
  fireEvent.input(composer, { bubbles: true });
  fireEvent.change(composer, { target: { value: prompt } });
  await waitFor(() =>
    expect(screen.getByRole("button", { name: "Send" })).toBeEnabled()
  );
  fireEvent.click(screen.getByRole("button", { name: "Send" }));
}

function chatRequestBodies(): Array<Record<string, unknown>> {
  return (authFetch as jest.Mock).mock.calls
    .filter(([path]) => path === "/api/chat")
    .map(([, request]) => JSON.parse(request.body));
}

function renderScopeSwitchableChat(initialScopeId = "scope-a") {
  let updateScope = (_scopeId: string): void => undefined;
  window.history.replaceState({}, "", `/chat?scopeId=${initialScopeId}`);

  function ScopeHarness() {
    const [, setRevision] = React.useState(0);
    updateScope = (scopeId: string) => {
      window.history.pushState({}, "", `/chat?scopeId=${scopeId}`);
      setRevision((current) => current + 1);
    };
    return <ChatPage />;
  }

  const view = renderWithQueryClient(<ScopeHarness />);
  return {
    ...view,
    switchScope(scopeId: string) {
      act(() => updateScope(scopeId));
    },
  };
}

describe("ChatPage server-backed history", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    window.history.replaceState({}, "", "/chat");
    window.localStorage.clear();
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue(
      conversationDetail([])
    );
    (chatHistoryApi.deleteConversation as jest.Mock).mockResolvedValue(
      undefined
    );
    const { ChatHistoryApiError } = jest.requireMock("./chatHistoryApi");
    (chatHistoryApi.recoverCreate as jest.Mock).mockRejectedValue(
      new ChatHistoryApiError("Recovery is not materialized.", 404)
    );
  });

  it("loads server history and restores its detail without local-only controls", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue(
      conversationDetail()
    );

    renderWithQueryClient(<ChatPage />);

    expect(document.querySelector(".aevatar-chat-main-header")).toBeTruthy();

    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );

    expect(await screen.findByText("The support workflow is ready.")).toBeTruthy();
    const messageList = document.querySelector<HTMLElement>(
      ".aevatar-chat-message-list"
    );
    expect(messageList).not.toBeNull();
    expect(messageList?.style.marginInline).toBe("auto");
    expect(messageList?.style.maxWidth).toBe("1440px");
    expect(messageList?.style.width).toBe("100%");
    expect(chatHistoryApi.loadConversation).toHaveBeenCalledWith(
      "scope-a",
      "conversation-a"
    );
    expect(screen.getByText("2 turns")).toBeTruthy();
    expect(screen.queryByText("History is stored in this browser.")).toBeNull();
    expect(screen.queryByRole("button", { name: /rename/i })).toBeNull();
  });

  it("recovers a create identity when the stream completes without context", async () => {
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        { runFinished: { result: { output: "Recovered response" } } },
      ])
    );
    (chatHistoryApi.recoverCreate as jest.Mock).mockResolvedValue({
      conversationId: "recovered-conversation",
      stateVersion: 2,
      status: "append_committed",
      turnId: "recovered-turn",
    });

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Recover this create");

    expect(await screen.findByText("Recovered response")).toBeTruthy();
    const [body] = chatRequestBodies();
    expect(body.conversation).toEqual({ conversationId: null });
    expect(body.commandId).toEqual(expect.any(String));
    const createCommandId = String(body.commandId);
    await waitFor(() =>
      expect(chatHistoryApi.recoverCreate).toHaveBeenCalledWith(
        "scope-a",
        createCommandId,
        expect.any(AbortSignal)
      )
    );
    expect(
      await screen.findByRole("button", { name: "Recover this create" })
    ).toBeTruthy();
  });

  it("hydrates a recovered create from its conversation version before continuing", async () => {
    const stream = createControlledSseResponse();
    const recoveredMeta = {
      ...serverConversation,
      id: "recovered-after-disconnect",
      title: "Recover a disconnected create",
    };
    const recoveredMessages = [
      {
        content: "Recover a disconnected create",
        id: "recovered-turn:user",
        role: "user" as const,
        status: "complete" as const,
        timestamp: 1784255700000,
        turnId: "recovered-turn",
      },
      {
        content: "Recovered after the stream disconnected.",
        id: "recovered-turn:assistant",
        role: "assistant" as const,
        status: "complete" as const,
        timestamp: 1784255700100,
        turnId: "recovered-turn",
      },
    ];
    (chatHistoryApi.listConversationMetas as jest.Mock)
      .mockResolvedValueOnce([])
      .mockResolvedValue([recoveredMeta]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue(
      conversationDetail(recoveredMessages, 1)
    );
    (chatHistoryApi.recoverCreate as jest.Mock).mockResolvedValue({
      conversationId: "recovered-after-disconnect",
      stateVersion: 4,
      status: "append_committed",
      turnId: "recovered-turn",
    });
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(stream.response)
      .mockResolvedValueOnce(
        createSseResponse([
          chatContextFrame(
            "recovered-after-disconnect",
            "continued-turn",
            "scope-a",
            1
          ),
          { runFinished: { result: { output: "Continued recovered chat." } } },
        ])
      );

    const view = renderWithQueryClient(<ChatPage />);
    await sendPrompt("Recover a disconnected create");
    await waitFor(() => expect(chatRequestBodies()).toHaveLength(1));
    act(() => stream.fail(new Error("Create stream disconnected")));

    await waitFor(() =>
      expect(chatHistoryApi.recoverCreate).toHaveBeenCalledWith(
        "scope-a",
        expect.any(String),
        expect.any(AbortSignal)
      )
    );
    await waitFor(() => expect(screen.getByRole("textbox")).toBeEnabled());
    await sendPrompt("Continue the recovered chat");
    await waitFor(() => expect(chatRequestBodies()).toHaveLength(2));
    expect(chatRequestBodies()[1]).toMatchObject({
      conversation: {
        conversationId: "recovered-after-disconnect",
        minimumStateVersion: 1,
      },
      prompt: "Continue the recovered chat",
    });
    view.unmount();
  });

  it("blocks reads and chat writes when the route scope differs from the authenticated scope", async () => {
    window.history.replaceState({}, "", "/chat?scopeId=scope-b");
    (studioApi.getAuthSession as jest.Mock).mockResolvedValueOnce({
      authenticated: true,
      enabled: true,
      scopeId: "scope-a",
      scopeSource: "nyxid",
    });

    renderWithQueryClient(<ChatPage />);

    expect(
      await screen.findByText(
        "Requested scope scope-b does not match authenticated scope scope-a. Open Chat from the active workspace or sign in again."
      )
    ).toBeTruthy();
    expect(screen.getByRole("textbox")).toBeDisabled();
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
    expect(chatHistoryApi.listConversationMetas).not.toHaveBeenCalled();
    expect(authFetch).not.toHaveBeenCalled();
  });

  it("disables chat creation but keeps history management available when authentication is disabled", async () => {
    (studioApi.getAuthSession as jest.Mock).mockResolvedValueOnce({
      authenticated: false,
      enabled: false,
      scopeId: "scope-a",
      scopeSource: "studio-host",
    });
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);

    renderWithQueryClient(<ChatPage />);

    expect(
      await screen.findByText(
        "Starting or continuing a chat requires a trusted authenticated scope. Existing chat history remains available to manage."
      )
    ).toBeTruthy();
    await waitFor(() =>
      expect(chatHistoryApi.listConversationMetas).toHaveBeenCalledWith("scope-a")
    );
    expect(screen.getByRole("textbox")).toBeDisabled();
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
    expect(authFetch).not.toHaveBeenCalled();

    fireEvent.click(
      await screen.findByRole("button", { name: "Delete Server conversation" })
    );
    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    await waitFor(() =>
      expect(chatHistoryApi.deleteConversation).toHaveBeenCalledWith(
        "scope-a",
        "conversation-a"
      )
    );
  });

  it("preserves open history roles, statuses, authors, and stopped errors", async () => {
    expect(
      hydrateStoredMessages([
      {
        authorId: "automation-1",
        authorName: "Release automation",
        content: "Queued for review",
        id: "turn-open:observer",
        role: "observer",
        status: "archived",
        timestamp: 1784255700000,
      },
      {
        content: "Review recorded",
        id: "turn-open:auditor",
        role: "auditor",
        status: "complete",
        timestamp: 1784255701000,
      },
      {
        content: "",
        error: "Workflow stopped before completion.",
        id: "turn-open:assistant",
        role: "assistant",
        status: "complete",
        timestamp: 1784255702000,
      },
      ])
    ).toEqual([
      expect.objectContaining({
        authorId: "automation-1",
        authorName: "Release automation",
        role: "observer",
        status: "archived",
      }),
      expect.objectContaining({
        role: "auditor",
        status: "complete",
      }),
      expect.objectContaining({
        error: "Workflow stopped before completion.",
        role: "assistant",
        status: "error",
      }),
    ]);
  });

  it("shows the author or role for non-assistant history messages", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue(
      conversationDetail([
        {
          authorName: "Release automation",
          content: "Queued for review",
          id: "turn-open:observer",
          role: "observer",
          status: "complete",
          timestamp: 1784255700000,
        },
        {
          content: "Review recorded",
          id: "turn-open:auditor",
          role: "auditor",
          status: "complete",
          timestamp: 1784255701000,
        },
      ])
    );

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );

    expect(await screen.findByText("Release automation")).toBeTruthy();
    expect(screen.getByText("auditor")).toBeTruthy();
    expect(screen.getByText("Queued for review")).toBeTruthy();
    expect(screen.getByText("Review recorded")).toBeTruthy();
  });

  it("derives a restored conversation status from the latest assistant turn", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue(
      conversationDetail([
        ...serverMessages,
        {
          content: "The first attempt failed.",
          error: "Dispatch failed.",
          id: "turn-b:assistant",
          role: "assistant",
          status: "error",
          timestamp: 1784255701000,
        },
        {
          content: "Try again",
          id: "turn-c:user",
          role: "user",
          status: "complete",
          timestamp: 1784255702000,
        },
        {
          content: "The retry succeeded.",
          id: "turn-c:assistant",
          role: "assistant",
          status: "complete",
          timestamp: 1784255703000,
        },
      ])
    );

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );

    expect(await screen.findByText("The retry succeeded.")).toBeTruthy();
    expect(screen.getByText("Completed")).toBeTruthy();
  });

  it("sends a raw new-chat prompt with an explicit create intent", async () => {
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        chatContextFrame(),
        {
          runFinished: {
            result: {
              output: "Request completed and saved by the server.",
            },
          },
        },
      ])
    );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Create a support team");

    expect(
      await screen.findByText("Request completed and saved by the server.")
    ).toBeTruthy();
    const [body] = chatRequestBodies();
    expect(body).toEqual({
      commandId: expect.any(String),
      conversation: { conversationId: null },
      prompt: "Create a support team",
      sessionId: expect.any(String),
      workflow: "studio",
    });
    expect(body).not.toHaveProperty("scopeId");
    expect(body).not.toHaveProperty("chatHistory");
    expect(window.localStorage.length).toBe(0);
    expect(screen.getByRole("button", { name: "Create a support team" })).toBeTruthy();
  });

  it("fails a completed stream that never establishes conversation context", async () => {
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        { runFinished: { result: { output: "Unbound response" } } },
      ])
    );

    const view = renderWithQueryClient(<ChatPage />);
    await screen.findByText("No chat history");

    jest.useFakeTimers();
    try {
      await sendPrompt("Start an unbound chat");
      await act(async () => {
        await jest.advanceTimersByTimeAsync(3_000);
      });

      expect(chatHistoryApi.recoverCreate).toHaveBeenCalledTimes(4);
      expect(
        await screen.findByText(
          "Chat completed without a conversation context."
        )
      ).toBeTruthy();
    } finally {
      view.unmount();
      jest.useRealTimers();
    }
  });

  it("keeps a create command id for the same prompt and replaces it for new input", async () => {
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        { runFinished: { result: { output: "Unbound response" } } },
      ])
    );
    (chatHistoryApi.recoverCreate as jest.Mock).mockRejectedValue(
      new Error("Recovery failed.")
    );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Original create request");
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Send" })).toBeInTheDocument()
    );

    await sendPrompt("Original create request");
    await waitFor(() => expect(chatRequestBodies()).toHaveLength(2));
    const [firstBody, retryBody] = chatRequestBodies();
    expect(retryBody.commandId).toBe(firstBody.commandId);

    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Send" })).toBeInTheDocument()
    );
    await sendPrompt("Changed create request");
    await waitFor(() => expect(chatRequestBodies()).toHaveLength(3));
    const changedBody = chatRequestBodies()[2];
    expect(changedBody.commandId).toEqual(expect.any(String));
    expect(changedBody.commandId).not.toBe(firstBody.commandId);
  });

  it("uses the reconciled Conversation watermark when create context reports another version domain", async () => {
    const firstProjectedConversation = {
      ...serverConversation,
      id: "server-conversation",
      messageCount: 1,
      title: "Create a fund analysis workflow",
    };
    const secondProjectedConversation = {
      ...firstProjectedConversation,
      messageCount: 2,
    };
    (chatHistoryApi.listConversationMetas as jest.Mock)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([firstProjectedConversation])
      .mockResolvedValue([secondProjectedConversation]);
    (chatHistoryApi.loadConversation as jest.Mock)
      .mockResolvedValueOnce(
        conversationDetail(
          [
            {
              content: "Choose a Team: team01 or team02.",
              id: "turn-1:assistant",
              role: "assistant",
              status: "complete",
              timestamp: 1784255700000,
              turnId: "turn-1",
            },
          ],
          8
        )
      )
      .mockResolvedValue(
        conversationDetail(
          [
            {
              content: "Continuing with team01.",
              id: "turn-2:assistant",
              role: "assistant",
              status: "complete",
              timestamp: 1784255701000,
              turnId: "turn-2",
            },
          ],
          9
        )
      );
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(
        createSseResponse([
          chatContextFrame("server-conversation", "turn-1", "scope-a", 40),
          {
            runFinished: {
              result: { output: "Choose a Team: team01 or team02." },
            },
          },
        ])
      )
      .mockResolvedValueOnce(
        createSseResponse([
          chatContextFrame("server-conversation", "turn-2", "scope-a", 8),
          { runFinished: { result: { output: "Continuing with team01." } } },
        ])
      );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Create a workflow that generates fund analysis reports");
    await screen.findByText("Choose a Team: team01 or team02.");
    await sendPrompt("team01");
    await screen.findByText("Continuing with team01.");

    const [firstBody, secondBody] = chatRequestBodies();
    expect(firstBody.commandId).toEqual(expect.any(String));
    expect(firstBody.conversation).toEqual({ conversationId: null });
    expect(secondBody.conversation).toEqual({
      conversationId: "server-conversation",
      minimumStateVersion: 8,
    });
    expect(secondBody).not.toHaveProperty("commandId");
    expect(secondBody.sessionId).toBe(firstBody.sessionId);
    expect(secondBody.prompt).toBe("team01");
    expect(String(secondBody.prompt)).not.toContain("<conversation_history>");
  });

  it("does not let a stale accepted context move the continuation watermark backwards", async () => {
    const continuedMeta = {
      ...serverConversation,
      messageCount: 4,
    };
    const continuedMessages = [
      ...serverMessages,
      {
        content: "First continuation",
        id: "turn-b:user",
        role: "user" as const,
        status: "complete" as const,
        timestamp: 1784255700500,
        turnId: "turn-b",
      },
      {
        content: "First continuation answer",
        id: "turn-b:assistant",
        role: "assistant" as const,
        status: "complete" as const,
        timestamp: 1784255700600,
        turnId: "turn-b",
      },
    ];
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      continuedMeta,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock)
      .mockResolvedValueOnce(conversationDetail(serverMessages, 8))
      .mockResolvedValueOnce(conversationDetail(continuedMessages, 7))
      .mockResolvedValue(conversationDetail(continuedMessages, 8));
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(
        createSseResponse([
          chatContextFrame("conversation-a", "turn-b", "scope-a", 7),
          {
            runFinished: { result: { output: "First continuation answer" } },
          },
        ])
      )
      .mockResolvedValueOnce(
        createSseResponse([
          chatContextFrame("conversation-a", "turn-c", "scope-a", 8),
          {
            runFinished: { result: { output: "Second continuation answer" } },
          },
        ])
      );

    const view = renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );
    await screen.findByText("The support workflow is ready.");
    await sendPrompt("First continuation");
    await screen.findByText("First continuation answer");
    await waitFor(() => expect(screen.getByRole("textbox")).toBeEnabled());

    await sendPrompt("Second continuation");
    await waitFor(() => expect(chatRequestBodies()).toHaveLength(2));
    expect(chatRequestBodies()[0].conversation).toEqual({
      conversationId: "conversation-a",
      minimumStateVersion: 8,
    });
    expect(chatRequestBodies()[1].conversation).toEqual({
      conversationId: "conversation-a",
      minimumStateVersion: 8,
    });
    view.unmount();
  });

  it("refreshes authoritative detail before retrying a 503 continuation", async () => {
    const projectedConversation = {
      ...serverConversation,
      messageCount: 6,
    };
    const interveningMessages = [
      {
        content: "Question from another tab",
        id: "turn-b:user",
        role: "user" as const,
        status: "complete" as const,
        timestamp: 1784255700500,
        turnId: "turn-b",
      },
      {
        content: "Answer from another tab",
        id: "turn-b:assistant",
        role: "assistant" as const,
        status: "complete" as const,
        timestamp: 1784255700600,
        turnId: "turn-b",
      },
    ];
    const projectedMessages = [
      ...serverMessages,
      ...interveningMessages,
      {
        content: "Continue after projection catches up",
        id: "turn-c:user",
        role: "user" as const,
        status: "complete" as const,
        timestamp: 1784255700900,
        turnId: "turn-c",
      },
      {
        content: "Follow-up answer",
        id: "turn-c:assistant",
        role: "assistant" as const,
        status: "complete" as const,
        timestamp: 1784255701000,
        turnId: "turn-c",
      },
    ];
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      projectedConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock)
      .mockResolvedValueOnce(conversationDetail(serverMessages, 7))
      .mockResolvedValueOnce(
        conversationDetail([...serverMessages, ...interveningMessages], 8)
      )
      .mockResolvedValue(conversationDetail(projectedMessages, 9));
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(historyReservationUnavailableResponse())
      .mockResolvedValueOnce(
        createSseResponse([
          chatContextFrame("conversation-a", "turn-c", "scope-a", 8),
          { runFinished: { result: { output: "Follow-up answer" } } },
        ])
      );

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );
    await screen.findByText("The support workflow is ready.");
    await sendPrompt("Continue after projection catches up");

    expect(await screen.findByText("Follow-up answer")).toBeTruthy();
    await waitFor(() => expect(chatRequestBodies()).toHaveLength(2));
    const [initialRequest, retryRequest] = chatRequestBodies();
    expect(initialRequest.conversation).toEqual({
      conversationId: "conversation-a",
      minimumStateVersion: 7,
    });
    expect(retryRequest.conversation).toEqual({
      conversationId: "conversation-a",
      minimumStateVersion: 8,
    });
    expect(initialRequest.prompt).toBe("Continue after projection catches up");
    expect(retryRequest.prompt).toBe("Continue after projection catches up");
    expect(await screen.findByText("Answer from another tab")).toBeTruthy();
    expect(screen.getByText("Question from another tab")).toBeTruthy();
    expect(chatHistoryApi.loadConversation).toHaveBeenNthCalledWith(
      2,
      "scope-a",
      "conversation-a",
      expect.any(AbortSignal)
    );
  });

  it("keeps refreshed authoritative messages when an accepted retry stream fails", async () => {
    const stream = createControlledSseResponse();
    const interveningMessages = [
      {
        content: "Question committed in another tab",
        id: "turn-b:user",
        role: "user" as const,
        status: "complete" as const,
        timestamp: 1784255700500,
        turnId: "turn-b",
      },
      {
        content: "Answer committed in another tab",
        id: "turn-b:assistant",
        role: "assistant" as const,
        status: "complete" as const,
        timestamp: 1784255700600,
        turnId: "turn-b",
      },
    ];
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock)
      .mockResolvedValueOnce(conversationDetail(serverMessages, 7))
      .mockResolvedValueOnce(
        conversationDetail([...serverMessages, ...interveningMessages], 8)
      )
      .mockRejectedValue(new Error("Projection temporarily unavailable"));
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(historyReservationUnavailableResponse())
      .mockResolvedValueOnce(stream.response);

    const view = renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );
    await screen.findByText("The support workflow is ready.");
    await sendPrompt("Continue after refreshing history");
    await waitFor(() => expect(chatRequestBodies()).toHaveLength(2));
    act(() => {
      stream.enqueue(chatContextFrame("conversation-a", "turn-c", "scope-a", 8));
      stream.enqueue({
        textMessageContent: {
          delta: "Partial continuation",
          messageId: "message-c",
        },
      });
    });
    await screen.findByText("Partial continuation");
    act(() => stream.fail(new Error("Continuation stream disconnected")));

    expect(
      await screen.findByText("Question committed in another tab")
    ).toBeTruthy();
    expect(screen.getByText("Answer committed in another tab")).toBeTruthy();
    expect(
      screen.getAllByText("Continuation stream disconnected")
    ).not.toHaveLength(0);
    view.unmount();
  });

  it("keeps a refreshed authoritative watermark after reservation retries are rejected", async () => {
    const interveningMessages = [
      {
        content: "Question accepted in another tab",
        id: "turn-b:user",
        role: "user" as const,
        status: "complete" as const,
        timestamp: 1784255700500,
        turnId: "turn-b",
      },
      {
        content: "Answer committed in another tab",
        id: "turn-b:assistant",
        role: "assistant" as const,
        status: "complete" as const,
        timestamp: 1784255700600,
        turnId: "turn-b",
      },
    ];
    const refreshedDetail = conversationDetail(
      [...serverMessages, ...interveningMessages],
      8
    );
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock)
      .mockResolvedValueOnce(conversationDetail(serverMessages, 7))
      .mockResolvedValueOnce(refreshedDetail)
      .mockResolvedValueOnce(conversationDetail(serverMessages, 7))
      .mockResolvedValue(
        conversationDetail(
          [
            ...refreshedDetail.messages,
            {
              content: "Accepted after the explicit rejection",
              id: "turn-d:assistant",
              role: "assistant",
              status: "complete",
              timestamp: 1784255701000,
              turnId: "turn-d",
            },
          ],
          9
        )
      );
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(historyReservationUnavailableResponse())
      .mockResolvedValueOnce(historyReservationUnavailableResponse())
      .mockResolvedValueOnce(
        createSseResponse([
          chatContextFrame("conversation-a", "turn-d", "scope-a", 8),
          {
            runFinished: {
              result: { output: "Accepted after the explicit rejection" },
            },
          },
        ])
      );

    const view = renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );
    await screen.findByText("The support workflow is ready.");

    jest.useFakeTimers();
    try {
      await sendPrompt("Continue from the stale tab");
      await act(async () => {
        await jest.advanceTimersByTimeAsync(0);
      });
      expect(chatRequestBodies()).toHaveLength(1);
      expect(chatHistoryApi.loadConversation).toHaveBeenCalledTimes(1);

      await act(async () => {
        await jest.advanceTimersByTimeAsync(299);
      });
      expect(chatRequestBodies()).toHaveLength(1);
      expect(chatHistoryApi.loadConversation).toHaveBeenCalledTimes(1);

      await act(async () => {
        await jest.advanceTimersByTimeAsync(1);
      });
      expect(chatRequestBodies()).toHaveLength(2);
      expect(chatHistoryApi.loadConversation).toHaveBeenCalledTimes(2);

      await act(async () => {
        await jest.advanceTimersByTimeAsync(899);
      });
      expect(chatRequestBodies()).toHaveLength(2);
      expect(chatHistoryApi.loadConversation).toHaveBeenCalledTimes(2);

      await act(async () => {
        await jest.advanceTimersByTimeAsync(1);
      });
      expect(chatRequestBodies()).toHaveLength(2);
      expect(chatHistoryApi.loadConversation).toHaveBeenCalledTimes(3);
      expect(
        await screen.findByText("Answer committed in another tab")
      ).toBeTruthy();
      expect(screen.getByText("Question accepted in another tab")).toBeTruthy();
      expect(screen.getByRole("textbox")).toBeEnabled();

      await sendPrompt("Continue after the explicit rejection");
      await act(async () => {
        await jest.advanceTimersByTimeAsync(0);
      });
      expect(chatRequestBodies()).toHaveLength(3);
      expect(chatRequestBodies()[2]).toMatchObject({
        conversation: {
          conversationId: "conversation-a",
          minimumStateVersion: 8,
        },
        prompt: "Continue after the explicit rejection",
      });
      expect(
        await screen.findByText("Accepted after the explicit rejection")
      ).toBeTruthy();
    } finally {
      view.unmount();
      jest.useRealTimers();
    }
  });

  it("does not lock a continuation when history refresh is definitively rejected", async () => {
    const { ChatHistoryApiError } = jest.requireMock("./chatHistoryApi");
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock)
      .mockResolvedValueOnce(conversationDetail(serverMessages, 7))
      .mockRejectedValueOnce(
        new ChatHistoryApiError("Conversation history is unavailable.", 404)
      );
    (authFetch as jest.Mock).mockResolvedValueOnce(
      historyReservationUnavailableResponse()
    );

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );
    await screen.findByText("The support workflow is ready.");
    await sendPrompt("Continue before history disappears");

    expect(
      await screen.findAllByText("Conversation history is unavailable.")
    ).not.toHaveLength(0);
    expect(screen.getByRole("textbox")).toBeEnabled();
    expect(
      screen.queryByText(
        "The continuation may have been accepted, but its turn identity was not received. Reload this page before continuing."
      )
    ).toBeNull();
    expect(screen.queryByRole("button", { name: "Retry" })).toBeNull();
  });

  it("does not lock a continuation when a rejected chat response body is unreadable", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue(
      conversationDetail(serverMessages, 7)
    );
    (authFetch as jest.Mock).mockResolvedValueOnce({
      ok: false,
      status: 409,
      statusText: "Conflict",
      text: jest.fn().mockRejectedValue(new Error("Response body disconnected")),
    } as unknown as Response);

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );
    await screen.findByText("The support workflow is ready.");
    await sendPrompt("Continue into a rejected response");

    expect(await screen.findAllByText("HTTP 409 Conflict")).not.toHaveLength(0);
    expect(screen.getByRole("textbox")).toBeEnabled();
    expect(
      screen.queryByText(
        "The continuation may have been accepted, but its turn identity was not received. Reload this page before continuing."
      )
    ).toBeNull();
    expect(screen.queryByRole("button", { name: "Retry" })).toBeNull();
  });

  it("does not lock a continuation stopped during reservation retry backoff", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue(
      conversationDetail(serverMessages, 7)
    );
    (authFetch as jest.Mock).mockResolvedValueOnce(
      historyReservationUnavailableResponse()
    );

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );
    await screen.findByText("The support workflow is ready.");
    await sendPrompt("Stop before retrying");
    await waitFor(() => expect(chatRequestBodies()).toHaveLength(1));
    fireEvent.click(screen.getByRole("button", { name: "Stop" }));

    expect(await screen.findAllByText("Chat stopped.")).not.toHaveLength(0);
    expect(screen.getByRole("textbox")).toBeEnabled();
    expect(
      screen.queryByText(
        "The continuation may have been accepted, but its turn identity was not received. Reload this page before continuing."
      )
    ).toBeNull();
    expect(screen.queryByRole("button", { name: "Retry" })).toBeNull();
  });

  it("keeps every chat action disabled until reconciliation observes a positive Conversation watermark", async () => {
    let resolveZeroDetail: (
      detail: ReturnType<typeof conversationDetail>
    ) => void = () => undefined;
    const zeroDetail = new Promise<ReturnType<typeof conversationDetail>>(
      (resolve) => {
        resolveZeroDetail = resolve;
      }
    );
    let resolvePositiveDetail: (
      detail: ReturnType<typeof conversationDetail>
    ) => void = () => undefined;
    const positiveDetail = new Promise<ReturnType<typeof conversationDetail>>(
      (resolve) => {
        resolvePositiveDetail = resolve;
      }
    );
    const projectedConversation = {
      ...serverConversation,
      id: "server-confirm",
      messageCount: 1,
      title: "Confirmation plan",
    };
    const projectedMessages = [
      {
        content: "Please confirm this plan.",
        id: "turn-confirm:assistant",
        role: "assistant" as const,
        status: "complete" as const,
        timestamp: 1784255700000,
        turnId: "turn-confirm",
      },
    ];
    (chatHistoryApi.listConversationMetas as jest.Mock)
      .mockResolvedValueOnce([])
      .mockResolvedValue([projectedConversation]);
    (chatHistoryApi.loadConversation as jest.Mock)
      .mockReturnValueOnce(zeroDetail)
      .mockReturnValueOnce(positiveDetail);
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(
        createSseResponse([
          chatContextFrame("server-confirm", "turn-confirm", "scope-a", 7),
          {
            runFinished: {
              result: { output: "Please confirm this plan." },
            },
          },
        ])
      )
      .mockResolvedValueOnce(
        createSseResponse([
          chatContextFrame("server-confirm", "turn-create", "scope-a", 8),
          { runFinished: { result: { output: "Created." } } },
        ])
      );

    const view = renderWithQueryClient(<ChatPage />);
    await screen.findByText("No chat history");

    jest.useFakeTimers();
    try {
      await sendPrompt("Draft a workflow plan");

      const confirmButton = await screen.findByRole("button", {
        name: "Confirm and create",
      });
      await waitFor(() =>
        expect(chatHistoryApi.loadConversation).toHaveBeenCalledTimes(1)
      );
      expect(confirmButton).toBeDisabled();
      expect(screen.getByRole("textbox")).toBeDisabled();

      await act(async () => {
        resolveZeroDetail(conversationDetail(projectedMessages, 0));
        await Promise.resolve();
        await jest.advanceTimersByTimeAsync(300);
      });
      await waitFor(() =>
        expect(chatHistoryApi.loadConversation).toHaveBeenCalledTimes(2)
      );
      expect(confirmButton).toBeDisabled();
      expect(screen.getByRole("textbox")).toBeDisabled();

      await act(async () => {
        resolvePositiveDetail(conversationDetail(projectedMessages, 6));
        await Promise.resolve();
        await jest.advanceTimersByTimeAsync(0);
      });
      await waitFor(() =>
        expect(
          screen.getByRole("button", { name: "Confirm and create" })
        ).toBeEnabled()
      );
      expect(chatHistoryApi.loadConversation).toHaveBeenCalledTimes(2);
      expect(screen.getByRole("textbox")).toBeEnabled();

      fireEvent.click(
        screen.getByRole("button", { name: "Confirm and create" })
      );
      await waitFor(() => expect(chatRequestBodies()).toHaveLength(2));
      expect(chatRequestBodies()[1]).toMatchObject({
        conversation: {
          conversationId: "server-confirm",
          minimumStateVersion: 6,
        },
        prompt: "Confirm. Please create it now.",
      });
    } finally {
      view.unmount();
      jest.useRealTimers();
    }
  });

  it("does not use create recovery for a continuation without context", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue(
      conversationDetail()
    );
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        { runFinished: { result: { output: "Unbound follow-up" } } },
      ])
    );

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );
    await screen.findByText("The support workflow is ready.");
    await sendPrompt("Continue without context");

    const missingContextMessage =
      "The continuation may have been accepted, but its turn identity was not received. Reload this page before continuing.";
    expect(await screen.findAllByText(missingContextMessage)).not.toHaveLength(0);
    expect(screen.getByRole("textbox")).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Retry" })).toBeNull();
    expect(
      screen.queryByRole("button", { name: /Retry saving/ })
    ).toBeNull();
    expect(chatHistoryApi.listConversationMetas).toHaveBeenCalledTimes(1);
    expect(chatHistoryApi.loadConversation).toHaveBeenCalledTimes(1);
    expect(chatHistoryApi.recoverCreate).not.toHaveBeenCalled();
  });

  it("rejects a later Chat History context from a different scope", async () => {
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        chatContextFrame("server-conversation", "turn-1"),
        chatContextFrame("server-conversation", "turn-1", "scope-b"),
      ])
    );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Keep the accepted scope");

    expect(
      await screen.findByText(
        "Chat History context does not match the active scope."
      )
    ).toBeTruthy();
  });

  it("rejects a later Chat History context with a different conversation", async () => {
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        chatContextFrame("server-conversation", "turn-1"),
        chatContextFrame("other-conversation", "turn-1"),
      ])
    );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Keep the first conversation identity");

    expect(
      await screen.findByText("Chat History context changed during the stream.")
    ).toBeTruthy();
  });

  it("keeps context and visible streaming text before projection is readable", async () => {
    const stream = createControlledSseResponse();
    (authFetch as jest.Mock).mockResolvedValue(stream.response);

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Stream a draft plan");

    act(() => {
      stream.enqueue(chatContextFrame("server-stream", "turn-stream"));
      stream.enqueue({
        textMessageContent: {
          delta: "Draft plan in progress",
          messageId: "message-a",
        },
      });
    });

    expect(await screen.findByText("Draft plan in progress")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Stream a draft plan" })).toBeTruthy();
    expect(screen.getAllByText("Streaming")).toHaveLength(2);
    expect(window.localStorage.length).toBe(0);

    act(() => stream.close());
  });

  it("keeps one live conversation until delayed projection observes its turn", async () => {
    const projectedConversation = {
      ...serverConversation,
      id: "server-projected",
      messageCount: 1,
      title: "Projected conversation",
    };
    (chatHistoryApi.listConversationMetas as jest.Mock)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([projectedConversation]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue(
      conversationDetail([
        {
          content: "Live response",
          id: "turn-projected:assistant",
          role: "assistant",
          status: "complete",
          timestamp: 1784255700000,
          turnId: "turn-projected",
        },
      ])
    );
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        chatContextFrame("server-projected", "turn-projected"),
        { runFinished: { result: { output: "Live response" } } },
      ])
    );

    const view = renderWithQueryClient(<ChatPage />);
    await screen.findByText("No chat history");

    jest.useFakeTimers();
    try {
      await sendPrompt("Projection-safe prompt");
      expect(await screen.findByText("Live response")).toBeTruthy();

      await waitFor(() =>
        expect(chatHistoryApi.listConversationMetas).toHaveBeenCalledTimes(2)
      );
      expect(
        screen.getAllByRole("button", { name: "Projection-safe prompt" })
      ).toHaveLength(1);
      expect(screen.getByText("Live response")).toBeTruthy();
      fireEvent.click(screen.getByRole("button", { name: "New Chat" }));
      expect(
        screen.getAllByRole("button", { name: "Projection-safe prompt" })
      ).toHaveLength(1);
      fireEvent.click(
        screen.getByRole("button", { name: "Projection-safe prompt" })
      );
      expect(screen.getByText("Live response")).toBeTruthy();

      await act(async () => {
        await jest.advanceTimersByTimeAsync(300);
      });
      expect(chatHistoryApi.listConversationMetas).toHaveBeenCalledTimes(3);
      expect(
        screen.getAllByRole("button", { name: "Projection-safe prompt" })
      ).toHaveLength(1);

      await act(async () => {
        await jest.advanceTimersByTimeAsync(900);
      });
      await waitFor(() => {
        expect(chatHistoryApi.listConversationMetas).toHaveBeenCalledTimes(4);
        expect(
          screen.getAllByRole("button", { name: "Projected conversation" })
        ).toHaveLength(1);
      });
      expect(
        screen.queryByRole("button", { name: "Projection-safe prompt" })
      ).toBeNull();
      expect(screen.getByText("Live response")).toBeTruthy();
      expect(chatHistoryApi.loadConversation).toHaveBeenCalledWith(
        "scope-a",
        "server-projected",
        expect.any(AbortSignal)
      );
    } finally {
      view.unmount();
      jest.useRealTimers();
    }
  });

  it("uses typed turn identity only when metadata cannot prove observation", async () => {
    const projectedConversation = {
      ...serverConversation,
      id: "server-typed-turn",
      messageCount: 0,
      title: "Typed turn conversation",
    };
    (chatHistoryApi.listConversationMetas as jest.Mock)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([projectedConversation]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValueOnce(
      conversationDetail([
        {
          content: "Projected response",
          id: "opaque-message-id",
          role: "assistant",
          status: "complete",
          timestamp: 1784255700000,
          turnId: "turn-typed",
        },
      ])
    );
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        chatContextFrame("server-typed-turn", "turn-typed"),
        { runFinished: { result: { output: "Typed turn response" } } },
      ])
    );

    renderWithQueryClient(<ChatPage />);
    await screen.findByText("No chat history");
    await sendPrompt("Use typed turn identity");

    expect(await screen.findByText("Projected response")).toBeTruthy();
    await waitFor(() =>
      expect(chatHistoryApi.loadConversation).toHaveBeenCalledWith(
        "scope-a",
        "server-typed-turn",
        expect.any(AbortSignal)
      )
    );
    expect(
      await screen.findByRole("button", { name: "Typed turn conversation" })
    ).toBeTruthy();
    expect(screen.queryByText("History save was not confirmed")).toBeNull();
  });

  it("keeps a stopped server-owned turn visible while projection catches up", async () => {
    const stream = createControlledSseResponse();
    const projectedConversation = {
      ...serverConversation,
      id: "server-stopped",
      messageCount: 1,
      title: "Stopped conversation",
    };
    (authFetch as jest.Mock).mockResolvedValue(stream.response);
    (chatHistoryApi.listConversationMetas as jest.Mock)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([projectedConversation]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue(
      conversationDetail([
        {
          content: "Chat stopped.",
          id: "turn-stopped:assistant",
          role: "assistant",
          status: "error",
          timestamp: 1784255700000,
          turnId: "turn-stopped",
        },
      ])
    );

    renderWithQueryClient(<ChatPage />);
    await screen.findByText("No chat history");
    await sendPrompt("Stop after context");
    act(() => stream.enqueue(chatContextFrame("server-stopped", "turn-stopped")));
    await screen.findByRole("button", { name: "Stop after context" });
    fireEvent.click(screen.getByRole("button", { name: "Stop" }));
    act(() => stream.close());

    expect(await screen.findByText("Chat stopped.")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "New Chat" }));
    expect(
      screen.getByRole("button", { name: "Stop after context" })
    ).toBeTruthy();
    expect(
      await screen.findByRole(
        "button",
        { name: "Stopped conversation" },
        { timeout: 1_500 }
      )
    ).toBeTruthy();
    expect(chatHistoryApi.loadConversation).toHaveBeenCalledWith(
      "scope-a",
      "server-stopped",
      expect.any(AbortSignal)
    );
  });

  it("aborts reconciliation index and detail reads when the page unmounts", async () => {
    const projectedConversation = {
      ...serverConversation,
      id: "server-aborted-reconciliation",
      messageCount: 1,
    };
    let reconciliationListSignal: AbortSignal | undefined;
    let reconciliationDetailSignal: AbortSignal | undefined;
    (chatHistoryApi.listConversationMetas as jest.Mock).mockImplementation(
      async (_scopeId: string, signal?: AbortSignal) => {
        if (!signal) {
          return [];
        }

        reconciliationListSignal = signal;
        return [projectedConversation];
      }
    );
    (chatHistoryApi.loadConversation as jest.Mock).mockImplementation(
      (
        _scopeId: string,
        _conversationId: string,
        signal?: AbortSignal
      ) => {
        reconciliationDetailSignal = signal;
        return new Promise(() => undefined);
      }
    );
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        chatContextFrame(
          "server-aborted-reconciliation",
          "turn-aborted-reconciliation"
        ),
        { runFinished: { result: { output: "Awaiting projection" } } },
      ])
    );

    const view = renderWithQueryClient(<ChatPage />);
    await screen.findByText("No chat history");
    await sendPrompt("Abort reconciliation reads");
    await waitFor(() =>
      expect(chatHistoryApi.loadConversation).toHaveBeenCalledWith(
        "scope-a",
        "server-aborted-reconciliation",
        expect.any(AbortSignal)
      )
    );

    view.unmount();

    expect(reconciliationListSignal?.aborted).toBe(true);
    expect(reconciliationDetailSignal?.aborted).toBe(true);
  });

  it("discards an old-scope stream before it can recreate pending history", async () => {
    const stream = createControlledSseResponse();
    (authFetch as jest.Mock).mockResolvedValue(stream.response);
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([]);

    const view = renderScopeSwitchableChat("scope-a");
    await screen.findByText("No chat history");
    await sendPrompt("Scope isolated prompt");
    act(() => stream.enqueue(chatContextFrame("server-old-scope", "turn-old")));
    await screen.findByRole("button", { name: "Scope isolated prompt" });

    view.switchScope("scope-b");
    expect(
      await screen.findByText(
        "Requested scope scope-b does not match authenticated scope scope-a. Open Chat from the active workspace or sign in again."
      )
    ).toBeTruthy();
    expect(chatHistoryApi.listConversationMetas).not.toHaveBeenCalledWith(
      "scope-b"
    );

    const streamRequest = (authFetch as jest.Mock).mock.calls.find(
      ([path]) => path === "/api/chat"
    )?.[1] as RequestInit | undefined;
    expect(streamRequest?.signal?.aborted).toBe(true);

    await act(async () => {
      stream.enqueue({
        runFinished: { result: { output: "Late old-scope response" } },
      });
      stream.close();
    });

    expect(
      screen.queryByRole("button", { name: "Scope isolated prompt" })
    ).toBeNull();
    expect(screen.queryByText("Late old-scope response")).toBeNull();
    expect(chatHistoryApi.loadConversation).not.toHaveBeenCalledWith(
      "scope-a",
      "server-old-scope"
    );
  });

  it("shows exhausted reconciliation and retries until metadata observes the turn", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([]);
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        chatContextFrame("server-late", "turn-late"),
        { runFinished: { result: { output: "Optimistic response" } } },
      ])
    );

    const view = renderWithQueryClient(<ChatPage />);
    await screen.findByText("No chat history");

    jest.useFakeTimers();
    try {
      await sendPrompt("Late projection prompt");
      expect(await screen.findByText("Optimistic response")).toBeTruthy();
      await act(async () => {
        await jest.advanceTimersByTimeAsync(3_000);
      });

      expect(chatHistoryApi.listConversationMetas).toHaveBeenCalledTimes(5);
      expect(chatHistoryApi.loadConversation).not.toHaveBeenCalled();
      expect(
        await screen.findByText("History save was not confirmed")
      ).toBeTruthy();
      expect(
        screen.getByText("History save was not observed by the server.")
      ).toBeTruthy();

      (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValueOnce([
        {
          ...serverConversation,
          id: "server-late",
          messageCount: 1,
          title: "Late authoritative conversation",
        },
      ]);
      (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValueOnce(
        conversationDetail(
          [
            {
              content: "Optimistic response",
              id: "turn-late:assistant",
              role: "assistant",
              status: "complete",
              timestamp: 1784255700000,
              turnId: "turn-late",
            },
          ],
          8
        )
      );
      fireEvent.click(
        screen.getByRole("button", {
          name: "Retry saving Late projection prompt",
        })
      );
      expect(
        await screen.findByRole("button", {
          name: "Late authoritative conversation",
        })
      ).toBeTruthy();
      expect(screen.queryByText("History save was not confirmed")).toBeNull();
      expect(chatHistoryApi.loadConversation).toHaveBeenCalledWith(
        "scope-a",
        "server-late",
        expect.any(AbortSignal)
      );
    } finally {
      view.unmount();
      jest.useRealTimers();
    }
  });

  it("shows list failure separately and retries without disabling chat", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock)
      .mockRejectedValueOnce(new Error("History service is offline"))
      .mockResolvedValueOnce([]);

    renderWithQueryClient(<ChatPage />);

    expect(await screen.findByText("Chat history could not be loaded")).toBeTruthy();
    expect(screen.getByText("History service is offline")).toBeTruthy();
    expect(screen.getByRole("textbox")).toBeEnabled();

    fireEvent.click(
      screen.getByRole("button", { name: "Retry chat history" })
    );
    expect(await screen.findByText("No chat history")).toBeTruthy();
    expect(chatHistoryApi.listConversationMetas).toHaveBeenCalledTimes(2);
  });

  it("keeps the selected conversation stable while retrying a detail failure", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock)
      .mockRejectedValueOnce(new Error("Detail is not available"))
      .mockResolvedValueOnce(conversationDetail());

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );

    expect(await screen.findByText("Conversation could not be loaded")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(await screen.findByText("The support workflow is ready.")).toBeTruthy();
    expect(chatHistoryApi.loadConversation).toHaveBeenCalledTimes(2);
  });

  it("prevents sending while a selected conversation is still loading", async () => {
    let resolveDetail: (detail: ReturnType<typeof conversationDetail>) => void =
      () => undefined;
    const detailPromise = new Promise<ReturnType<typeof conversationDetail>>(
      (resolve) => {
        resolveDetail = resolve;
      }
    );
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockReturnValue(detailPromise);

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );

    await waitFor(() => expect(screen.getByRole("textbox")).toBeDisabled());
    act(() => resolveDetail(conversationDetail()));
    expect(
      await screen.findByText("The support workflow is ready.")
    ).toBeTruthy();
    expect(screen.getByRole("textbox")).toBeEnabled();
  });

  it("ignores an older detail response after the user selects another conversation", async () => {
    let resolveFirst:
      | ((value: ReturnType<typeof conversationDetail>) => void)
      | undefined;
    const firstDetail = new Promise<ReturnType<typeof conversationDetail>>(
      (resolve) => {
        resolveFirst = resolve;
      }
    );
    const secondConversation = {
      ...serverConversation,
      id: "conversation-b",
      title: "Second conversation",
    };
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
      secondConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockImplementation(
      async (_scopeId: string, conversationId: string) =>
        conversationId === "conversation-a"
          ? firstDetail
          : conversationDetail([
              {
                ...serverMessages[1],
                content: "Second conversation answer",
                id: "turn-b:assistant",
              },
            ])
    );

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );
    fireEvent.click(
      await screen.findByRole("button", { name: "Second conversation" })
    );
    expect(await screen.findByText("Second conversation answer")).toBeTruthy();

    await act(async () => {
      resolveFirst?.(conversationDetail());
      await firstDetail;
    });
    expect(screen.queryByText("The support workflow is ready.")).toBeNull();
    expect(screen.getByText("Second conversation answer")).toBeTruthy();
  });

  it("restores a server conversation and sends only the new follow-up prompt", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue(
      conversationDetail()
    );
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        chatContextFrame("conversation-a", "turn-b"),
        { runFinished: { result: { output: "Follow-up answer" } } },
      ])
    );

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );
    await screen.findByText("The support workflow is ready.");
    await sendPrompt("Add retry handling");
    await screen.findByText("Follow-up answer");

    const [body] = chatRequestBodies();
    expect(body).toMatchObject({
      conversation: {
        conversationId: "conversation-a",
        minimumStateVersion: 7,
      },
      prompt: "Add retry handling",
    });
    expect(body).not.toHaveProperty("commandId");
    expect(String(body.prompt)).not.toContain("Create a support workflow");
  });

  it("requires confirmation before deleting and removes only after success", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);

    renderWithQueryClient(<ChatPage />);
    await screen.findByRole("button", { name: "Server conversation" });

    fireEvent.click(
      screen.getByRole("button", { name: "Delete Server conversation" })
    );
    expect(screen.getByText("Delete conversation?")).toBeTruthy();
    expect(chatHistoryApi.deleteConversation).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(chatHistoryApi.deleteConversation).not.toHaveBeenCalled();

    fireEvent.click(
      screen.getByRole("button", { name: "Delete Server conversation" })
    );
    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    await waitFor(() =>
      expect(chatHistoryApi.deleteConversation).toHaveBeenCalledWith(
        "scope-a",
        "conversation-a"
      )
    );
    expect(await screen.findByText("No chat history")).toBeTruthy();
  });

  it("does not apply a delayed delete result to a new scope", async () => {
    let resolveDelete = (): void => undefined;
    const deletePromise = new Promise<void>((resolve) => {
      resolveDelete = resolve;
    });
    const sharedConversation = {
      ...serverConversation,
      id: "shared-conversation",
      title: "Shared conversation",
    };
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      sharedConversation,
    ]);
    (chatHistoryApi.deleteConversation as jest.Mock).mockReturnValue(deletePromise);

    const view = renderScopeSwitchableChat("scope-a");
    await screen.findByRole("button", { name: "Shared conversation" });
    fireEvent.click(
      screen.getByRole("button", { name: "Delete Shared conversation" })
    );
    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    await waitFor(() =>
      expect(chatHistoryApi.deleteConversation).toHaveBeenCalledWith(
        "scope-a",
        "shared-conversation"
      )
    );

    view.switchScope("scope-b");
    expect(
      await screen.findByText(
        "Requested scope scope-b does not match authenticated scope scope-a. Open Chat from the active workspace or sign in again."
      )
    ).toBeTruthy();
    expect(chatHistoryApi.listConversationMetas).not.toHaveBeenCalledWith(
      "scope-b"
    );
    await act(async () => resolveDelete());
    view.switchScope("scope-a");

    expect(
      await screen.findByRole("button", { name: "Shared conversation" })
    ).toBeTruthy();
  });

  it("reports deletion failures with a toast and keeps history visible", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.deleteConversation as jest.Mock).mockRejectedValue(
      new Error("Delete request failed")
    );

    renderWithQueryClient(<ChatPage />);
    await screen.findByRole("button", { name: "Server conversation" });
    fireEvent.click(
      screen.getByRole("button", { name: "Delete Server conversation" })
    );
    fireEvent.click(screen.getByRole("button", { name: "Delete" }));

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Conversation could not be deleted",
      ),
    );
    expect(screen.queryByText("Conversation could not be deleted")).toBeNull();
    expect(screen.queryByText("Delete request failed")).toBeNull();
    expect(screen.getByRole("button", { name: "Server conversation" })).toBeTruthy();
  });

  it("opens Workflow Studio only for structured target identifiers", async () => {
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        chatContextFrame(),
        {
          runFinished: {
            result: {
              memberId: "member-a",
              output: "Created.",
              scopeId: "scope-a",
              teamId: "team-a",
              workflowId: "workflow-a",
            },
          },
        },
      ])
    );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Directly create the workflow");
    fireEvent.click(
      await screen.findByRole("button", { name: "Open Workflow Studio" })
    );

    expect(history.push).toHaveBeenCalledWith(
      "/scopes/scope-a/teams/team-a/members/member-a/workflow?workflowId=workflow-a"
    );
  });
});
