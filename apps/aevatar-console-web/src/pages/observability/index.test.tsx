import { screen } from "@testing-library/react";
import React from "react";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import ObservabilityPage from "./index";

describe("ObservabilityPage", () => {
  beforeEach(() => {
    window.history.replaceState(
      null,
      "",
      "/observability?workflow=direct&actorId=Workflow:19fe1b04&commandId=cmd-123"
    );
  });

  it("renders platform-oriented observability jumps", async () => {
    const { container } = renderWithQueryClient(
      React.createElement(ObservabilityPage)
    );

    expect(container.textContent).toContain("Observability");
    expect(container.textContent).toContain(
      "Use configured external tools as the jump hub for runtime, scopes, raw platform services, platform governance, and local settings without adding new backend APIs."
    );
    expect(container.textContent).toContain("Console surfaces");
    expect(container.textContent).toContain("Open Runtime Explorer");
    expect(container.textContent).toContain("Open Console Settings");
    expect(container.textContent).toContain("Open Scopes");
    expect(container.textContent).toContain("Open Platform Services");
    expect(container.textContent).toContain("Open Platform Governance");
    expect(screen.getByText("direct")).toBeTruthy();
    expect(screen.getByText("Workflow:19fe1b04")).toBeTruthy();
    expect(screen.getByText("cmd-123")).toBeTruthy();
  });
});
