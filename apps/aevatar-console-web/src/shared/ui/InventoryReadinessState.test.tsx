import { fireEvent, render, screen } from "@testing-library/react";
import React from "react";
import InventoryReadinessState from "./InventoryReadinessState";

describe("InventoryReadinessState", () => {
  it("renders loading state without presenting an empty inventory", () => {
    render(
      <InventoryReadinessState
        description="Keep the current inventory visible until the request resolves."
        kind="loading"
        title="Loading inventory"
      />,
    );

    expect(screen.getByText("Loading inventory")).toBeTruthy();
    expect(screen.getByText("Keep the current inventory visible until the request resolves.")).toBeTruthy();
    expect(screen.getByText("Loading inventory").closest("[aria-busy='true']")).toBeTruthy();
  });

  it("renders error action without falling through to empty state", () => {
    const retry = jest.fn();

    render(
      <InventoryReadinessState
        action={{ label: "Retry inventory", onClick: retry }}
        description="The inventory query failed."
        kind="error"
        title="Inventory unavailable"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Retry inventory" }));

    expect(retry).toHaveBeenCalledTimes(1);
    expect(screen.getByText("Inventory unavailable")).toBeTruthy();
    expect(screen.queryByText("No inventory")).toBeNull();
  });

  it("renders empty state with an operator action", () => {
    const refine = jest.fn();

    render(
      <InventoryReadinessState
        action={{ label: "Refine scope", onClick: refine }}
        description="Try a narrower Team, App, or Namespace."
        kind="empty"
        title="No inventory"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Refine scope" }));

    expect(refine).toHaveBeenCalledTimes(1);
    expect(screen.getByText("Try a narrower Team, App, or Namespace.")).toBeTruthy();
  });
});
