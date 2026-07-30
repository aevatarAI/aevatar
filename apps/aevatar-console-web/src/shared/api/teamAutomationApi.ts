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
import {
  encodeScheduledDispatchOwnerQuery,
  type ScheduledDispatchOwner,
} from "./scheduledDispatchApi";

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

type TeamAutomationGrantBase = {
  readonly grantId: string;
  readonly targetId: string;
  readonly displayName: string;
};

export type TeamAutomationNodeRole = "primary" | "fallback";

export type TeamAutomationServiceGrant = TeamAutomationGrantBase & {
  readonly kind: "service";
  readonly nodeGrantRequirement: "required" | "not_required";
  readonly nodeIds: readonly string[];
  readonly serviceSlug: string | null;
};

export type TeamAutomationNodeGrant = TeamAutomationGrantBase & {
  readonly kind: "node";
  readonly userServiceId: string;
};

export type TeamAutomationGrant =
  | TeamAutomationServiceGrant
  | TeamAutomationNodeGrant;

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
  readonly serviceGrants: readonly TeamAutomationServiceGrant[];
  readonly nodeGrants: readonly TeamAutomationNodeGrant[];
  readonly ownerLLMSelection: {
    readonly model: string;
    readonly nyxIdUserServiceId: string;
    readonly routeKind: "gateway" | "nyx_id_user_service";
    readonly routeValue: string;
    readonly serviceSlugSnapshot: string;
  };
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

export type TeamAutomationRevocationTrack =
  | "NotRequired"
  | "Pending"
  | "Completed"
  | "Failed";

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
  readonly nyxIdRevocationStatus: TeamAutomationRevocationTrack;
  readonly vaultRevocationStatus: TeamAutomationRevocationTrack;
  readonly ownerLLMRouteKind: string;
  readonly ownerLLMRoute: string;
  readonly ownerLLMUserServiceId: string;
  readonly ownerLLMServiceSlug: string;
  readonly ownerLLMModel: string;
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
  readonly preflightLocator?: string;
  readonly requiredStateVersion?: number;
  readonly retryable: boolean;
  readonly status: number;

  constructor(
    message: string,
    status: number,
    code?: string,
    options?: {
      readonly preflightLocator?: string;
      readonly requiredStateVersion?: number;
      readonly retryable?: boolean;
    },
  ) {
    super(message);
    this.name = "TeamAutomationApiError";
    this.status = status;
    this.code = code;
    this.preflightLocator = options?.preflightLocator;
    this.requiredStateVersion = options?.requiredStateVersion;
    this.retryable = options?.retryable ?? false;
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
  if (normalized === "1" || normalized === "nyx_id_credential_scope_read") {
    return "read";
  }
  if (normalized === "2" || normalized === "nyx_id_credential_scope_proxy") {
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

function normalizeNodeGrantRequirement(
  value: unknown,
): TeamAutomationServiceGrant["nodeGrantRequirement"] {
  const normalized = String(value ?? "").trim().toLowerCase();
  if (
    normalized === "1" ||
    normalized === "authorization_grant_requirement_required"
  ) {
    return "required";
  }
  if (
    normalized === "2" ||
    normalized === "authorization_grant_requirement_not_required"
  ) {
    return "not_required";
  }
  throw new Error(`Unknown NyxID node grant requirement: ${String(value)}.`);
}

function normalizeOwnerLLMRouteKind(value: unknown): "gateway" | "nyx_id_user_service" {
  const normalized = String(value ?? "").trim().toLowerCase();
  if (
    normalized === "1" ||
    normalized === "scheduled_invocation_owner_llm_route_kind_gateway"
  ) {
    return "gateway";
  }
  if (
    normalized === "2" ||
    normalized === "scheduled_invocation_owner_llm_route_kind_nyx_id_user_service"
  ) {
    return "nyx_id_user_service";
  }
  throw new Error(`Unknown Team automation owner LLM route kind: ${String(value)}.`);
}

function normalizeRevocationTrack(value: unknown): TeamAutomationRevocationTrack {
  const normalized = String(value ?? "").replace(/[_\s-]/g, "").toLowerCase();
  switch (normalized) {
    case "notrequired":
      return "NotRequired";
    case "pending":
      return "Pending";
    case "completed":
      return "Completed";
    case "failed":
      return "Failed";
    default:
      throw new Error(`Unknown Team automation revocation track: ${String(value)}.`);
  }
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
        ownerLLMSelection: {
          model: "",
          nyxIdUserServiceId: "",
          routeKind: "gateway",
          routeValue: "",
          serviceSlugSnapshot: "",
        },
        disclosures: [],
        warning: detail || failureCode,
      };
    }
    throw new Error(detail || failureCode || "Team Automation preflight failed.");
  }

  const plan = expectRecord(planValue, `${label}.plan`);
  requiredString(plan, ["schemaVersion", "SchemaVersion"], `${label}.plan.schemaVersion`);
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
      const nodeGrantRequirement = normalizeNodeGrantRequirement(
        field(grant, "nodeGrantRequirement", "NodeGrantRequirement"),
      );
      const nodeIds = readOptionalArray(
        grant,
        ["nodeIds", "NodeIds"],
        `${grantLabel}.nodeIds`,
        (nodeId, nodeLabel) => {
          if (typeof nodeId !== "string" || !nodeId.trim()) {
            throw new Error(`${nodeLabel} must be a non-empty string.`);
          }
          return nodeId.trim();
        },
      );
      return {
        grantId: `service:${targetId}`,
        kind: "service" as const,
        targetId,
        displayName:
          optionalString(grant, ["displayName", "DisplayName"]) || serviceSlug || targetId,
        nodeGrantRequirement,
        nodeIds,
        serviceSlug: serviceSlug || null,
      };
    },
  );
  const nodeGrants = serviceGrants.flatMap((grant) =>
    grant.nodeIds.map((targetId) => ({
      grantId: `node:${grant.targetId}:${targetId}`,
      kind: "node" as const,
      targetId,
      displayName: targetId,
      userServiceId: grant.targetId,
    })),
  );
  const ownerLLM = expectRecord(
    field(plan, "ownerLlmSelection", "OwnerLlmSelection", "ownerLLMSelection", "OwnerLLMSelection"),
    `${label}.plan.ownerLlmSelection`,
  );
  const ownerLLMSelection = {
    routeKind: normalizeOwnerLLMRouteKind(field(ownerLLM, "routeKind", "RouteKind")),
    routeValue: requiredString(ownerLLM, ["routeValue", "RouteValue"], `${label}.plan.ownerLlmSelection.routeValue`),
    nyxIdUserServiceId: optionalString(ownerLLM, ["nyxIdUserServiceId", "NyxIdUserServiceId"]),
    serviceSlugSnapshot: optionalString(ownerLLM, ["serviceSlugSnapshot", "ServiceSlugSnapshot"]),
    model: requiredString(ownerLLM, ["model", "Model"], `${label}.plan.ownerLlmSelection.model`),
  };
  if (ownerLLMSelection.routeKind === "nyx_id_user_service") {
    const matchingGrant = serviceGrants.find(
      (grant) => grant.targetId === ownerLLMSelection.nyxIdUserServiceId,
    );
    if (
      !matchingGrant ||
      ownerLLMSelection.routeValue !== ownerLLMSelection.nyxIdUserServiceId ||
      (ownerLLMSelection.serviceSlugSnapshot &&
        matchingGrant.serviceSlug !== ownerLLMSelection.serviceSlugSnapshot)
    ) {
      throw new Error("Team automation owner LLM route identity does not match its exact service grant.");
    }
  }
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
    ownerLLMSelection,
    disclosures,
  };
}

function decodeView(value: unknown, label = "ScheduledDispatchSummary"): TeamAutomationView {
  const record = expectRecord(value, label);
  const teamOwned = readBoolean(
    record,
    ["teamOwned", "TeamOwned"],
    `${label}.teamOwned`,
  );
  if (!teamOwned) {
    throw new Error(`${label} is not a Team-owned automation schedule.`);
  }

  return {
    scopeId: requiredString(
      record,
      ["teamOwnerScopeId", "TeamOwnerScopeId"],
      `${label}.teamOwnerScopeId`,
    ),
    teamId: requiredString(record, ["teamId", "TeamId"], `${label}.teamId`),
    memberId: requiredString(
      record,
      ["teamOwnerMemberId", "TeamOwnerMemberId"],
      `${label}.teamOwnerMemberId`,
    ),
    scheduleId: requiredString(record, ["scheduleId", "ScheduleId"], `${label}.scheduleId`),
    publishedServiceId: requiredString(
      record,
      ["serviceId", "ServiceId"],
      `${label}.serviceId`,
    ),
    credentialSourceKind: normalizeCredentialSourceKind(
      field(record, "credentialSourceKind", "CredentialSourceKind"),
    ),
    displayName: readString(record, ["displayName", "DisplayName"], `${label}.displayName`),
    prompt: readString(record, ["prompt", "Prompt"], `${label}.prompt`),
    cronExpression: requiredString(
      record,
      ["cronExpression", "CronExpression"],
      `${label}.cronExpression`,
    ),
    timezone: requiredString(
      record,
      ["timezone", "Timezone"],
      `${label}.timezone`,
    ),
    enabled: readBoolean(record, ["enabled", "Enabled"], `${label}.enabled`),
    authorizationStatus: normalizeStatus(
      field(record, "teamAutomationLifecycleStatus", "TeamAutomationLifecycleStatus"),
    ),
    credentialExpiresAtUtc: decodeNullableTimestamp(
      field(record, "credentialExpiresAt", "CredentialExpiresAt"),
      `${label}.credentialExpiresAt`,
    ),
    lastAuthorizationErrorCode: readString(
      record,
      ["lastAuthorizationErrorCode", "LastAuthorizationErrorCode"],
      `${label}.lastAuthorizationErrorCode`,
    ),
    operationId: requiredString(
      record,
      ["teamAutomationOperationId", "TeamAutomationOperationId"],
      `${label}.teamAutomationOperationId`,
    ),
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
    nyxIdRevocationStatus: normalizeRevocationTrack(
      field(record, "nyxIdRevocationStatus", "NyxIdRevocationStatus"),
    ),
    vaultRevocationStatus: normalizeRevocationTrack(
      field(record, "vaultRevocationStatus", "VaultRevocationStatus"),
    ),
    ownerLLMRouteKind: requiredString(
      record,
      ["ownerLlmRouteKind", "OwnerLlmRouteKind", "ownerLLMRouteKind", "OwnerLLMRouteKind"],
      `${label}.ownerLlmRouteKind`,
    ),
    ownerLLMRoute: requiredString(
      record,
      ["ownerLlmRoute", "OwnerLlmRoute", "ownerLLMRoute", "OwnerLLMRoute"],
      `${label}.ownerLlmRoute`,
    ),
    ownerLLMUserServiceId: readString(
      record,
      ["ownerLlmUserServiceId", "OwnerLlmUserServiceId", "ownerLLMUserServiceId", "OwnerLLMUserServiceId"],
      `${label}.ownerLlmUserServiceId`,
    ),
    ownerLLMServiceSlug: readString(
      record,
      ["ownerLlmServiceSlug", "OwnerLlmServiceSlug", "ownerLLMServiceSlug", "OwnerLLMServiceSlug"],
      `${label}.ownerLlmServiceSlug`,
    ),
    ownerLLMModel: requiredString(
      record,
      ["ownerLlmModel", "OwnerLlmModel", "ownerLLMModel", "OwnerLLMModel"],
      `${label}.ownerLlmModel`,
    ),
    stateVersion: requiredNonNegativeInteger(
      record,
      ["stateVersion", "StateVersion"],
      `${label}.stateVersion`,
    ),
    updatedAt: decodeTimestamp(field(record, "updatedAt", "UpdatedAt"), `${label}.updatedAt`),
  };
}

function decodeList(value: unknown, label = "ScheduledDispatchListResult"): TeamAutomationListResult {
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

function scheduleOwner(route: TeamAutomationRoute): ScheduledDispatchOwner {
  const normalized = normalizeRoute(route);
  return {
    kind: "studio_member_automation",
    scopeId: normalized.scopeId,
    teamId: normalized.teamId,
    memberId: normalized.memberId,
  };
}

function scheduleCollectionPath(
  route: TeamAutomationRoute,
  query?: { readonly cursor?: string; readonly take?: number },
): string {
  return withQuery("/api/schedules", {
    ...encodeScheduledDispatchOwnerQuery(scheduleOwner(route)),
    cursor: query?.cursor,
    includeTotalCount: true,
    take: query?.take,
  });
}

function scheduleDetailPath(route: TeamAutomationRoute, scheduleId: string): string {
  const normalizedScheduleId = scheduleId.trim();
  if (!normalizedScheduleId) {
    throw new Error("Team automation scheduleId is required.");
  }

  return withQuery(`/api/schedules/${encodeURIComponent(normalizedScheduleId)}`, {
    ...encodeScheduledDispatchOwnerQuery(scheduleOwner(route)),
  });
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
  assertRouteMatches(view, normalizeRoute(expectedRoute), label ?? "ScheduledDispatchSummary");
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
      `${label ?? "ScheduledDispatchListResult"}.items[${index}]`,
    ),
  );
  return result;
}

function decodeDetailForRoute(
  value: unknown,
  expectedRoute: TeamAutomationRoute,
  label = "ScheduledDispatchDetail",
): TeamAutomationView {
  const record = expectRecord(value, label);
  return decodeViewForRoute(
    field(record, "schedule", "Schedule"),
    expectedRoute,
    `${label}.schedule`,
  );
}

async function requestTeamAutomation<T>(
  path: string,
  decoder: (value: unknown, label?: string) => T,
  init?: RequestInit,
): Promise<T> {
  const response = await authFetch(path, init);
  if (!response.ok) {
    const details = await readResponseErrorDetails(response);
    throw new TeamAutomationApiError(details.message, details.status, details.code, details);
  }
  return decoder(await response.json());
}

async function refreshAuthorizationCatalog(): Promise<void> {
  const response = await authFetch("/api/auth/nyxid/authorization-catalog:refresh", {
    method: "POST",
  });
  if (!response.ok) {
    const details = await readResponseErrorDetails(response);
    throw new TeamAutomationApiError(details.message, details.status, details.code, details);
  }

  const payload = expectRecord(
    await response.json(),
    "NyxIdAuthorizationCatalogRefreshResponse",
  );
  if (readBoolean(payload, ["ready", "Ready"], "authorizationCatalog.ready")) {
    return;
  }

  const refreshStatus = requiredString(
    payload,
    ["refreshStatus", "RefreshStatus"],
    "authorizationCatalog.refreshStatus",
  );
  const refreshFailureCode = optionalString(
    payload,
    ["refreshFailureCode", "RefreshFailureCode"],
  );
  const visibilityStatus = requiredString(
    payload,
    ["visibilityStatus", "VisibilityStatus"],
    "authorizationCatalog.visibilityStatus",
  );
  const visibilityFailureCode = optionalString(
    payload,
    ["visibilityFailureCode", "VisibilityFailureCode"],
  );
  const requiredStateVersion = requiredNonNegativeInteger(
    payload,
    ["requiredStateVersion", "RequiredStateVersion"],
    "authorizationCatalog.requiredStateVersion",
  );

  if (visibilityStatus === "projection_pending") {
    throw new TeamAutomationApiError(
      visibilityFailureCode || "NyxID authorization catalog projection is pending.",
      response.status,
      "TEAM_AUTOMATION_AUTHORIZATION_PROJECTION_PENDING",
      { requiredStateVersion, retryable: true },
    );
  }
  if (refreshStatus === "superseded") {
    throw new TeamAutomationApiError(
      refreshFailureCode || "NyxID authorization catalog refresh was superseded.",
      response.status,
      "TEAM_AUTOMATION_AUTHORIZATION_REFRESH_SUPERSEDED",
      { requiredStateVersion, retryable: true },
    );
  }
  throw new TeamAutomationApiError(
    visibilityFailureCode ||
      refreshFailureCode ||
      "NyxID authorization catalog is not ready.",
    response.status,
    "TEAM_AUTOMATION_AUTHORIZATION_REFRESH_UNAVAILABLE",
    { requiredStateVersion, retryable: true },
  );
}

function listTeamAutomations(
  route: TeamAutomationRoute,
  query?: { readonly cursor?: string; readonly take?: number },
): Promise<TeamAutomationListResult> {
  return requestTeamAutomation(
    scheduleCollectionPath(route, query),
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
      scheduleDetailPath(route, scheduleId),
      (value, label) => decodeDetailForRoute(value, route, label),
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

  retryRevocation(
    route: TeamAutomationRoute,
    scheduleId: string,
  ): Promise<TeamAutomationMutationReceipt> {
    return requestTeamAutomation(
      `${schedulePath(route, scheduleId)}/retry-revocation`,
      decodeReceipt,
      {
        method: "POST",
      },
    );
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
