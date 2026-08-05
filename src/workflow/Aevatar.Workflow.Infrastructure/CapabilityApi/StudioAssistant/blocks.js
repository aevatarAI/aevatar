import { validateActionRequest } from "./protocol.js";

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
