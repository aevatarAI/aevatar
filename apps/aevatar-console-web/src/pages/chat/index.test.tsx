import { fireEvent, screen, waitFor } from "@testing-library/react";
import * as React from "react";
import { authFetch } from "@/shared/auth/fetch";
import { history } from "@/shared/navigation/history";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import ChatPage from "./index";

jest.mock("@/shared/auth/fetch", () => ({
  authFetch: jest.fn(),
}));

jest.mock("@/shared/navigation/history", () => ({
  history: {
    push: jest.fn(),
  },
}));

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(async () => ({
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

function setNativeTextareaValue(
  element: HTMLElement,
  value: string
): void {
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

describe("ChatPage MVP", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    window.localStorage.clear();
  });

  it("does not create another local draft when the active New Chat is empty", async () => {
    renderWithQueryClient(<ChatPage />);

    const newChatButton = await screen.findByRole("button", { name: "New Chat" });
    fireEvent.click(newChatButton);
    fireEvent.click(newChatButton);
    fireEvent.click(newChatButton);

    await waitFor(() =>
      expect(screen.getAllByText("New chat")).toHaveLength(2)
    );

    const stored = JSON.parse(
      window.localStorage.getItem("aevatar.chat.localHistory.v1:scope-a") || "[]"
    );
    expect(stored).toHaveLength(1);
    expect(stored[0]).toMatchObject({
      messages: [],
      status: "draft",
      title: "New chat",
    });
  });

  it("calls POST /api/chat with a local session id and keeps text-only results in Chat", async () => {
    (authFetch as jest.Mock).mockResolvedValueOnce(
      createSseResponse([
        {
          runFinished: {
            result: {
              output: "Request completed. I saved the conversation here.",
            },
          },
        },
        {
          usage: {
            completionTokens: 5,
            promptTokens: 7,
            totalTokens: 12,
          },
        },
      ])
    );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Create a support team");

    await waitFor(() =>
      expect(authFetch).toHaveBeenCalledWith(
        "/api/chat",
        expect.objectContaining({
          method: "POST",
        })
      )
    );
    const request = (authFetch as jest.Mock).mock.calls[0][1];
    expect(JSON.parse(request.body)).toMatchObject({
      prompt: "Create a support team",
      scopeId: "scope-a",
      workflow: "studio",
    });
    expect(JSON.parse(request.body).sessionId).toBeTruthy();
    expect(await screen.findByText("Request completed. I saved the conversation here.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Open Workflow Studio" })).toBeNull();
    expect(await screen.findByText("12 tokens")).toBeTruthy();
  });

  it("persists visible assistant streaming text before the response finishes", async () => {
    const stream = createControlledSseResponse();
    (authFetch as jest.Mock).mockResolvedValueOnce(stream.response);

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Stream a draft plan");

    stream.enqueue({
      textMessageContent: {
        delta: "Draft plan in progress",
        messageId: "message-a",
      },
    });

    await screen.findByText("Draft plan in progress");
    const stored = JSON.parse(
      window.localStorage.getItem("aevatar.chat.localHistory.v1:scope-a") || "[]"
    );
    expect(stored[0].messages).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          content: "Draft plan in progress",
          role: "assistant",
          status: "complete",
        }),
      ])
    );

    stream.close();
  });

  it("shows confirmation as the primary path and sends confirmation on the same session", async () => {
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(
        createSseResponse([
          {
            runFinished: {
              result: {
                output: "Plan ready. Please confirm before I create resources.",
              },
            },
          },
        ])
      )
      .mockResolvedValueOnce(
        createSseResponse([
          {
            runFinished: {
              result: {
                output: "Created.",
                scopeId: "scope-a",
                teamId: "team-a",
                memberId: "member-a",
                workflowId: "workflow-a",
              },
            },
          },
        ])
      );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Plan a claims team");

    fireEvent.click(await screen.findByRole("button", { name: "Confirm and create" }));

    await waitFor(() => expect(authFetch).toHaveBeenCalledTimes(2));
    const firstBody = JSON.parse((authFetch as jest.Mock).mock.calls[0][1].body);
    const secondBody = JSON.parse((authFetch as jest.Mock).mock.calls[1][1].body);
    expect(secondBody.sessionId).toBe(firstBody.sessionId);
    expect(secondBody.prompt).toContain("Confirm. Please create it now.");
    expect(await screen.findByRole("button", { name: "Open Workflow Studio" })).toBeTruthy();
  });

  it("opens Workflow Studio only when structured identifiers are returned", async () => {
    (authFetch as jest.Mock).mockResolvedValueOnce(
      createSseResponse([
        {
          runFinished: {
            result: {
              output: "Created.",
              scopeId: "scope-a",
              teamId: "team-a",
              memberId: "member-a",
              workflowId: "workflow-a",
            },
          },
        },
      ])
    );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt("Directly create the workflow");

    fireEvent.click(await screen.findByRole("button", { name: "Open Workflow Studio" }));

    expect(history.push).toHaveBeenCalledWith(
      "/scopes/scope-a/teams/team-a/members/member-a/workflow?workflowId=workflow-a"
    );
  });

  it("restores local history after remount", async () => {
    (authFetch as jest.Mock).mockResolvedValueOnce(
      createSseResponse([
        {
          runFinished: {
            result: {
              output: "Saved locally.",
            },
          },
        },
      ])
    );
    const firstView = renderWithQueryClient(<ChatPage />);
    await sendPrompt("Create an onboarding team");
    await screen.findByText("Saved locally.");
    firstView.unmount();

    renderWithQueryClient(<ChatPage />);

    expect(await screen.findByText("Create an onboarding team")).toBeTruthy();
  });
});
