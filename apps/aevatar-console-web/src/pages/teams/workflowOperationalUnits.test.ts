import {
  buildWorkflowOperationalUnits,
  collectWorkflowOperationalServiceIds,
  resolveWorkflowOperationalUnit,
} from "./workflowOperationalUnits";

describe("workflowOperationalUnits", () => {
  const binding = {
    available: true,
    scopeId: "scope-a",
    serviceId: "service-alpha",
    displayName: "Alpha Team",
    serviceKey: "scope-a:alpha",
    defaultServingRevisionId: "rev-alpha",
    activeServingRevisionId: "rev-alpha",
    deploymentId: "dep-alpha",
    deploymentStatus: "Active",
    primaryActorId: "actor://alpha",
    updatedAt: "2026-04-13T09:00:00Z",
    revisions: [
      {
        revisionId: "rev-alpha",
        implementationKind: "workflow",
        status: "Published",
        artifactHash: "hash-alpha",
        failureReason: "",
        isDefaultServing: true,
        isActiveServing: true,
        isServingTarget: true,
        allocationWeight: 100,
        servingState: "Active",
        deploymentId: "dep-alpha",
        primaryActorId: "actor://alpha",
        createdAt: "2026-04-13T08:00:00Z",
        preparedAt: "2026-04-13T08:05:00Z",
        publishedAt: "2026-04-13T08:10:00Z",
        retiredAt: null,
        workflowName: "customer-support-triage",
        workflowDefinitionActorId: "definition://customer-support-triage",
        inlineWorkflowCount: 1,
        scriptId: "",
        scriptRevision: "",
        scriptDefinitionActorId: "",
        scriptSourceHash: "",
        staticActorTypeName: "",
      },
    ],
  } as const;

  const workflows = [
    {
      scopeId: "scope-a",
      workflowId: "wf-alpha",
      displayName: "Customer Support Triage",
      serviceKey: "scope-a:alpha",
      workflowName: "customer-support-triage",
      actorId: "actor://alpha",
      activeRevisionId: "rev-alpha",
      deploymentId: "dep-alpha",
      deploymentStatus: "Active",
      updatedAt: "2026-04-13T09:00:00Z",
    },
    {
      scopeId: "scope-a",
      workflowId: "wf-draft",
      displayName: "Draft Team",
      serviceKey: "",
      workflowName: "draft-team",
      actorId: "actor://draft",
      activeRevisionId: "rev-draft",
      deploymentId: "",
      deploymentStatus: "Draft",
      updatedAt: "2026-04-13T08:00:00Z",
    },
  ] as const;

  const services = [
    {
      serviceKey: "scope-a:alpha",
      tenantId: "scope-a",
      appId: "default",
      namespace: "default",
      serviceId: "service-alpha",
      displayName: "Support Runtime",
      defaultServingRevisionId: "rev-alpha",
      activeServingRevisionId: "rev-alpha",
      deploymentId: "dep-alpha",
      primaryActorId: "actor://alpha",
      deploymentStatus: "Active",
      endpoints: [],
      policyIds: [],
      updatedAt: "2026-04-13T09:01:00Z",
    },
    {
      serviceKey: "scope-a:stale",
      tenantId: "scope-a",
      appId: "default",
      namespace: "default",
      serviceId: "service-stale",
      displayName: "Old Runtime",
      defaultServingRevisionId: "rev-stale",
      activeServingRevisionId: "rev-stale",
      deploymentId: "dep-stale",
      primaryActorId: "actor://stale",
      deploymentStatus: "Active",
      endpoints: [],
      policyIds: [],
      updatedAt: "2026-04-12T09:01:00Z",
    },
  ];

  const runs = [
    {
      scopeId: "scope-a",
      serviceId: "service-alpha",
      runId: "run-latest",
      actorId: "actor://alpha",
      definitionActorId: "definition://customer-support-triage",
      revisionId: "rev-alpha",
      deploymentId: "dep-alpha",
      workflowName: "customer-support-triage",
      completionStatus: "waiting_approval",
      stateVersion: 2,
      lastEventId: "evt-2",
      lastUpdatedAt: "2026-04-13T09:05:00Z",
      boundAt: "2026-04-13T09:00:00Z",
      bindingUpdatedAt: "2026-04-13T09:00:00Z",
      lastSuccess: false,
      totalSteps: 4,
      completedSteps: 2,
      roleReplyCount: 1,
      lastOutput: "",
      lastError: "Waiting on approval",
    },
    {
      scopeId: "scope-a",
      serviceId: "service-alpha",
      runId: "run-good",
      actorId: "actor://alpha",
      definitionActorId: "definition://customer-support-triage",
      revisionId: "rev-alpha",
      deploymentId: "dep-alpha",
      workflowName: "customer-support-triage",
      completionStatus: "completed",
      stateVersion: 1,
      lastEventId: "evt-1",
      lastUpdatedAt: "2026-04-13T08:55:00Z",
      boundAt: "2026-04-13T08:50:00Z",
      bindingUpdatedAt: "2026-04-13T08:50:00Z",
      lastSuccess: true,
      totalSteps: 3,
      completedSteps: 3,
      roleReplyCount: 1,
      lastOutput: "Resolved",
      lastError: "",
    },
  ] as const;

  it("collects the deduped matched service ids for the roster queries", () => {
    expect(
      collectWorkflowOperationalServiceIds({
        binding,
        services,
        workflows,
      }),
    ).toEqual(["service-alpha"]);
  });

  it("ignores stale service and run hints when a workflow-backed match exists", () => {
    const unit = resolveWorkflowOperationalUnit({
      binding,
      preferredRunId: "run-does-not-exist",
      preferredServiceId: "service-stale",
      runs,
      services,
      signals: {
        runtimeAvailableByServiceId: new Set(["service-alpha"]),
        servicesAvailable: true,
      },
      workflow: workflows[0],
    });

    expect(unit.matchedService?.serviceId).toBe("service-alpha");
    expect(unit.latestRun?.runId).toBe("run-latest");
    expect(unit.attention).toBe("waiting");
    expect(unit.staleHints).toEqual({
      runId: false,
      serviceId: true,
    });
  });

  it("marks a workflow with no bound service and no service key as draft only", () => {
    const unit = resolveWorkflowOperationalUnit({
      binding,
      services,
      signals: {
        servicesAvailable: true,
      },
      workflow: workflows[1],
    });

    expect(unit.attention).toBe("draft");
    expect(unit.isDraftOnly).toBe(true);
    expect(unit.matchedService).toBeNull();
  });

  it("sorts attention-first roster cards before healthy or draft cards", () => {
    const units = buildWorkflowOperationalUnits({
      binding,
      workflows: [
        ...workflows,
        {
          scopeId: "scope-a",
          workflowId: "wf-healthy",
          displayName: "Healthy Team",
          serviceKey: "scope-a:healthy",
          workflowName: "healthy-team",
          actorId: "actor://healthy",
          activeRevisionId: "rev-healthy",
          deploymentId: "dep-healthy",
          deploymentStatus: "Active",
          updatedAt: "2026-04-13T07:00:00Z",
        },
      ],
      services: [
        ...services,
        {
          serviceKey: "scope-a:healthy",
          tenantId: "scope-a",
          appId: "default",
          namespace: "default",
          serviceId: "service-healthy",
          displayName: "Healthy Runtime",
          defaultServingRevisionId: "rev-healthy",
          activeServingRevisionId: "rev-healthy",
          deploymentId: "dep-healthy",
          primaryActorId: "actor://healthy",
          deploymentStatus: "Active",
          endpoints: [],
          policyIds: [],
          updatedAt: "2026-04-13T07:01:00Z",
        },
      ],
      runsByServiceId: {
        "service-alpha": runs,
        "service-healthy": [
          {
            scopeId: "scope-a",
            serviceId: "service-healthy",
            runId: "run-healthy",
            actorId: "actor://healthy",
            definitionActorId: "definition://healthy",
            revisionId: "rev-healthy",
            deploymentId: "dep-healthy",
            workflowName: "healthy-team",
            completionStatus: "completed",
            stateVersion: 1,
            lastEventId: "evt-healthy",
            lastUpdatedAt: "2026-04-13T07:05:00Z",
            boundAt: "2026-04-13T07:00:00Z",
            bindingUpdatedAt: "2026-04-13T07:00:00Z",
            lastSuccess: true,
            totalSteps: 2,
            completedSteps: 2,
            roleReplyCount: 1,
            lastOutput: "Done",
            lastError: "",
          },
        ],
      },
      signals: {
        runtimeAvailableByServiceId: new Set([
          "service-alpha",
          "service-healthy",
        ]),
        servicesAvailable: true,
      },
    });

    expect(units.map((unit) => unit.workflow.workflowId)).toEqual([
      "wf-alpha",
      "wf-healthy",
      "wf-draft",
    ]);
  });
});
