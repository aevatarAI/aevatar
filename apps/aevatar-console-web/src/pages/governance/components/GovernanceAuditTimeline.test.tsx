import { fireEvent, render, screen } from "@testing-library/react";
import React from "react";
import GovernanceAuditTimeline, {
  type GovernanceAuditEvent,
} from "./GovernanceAuditTimeline";

const auditEvent: GovernanceAuditEvent = {
  action: "Endpoint exposure updated",
  actor: "governance-worker",
  at: "2026-04-16T10:00:00Z",
  id: "event-1",
  status: "public",
  summary: "Endpoint chat is now public after catalog update.",
  targetId: "chat",
  targetKind: "endpoint",
  targetLabel: "Chat endpoint",
};

describe("GovernanceAuditTimeline", () => {
  it("renders audit events as read-only facts when no selection handler is provided", () => {
    render(<GovernanceAuditTimeline events={[auditEvent]} />);

    expect(screen.getByText("Endpoint exposure updated")).toBeInTheDocument();
    expect(screen.getByText("Endpoint chat is now public after catalog update.")).toBeInTheDocument();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("renders audit events as selectable buttons when a selection handler is provided", () => {
    const onSelect = jest.fn();

    render(
      <GovernanceAuditTimeline
        events={[auditEvent]}
        onSelect={onSelect}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: /Endpoint exposure updated/ }));

    expect(onSelect).toHaveBeenCalledWith(auditEvent);
  });
});
