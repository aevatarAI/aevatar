import { runtimeGAgentApi } from "./runtimeGAgentApi";
import { persistAuthSession } from "@/shared/auth/session";

describe("runtimeGAgentApi", () => {
  const originalFetch = global.fetch;

  beforeEach(() => {
    window.localStorage.clear();
    jest.spyOn(Date, "now").mockReturnValue(1_700_000_000_000);
    persistAuthSession({
      tokens: {
        accessToken: "access-token",
        tokenType: "Bearer",
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: "user-1",
      },
    });
  });

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
    window.localStorage.clear();
  });

  it("loads discovered GAgent types from the scope capability endpoint", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => [
        {
          typeName: "OrdersGAgent",
          fullName: "Tests.OrdersGAgent",
          assemblyName: "Tests",
        },
      ],
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(runtimeGAgentApi.listTypes()).resolves.toEqual([
      {
        typeName: "OrdersGAgent",
        fullName: "Tests.OrdersGAgent",
        assemblyName: "Tests",
      },
    ]);

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe("/api/scopes/gagent-types");
    expect(new Headers(init?.headers).get("Authorization")).toBe(
      "Bearer access-token"
    );
  });

  it("loads saved actors for the current scope", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => [
        {
          gAgentType: "Tests.OrdersGAgent",
          actorIds: ["orders-1", "orders-2"],
        },
      ],
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(runtimeGAgentApi.listActors("scope-1")).resolves.toEqual([
      {
        gAgentType: "Tests.OrdersGAgent",
        actorIds: ["orders-1", "orders-2"],
      },
    ]);

    const [input] = fetchMock.mock.calls[0] as [string, RequestInit | undefined];
    expect(input).toBe("/api/scopes/scope-1/gagent-actors");
  });

  it("persists a saved actor for the current scope", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: async () => "",
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      runtimeGAgentApi.addActor("scope-1", "Tests.OrdersGAgent", "orders-3")
    ).resolves.toBeUndefined();

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe("/api/scopes/scope-1/gagent-actors");
    expect(init?.method).toBe("POST");
    expect(new Headers(init?.headers).get("Authorization")).toBe(
      "Bearer access-token"
    );
    expect(JSON.parse(String(init?.body))).toEqual({
      gagentType: "Tests.OrdersGAgent",
      actorId: "orders-3",
    });
  });

  it("removes a saved actor for the current scope", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: async () => "",
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      runtimeGAgentApi.removeActor("scope-1", "Tests.OrdersGAgent", "orders-3")
    ).resolves.toBeUndefined();

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe(
      "/api/scopes/scope-1/gagent-actors/orders-3?gagentType=Tests.OrdersGAgent"
    );
    expect(init?.method).toBe("DELETE");
    expect(new Headers(init?.headers).get("Authorization")).toBe(
      "Bearer access-token"
    );
  });

  it("routes draft runs through the scope GAgent draft endpoint", async () => {
    const controller = new AbortController();
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await runtimeGAgentApi.streamDraftRun(
      "scope-1",
      {
        actorTypeName: "Tests.OrdersGAgent, Tests",
        prompt: "hello agent",
        preferredActorId: "orders-1",
      },
      controller.signal
    );

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe("/api/scopes/scope-1/gagent/draft-run");
    expect(init?.method).toBe("POST");
    expect(new Headers(init?.headers).get("Accept")).toBe("text/event-stream");
    expect(new Headers(init?.headers).get("Authorization")).toBe(
      "Bearer access-token"
    );
    expect(JSON.parse(String(init?.body))).toEqual({
      actorTypeName: "Tests.OrdersGAgent, Tests",
      prompt: "hello agent",
      preferredActorId: "orders-1",
    });
    expect(init?.signal).toBe(controller.signal);
  });

  it("loads the authoritative scope binding with GAgent revision details", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        Available: true,
        ScopeId: "scope-1",
        ServiceId: "service-1",
        DisplayName: "Orders Assistant",
        ServiceKey: "default",
        DefaultServingRevisionId: "rev-2",
        ActiveServingRevisionId: "rev-2",
        DeploymentId: "deploy-1",
        DeploymentStatus: "Ready",
        PrimaryActorId: "orders-actor",
        UpdatedAt: "2026-03-31T08:00:00Z",
        Revisions: [
          {
            RevisionId: "rev-2",
            ImplementationKind: 3,
            Status: "Ready",
            ArtifactHash: "artifact-1",
            FailureReason: "",
            IsDefaultServing: true,
            IsActiveServing: true,
            IsServingTarget: true,
            AllocationWeight: 100,
            ServingState: "Ready",
            DeploymentId: "deploy-1",
            PrimaryActorId: "orders-actor",
            CreatedAt: "2026-03-31T07:00:00Z",
            PreparedAt: "2026-03-31T07:10:00Z",
            PublishedAt: "2026-03-31T07:20:00Z",
            RetiredAt: null,
            WorkflowName: "",
            WorkflowDefinitionActorId: "",
            InlineWorkflowCount: 0,
            ScriptId: "",
            ScriptRevision: "",
            ScriptDefinitionActorId: "",
            ScriptSourceHash: "",
            StaticActorTypeName: "Tests.OrdersGAgent, Tests",
            StaticPreferredActorId: "orders-actor",
          },
        ],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(runtimeGAgentApi.getScopeBinding("scope-1")).resolves.toEqual({
      available: true,
      scopeId: "scope-1",
      serviceId: "service-1",
      displayName: "Orders Assistant",
      serviceKey: "default",
      defaultServingRevisionId: "rev-2",
      activeServingRevisionId: "rev-2",
      deploymentId: "deploy-1",
      deploymentStatus: "Ready",
      primaryActorId: "orders-actor",
      updatedAt: "2026-03-31T08:00:00Z",
      revisions: [
        expect.objectContaining({
          revisionId: "rev-2",
          implementationKind: "gagent",
          staticActorTypeName: "Tests.OrdersGAgent, Tests",
          staticPreferredActorId: "orders-actor",
          isDefaultServing: true,
          isActiveServing: true,
        }),
      ],
    });

    const [input] = fetchMock.mock.calls[0] as [string, RequestInit | undefined];
    expect(input).toBe("/api/scopes/scope-1/binding");
  });

  it("publishes a GAgent binding revision through the scope binding endpoint", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        scopeId: "scope-1",
        serviceId: "service-1",
        displayName: "Orders Assistant",
        revisionId: "rev-3",
        implementationKind: "gagent",
        targetName: "Orders Assistant",
        expectedActorId: "orders-actor",
        gAgent: {
          actorTypeName: "Tests.OrdersGAgent, Tests",
          preferredActorId: "orders-actor",
        },
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      runtimeGAgentApi.bindScopeGAgent({
        scopeId: "scope-1",
        displayName: "Orders Assistant",
        actorTypeName: "Tests.OrdersGAgent, Tests",
        preferredActorId: "orders-actor",
        revisionId: "rev-3",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            requestTypeUrl: "type.googleapis.com/google.protobuf.StringValue",
            description: "Chat with the orders assistant.",
          },
        ],
      })
    ).resolves.toEqual({
      scopeId: "scope-1",
      serviceId: "service-1",
      displayName: "Orders Assistant",
      revisionId: "rev-3",
      implementationKind: "gagent",
      targetName: "Orders Assistant",
      expectedActorId: "orders-actor",
      gAgent: {
        actorTypeName: "Tests.OrdersGAgent, Tests",
        preferredActorId: "orders-actor",
      },
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe("/api/scopes/scope-1/binding");
    expect(init?.method).toBe("PUT");
    expect(JSON.parse(String(init?.body))).toEqual({
      implementationKind: "gagent",
      displayName: "Orders Assistant",
      revisionId: "rev-3",
      gagent: {
        actorTypeName: "Tests.OrdersGAgent, Tests",
        preferredActorId: "orders-actor",
        endpoints: [
          {
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            requestTypeUrl:
              "type.googleapis.com/google.protobuf.StringValue",
            responseTypeUrl: undefined,
            description: "Chat with the orders assistant.",
          },
        ],
      },
    });
  });

  it("activates and retires GAgent binding revisions", async () => {
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          scopeId: "scope-1",
          serviceId: "service-1",
          displayName: "Orders Assistant",
          revisionId: "rev-3",
        }),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          scopeId: "scope-1",
          serviceId: "service-1",
          revisionId: "rev-2",
          status: "Retired",
        }),
      } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      runtimeGAgentApi.activateScopeBindingRevision("scope-1", "rev-3")
    ).resolves.toEqual({
      scopeId: "scope-1",
      serviceId: "service-1",
      displayName: "Orders Assistant",
      revisionId: "rev-3",
    });

    await expect(
      runtimeGAgentApi.retireScopeBindingRevision("scope-1", "rev-2")
    ).resolves.toEqual({
      scopeId: "scope-1",
      serviceId: "service-1",
      revisionId: "rev-2",
      status: "Retired",
    });

    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      "/api/scopes/scope-1/binding/revisions/rev-3:activate"
    );
    expect(fetchMock.mock.calls[1]?.[0]).toBe(
      "/api/scopes/scope-1/binding/revisions/rev-2:retire"
    );
    expect(fetchMock.mock.calls[0]?.[1]?.method).toBe("POST");
    expect(fetchMock.mock.calls[1]?.[1]?.method).toBe("POST");
  });
});
