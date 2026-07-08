import {
  createConversationId,
  deleteConversation,
  hydrateChatMessages,
  listConversationMetas,
  loadConversation,
  renameConversation,
  saveConversation,
  serializeChatMessages,
} from "./chatHistory";
import type { ChatMessage, ConversationMeta } from "./chatTypes";

describe("chatHistory", () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it("round-trips chat messages and keeps the newest conversation first", () => {
    const firstId = createConversationId();
    const secondId = createConversationId();
    const firstMeta: ConversationMeta = {
      createdAt: "2026-04-01T09:00:00.000Z",
      id: firstId,
      messageCount: 2,
      scopeId: "scope-a",
      serviceId: "support",
      serviceKind: "service",
      status: "completed_text",
      title: "First conversation",
      updatedAt: "2026-04-01T09:00:00.000Z",
      usage: {
        totalTokens: 12,
      },
    };
    const secondMeta: ConversationMeta = {
      createdAt: "2026-04-01T10:00:00.000Z",
      id: secondId,
      messageCount: 1,
      scopeId: "scope-a",
      serviceId: "support",
      serviceKind: "service",
      status: "completed_with_studio_target",
      target: {
        memberId: "member-a",
        scopeId: "scope-a",
        teamId: "team-a",
        workflowId: "workflow-a",
      },
      title: "Second conversation",
      updatedAt: "2026-04-01T10:00:00.000Z",
    };
    const firstMessages: ChatMessage[] = [
      {
        content: "Need an answer",
        id: "user-1",
        role: "user",
        status: "complete",
        timestamp: 1,
      },
      {
        content: "Here is an answer",
        id: "assistant-1",
        role: "assistant",
        status: "complete",
        thinking: "reasoning trail",
        timestamp: 2,
      },
    ];
    const secondMessages: ChatMessage[] = [
      {
        content: "Follow-up",
        id: "user-2",
        role: "user",
        status: "complete",
        timestamp: 3,
      },
    ];

    saveConversation("scope-a", firstMeta, serializeChatMessages(firstMessages));
    saveConversation("scope-a", secondMeta, serializeChatMessages(secondMessages));

    expect(listConversationMetas("scope-a").map((item) => item.id)).toEqual([
      secondId,
      firstId,
    ]);
    expect(listConversationMetas("scope-a")[0]).toMatchObject({
      status: "completed_with_studio_target",
      target: {
        memberId: "member-a",
        teamId: "team-a",
      },
    });
    expect(
      hydrateChatMessages(loadConversation("scope-a", firstId))
    ).toEqual([
      {
        content: "Need an answer",
        id: "user-1",
        role: "user",
        status: "complete",
        timestamp: 1,
      },
      {
        content: "Here is an answer",
        error: undefined,
        events: undefined,
        id: "assistant-1",
        pendingRunIntervention: undefined,
        role: "assistant",
        status: "complete",
        steps: undefined,
        thinking: "reasoning trail",
        timestamp: 2,
        toolCalls: undefined,
      },
    ]);
    expect(window.localStorage.getItem("aevatar.chat.localHistory.v1:scope-a")).toContain(
      "First conversation"
    );
  });

  it("deletes conversations cleanly", () => {
    const conversationId = createConversationId();
    saveConversation(
      "scope-a",
      {
        createdAt: "2026-04-01T09:00:00.000Z",
        id: conversationId,
        messageCount: 1,
        scopeId: "scope-a",
        serviceId: "support",
        serviceKind: "service",
        status: "completed_text",
        title: "Delete me",
        updatedAt: "2026-04-01T09:00:00.000Z",
      },
      [
        {
          content: "Hello",
          id: "message-1",
          role: "user",
          status: "complete",
          timestamp: 1,
        },
      ]
    );

    deleteConversation("scope-a", conversationId);

    expect(listConversationMetas("scope-a")).toEqual([]);
    expect(loadConversation("scope-a", conversationId)).toEqual([]);
  });

  it("renames local conversations", () => {
    const conversationId = createConversationId();
    saveConversation(
      "scope-a",
      {
        createdAt: "2026-07-01T09:00:00.000Z",
        id: conversationId,
        messageCount: 1,
        scopeId: "scope-a",
        serviceId: "chat",
        serviceKind: "chat",
        status: "completed_text",
        title: "Original title",
        updatedAt: "2026-07-01T09:00:00.000Z",
      },
      [
        {
          content: "Hello",
          id: "message-1",
          role: "user",
          status: "complete",
          timestamp: 1,
        },
      ]
    );

    renameConversation("scope-a", conversationId, "Renamed title");

    expect(listConversationMetas("scope-a")[0].title).toBe("Renamed title");
  });
});
