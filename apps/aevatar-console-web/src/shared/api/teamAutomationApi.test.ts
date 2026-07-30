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

function automationView(overrides?: Record<string, unknown>) {
  return {
    scopeId: "scope-alpha",
    teamId: "team-alpha",
    memberId: "m-alpha",
    scheduleId: "sch-alpha",
    publishedServiceId: "svc-alpha",
    credentialSourceKind: "scheduled_invocation_agent_key",
    displayName: "Daily review",
    prompt: "Summarize open work.",
    scheduleCron: "0 9 * * 1-5",
    scheduleTimezone: "Asia/Singapore",
    enabled: true,
    authorizationStatus: "active",
    credentialExpiresAtUtc: "2026-10-14T00:00:00Z",
    lastAuthorizationErrorCode: "",
    operationId: "op-alpha",
    credentialGeneration: 1,
    revocationPending: false,
    nyxIdRevocationStatus: "NotRequired",
    vaultRevocationStatus: "NotRequired",
    ownerLlmRouteKind: "nyx_id_user_service",
    ownerLlmRoute: "us-alpha",
    ownerLlmUserServiceId: "us-alpha",
    ownerLlmServiceSlug: "connector-alpha",
    ownerLlmModel: "gpt-5",
    nextFireAt: "2026-07-17T01:00:00Z",
    lastFireAt: null,
    stateVersion: 4,
    updatedAt: "2026-07-16T00:00:00Z",
    ...overrides,
  };
}

describe("teamAutomationApi", () => {
  const originalFetch = global.fetch;

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
  });

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

  it("lists only the canonical member resource", async () => {
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
      "/api/scopes/scope-alpha/teams/team-alpha/members/m-alpha/automations?take=200",
    );
  });

  it("rejects a list item from a different Team member route", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        items: [automationView({ memberId: "m-other" })],
        nextCursor: null,
        totalCount: 1,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(teamAutomationApi.list(draft)).rejects.toThrow(
      "does not belong to the requested Team member route",
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

  it("fails closed on an unknown revocation track", () => {
    expect(() =>
      teamAutomationApiDecoders.view(
        automationView({ nyxIdRevocationStatus: "Future" }),
      ),
    ).toThrow("Unknown Team automation revocation track");
  });

  it("follows scoped list cursors without falling back to the generic schedule API", async () => {
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
      "/api/scopes/scope-alpha/teams/team-alpha/members/m-alpha/automations?take=200",
      "/api/scopes/scope-alpha/teams/team-alpha/members/m-alpha/automations?cursor=cursor-2&take=200",
    ]);
    expect(fetchMock.mock.calls.map(([path]) => path)).not.toContain("/api/schedules");
  });
});
