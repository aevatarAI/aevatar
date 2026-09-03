import { render, waitFor } from "@testing-library/react";
import React from "react";
import { requiresGlobalAuthGate } from "./shared/auth/globalAuthRoutes";
import { ProtectedRouteRedirectGate } from "./shared/auth/ProtectedRouteRedirectGate";

const mockedHistoryReplace = jest.fn();

jest.mock("./shared/navigation/history", () => ({
  history: {
    push: jest.fn(),
    replace: (...args: unknown[]) => mockedHistoryReplace(...args),
  },
}));

describe("ProtectedRouteRedirectGate", () => {
  beforeEach(() => {
    mockedHistoryReplace.mockReset();
    window.history.replaceState({}, "", "/scopes");
  });

  it("redirects protected routes into the login flow after mount", async () => {
    render(
      React.createElement(ProtectedRouteRedirectGate, {
        pathname: "/scopes",
      }),
    );

    await waitFor(() => {
      expect(mockedHistoryReplace).toHaveBeenCalledWith("/login?redirect=%2Fscopes");
    });
  });

  it("keeps Mission Wall behind the login flow", async () => {
    window.history.replaceState({}, "", "/runtime/mission-wall?focusRunId=run-1");

    render(
      React.createElement(ProtectedRouteRedirectGate, {
        pathname: "/runtime/mission-wall",
      }),
    );

    await waitFor(() => {
      expect(mockedHistoryReplace).toHaveBeenCalledWith(
        "/login?redirect=%2Fruntime%2Fmission-wall%3FfocusRunId%3Drun-1",
      );
    });
  });

  it("preserves the delivered Team member workflow deep link through login", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/s-customer/teams/t-hr/members/m-reminder/workflow?workflowId=wf-reminder#run",
    );

    render(
      React.createElement(ProtectedRouteRedirectGate, {
        pathname: "/scopes/s-customer/teams/t-hr/members/m-reminder/workflow",
      }),
    );

    await waitFor(() => {
      expect(mockedHistoryReplace).toHaveBeenCalledWith(
        "/login?redirect=%2Fscopes%2Fs-customer%2Fteams%2Ft-hr%2Fmembers%2Fm-reminder%2Fworkflow%3FworkflowId%3Dwf-reminder%23run",
      );
    });
  });
});

describe("global auth route classification", () => {
  it("protects canonical Team member workflow routes while legacy Studio keeps its own recovery", () => {
    expect(
      requiresGlobalAuthGate(
        "/scopes/s-customer/teams/t-hr/members/m-reminder/workflow",
      ),
    ).toBe(true);
    expect(requiresGlobalAuthGate("/studio")).toBe(false);
    expect(requiresGlobalAuthGate("/login")).toBe(false);
  });
});
