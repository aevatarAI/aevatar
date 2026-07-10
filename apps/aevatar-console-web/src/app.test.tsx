import { render, waitFor } from "@testing-library/react";
import React from "react";
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
});
