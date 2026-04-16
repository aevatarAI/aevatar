import { waitFor } from "@testing-library/react";
import React from "react";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import ScopeOverviewPage from "./overview";

describe("ScopeOverviewPage", () => {
  it("redirects legacy overview links to the teams home route and keeps query context", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/overview?scopeId=scope-a&workflowId=workflow-alpha",
    );

    renderWithQueryClient(React.createElement(ScopeOverviewPage));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("scopeId")).toBe("scope-a");
    expect(params.get("workflowId")).toBe("workflow-alpha");
  });
});
