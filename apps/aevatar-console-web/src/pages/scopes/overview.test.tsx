import { waitFor } from "@testing-library/react";
import React from "react";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import ScopeOverviewPage from "./overview";

describe("ScopeOverviewPage", () => {
  it("redirects legacy overview links to the scoped teams route and keeps non-scope query context", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/overview?scopeId=scope-a&workflowId=workflow-alpha",
    );

    renderWithQueryClient(React.createElement(ScopeOverviewPage));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/scopes/scope-a/teams");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("scopeId")).toBeNull();
    expect(params.get("workflowId")).toBe("workflow-alpha");
  });
});
