import {
  deriveTeamIntegrationsSummary,
  deriveTeamWorkflowRoleBindings,
} from "./teamIntegrations";

describe("teamIntegrations", () => {
  it("derives connector usage from the current workflow documents", () => {
    const workflowRoles = deriveTeamWorkflowRoleBindings([
      {
        name: "support-triage",
        roles: [
          {
            id: "triage_operator",
            connectors: ["web-search", "crm-sync"],
          },
          {
            id: "ops_dispatch",
            connectors: ["ops-terminal"],
          },
        ],
      },
      {
        name: "support-inline",
        roles: [
          {
            id: "triage_operator",
            connectors: ["web-search"],
          },
        ],
      },
    ]);

    const summary = deriveTeamIntegrationsSummary({
      bindingKind: "workflow",
      connectorCatalog: {
        homeDirectory: "actor://connector-catalog",
        filePath: "actor://connector-catalog/connectors",
        fileExists: true,
        connectors: [
          {
            name: "web-search",
            type: "http",
            enabled: true,
            timeoutMs: 30_000,
            retry: 1,
            http: {
              baseUrl: "https://search.example.com",
              allowedMethods: ["GET"],
              allowedPaths: ["/search"],
              allowedInputKeys: ["query"],
              defaultHeaders: {},
            },
          },
        ],
      },
      teamWorkflowRoles: workflowRoles,
      workflowDocumentCount: 2,
      workspaceSettings: {
        runtimeBaseUrl: "https://runtime.aevatar.test",
        directories: [],
      },
    });

    expect(summary.teamRoleUsageStatus).toBe("resolved");
    expect(summary.linkedConnectorCount).toBe(1);
    expect(summary.roleReferenceCount).toBe(3);
    expect(summary.items[0]?.usedByRoles).toEqual(["triage_operator"]);
    expect(summary.unresolvedReferences).toEqual(["crm-sync", "ops-terminal"]);
  });

  it("stays honest when the current team is not workflow-bound", () => {
    const summary = deriveTeamIntegrationsSummary({
      bindingKind: "script",
      connectorCatalog: {
        homeDirectory: "actor://connector-catalog",
        filePath: "actor://connector-catalog/connectors",
        fileExists: true,
        connectors: [],
      },
      teamWorkflowRoles: [],
      workspaceSettings: {
        runtimeBaseUrl: "https://runtime.aevatar.test",
        directories: [],
      },
    });

    expect(summary.teamRoleUsageStatus).toBe("not_applicable");
    expect(summary.teamRoleUsageSummary).toContain("Script-bound");
  });

  it("marks workflow role usage unavailable when the bound workflow source cannot be loaded", () => {
    const summary = deriveTeamIntegrationsSummary({
      bindingKind: "workflow",
      connectorCatalog: {
        homeDirectory: "actor://connector-catalog",
        filePath: "actor://connector-catalog/connectors",
        fileExists: true,
        connectors: [],
      },
      teamWorkflowRoles: null,
      workspaceSettings: {
        runtimeBaseUrl: "https://runtime.aevatar.test",
        directories: [],
      },
    });

    expect(summary.teamRoleUsageStatus).toBe("unavailable");
    expect(summary.teamRoleUsageSummary).toContain("could not be loaded");
  });
});
