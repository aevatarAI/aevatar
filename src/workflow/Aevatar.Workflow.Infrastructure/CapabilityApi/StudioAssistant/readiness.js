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

export function describeReadinessFailure(error) {
  const status = Number(error?.status || 0);
  const code = String(error?.code || "").trim().toUpperCase();

  if (code === "OAUTH_CLIENT_INACTIVE" || code === "INVALID_CLIENT") {
    return {
      freshness: "登录配置不可用",
      summary: "Aevatar 的 NyxID 登录客户端已停用。",
      guidance: "请联系管理员在 NyxID 启用当前 OAuth 客户端，或为此环境配置有效的 Client ID。重复登录不会恢复。",
      action: "retry",
      actionLabel: "修复后重新检查",
    };
  }
  if (status === 401 || code === "AUTH_REQUIRED" || code === "REFRESH_TOKEN_REJECTED") {
    return {
      freshness: "需要重新登录",
      summary: "NyxID 登录已失效，暂时无法检查运行状态。",
      guidance: "请重新登录 NyxID。若登录页提示 OAuth client is inactive，请联系管理员启用客户端；重复登录不会恢复。",
      action: "login",
      actionLabel: "重新登录",
    };
  }
  if (status === 403) {
    return {
      freshness: "无权检查",
      summary: "当前账号无权读取 NyxID 运行状态。",
      guidance: "请在账户设置中更换有权限的 NyxID 账号；仍被拒绝时，请管理员检查该账号的访问策略。",
      action: "account",
      actionLabel: "打开账户设置",
    };
  }
  if (status === 404) {
    return {
      freshness: "检查服务未部署",
      summary: "当前 NyxID 环境尚未提供运行状态检查。",
      guidance: "这不是账号配置问题。请联系管理员部署 assistant readiness 接口，然后返回这里重新检查。",
      action: "retry",
      actionLabel: "部署后重新检查",
    };
  }
  if (status === 502 || code === "READINESS_INVALID") {
    return {
      freshness: "状态格式不兼容",
      summary: "NyxID 返回了当前 Studio 无法识别的运行状态。",
      guidance: "请先重新检查；若仍出现，请管理员确认 Aevatar 与 NyxID 的 readiness 契约版本一致。",
      action: "retry",
      actionLabel: "重新检查",
    };
  }
  if (status >= 500) {
    return {
      freshness: "服务暂时不可用",
      summary: "NyxID 暂时无法提供运行状态。",
      guidance: "请稍后重新检查；持续失败时，请管理员检查 NyxID 服务状态。",
      action: "retry",
      actionLabel: "重新检查",
    };
  }
  if (!status) {
    return {
      freshness: "无法连接",
      summary: "当前浏览器无法连接 NyxID。",
      guidance: "请检查网络或 VPN 后重新检查；其他 NyxID 页面也无法打开时，请联系管理员。",
      action: "retry",
      actionLabel: "重新检查",
    };
  }
  return {
    freshness: `检查失败 (${status})`,
    summary: "暂时无法读取 NyxID 运行状态。",
    guidance: "请重新检查；持续失败时，请将状态码提供给管理员排查。",
    action: "retry",
    actionLabel: "重新检查",
  };
}

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
