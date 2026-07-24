import { act, fireEvent, screen, waitFor } from "@testing-library/react";
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
const gatewayRoute = "/api/v1/llm/gateway/v1";
const sharedExactServiceRoute = "/api/v1/proxy/s/shared-openai";

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
    savedRoute: gatewayRoute,
    savedRouteLabel: "Backend saved gateway",
    savedRouteKind: "gateway",
    savedUserServiceId: null,
    savedServiceSlug: null,
    effectiveRoute: gatewayRoute,
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
        routeValue: gatewayRoute,
        label: "Gateway route option",
        source: "gateway_provider",
        status: "ready",
        allowed: true,
        ready: true,
        userServiceId: null,
        serviceSlug: null,
        defaultModel: null,
        description: null,
      },
      {
        routeValue: "/api/v1/proxy/s/openai-team",
        label: "OpenAI Team Service",
        source: "user_service",
        status: "ready",
        allowed: true,
        ready: true,
        userServiceId: "us-openai",
        serviceSlug: "openai-team",
        defaultModel: "gpt-4.1-mini",
        description: null,
      },
      {
        routeValue: "/api/v1/proxy/s/anthropic-team",
        label: "Anthropic Lab Service",
        source: "user_service",
        status: "ready",
        allowed: true,
        ready: true,
        userServiceId: "us-anthropic",
        serviceSlug: "anthropic-team",
        defaultModel: "claude-3-haiku",
        description: null,
      },
    ],
    modelGroupsByRoute: [
      {
        routeValue: gatewayRoute,
        groupId: "openai",
        label: "OpenAI Gateway",
        models: ["gpt-4o", "gpt-4o-mini"],
      },
      {
        routeValue: gatewayRoute,
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

function createExactServiceSettings(
  savedUserServiceId: string,
  defaultModel = "gpt-shared",
  overrides: Record<string, unknown> = {},
) {
  const labels: Record<string, string> = {
    "us-alpha": "Shared OpenAI alpha",
    "us-beta": "Shared OpenAI beta",
    "us-gamma": "Shared OpenAI gamma",
  };
  return createLlmSettings({
    savedRoute: sharedExactServiceRoute,
    savedRouteLabel: labels[savedUserServiceId],
    savedRouteKind: "nyx_id_user_service",
    savedUserServiceId,
    savedServiceSlug: "shared-openai",
    effectiveRoute: sharedExactServiceRoute,
    effectiveRouteLabel: labels[savedUserServiceId],
    defaultModel,
    routeOptions: [
      {
        routeValue: gatewayRoute,
        label: "Gateway route option",
        source: "gateway_provider",
        status: "ready",
        allowed: true,
        ready: true,
        userServiceId: null,
        serviceSlug: null,
        defaultModel: null,
        description: null,
      },
      ...Object.entries(labels).map(([userServiceId, label]) => ({
        routeValue: sharedExactServiceRoute,
        label,
        source: "user_service",
        status: "ready",
        allowed: true,
        ready: true,
        userServiceId,
        serviceSlug: "shared-openai",
        defaultModel: `gpt-${userServiceId.slice(3)}`,
        description: null,
      })),
    ],
    modelGroupsByRoute: [
      {
        routeValue: sharedExactServiceRoute,
        groupId: "shared-openai",
        label: "Shared OpenAI",
        models: ["gpt-alpha", "gpt-beta", "gpt-gamma", "gpt-shared"],
      },
    ],
    ...overrides,
  });
}

function selectedLlmServiceElement(): Element | null {
  return screen
    .getByRole("combobox", { name: "Preferred LLM service" })
    .closest(".ant-select");
}

async function selectLlmService(label: string): Promise<void> {
  fireEvent.mouseDown(
    screen.getByRole("combobox", { name: "Preferred LLM service" }),
  );
  fireEvent.click(await screen.findByRole("option", { name: label }));
}

async function selectDefaultModel(label: string): Promise<void> {
  const control = screen.getByLabelText("Default model");
  if (!control.closest(".ant-select")) {
    fireEvent.change(control, { target: { value: label } });
    return;
  }

  fireEvent.mouseDown(control);
  fireEvent.click(await screen.findByRole("option", { name: label }));
}

function selectedDefaultModelElement(): Element {
  const control = screen.getByLabelText("Default model");
  return control.closest(".ant-select") ?? control;
}

function createDeferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((nextResolve, nextReject) => {
    resolve = nextResolve;
    reject = nextReject;
  });
  return { promise, reject, resolve };
}

describe("SettingsPage", () => {
  beforeEach(() => {
    process.env.NYXID_CLIENT_ID = "console-client-1";
    window.localStorage.clear();
    window.history.replaceState({}, "", "/settings");
    jest.clearAllMocks();

    mockStudioApi.getAuthSession.mockReset();
    mockStudioApi.getUserConfigRuntime.mockReset();
    mockStudioApi.getUserLlmSettings.mockReset();
    mockStudioApi.saveUserLlmSettings.mockReset();

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
    mockStudioApi.saveUserLlmSettings.mockResolvedValue({
      accepted: true,
      commandId: "cmd-settings-1",
      ackStage: "accepted_for_dispatch",
      actorId: "user-1",
      correlationId: "corr-settings-1",
      ackedAtUtc: "2026-07-23T08:00:00Z",
    });
  });

  afterEach(() => {
    jest.useRealTimers();
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

  it("uses route-scoped model choices for the saved exact service", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        savedRoute: "/api/v1/proxy/s/anthropic-team",
        savedRouteLabel: "Anthropic Lab Service",
        savedRouteKind: "nyx_id_user_service",
        savedUserServiceId: "us-anthropic",
        savedServiceSlug: "anthropic-team",
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
        savedRouteKind: "nyx_id_user_service",
        savedUserServiceId: "us-anthropic",
        savedServiceSlug: "anthropic-team",
        effectiveRoute: "/api/v1/proxy/s/anthropic-team",
        effectiveRouteLabel: "Anthropic Lab Service",
        modelGroupsByRoute: [
          {
            routeValue: gatewayRoute,
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

  it("excludes stale service routes and models that are not in the backend catalog", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        defaultModel: "retired-model",
        effectiveRoute: "/api/v1/proxy/s/retired-team",
        effectiveRouteLabel: "Retired Team",
        routeOptions: [
          {
            routeValue: gatewayRoute,
            label: "NyxID Gateway",
            source: "gateway_provider",
            status: "ready",
            allowed: true,
            ready: true,
            userServiceId: null,
            serviceSlug: null,
            defaultModel: null,
            description: null,
          },
        ],
        savedRoute: "/api/v1/proxy/s/retired-team",
        savedRouteLabel: "Retired Team",
        savedRouteKind: "nyx_id_user_service",
        savedUserServiceId: null,
        savedServiceSlug: "retired-team",
      })
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.mouseDown(await screen.findByRole("combobox", { name: "Preferred LLM service" }));

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
        routeValue: gatewayRoute,
      });
    });
  });

  it("selects and saves duplicate-route services by exact user service ID", async () => {
    const sharedRoute = "/api/v1/proxy/s/shared-openai";
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        defaultModel: "gpt-shared",
        routeOptions: [
          {
            routeValue: gatewayRoute,
            label: "Gateway route option",
            source: "gateway_provider",
            status: "ready",
            allowed: true,
            ready: true,
            userServiceId: null,
            serviceSlug: null,
            defaultModel: null,
            description: null,
          },
          {
            routeValue: sharedRoute,
            label: "Shared OpenAI alpha",
            source: "user_service",
            status: "ready",
            allowed: true,
            ready: true,
            userServiceId: "us-alpha",
            serviceSlug: "shared-openai",
            defaultModel: "gpt-alpha",
            description: null,
          },
          {
            routeValue: sharedRoute,
            label: "Shared OpenAI beta",
            source: "user_service",
            status: "ready",
            allowed: true,
            ready: true,
            userServiceId: "us-beta",
            serviceSlug: "shared-openai",
            defaultModel: "gpt-beta",
            description: null,
          },
          {
            routeValue: "/api/v1/proxy/s/diagnostic-only",
            label: "Provider diagnostic",
            source: "provider_diagnostic",
            status: "ready",
            allowed: true,
            ready: true,
            userServiceId: null,
            serviceSlug: "diagnostic-only",
            defaultModel: null,
            description: "Health only",
          },
        ],
        modelGroupsByRoute: [
          {
            routeValue: sharedRoute,
            groupId: "shared-openai",
            label: "Shared OpenAI",
            models: ["gpt-shared"],
          },
        ],
      }),
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.mouseDown(
      await screen.findByRole("combobox", { name: "Preferred LLM service" }),
    );
    expect(
      await screen.findByLabelText(
        "Provider diagnostic · Ready · Provider diagnostic",
      ),
    ).toHaveAttribute("tabindex", "0");
    expect(await screen.findByRole("option", { name: "Shared OpenAI alpha" })).toBeTruthy();
    fireEvent.click(screen.getByRole("option", { name: "Shared OpenAI beta" }));
    expect(screen.queryByRole("option", { name: "Provider diagnostic" })).toBeNull();

    fireEvent.click(await screen.findByRole("button", { name: "Save config" }));

    await waitFor(() => {
      expect(mockStudioApi.saveUserLlmSettings).toHaveBeenCalledWith({
        userServiceId: "us-beta",
        model: "gpt-shared",
      });
    });
  });

  it("hydrates a pristine draft when exact server identity refreshes from A to B", async () => {
    mockStudioApi.getUserLlmSettings
      .mockResolvedValueOnce(createExactServiceSettings("us-alpha"))
      .mockResolvedValue(createExactServiceSettings("us-beta"));
    const view = renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent(
        "Shared OpenAI alpha",
      ),
    );

    await act(async () => {
      await view.queryClient.invalidateQueries({
        queryKey: ["settings", "user-llm-settings"],
      });
    });

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent(
        "Shared OpenAI beta",
      ),
    );
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
  });

  it("preserves an explicitly edited exact service across a server refresh", async () => {
    mockStudioApi.getUserLlmSettings
      .mockResolvedValueOnce(createExactServiceSettings("us-alpha"))
      .mockResolvedValue(createExactServiceSettings("us-gamma"));
    const view = renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent(
        "Shared OpenAI alpha",
      ),
    );
    await selectLlmService("Shared OpenAI beta");

    await act(async () => {
      await view.queryClient.invalidateQueries({
        queryKey: ["settings", "user-llm-settings"],
      });
    });

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent(
        "Shared OpenAI beta",
      ),
    );
    expect(screen.getByRole("button", { name: "Save config" })).toBeEnabled();
  });

  it("becomes pristine again when an edit returns from B to the observed A", async () => {
    mockStudioApi.getUserLlmSettings
      .mockResolvedValueOnce(createExactServiceSettings("us-alpha"))
      .mockResolvedValue(createExactServiceSettings("us-gamma"));
    const view = renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent(
        "Shared OpenAI alpha",
      ),
    );
    await selectLlmService("Shared OpenAI beta");
    await selectLlmService("Shared OpenAI alpha");

    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
    await act(async () => {
      await view.queryClient.invalidateQueries({
        queryKey: ["settings", "user-llm-settings"],
      });
    });

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent(
        "Shared OpenAI gamma",
      ),
    );
  });

  it("keeps an accepted exact target pending until identity and model are observed", async () => {
    mockStudioApi.getUserLlmSettings
      .mockResolvedValueOnce(
        createExactServiceSettings("us-alpha", "gpt-alpha", {
          modelGroupsByRoute: [],
        }),
      )
      .mockResolvedValueOnce(
        createExactServiceSettings("us-beta", "gpt-alpha", {
          modelGroupsByRoute: [],
        }),
      )
      .mockResolvedValue(
        createExactServiceSettings("us-beta", "gpt-beta", {
          modelGroupsByRoute: [],
        }),
      );
    const view = renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent(
        "Shared OpenAI alpha",
      ),
    );
    await selectLlmService("Shared OpenAI beta");
    await selectDefaultModel("gpt-beta");
    expect(selectedDefaultModelElement()).toHaveValue("gpt-beta");
    fireEvent.click(screen.getByRole("button", { name: "Save config" }));

    expect(
      await screen.findByText(
        "Save accepted for Shared OpenAI beta. Waiting for the exact service and model to be observed.",
      ),
    ).toBeTruthy();
    expect(selectedLlmServiceElement()).toHaveTextContent("Shared OpenAI beta");
    expect(selectedDefaultModelElement()).toHaveValue("gpt-beta");
    expect(screen.getByRole("button", { name: "Save config" })).toBeEnabled();

    await act(async () => {
      await view.queryClient.invalidateQueries({
        queryKey: ["settings", "user-llm-settings"],
      });
    });

    await waitFor(() =>
      expect(
        screen.queryByText(
          "Save accepted for Shared OpenAI beta. Waiting for the exact service and model to be observed.",
        ),
      ).toBeNull(),
    );
    expect(selectedLlmServiceElement()).toHaveTextContent("Shared OpenAI beta");
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
  });

  it("does not let an older settings refetch replace an observed save", async () => {
    const initial = createExactServiceSettings("us-alpha", "gpt-alpha");
    const committed = createExactServiceSettings("us-beta", "gpt-beta");
    const staleRefetch = createDeferred<ReturnType<typeof createExactServiceSettings>>();
    mockStudioApi.getUserLlmSettings
      .mockResolvedValueOnce(initial)
      .mockReturnValueOnce(staleRefetch.promise)
      .mockResolvedValue(committed);
    const view = renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent(
        "Shared OpenAI alpha",
      ),
    );

    let staleRefetchRequest!: Promise<void>;
    act(() => {
      staleRefetchRequest = view.queryClient.refetchQueries({
        queryKey: ["settings", "user-llm-settings"],
      });
    });
    await waitFor(() =>
      expect(mockStudioApi.getUserLlmSettings).toHaveBeenCalledTimes(2),
    );

    await selectLlmService("Shared OpenAI beta");
    await selectDefaultModel("gpt-beta");
    fireEvent.click(screen.getByRole("button", { name: "Save config" }));

    await waitFor(() =>
      expect(mockStudioApi.getUserLlmSettings).toHaveBeenCalledTimes(3),
    );
    await waitFor(() =>
      expect(screen.queryByText(/Waiting for the exact service and model/)).toBeNull(),
    );
    expect(selectedLlmServiceElement()).toHaveTextContent("Shared OpenAI beta");
    expect(selectedDefaultModelElement()).toHaveTextContent("gpt-beta");

    await act(async () => {
      staleRefetch.resolve(initial);
      await staleRefetchRequest;
    });

    expect(
      view.queryClient.getQueryData(["settings", "user-llm-settings"]),
    ).toMatchObject({
      savedUserServiceId: "us-beta",
      defaultModel: "gpt-beta",
    });
    expect(selectedLlmServiceElement()).toHaveTextContent("Shared OpenAI beta");
    expect(selectedDefaultModelElement()).toHaveTextContent("gpt-beta");
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
  });

  it("does not let an observed B save clear a newer in-flight edit to C", async () => {
    const saveReceipt = createDeferred<{
      accepted: boolean;
      commandId: string;
      ackStage: string;
      actorId: string;
      correlationId: string;
      ackedAtUtc: string;
    }>();
    mockStudioApi.saveUserLlmSettings.mockReturnValueOnce(saveReceipt.promise);
    mockStudioApi.getUserLlmSettings
      .mockResolvedValueOnce(createExactServiceSettings("us-alpha"))
      .mockResolvedValue(createExactServiceSettings("us-beta"));
    renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent(
        "Shared OpenAI alpha",
      ),
    );
    await selectLlmService("Shared OpenAI beta");
    fireEvent.click(screen.getByRole("button", { name: "Save config" }));
    await waitFor(() =>
      expect(mockStudioApi.saveUserLlmSettings).toHaveBeenCalledTimes(1),
    );
    await selectLlmService("Shared OpenAI gamma");

    await act(async () => {
      saveReceipt.resolve({
        accepted: true,
        commandId: "cmd-settings-b",
        ackStage: "accepted_for_dispatch",
        actorId: "user-1",
        correlationId: "corr-settings-b",
        ackedAtUtc: "2026-07-23T09:00:00Z",
      });
      await saveReceipt.promise;
    });

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent(
        "Shared OpenAI gamma",
      ),
    );
    expect(screen.getByRole("button", { name: "Save config" })).toBeEnabled();
  });

  it("does not apply an in-flight PUT failure to a newer edited draft", async () => {
    const saveReceipt = createDeferred<{
      accepted: boolean;
      commandId: string;
      ackStage: string;
      actorId: string;
      correlationId: string;
      ackedAtUtc: string;
    }>();
    mockStudioApi.saveUserLlmSettings.mockReturnValueOnce(saveReceipt.promise);
    mockStudioApi.getUserLlmSettings.mockResolvedValue(
      createExactServiceSettings("us-alpha"),
    );
    renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent(
        "Shared OpenAI alpha",
      ),
    );
    await selectLlmService("Shared OpenAI beta");
    fireEvent.click(screen.getByRole("button", { name: "Save config" }));
    await waitFor(() =>
      expect(mockStudioApi.saveUserLlmSettings).toHaveBeenCalledTimes(1),
    );
    await selectLlmService("Shared OpenAI gamma");

    await act(async () => {
      saveReceipt.reject(new Error("obsolete write failed"));
      try {
        await saveReceipt.promise;
      } catch {
        // The component owns the rejected mutation.
      }
    });

    expect(selectedLlmServiceElement()).toHaveTextContent("Shared OpenAI gamma");
    expect(screen.getByRole("button", { name: "Save config" })).toBeEnabled();
    expect(screen.queryByText("Save failed")).toBeNull();
    expect(screen.queryByText("obsolete write failed")).toBeNull();
  });

  it("uses the current inventory route for an exact saved ID and its model group", async () => {
    const oldRoute = "/api/v1/proxy/s/shared-openai-old";
    const currentRoute = "/api/v1/proxy/s/shared-openai-current";
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        savedRoute: oldRoute,
        savedRouteLabel: "Shared OpenAI alpha",
        savedRouteKind: "nyx_id_user_service",
        savedUserServiceId: "us-alpha",
        savedServiceSlug: "shared-openai",
        effectiveRoute: currentRoute,
        effectiveRouteLabel: "Shared OpenAI alpha",
        defaultModel: "current-model",
        routeOptions: [
          {
            routeValue: gatewayRoute,
            label: "Gateway route option",
            source: "gateway_provider",
            status: "ready",
            allowed: true,
            ready: true,
            userServiceId: null,
            serviceSlug: null,
            defaultModel: null,
            description: null,
          },
          {
            routeValue: currentRoute,
            label: "Shared OpenAI alpha",
            source: "user_service",
            status: "ready",
            allowed: true,
            ready: true,
            userServiceId: "us-alpha",
            serviceSlug: "shared-openai",
            defaultModel: "current-model",
            description: null,
          },
        ],
        modelGroupsByRoute: [
          {
            routeValue: oldRoute,
            groupId: "shared-openai-old",
            label: "Old route models",
            models: ["old-model"],
          },
          {
            routeValue: currentRoute,
            groupId: "us-alpha",
            label: "Current route models",
            models: ["current-model"],
          },
        ],
      }),
    );
    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.mouseDown(
      await screen.findByRole("combobox", { name: "Default model" }),
    );
    expect(
      await screen.findByRole("option", { name: "current-model" }),
    ).toBeTruthy();
    expect(screen.queryByRole("option", { name: "old-model" })).toBeNull();
  });

  it("refreshes the route snapshot when the same exact service moves", async () => {
    const oldRoute = "/api/v1/proxy/s/shared-openai-r1";
    const currentRoute = "/api/v1/proxy/s/shared-openai-r2";
    const settingsAtRoute = (routeValue: string, model: string) => {
      const settings = createExactServiceSettings("us-alpha", "gpt-alpha");
      return {
        ...settings,
        savedRoute: routeValue,
        effectiveRoute: routeValue,
        routeOptions: settings.routeOptions.map(
          (option: { userServiceId?: string | null }) =>
            option.userServiceId === "us-alpha"
              ? { ...option, routeValue }
              : option,
        ),
        modelGroupsByRoute: [
          {
            routeValue,
            groupId: "us-alpha",
            label: "Shared OpenAI alpha",
            models: [model],
          },
        ],
      };
    };
    mockStudioApi.getUserLlmSettings
      .mockResolvedValueOnce(settingsAtRoute(oldRoute, "route-one-model"))
      .mockResolvedValue(settingsAtRoute(currentRoute, "route-two-model"));
    const view = renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent(
        "Shared OpenAI alpha",
      ),
    );
    await act(async () => {
      await view.queryClient.invalidateQueries({
        queryKey: ["settings", "user-llm-settings"],
      });
    });

    fireEvent.mouseDown(
      await screen.findByRole("combobox", { name: "Default model" }),
    );
    expect(
      await screen.findByRole("option", { name: "route-two-model" }),
    ).toBeTruthy();
    expect(screen.queryByRole("option", { name: "route-one-model" })).toBeNull();
  });

  it("treats a saved service without an exact ID as unavailable", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        savedRoute: "/api/v1/proxy/s/openai-team",
        savedRouteLabel: "Legacy OpenAI selection",
        savedRouteKind: "nyx_id_user_service",
        savedUserServiceId: null,
        savedServiceSlug: "openai-team",
        effectiveRoute: gatewayRoute,
        routeFallbackActive: true,
      }),
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    expect(
      await screen.findByText(
        "Saved service identity unavailable. Choose an exact connected service before saving.",
      ),
    ).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
  });

  it("keeps the accepted exact target through a stale catalog and observes it automatically", async () => {
    const initial = createExactServiceSettings("us-alpha", "gpt-alpha");
    const stale = createExactServiceSettings("us-alpha", "gpt-alpha", {
      routeOptions: initial.routeOptions.filter(
        (option: { userServiceId?: string | null }) => option.userServiceId !== "us-beta",
      ),
    });
    mockStudioApi.getUserLlmSettings
      .mockResolvedValueOnce(initial)
      .mockResolvedValueOnce(stale)
      .mockResolvedValue(createExactServiceSettings("us-beta", "gpt-beta"));
    renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent("Shared OpenAI alpha"),
    );
    await selectLlmService("Shared OpenAI beta");
    await selectDefaultModel("gpt-beta");
    jest.useFakeTimers({ now: 0 });
    fireEvent.click(screen.getByRole("button", { name: "Save config" }));

    await act(async () => {
      await Promise.resolve();
      await jest.advanceTimersByTimeAsync(0);
      await jest.advanceTimersByTimeAsync(1);
    });

    expect(screen.getByText(
      "Save accepted for Shared OpenAI beta. Waiting for the exact service and model to be observed.",
    )).toBeTruthy();
    fireEvent.mouseDown(screen.getByRole("combobox", { name: "Preferred LLM service" }));
    expect(screen.getByRole("option", { name: "Shared OpenAI beta (unavailable)" }))
      .toHaveAttribute("aria-disabled", "true");
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();

    await act(async () => {
      await jest.advanceTimersByTimeAsync(250);
    });

    expect(mockStudioApi.getUserLlmSettings).toHaveBeenCalledTimes(3);
    expect(screen.queryByText(/Waiting for the exact service and model/)).toBeNull();
    expect(selectedLlmServiceElement()).toHaveTextContent("Shared OpenAI beta");
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
  });

  it("retains a disabled exact saved service as unavailable", async () => {
    const saved = createExactServiceSettings("us-beta", "gpt-beta");
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce({
      ...saved,
      routeFallbackActive: true,
      routeOptions: saved.routeOptions.map(
        (option: { userServiceId?: string | null }) =>
          option.userServiceId === "us-beta"
            ? {
                ...option,
                allowed: false,
                ready: false,
                status: "unavailable",
              }
            : option,
      ),
    });
    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.mouseDown(
      await screen.findByRole("combobox", { name: "Preferred LLM service" }),
    );

    expect(
      screen.getByRole("option", { name: "Shared OpenAI beta (unavailable)" }),
    ).toHaveAttribute("aria-disabled", "true");
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
  });

  it("observes a blank service model as the exact option platform default", async () => {
    mockStudioApi.getUserLlmSettings
      .mockResolvedValueOnce(createExactServiceSettings("us-alpha", "gpt-alpha"))
      .mockResolvedValueOnce(createExactServiceSettings("us-alpha", "gpt-alpha"))
      .mockResolvedValue(createExactServiceSettings("us-beta", "gpt-beta"));
    renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent("Shared OpenAI alpha"),
    );
    await selectLlmService("Shared OpenAI beta");
    fireEvent.mouseDown(screen.getByLabelText("Default model"));
    expect(screen.getAllByRole("option").map((option) => option.textContent))
      .toContain("Platform default");
    fireEvent.click(screen.getByRole("option", { name: "Platform default" }));
    jest.useFakeTimers({ now: 0 });
    fireEvent.click(screen.getByRole("button", { name: "Save config" }));

    await act(async () => {
      await Promise.resolve();
      await jest.advanceTimersByTimeAsync(0);
      await jest.advanceTimersByTimeAsync(250);
    });

    expect(mockStudioApi.saveUserLlmSettings).toHaveBeenCalledWith({
      userServiceId: "us-beta",
      model: "",
    });
    expect(screen.queryByText(/Waiting for the exact service and model/)).toBeNull();
    expect(selectedDefaultModelElement()).toHaveTextContent("gpt-beta");
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
  });

  it("keeps pending copy bound to the accepted target after a newer edit", async () => {
    mockStudioApi.getUserLlmSettings
      .mockResolvedValueOnce(createExactServiceSettings("us-alpha"))
      .mockResolvedValue(createExactServiceSettings("us-alpha"));
    renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent("Shared OpenAI alpha"),
    );
    await selectLlmService("Shared OpenAI beta");
    fireEvent.click(screen.getByRole("button", { name: "Save config" }));
    await screen.findByText(/Save accepted/);
    await selectLlmService("Shared OpenAI gamma");

    expect(screen.getByText(
      "Save accepted for Shared OpenAI beta. Waiting for the exact service and model to be observed.",
    )).toBeTruthy();
    expect(selectedLlmServiceElement()).toHaveTextContent("Shared OpenAI gamma");
  });

  it("reports accepted_unobserved after the bounded observation window", async () => {
    mockStudioApi.getUserLlmSettings
      .mockResolvedValueOnce(createExactServiceSettings("us-alpha"))
      .mockResolvedValue(createExactServiceSettings("us-alpha"));
    renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent("Shared OpenAI alpha"),
    );
    await selectLlmService("Shared OpenAI beta");
    jest.useFakeTimers({ now: 0 });
    fireEvent.click(screen.getByRole("button", { name: "Save config" }));

    for (const delay of [0, 250, 500, 1000, 2000, 3000, 5000]) {
      await act(async () => {
        await Promise.resolve();
        await jest.advanceTimersByTimeAsync(delay);
      });
    }

    expect(mockStudioApi.getUserLlmSettings).toHaveBeenCalledTimes(8);
    expect(screen.getByText(
      "Save accepted for Shared OpenAI beta. Waiting for the exact service and model to be observed.",
    )).toBeTruthy();
    await act(async () => {
      await jest.advanceTimersByTimeAsync(4_999);
    });
    expect(screen.getByText(
      "Save accepted for Shared OpenAI beta. Waiting for the exact service and model to be observed.",
    )).toBeTruthy();
    await act(async () => {
      await jest.advanceTimersByTimeAsync(1);
    });
    expect(screen.getByText(
      "Save accepted for Shared OpenAI beta, but it has not been observed yet.",
    )).toBeTruthy();
    expect(screen.getByRole("button", { name: "Retry observation" })).toBeEnabled();
    expect(screen.queryByText("Save failed")).toBeNull();
  });
});
