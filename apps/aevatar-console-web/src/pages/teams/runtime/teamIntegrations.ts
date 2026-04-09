import type {
  StudioConnectorCatalog,
  StudioConnectorDefinition,
  StudioRoleCatalog,
  StudioWorkspaceSettings,
} from "@/shared/studio/models";

export type TeamIntegrationItem = {
  key: string;
  name: string;
  type: string;
  enabled: boolean;
  summary: string;
  usedByRoles: string[];
};

export type TeamIntegrationsSummary = {
  available: boolean;
  connectorCount: number;
  directoryCount: number;
  items: TeamIntegrationItem[];
  linkedConnectorCount: number;
  roleReferenceCount: number;
  runtimeBaseUrl: string;
  runtimeHostLabel: string;
  summary: string;
  unresolvedReferences: string[];
  workspaceSummary: string;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function summarizeRuntimeBase(runtimeBaseUrl: string): string {
  const normalized = trimOptional(runtimeBaseUrl);
  if (!normalized) {
    return "Not configured";
  }

  try {
    return new URL(normalized).host || normalized;
  } catch {
    return normalized;
  }
}

function describeConnector(connector: StudioConnectorDefinition): string {
  if (connector.type === "http") {
    return trimOptional(connector.http?.baseUrl) || "HTTP connector";
  }

  if (connector.type === "cli") {
    return trimOptional(connector.cli?.command) || "CLI connector";
  }

  if (connector.type === "mcp") {
    return (
      trimOptional(connector.mcp?.serverName) ||
      trimOptional(connector.mcp?.command) ||
      "MCP connector"
    );
  }

  return "Connector definition";
}

export function deriveTeamIntegrationsSummary(input: {
  connectorCatalog: StudioConnectorCatalog | null;
  roleCatalog: StudioRoleCatalog | null;
  workspaceSettings: StudioWorkspaceSettings | null;
}): TeamIntegrationsSummary {
  const runtimeBaseUrl = trimOptional(input.workspaceSettings?.runtimeBaseUrl);
  const runtimeHostLabel = summarizeRuntimeBase(runtimeBaseUrl);
  const directoryCount = input.workspaceSettings?.directories.length ?? 0;
  const connectors = input.connectorCatalog?.connectors ?? [];
  const roles = input.roleCatalog?.roles ?? [];

  const connectorRoleMap = new Map<string, Set<string>>();
  roles.forEach((role) => {
    const roleName = trimOptional(role.name) || trimOptional(role.id) || "role";
    role.connectors.forEach((connectorName) => {
      const normalizedName = trimOptional(connectorName).toLowerCase();
      if (!normalizedName) {
        return;
      }

      if (!connectorRoleMap.has(normalizedName)) {
        connectorRoleMap.set(normalizedName, new Set<string>());
      }

      connectorRoleMap.get(normalizedName)?.add(roleName);
    });
  });

  const connectorNameSet = new Set(
    connectors
      .map((connector) => trimOptional(connector.name).toLowerCase())
      .filter(Boolean),
  );

  const unresolvedReferences = [...connectorRoleMap.keys()]
    .filter((connectorName) => !connectorNameSet.has(connectorName))
    .sort();

  const items = connectors
    .map((connector) => {
      const normalizedName = trimOptional(connector.name).toLowerCase();
      const usedByRoles = [
        ...(connectorRoleMap.get(normalizedName) ?? new Set<string>()),
      ].sort();

      return {
        key: `${connector.type}:${connector.name}`,
        name: trimOptional(connector.name) || "Unnamed connector",
        type: trimOptional(connector.type) || "unknown",
        enabled: connector.enabled !== false,
        summary: describeConnector(connector),
        usedByRoles,
      };
    })
    .sort((left, right) => {
      if (left.usedByRoles.length !== right.usedByRoles.length) {
        return right.usedByRoles.length - left.usedByRoles.length;
      }

      if (left.enabled !== right.enabled) {
        return Number(right.enabled) - Number(left.enabled);
      }

      return left.name.localeCompare(right.name);
    });

  const linkedConnectorCount = items.filter(
    (connector) => connector.usedByRoles.length > 0,
  ).length;
  const roleReferenceCount = [...connectorRoleMap.values()].reduce(
    (count, names) => count + names.size,
    0,
  );
  const available =
    runtimeBaseUrl.length > 0 ||
    directoryCount > 0 ||
    connectors.length > 0 ||
    roles.length > 0;

  let summary = "No integration facts are currently visible for this team.";
  if (connectors.length > 0 && linkedConnectorCount > 0) {
    summary = `${linkedConnectorCount} of ${connectors.length} connector definitions are referenced by saved team roles.`;
  } else if (connectors.length > 0) {
    summary = `${connectors.length} connector definitions are available in this workspace, but no saved team role is explicitly using them yet.`;
  } else if (unresolvedReferences.length > 0) {
    summary = `${unresolvedReferences.length} saved connector references are visible, but the matching connector definitions are not currently loaded.`;
  } else if (available) {
    summary =
      "Workspace integration context is visible, but no connector definitions are currently available.";
  }

  const workspaceSummary = [
    runtimeBaseUrl ? `Runtime ${runtimeBaseUrl}` : "Runtime base URL unavailable",
    directoryCount > 0
      ? `${directoryCount} workflow directories visible`
      : "No workflow directories visible",
  ].join(" · ");

  return {
    available,
    connectorCount: connectors.length,
    directoryCount,
    items,
    linkedConnectorCount,
    roleReferenceCount,
    runtimeBaseUrl,
    runtimeHostLabel,
    summary,
    unresolvedReferences,
    workspaceSummary,
  };
}
