import fs from "node:fs";
import path from "node:path";
import { fireEvent, screen } from "@testing-library/react";

type RelayHooks = {
  buildLlmSelectionOptions: (settings: unknown) => Array<{
    kind: string;
    label: string;
    routeValue: string;
    userServiceId: string | null;
    value: string;
    defaultModel: string | null;
  }>;
  enterWizard: (platformId: string) => void;
  renderStep4: () => HTMLElement;
  loadLlm: (force?: boolean) => Promise<unknown>;
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
let relayHooksToDispose: RelayHooks[] = [];

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
    `${executable}\nreturn { buildLlmSelectionOptions, enterWizard, loadLlm, renderStep4, saveLlm, savedLlmSelectionValue, state };`,
  ) as () => RelayHooks;
  const hooks = factory();
  relayHooksToDispose.push(hooks);
  return hooks;
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
        routeValue: sharedRoute,
        label: "Provider diagnostic",
        source: "provider_diagnostic",
        status: "ready",
        allowed: true,
        ready: true,
        userServiceId: "diag-health",
        serviceSlug: "shared-openai",
        defaultModel: "diagnostic-model",
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

function configureDraft(
  hooks: RelayHooks,
  settings: Record<string, unknown> = relaySettings("us-alpha"),
) {
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
  document.body.innerHTML = '<div id="app"></div>';
  document.getElementById("app")?.appendChild(hooks.renderStep4());
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

describe("NyxID relay owner LLM selection", () => {
  beforeEach(() => {
    document.body.innerHTML = '<div id="app"></div>';
    localStorage.clear();
    jest.clearAllMocks();
  });

  afterEach(() => {
    relayHooksToDispose.forEach((hooks) => {
      hooks.state.llmSaveToken += 1;
    });
    relayHooksToDispose = [];
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
    hooks.state.llmSel.model = "  gpt-new  ";
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(
        fetchResponse(200, {
          ...relaySettings("us-beta"),
          defaultModel: "gpt-new",
        }),
      );
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();
    renderStep(hooks);

    expect(hooks.state.llmSaved).toBe(true);
    expect(hooks.state.llmSaveState).toBe("observed");
    expect(document.body).toHaveTextContent("已保存 ✓");
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
          defaultModel: "gpt-old",
        }),
      );
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();

    expect(hooks.state.llmSaved).toBe(false);
    expect(hooks.state.llmSaveState).toBe("accepted");
  });

  it("does not guess a service default model when the save omits model", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    hooks.state.llmSel.model = null;
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(fetchResponse(200, relaySettings("us-beta")));
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();

    expect(hooks.state.llmSaved).toBe(false);
    expect(hooks.state.llmSaveState).toBe("accepted");
  });

  it("observes Gateway only through typed kind and the canonical route", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks, relaySettings("us-alpha"));
    hooks.state.llmSel = { value: "gateway", model: null };
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(
        fetchResponse(200, {
          ...relaySettings(null, "gateway"),
          defaultModel: "",
        }),
      );
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();

    expect(hooks.state.llmSaved).toBe(true);
    expect(hooks.state.llmSaveState).toBe("observed");
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({
      routeValue: gatewayRoute,
      model: "",
    });
  });

  it("keeps blank Gateway accepted while a non-empty model remains visible", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks, relaySettings("us-alpha"));
    hooks.state.llmSel = { value: "gateway", model: "" };
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(
        fetchResponse(200, {
          ...relaySettings(null, "gateway"),
          defaultModel: "gpt-old",
        }),
      );
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();

    expect(hooks.state.llmSaved).toBe(false);
    expect(hooks.state.llmSaveState).toBe("accepted");
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

  it("observes a blank service model through that exact option default", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    hooks.state.llmSel.model = "";
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(
        fetchResponse(200, {
          ...relaySettings("us-beta"),
          defaultModel: "gpt-beta",
        }),
      );
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();

    expect(hooks.state.llmSaveState).toBe("observed");
    expect(hooks.state.llmSaved).toBe(true);
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({
      userServiceId: "us-beta",
      model: "",
    });
  });

  it("observes a blank service model when the exact option default is null", async () => {
    const hooks = loadRelayHooks();
    const settings = {
      ...relaySettings("us-alpha"),
      routeOptions: relaySettings("us-alpha").routeOptions.map((option) =>
        option.userServiceId === "us-beta"
          ? { ...option, defaultModel: null }
          : option,
      ),
    };
    configureDraft(hooks, settings);
    hooks.state.llmSel.model = "";
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(
        fetchResponse(200, {
          ...settings,
          savedRouteKind: "nyx_id_user_service",
          savedUserServiceId: "us-beta",
          defaultModel: "",
        }),
      );
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();

    expect(hooks.state.llmSaveState).toBe("observed");
    expect(hooks.state.llmSaved).toBe(true);
  });

  it("does not let an older failed save replace a newer observed save status", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const oldWrite = createDeferred<Response>();
    const fetchMock = jest.fn((input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === "PUT") {
        const body = JSON.parse(String(init.body));
        return body.userServiceId === "us-beta"
          ? oldWrite.promise
          : Promise.resolve(fetchResponse(202, { accepted: true }));
      }
      return Promise.resolve(
        fetchResponse(200, {
          ...relaySettings(null, "gateway"),
          defaultModel: "",
        }),
      );
    });
    global.fetch = fetchMock as typeof global.fetch;

    const oldSave = hooks.saveLlm();
    renderStep(hooks);
    fireEvent.change(screen.getByLabelText("LLM 服务"), {
      target: { value: "gateway" },
    });
    hooks.state.llmSel.model = "";
    await hooks.saveLlm();
    expect(hooks.state.llmSaveState).toBe("observed");

    oldWrite.resolve(fetchResponse(400, { error: "old write failed" }));
    await oldSave;

    expect(hooks.state.llmSel.value).toBe("gateway");
    expect(hooks.state.llmSaveState).toBe("observed");
    expect(hooks.state.llmSaved).toBe(true);
  });

  it("does not apply an in-flight PUT failure to a newer edited draft", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const write = createDeferred<Response>();
    global.fetch = jest.fn().mockReturnValueOnce(write.promise) as typeof global.fetch;
    const warn = jest.spyOn(console, "warn").mockImplementation(() => undefined);

    const saving = hooks.saveLlm();
    renderStep(hooks);
    fireEvent.change(screen.getByLabelText("LLM 服务"), {
      target: { value: "gateway" },
    });
    write.reject(new Error("old write failed"));
    await saving;
    renderStep(hooks);

    expect(hooks.state.llmSel.value).toBe("gateway");
    expect(hooks.state.llmSaveState).toBe("idle");
    expect(hooks.state.llmSaveError).toBeNull();
    expect(document.body).not.toHaveTextContent("保存失败");
    warn.mockRestore();
  });

  it("keeps observing an accepted target after an edit without marking the edit saved", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const oldWrite = createDeferred<Response>();
    const fetchMock = jest
      .fn()
      .mockReturnValueOnce(oldWrite.promise)
      .mockResolvedValueOnce(fetchResponse(200, relaySettings("us-beta")));
    global.fetch = fetchMock as typeof global.fetch;

    const saving = hooks.saveLlm();
    renderStep(hooks);
    fireEvent.change(screen.getByLabelText("LLM 服务"), {
      target: { value: "gateway" },
    });
    oldWrite.resolve(fetchResponse(202, { accepted: true }));
    await saving;
    renderStep(hooks);

    expect(hooks.state.llmSel.value).toBe("gateway");
    expect(hooks.state.llmSaved).toBe(false);
    expect(document.body).toHaveTextContent("Shared OpenAI beta");
  });

  it("retains a newer third-service draft when observation omits it", async () => {
    const hooks = loadRelayHooks();
    const initial = relaySettings("us-alpha");
    configureDraft(hooks, {
      ...initial,
      routeOptions: [
        ...initial.routeOptions,
        {
          routeValue: sharedRoute,
          label: "Shared OpenAI gamma",
          source: "user_service",
          status: "ready",
          allowed: true,
          ready: true,
          userServiceId: "us-gamma",
          serviceSlug: "shared-openai",
          defaultModel: "gpt-gamma",
          description: null,
        },
      ],
    });
    const readStarted = createDeferred<void>();
    const observationRead = createDeferred<Response>();
    global.fetch = jest.fn((_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === "PUT") {
        return Promise.resolve(fetchResponse(202, { accepted: true }));
      }
      readStarted.resolve();
      return observationRead.promise;
    }) as typeof global.fetch;

    const saving = hooks.saveLlm();
    await readStarted.promise;
    expect(hooks.state.llmSaveState).toBe("accepted");
    renderStep(hooks);
    fireEvent.change(screen.getByLabelText("LLM 服务"), {
      target: { value: "user-service:us-gamma" },
    });
    observationRead.resolve(fetchResponse(200, relaySettings("us-alpha")));
    await saving;
    renderStep(hooks);

    expect(hooks.state.llmSel.value).toBe("user-service:us-gamma");
    expect(
      screen.getByRole("option", { name: "Shared OpenAI gamma（暂不可用）" }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", { name: /保存回复模型|重新确认保存/ }),
    ).toBeDisabled();
  });

  it("retains the accepted exact target when a stale catalog omits it", async () => {
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const stale = {
      ...relaySettings("us-alpha"),
      routeOptions: relaySettings("us-alpha").routeOptions.filter(
        (option) => option.userServiceId !== "us-beta",
      ),
    };
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(202, { accepted: true }))
      .mockResolvedValueOnce(fetchResponse(200, stale));
    global.fetch = fetchMock as typeof global.fetch;

    await hooks.saveLlm();
    renderStep(hooks);

    expect(hooks.state.llmSel).toEqual({
      value: "user-service:us-beta",
      model: "gpt-shared",
    });
    const retained = screen.getByRole("option", { name: "Shared OpenAI beta（暂不可用）" });
    expect(retained).toBeDisabled();
    expect(screen.getByRole("button", { name: /保存回复模型|重新确认保存/ }))
      .toBeDisabled();
  });

  it("keeps the last good catalog after a transient observation read failure", async () => {
    const hooks = loadRelayHooks();
    const initial = relaySettings("us-alpha");
    hooks.state.llm = initial;
    localStorage.setItem(
      "relay-test:token",
      JSON.stringify({ access_token: "relay-access-token" }),
    );
    const fetchMock = jest.fn().mockRejectedValueOnce(new Error("catalog unavailable"));
    global.fetch = fetchMock as typeof global.fetch;
    const warn = jest.spyOn(console, "warn").mockImplementation(() => undefined);

    await expect(hooks.loadLlm(true)).resolves.toBe(initial);

    expect(hooks.state.llm).toBe(initial);
    warn.mockRestore();
  });

  it("keeps the last good catalog when a read has no decodable payload", async () => {
    const hooks = loadRelayHooks();
    const initial = relaySettings("us-alpha");
    hooks.state.llm = initial;
    localStorage.setItem(
      "relay-test:token",
      JSON.stringify({ access_token: "relay-access-token" }),
    );
    global.fetch = jest
      .fn()
      .mockResolvedValueOnce(fetchResponse(200, undefined)) as typeof global.fetch;

    await expect(hooks.loadLlm(true)).resolves.toBe(initial);
    expect(hooks.state.llm).toBe(initial);
  });

  it("keeps the relay draft revision monotonic across wizard resets", () => {
    const hooks = loadRelayHooks();
    hooks.state.llmDraftRevision = 9;

    hooks.enterWizard("lark");

    expect(hooks.state.llmDraftRevision).toBe(10);
  });

  it("stops after the exact bounded observation schedule as accepted_unobserved", async () => {
    jest.useFakeTimers({ now: 0 });
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const getTimes: number[] = [];
    const fetchMock = jest.fn((_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === "PUT") {
        return Promise.resolve(fetchResponse(202, { accepted: true }));
      }
      getTimes.push(Date.now());
      return Promise.resolve(fetchResponse(200, relaySettings("us-alpha")));
    });
    global.fetch = fetchMock as typeof global.fetch;

    const saving = hooks.saveLlm();
    for (const delay of [0, 250, 500, 1000, 2000, 3000, 5000]) {
      await jest.advanceTimersByTimeAsync(delay);
    }
    await saving;
    expect(hooks.state.llmSaveState).toBe("accepted");
    await jest.advanceTimersByTimeAsync(5_000);
    renderStep(hooks);

    expect(getTimes).toEqual([0, 250, 750, 1750, 3750, 6750, 11750]);
    expect(hooks.state.llmSaveState).toBe("accepted_unobserved");
    expect(hooks.state.llmSaved).toBe(false);
    expect(document.body).toHaveTextContent("Shared OpenAI beta");
    expect(screen.getByRole("button", { name: "重新检查" })).toBeEnabled();
    expect(document.body).not.toHaveTextContent("保存失败");
  });

  it("allows the seventh relay observation attempt to succeed during its settle window", async () => {
    jest.useFakeTimers({ now: 0 });
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const reads: Array<ReturnType<typeof createDeferred<Response>>> = [];
    global.fetch = jest.fn((_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === "PUT") {
        return Promise.resolve(fetchResponse(202, { accepted: true }));
      }
      const read = createDeferred<Response>();
      reads.push(read);
      return read.promise;
    }) as typeof global.fetch;

    const saving = hooks.saveLlm();
    for (const delay of [0, 250, 500, 1000, 2000, 3000, 5000]) {
      await jest.advanceTimersByTimeAsync(delay);
    }
    expect(reads).toHaveLength(7);
    expect(hooks.state.llmSaveState).toBe("accepted");

    reads[6]?.resolve(fetchResponse(200, relaySettings("us-beta")));
    await saving;

    expect(hooks.state.llmSaveState).toBe("observed");
    expect(hooks.state.llmSaved).toBe(true);
  });

  it("reaches the fixed deadline when observation GETs hang", async () => {
    jest.useFakeTimers({ now: 0 });
    const hooks = loadRelayHooks();
    configureDraft(hooks);
    const getTimes: number[] = [];
    const signals: AbortSignal[] = [];
    const reads: Array<ReturnType<typeof createDeferred<Response>>> = [];
    global.fetch = jest.fn((_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === "PUT") {
        return Promise.resolve(fetchResponse(202, { accepted: true }));
      }
      getTimes.push(Date.now());
      if (init?.signal) signals.push(init.signal);
      const read = createDeferred<Response>();
      reads.push(read);
      return read.promise;
    }) as typeof global.fetch;

    let savingSettled = false;
    const saving = hooks.saveLlm().then(() => {
      savingSettled = true;
    });
    await jest.advanceTimersByTimeAsync(0);
    expect(signals).toHaveLength(1);
    expect(signals[0]?.aborted).toBe(false);

    await jest.advanceTimersByTimeAsync(250);
    expect(signals).toHaveLength(2);
    expect(signals[0]?.aborted).toBe(true);
    expect(signals[1]?.aborted).toBe(false);

    await jest.advanceTimersByTimeAsync(11_500);

    expect(getTimes).toEqual([0, 250, 750, 1750, 3750, 6750, 11750]);
    expect(savingSettled).toBe(false);
    expect(hooks.state.llmSaveState).toBe("accepted");
    expect(signals).toHaveLength(7);
    expect(signals.slice(0, 6).every((signal) => signal.aborted)).toBe(true);
    expect(signals[6]?.aborted).toBe(false);

    await jest.advanceTimersByTimeAsync(4_999);
    expect(savingSettled).toBe(false);
    expect(signals[6]?.aborted).toBe(false);
    await jest.advanceTimersByTimeAsync(1);

    await saving;
    expect(hooks.state.llmSaveState).toBe("accepted_unobserved");
    expect(signals).toHaveLength(7);
    expect(signals.every((signal) => signal.aborted)).toBe(true);
    const lastGoodCatalog = hooks.state.llm;
    reads[0]?.resolve(
      fetchResponse(200, {
        ...relaySettings("us-beta"),
        defaultModel: "gpt-shared",
      }),
    );
    const warn = jest.spyOn(console, "warn").mockImplementation(() => undefined);
    reads[1]?.reject(new Error("late observation failure"));
    await Promise.resolve();
    await Promise.resolve();
    expect(hooks.state.llm).toBe(lastGoodCatalog);
    expect(hooks.state.llmSaveState).toBe("accepted_unobserved");
    expect(warn).not.toHaveBeenCalled();
    warn.mockRestore();
  });
});
