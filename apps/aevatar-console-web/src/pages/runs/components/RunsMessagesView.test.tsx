import { render, screen } from "@testing-library/react";
import React from "react";
import { setLocale } from "@umijs/max";
import RunsMessagesView from "./RunsMessagesView";

describe("RunsMessagesView", () => {
  beforeEach(() => {
    setLocale("en-US");
  });

  it("renders message cards with role, status, id, and content", () => {
    render(
      <RunsMessagesView
        messages={[
          {
            complete: false,
            content: "Streaming reply chunk",
            messageId: "msg-1",
            role: "assistant",
          },
          {
            complete: true,
            content: "Operator prompt",
            messageId: "msg-2",
            role: "user",
          },
        ]}
      />,
    );

    expect(screen.getByText("Message stream")).toBeInTheDocument();
    expect(screen.getByText("2 observed")).toBeInTheDocument();
    expect(screen.getByText("assistant")).toBeInTheDocument();
    expect(screen.getByText("Streaming")).toBeInTheDocument();
    expect(screen.getByText("msg-1")).toBeInTheDocument();
    expect(screen.getByText("Streaming reply chunk")).toBeInTheDocument();
    expect(screen.getByText("user")).toBeInTheDocument();
    expect(screen.getByText("Complete")).toBeInTheDocument();
    expect(screen.getByText("msg-2")).toBeInTheDocument();
    expect(screen.getByText("Operator prompt")).toBeInTheDocument();
  });

  it("renders an inline accessory above the message stream", () => {
    render(
      <RunsMessagesView
        messages={[]}
        topAccessory={<div>Action required</div>}
      />,
    );

    expect(screen.getByText("Action required")).toBeInTheDocument();
    expect(screen.getByText("No message output yet.")).toBeInTheDocument();
  });
});
