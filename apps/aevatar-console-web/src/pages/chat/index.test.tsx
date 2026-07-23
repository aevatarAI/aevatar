import { act, fireEvent, screen, waitFor } from "@testing-library/react";
import * as React from "react";
import { authFetch } from "@/shared/auth/fetch";
import { history } from "@/shared/navigation/history";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import { chatHistoryApi } from "./chatHistoryApi";
import ChatPage, { hydrateStoredMessages } from "./index";

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

function chatContextFrame(
  conversationId = "conversation-a",
  turnId = "turn-a",
  scopeId = "scope-a"
): unknown {
  return {
    custom: {
      name: "aevatar.chat.context",
      payload: {
        "@type": CHAT_HISTORY_CONTEXT_TYPE,
        conversationId,
        scopeId,
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

function createControlledSseResponse(): {
  close: () => void;
  enqueue: (frame: unknown) => void;
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
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue([]);
    (chatHistoryApi.deleteConversation as jest.Mock).mockResolvedValue(undefined);
    const { ChatHistoryApiError } = jest.requireMock("./chatHistoryApi");
    (chatHistoryApi.recoverCreate as jest.Mock).mockRejectedValue(
      new ChatHistoryApiError("Recovery is not materialized.", 404)
    );
  });

  it("loads server history and restores its detail without local-only controls", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue(serverMessages);

    renderWithQueryClient(<ChatPage />);

    expect(document.querySelector(".aevatar-chat-main-header")).toBeTruthy();

    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );

    expect(await screen.findByText("The support workflow is ready.")).toBeTruthy();
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
    expect(body.conversation).toEqual({});
    expect(body.commandId).toEqual(expect.any(String));
    const createCommandId = String(body.commandId);
    await waitFor(() =>
      expect(chatHistoryApi.recoverCreate).toHaveBeenCalledWith(
        "scope-a",
        createCommandId
      )
    );
    expect(
      await screen.findByRole("button", { name: "Recover this create" })
    ).toBeTruthy();
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
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue([
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
    ]);

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
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue([
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
    ]);

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
      conversation: {},
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

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Start an unbound chat");

    expect(
      await screen.findByText(
        "Chat completed without a conversation context.",
        {},
        { timeout: 5_000 }
      )
    ).toBeTruthy();
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
      expect(screen.getByRole("button", { name: "Send" })).toBeInTheDocument(),
      { timeout: 5_000 }
    );
    await sendPrompt("Changed create request");
    await waitFor(() => expect(chatRequestBodies()).toHaveLength(3));
    const changedBody = chatRequestBodies()[2];
    expect(changedBody.commandId).toEqual(expect.any(String));
    expect(changedBody.commandId).not.toBe(firstBody.commandId);
  });

  it("binds the server conversation id and reuses the independent session id", async () => {
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(
        createSseResponse([
          chatContextFrame("server-conversation", "turn-1"),
          { runFinished: { result: { output: "First answer" } } },
        ])
      )
      .mockResolvedValueOnce(
        createSseResponse([
          chatContextFrame("server-conversation", "turn-2"),
          { runFinished: { result: { output: "Second answer" } } },
        ])
      );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("First prompt");
    await screen.findByText("First answer");
    await sendPrompt("Second prompt");
    await screen.findByText("Second answer");

    const [firstBody, secondBody] = chatRequestBodies();
    expect(firstBody.commandId).toEqual(expect.any(String));
    expect(firstBody.conversation).toEqual({});
    expect(secondBody.conversation).toEqual({
      conversationId: "server-conversation",
    });
    expect(secondBody).not.toHaveProperty("commandId");
    expect(secondBody.sessionId).toBe(firstBody.sessionId);
    expect(secondBody.prompt).toBe("Second prompt");
    expect(String(secondBody.prompt)).not.toContain("<conversation_history>");
  });

  it("does not use create recovery for a continuation without context", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue(serverMessages);
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

    expect(
      await screen.findByText("Chat completed without a conversation context.")
    ).toBeTruthy();
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
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        chatContextFrame("server-projected", "turn-projected"),
        { runFinished: { result: { output: "Live response" } } },
      ])
    );

    renderWithQueryClient(<ChatPage />);
    await screen.findByText("No chat history");
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

    await waitFor(
      () => {
        expect(chatHistoryApi.listConversationMetas).toHaveBeenCalledTimes(4);
        expect(
          screen.getAllByRole("button", { name: "Projected conversation" })
        ).toHaveLength(1);
      },
      { timeout: 2_500 }
    );
    expect(screen.queryByRole("button", { name: "Projection-safe prompt" })).toBeNull();
    expect(screen.getByText("Live response")).toBeTruthy();
    expect(chatHistoryApi.loadConversation).not.toHaveBeenCalled();
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
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValueOnce([
      {
        content: "Projected response",
        id: "opaque-message-id",
        role: "assistant",
        status: "complete",
        timestamp: 1784255700000,
        turnId: "turn-typed",
      },
    ]);
    (authFetch as jest.Mock).mockResolvedValue(
      createSseResponse([
        chatContextFrame("server-typed-turn", "turn-typed"),
        { runFinished: { result: { output: "Typed turn response" } } },
      ])
    );

    renderWithQueryClient(<ChatPage />);
    await screen.findByText("No chat history");
    await sendPrompt("Use typed turn identity");

    expect(await screen.findByText("Typed turn response")).toBeTruthy();
    await waitFor(() =>
      expect(chatHistoryApi.loadConversation).toHaveBeenCalledWith(
        "scope-a",
        "server-typed-turn"
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
    expect(chatHistoryApi.loadConversation).not.toHaveBeenCalled();
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
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 20));
    });

    expect(
      screen.queryByRole("button", { name: "Scope isolated prompt" })
    ).toBeNull();
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

    renderWithQueryClient(<ChatPage />);
    await screen.findByText("No chat history");
    await sendPrompt("Late projection prompt");
    expect(await screen.findByText("Optimistic response")).toBeTruthy();
    await waitFor(
      () => expect(chatHistoryApi.listConversationMetas).toHaveBeenCalledTimes(5),
      { timeout: 4_500 }
    );
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
    expect(chatHistoryApi.loadConversation).not.toHaveBeenCalled();
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
      .mockResolvedValueOnce(serverMessages);

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
    let resolveDetail: (messages: typeof serverMessages) => void = () => undefined;
    const detailPromise = new Promise<typeof serverMessages>((resolve) => {
      resolveDetail = resolve;
    });
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockReturnValue(detailPromise);

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole("button", { name: "Server conversation" })
    );

    await waitFor(() => expect(screen.getByRole("textbox")).toBeDisabled());
    act(() => resolveDetail(serverMessages));
    expect(await screen.findByText("The support workflow is ready.")).toBeTruthy();
    expect(screen.getByRole("textbox")).toBeEnabled();
  });

  it("ignores an older detail response after the user selects another conversation", async () => {
    let resolveFirst: ((value: typeof serverMessages) => void) | undefined;
    const firstDetail = new Promise<typeof serverMessages>((resolve) => {
      resolveFirst = resolve;
    });
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
          : [
              {
                ...serverMessages[1],
                content: "Second conversation answer",
                id: "turn-b:assistant",
              },
            ]
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
      resolveFirst?.(serverMessages);
      await firstDetail;
    });
    expect(screen.queryByText("The support workflow is ready.")).toBeNull();
    expect(screen.getByText("Second conversation answer")).toBeTruthy();
  });

  it("restores a server conversation and sends only the new follow-up prompt", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue(serverMessages);
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
      conversation: { conversationId: "conversation-a" },
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

  it("keeps history visible when deletion fails", async () => {
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

    expect(await screen.findByText("Conversation could not be deleted")).toBeTruthy();
    expect(screen.getByText("Delete request failed")).toBeTruthy();
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
