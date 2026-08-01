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
  resetLlmSaveOperation: () => void;
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
const relayObservationIntervalMs = 1_500;
const relayObservationMaxAttempts = 4;
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
    `${executable}\nreturn { buildLlmSelectionOptions, renderStep4, resetLlmSaveOperation: typeof resetLlmSaveOperation === "function" ? resetLlmSaveOperation : () => {}, saveLlm, savedLlmSelectionValue, state };`,
  ) as () => RelayHooks;
  return factory();
}

function relaySettings(
  savedUserServiceId: string | null,
  savedRouteKind: string = "nyx_id_user_service",
) {
  const savedRoute = savedRouteKind === "gateway" ? gatewayRoute : sharedRoute;
  return {
    userConfigStateVersion: 10,
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
        defaultModel: null,
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
        defaultModel: "gpt-shared",
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
        defaultModel: "gpt-shared",
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
        defaultModel: null,
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

function relayObservation(
  savedUserServiceId: string | null,
  userConfigStateVersion: number,
  defaultModel = "gpt-shared",
  savedRouteKind: string = "nyx_id_user_service",
) {
  return {
    userConfigStateVersion,
    savedRoute: savedRouteKind === "gateway" ? gatewayRoute : sharedRoute,
    savedRouteKind,
    savedUserServiceId,
    defaultModel,
  };
}

function fetchResponse(status: number, body: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    text: async () => (body === undefined ? "" : JSON.stringify(body)),
  } as Response;
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

async function flushAsyncWork(): Promise<void> {
  for (let index = 0; index < 6; index += 1) {
    await Promise.resolve();
  }
}

function configureDraft(hooks: RelayHooks, settings = relaySettings("us-alpha")) {
  hooks.state.llm = {
    ...settings,
    routeOptions: [
      ...settings.routeOptions,
      {
        routeValue: sharedRoute,
        defaultModel: "gpt-shared",
        label: "Shared OpenAI gamma",
        source: "user_service",
        status: "ready",
        allowed: true,
        ready: true,
        userServiceId: "us-gamma",
        serviceSlug: "shared-openai",
        description: null,
      },
    ],
  };
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
    jest.useRealTimers();
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

  it("treats a 2xx receipt without accepted true as failed", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: false }))
      .mockResolvedValueOnce(fetchResponse(200, relaySettings("us-beta")));
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(hooks.state.llmSaved).toBe(false);
    expect(hooks.state.llmSaveState).toBe("failed");
  });

  it("keeps an accepted service intent pending until the target setting is visible", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(fetchResponse(200, relayObservation("us-alpha", 10)));
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();
    renderStep(hooks);

    expect(hooks.state.llmSaved).toBe(false);
    expect(hooks.state.llmSaveState).toBe("accepted");
    expect(hooks.state.llmSel.value).toBe("user-service:us-beta");
    expect(document.body).toHaveTextContent("保存请求已接受");
    expect(document.body).not.toHaveTextContent("目标设置已可见 ✓");
  });

  it("claims the target setting visible only after exact identity, model, and version match", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    hooks.state.llmSel.model = "  gpt-new  ";
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(fetchResponse(200, relayObservation("us-beta", 11, "gpt-new")));
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();
    renderStep(hooks);

    expect(hooks.state.llmSaved).toBe(true);
    expect(hooks.state.llmSaveState).toBe("observed");
    expect(document.body).toHaveTextContent("目标设置已可见 ✓");
    expect(fetchMock.mock.calls[1][0]).toBe("/api/user-config/llm/observation");
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({
      userServiceId: "us-beta",
      model: "gpt-new",
    });
  });

  it("keeps an exact service accepted while its old model is still observed", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    hooks.state.llmSel.model = "gpt-new";
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(
        fetchResponse(200, {
          ...relaySettings("us-beta"),
          userConfigStateVersion: 11,
          defaultModel: "gpt-old",
        }),
      );
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();

    expect(hooks.state.llmSaved).toBe(false);
    expect(hooks.state.llmSaveState).toBe("accepted");
  });

  it("observes the real service default after the read-model version advances", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    hooks.state.llmSel.model = null;
    hooks.state.llm.routeOptions = hooks.state.llm.routeOptions.map((option: any) =>
      option.userServiceId === "us-beta"
        ? { ...option, defaultModel: "platform-beta" }
        : option,
    );
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(fetchResponse(200, {
        ...relaySettings("us-beta"),
        userConfigStateVersion: 11,
        defaultModel: "platform-beta",
      }));
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();

    expect(hooks.state.llmSaved).toBe(true);
    expect(hooks.state.llmSaveState).toBe("observed");
    expect(hooks.state.llmSel.model).toBe("platform-beta");
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({
      userServiceId: "us-beta",
      model: "platform-beta",
    });
    expect(fetchMock.mock.calls[1][0]).toBe("/api/user-config/llm/observation");
  });

  it("observes Gateway only through typed kind and the canonical route", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks, relaySettings("us-alpha"));
    hooks.state.llmSel = { value: "gateway", model: null };
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(fetchResponse(200, {
        ...relaySettings(null, "gateway"),
        userConfigStateVersion: 11,
      }));
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();

    expect(hooks.state.llmSaved).toBe(true);
    expect(hooks.state.llmSaveState).toBe("observed");
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({
      routeValue: gatewayRoute,
      model: null,
    });
  });

  it("keeps Gateway accepted while an explicit model change is stale", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks, relaySettings("us-alpha"));
    hooks.state.llmSel = { value: "gateway", model: "gpt-new" };
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(
        fetchResponse(200, {
          ...relaySettings(null, "gateway"),
          userConfigStateVersion: 11,
          defaultModel: "gpt-old",
        }),
      );
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();

    expect(hooks.state.llmSaved).toBe(false);
    expect(hooks.state.llmSaveState).toBe("accepted");
  });

  it("observes Gateway after its explicit normalized model is visible", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks, relaySettings("us-alpha"));
    hooks.state.llmSel = { value: "gateway", model: "  gpt-new  " };
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(
        fetchResponse(200, {
          ...relaySettings(null, "gateway"),
          userConfigStateVersion: 11,
          defaultModel: "gpt-new",
        }),
      );
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();

    expect(hooks.state.llmSaved).toBe(true);
    expect(hooks.state.llmSaveState).toBe("observed");
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({
      routeValue: gatewayRoute,
      model: "gpt-new",
    });
  });

  it("preserves the observed model on initial load and defaults only after a service switch", () => {
    const hooks = loadRelayHooks();
    const betaRoute = "/api/v1/proxy/s/beta-openai";
    const settings = {
      ...relaySettings("us-alpha"),
      defaultModel: "gpt-saved",
      routeOptions: relaySettings("us-alpha").routeOptions.map((option) =>
        option.userServiceId === "us-beta"
          ? { ...option, routeValue: betaRoute }
          : option,
      ),
      modelGroupsByRoute: [
        {
          routeValue: sharedRoute,
          groupId: "shared-openai",
          label: "Shared OpenAI",
          models: ["gpt-first", "gpt-saved"],
        },
        {
          routeValue: betaRoute,
          groupId: "beta-openai",
          label: "Beta OpenAI",
          models: ["beta-first"],
        },
      ],
    };
    hooks.state.llm = settings;
    hooks.state.llmSel = { value: null, model: null };

    renderStep(hooks);

    expect(hooks.state.llmSel.model).toBe("gpt-saved");

    hooks.state.llmSel = { value: "user-service:us-beta", model: null };
    renderStep(hooks);

    expect(hooks.state.llmSel.model).toBe("beta-first");
  });

  it("does not observe an exact identity and model at the pre-save state version", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(fetchResponse(200, relaySettings("us-beta")));
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();

    expect(hooks.state.llmSaved).toBe(false);
    expect(hooks.state.llmSaveState).toBe("accepted");
  });

  it("keeps the accepted target through a stale catalog and observes it on a later timer tick", async () => {
    jest.useFakeTimers();
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    hooks.state.llmSel.model = "gpt-new";
    const staleWithoutBeta = {
      ...relaySettings("us-alpha"),
      routeOptions: relaySettings("us-alpha").routeOptions.filter(
        (option) => option.userServiceId !== "us-beta",
      ),
    };
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(fetchResponse(200, relayObservation("us-alpha", 10)))
      .mockResolvedValueOnce(fetchResponse(200, relayObservation("us-beta", 11, "gpt-new")));
    global.fetch = fetchMock as typeof global.fetch;

    try {
      await hooks.saveLlm();
      hooks.state.llm = staleWithoutBeta;
      renderStep(hooks);

      expect(hooks.state.llmSaveState).toBe("accepted");
      expect(hooks.state.llmPendingSave).toMatchObject({
        baseUserConfigStateVersion: 10,
        target: {
          label: "Shared OpenAI beta",
          model: "gpt-new",
          value: "user-service:us-beta",
        },
      });
      expect(hooks.state.llmSel.value).toBe("user-service:us-beta");
      expect(document.body).toHaveTextContent(
        "保存目标 Shared OpenAI beta / gpt-new 的设置尚未可见",
      );
      expect(fetchMock.mock.calls[1][0]).toBe("/api/user-config/llm/observation");

      await jest.advanceTimersByTimeAsync(relayObservationIntervalMs);
      await flushAsyncWork();

      expect(fetchMock).toHaveBeenCalledTimes(3);
      expect(hooks.state.llmSaveState).toBe("observed");
      expect(hooks.state.llmSaved).toBe(true);
      expect(hooks.state.llmPendingSave).toBeNull();

      await jest.advanceTimersByTimeAsync(relayObservationIntervalMs * 3);
      await flushAsyncWork();
      expect(fetchMock).toHaveBeenCalledTimes(3);
    } finally {
      jest.clearAllTimers();
      jest.useRealTimers();
    }
  });

  it("bounds automatic target checks and keeps the accepted target retryable", async () => {
    jest.useFakeTimers();
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValue(fetchResponse(200, relayObservation("us-beta", 10)));
    global.fetch = fetchMock as typeof global.fetch;

    try {
      await hooks.saveLlm();
      await jest.advanceTimersByTimeAsync(relayObservationIntervalMs * 16);
      await flushAsyncWork();
      renderStep(hooks);

      expect(fetchMock).toHaveBeenCalledTimes(1 + relayObservationMaxAttempts);
      expect(hooks.state.llmSaveState).toBe("accepted");
      expect(hooks.state.llmPendingSave).not.toBeNull();
      expect(document.body).toHaveTextContent("重新检查目标设置");

      await jest.advanceTimersByTimeAsync(relayObservationIntervalMs * 32);
      await flushAsyncWork();
      expect(fetchMock).toHaveBeenCalledTimes(1 + relayObservationMaxAttempts);
    } finally {
      jest.clearAllTimers();
      jest.useRealTimers();
    }
  });

  it("ignores an old accepted PUT after the draft switches to another service", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const put = createDeferred<Response>();
    const fetchMock = jest.fn().mockReturnValueOnce(put.promise);
    global.fetch = fetchMock as typeof global.fetch;

    const savePromise = hooks.saveLlm();
    await flushAsyncWork();
    hooks.state.llmSel = { value: "user-service:us-gamma", model: "gpt-shared" };
    hooks.resetLlmSaveOperation();
    put.resolve(fetchResponse(202, { accepted: true }));
    await savePromise;

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(hooks.state.llmSel.value).toBe("user-service:us-gamma");
    expect(hooks.state.llmSaveState).toBe("idle");
    expect(hooks.state.llmSaved).toBe(false);
  });

  it("ignores an old rejected PUT after the draft switches to another service", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const put = createDeferred<Response>();
    const fetchMock = jest.fn().mockReturnValueOnce(put.promise);
    global.fetch = fetchMock as typeof global.fetch;

    const savePromise = hooks.saveLlm();
    await flushAsyncWork();
    hooks.state.llmSel = { value: "user-service:us-gamma", model: "gpt-shared" };
    hooks.resetLlmSaveOperation();
    put.resolve(fetchResponse(400, { error: "beta rejected" }));
    await savePromise;

    expect(hooks.state.llmSel.value).toBe("user-service:us-gamma");
    expect(hooks.state.llmSaveState).toBe("idle");
    expect(hooks.state.llmSaveError).toBeNull();
  });

  it("ignores an old observation response after the draft switches", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const observation = createDeferred<Response>();
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockReturnValueOnce(observation.promise);
    global.fetch = fetchMock as typeof global.fetch;

    const savePromise = hooks.saveLlm();
    await flushAsyncWork();
    expect(fetchMock).toHaveBeenCalledTimes(2);
    hooks.state.llmSel = { value: "user-service:us-gamma", model: "gpt-shared" };
    hooks.resetLlmSaveOperation();
    observation.resolve(fetchResponse(200, {
      ...relaySettings("us-beta"),
      userConfigStateVersion: 11,
    }));
    await savePromise;

    expect(hooks.state.llmSel.value).toBe("user-service:us-gamma");
    expect(hooks.state.llmSaveState).toBe("idle");
    expect(hooks.state.llmSaved).toBe(false);
  });

  it("clears periodic observation when the pending draft switches", async () => {
    jest.useFakeTimers();
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValue(fetchResponse(200, relaySettings("us-alpha")));
    global.fetch = fetchMock as typeof global.fetch;

    try {
      await hooks.saveLlm();
      hooks.state.llmSel = { value: "user-service:us-gamma", model: "gpt-shared" };
      hooks.resetLlmSaveOperation();

      await jest.advanceTimersByTimeAsync(relayObservationIntervalMs * 2);
      await flushAsyncWork();

      expect(fetchMock).toHaveBeenCalledTimes(2);
      expect(hooks.state.llmSaveState).toBe("idle");
      expect(hooks.state.llmPendingSave).toBeNull();
    } finally {
      jest.clearAllTimers();
      jest.useRealTimers();
    }
  });
});
