import fs from "node:fs";
import path from "node:path";

type RelayHooks = {
  buildLlmSelectionOptions: (settings: unknown) => Array<{
    kind: string;
    label: string;
    routeValue: string;
    userServiceId: string | null;
    value: string;
  }>;
  renderStep4: () => HTMLElement;
  saveLlm: () => Promise<void>;
  savedLlmSelectionValue: (settings: unknown) => string | null;
  state: Record<string, any>;
};

const relayHtmlPath = path.resolve(
  __dirname,
  "../../../../../agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/channels.html",
);
const gatewayRoute = "/api/v1/llm/gateway/v1";
const sharedRoute = "/api/v1/proxy/s/shared-openai";
const originalFetch = global.fetch;

function loadRelayHooks(): RelayHooks {
  const html = fs.readFileSync(relayHtmlPath, "utf8");
  const script = html.match(/<script>([\s\S]*?)<\/script>/)?.[1];
  if (!script) {
    throw new Error("Relay script not found.");
  }

  const withoutInit = script.slice(0, script.indexOf("/* init */"));
  const executable = withoutInit.replace(
    "const BACKEND_CONSOLE_CONFIG = __BACKEND_CONSOLE_CONFIG__;",
    `const BACKEND_CONSOLE_CONFIG = ${JSON.stringify({
      authority: "https://id.test",
      clientId: "relay-test",
      resources: [],
      scope: "openid profile",
      storageKey: "relay-test",
    })};`,
  );
  const factory = new Function(
    `${executable}\nreturn { buildLlmSelectionOptions, renderStep4, saveLlm, savedLlmSelectionValue, state };`,
  ) as () => RelayHooks;
  return factory();
}

function relaySettings(
  savedUserServiceId: string | null,
  savedRouteKind: string = "nyx_id_user_service",
) {
  const savedRoute = savedRouteKind === "gateway" ? gatewayRoute : sharedRoute;
  return {
    savedRoute,
    savedRouteLabel:
      savedRouteKind === "gateway" ? "Gateway" : "Shared OpenAI",
    savedRouteKind,
    savedUserServiceId,
    savedServiceSlug:
      savedRouteKind === "nyx_id_user_service" ? "shared-openai" : null,
    effectiveRoute: savedRoute,
    effectiveRouteLabel:
      savedRouteKind === "gateway" ? "Gateway" : "Shared OpenAI",
    routeFallbackActive: false,
    fallbackReason: null,
    catalogStatus: "ready",
    defaultModel: "gpt-shared",
    capabilities: {
      canEditRoute: true,
      canEditModel: true,
      canSave: true,
      canRetryCatalog: false,
    },
    routeOptions: [
      {
        routeValue: gatewayRoute,
        label: "Gateway",
        source: "gateway_provider",
        status: "ready",
        allowed: true,
        ready: true,
        userServiceId: null,
        serviceSlug: null,
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
        description: null,
      },
      {
        routeValue: sharedRoute,
        label: "Provider diagnostic",
        source: "provider_diagnostic",
        status: "ready",
        allowed: true,
        ready: true,
        userServiceId: "diag-health",
        serviceSlug: "shared-openai",
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
  };
}

function fetchResponse(status: number, body: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    text: async () => (body === undefined ? "" : JSON.stringify(body)),
  } as Response;
}

function configureDraft(hooks: RelayHooks, settings = relaySettings("us-alpha")) {
  hooks.state.llm = settings;
  hooks.state.llmSel = {
    value: "user-service:us-beta",
    model: "gpt-shared",
  };
  localStorage.setItem(
    "relay-test:token",
    JSON.stringify({ access_token: "relay-access-token" }),
  );
}

function renderStep(hooks: RelayHooks): void {
  document.body.replaceChildren(hooks.renderStep4());
}

describe("NyxID relay owner LLM selection", () => {
  beforeEach(() => {
    document.body.innerHTML = '<div id="app"></div>';
    localStorage.clear();
    jest.clearAllMocks();
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it("keeps duplicate exact IDs and excludes diagnostics from selection", () => {
    const hooks = loadRelayHooks();

    expect(
      hooks
        .buildLlmSelectionOptions(relaySettings("us-alpha"))
        .map((option) => option.value),
    ).toEqual([
      "gateway",
      "user-service:us-alpha",
      "user-service:us-beta",
    ]);
  });

  it("fails closed for malformed or unknown saved identity", () => {
    const hooks = loadRelayHooks();

    expect(
      hooks.savedLlmSelectionValue(relaySettings(null)),
    ).toBeNull();
    expect(
      hooks.savedLlmSelectionValue(
        relaySettings("us-beta", "future_selection_kind"),
      ),
    ).toBeNull();
    expect(
      hooks.savedLlmSelectionValue({
        ...relaySettings(null, "gateway"),
        savedRoute: sharedRoute,
      }),
    ).toBeNull();
  });

  it("shows failure and never claims saved after a non-2xx response", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(400, { error: "invalid selection" }))
      .mockResolvedValueOnce(fetchResponse(200, relaySettings("us-alpha")));
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();
    renderStep(hooks);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(hooks.state.llmSaved).toBe(false);
    expect(hooks.state.llmSaveState).toBe("failed");
    expect(hooks.state.llmSel.value).toBe("user-service:us-beta");
    expect(document.body).toHaveTextContent("保存失败");
  });

  it("keeps an accepted service intent pending until exact identity is observed", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(fetchResponse(200, relaySettings("us-alpha")));
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();
    renderStep(hooks);

    expect(hooks.state.llmSaved).toBe(false);
    expect(hooks.state.llmSaveState).toBe("accepted");
    expect(hooks.state.llmSel.value).toBe("user-service:us-beta");
    expect(document.body).toHaveTextContent("保存请求已接受");
    expect(document.body).not.toHaveTextContent("已保存 ✓");
  });

  it("claims saved only after the exact service ID is observed", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(fetchResponse(200, relaySettings("us-beta")));
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();
    renderStep(hooks);

    expect(hooks.state.llmSaved).toBe(true);
    expect(hooks.state.llmSaveState).toBe("observed");
    expect(document.body).toHaveTextContent("已保存 ✓");
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({
      userServiceId: "us-beta",
      model: "gpt-shared",
    });
  });

  it("observes Gateway only through typed kind and the canonical route", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks, relaySettings("us-alpha"));
    hooks.state.llmSel = { value: "gateway", model: null };
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(fetchResponse(200, relaySettings(null, "gateway")));
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();

    expect(hooks.state.llmSaved).toBe(true);
    expect(hooks.state.llmSaveState).toBe("observed");
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({
      routeValue: gatewayRoute,
      model: null,
    });
  });
});
