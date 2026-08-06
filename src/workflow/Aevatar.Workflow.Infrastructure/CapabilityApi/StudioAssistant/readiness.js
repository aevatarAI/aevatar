const SNAPSHOT_KEYS = new Set(["revision", "evaluatedAt", "capabilities"]);
const CAPABILITY_KEYS = new Set([
  "capabilityId",
  "label",
  "required",
  "status",
  "connectionState",
  "grantState",
  "requestedScopes",
  "managementUrl",
  "reasonCode",
]);
const STATUSES = new Set(["available", "missing", "cannot_use", "cannot_check"]);
const CONNECTION_STATES = new Set([
  "not_connected", "connecting", "verifying", "connected", "expired", "revoked", "unknown",
]);
const GRANT_STATES = new Set([
  "not_required", "granted", "partial", "missing", "expired", "revoked", "unknown",
]);
const SECRET_KEY = /(authorization|api[-_]?key|token|secret|password|credential|cookie)/i;
const SECRET_VALUE = /Bearer\s+\S+|\beyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b|nyx(?:id)?_[A-Za-z0-9_-]{8,}/i;

export class ReadinessValidationError extends Error {
  constructor(message) {
    super(message);
    this.name = "ReadinessValidationError";
    this.code = "READINESS_INVALID";
  }
}

export function normalizeReadinessSnapshot(input, { nyxidWebUrl }) {
  const snapshot = object(input, "Readiness snapshot");
  rejectSecrets(snapshot);
  exactKeys(snapshot, SNAPSHOT_KEYS, "Readiness snapshot");
  const revision = boundedString(snapshot.revision, "revision", 256);
  const evaluatedAt = timestamp(snapshot.evaluatedAt);
  if (!Array.isArray(snapshot.capabilities)) invalid("capabilities must be an array");

  const origin = new URL(nyxidWebUrl).origin;
  const identities = new Set();
  const capabilities = snapshot.capabilities.map((inputCapability) => {
    const capability = object(inputCapability, "Capability");
    exactKeys(capability, CAPABILITY_KEYS, "Capability");
    const capabilityId = boundedString(capability.capabilityId, "capabilityId", 128);
    if (identities.has(capabilityId)) invalid("capabilityId must be unique");
    identities.add(capabilityId);
    const requestedScopes = Array.isArray(capability.requestedScopes)
      ? [...new Set(capability.requestedScopes.map((scope) =>
        boundedString(scope, "requestedScopes", 256)))].sort()
      : invalid("requestedScopes must be an array");
    const connectionState = enumeration(
      capability.connectionState,
      CONNECTION_STATES,
      "connectionState",
    );
    const grantState = enumeration(capability.grantState, GRANT_STATES, "grantState");
    let status = enumeration(capability.status, STATUSES, "status");
    if (status === "available" && (connectionState === "unknown" || grantState === "unknown")) {
      status = "cannot_check";
    } else if (status === "available" &&
        (connectionState !== "connected" || !new Set(["granted", "not_required"]).has(grantState))) {
      status = "missing";
    }
    return {
      capabilityId,
      label: boundedString(capability.label, "label", 160),
      required: boolean(capability.required, "required"),
      status,
      connectionState,
      grantState,
      requestedScopes,
      managementUrl: managementUrl(capability.managementUrl, origin),
      reasonCode: capability.reasonCode == null
        ? null
        : boundedString(capability.reasonCode, "reasonCode", 128),
    };
  });
  return { revision, evaluatedAt, capabilities };
}

function object(value, name) {
  if (!value || typeof value !== "object" || Array.isArray(value)) invalid(`${name} must be an object`);
  return value;
}

function exactKeys(value, allowed, name) {
  if (Object.keys(value).some((key) => !allowed.has(key))) invalid(`${name} has unknown fields`);
}

function boundedString(value, name, maximum) {
  const normalized = typeof value === "string" ? value.trim() : "";
  if (!normalized || normalized.length > maximum) invalid(`${name} is invalid`);
  return normalized;
}

function timestamp(value) {
  const normalized = boundedString(value, "evaluatedAt", 64);
  const date = new Date(normalized);
  if (Number.isNaN(date.valueOf())) invalid("evaluatedAt is invalid");
  return date.toISOString();
}

function boolean(value, name) {
  if (typeof value !== "boolean") invalid(`${name} must be boolean`);
  return value;
}

function enumeration(value, allowed, name) {
  if (!allowed.has(value)) invalid(`${name} is invalid`);
  return value;
}

function managementUrl(value, origin) {
  if (value == null) return null;
  let parsed;
  try {
    parsed = new URL(boundedString(value, "managementUrl", 2048));
  } catch {
    invalid("managementUrl is invalid");
  }
  if (parsed.protocol !== "https:" || parsed.origin !== origin) invalid("managementUrl is not allowed");
  return parsed.toString();
}

function rejectSecrets(value) {
  if (Array.isArray(value)) {
    value.forEach(rejectSecrets);
    return;
  }
  if (value && typeof value === "object") {
    for (const [key, item] of Object.entries(value)) {
      if (SECRET_KEY.test(key)) invalid("Readiness snapshot contains secret fields");
      rejectSecrets(item);
    }
    return;
  }
  if (typeof value === "string" && SECRET_VALUE.test(value)) {
    invalid("Readiness snapshot contains secret values");
  }
}

function invalid(message) {
  throw new ReadinessValidationError(message);
}
