import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { AGUIEventType, CustomEventName } from "@aevatar-react-sdk/types";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import ChatPage from "./index";

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    bindScopeGAgent: jest.fn(async () => ({
      displayName: "NyxID Chat",
      revisionId: "rev-nyx",
      scopeId: "scope-a",
      serviceId: "nyxid-chat",
      targetKind: "gagent",
      targetName: "NyxID Chat",
    })),
    getAuthSession: jest.fn(async () => ({
      enabled: true,
      scopeId: "scope-a",
      scopeSource: "nyxid",
    })),
    getScopeBinding: jest.fn(async () => ({
      available: true,
      scopeId: "scope-a",
      serviceId: "support-service",
    })),
  },
}));

jest.mock("@/shared/api/servicesApi", () => ({
  servicesApi: {
    listServices: jest.fn(async () => [
      {
        activeServingRevisionId: "rev-1",
        appId: "default",
        defaultServingRevisionId: "rev-1",
        deploymentId: "deploy-1",
        deploymentStatus: "Active",
        displayName: "Support service",
        endpoints: [
          {
            description: "Streaming support chat",
            displayName: "Chat",
            endpointId: "chat",
            kind: "chat",
            requestTypeUrl: "type.googleapis.com/google.protobuf.StringValue",
            responseTypeUrl: "type.googleapis.com/google.protobuf.StringValue",
          },
          {
            description: "Ask for structured help",
            displayName: "Assist",
            endpointId: "assist",
            kind: "command",
            requestTypeUrl: "type.googleapis.com/google.protobuf.StringValue",
            responseTypeUrl: "type.googleapis.com/google.protobuf.StringValue",
          },
        ],
        namespace: "default",
        policyIds: [],
        primaryActorId: "actor://support",
        serviceId: "support-service",
        serviceKey: "scope-a:default:default:support-service",
        tenantId: "scope-a",
        updatedAt: "2026-04-01T09:00:00Z",
      },
    ]),
  },
}));

jest.mock("@/shared/api/runtimeRunsApi", () => ({
  runtimeRunsApi: {
    streamChat: jest.fn(async () => ({
      body: {},
      ok: true,
      streamKind: "chat",
    })),
    streamEndpoint: jest.fn(async () => ({
      body: {},
      ok: true,
      streamKind: "execute",
    })),
  },
}));

jest.mock("@/shared/agui/sseFrameNormalizer", () => ({
  parseBackendSSEStream: jest.fn(),
}));

jest.mock("./chatHistoryApi", () => ({
  chatHistoryApi: {
    deleteConversation: jest.fn(async () => undefined),
    listConversationMetas: jest.fn(async () => []),
    loadConversation: jest.fn(async () => []),
    saveConversation: jest.fn(async () => undefined),
  },
}));

import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import { studioApi } from "@/shared/studio/api";
import { chatHistoryApi } from "./chatHistoryApi";

function createChatEventStream() {
  return (async function* () {
    yield {
      runId: "run-1",
      threadId: "thread-1",
      timestamp: 1,
      type: AGUIEventType.RUN_STARTED,
    };
    yield {
      name: CustomEventName.RunContext,
      timestamp: 2,
      type: AGUIEventType.CUSTOM,
      value: {
        actorId: "actor://support",
        commandId: "cmd-1",
      },
    };
    yield {
      name: "aevatar.llm.reasoning",
      timestamp: 3,
      type: AGUIEventType.CUSTOM,
      value: {
        delta: "Inspecting the request",
      },
    };
    yield {
      stepName: "lookup_context",
      timestamp: 4,
      type: AGUIEventType.STEP_STARTED,
    };
    yield {
      timestamp: 5,
      toolCallId: "tool-1",
      toolName: "knowledge.search",
      type: AGUIEventType.TOOL_CALL_START,
    };
    yield {
      result: "3 matches",
      timestamp: 6,
      toolCallId: "tool-1",
      toolName: "knowledge.search",
      type: AGUIEventType.TOOL_CALL_END,
    };
    yield {
      delta: "Hello from the migrated chat.",
      messageId: "msg-1",
      timestamp: 7,
      type: AGUIEventType.TEXT_MESSAGE_CONTENT,
    };
    yield {
      stepName: "lookup_context",
      timestamp: 8,
      type: AGUIEventType.STEP_FINISHED,
    };
  })();
}

function createExecuteEventStream() {
  return (async function* () {
    yield {
      runId: "run-execute",
      threadId: "thread-execute",
      timestamp: 1,
      type: AGUIEventType.RUN_STARTED,
    };
    yield {
      delta: "Executed through endpoint stream.",
      messageId: "msg-execute",
      timestamp: 2,
      type: AGUIEventType.TEXT_MESSAGE_CONTENT,
    };
  })();
}

describe("ChatPage", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    window.history.pushState({}, "", "/chat");
    window.scrollTo = jest.fn();
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue([]);
    (parseBackendSSEStream as jest.Mock).mockImplementation((response: { streamKind?: string }) =>
      response?.streamKind === "execute" ? createExecuteEventStream() : createChatEventStream()
    );
  });

  it("renders chat as a scoped page and streams the assistant output", async () => {
    const { container } = renderWithQueryClient(React.createElement(ChatPage));

    expect(container.textContent).toContain("Console");
    expect(container.textContent).toContain("History");
    expect(container.textContent).toContain("New Chat");
    expect(container.textContent).toContain("Chat");
    expect(container.textContent).toContain("Debug");

    const serviceSelector = await screen.findByLabelText("Chat service");
    await waitFor(() => {
      expect(serviceSelector.textContent).toContain("Support service");
    });
    expect(screen.getByAltText("NyxID")).toBeTruthy();

    const promptInput = await screen.findByPlaceholderText(
      "Describe the task, ask a question, or paste the next operator instruction."
    );
    fireEvent.change(promptInput, {
      target: { value: "Help me draft the next response." },
    });
    const sendButton = await screen.findByLabelText("Send");
    fireEvent.click(sendButton);

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          prompt: "Help me draft the next response.",
        }),
        expect.any(AbortSignal),
        {
          serviceId: "support-service",
        }
      );
    });
    expect(await screen.findByText("Hello from the migrated chat.")).toBeTruthy();
    await waitFor(() => {
      expect(chatHistoryApi.saveConversation).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          runId: "run-1",
          serviceId: "support-service",
          title: "Help me draft the next response.",
        }),
        expect.any(Array)
      );
    });
    fireEvent.click(screen.getByRole("button", { name: "Thinking" }));
    fireEvent.click(screen.getByRole("button", { name: /2 actions/i }));
    expect(screen.getByText("Inspecting the request")).toBeTruthy();
    expect(screen.getByText("lookup_context")).toBeTruthy();
    expect(screen.getByText("knowledge.search")).toBeTruthy();
    expect(screen.getByText(/Run: run-1/i)).toBeTruthy();
    expect(screen.getByText(/Actor: actor:\/\/support/i)).toBeTruthy();
  });

  it("binds and uses the built-in NyxID chat service", async () => {
    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    fireEvent.click(serviceSelector);
    fireEvent.click(await screen.findByRole("option", { name: /NyxID Chat/i }));

    const promptInput = await screen.findByPlaceholderText(
      "Describe the task, ask a question, or paste the next operator instruction."
    );
    fireEvent.change(promptInput, {
      target: { value: "Help me inspect my service binding." },
    });
    fireEvent.click(await screen.findByLabelText("Send"));

    await waitFor(() => {
      expect(studioApi.bindScopeGAgent).toHaveBeenCalledWith(
        expect.objectContaining({
          actorTypeName: "Aevatar.GAgents.NyxidChat.NyxIdChatGAgent",
          preferredActorId: "NyxIdChat:scope-a",
          scopeId: "scope-a",
          serviceId: "nyxid-chat",
        })
      );
    });

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          prompt: "Help me inspect my service binding.",
        }),
        expect.any(AbortSignal),
        {
          serviceId: "nyxid-chat",
        }
      );
    });
  });

  it("prefers the routed scope and service when Chat opens from Studio", async () => {
    window.history.pushState(
      {},
      "",
      "/chat?scopeId=scope-route&serviceId=nyxid-chat"
    );
    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    await waitFor(() => {
      expect(serviceSelector.textContent).toContain("NyxID Chat");
    });

    const promptInput = await screen.findByPlaceholderText(
      "Describe the task, ask a question, or paste the next operator instruction."
    );
    fireEvent.change(promptInput, {
      target: { value: "Route me to the published chat." },
    });
    fireEvent.click(await screen.findByLabelText("Send"));

    await waitFor(() => {
      expect(studioApi.bindScopeGAgent).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: "scope-route",
          serviceId: "nyxid-chat",
        })
      );
    });

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        "scope-route",
        expect.objectContaining({
          prompt: "Route me to the published chat.",
        }),
        expect.any(AbortSignal),
        {
          serviceId: "nyxid-chat",
        }
      );
    });
  });

  it("restores chat history through the remote history API", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      {
        actorId: "actor://support",
        createdAt: "2026-04-01T08:00:00.000Z",
        id: "conversation-1",
        messageCount: 2,
        runId: "run-history",
        serviceId: "support-service",
        serviceKind: "service",
        title: "Need historical context",
        updatedAt: "2026-04-01T08:10:00.000Z",
      },
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue([
      {
        content: "Need historical context",
        id: "user-1",
        role: "user",
        status: "complete",
        timestamp: 1,
      },
      {
        content: "Restored answer",
        id: "assistant-1",
        role: "assistant",
        status: "complete",
        thinking: "stored reasoning",
        timestamp: 2,
      },
    ]);

    renderWithQueryClient(React.createElement(ChatPage));

    const historyItem = await screen.findByText("Need historical context");
    fireEvent.click(historyItem);

    await screen.findByText("Restored answer");
    fireEvent.click(screen.getByRole("button", { name: "Thinking" }));
    expect(chatHistoryApi.loadConversation).toHaveBeenCalledWith(
      "scope-a",
      "conversation-1"
    );
    expect(screen.getByText("stored reasoning")).toBeTruthy();
    expect(screen.getByText(/Run: run-history/i)).toBeTruthy();
  });

  it("shows the raw debug stream for the active conversation", async () => {
    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    await waitFor(() => {
      expect(serviceSelector.textContent).toContain("Support service");
    });

    const promptInput = await screen.findByPlaceholderText(
      "Describe the task, ask a question, or paste the next operator instruction."
    );
    fireEvent.change(promptInput, {
      target: { value: "Show me the runtime event stream." },
    });
    fireEvent.click(screen.getByLabelText("Send"));

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          prompt: "Show me the runtime event stream.",
        }),
        expect.any(AbortSignal),
        {
          serviceId: "support-service",
        }
      );
    });

    expect(screen.queryByText("Raw Events (8)")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Debug" }));

    expect(await screen.findByText("Raw Events (8)")).toBeTruthy();
    expect(screen.getByText("RUN_STARTED")).toBeTruthy();
    expect(screen.getByText("TOOL_CALL_START")).toBeTruthy();
    expect(screen.getByText("TEXT_MESSAGE_CONTENT")).toBeTruthy();
  });

  it("opens a blank Studio draft when Create is clicked", async () => {
    renderWithQueryClient(React.createElement(ChatPage));

    fireEvent.click(await screen.findByLabelText("Chat service"));
    fireEvent.click(await screen.findByRole("button", { name: "Create" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/studio");
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("tab")).toBe("studio");
      expect(searchParams.get("draft")).toBe("new");
    });
  });
});
