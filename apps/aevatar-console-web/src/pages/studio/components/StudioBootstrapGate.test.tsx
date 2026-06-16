import { render, screen } from "@testing-library/react";
import React from "react";
import StudioBootstrapGate from "./StudioBootstrapGate";

describe("StudioBootstrapGate", () => {
  it("renders one bootstrap summary banner and keeps children mounted", () => {
    render(
      <StudioBootstrapGate
        appContextLoading
        appContextError={new Error("app context failed")}
        authLoading={false}
        authError={new Error("auth bootstrap warning")}
        workspaceLoading={false}
        workspaceError={new Error("workspace failed")}
      >
        <div>Studio workbench</div>
      </StudioBootstrapGate>,
    );

    expect(
      screen.getByText(
        "Studio currently has some capabilities that are temporarily unavailable.",
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/team context: app context failed/i),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/workspace settings: workspace failed/i),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/login status: auth bootstrap warning/i),
    ).toBeInTheDocument();
    expect(screen.getByText("Studio workbench")).toBeInTheDocument();
  });
});
