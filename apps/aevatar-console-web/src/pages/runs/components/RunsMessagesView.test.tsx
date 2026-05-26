import { render, screen } from "@testing-library/react";
import React from "react";
import RunsMessagesView from "./RunsMessagesView";

describe("RunsMessagesView", () => {
  it("renders message cards with role, status, and content", () => {
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
    expect(screen.getByText("streaming")).toBeInTheDocument();
    expect(screen.getByText("msg-1")).toBeInTheDocument();
    expect(screen.getByText("Streaming reply chunk")).toBeInTheDocument();
    expect(screen.getByText("user")).toBeInTheDocument();
    expect(screen.getByText("complete")).toBeInTheDocument();
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
