type ServiceRevisionCatalogOverrides = Partial<{
  scopeId: string;
  serviceId: string;
  displayName: string;
  workflowName: string;
  revisionId: string;
  deploymentStatus: string;
}>;

export function buildServiceRevisionCatalogFixture(
  overrides?: ServiceRevisionCatalogOverrides,
) {
  const scopeId = overrides?.scopeId ?? "scope-1";
  const serviceId = overrides?.serviceId ?? "default";
  const displayName = overrides?.displayName ?? "workspace-demo";
  const workflowName = overrides?.workflowName ?? displayName;
  const revisionId = overrides?.revisionId ?? "rev-2";
  const deploymentStatus = overrides?.deploymentStatus ?? "Active";

  return {
    scopeId,
    serviceId,
    serviceKey: `${scopeId}:default:default:${serviceId}`,
    displayName,
    defaultServingRevisionId: revisionId,
    activeServingRevisionId: revisionId,
    deploymentId: "dep-2",
    deploymentStatus,
    primaryActorId: "actor-default",
    catalogStateVersion: 2,
    catalogLastEventId: "event-2",
    updatedAt: "2026-03-26T08:00:00Z",
    revisions: [
      {
        revisionId,
        implementationKind: "workflow",
        status: "Published",
        artifactHash: "hash-2",
        failureReason: "",
        isDefaultServing: true,
        isActiveServing: true,
        isServingTarget: true,
        allocationWeight: 100,
        servingState: "Active",
        deploymentId: "dep-2",
        primaryActorId: "actor-default",
        createdAt: "2026-03-26T07:00:00Z",
        preparedAt: "2026-03-26T07:01:00Z",
        publishedAt: "2026-03-26T07:02:00Z",
        retiredAt: null,
        workflowName,
        workflowDefinitionActorId: "scope-workflow:scope-1:default",
        inlineWorkflowCount: 1,
        scriptId: "",
        scriptRevision: "",
        scriptDefinitionActorId: "",
        scriptSourceHash: "",
        staticAgentKind: "",
      },
    ],
  };
}

export function buildScriptServiceRevisionCatalogFixture(
  overrides?: Partial<{
    scopeId: string;
    serviceId: string;
    displayName: string;
    scriptId: string;
    revisionId: string;
  }>,
) {
  const scriptId = overrides?.scriptId ?? "script-alpha";
  const revisionId = overrides?.revisionId ?? "rev-script-1";
  const catalog = buildServiceRevisionCatalogFixture({
    scopeId: overrides?.scopeId,
    serviceId: overrides?.serviceId ?? scriptId,
    displayName: overrides?.displayName ?? scriptId,
    workflowName: "",
    revisionId,
  });

  return {
    ...catalog,
    revisions: [
      {
        ...catalog.revisions[0],
        implementationKind: "script",
        workflowName: "",
        workflowDefinitionActorId: "",
        inlineWorkflowCount: 0,
        scriptId,
        scriptRevision: revisionId,
        scriptDefinitionActorId: "definition-1",
        scriptSourceHash: "hash-1",
      },
    ],
  };
}

export function buildGAgentServiceRevisionCatalogFixture(
  overrides?: Partial<{
    scopeId: string;
    serviceId: string;
    displayName: string;
    agentKind: string;
    revisionId: string;
  }>,
) {
  const agentKind = overrides?.agentKind ?? "Tests.OrdersGAgent";
  const revisionId = overrides?.revisionId ?? "rev-gagent-1";
  const catalog = buildServiceRevisionCatalogFixture({
    scopeId: overrides?.scopeId,
    serviceId: overrides?.serviceId ?? "gagent-1",
    displayName: overrides?.displayName ?? "gagent-1",
    workflowName: "",
    revisionId,
  });

  return {
    ...catalog,
    revisions: [
      {
        ...catalog.revisions[0],
        implementationKind: "gagent",
        workflowName: "",
        workflowDefinitionActorId: "",
        inlineWorkflowCount: 0,
        scriptId: "",
        scriptRevision: "",
        scriptDefinitionActorId: "",
        scriptSourceHash: "",
        staticAgentKind: agentKind,
      },
    ],
  };
}
