import { fireEvent, render, screen } from "@testing-library/react";
import React from "react";
import StudioShell, { type StudioShellNavItem } from "./StudioShell";

describe("StudioShell", () => {
  const navItems: readonly StudioShellNavItem[] = [
    {
      key: "workflows",
      label: "Workflows",
      description: "Browse workspace workflows and start new drafts.",
      count: 3,
    },
    {
      key: "studio",
      label: "Studio",
      description: "Edit the active draft and inspect execution runs.",
      count: 0,
    },
    {
      key: "roles",
      label: "Roles",
      description: "Edit, import, and save workflow role definitions.",
    },
  ];

  it("renders a collapsible icon rail and forwards selection", () => {
    const handleSelectPage = jest.fn();

    const { container } = render(
      React.createElement(StudioShell, {
        currentPage: "workflows",
        navItems,
        onSelectPage: handleSelectPage,
        pageTitle: "Studio page",
        children: React.createElement("div", null, "Studio content"),
      })
    );

    expect(container.firstChild).toHaveStyle({
      flex: "1",
      height: "100%",
      minHeight: "0",
      overflow: "hidden",
    });
    expect(container.querySelector(".ant-row")).toHaveStyle({
      flex: "1",
      minHeight: "0",
    });
    expect(screen.getByText("Studio content").parentElement).toHaveStyle({
      flex: "1",
      minHeight: "0",
      overflowX: "hidden",
      overflowY: "auto",
    });

    expect(screen.getByLabelText("Workbench")).toHaveStyle({ width: "64px" });
    expect(screen.getByLabelText("Workbench navigation")).toBeTruthy();
    expect(screen.getByRole("button", { name: /workflows/i })).toHaveAttribute(
      "aria-current",
      "page"
    );
    expect(
      screen.queryByText("Browse workspace workflows and start new drafts.")
    ).toBeNull();
    expect(screen.queryByText("Workflows")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /studio/i }));

    expect(handleSelectPage).toHaveBeenCalledWith("studio");

    fireEvent.click(screen.getByRole("button", { name: "Expand workbench" }));

    expect(screen.getByLabelText("Workbench")).toHaveStyle({ width: "160px" });
    expect(screen.getByText("Workflows")).toBeTruthy();
    expect(screen.getByText("Collapse")).toBeTruthy();
    expect(
      screen.getByRole("button", { name: "Collapse workbench" })
    ).toHaveAttribute("aria-pressed", "true");
  });

  it("allows callers to hand scroll ownership to the page content", () => {
    render(
      React.createElement(StudioShell, {
        contentOverflow: "hidden",
        currentPage: "workflows",
        navItems,
        onSelectPage: jest.fn(),
        pageTitle: "Studio page",
        children: React.createElement("div", null, "Studio content"),
      })
    );

    expect(screen.getByText("Studio content").parentElement).toHaveStyle({
      overflowY: "hidden",
    });
  });
});
