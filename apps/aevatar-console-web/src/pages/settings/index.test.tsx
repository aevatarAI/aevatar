import { act, fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { persistAuthSession } from "@/shared/auth/session";
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from "../../../tests/reactQueryTestUtils";
import SettingsPage from "./index";

const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(),
    getUserConfigRuntime: jest.fn(),
    getUserLlmSettings: jest.fn(),
    saveUserLlmSettings: jest.fn(),
  },
}));

jest.mock("@/shared/ui/ConsoleToast", () => ({
  useConsoleToast: () => mockConsoleToast,
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
    savedSelection: {
      routeKind: "gateway",
      routeValue: gatewayRoute,
      modelSelection: { kind: "provider_default" },
    },
    savedRouteLabel: "Backend saved gateway",
    selectionStatus: "ready",
    catalogDiagnostic: "unspecified",
    remediation: "none",
    catalogStatus: "ready",
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
        modelCatalog: {
          certainty: "enumerated",
          modelIds: [
            "claude-3-5-sonnet",
            "claude-3-opus",
            "gpt-4o",
            "gpt-4o-mini",
          ],
          defaultModelId: "gpt-4o",
          diagnostic: "unspecified",
        },
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
        modelCatalog: {
          certainty: "enumerated",
          modelIds: ["gpt-4.1-mini"],
          defaultModelId: "gpt-4.1-mini",
          diagnostic: "unspecified",
        },
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
        modelCatalog: {
          certainty: "enumerated",
          modelIds: ["claude-3-haiku"],
          defaultModelId: "claude-3-haiku",
          diagnostic: "unspecified",
        },
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
  userServiceId: string,
  defaultModel = "gpt-shared",
  overrides: Record<string, unknown> = {},
) {
  const labels: Record<string, string> = {
    "us-alpha": "Shared OpenAI alpha",
    "us-beta": "Shared OpenAI beta",
    "us-gamma": "Shared OpenAI gamma",
  };
  return createLlmSettings({
    savedSelection: {
      routeKind: "nyx_id_user_service",
      routeValue: sharedExactServiceRoute,
      nyxIdUserServiceId: userServiceId,
      serviceSlugSnapshot: "shared-openai",
      modelSelection: defaultModel
        ? { kind: "explicit_model", modelId: defaultModel }
        : { kind: "provider_default" },
    },
    savedRouteLabel: labels[userServiceId],
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
        modelCatalog: {
          certainty: "not_verifiable",
          modelIds: [],
          defaultModelId: null,
          diagnostic: "not_published",
        },
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
        modelCatalog: {
          certainty: "enumerated",
          modelIds: ["gpt-alpha", "gpt-beta", "gpt-gamma", "gpt-shared"],
          defaultModelId: `gpt-${userServiceId.slice(3)}`,
          diagnostic: "unspecified",
        },
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
    expect(screen.getAllByText("Saved selection").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Backend saved gateway").length).toBeGreaterThan(0);
    expect(screen.queryByText("Effective route")).toBeNull();
    expect(screen.getByText("Connected providers")).toBeTruthy();
    expect(screen.getAllByText("OpenAI Team Service").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Default model").length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: /Advanced runtime/i }));
    expect(screen.getByDisplayValue("http://127.0.0.1:5080")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Reset" })).toBeEnabled();
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

  it("reports service access review failures with a toast and keeps retry available", async () => {
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
    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Could not start service access review. Try again.",
      ),
    );
    expect(screen.queryByRole("alert")).toBeNull();
    await waitFor(() => expect(manageButton).not.toHaveClass("ant-btn-loading"));
    fireEvent.click(manageButton);
    await waitFor(() => expect(assign).toHaveBeenCalledTimes(2));
    expect(screen.queryByRole("alert")).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("reports a current settings save failure with a toast", async () => {
    mockStudioApi.saveUserLlmSettings.mockRejectedValue(
      new Error("PUT /api/settings returned 500"),
    );
    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.mouseDown(
      await screen.findByRole("combobox", { name: "Default model" }),
    );
    fireEvent.click(screen.getByRole("option", { name: "gpt-4o" }));
    fireEvent.click(screen.getByRole("button", { name: "Save config" }));

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Settings could not be saved. Try again.",
      ),
    );
    expect(screen.queryByText("Save failed")).toBeNull();
    expect(screen.queryByText("PUT /api/settings returned 500")).toBeNull();
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
        savedSelection: {
          routeKind: "nyx_id_user_service",
          routeValue: "/api/v1/proxy/s/anthropic-team",
          nyxIdUserServiceId: "us-anthropic",
          serviceSlugSnapshot: "anthropic-team",
          modelSelection: {
            kind: "explicit_model",
            modelId: "claude-3-haiku",
          },
        },
        savedRouteLabel: "Anthropic Lab Service",
      })
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() => {
      expect(screen.getByText("1 live")).toBeTruthy();
    });
  });

  it("offers only provider default when the selected catalog is not verifiable", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        savedSelection: {
          routeKind: "nyx_id_user_service",
          routeValue: "/api/v1/proxy/s/anthropic-team",
          nyxIdUserServiceId: "us-anthropic",
          serviceSlugSnapshot: "anthropic-team",
          modelSelection: { kind: "provider_default" },
        },
        savedRouteLabel: "Anthropic Lab Service",
        routeOptions: createLlmSettings().routeOptions.map((option) =>
          option.userServiceId === "us-anthropic"
            ? {
                ...option,
                modelCatalog: {
                  certainty: "not_verifiable",
                  modelIds: [],
                  defaultModelId: null,
                  diagnostic: "not_published",
                },
              }
            : option,
        ),
      })
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    expect(await screen.findByText("Provider default only")).toBeTruthy();
    fireEvent.mouseDown(screen.getByRole("combobox", { name: "Default model" }));
    expect(screen.getByRole("option", { name: "Provider default" })).toBeTruthy();
    expect(screen.queryByRole("option", { name: "claude-3-haiku" })).toBeNull();
  });

  it("retains an unavailable saved selection without switching providers", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        savedSelection: {
          routeKind: "nyx_id_user_service",
          routeValue: "/api/v1/proxy/s/retired-team",
          nyxIdUserServiceId: "us-retired",
          serviceSlugSnapshot: "retired-team",
          modelSelection: {
            kind: "explicit_model",
            modelId: "retired-model",
          },
        },
        savedRouteLabel: "Retired Team",
        selectionStatus: "needs_repair",
        catalogDiagnostic: "route_not_ready",
        remediation: "choose_replacement",
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
            modelCatalog: {
              certainty: "enumerated",
              modelIds: ["gpt-4o"],
              defaultModelId: "gpt-4o",
              diagnostic: "unspecified",
            },
            description: null,
          },
        ],
      })
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.mouseDown(await screen.findByRole("combobox", { name: "Preferred LLM service" }));
    expect(screen.getByRole("option", { name: "Retired Team (unavailable)" }))
      .toHaveAttribute("aria-disabled", "true");
    expect(screen.getByText(
      "Retired Team · retired-model is unavailable. New requests will not switch providers; choose a replacement or reset to System default.",
    )).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
  });

  it("saves canonical LLM settings", async () => {
    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.mouseDown(await screen.findByRole("combobox", { name: "Default model" }));
    fireEvent.click(screen.getByRole("option", { name: "gpt-4o" }));

    fireEvent.click(await screen.findByRole("button", { name: "Save config" }));

    await waitFor(() => {
      expect(mockStudioApi.saveUserLlmSettings).toHaveBeenCalledWith({
        action: "select_gateway",
        gateway: {
          model: { kind: "explicit_model", modelId: "gpt-4o" },
        },
      });
    });
  });

  it("selects and saves duplicate-route services by exact user service ID", async () => {
    const exactSettings = createExactServiceSettings("us-alpha", "gpt-shared");
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      {
        ...exactSettings,
        routeOptions: [
          ...exactSettings.routeOptions,
          {
            routeValue: "/api/v1/proxy/s/diagnostic-only",
            label: "Provider diagnostic",
            source: "provider_diagnostic",
            status: "ready",
            allowed: true,
            ready: true,
            userServiceId: null,
            serviceSlug: "diagnostic-only",
            modelCatalog: {
              certainty: "unavailable",
              modelIds: [],
              defaultModelId: null,
              diagnostic: "observation_unavailable",
            },
            description: "Health only",
          },
        ],
      },
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.mouseDown(
      await screen.findByRole("combobox", { name: "Preferred LLM service" }),
    );
    expect(
      await screen.findByLabelText(
        "Provider diagnostic · Ready · Provider diagnostic",
      ),
    ).toHaveAttribute("role", "status");
    expect(await screen.findByRole("option", { name: "Shared OpenAI alpha" })).toBeTruthy();
    fireEvent.click(screen.getByRole("option", { name: "Shared OpenAI beta" }));
    expect(screen.queryByRole("option", { name: "Provider diagnostic" })).toBeNull();
    await selectDefaultModel("gpt-shared");

    fireEvent.click(await screen.findByRole("button", { name: "Save config" }));

    await waitFor(() => {
      expect(mockStudioApi.saveUserLlmSettings).toHaveBeenCalledWith({
        action: "select_user_service",
        userService: {
          userServiceId: "us-beta",
          model: { kind: "explicit_model", modelId: "gpt-shared" },
        },
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
    await selectDefaultModel("gpt-shared");

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
    const initial = createExactServiceSettings("us-alpha", "gpt-alpha", {
      modelGroupsByRoute: [],
    });
    const routeOnly = createExactServiceSettings("us-beta", "gpt-alpha", {
      modelGroupsByRoute: [],
    });
    const committed = createExactServiceSettings("us-beta", "gpt-beta", {
      modelGroupsByRoute: [],
    });
    let exposeCommitted = false;
    mockStudioApi.getUserLlmSettings
      .mockResolvedValueOnce(initial)
      .mockImplementation(async () =>
        exposeCommitted ? committed : routeOnly,
      );
    const view = renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent(
        "Shared OpenAI alpha",
      ),
    );
    await selectLlmService("Shared OpenAI beta");
    await selectDefaultModel("gpt-beta");
    expect(selectedDefaultModelElement()).toHaveTextContent("gpt-beta");
    fireEvent.click(screen.getByRole("button", { name: "Save config" }));

    expect(
      await screen.findByText(
        "Update submitted · cmd-settings-1",
      ),
    ).toBeTruthy();
    expect(selectedLlmServiceElement()).toHaveTextContent("Shared OpenAI beta");
    expect(selectedDefaultModelElement()).toHaveTextContent("gpt-beta");
    expect(screen.getByText("Save pending")).toBeTruthy();

    exposeCommitted = true;
    await act(async () => {
      await view.queryClient.invalidateQueries({
        queryKey: ["settings", "user-llm-settings"],
      });
    });

    await waitFor(() =>
      expect(
        screen.queryByText(
          "Update submitted · cmd-settings-1",
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
      savedSelection: {
        routeKind: "nyx_id_user_service",
        nyxIdUserServiceId: "us-beta",
        modelSelection: { kind: "explicit_model", modelId: "gpt-beta" },
      },
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

  it("renders System default separately from Gateway", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        savedSelection: {
          routeKind: "unspecified",
          modelSelection: { kind: "unspecified" },
        },
        savedRouteLabel: "System default",
        selectionStatus: "system_default",
      }),
    );
    renderWithQueryClient(React.createElement(SettingsPage));

    expect((await screen.findAllByText("System default")).length).toBeGreaterThan(0);
    expect(screen.queryByText("Backend saved gateway")).toBeNull();
    expect(screen.queryByText("Gateway")).toBeNull();
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
  });

  it("reports verification unavailable without declaring the selection broken", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createExactServiceSettings("us-beta", "gpt-beta", {
        selectionStatus: "verification_unavailable",
        catalogDiagnostic: "observation_unavailable",
        remediation: "retry_catalog",
        capabilities: {
          canEditRoute: false,
          canEditModel: false,
          canSave: false,
          canRetryCatalog: true,
        },
      }),
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    expect(await screen.findByText("Verification unavailable")).toBeTruthy();
    expect(screen.getByText(
      "The exact saved selection is retained. Retry verification before changing it.",
    )).toBeTruthy();
    expect(screen.getByRole("button", { name: "Retry" })).toBeEnabled();
    expect(screen.queryByText("Saved selection needs repair")).toBeNull();
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
  });

  it("resets the complete selection to System default", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValue(
      createExactServiceSettings("us-alpha", "gpt-alpha"),
    );
    renderWithQueryClient(React.createElement(SettingsPage));

    const resetButton = await screen.findByRole("button", { name: "Reset" });
    await waitFor(() => expect(resetButton).toBeEnabled());
    fireEvent.click(resetButton);

    await waitFor(() => {
      expect(mockStudioApi.saveUserLlmSettings).toHaveBeenCalledWith({
        action: "reset",
      });
    });
    expect(await screen.findByText("Update submitted · cmd-settings-1"))
      .toBeTruthy();
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
      "Update submitted · cmd-settings-1",
    )).toBeTruthy();
    fireEvent.mouseDown(screen.getByRole("combobox", { name: "Preferred LLM service" }));
    expect(screen.getByRole("option", { name: "Shared OpenAI beta (unavailable)" }))
      .toHaveAttribute("aria-disabled", "true");
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();

    await act(async () => {
      await jest.advanceTimersByTimeAsync(250);
    });

    expect(mockStudioApi.getUserLlmSettings).toHaveBeenCalledTimes(3);
    expect(screen.queryByText("Update submitted · cmd-settings-1")).toBeNull();
    expect(selectedLlmServiceElement()).toHaveTextContent("Shared OpenAI beta");
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
  });

  it("retains a disabled exact saved service as unavailable", async () => {
    const saved = createExactServiceSettings("us-beta", "gpt-beta");
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce({
      ...saved,
      selectionStatus: "needs_repair",
      catalogDiagnostic: "route_not_ready",
      remediation: "choose_replacement",
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
      screen.getByRole("option", { name: "Shared OpenAI beta" }),
    ).toHaveAttribute("aria-disabled", "true");
    expect(screen.getByText(
      "Shared OpenAI beta · gpt-beta is unavailable. New requests will not switch providers; choose a replacement or reset to System default.",
    )).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
  });

  it("saves Provider default as an explicit model-selection intent", async () => {
    mockStudioApi.getUserLlmSettings
      .mockResolvedValueOnce(createExactServiceSettings("us-alpha", "gpt-alpha"))
      .mockResolvedValueOnce(createExactServiceSettings("us-alpha", "gpt-alpha"))
      .mockResolvedValue(createExactServiceSettings("us-beta", ""));
    renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() =>
      expect(selectedLlmServiceElement()).toHaveTextContent("Shared OpenAI alpha"),
    );
    await selectLlmService("Shared OpenAI beta");
    fireEvent.mouseDown(screen.getByLabelText("Default model"));
    expect(screen.getAllByRole("option").map((option) => option.textContent))
      .toContain("Provider default");
    fireEvent.click(screen.getByRole("option", { name: "Provider default" }));
    jest.useFakeTimers({ now: 0 });
    fireEvent.click(screen.getByRole("button", { name: "Save config" }));

    await act(async () => {
      await Promise.resolve();
      await jest.advanceTimersByTimeAsync(0);
      await jest.advanceTimersByTimeAsync(250);
    });

    expect(mockStudioApi.saveUserLlmSettings).toHaveBeenCalledWith({
      action: "select_user_service",
      userService: {
        userServiceId: "us-beta",
        model: { kind: "provider_default" },
      },
    });
    expect(screen.queryByText("Update submitted · cmd-settings-1")).toBeNull();
    expect(selectedDefaultModelElement()).toHaveTextContent("Provider default");
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
    await screen.findByText("Update submitted · cmd-settings-1");
    await selectLlmService("Shared OpenAI gamma");

    expect(screen.getByText(
      "Update submitted · cmd-settings-1",
    )).toBeTruthy();
    expect(screen.getByText(
      "Waiting for the exact service and model selection to appear.",
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
      "Update submitted · cmd-settings-1",
    )).toBeTruthy();
    await act(async () => {
      await jest.advanceTimersByTimeAsync(4_999);
    });
    expect(screen.getByText(
      "Waiting for the exact service and model selection to appear.",
    )).toBeTruthy();
    await act(async () => {
      await jest.advanceTimersByTimeAsync(1);
    });
    expect(screen.getByText(
      "The exact selection has not been observed yet.",
    )).toBeTruthy();
    expect(screen.getByRole("button", { name: "Retry observation" })).toBeEnabled();
    expect(screen.queryByText("Save failed")).toBeNull();
  });
});
