import { fireEvent, screen } from "@testing-library/react";
import { setLocale } from "@umijs/max";
import React from "react";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import PlatformOverviewPage from "./index";

describe("PlatformOverviewPage", () => {
  beforeEach(() => {
    setLocale("en-US", false);
    window.history.replaceState({}, "", "/platform");
  });

  it("renders a task-oriented platform overview without backend object labels as the primary modules", () => {
    renderWithQueryClient(React.createElement(PlatformOverviewPage));

    expect(screen.getByText("Aevatar / Platform")).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Platform overview" })).toBeTruthy();
    expect(screen.getByText("Publish-and-run path")).toBeTruthy();
    expect(screen.getByText("Platform modules")).toBeTruthy();
    expect(screen.getByText("No synthetic health score")).toBeTruthy();

    for (const name of [
      "Capabilities",
      "Access & Rules",
      "Releases",
      "Runs",
      "Runtime Map",
    ]) {
      expect(screen.getByRole("heading", { name })).toBeTruthy();
    }

    expect(
      screen.getByText(
        "Starts from explicit runtime context; no actor graph is guessed on the overview.",
      ),
    ).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "Services" })).toBeNull();
    expect(screen.queryByRole("heading", { name: "Governance" })).toBeNull();
    expect(screen.queryByRole("heading", { name: "Deployments" })).toBeNull();
    expect(screen.queryByRole("heading", { name: "Event Stream" })).toBeNull();
    expect(screen.queryByRole("heading", { name: "Topology" })).toBeNull();
  });

  it("keeps CTA navigation on the unchanged deep-link routes", () => {
    renderWithQueryClient(React.createElement(PlatformOverviewPage));

    fireEvent.click(screen.getByRole("button", { name: "Open capabilities" }));
    expect(window.location.pathname).toBe("/services");

    window.history.replaceState({}, "", "/platform");
    fireEvent.click(screen.getByRole("button", { name: "Open access and rules" }));
    expect(window.location.pathname).toBe("/governance");

    window.history.replaceState({}, "", "/platform");
    fireEvent.click(screen.getByRole("button", { name: "Open releases" }));
    expect(window.location.pathname).toBe("/deployments");

    window.history.replaceState({}, "", "/platform");
    fireEvent.click(screen.getByRole("button", { name: "Open runs" }));
    expect(window.location.pathname).toBe("/runtime/runs");

    window.history.replaceState({}, "", "/platform");
    fireEvent.click(screen.getByRole("button", { name: "Open runtime map" }));
    expect(window.location.pathname).toBe("/runtime/explorer");
  });
});
