import { validateActionRequest } from "./protocol.js?v=20260823-m62-studio-redesign";

// Assistant prose is always Markdown text. Executable cards are built only
// from actor-authored, schema-v4 action requests.

export function splitMessageSegments(source) {
  const text = source == null ? "" : String(source);
  return text.trim() ? [{ kind: "text", text }] : [];
}

/**
 * Build a NyxID-shaped presentation block from an authoritative action
 * request plus optional catalogue metadata served by `/api/nyxid/connectors`.
 */
export function buildConnectCardBlock(actionRequest, connectors) {
  const request = validateActionRequest(actionRequest);
  const accessReview = request.params.serviceAccessReview || null;
  if (accessReview) return buildServiceAccessReviewCardBlock(request, accessReview, connectors);

  const catalog = request.params.catalogService || null;
  const custom = request.params.customService || null;
  const slug = catalog?.serviceSlug || null;
  const available = slug
    ? (connectors?.available || []).find((service) => service.slug === slug) || null
    : null;
  const connectedService = slug
    ? (connectors?.connected || []).find((service) => service.slug === slug) || null
    : null;
  const info = connectedService || available;
  const authKind = String(custom?.authMethod || info?.authKind || "api_key");
  const serviceName = String(custom?.name || info?.name || slug || "Custom service");
  return {
    type: "connect_card",
    block_id: request.actionRequestId,
    identity: {
      actorId: request.actorId,
      originTurnId: request.originTurnId,
      taskId: request.taskId,
      stepId: request.stepId,
      actionRequestId: request.actionRequestId,
    },
    params: request.params,
    variant: catalog ? "catalogService" : "customService",
    catalog_slug: slug,
    endpoint_url: custom?.endpointUrl || null,
    service_name: serviceName,
    icon_url: String(info?.iconUrl || ""),
    subtitle: custom?.endpointUrl || String(info?.description || "").slice(0, 140),
    auth_kind: authKind,
    auth_key_name: custom?.authKeyName || null,
    requested_scopes: catalog?.requestedScopes || [],
    granted_scopes: null,
    state: "needs_connection",
    error_message: null,
    known: Boolean(custom || info),
    api_key_url: String(available?.apiKeyUrl || ""),
    api_key_instructions: String(available?.apiKeyInstructions || ""),
    docs_url: String(available?.docsUrl || ""),
    steps: connectCardSteps(serviceName, authKind),
    footer: "由 NyxID 托管凭证 · Agent 不接触原始密钥 · 可随时在 NyxID 撤销",
  };
}

function buildServiceAccessReviewCardBlock(request, accessReview, connectors) {
  const connectedService = (connectors?.connected || []).find((service) =>
    service.slug === accessReview.serviceSlug &&
    (service.userServices || []).some((candidate) =>
      candidate.userServiceId === accessReview.userServiceId)) || null;
  const available = (connectors?.available || []).find((service) =>
    service.slug === accessReview.serviceSlug) || null;
  const info = connectedService || available;
  const serviceName = String(info?.name || accessReview.serviceSlug || "Service");
  return {
    type: "service_access_review_card",
    block_id: request.actionRequestId,
    identity: {
      actorId: request.actorId,
      originTurnId: request.originTurnId,
      taskId: request.taskId,
      stepId: request.stepId,
      actionRequestId: request.actionRequestId,
    },
    params: request.params,
    variant: "serviceAccessReview",
    catalog_slug: accessReview.serviceSlug,
    user_service_id: accessReview.userServiceId,
    resource_uri: accessReview.resourceUri,
    endpoint_url: null,
    service_name: serviceName,
    icon_url: String(info?.iconUrl || ""),
    subtitle: `允许当前 Aevatar OAuth client 访问已连接的 ${serviceName} service`,
    auth_kind: "oauth",
    auth_key_name: null,
    requested_scopes: [],
    granted_scopes: null,
    state: "needs_review",
    error_message: null,
    known: Boolean(info),
    api_key_url: "",
    api_key_instructions: "",
    docs_url: String(available?.docsUrl || ""),
    steps: serviceAccessReviewSteps(serviceName),
    footer: "Review bearer 仅用于读取精确 service catalog 并恢复当前 action",
  };
}

function serviceAccessReviewSteps(serviceName) {
  return [
    {
      title: `更新 OAuth client 对 ${serviceName} 的访问`,
      body: "在 NyxID consent 页确认当前 Aevatar client 可以访问这个已连接 service。",
      done: false,
    },
    {
      title: "验证精确的 service access",
      body: "使用受限 review bearer 核对 UserService.id、service slug 与 resource URI。",
      done: false,
    },
    {
      title: "恢复原 action 并等待 actor 验证",
      body: "浏览器只报告 typed evidence；actor 验证 postcondition 后继续原任务。",
      done: false,
    },
  ];
}

export function connectCardSteps(serviceName, authKind) {
  const authorizeBody = authKind === "api_key"
    ? "在下方粘贴 API key，或前往 NyxID 完成连接。密钥直接提交给 NyxID，不会出现在聊天记录里。"
    : authKind === "oauth"
      ? "跳转到 NyxID 完成 OAuth 授权，只授予所需的最小权限。"
      : authKind === "device_code"
        ? "跳转到 NyxID 完成设备码授权。"
        : "跳转到 NyxID 完成该服务的连接配置。";
  return [
    {
      title: `授权 NyxID 访问 ${serviceName}`,
      body: authorizeBody,
      done: false,
    },
    {
      title: "NyxID 封存并代理凭证",
      body: "凭证加密保存在 NyxID vault；每次调用都经代理转发并限定范围。",
      done: false,
    },
    {
      title: "向 Aevatar 报告结果并等待 actor 验证",
      body: "连接结果只是继续信号；actor 确认后才会显示任务成功。",
      done: false,
    },
  ];
}

export function connectorInitial(name) {
  const words = String(name || "").trim().split(/\s+/).filter(Boolean);
  const first = words[0]?.charAt(0) || "?";
  const second = words.length > 1 ? words[1]?.charAt(0) || "" : "";
  return `${first}${second}`.toUpperCase();
}
