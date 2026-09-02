import {
  teamAutomationApi,
  teamAutomationApiDecoders,
  type TeamAutomationCreateDraft,
} from "@/shared/api/teamAutomationApi";

const draft: TeamAutomationCreateDraft = {
  scopeId: "scope-alpha",
  teamId: "team-alpha",
  memberId: "m-alpha",
  displayName: "Daily review",
  prompt: "Summarize open work.",
  cronExpression: "0 9 * * 1-5",
  timezone: "Asia/Singapore",
  enabled: true,
};

function authorizationResult() {
  return {
    success: true,
    failureCode: "SCHEDULED_INVOCATION_AUTHORIZATION_FAILURE_CODE_UNSPECIFIED",
    detail: "",
    plan: {
      schemaVersion: "scheduled-invocation-authorization/v1",
      invocationTarget: {
        studioMember: {
          scopeId: "scope-alpha",
          teamId: "team-alpha",
          memberId: "m-alpha",
          publishedServiceId: "svc-alpha",
          draftWorkflowId: "wf-alpha",
          workflowRevisionId: "rev-alpha",
        },
      },
      nyxIdServiceGrants: [
        {
          userServiceId: "us-alpha",
          serviceSlug: "connector-alpha",
          displayName: "Connector Alpha",
          nodeGrantRequirement: "AUTHORIZATION_GRANT_REQUIREMENT_REQUIRED",
          nodeIds: ["node-alpha"],
        },
      ],
      credentialPolicy: {
        scopes: ["NYX_ID_CREDENTIAL_SCOPE_READ", "NYX_ID_CREDENTIAL_SCOPE_PROXY"],
        serviceGrantRequirement: "AUTHORIZATION_GRANT_REQUIREMENT_REQUIRED",
        nodeGrantRequirement: "AUTHORIZATION_GRANT_REQUIREMENT_REQUIRED",
        allowAllServices: false,
        allowAllNodes: false,
        expiresAt: "2026-10-14T00:00:00Z",
        policyVersion: "scheduled-invocation-auth/v1",
      },
      disclosures: [
        "SCHEDULED_INVOCATION_DISCLOSURE_DEDICATED_CREDENTIAL",
        "SCHEDULED_INVOCATION_DISCLOSURE_AEVATAR_SECRET_CUSTODY",
        "SCHEDULED_INVOCATION_DISCLOSURE_BROWSER_NEVER_RECEIVES_SECRET",
        "SCHEDULED_INVOCATION_DISCLOSURE_DELETE_REVOKES_CREDENTIAL",
        "SCHEDULED_INVOCATION_DISCLOSURE_PAUSE_RESUME_PRESERVES_CREDENTIAL",
        "SCHEDULED_INVOCATION_DISCLOSURE_NODE_IDS_ARE_PERMISSION_SET",
      ],
      permissionDigest: "digest-alpha",
      ownerLlmSelection: {
        routeKind: "SCHEDULED_INVOCATION_OWNER_LLM_ROUTE_KIND_NYX_ID_USER_SERVICE",
        routeValue: "us-alpha",
        nyxIdUserServiceId: "us-alpha",
        serviceSlugSnapshot: "connector-alpha",
        model: "gpt-5",
      },
    },
  };
}

function noServiceAuthorizationResult() {
  return {
    success: true,
    failureCode: "SCHEDULED_INVOCATION_AUTHORIZATION_FAILURE_CODE_UNSPECIFIED",
    detail: "",
    plan: {
      schemaVersion: "scheduled-invocation-authorization/v2",
      invocationTarget: {
        studioMember: {
          scopeId: "scope-alpha",
          teamId: "team-alpha",
          memberId: "m-alpha",
          publishedServiceId: "svc-alpha",
          draftWorkflowId: "wf-alpha",
          workflowRevisionId: "rev-alpha",
        },
      },
      nyxIdServiceGrants: [],
      credentialPolicy: {
        scopes: [1, 2],
        serviceGrantRequirement: 2,
        nodeGrantRequirement: 2,
        allowAllServices: false,
        allowAllNodes: false,
        expiresAt: { seconds: "1791936000", nanos: 0 },
        policyVersion: "scheduled-invocation-auth/v2",
      },
      disclosures: [1, 2, 3, 4, 5, 6],
      permissionDigest: "digest-no-service",
      catalogAuthority: null,
      ownerLlmSelection: null,
    },
  };
}

function authorizationResultWithoutOwnerLlmSelection() {
  const result = authorizationResult();
  const { ownerLlmSelection: _ownerLlmSelection, ...plan } = result.plan;
  return {
    ...result,
    plan,
  };
}

function automationView(overrides?: Record<string, unknown>) {
  return {
    scheduleId: "sch-alpha",
    displayName: "Daily review",
    targetKind: "ServiceInvocation",
    targetActorId: "actor-alpha",
    payloadTypeUrl: "type.googleapis.com/aevatar.ChatRequestEvent",
    serviceKey: "scope-alpha:default:default:svc-alpha",
    serviceId: "svc-alpha",
    serviceEndpointId: "chat",
    prompt: "Summarize open work.",
    cronExpression: "0 9 * * 1-5",
    timezone: "Asia/Singapore",
    enabled: true,
    createdAt: "2026-07-15T00:00:00Z",
    updatedAt: "2026-07-16T00:00:00Z",
    nextFireAt: "2026-07-17T01:00:00Z",
    lastFireAt: null,
    lastTargetActorId: "",
    lastCommandId: "",
    lastCorrelationId: "",
    lastError: "",
    fireCount: 0,
    failureCount: 0,
    headers: {},
    scheduleActorId: "schedule-actor-alpha",
    scheduleKind: "Workflow",
    deleted: false,
    teamOwned: true,
    teamOwnerScopeId: "scope-alpha",
    teamOwnerMemberId: "m-alpha",
    teamId: "team-alpha",
    credentialSourceKind: "ScheduledInvocationAgentKey",
    teamAutomationLifecycleStatus: 2,
    credentialExpiresAt: "2026-10-14T00:00:00Z",
    teamAutomationOperationId: "op-alpha",
    lastAuthorizationErrorCode: "",
    credentialGeneration: 1,
    revocationPending: false,
    nyxIdRevocationStatus: "NotRequired",
    vaultRevocationStatus: "NotRequired",
    ownerLlmRouteKind: "nyx_id_user_service",
    ownerLlmRoute: "us-alpha",
    ownerLlmUserServiceId: "us-alpha",
    ownerLlmServiceSlug: "connector-alpha",
    ownerLlmModel: "gpt-5",
    stateVersion: 4,
    ...overrides,
  };
}

describe("teamAutomationApi", () => {
  const originalFetch = global.fetch;

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
  });

  it.each([
    [1, "provisioning_pending"],
    [2, "active"],
    [3, "needs_authorization"],
    [4, "replacement_pending"],
    [5, "deleting"],
    [6, "revocation_pending"],
    [7, "failed"],
  ] as const)(
    "decodes canonical numeric Team automation lifecycle status %s as %s",
    (wireStatus, authorizationStatus) => {
      expect(
        teamAutomationApiDecoders.view(
          automationView({ teamAutomationLifecycleStatus: wireStatus }),
        ),
      ).toEqual(expect.objectContaining({ authorizationStatus }));
    },
  );

  it("decodes canonical no-owner LLM runtime evidence", () => {
    expect(
      teamAutomationApiDecoders.view(
        automationView({
          ownerLlmRouteKind: "unspecified",
          ownerLlmRoute: "",
          ownerLlmUserServiceId: "",
          ownerLlmServiceSlug: "",
          ownerLlmModel: "",
        }),
      ),
    ).toEqual(
      expect.objectContaining({
        ownerLLMRouteKind: "unspecified",
        ownerLLMRoute: "",
        ownerLLMUserServiceId: "",
        ownerLLMServiceSlug: "",
        ownerLLMModel: "",
      }),
    );
  });

  it.each([
    ["NeedsAuthorization", "needs_authorization"],
    [
      "TEAM_AUTOMATION_STATUS_REPLACEMENT_PENDING",
      "replacement_pending",
    ],
  ] as const)(
    "decodes canonical textual Team automation lifecycle status %s as %s",
    (wireStatus, authorizationStatus) => {
      expect(
        teamAutomationApiDecoders.view(
          automationView({ teamAutomationLifecycleStatus: wireStatus }),
        ),
      ).toEqual(expect.objectContaining({ authorizationStatus }));
    },
  );

  it.each([0, 8, "Future"])(
    "rejects unknown Team automation lifecycle status %s",
    (wireStatus) => {
      expect(() =>
        teamAutomationApiDecoders.view(
          automationView({ teamAutomationLifecycleStatus: wireStatus }),
        ),
      ).toThrow(`Unknown Team automation status: ${String(wireStatus)}.`);
    },
  );

  it.each([
    "constructor",
    [2],
    ["NeedsAuthorization"],
    {},
    true,
    null,
    undefined,
    "act-ive",
    "n e e d s authorization",
    "TEAM---AUTOMATION STATUS--ACTIVE",
  ] as const)(
    "rejects malformed Team automation lifecycle value %s",
    (wireStatus) => {
      expect(() =>
        teamAutomationApiDecoders.view(
          automationView({ teamAutomationLifecycleStatus: wireStatus }),
        ),
      ).toThrow(`Unknown Team automation status: ${String(wireStatus)}.`);
    },
  );

  it("decodes exact typed service and node authorization grants", () => {
    const review = teamAutomationApiDecoders.permissionReview(authorizationResult());

    expect(review).toEqual(
      expect.objectContaining({
        status: "ready",
        permissionDigest: "digest-alpha",
        policyVersion: "scheduled-invocation-auth/v1",
        serviceGrants: [
          {
            displayName: "Connector Alpha",
            grantId: "service:us-alpha",
            kind: "service",
            nodeGrantRequirement: "required",
            nodeIds: ["node-alpha"],
            serviceSlug: "connector-alpha",
            targetId: "us-alpha",
          },
        ],
        nodeGrants: [
          {
            displayName: "node-alpha",
            grantId: "node:us-alpha:node-alpha",
            kind: "node",
            targetId: "node-alpha",
            userServiceId: "us-alpha",
          },
        ],
      }),
    );
    expect(review.credentialPlan).toEqual(
      expect.objectContaining({
        scopes: ["read", "proxy"],
        allowAllServices: false,
        allowAllNodes: false,
        browserReceivesRawKey: false,
      }),
    );
    expect(JSON.stringify(review)).not.toContain("m-alpha");
    expect(JSON.stringify(review)).not.toContain("wf-alpha");
    expect(JSON.stringify(review)).not.toContain("svc-alpha");
  });

  it("decodes a v2 non-LLM plan without service or node authorization", () => {
    const review = teamAutomationApiDecoders.permissionReview(
      noServiceAuthorizationResult(),
    );

    expect(review).toEqual(
      expect.objectContaining({
        status: "ready",
        ownerLLMSelection: null,
        serviceGrants: [],
        nodeGrants: [],
        credentialPlan: expect.objectContaining({
          serviceGrantRequirement: "not_required",
          nodeGrantRequirement: "not_required",
        }),
      }),
    );
  });

  it("keeps an absent owner LLM selection independent from required workflow service grants", () => {
    const base = authorizationResult();
    const result = {
      ...base,
      plan: {
        ...base.plan,
        ownerLlmSelection: null,
        nyxIdServiceGrants: base.plan.nyxIdServiceGrants.map((grant) => ({
          ...grant,
          nodeGrantRequirement: "AUTHORIZATION_GRANT_REQUIREMENT_NOT_REQUIRED",
          nodeIds: [],
        })),
        credentialPolicy: {
          ...base.plan.credentialPolicy,
          nodeGrantRequirement: "AUTHORIZATION_GRANT_REQUIREMENT_NOT_REQUIRED",
        },
      },
    };

    expect(teamAutomationApiDecoders.permissionReview(result)).toEqual(
      expect.objectContaining({
        ownerLLMSelection: null,
        serviceGrants: [
          expect.objectContaining({
            targetId: "us-alpha",
            nodeGrantRequirement: "not_required",
          }),
        ],
        credentialPlan: expect.objectContaining({
          serviceGrantRequirement: "required",
          nodeGrantRequirement: "not_required",
        }),
      }),
    );
  });

  it("decodes an omitted owner LLM selection as null", () => {
    expect(
      teamAutomationApiDecoders.permissionReview(
        authorizationResultWithoutOwnerLlmSelection(),
      ),
    ).toEqual(expect.objectContaining({ ownerLLMSelection: null }));
  });

  it.each([
    ["requires services but returns none", () => {
      const base = noServiceAuthorizationResult();
      return {
        ...base,
        plan: {
          ...base.plan,
          credentialPolicy: {
            ...base.plan.credentialPolicy,
            serviceGrantRequirement: 1,
          },
        },
      };
    }],
    ["does not require services but returns one", () => {
      const base = authorizationResult();
      return {
        ...base,
        plan: {
          ...base.plan,
          credentialPolicy: {
            ...base.plan.credentialPolicy,
            serviceGrantRequirement:
              "AUTHORIZATION_GRANT_REQUIREMENT_NOT_REQUIRED",
          },
        },
      };
    }],
  ])("fails closed when the service-grant policy %s", (_caseName, createResult) => {
    expect(() => teamAutomationApiDecoders.permissionReview(createResult())).toThrow(
      "Team Automation authorization service grant requirement does not match exact service grants.",
    );
  });

  it("fails closed when node policy disagrees with required service nodes", () => {
    const base = authorizationResult();
    const result = {
      ...base,
      plan: {
        ...base.plan,
        credentialPolicy: {
          ...base.plan.credentialPolicy,
          nodeGrantRequirement: "AUTHORIZATION_GRANT_REQUIREMENT_NOT_REQUIRED",
        },
      },
    };

    expect(() => teamAutomationApiDecoders.permissionReview(result)).toThrow(
      "Team Automation authorization node grant requirement does not match exact service grants.",
    );
  });

  it.each([
    [
      "requires nodes but returns none",
      "AUTHORIZATION_GRANT_REQUIREMENT_REQUIRED",
      [],
      "AUTHORIZATION_GRANT_REQUIREMENT_REQUIRED",
    ],
    [
      "does not require nodes but returns one",
      "AUTHORIZATION_GRANT_REQUIREMENT_NOT_REQUIRED",
      ["node-alpha"],
      "AUTHORIZATION_GRANT_REQUIREMENT_NOT_REQUIRED",
    ],
  ] as const)(
    "fails closed when a service grant %s",
    (
      _caseName,
      serviceNodeGrantRequirement,
      nodeIds,
      credentialNodeGrantRequirement,
    ) => {
      const base = authorizationResult();
      const result = {
        ...base,
        plan: {
          ...base.plan,
          nyxIdServiceGrants: base.plan.nyxIdServiceGrants.map((grant) => ({
            ...grant,
            nodeGrantRequirement: serviceNodeGrantRequirement,
            nodeIds,
          })),
          credentialPolicy: {
            ...base.plan.credentialPolicy,
            nodeGrantRequirement: credentialNodeGrantRequirement,
          },
        },
      };

      expect(() => teamAutomationApiDecoders.permissionReview(result)).toThrow(
        "Team Automation authorization node grant requirement does not match per-service node grants.",
      );
    },
  );

  it.each([
    ["service policy", () => {
      const base = authorizationResult();
      return {
        ...base,
        plan: {
          ...base.plan,
          credentialPolicy: {
            ...base.plan.credentialPolicy,
            serviceGrantRequirement:
              "AUTHORIZATION_GRANT_REQUIREMENT_UNSPECIFIED",
          },
        },
      };
    }],
    ["node policy", () => {
      const base = authorizationResult();
      return {
        ...base,
        plan: {
          ...base.plan,
          credentialPolicy: {
            ...base.plan.credentialPolicy,
            nodeGrantRequirement: 0,
          },
        },
      };
    }],
    ["service grant", () => {
      const base = authorizationResult();
      return {
        ...base,
        plan: {
          ...base.plan,
          nyxIdServiceGrants: base.plan.nyxIdServiceGrants.map((grant) => ({
            ...grant,
            nodeGrantRequirement:
              "AUTHORIZATION_GRANT_REQUIREMENT_CONDITIONAL_REQUIRED",
          })),
        },
      };
    }],
  ])("fails closed on unspecified or unknown %s grant requirements", (_label, createResult) => {
    expect(() => teamAutomationApiDecoders.permissionReview(createResult())).toThrow(
      "grant requirement",
    );
  });

  it("fails closed when a preflight plan allows all services", () => {
    const result = authorizationResult();
    result.plan.credentialPolicy.allowAllServices = true;

    expect(() => teamAutomationApiDecoders.permissionReview(result)).toThrow(
      "must use exact service and node grants",
    );
  });

  it("fails closed when a required disclosure is missing or unknown", () => {
    const missing = authorizationResult();
    missing.plan.disclosures = missing.plan.disclosures.filter(
      (value) =>
        value !== "SCHEDULED_INVOCATION_DISCLOSURE_AEVATAR_SECRET_CUSTODY",
    );
    expect(() => teamAutomationApiDecoders.permissionReview(missing)).toThrow(
      "missing required disclosures",
    );

    const unknown = authorizationResult();
    unknown.plan.disclosures.push("SCHEDULED_INVOCATION_DISCLOSURE_FUTURE_MODE");
    expect(() => teamAutomationApiDecoders.permissionReview(unknown)).toThrow(
      "Unknown Team automation disclosure",
    );
  });

  it("fails closed on an unknown node grant requirement", () => {
    const result = authorizationResult();
    result.plan.nyxIdServiceGrants[0].nodeGrantRequirement =
      "AUTHORIZATION_GRANT_REQUIREMENT_FUTURE";

    expect(() => teamAutomationApiDecoders.permissionReview(result)).toThrow(
      "Unknown NyxID node grant requirement",
    );
  });

  it("fails closed when future enum names merely share a known suffix", () => {
    const futureScope = authorizationResult();
    futureScope.plan.credentialPolicy.scopes[0] =
      "NYX_ID_CREDENTIAL_SCOPE_ARCHIVE_READ";
    expect(() => teamAutomationApiDecoders.permissionReview(futureScope)).toThrow(
      "Unknown NyxID credential scope",
    );

    const futureRequirement = authorizationResult();
    futureRequirement.plan.nyxIdServiceGrants[0].nodeGrantRequirement =
      "AUTHORIZATION_GRANT_REQUIREMENT_CONDITIONAL_REQUIRED";
    expect(() =>
      teamAutomationApiDecoders.permissionReview(futureRequirement),
    ).toThrow("Unknown NyxID node grant requirement");

    const futureRouteKind = authorizationResult();
    futureRouteKind.plan.ownerLlmSelection.routeKind =
      "SCHEDULED_INVOCATION_OWNER_LLM_ROUTE_KIND_FUTURE_GATEWAY";
    expect(() =>
      teamAutomationApiDecoders.permissionReview(futureRouteKind),
    ).toThrow("Unknown Team automation owner LLM route kind");
  });

  it("preserves duplicate grants and their order in the review", () => {
    const result = authorizationResult();
    result.plan.nyxIdServiceGrants[0].nodeIds.push("node-alpha");

    const review = teamAutomationApiDecoders.permissionReview(result);

    expect(review.nodeGrants.map((grant) => grant.targetId)).toEqual([
      "node-alpha",
      "node-alpha",
    ]);
  });

  it("turns the numeric authorization-plan-changed failure into a fresh-review state", () => {
    expect(
      teamAutomationApiDecoders.permissionReview({
        success: false,
        plan: null,
        failureCode: 12,
        detail: "scheduled_invocation_authorization_plan_changed",
      }),
    ).toEqual(
      expect.objectContaining({
        status: "plan-changed",
        warning: "scheduled_invocation_authorization_plan_changed",
      }),
    );
  });

  it("does not treat a rejected admission receipt as success", () => {
    expect(() =>
      teamAutomationApiDecoders.receipt({
        accepted: false,
        status: "accepted",
        scheduleId: "sch-alpha",
        operationId: "op-alpha",
        commandId: "cmd-alpha",
      }),
    ).toThrow("Team automation command was not accepted.");
  });

  it("calls scoped preflight without browser-provided grants or secrets", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => authorizationResult(),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(teamAutomationApi.preflightCreate(draft)).resolves.toEqual(
      expect.objectContaining({ permissionDigest: "digest-alpha" }),
    );

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe(
      "/api/scopes/scope-alpha/teams/team-alpha/members/m-alpha/automations/preflight",
    );
    const body = JSON.parse(String(init.body));
    expect(body).toEqual({
      scheduleCron: "0 9 * * 1-5",
      scheduleTimezone: "Asia/Singapore",
      prompt: "Summarize open work.",
      displayName: "Daily review",
      enabled: true,
    });
    expect(body).not.toHaveProperty("credentialExpiresAtUtc");
    expect(JSON.stringify(body)).not.toMatch(
      /fullKey|accessToken|refreshToken|SecretReference|apiKeyId|allowedService|allowedNode/i,
    );
  });

  it("uses one scoped create command after confirmation", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 202,
      json: async () => ({
        accepted: true,
        status: "accepted",
        scheduleId: "sch-alpha",
        operationId: "op-alpha",
        commandId: "cmd-alpha",
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await teamAutomationApi.create(
      draft,
      "digest-alpha",
      "scheduled-invocation-auth/v1",
    );

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe(
      "/api/scopes/scope-alpha/teams/team-alpha/members/m-alpha/automations",
    );
    const body = JSON.parse(String(init.body));
    expect(body).toEqual(
      expect.objectContaining({
        confirmedPermissionDigest: "digest-alpha",
        confirmedPolicyVersion: "scheduled-invocation-auth/v1",
        credentialProvisioningKind: "dedicated_scheduled_invocation_agent_key",
        enabled: true,
        operationId: expect.any(String),
        idempotencyKey: expect.any(String),
      }),
    );
    expect(body).not.toHaveProperty("credentialExpiresAtUtc");
    expect(JSON.stringify(body)).not.toContain("svc-alpha");
    expect(JSON.stringify(body)).not.toMatch(
      /fullKey|accessToken|refreshToken|SecretReference|apiKeyId|credentialId/i,
    );
  });

  it("lists only the canonical owner-scoped schedule resource", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        items: [automationView()],
        nextCursor: null,
        totalCount: 1,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      teamAutomationApi.list(draft, { take: 200 }),
    ).resolves.toEqual({
      items: [expect.objectContaining({ scheduleId: "sch-alpha", memberId: "m-alpha" })],
      nextCursor: null,
      totalCount: 1,
    });
    expect(fetchMock.mock.calls[0][0]).toBe(
      "/api/schedules?ownerKind=studio_member_automation&ownerScopeId=scope-alpha&ownerTeamId=team-alpha&ownerMemberId=m-alpha&includeTotalCount=true&take=200",
    );
  });

  it.each([
    ["scope", { teamOwnerScopeId: "scope-other" }],
    ["Team", { teamId: "team-other" }],
    ["member", { teamOwnerMemberId: "m-other" }],
  ])("rejects a list item from a different %s owner", async (_label, overrides) => {
    global.fetch = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        items: [automationView(overrides)],
        nextCursor: null,
        totalCount: 1,
      }),
    } as Response) as typeof global.fetch;

    await expect(teamAutomationApi.list(draft)).rejects.toThrow(
      "does not belong to the requested Team member route",
    );
  });

  it("rejects a generic schedule from the owner-scoped collection", async () => {
    global.fetch = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        items: [automationView({ teamOwned: false })],
        nextCursor: null,
        totalCount: 1,
      }),
    } as Response) as typeof global.fetch;

    await expect(teamAutomationApi.list(draft)).rejects.toThrow(
      "is not a Team-owned automation schedule",
    );
  });

  it("gets canonical owner-scoped schedule detail", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        schedule: automationView(),
        recentFires: [],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(teamAutomationApi.get(draft, " sch/alpha ")).resolves.toEqual(
      expect.objectContaining({
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scheduleId: "sch-alpha",
      }),
    );
    expect(fetchMock.mock.calls[0][0]).toBe(
      "/api/schedules/sch%2Falpha?ownerKind=studio_member_automation&ownerScopeId=scope-alpha&ownerTeamId=team-alpha&ownerMemberId=m-alpha",
    );
  });

  it("rejects a preflight plan for a different route", async () => {
    const result = authorizationResult();
    result.plan.invocationTarget.studioMember.teamId = "team-other";
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => result,
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(teamAutomationApi.preflightCreate(draft)).rejects.toThrow(
      "does not belong to the requested Team member route",
    );
  });

  it("requires credential health and authoritative update fields", () => {
    expect(() =>
      teamAutomationApiDecoders.view(
        automationView({ credentialSourceKind: undefined }),
      ),
    ).toThrow("Unknown Team automation credential source");
    expect(() =>
      teamAutomationApiDecoders.view(automationView({ updatedAt: undefined })),
    ).toThrow("must be an object");
  });

  it("uses a caller-owned operation identity verbatim", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 202,
      json: async () => ({
        accepted: true,
        status: "pending",
        scheduleId: "sch-alpha",
        operationId: "op-stable",
        commandId: "cmd-alpha",
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await teamAutomationApi.create(
      draft,
      "digest-alpha",
      "scheduled-invocation-auth/v1",
      { operationId: "op-stable", idempotencyKey: "idem-stable" },
    );

    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body));
    expect(body).toEqual(
      expect.objectContaining({
        operationId: "op-stable",
        idempotencyKey: "idem-stable",
      }),
    );
  });

  it("retries credential revocation without a browser operation ledger", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 202,
      json: async () => ({
        accepted: true,
        status: "pending",
        scheduleId: "sch-alpha",
        operationId: "op-stable",
        commandId: "cmd-retry-revocation",
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      teamAutomationApi.retryRevocation(draft, "sch-alpha"),
    ).resolves.toEqual(
      expect.objectContaining({
        operationId: "op-stable",
        scheduleId: "sch-alpha",
        status: "pending",
      }),
    );

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe(
      "/api/scopes/scope-alpha/teams/team-alpha/members/m-alpha/automations/sch-alpha/retry-revocation",
    );
    expect(init.method).toBe("POST");
    expect(init.body).toBeUndefined();
  });

  it("preserves typed retry and preflight error details", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: false,
      status: 503,
      statusText: "Service Unavailable",
      text: async () => JSON.stringify({
        code: "TEAM_AUTOMATION_AUTHORIZATION_PROJECTION_PENDING",
        message: "Projection pending",
        retryable: true,
        requiredStateVersion: 19,
        preflightLocator: "/canonical/preflight",
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(teamAutomationApi.list(draft)).rejects.toMatchObject({
      code: "TEAM_AUTOMATION_AUTHORIZATION_PROJECTION_PENDING",
      preflightLocator: "/canonical/preflight",
      requiredStateVersion: 19,
      retryable: true,
      status: 503,
    });
  });

  it("preserves typed preflight authorization failure details", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: false,
      status: 403,
      statusText: "Forbidden",
      text: async () => JSON.stringify({
        code: "TEAM_AUTOMATION_AUTHORIZATION_SERVICE_ACCESS_DENIED",
        message:
          "This automation is not authorized to use one or more required services.",
        retryable: false,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(teamAutomationApi.preflightCreate(draft)).rejects.toMatchObject({
      code: "TEAM_AUTOMATION_AUTHORIZATION_SERVICE_ACCESS_DENIED",
      message: "This automation is not authorized to use one or more required services.",
      retryable: false,
      status: 403,
    });
  });

  it("fails closed on an unknown revocation track", () => {
    expect(() =>
      teamAutomationApiDecoders.view(
        automationView({ nyxIdRevocationStatus: "Future" }),
      ),
    ).toThrow("Unknown Team automation revocation track");
  });

  it("lists every member automation in a Team without ownerMemberId", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        items: [
          automationView({ scheduleId: "sch-alpha" }),
          automationView({
            scheduleId: "sch-beta",
            serviceId: "svc-beta",
            teamOwnerMemberId: "m-beta",
          }),
        ],
        nextCursor: null,
        totalCount: 2,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    const result = await teamAutomationApi.listAll(
      { scopeId: "scope-alpha", teamId: "team-alpha" },
      { take: 200 },
    );

    expect(result.items.map((item) => item.memberId)).toEqual(["m-alpha", "m-beta"]);
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/schedules?ownerKind=studio_member_automation&ownerScopeId=scope-alpha&ownerTeamId=team-alpha&includeTotalCount=true&take=200",
    );
  });

  it("keeps the canonical owner tuple on every list page", async () => {
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          items: [automationView({ scheduleId: "sch-first" })],
          nextCursor: "cursor-2",
          totalCount: 2,
        }),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          items: [automationView({ scheduleId: "sch-second" })],
          nextCursor: null,
          totalCount: 2,
        }),
      } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    const result = await teamAutomationApi.listAll(draft, { take: 200 });

    expect(result.items.map((item) => item.scheduleId)).toEqual([
      "sch-first",
      "sch-second",
    ]);
    expect(fetchMock.mock.calls.map(([path]) => path)).toEqual([
      "/api/schedules?ownerKind=studio_member_automation&ownerScopeId=scope-alpha&ownerTeamId=team-alpha&ownerMemberId=m-alpha&includeTotalCount=true&take=200",
      "/api/schedules?ownerKind=studio_member_automation&ownerScopeId=scope-alpha&ownerTeamId=team-alpha&ownerMemberId=m-alpha&cursor=cursor-2&includeTotalCount=true&take=200",
    ]);
  });
});
