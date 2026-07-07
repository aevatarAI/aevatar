import { fireEvent, render, screen } from "@testing-library/react";
import { getLocale, setLocale } from "@umijs/max";
import React from "react";
import {
  clearStoredAuthSession,
  persistAuthSession,
} from "@/shared/auth/session";
import { ConsoleHeaderActions } from "./ConsoleHeaderActions";

const mockedHistoryPush = jest.fn();

jest.mock("@/shared/navigation/history", () => ({
  history: {
    push: (...args: unknown[]) => mockedHistoryPush(...args),
  },
}));

describe("ConsoleHeaderActions", () => {
  beforeEach(() => {
    clearStoredAuthSession();
    mockedHistoryPush.mockReset();
    setLocale("en-US", false);
    window.history.replaceState({}, "", "/runtime/mission-wall?focusRunId=run-1");
  });

  afterEach(() => {
    clearStoredAuthSession();
  });

  it("renders a login entry when there is no restorable auth session", () => {
    render(React.createElement(ConsoleHeaderActions));

    expect(
      screen.getByRole("button", { name: "Switch language" }),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(mockedHistoryPush).toHaveBeenCalledWith(
      "/login?redirect=%2Fruntime%2Fmission-wall%3FfocusRunId%3Drun-1",
    );
  });

  it("keeps the language switch and authenticated user menu together", () => {
    persistAuthSession({
      tokens: {
        accessToken: "token",
        expiresAt: Date.now() + 60_000,
        expiresIn: 60,
        tokenType: "Bearer",
      },
      user: {
        email: "abigail@example.com",
        name: "Abigail Deng",
        picture: "https://example.com/avatar.png",
        sub: "user-abigail",
      },
    });

    render(React.createElement(ConsoleHeaderActions));

    fireEvent.click(screen.getByRole("button", { name: "Switch language" }));
    fireEvent.click(screen.getByText("中文"));

    expect(getLocale()).toBe("zh-CN");
    expect(screen.getByText("Abigail Deng")).toBeInTheDocument();
  });

  it("applies an optional dropdown root class to action menus", async () => {
    persistAuthSession({
      tokens: {
        accessToken: "token",
        expiresAt: Date.now() + 60_000,
        expiresIn: 60,
        tokenType: "Bearer",
      },
      user: {
        email: "abigail@example.com",
        name: "Abigail Deng",
        picture: "https://example.com/avatar.png",
        sub: "user-abigail",
      },
    });

    render(
      React.createElement(ConsoleHeaderActions, {
        dropdownRootClassName: "mission-wall-header-menu",
      }),
    );

    expect(document.querySelector(".console-header-actions")).toHaveAttribute(
      "data-dropdown-root-class-name",
      "mission-wall-header-menu",
    );

    fireEvent.click(screen.getByRole("button", { name: "Switch language" }));

    expect(await screen.findByText("中文")).toBeInTheDocument();
    expect(
      document.querySelector(".mission-wall-header-menu"),
    ).toBeInTheDocument();
  });
});
