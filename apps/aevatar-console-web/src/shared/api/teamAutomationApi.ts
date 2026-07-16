import { authFetch } from "@/shared/auth/fetch";
import { jsonBody, withQuery } from "./http/client";
import {
  expectArray,
  expectRecord,
  readBoolean,
  readNullableString,
  readNumber,
  readOptionalArray,
  readString,
} from "./http/decoders";
import { readResponseErrorDetails } from "./http/error";

export type TeamAutomationRoute = {
  readonly scopeId: string;
  readonly teamId: string;
  readonly memberId: string;
};

export type TeamAutomationCreateDraft = TeamAutomationRoute & {
  readonly displayName: string;
  readonly prompt: string;
  readonly cronExpression: string;
  readonly timezone?: string;
  readonly enabled: boolean;
};

export type TeamAutomationGrant = {
  readonly grantId: string;
  readonly targetId: string;
  readonly displayName: string;
  readonly permission: string;
  readonly role?: TeamAutomationNodeRole;
};

export type TeamAutomationNodeRole = "primary" | "fallback";

export type TeamAutomationDisclosure =
  | "dedicated_credential"
  | "aevatar_secret_custody"
  | "browser_never_receives_secret"
  | "delete_revokes_credential"
  | "pause_resume_preserves_credential"
  | "node_ids_are_permission_set";

export type TeamAutomationOperationIdentity = {
  readonly operationId: string;
  readonly idempotencyKey: string;
};

export type TeamAutomationCredentialPlan = {
  readonly mode: "dedicated-per-schedule";
  readonly hostedBy: "Aevatar";
  readonly browserReceivesRawKey: false;
  readonly scopes: readonly string[];
  readonly allowAllServices: false;
  readonly allowAllNodes: false;
  readonly expiresAt: string;
};

export type TeamAutomationPermissionReview = {
  readonly status: "ready" | "plan-changed";
  readonly permissionDigest: string;
  readonly policyVersion: string;
  readonly credentialPlan: TeamAutomationCredentialPlan;
  readonly serviceGrants: readonly TeamAutomationGrant[];
  readonly nodeGrants: readonly TeamAutomationGrant[];
  readonly disclosures: readonly TeamAutomationDisclosure[];
  readonly warning?: string;
};

export type TeamAutomationAuthorizationStatus =
  | "provisioning_pending"
  | "active"
  | "needs_authorization"
  | "replacement_pending"
  | "deleting"
  | "revocation_pending"
  | "failed";

export type TeamAutomationView = TeamAutomationRoute & {
  readonly scheduleId: string;
  readonly publishedServiceId: string;
  readonly credentialSourceKind: "scheduled_invocation_agent_key";
  readonly displayName: string;
  readonly prompt: string;
  readonly cronExpression: string;
  readonly timezone: string;
  readonly enabled: boolean;
  readonly authorizationStatus: TeamAutomationAuthorizationStatus;
  readonly credentialExpiresAtUtc: string | null;
  readonly lastAuthorizationErrorCode: string;
  readonly operationId: string;
  readonly credentialGeneration: number;
  readonly revocationPending: boolean;
  readonly nextFireAt: string | null;
  readonly lastFireAt: string | null;
  readonly stateVersion: number;
  readonly updatedAt: string;
};

export type TeamAutomationListResult = {
  readonly items: readonly TeamAutomationView[];
  readonly nextCursor: string | null;
  readonly totalCount: number | null;
};

export type TeamAutomationMutationReceipt = {
  readonly accepted: boolean;
  readonly status: "accepted" | "pending";
  readonly scheduleId: string;
  readonly operationId: string;
  readonly commandId: string;
};

export type TeamAutomationUpdateInput = {
  readonly displayName: string;
  readonly prompt: string;
  readonly cronExpression: string;
  readonly timezone?: string;
  readonly enabled: boolean;
};

export class TeamAutomationApiError extends Error {
  readonly code?: string;
  readonly status: number;

  constructor(message: string, status: number, code?: string) {
    super(message);
    this.name = "TeamAutomationApiError";
    this.status = status;
    this.code = code;
  }
}

function field(record: Record<string, unknown>, ...keys: string[]): unknown {
  for (const key of keys) {
    if (key in record) {
      return record[key];
    }
  }

  return undefined;
}

function optionalString(
  record: Record<string, unknown>,
  keys: string[],
  fallback = "",
): string {
  const value = field(record, ...keys);
  if (value === undefined || value === null) {
    return fallback;
  }
  if (typeof value !== "string") {
    throw new Error(`${keys[0]} must be a string.`);
  }
  return value;
}

function requiredString(
  record: Record<string, unknown>,
  keys: string[],
  label: string,
): string {
  const value = readString(record, keys, label).trim();
  if (!value) {
    throw new Error(`${label} must not be empty.`);
  }
  return value;
}

function requiredNonNegativeInteger(
  record: Record<string, unknown>,
  keys: string[],
  label: string,
): number {
  const value = readNumber(record, keys, label);
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new Error(`${label} must be a non-negative safe integer.`);
  }
  return value;
}

function decodeTimestamp(value: unknown, label: string): string {
  if (typeof value === "string") {
    if (!value.trim() || !Number.isFinite(Date.parse(value))) {
      throw new Error(`${label} must be a valid ISO timestamp.`);
    }
    return value;
  }
  const record = expectRecord(value, label);
  const seconds = field(record, "seconds", "Seconds");
  const nanos = field(record, "nanos", "Nanos");
  if ((typeof seconds !== "number" && typeof seconds !== "string") ||
      (nanos !== undefined && typeof nanos !== "number")) {
    throw new Error(`${label} must be an ISO timestamp or protobuf Timestamp.`);
  }
  const normalizedSeconds = Number(seconds);
  const normalizedNanos = Number(nanos ?? 0);
  if (
    !Number.isSafeInteger(normalizedSeconds) ||
    !Number.isSafeInteger(normalizedNanos) ||
    normalizedNanos < 0 ||
    normalizedNanos > 999_999_999
  ) {
    throw new Error(`${label} contains an invalid protobuf Timestamp.`);
  }
  const milliseconds = normalizedSeconds * 1_000 + normalizedNanos / 1_000_000;
  return new Date(milliseconds).toISOString();
}

function decodeNullableTimestamp(value: unknown, label: string): string | null {
  return value === undefined || value === null ? null : decodeTimestamp(value, label);
}

function normalizeStatus(value: unknown): TeamAutomationAuthorizationStatus {
  const normalized = String(value ?? "").trim().toLowerCase();
  const status = normalized
    .replace(/^studio_member_automation_status_/, "")
    .replace(/^team_automation_status_/, "");
  switch (status) {
    case "provisioning_pending":
    case "active":
    case "needs_authorization":
    case "replacement_pending":
    case "deleting":
    case "revocation_pending":
    case "failed":
      return status;
    default:
      throw new Error(`Unknown Team automation status: ${String(value)}.`);
  }
}

function normalizeScope(value: unknown): string {
  const normalized = String(value ?? "").trim().toLowerCase();
  if (normalized === "1" || normalized.endsWith("_read")) {
    return "read";
  }
  if (normalized === "2" || normalized.endsWith("_proxy")) {
    return "proxy";
  }
  throw new Error(`Unknown NyxID credential scope: ${String(value)}.`);
}

function normalizeDisclosure(value: unknown): TeamAutomationDisclosure {
  const normalized = String(value ?? "").trim().toLowerCase();
  const numeric: Record<string, TeamAutomationDisclosure> = {
    "1": "dedicated_credential",
    "2": "aevatar_secret_custody",
    "3": "browser_never_receives_secret",
    "4": "delete_revokes_credential",
    "5": "pause_resume_preserves_credential",
    "6": "node_ids_are_permission_set",
  };
  const disclosure =
    numeric[normalized] ?? normalized.replace(/^scheduled_invocation_disclosure_/, "");
  switch (disclosure) {
    case "dedicated_credential":
    case "aevatar_secret_custody":
    case "browser_never_receives_secret":
    case "delete_revokes_credential":
    case "pause_resume_preserves_credential":
    case "node_ids_are_permission_set":
      return disclosure;
    default:
      throw new Error(`Unknown Team automation disclosure: ${String(value)}.`);
  }
}

function normalizeNodeRole(value: unknown): TeamAutomationNodeRole {
  const normalized = String(value ?? "").trim().toLowerCase();
  if (normalized === "1" || normalized.endsWith("_primary")) {
    return "primary";
  }
  if (normalized === "2" || normalized.endsWith("_fallback")) {
    return "fallback";
  }
  throw new Error(`Unknown NyxID node role: ${String(value)}.`);
}

function normalizeCredentialSourceKind(
  value: unknown,
): TeamAutomationView["credentialSourceKind"] {
  const normalized = String(value ?? "")
    .trim()
    .replace(/[-_\s]/g, "")
    .toLowerCase();
  if (normalized === "6" || normalized === "scheduledinvocationagentkey") {
    return "scheduled_invocation_agent_key";
  }
  throw new Error(`Unknown Team automation credential source: ${String(value)}.`);
}

const requiredDisclosures: readonly TeamAutomationDisclosure[] = [
  "dedicated_credential",
  "aevatar_secret_custody",
  "browser_never_receives_secret",
  "delete_revokes_credential",
  "pause_resume_preserves_credential",
  "node_ids_are_permission_set",
];

function assertRouteMatches(
  actual: TeamAutomationRoute,
  expected: TeamAutomationRoute,
  label: string,
): void {
  if (
    actual.scopeId !== expected.scopeId ||
    actual.teamId !== expected.teamId ||
    actual.memberId !== expected.memberId
  ) {
    throw new Error(`${label} does not belong to the requested Team member route.`);
  }
}

function decodePermissionReview(
  value: unknown,
  label = "StudioMemberWorkflowAuthorizationResult",
  expectedRoute?: TeamAutomationRoute,
): TeamAutomationPermissionReview {
  const result = expectRecord(value, label);
  const success = readBoolean(result, ["success", "Success"], `${label}.success`);
  const failureCodeValue = field(result, "failureCode", "FailureCode");
  const failureCode =
    failureCodeValue === undefined || failureCodeValue === null
      ? ""
      : String(failureCodeValue);
  const detail = optionalString(result, ["detail", "Detail"]);
  const planValue = field(result, "plan", "Plan");
  if (!success || !planValue) {
    if (
      failureCode === "12" ||
      failureCode.toLowerCase().includes("authorization_plan_changed")
    ) {
      return {
        status: "plan-changed",
        permissionDigest: "",
        policyVersion: "",
        credentialPlan: {
          mode: "dedicated-per-schedule",
          hostedBy: "Aevatar",
          browserReceivesRawKey: false,
          scopes: [],
          allowAllServices: false,
          allowAllNodes: false,
          expiresAt: "",
        },
        serviceGrants: [],
        nodeGrants: [],
        disclosures: [],
        warning: detail || failureCode,
      };
    }
    throw new Error(detail || failureCode || "Team Automation preflight failed.");
  }

  const plan = expectRecord(planValue, `${label}.plan`);
  const invocationTarget = expectRecord(
    field(plan, "invocationTarget", "InvocationTarget"),
    `${label}.plan.invocationTarget`,
  );
  const studioMember = expectRecord(
    field(invocationTarget, "studioMember", "StudioMember"),
    `${label}.plan.invocationTarget.studioMember`,
  );
  const planRoute = {
    scopeId: requiredString(
      studioMember,
      ["scopeId", "ScopeId"],
      `${label}.plan.invocationTarget.studioMember.scopeId`,
    ),
    teamId: requiredString(
      studioMember,
      ["teamId", "TeamId"],
      `${label}.plan.invocationTarget.studioMember.teamId`,
    ),
    memberId: requiredString(
      studioMember,
      ["memberId", "MemberId"],
      `${label}.plan.invocationTarget.studioMember.memberId`,
    ),
  };
  if (expectedRoute) {
    assertRouteMatches(planRoute, normalizeRoute(expectedRoute), `${label}.plan`);
  }
  const credentialPolicy = expectRecord(
    field(plan, "credentialPolicy", "CredentialPolicy"),
    `${label}.plan.credentialPolicy`,
  );
  const serviceGrants = readOptionalArray(
    plan,
    ["nyxIdServiceGrants", "NyxIdServiceGrants"],
    `${label}.plan.nyxIdServiceGrants`,
    (entry, grantLabel) => {
      const grant = expectRecord(entry, grantLabel ?? "NyxIdServiceGrant");
      const targetId = requiredString(
        grant,
        ["userServiceId", "UserServiceId"],
        `${grantLabel}.userServiceId`,
      );
      const serviceSlug = optionalString(grant, ["serviceSlug", "ServiceSlug"]);
      return {
        grantId: `service:${targetId}`,
        targetId,
        displayName:
          optionalString(grant, ["displayName", "DisplayName"]) || serviceSlug || targetId,
        permission: serviceSlug ? `NyxID service ${serviceSlug}` : "NyxID service access",
      };
    },
  );
  const nodeGrants = readOptionalArray(
    plan,
    ["nyxIdNodeGrants", "NyxIdNodeGrants"],
    `${label}.plan.nyxIdNodeGrants`,
    (entry, grantLabel) => {
      const grant = expectRecord(entry, grantLabel ?? "NyxIdNodeGrant");
      const targetId = requiredString(
        grant,
        ["nodeId", "NodeId"],
        `${grantLabel}.nodeId`,
      );
      const userServiceId = requiredString(
        grant,
        ["userServiceId", "UserServiceId"],
        `${grantLabel}.userServiceId`,
      );
      const role = normalizeNodeRole(field(grant, "role", "Role"));
      return {
        grantId: `node:${userServiceId}:${targetId}`,
        targetId,
        displayName: optionalString(grant, ["displayName", "DisplayName"]) || targetId,
        permission: `NyxID ${role} node for ${userServiceId}`,
        role,
      };
    },
  );
  const scopesValue = field(credentialPolicy, "scopes", "Scopes");
  const scopes = expectArray(
    scopesValue,
    `${label}.plan.credentialPolicy.scopes`,
    normalizeScope,
  );
  const disclosuresValue = field(plan, "disclosures", "Disclosures") ?? [];
  const disclosures = expectArray(
    disclosuresValue,
    `${label}.plan.disclosures`,
    normalizeDisclosure,
  );
  const allowAllServices = readBoolean(
    credentialPolicy,
    ["allowAllServices", "AllowAllServices"],
    `${label}.plan.credentialPolicy.allowAllServices`,
  );
  const allowAllNodes = readBoolean(
    credentialPolicy,
    ["allowAllNodes", "AllowAllNodes"],
    `${label}.plan.credentialPolicy.allowAllNodes`,
  );
  if (allowAllServices || allowAllNodes) {
    throw new Error("Team Automation authorization must use exact service and node grants.");
  }
  if (!scopes.includes("read") || !scopes.includes("proxy")) {
    throw new Error("Team Automation authorization requires read and proxy scopes.");
  }
  if (!disclosures.includes("browser_never_receives_secret")) {
    throw new Error("Team Automation authorization did not prove browser secret isolation.");
  }
  const missingDisclosures = requiredDisclosures.filter(
    (disclosure) => !disclosures.includes(disclosure),
  );
  if (missingDisclosures.length > 0) {
    throw new Error(
      `Team Automation authorization is missing required disclosures: ${missingDisclosures.join(", ")}.`,
    );
  }

  return {
    status: "ready",
    permissionDigest: requiredString(
      plan,
      ["permissionDigest", "PermissionDigest"],
      `${label}.plan.permissionDigest`,
    ),
    policyVersion: requiredString(
      credentialPolicy,
      ["policyVersion", "PolicyVersion"],
      `${label}.plan.credentialPolicy.policyVersion`,
    ),
    credentialPlan: {
      mode: "dedicated-per-schedule",
      hostedBy: "Aevatar",
      browserReceivesRawKey: false,
      scopes,
      allowAllServices: false,
      allowAllNodes: false,
      expiresAt: decodeTimestamp(
        field(credentialPolicy, "expiresAt", "ExpiresAt"),
        `${label}.plan.credentialPolicy.expiresAt`,
      ),
    },
    serviceGrants,
    nodeGrants,
    disclosures,
  };
}

function decodeView(value: unknown, label = "StudioMemberAutomationView"): TeamAutomationView {
  const record = expectRecord(value, label);
  return {
    scopeId: requiredString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
    teamId: requiredString(record, ["teamId", "TeamId"], `${label}.teamId`),
    memberId: requiredString(record, ["memberId", "MemberId"], `${label}.memberId`),
    scheduleId: requiredString(record, ["scheduleId", "ScheduleId"], `${label}.scheduleId`),
    publishedServiceId: requiredString(
      record,
      ["publishedServiceId", "PublishedServiceId"],
      `${label}.publishedServiceId`,
    ),
    credentialSourceKind: normalizeCredentialSourceKind(
      field(record, "credentialSourceKind", "CredentialSourceKind"),
    ),
    displayName: readString(record, ["displayName", "DisplayName"], `${label}.displayName`),
    prompt: readString(record, ["prompt", "Prompt"], `${label}.prompt`),
    cronExpression: requiredString(
      record,
      ["scheduleCron", "ScheduleCron", "cronExpression", "CronExpression"],
      `${label}.scheduleCron`,
    ),
    timezone: requiredString(
      record,
      ["scheduleTimezone", "ScheduleTimezone", "timezone", "Timezone"],
      `${label}.scheduleTimezone`,
    ),
    enabled: readBoolean(record, ["enabled", "Enabled"], `${label}.enabled`),
    authorizationStatus: normalizeStatus(
      field(record, "authorizationStatus", "AuthorizationStatus", "status", "Status"),
    ),
    credentialExpiresAtUtc: decodeNullableTimestamp(
      field(record, "credentialExpiresAtUtc", "CredentialExpiresAtUtc"),
      `${label}.credentialExpiresAtUtc`,
    ),
    lastAuthorizationErrorCode: readString(
      record,
      ["lastAuthorizationErrorCode", "LastAuthorizationErrorCode"],
      `${label}.lastAuthorizationErrorCode`,
    ),
    operationId: requiredString(record, ["operationId", "OperationId"], `${label}.operationId`),
    credentialGeneration: requiredNonNegativeInteger(
      record,
      ["credentialGeneration", "CredentialGeneration"],
      `${label}.credentialGeneration`,
    ),
    revocationPending: readBoolean(
      record,
      ["revocationPending", "RevocationPending"],
      `${label}.revocationPending`,
    ),
    nextFireAt: decodeNullableTimestamp(
      field(record, "nextFireAt", "NextFireAt"),
      `${label}.nextFireAt`,
    ),
    lastFireAt: decodeNullableTimestamp(
      field(record, "lastFireAt", "LastFireAt"),
      `${label}.lastFireAt`,
    ),
    stateVersion: requiredNonNegativeInteger(
      record,
      ["stateVersion", "StateVersion"],
      `${label}.stateVersion`,
    ),
    updatedAt: decodeTimestamp(field(record, "updatedAt", "UpdatedAt"), `${label}.updatedAt`),
  };
}

function decodeList(value: unknown, label = "StudioMemberAutomationListResponse"): TeamAutomationListResult {
  const record = expectRecord(value, label);
  const totalCountValue = field(record, "totalCount", "TotalCount");
  return {
    items: expectArray(field(record, "items", "Items"), `${label}.items`, decodeView),
    nextCursor: readNullableString(
      record,
      ["nextCursor", "NextCursor"],
      `${label}.nextCursor`,
    ),
    totalCount:
      totalCountValue === null || totalCountValue === undefined
        ? null
        : readNumber(record, ["totalCount", "TotalCount"], `${label}.totalCount`),
  };
}

function decodeReceipt(
  value: unknown,
  label = "StudioMemberAutomationMutationReceipt",
): TeamAutomationMutationReceipt {
  const record = expectRecord(value, label);
  const accepted = readBoolean(record, ["accepted", "Accepted"], `${label}.accepted`);
  const statusValue = readString(record, ["status", "Status"], `${label}.status`)
    .trim()
    .toLowerCase();
  if (statusValue !== "accepted" && statusValue !== "pending") {
    throw new Error(`${label}.status is not a recognized admission state.`);
  }
  if (!accepted) {
    throw new TeamAutomationApiError(
      "Team automation command was not accepted.",
      409,
      "TEAM_AUTOMATION_NOT_ACCEPTED",
    );
  }
  return {
    accepted,
    status: statusValue,
    scheduleId: requiredString(record, ["scheduleId", "ScheduleId"], `${label}.scheduleId`),
    operationId: requiredString(record, ["operationId", "OperationId"], `${label}.operationId`),
    commandId: requiredString(record, ["commandId", "CommandId"], `${label}.commandId`),
  };
}

function normalizeRoute(route: TeamAutomationRoute): TeamAutomationRoute {
  const normalized = {
    scopeId: route.scopeId.trim(),
    teamId: route.teamId.trim(),
    memberId: route.memberId.trim(),
  };
  if (!normalized.scopeId || !normalized.teamId || !normalized.memberId) {
    throw new Error("Team automation route requires scopeId, teamId, and memberId.");
  }
  return normalized;
}

function basePath(route: TeamAutomationRoute): string {
  const normalized = normalizeRoute(route);
  return `/api/scopes/${encodeURIComponent(normalized.scopeId)}/teams/${encodeURIComponent(normalized.teamId)}/members/${encodeURIComponent(normalized.memberId)}/automations`;
}

function schedulePath(route: TeamAutomationRoute, scheduleId: string): string {
  const normalizedScheduleId = scheduleId.trim();
  if (!normalizedScheduleId) {
    throw new Error("Team automation scheduleId is required.");
  }
  return `${basePath(route)}/${encodeURIComponent(normalizedScheduleId)}`;
}

function encodeDraft(draft: TeamAutomationCreateDraft) {
  return {
    scheduleCron: draft.cronExpression.trim(),
    scheduleTimezone: draft.timezone?.trim() || undefined,
    prompt: draft.prompt.trim() || undefined,
    displayName: draft.displayName.trim() || undefined,
    enabled: draft.enabled,
  };
}

export function createTeamAutomationOperationIdentity(): TeamAutomationOperationIdentity {
  const uuid = globalThis.crypto?.randomUUID?.();
  const value = uuid ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
  return {
    operationId: `team-automation:${value}`,
    idempotencyKey: `team-automation:${value}`,
  };
}

function normalizeOperationIdentity(
  identity?: TeamAutomationOperationIdentity,
): TeamAutomationOperationIdentity {
  const resolved = identity ?? createTeamAutomationOperationIdentity();
  const operationId = resolved.operationId.trim();
  const idempotencyKey = resolved.idempotencyKey.trim();
  if (!operationId || !idempotencyKey) {
    throw new Error("Team automation operationId and idempotencyKey are required.");
  }
  return { operationId, idempotencyKey };
}

function decodeViewForRoute(
  value: unknown,
  expectedRoute: TeamAutomationRoute,
  label?: string,
): TeamAutomationView {
  const view = decodeView(value, label);
  assertRouteMatches(view, normalizeRoute(expectedRoute), label ?? "StudioMemberAutomationView");
  return view;
}

function decodeListForRoute(
  value: unknown,
  expectedRoute: TeamAutomationRoute,
  label?: string,
): TeamAutomationListResult {
  const result = decodeList(value, label);
  result.items.forEach((item, index) =>
    assertRouteMatches(
      item,
      normalizeRoute(expectedRoute),
      `${label ?? "StudioMemberAutomationListResponse"}.items[${index}]`,
    ),
  );
  return result;
}

async function requestTeamAutomation<T>(
  path: string,
  decoder: (value: unknown, label?: string) => T,
  init?: RequestInit,
): Promise<T> {
  const response = await authFetch(path, init);
  if (!response.ok) {
    const details = await readResponseErrorDetails(response);
    throw new TeamAutomationApiError(details.message, details.status, details.code);
  }
  return decoder(await response.json());
}

async function refreshAuthorizationCatalog(): Promise<void> {
  const response = await authFetch("/api/auth/nyxid/authorization-catalog:refresh", {
    method: "POST",
  });
  if (!response.ok) {
    const details = await readResponseErrorDetails(response);
    throw new TeamAutomationApiError(details.message, details.status, details.code);
  }

  const payload = expectRecord(
    await response.json(),
    "NyxIdAuthorizationCatalogRefreshResponse",
  );
  if (!readBoolean(payload, ["ready", "Ready"], "authorizationCatalog.ready")) {
    throw new TeamAutomationApiError(
      "NyxID authorization catalog is not ready.",
      503,
      optionalString(payload, ["failureCode", "FailureCode"], "NYXID_AUTHORIZATION_CATALOG_NOT_READY"),
    );
  }
}

function listTeamAutomations(
  route: TeamAutomationRoute,
  query?: { readonly cursor?: string; readonly take?: number },
): Promise<TeamAutomationListResult> {
  return requestTeamAutomation(
    withQuery(basePath(route), { cursor: query?.cursor, take: query?.take }),
    (value, label) => decodeListForRoute(value, route, label),
  );
}

async function listAllTeamAutomations(
  route: TeamAutomationRoute,
  query?: { readonly cursor?: string; readonly take?: number },
): Promise<TeamAutomationListResult> {
  const items: TeamAutomationView[] = [];
  const seenCursors = new Set<string>();
  let cursor = query?.cursor;
  let totalCount: number | null = null;

  while (true) {
    if (cursor) {
      if (seenCursors.has(cursor)) {
        throw new Error("Team automation list returned a repeated cursor.");
      }
      seenCursors.add(cursor);
    }

    const result = await listTeamAutomations(route, { ...query, cursor });
    items.push(...result.items);
    if (result.totalCount !== null) {
      totalCount = result.totalCount;
    }
    if (!result.nextCursor) {
      return { items, nextCursor: null, totalCount };
    }
    cursor = result.nextCursor;
  }
}

export const teamAutomationApi = {
  refreshAuthorizationCatalog,

  preflightCreate(draft: TeamAutomationCreateDraft): Promise<TeamAutomationPermissionReview> {
    return requestTeamAutomation(
      `${basePath(draft)}/preflight`,
      (value, label) => decodePermissionReview(value, label, draft),
      { method: "POST", ...jsonBody(encodeDraft(draft)) },
    );
  },

  list: listTeamAutomations,

  listAll: listAllTeamAutomations,

  get(route: TeamAutomationRoute, scheduleId: string): Promise<TeamAutomationView> {
    return requestTeamAutomation(
      schedulePath(route, scheduleId),
      (value, label) => decodeViewForRoute(value, route, label),
    );
  },

  create(
    draft: TeamAutomationCreateDraft,
    permissionDigest: string,
    policyVersion: string,
    operationIdentity?: TeamAutomationOperationIdentity,
  ): Promise<TeamAutomationMutationReceipt> {
    return requestTeamAutomation(basePath(draft), decodeReceipt, {
      method: "POST",
      ...jsonBody({
        ...encodeDraft(draft),
        credentialProvisioningKind: "dedicated_scheduled_invocation_agent_key",
        confirmedPermissionDigest: permissionDigest.trim(),
        confirmedPolicyVersion: policyVersion.trim(),
        ...normalizeOperationIdentity(operationIdentity),
      }),
    });
  },

  update(
    route: TeamAutomationRoute,
    scheduleId: string,
    input: TeamAutomationUpdateInput,
    operationIdentity?: TeamAutomationOperationIdentity,
  ): Promise<TeamAutomationMutationReceipt> {
    return requestTeamAutomation(schedulePath(route, scheduleId), decodeReceipt, {
      method: "PUT",
      ...jsonBody({
        scheduleCron: input.cronExpression.trim(),
        scheduleTimezone: input.timezone?.trim() || undefined,
        prompt: input.prompt.trim() || undefined,
        displayName: input.displayName.trim() || undefined,
        enabled: input.enabled,
        ...normalizeOperationIdentity(operationIdentity),
      }),
    });
  },

  reauthorize(
    route: TeamAutomationRoute,
    scheduleId: string,
    draft: TeamAutomationCreateDraft,
    permissionDigest: string,
    policyVersion: string,
    operationIdentity?: TeamAutomationOperationIdentity,
  ): Promise<TeamAutomationMutationReceipt> {
    return requestTeamAutomation(
      `${schedulePath(route, scheduleId)}/reauthorize`,
      decodeReceipt,
      {
        method: "POST",
        ...jsonBody({
          ...encodeDraft(draft),
          credentialProvisioningKind: "dedicated_scheduled_invocation_agent_key",
          confirmedPermissionDigest: permissionDigest.trim(),
          confirmedPolicyVersion: policyVersion.trim(),
          ...normalizeOperationIdentity(operationIdentity),
        }),
      },
    );
  },

  delete(
    route: TeamAutomationRoute,
    scheduleId: string,
    operationIdentity?: TeamAutomationOperationIdentity,
  ): Promise<TeamAutomationMutationReceipt> {
    return requestTeamAutomation(schedulePath(route, scheduleId), decodeReceipt, {
      method: "DELETE",
      ...jsonBody(normalizeOperationIdentity(operationIdentity)),
    });
  },

  pause(
    route: TeamAutomationRoute,
    scheduleId: string,
    operationIdentity?: TeamAutomationOperationIdentity,
  ): Promise<TeamAutomationMutationReceipt> {
    return requestTeamAutomation(`${schedulePath(route, scheduleId)}/pause`, decodeReceipt, {
      method: "POST",
      ...jsonBody(normalizeOperationIdentity(operationIdentity)),
    });
  },

  resume(
    route: TeamAutomationRoute,
    scheduleId: string,
    operationIdentity?: TeamAutomationOperationIdentity,
  ): Promise<TeamAutomationMutationReceipt> {
    return requestTeamAutomation(`${schedulePath(route, scheduleId)}/resume`, decodeReceipt, {
      method: "POST",
      ...jsonBody(normalizeOperationIdentity(operationIdentity)),
    });
  },

  runNow(
    route: TeamAutomationRoute,
    scheduleId: string,
    operationIdentity?: TeamAutomationOperationIdentity,
  ): Promise<TeamAutomationMutationReceipt> {
    return requestTeamAutomation(`${schedulePath(route, scheduleId)}/run-now`, decodeReceipt, {
      method: "POST",
      ...jsonBody(normalizeOperationIdentity(operationIdentity)),
    });
  },
};

export const teamAutomationApiDecoders = {
  permissionReview: decodePermissionReview,
  view: decodeView,
  list: decodeList,
  receipt: decodeReceipt,
};
