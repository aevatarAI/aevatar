import type {
  StudioConnectorCatalog,
  StudioConnectorDefinition,
  StudioScopeBindingImplementationKind,
  StudioWorkflowDocument,
  StudioWorkflowRoleDocument,
  StudioWorkspaceSettings,
} from "@/shared/studio/models";
import { formatStudioScopeBindingImplementationKind } from "@/shared/studio/models";

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
  bindingKind: StudioScopeBindingImplementationKind;
  connectorCount: number;
  directoryCount: number;
  items: TeamIntegrationItem[];
  linkedConnectorCount: number;
  roleCount: number;
  roleReferenceCount: number;
  runtimeBaseUrl: string;
  runtimeHostLabel: string;
  summary: string;
  teamRoleUsageStatus:
    | "loading"
    | "resolved"
    | "unavailable"
    | "not_applicable";
  teamRoleUsageSummary: string;
  unresolvedReferences: string[];
  workflowDocumentCount: number;
  workspaceSummary: string;
};

export type TeamWorkflowRoleBinding = {
  connectors: string[];
  name: string;
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

function asWorkflowRoleDocument(
  value: unknown,
): StudioWorkflowRoleDocument | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return null;
  }

  return value as StudioWorkflowRoleDocument;
}

function toConnectorNames(value: unknown): string[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((entry) => {
      if (typeof entry === "string") {
        return trimOptional(entry);
      }

      if (!entry || typeof entry !== "object" || Array.isArray(entry)) {
        return "";
      }

      const record = entry as Record<string, unknown>;
      return trimOptional(
        typeof record.name === "string"
          ? record.name
          : typeof record.id === "string"
            ? record.id
            : "",
      );
    })
    .filter(Boolean);
}

export function deriveTeamWorkflowRoleBindings(
  documents: readonly StudioWorkflowDocument[],
): TeamWorkflowRoleBinding[] {
  const roleConnectorMap = new Map<string, Set<string>>();

  documents.forEach((document, documentIndex) => {
    const roles = Array.isArray(document.roles) ? document.roles : [];
    roles.forEach((role, roleIndex) => {
      const roleDocument = asWorkflowRoleDocument(role);
      if (!roleDocument) {
        return;
      }

      const roleName =
        trimOptional(roleDocument.name) ||
        trimOptional(roleDocument.id) ||
        `role-${documentIndex + 1}-${roleIndex + 1}`;
      const connectorNames = toConnectorNames(roleDocument.connectors);
      if (!roleConnectorMap.has(roleName)) {
        roleConnectorMap.set(roleName, new Set<string>());
      }

      const connectorSet = roleConnectorMap.get(roleName);
      connectorNames.forEach((connectorName) => {
        connectorSet?.add(connectorName);
      });
    });
  });

  return [...roleConnectorMap.entries()]
    .map(([name, connectors]) => ({
      name,
      connectors: [...connectors].sort(),
    }))
    .sort((left, right) => left.name.localeCompare(right.name));
}

export function deriveTeamIntegrationsSummary(input: {
  bindingKind: StudioScopeBindingImplementationKind;
  connectorCatalog: StudioConnectorCatalog | null;
  teamWorkflowRoles: TeamWorkflowRoleBinding[] | null | undefined;
  workflowDocumentCount?: number;
  workspaceSettings: StudioWorkspaceSettings | null;
}): TeamIntegrationsSummary {
  const runtimeBaseUrl = trimOptional(input.workspaceSettings?.runtimeBaseUrl);
  const runtimeHostLabel = summarizeRuntimeBase(runtimeBaseUrl);
  const directoryCount = input.workspaceSettings?.directories.length ?? 0;
  const connectors = input.connectorCatalog?.connectors ?? [];
  const teamWorkflowRoles = input.teamWorkflowRoles ?? [];
  const workflowDocumentCount = input.workflowDocumentCount ?? 0;

  const connectorRoleMap = new Map<string, Set<string>>();
  teamWorkflowRoles.forEach((role) => {
    const roleName = trimOptional(role.name) || "role";
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
  const roleCount = teamWorkflowRoles.length;
  const available =
    runtimeBaseUrl.length > 0 ||
    directoryCount > 0 ||
    connectors.length > 0 ||
    teamWorkflowRoles.length > 0;

  const bindingKindLabel = formatStudioScopeBindingImplementationKind(
    input.bindingKind,
  );
  const teamRoleUsageStatus: TeamIntegrationsSummary["teamRoleUsageStatus"] =
    input.bindingKind !== "workflow"
      ? "not_applicable"
      : input.teamWorkflowRoles === undefined
        ? "loading"
        : input.teamWorkflowRoles === null
        ? "unavailable"
        : "resolved";

  let teamRoleUsageSummary = "";
  if (teamRoleUsageStatus === "loading") {
    teamRoleUsageSummary =
      "Loading team-scoped connector usage from the current workflow";
  } else if (teamRoleUsageStatus === "resolved") {
    if (roleReferenceCount > 0) {
      teamRoleUsageSummary = `${roleReferenceCount} team-scoped connector references across ${roleCount} workflow roles`;
    } else if (roleCount > 0) {
      teamRoleUsageSummary = `${roleCount} workflow roles inspected for the current team`;
    } else {
      teamRoleUsageSummary =
        workflowDocumentCount > 0
          ? "Current workflow has no connector-bearing roles"
          : "No workflow role source is visible for this team";
    }
  } else if (teamRoleUsageStatus === "not_applicable") {
    teamRoleUsageSummary = `${bindingKindLabel}-bound teams do not expose workflow role connector usage`;
  } else {
    teamRoleUsageSummary =
      "Current workflow source could not be loaded for team-scoped role usage";
  }

  let summary = "No integration facts are currently visible for this team.";
  if (teamRoleUsageStatus === "loading" && connectors.length > 0) {
    summary =
      "Workspace connector definitions are visible while the current team's workflow connector usage is still loading.";
  } else if (
    teamRoleUsageStatus === "resolved" &&
    unresolvedReferences.length > 0
  ) {
    summary = `${unresolvedReferences.length} team-scoped connector references are visible in the current workflow, but the matching connector definitions are not currently loaded.`;
  } else if (
    teamRoleUsageStatus === "resolved" &&
    connectors.length > 0 &&
    linkedConnectorCount > 0
  ) {
    summary = `${linkedConnectorCount} of ${connectors.length} connector definitions are referenced by the current team's workflow roles.`;
  } else if (teamRoleUsageStatus === "resolved" && connectors.length > 0) {
    summary =
      roleCount > 0
        ? `${connectors.length} connector definitions are available in this workspace, but the current team's workflow roles are not explicitly using them yet.`
        : `${connectors.length} connector definitions are available in this workspace, but the current team's workflow does not currently expose any connector-bearing roles.`;
  } else if (teamRoleUsageStatus === "not_applicable" && connectors.length > 0) {
    summary = `This team is ${bindingKindLabel.toLowerCase()}-bound, so team-scoped role usage is not available. Workspace connector definitions are shown for context only.`;
  } else if (teamRoleUsageStatus === "unavailable" && connectors.length > 0) {
    summary =
      "Workspace connector definitions are visible, but the current team's workflow source could not be loaded, so team-scoped connector usage is unavailable.";
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
    bindingKind: input.bindingKind,
    connectorCount: connectors.length,
    directoryCount,
    items,
    linkedConnectorCount,
    roleCount,
    roleReferenceCount,
    runtimeBaseUrl,
    runtimeHostLabel,
    summary,
    teamRoleUsageStatus,
    teamRoleUsageSummary,
    unresolvedReferences,
    workflowDocumentCount,
    workspaceSummary,
  };
}
