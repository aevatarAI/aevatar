import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { persistAuthSession } from "@/shared/auth/session";
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from "../../../tests/reactQueryTestUtils";
import SettingsPage from "./index";

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(),
    getUserConfigRuntime: jest.fn(),
    getUserLlmSettings: jest.fn(),
    saveUserLlmSettings: jest.fn(),
  },
}));

const { studioApi: mockStudioApi } = jest.requireMock(
  "@/shared/studio/api",
) as {
  studioApi: {
    getAuthSession: jest.Mock;
    getUserConfigRuntime: jest.Mock;
    getUserLlmSettings: jest.Mock;
    saveUserLlmSettings: jest.Mock;
  };
};

const originalFetch = global.fetch;
const originalLocationDescriptor = Object.getOwnPropertyDescriptor(
  window,
  "location",
);
const originalCryptoDescriptor = Object.getOwnPropertyDescriptor(
  globalThis,
  "crypto",
);
const originalNyxIDClientId = process.env.NYXID_CLIENT_ID;

function installLocationAssignSpy() {
  const assign = jest.fn();
  Object.defineProperty(window, "location", {
    configurable: true,
    value: {
      ...window.location,
      assign,
      href: window.location.href,
      origin: window.location.origin,
    },
  });
  return assign;
}

function installDeterministicCrypto() {
  Object.defineProperty(globalThis, "crypto", {
    configurable: true,
    value: {
      getRandomValues: (array: Uint8Array) => {
        array.fill(7);
        return array;
      },
      subtle: {
        digest: jest.fn(async () => new Uint8Array(32).fill(9).buffer),
      },
    },
  });
}

function createLlmSettings(overrides: Record<string, unknown> = {}) {
  return {
    savedRoute: "",
    savedRouteLabel: "Backend saved gateway",
    effectiveRoute: "",
    effectiveRouteLabel: "Backend effective gateway",
    routeFallbackActive: false,
    fallbackReason: null,
    catalogStatus: "ready",
    defaultModel: "",
    capabilities: {
      canEditRoute: true,
      canEditModel: true,
      canSave: true,
      canRetryCatalog: false,
    },
    routeOptions: [
      {
        routeValue: "",
        label: "Gateway route option",
        source: "gateway_provider",
        status: "ready",
        allowed: true,
        ready: true,
        serviceId: null,
        serviceSlug: null,
        description: null,
      },
      {
        routeValue: "/api/v1/proxy/s/openai-team",
        label: "OpenAI Team Service",
        source: "user_service",
        status: "ready",
        allowed: true,
        ready: true,
        serviceId: "svc-openai",
        serviceSlug: "openai-team",
        description: null,
      },
      {
        routeValue: "/api/v1/proxy/s/anthropic-team",
        label: "Anthropic Lab Service",
        source: "user_service",
        status: "ready",
        allowed: true,
        ready: true,
        serviceId: "svc-anthropic",
        serviceSlug: "anthropic-team",
        description: null,
      },
    ],
    modelGroupsByRoute: [
      {
        routeValue: "",
        groupId: "openai",
        label: "OpenAI Gateway",
        models: ["gpt-4o", "gpt-4o-mini"],
      },
      {
        routeValue: "",
        groupId: "anthropic",
        label: "Anthropic Gateway",
        models: ["claude-3-5-sonnet", "claude-3-opus"],
      },
      {
        routeValue: "/api/v1/proxy/s/openai-team",
        groupId: "openai-team",
        label: "OpenAI Team Service",
        models: ["gpt-4.1-mini"],
      },
      {
        routeValue: "/api/v1/proxy/s/anthropic-team",
        groupId: "anthropic-team",
        label: "Anthropic Lab Service",
        models: ["claude-3-haiku"],
      },
    ],
    ...overrides,
  };
}

describe("SettingsPage", () => {
  beforeEach(() => {
    process.env.NYXID_CLIENT_ID = "console-client-1";
    window.localStorage.clear();
    window.history.replaceState({}, "", "/settings");
    jest.clearAllMocks();

    mockStudioApi.getAuthSession.mockResolvedValue({
      enabled: true,
      authenticated: true,
      providerDisplayName: "NyxID",
      profile: null,
      session: {
        authenticated: true,
        providerDisplayName: "NyxID",
      },
    });
    mockStudioApi.getUserLlmSettings.mockResolvedValue(createLlmSettings());
    mockStudioApi.getUserConfigRuntime.mockResolvedValue({
      runtimeMode: "local",
      activeRuntimeBaseUrl: "http://127.0.0.1:5080",
      localRuntimeBaseUrl: "http://127.0.0.1:5080",
      remoteRuntimeBaseUrl: "https://aevatar-console-backend-api.aevatar.ai",
      runtimeDefaults: {
        localRuntimeBaseUrl: "http://127.0.0.1:5080",
        remoteRuntimeBaseUrl: "https://aevatar-console-backend-api.aevatar.ai",
        localMode: "local",
        remoteMode: "remote",
      },
    });
    mockStudioApi.saveUserLlmSettings.mockImplementation(async (input) =>
      createLlmSettings({
        savedRoute: input.routeValue,
        effectiveRoute: input.routeValue,
        defaultModel: input.model ?? "",
      })
    );
  });

  afterEach(() => {
    if (originalNyxIDClientId === undefined) {
      delete process.env.NYXID_CLIENT_ID;
    } else {
      process.env.NYXID_CLIENT_ID = originalNyxIDClientId;
    }
    global.fetch = originalFetch;
    if (originalLocationDescriptor) {
      Object.defineProperty(window, "location", originalLocationDescriptor);
    }
    if (originalCryptoDescriptor) {
      Object.defineProperty(globalThis, "crypto", originalCryptoDescriptor);
    } else {
      Reflect.deleteProperty(globalThis, "crypto");
    }
    cleanupTestQueryClients();
  });

  it("renders the full-body LLM tab by default", async () => {
    renderWithQueryClient(React.createElement(SettingsPage));

    expect(await screen.findByRole("heading", { name: "Settings" })).toBeTruthy();
    expect(await screen.findByText("Edit defaults")).toBeTruthy();
    expect(screen.getByText("How defaults work")).toBeTruthy();
    expect(screen.getByText("Technical preview")).toBeTruthy();
    expect(screen.getAllByText("Effective route").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Backend effective gateway").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Backend saved gateway").length).toBeGreaterThan(0);
    expect(screen.getByText("Connected providers")).toBeTruthy();
    expect(screen.getAllByText("OpenAI Team Service").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Default model").length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: /Advanced runtime/i }));
    expect(screen.getByDisplayValue("http://127.0.0.1:5080")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Reset" })).toBeDisabled();
  });

  it("switches to the account tab in-place", async () => {
    persistAuthSession({
      tokens: {
        accessToken: "token",
        tokenType: "Bearer",
        expiresIn: 3600,
        expiresAt: Date.now() + 60_000,
      },
      user: {
        sub: "user-123",
        email: "ada@example.com",
        email_verified: true,
        name: "Ada Lovelace",
        roles: ["admin"],
        groups: ["platform"],
      },
    });

    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.click(await screen.findByRole("tab", { name: "Account" }));

    await waitFor(() => {
      expect(window.location.search).toBe("?section=account");
    });
    expect(screen.queryByRole("button", { name: "Save config" })).toBeNull();
    expect(await screen.findByText("Profile")).toBeTruthy();
    expect(screen.getByText("Ada Lovelace")).toBeTruthy();
    expect(screen.getByText("Authentication")).toBeTruthy();
  });

  it("starts NyxID service access review from Account settings", async () => {
    persistAuthSession({
      tokens: {
        accessToken: "token",
        tokenType: "Bearer",
        expiresIn: 3600,
        expiresAt: Date.now() + 60_000,
      },
      user: {
        sub: "user-123",
        email: "ada@example.com",
        email_verified: true,
        name: "Ada Lovelace",
      },
    });
    installDeterministicCrypto();
    window.history.replaceState({}, "", "/settings?section=account");
    const assign = installLocationAssignSpy();
    const fetchMock = jest.fn();
    global.fetch = fetchMock as typeof global.fetch;

    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.click(
      await screen.findByRole("button", { name: "Manage service access" }),
    );

    await waitFor(() => {
      expect(assign).toHaveBeenCalledTimes(1);
    });
    const authorizeUrl = new URL(assign.mock.calls[0][0]);
    expect(authorizeUrl.searchParams.get("prompt")).toBe("consent");
    expect(authorizeUrl.searchParams.getAll("resource")).toEqual([]);
    expect(fetchMock).not.toHaveBeenCalled();

    const pending = JSON.parse(
      window.localStorage.getItem(
        "aevatar-console:nyxid:pending:console-client-1",
      ) ?? "{}",
    );
    expect(pending).toEqual(
      expect.objectContaining({
        flow: "serviceAccessReview",
        returnTo: "/settings?section=account",
        state: authorizeUrl.searchParams.get("state"),
      }),
    );
  });

  it("keeps service access review retryable when redirect setup fails", async () => {
    persistAuthSession({
      tokens: { accessToken: "token", tokenType: "Bearer", expiresIn: 3600, expiresAt: Date.now() + 60_000 },
      user: { sub: "user-123", name: "Ada Lovelace" },
    });
    installDeterministicCrypto();
    window.history.replaceState({}, "", "/settings?section=account");
    const assign = installLocationAssignSpy();
    assign.mockImplementationOnce(() => {
      throw new Error("Navigation failed");
    });
    const fetchMock = jest.fn();
    global.fetch = fetchMock as typeof global.fetch;

    renderWithQueryClient(React.createElement(SettingsPage));
    const manageButton = await screen.findByRole("button", { name: "Manage service access" });
    fireEvent.click(manageButton);
    expect(await screen.findByRole("alert")).toHaveTextContent("Could not start service access review. Try again.");
    await waitFor(() => expect(manageButton).not.toHaveClass("ant-btn-loading"));
    fireEvent.click(manageButton);
    await waitFor(() => expect(assign).toHaveBeenCalledTimes(2));
    expect(screen.queryByRole("alert")).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("hides service access review when the user is signed out", async () => {
    mockStudioApi.getAuthSession.mockResolvedValueOnce({
      enabled: true, authenticated: false, providerDisplayName: "NyxID", profile: null, session: null,
    });
    window.history.replaceState({}, "", "/settings?section=account");
    renderWithQueryClient(React.createElement(SettingsPage));
    expect(await screen.findByRole("button", { name: "Sign in" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Manage service access" })).toBeNull();
  });

  it("shows gateway models from backend model groups", async () => {
    renderWithQueryClient(React.createElement(SettingsPage));

    expect(await screen.findByText("4 live")).toBeTruthy();
  });

  it("uses backend route-scoped model choices when the saved route is a service", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        savedRoute: "/api/v1/proxy/s/anthropic-team",
        savedRouteLabel: "Anthropic Lab Service",
        effectiveRoute: "/api/v1/proxy/s/anthropic-team",
        effectiveRouteLabel: "Anthropic Lab Service",
      })
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() => {
      expect(screen.getByText("1 live")).toBeTruthy();
    });
  });

  it("does not invent service-specific model choices when backend sends no route group", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        savedRoute: "/api/v1/proxy/s/anthropic-team",
        savedRouteLabel: "Anthropic Lab Service",
        effectiveRoute: "/api/v1/proxy/s/anthropic-team",
        effectiveRouteLabel: "Anthropic Lab Service",
        modelGroupsByRoute: [
          {
            routeValue: "",
            groupId: "openai",
            label: "OpenAI Gateway",
            models: ["gpt-4o", "gpt-4o-mini"],
          },
        ],
      })
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() => {
      expect(screen.getByPlaceholderText("Type a model ID for Anthropic Lab Service")).toBeTruthy();
    });

    expect(screen.queryByRole("combobox", { name: "Default model" })).toBeNull();
  });

  it("excludes stale saved routes and models that are not in the backend catalog", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        defaultModel: "retired-model",
        effectiveRoute: "/api/v1/proxy/s/retired-team",
        effectiveRouteLabel: "Retired Team",
        routeOptions: [
          {
            routeValue: "",
            label: "NyxID Gateway",
            source: "gateway_provider",
            status: "ready",
            allowed: true,
            ready: true,
            serviceId: null,
            serviceSlug: null,
            description: null,
          },
        ],
        savedRoute: "/api/v1/proxy/s/retired-team",
        savedRouteLabel: "Retired Team",
      })
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.mouseDown(await screen.findByRole("combobox", { name: "Preferred route" }));

    await waitFor(() => {
      expect(screen.getAllByText("NyxID Gateway").length).toBeGreaterThan(1);
    });
    expect(screen.queryByRole("option", { name: "Retired Team" })).toBeNull();
    expect(screen.queryByRole("option", { name: "retired-model" })).toBeNull();
  });

  it("saves canonical LLM settings", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        modelGroupsByRoute: [],
      })
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.change(await screen.findByLabelText("Default model"), {
      target: { value: "gpt-4o" },
    });

    fireEvent.click(await screen.findByRole("button", { name: "Save config" }));

    await waitFor(() => {
      expect(mockStudioApi.saveUserLlmSettings).toHaveBeenCalledWith({
        model: "gpt-4o",
        routeValue: "",
      });
    });
  });
});
