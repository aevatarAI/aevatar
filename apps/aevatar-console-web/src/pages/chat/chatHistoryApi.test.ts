jest.mock("./chatHistory", () => ({
  deleteConversation: jest.fn(),
  listConversationMetas: jest.fn(),
  loadConversation: jest.fn(),
  renameConversation: jest.fn(),
  saveConversation: jest.fn(),
}));

import {
  deleteConversation as deleteLocalConversation,
  listConversationMetas as listLocalConversationMetas,
  loadConversation as loadLocalConversation,
  renameConversation as renameLocalConversation,
  saveConversation as saveLocalConversation,
} from "./chatHistory";
import { chatHistoryApi } from "./chatHistoryApi";

describe("chatHistoryApi", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("uses local browser history for the MVP", async () => {
    const meta = {
      createdAt: "2026-07-01T08:00:00.000Z",
      id: "conversation-1",
      messageCount: 1,
      scopeId: "scope-a",
      serviceId: "chat",
      serviceKind: "chat",
      status: "completed_text",
      title: "Need routing help",
      updatedAt: "2026-07-01T08:01:00.000Z",
    };
    const messages = [
      {
        content: "Need routing help",
        id: "user-1",
        role: "user",
        status: "complete",
        timestamp: 1,
      },
    ];

    (listLocalConversationMetas as jest.Mock).mockReturnValue([meta]);
    (loadLocalConversation as jest.Mock).mockReturnValue(messages);

    await expect(chatHistoryApi.listConversationMetas("scope-a")).resolves.toEqual([
      meta,
    ]);
    await expect(
      chatHistoryApi.loadConversation("scope-a", "conversation-1")
    ).resolves.toEqual(messages);
    await chatHistoryApi.saveConversation("scope-a", meta as any, messages as any);
    await chatHistoryApi.renameConversation("scope-a", "conversation-1", "Renamed");
    await chatHistoryApi.deleteConversation("scope-a", "conversation-1");

    expect(saveLocalConversation).toHaveBeenCalledWith("scope-a", meta, messages);
    expect(renameLocalConversation).toHaveBeenCalledWith(
      "scope-a",
      "conversation-1",
      "Renamed"
    );
    expect(deleteLocalConversation).toHaveBeenCalledWith(
      "scope-a",
      "conversation-1"
    );
  });
});
