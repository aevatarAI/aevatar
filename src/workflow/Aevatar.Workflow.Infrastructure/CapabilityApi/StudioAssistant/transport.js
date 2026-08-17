import { normalizeReadinessSnapshot } from "./readiness.js?v=20260807-m40-thread-polish";

const nativeFetch = globalThis.fetch.bind(globalThis);
const backendConfig = globalThis.__AEVATAR_ASSISTANT_CONFIG__ || {};

const config = Object.freeze({
  authority: trimBaseUrl(backendConfig.authority),
  clientId: String(backendConfig.clientId || "").trim(),
  scope: String(backendConfig.scope || "").trim(),
  resources: uniqueStrings(backendConfig.resources),
  nyxidApi: trimBaseUrl(backendConfig.nyxidApi || backendConfig.authority),
  nyxidWeb: trimBaseUrl(backendConfig.nyxidWeb || backendConfig.authority),
  storageKey: String(backendConfig.storageKey || "aevatar-studio:session"),
  enableStudioWireInspector: backendConfig.enableStudioWireInspector === true,
  redirectUri: `${location.origin}/auto/callback`,
});

const TOKEN_KEY = `${config.storageKey}:token`;
const PKCE_KEY = `${config.storageKey}:pkce`;

function trimBaseUrl(value) {
  return String(value || "").trim().replace(/\/+$/, "");
}

function uniqueStrings(values) {
  return [...new Set((Array.isArray(values) ? values : [])
    .map((value) => String(value || "").trim())
    .filter(Boolean))];
}

function jsonResponse(value, status = 200, headers = {}) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8", ...headers },
  });
}

function getToken() {
  try {
    const value = JSON.parse(localStorage.getItem(TOKEN_KEY) || "null");
    return value?.access_token ? value : null;
  } catch {
    return null;
  }
}

function setToken(token) {
  localStorage.setItem(TOKEN_KEY, JSON.stringify(token));
}

function clearToken() {
  localStorage.removeItem(TOKEN_KEY);
}

function isEmbeddedInAdmin() {
  try {
    return window.parent !== window &&
      window.frameElement?.getAttribute("data-console-frame") === "1";
  } catch {
    return false;
  }
}

function notifyAdminShellAuth(type, reason = "", rejectedAccessToken = "") {
  if (!isEmbeddedInAdmin()) return false;
  window.parent.postMessage({
    source: "aevatar-backend-console-suite",
    type,
    reason,
    rejectedAccessToken,
  }, location.origin);
  return true;
}

function replacementToken(rejectedAccessToken) {
  const current = getToken();
  return current?.access_token && current.access_token !== rejectedAccessToken ? current : null;
}

async function requestAdminShellTokenRefresh(rejectedAccessToken) {
  const current = replacementToken(rejectedAccessToken);
  if (current) return { token: current, errorCode: "", reason: "" };
  if (!isEmbeddedInAdmin()) return { token: null, errorCode: "", reason: "" };

  const requestId = `auth-${Date.now().toString(36)}-${randomString(10)}`;
  return await new Promise((resolve) => {
    let settled = false;
    let timer = null;
    const finish = (result = {}) => {
      if (settled) return;
      settled = true;
      window.removeEventListener("message", onMessage);
      if (timer !== null) clearTimeout(timer);
      resolve({
        token: replacementToken(rejectedAccessToken),
        errorCode: String(result.errorCode || ""),
        reason: String(result.reason || ""),
      });
    };
    const onMessage = (event) => {
      const message = event.data || {};
      if (event.origin === location.origin &&
          event.source === window.parent &&
          message.source === "aevatar-backend-console-suite" &&
          message.type === "auth-refresh-result" &&
          message.requestId === requestId) {
        finish(message);
      }
    };
    window.addEventListener("message", onMessage);
    timer = setTimeout(finish, 10_000);
    window.parent.postMessage({
      source: "aevatar-backend-console-suite",
      type: "auth-refresh-request",
      requestId,
      rejectedAccessToken,
    }, location.origin);
  });
}

async function authorizedFetch(input, init = {}) {
  let token = getToken();
  if (!token) return jsonResponse({ code: "AUTH_REQUIRED", message: "NyxID login is required." }, 401);

  const request = (activeToken) => nativeFetch(input, {
    ...init,
    headers: {
      ...(init.headers || {}),
      Authorization: `Bearer ${activeToken.access_token}`,
    },
  });
  let response = await request(token);
  if (response.status !== 401) return response;

  const currentReplacement = replacementToken(token.access_token);
  const refreshResult = currentReplacement
    ? { token: currentReplacement, errorCode: "", reason: "" }
    : await requestAdminShellTokenRefresh(token.access_token);
  const replacement = refreshResult.token;
  if (replacement?.access_token && replacement.access_token !== token.access_token) {
    token = replacement;
    response = await request(token);
    if (response.status !== 401) return response;
  }

  clearToken();
  const errorCode = refreshResult.errorCode || "AUTH_REQUIRED";
  const reason = refreshResult.reason || "登录已过期，请重新使用 NyxID 登录。";
  notifyAdminShellAuth(
    "auth-required",
    reason,
    token.access_token,
  );
  return jsonResponse({ code: errorCode, message: reason }, 401);
}

function b64url(buffer) {
  return btoa(String.fromCharCode(...new Uint8Array(buffer)))
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/, "");
}

function randomString(length) {
  const bytes = new Uint8Array(length);
  crypto.getRandomValues(bytes);
  return b64url(bytes.buffer).slice(0, length);
}

async function beginLogin() {
  if (notifyAdminShellAuth("login-request")) return;
  if (!config.authority || !config.clientId) {
    throw new Error("NyxID OIDC is not configured for this host.");
  }
  const verifier = randomString(64);
  const state = randomString(32);
  const challenge = b64url(await crypto.subtle.digest(
    "SHA-256",
    new TextEncoder().encode(verifier),
  ));
  sessionStorage.setItem(PKCE_KEY, JSON.stringify({
    verifier,
    state,
    returnTo: "/workflow/studio",
  }));
  // ADR-0018: the session login never sends explicit `resource` parameters —
  // NyxID would narrow the grant to exactly that list (RFC 8707 intersection)
  // and the token could no longer reach other consented services such as the
  // deployment's default LLM route. The consent page owns the granted set.
  const url = new URL(`${config.authority}/oauth/authorize`);
  url.searchParams.set("response_type", "code");
  url.searchParams.set("client_id", config.clientId);
  url.searchParams.set("redirect_uri", config.redirectUri);
  url.searchParams.set("scope", config.scope);
  url.searchParams.set("state", state);
  url.searchParams.set("code_challenge", challenge);
  url.searchParams.set("code_challenge_method", "S256");
  location.assign(url.toString());
}

async function completeLoginIfCallback() {
  const parameters = new URLSearchParams(location.search);
  const code = parameters.get("code");
  if (!code) return;
  let pending = null;
  try {
    pending = JSON.parse(sessionStorage.getItem(PKCE_KEY) || "null");
  } catch {
    pending = null;
  }
  history.replaceState({}, "", pending?.returnTo || "/workflow/studio");
  if (!pending || pending.state !== parameters.get("state")) return;

  const form = new URLSearchParams();
  form.set("grant_type", "authorization_code");
  form.set("code", code);
  form.set("redirect_uri", config.redirectUri);
  form.set("client_id", config.clientId);
  form.set("code_verifier", pending.verifier);
  const response = await nativeFetch(`${config.authority}/oauth/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: form.toString(),
  });
  sessionStorage.removeItem(PKCE_KEY);
  if (!response.ok) return;
  const token = await response.json();
  token.obtained_at = Date.now();
  setToken(token);
}

async function userSessionResponse() {
  if (!getToken()) return jsonResponse({ authenticated: false });
  const response = await authorizedFetch(`${config.authority}/oauth/userinfo`, {
    cache: "no-store",
  });
  if (!response.ok) return jsonResponse({ authenticated: false }, response.status);
  const profile = await response.json();
  const scopeId = String(profile.sub || profile.id || "").trim();
  if (!scopeId) return jsonResponse({ authenticated: false }, 401);
  return jsonResponse({
    authenticated: true,
    authMode: "oidc-pkce",
    user: {
      id: scopeId,
      name: String(profile.name || profile.preferred_username || profile.email || "NyxID user"),
      email: String(profile.email || ""),
      picture: String(profile.picture || ""),
    },
    scopeId,
    resources: config.resources,
  });
}

async function nyxidJson(path, init = {}) {
  const response = await authorizedFetch(`${config.nyxidApi}${path}`, init);
  if (!response.ok) return { response, payload: null };
  return { response, payload: await response.json() };
}

const KEY_ACTION_SECRET_FIELD = /^(?:authorization|cookie|full[-_]?key|key[-_]?hash|access[-_]?token|refresh[-_]?token|secret|raw[-_]?upstream[-_]?body|oauth[-_]?client[-_]?secret)$/i;
const KEY_ACTION_IDENTITY = /^[^\s\u0000-\u001f\u007f/\\?#]{1,256}$/u;

export class KeyActionVerificationError extends Error {
  constructor(code = "NYXID_KEY_ACTION_INVALID") {
    super("NyxID key action evidence is invalid.");
    this.name = "KeyActionVerificationError";
    this.code = code;
  }
}

function invalidKeyAction(code) {
  return new KeyActionVerificationError(code);
}

function requireKeyActionObject(value, code = "NYXID_KEY_ACTION_INVALID") {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw invalidKeyAction(code);
  }
  return value;
}

function assertExactKeyActionFields(value, allowed, code = "NYXID_KEY_ACTION_INVALID") {
  const allowedFields = new Set(allowed);
  if (Object.keys(value).some((key) => !allowedFields.has(key))) {
    throw invalidKeyAction(code);
  }
}

function requireKeyActionIdentity(value, code = "NYXID_KEY_ACTION_IDENTITY_INVALID") {
  if (typeof value !== "string" || !KEY_ACTION_IDENTITY.test(value)) {
    throw invalidKeyAction(code);
  }
  return value;
}

function requireKeyActionText(value, maximum, code = "NYXID_KEY_ACTION_INVALID") {
  if (typeof value !== "string" ||
      value.length < 1 ||
      value.length > maximum ||
      value.trim() !== value) {
    throw invalidKeyAction(code);
  }
  return value;
}

function assertKeyActionSecretFree(value) {
  if (!value || typeof value !== "object") return;
  if (Array.isArray(value)) {
    for (const item of value) assertKeyActionSecretFree(item);
    return;
  }
  for (const [key, item] of Object.entries(value)) {
    if (KEY_ACTION_SECRET_FIELD.test(key)) {
      throw invalidKeyAction("NYXID_KEY_ACTION_SECRET_FIELD");
    }
    assertKeyActionSecretFree(item);
  }
}

function requireUniqueKeyActionIdentities(value, code = "NYXID_KEY_ACTION_INVALID") {
  if (!Array.isArray(value) || value.length < 1 || value.length > 64) {
    throw invalidKeyAction(code);
  }
  const identities = value.map((item) => requireKeyActionIdentity(item, code));
  if (new Set(identities).size !== identities.length) {
    throw invalidKeyAction(code);
  }
  return identities;
}

function requireKeyActionTimestamp(value, code = "NYXID_KEY_ACTION_TIMESTAMP_INVALID") {
  if (typeof value !== "string" || !value || !Number.isFinite(Date.parse(value))) {
    throw invalidKeyAction(code);
  }
  return value;
}

export function buildKeyActionMutation(action, input) {
  const value = requireKeyActionObject(input);
  if (action === "key.create") {
    assertExactKeyActionFields(value, [
      "actionRequestId", "name", "platform", "allowedServiceIds",
    ]);
    return {
      path: "/api/v1/assistant/actions/key-create",
      body: {
        actionRequestId: requireKeyActionIdentity(value.actionRequestId),
        name: requireKeyActionText(value.name, 200),
        platform: requireKeyActionText(value.platform, 100),
        allowedServiceIds: requireUniqueKeyActionIdentities(value.allowedServiceIds),
      },
    };
  }
  if (action === "key.rotate") {
    assertExactKeyActionFields(value, ["actionRequestId", "keyId"]);
    return {
      path: "/api/v1/assistant/actions/key-rotate",
      body: {
        actionRequestId: requireKeyActionIdentity(value.actionRequestId),
        keyId: requireKeyActionIdentity(value.keyId),
      },
    };
  }
  throw invalidKeyAction("NYXID_KEY_ACTION_UNSUPPORTED");
}

export function normalizeKeyActionEffect(action, input) {
  const value = requireKeyActionObject(input, "NYXID_KEY_ACTION_EFFECT_INVALID");
  const allowedFields = action === "key.create"
    ? ["resource", "replayed", "fullKey"]
    : action === "key.rotate"
      ? ["resource", "replayed", "requestedAt", "fullKey"]
      : null;
  if (!allowedFields) throw invalidKeyAction("NYXID_KEY_ACTION_UNSUPPORTED");
  assertExactKeyActionFields(value, allowedFields, "NYXID_KEY_ACTION_EFFECT_INVALID");
  const resource = requireKeyActionObject(value.resource, "NYXID_KEY_ACTION_EFFECT_INVALID");
  assertExactKeyActionFields(resource, ["keyId"], "NYXID_KEY_ACTION_EFFECT_INVALID");
  if (typeof value.replayed !== "boolean") {
    throw invalidKeyAction("NYXID_KEY_ACTION_EFFECT_INVALID");
  }
  const fullKeyPresent = Object.prototype.hasOwnProperty.call(value, "fullKey");
  if (value.replayed && fullKeyPresent) {
    throw invalidKeyAction("NYXID_KEY_ACTION_REPLAY_SECRET");
  }
  if (!value.replayed && !fullKeyPresent) {
    throw invalidKeyAction("NYXID_KEY_ACTION_EFFECT_INVALID");
  }
  if (fullKeyPresent && (typeof value.fullKey !== "string" || !value.fullKey)) {
    throw invalidKeyAction("NYXID_KEY_ACTION_EFFECT_INVALID");
  }
  const result = {
    resource: { keyId: requireKeyActionIdentity(resource.keyId) },
    replayed: value.replayed,
  };
  if (action === "key.rotate") {
    result.requestedAt = requireKeyActionTimestamp(value.requestedAt);
  }
  if (fullKeyPresent) result.fullKey = value.fullKey;
  return result;
}

export function verifyPersonalServiceReadBack(expectedServiceId, input) {
  const expectedId = requireKeyActionIdentity(expectedServiceId);
  const value = requireKeyActionObject(input, "NYXID_KEY_SERVICE_READ_BACK_INVALID");
  assertKeyActionSecretFree(value);
  if (requireKeyActionIdentity(value.id) !== expectedId ||
      value.is_active !== true ||
      value.credential_source?.type !== "personal") {
    throw invalidKeyAction("NYXID_KEY_SERVICE_READ_BACK_MISMATCH");
  }
  return { verified: true, serviceId: expectedId };
}

export function verifyKeyCreateReadBack(request, effect, input) {
  const requestValue = requireKeyActionObject(request, "NYXID_KEY_CREATE_READ_BACK_INVALID");
  const params = requireKeyActionObject(
    requestValue.params,
    "NYXID_KEY_CREATE_READ_BACK_INVALID",
  );
  const effectValue = requireKeyActionObject(effect, "NYXID_KEY_CREATE_READ_BACK_INVALID");
  const effectResource = requireKeyActionObject(
    effectValue.resource,
    "NYXID_KEY_CREATE_READ_BACK_INVALID",
  );
  const value = requireKeyActionObject(input, "NYXID_KEY_CREATE_READ_BACK_INVALID");
  assertKeyActionSecretFree(value);

  const expectedKeyId = requireKeyActionIdentity(effectResource.keyId);
  const expectedServiceIds = requireUniqueKeyActionIdentities(
    params.allowedServiceIds,
    "NYXID_KEY_CREATE_READ_BACK_INVALID",
  );
  const actualServiceIds = requireUniqueKeyActionIdentities(
    value.allowed_service_ids,
    "NYXID_KEY_CREATE_READ_BACK_INVALID",
  );
  const actualNodeIds = Array.isArray(value.allowed_node_ids)
    ? value.allowed_node_ids.map((item) => requireKeyActionIdentity(
      item,
      "NYXID_KEY_CREATE_READ_BACK_INVALID",
    ))
    : null;
  const exactServiceIds = actualServiceIds.length === expectedServiceIds.length &&
    [...actualServiceIds].sort().every((id, index) => id === [...expectedServiceIds].sort()[index]);
  if (requireKeyActionIdentity(value.id) !== expectedKeyId ||
      value.name !== params.name ||
      value.platform !== params.platform ||
      value.scopes !== "proxy" ||
      value.is_active !== true ||
      !exactServiceIds ||
      !actualNodeIds ||
      actualNodeIds.length !== 0 ||
      value.allow_all_services !== false ||
      value.allow_all_nodes !== false) {
    throw invalidKeyAction("NYXID_KEY_CREATE_READ_BACK_MISMATCH");
  }
  return { verified: true, keyId: expectedKeyId };
}

export function verifyKeyRotateReadBack(request, effect, input) {
  const requestValue = requireKeyActionObject(request, "NYXID_KEY_ROTATE_READ_BACK_INVALID");
  const params = requireKeyActionObject(
    requestValue.params,
    "NYXID_KEY_ROTATE_READ_BACK_INVALID",
  );
  const effectValue = requireKeyActionObject(effect, "NYXID_KEY_ROTATE_READ_BACK_INVALID");
  const effectResource = requireKeyActionObject(
    effectValue.resource,
    "NYXID_KEY_ROTATE_READ_BACK_INVALID",
  );
  const value = requireKeyActionObject(input, "NYXID_KEY_ROTATE_READ_BACK_INVALID");
  assertKeyActionSecretFree(value);

  const predecessorId = requireKeyActionIdentity(params.keyId);
  const successorId = requireKeyActionIdentity(effectResource.keyId);
  const requestedAt = Date.parse(requireKeyActionTimestamp(effectValue.requestedAt));
  const createdAt = Date.parse(requireKeyActionTimestamp(value.created_at));
  const updatedAt = Date.parse(requireKeyActionTimestamp(value.updated_at));
  if (requireKeyActionIdentity(value.id) !== successorId ||
      successorId === predecessorId ||
      value.rotation_predecessor_id !== predecessorId ||
      !Number.isInteger(value.state_version) ||
      value.state_version < 1 ||
      value.is_active !== true ||
      createdAt < requestedAt ||
      updatedAt < requestedAt ||
      updatedAt < createdAt) {
    throw invalidKeyAction("NYXID_KEY_ROTATE_READ_BACK_MISMATCH");
  }
  return { verified: true, keyId: successorId };
}

function keyActionErrorResponse(status = 502, code = "NYXID_KEY_ACTION_UNAVAILABLE") {
  return jsonResponse({
    code,
    message: "NyxID key action evidence is unavailable.",
  }, status);
}

async function keyActionJson(path, init = {}) {
  const response = await authorizedFetch(`${config.nyxidApi}${path}`, init);
  if (!response.ok) {
    return { response: keyActionErrorResponse(response.status), payload: null };
  }
  try {
    return { response, payload: await response.json() };
  } catch {
    return { response: keyActionErrorResponse(), payload: null };
  }
}

function keyActionPathIdentity(pathname, prefix) {
  if (!pathname.startsWith(prefix)) return null;
  const encodedIdentity = pathname.slice(prefix.length);
  if (!encodedIdentity || encodedIdentity.includes("/")) {
    throw invalidKeyAction("NYXID_KEY_ACTION_IDENTITY_INVALID");
  }
  try {
    return requireKeyActionIdentity(decodeURIComponent(encodedIdentity));
  } catch (error) {
    if (error instanceof KeyActionVerificationError) throw error;
    throw invalidKeyAction("NYXID_KEY_ACTION_IDENTITY_INVALID");
  }
}

function proxyResourceForSlug(slug) {
  return `${config.nyxidApi}/api/v1/proxy/s/${encodeURIComponent(slug)}`;
}

export function normalizeServices(payload, coreResources = config.resources) {
  const core = new Set(uniqueStrings(coreResources));
  return (Array.isArray(payload?.services) ? payload.services : [])
    .map((service) => {
      const resourceUri = String(service.resource_uri || proxyResourceForSlug(service.slug || ""));
      const active = service.is_active !== false;
      const available = service.credential_source?.allowed !== false;
      return {
        id: String(service.id || ""),
        slug: String(service.slug || ""),
        label: String(service.label || service.catalog_service_name || service.slug || "Service"),
        resourceUri,
        active,
        available,
        authorized: active && available,
        core: core.has(resourceUri),
        source: service.credential_source?.type || "personal",
        sourceName: service.credential_source?.org_name || "",
      };
    })
    .filter((service) => service.id && service.slug && service.resourceUri)
    .sort((left, right) =>
      Number(right.core) - Number(left.core) ||
      Number(right.authorized) - Number(left.authorized) ||
      left.label.localeCompare(right.label));
}

function catalogAuthKind(entry) {
  if (entry.service_type === "ssh") return "ssh";
  const providerType = String(entry.provider_type || "").toLowerCase();
  if (providerType === "oauth2") return "oauth";
  if (providerType === "device_code") return "device_code";
  if (entry.requires_credential === false) return "none";
  if (Array.isArray(entry.token_exchange_credential_fields) && entry.token_exchange_credential_fields.length) {
    return "token_exchange";
  }
  if (entry.requires_gateway_url) return "self_hosted";
  return "api_key";
}

function connectorUserService(key) {
  return {
    userServiceId: String(key?.id || ""),
    apiKeyId: String(key?.api_key_id || ""),
    endpointUrl: String(key?.endpoint_url || ""),
    label: String(key?.label || key?.catalog_service_name || key?.slug || "Service"),
  };
}

export function deriveConnectors(keysInput, catalogInput) {
  const keys = Array.isArray(keysInput) ? keysInput : [];
  const catalog = Array.isArray(catalogInput) ? catalogInput : [];
  const catalogBySlug = new Map(catalog.map((entry) => [entry.slug, entry]));
  const connectedGroups = new Map();
  const customKeys = [];
  for (const key of keys) {
    if (key.catalog_service_slug) {
      const group = connectedGroups.get(key.catalog_service_slug) || [];
      group.push(key);
      connectedGroups.set(key.catalog_service_slug, group);
    } else {
      customKeys.push(key);
    }
  }

  const connected = [];
  for (const [slug, group] of connectedGroups) {
    const entry = catalogBySlug.get(slug);
    const first = group[0];
    const userServices = group.map(connectorUserService).filter((service) => service.userServiceId);
    connected.push({
      slug,
      name: String(entry?.name || first.catalog_service_name || first.label || slug),
      description: String(entry?.description || first.description || ""),
      iconUrl: String(entry?.icon_url || ""),
      authKind: entry ? catalogAuthKind(entry) : String(first.credential_type || "api_key"),
      connectionCount: group.length,
      userServices,
      ...(userServices.length === 1 ? { userServiceId: userServices[0].userServiceId } : {}),
      status: group.some((key) => key.is_active !== false && key.status !== "error")
        ? "connected"
        : "error",
    });
  }
  for (const key of customKeys) {
    const userServices = [connectorUserService(key)].filter((service) => service.userServiceId);
    connected.push({
      slug: String(key.slug || ""),
      name: String(key.label || key.slug || "Custom service"),
      description: String(key.description || ""),
      iconUrl: "",
      authKind: String(key.credential_type || "custom"),
      connectionCount: 1,
      userServices,
      ...(userServices.length === 1 ? { userServiceId: userServices[0].userServiceId } : {}),
      status: key.is_active !== false ? "connected" : "error",
      custom: true,
    });
  }
  connected.sort((left, right) => left.name.localeCompare(right.name));
  const available = catalog
    .filter((entry) => entry.slug && !connectedGroups.has(entry.slug))
    .map((entry) => ({
      slug: String(entry.slug),
      name: String(entry.name || entry.slug),
      description: String(entry.description || ""),
      iconUrl: String(entry.icon_url || ""),
      authKind: catalogAuthKind(entry),
      apiKeyUrl: String(entry.api_key_url || ""),
      apiKeyInstructions: String(entry.api_key_instructions || ""),
      docsUrl: String(entry.documentation_url || ""),
    }))
    .sort((left, right) => left.name.localeCompare(right.name));
  return { connected, available };
}

function mapAttachment(attachment) {
  if (!attachment?.dataBase64 || !attachment?.name) return [];
  const mediaType = String(attachment.mediaType || "application/octet-stream");
  const type = mediaType.startsWith("image/")
    ? "image"
    : mediaType.startsWith("audio/")
      ? "audio"
      : mediaType.startsWith("video/")
        ? "video"
        : "file";
  return [{ type, dataBase64: attachment.dataBase64, mediaType, name: attachment.name }];
}

export function canonicalAssistantRequest(input, generatedRequestId = crypto.randomUUID()) {
  const body = typeof input === "string" ? JSON.parse(input) : structuredClone(input || {});
  const type = String(body.type || "");
  const request = { ...body };
  delete request.surface;
  delete request.attachment;
  if (type === "text") {
    const inputParts = mapAttachment(body.attachment);
    if (inputParts.length) request.inputParts = inputParts;
  }
  if (!request.clientRequestId && type === "approval.resolve") {
    request.clientRequestId = `client-approval-${generatedRequestId}`;
  }
  return request;
}

async function forwardAssistant(body, init) {
  const request = canonicalAssistantRequest(body);
  const clientRequestId = request.clientRequestId || crypto.randomUUID();
  return await authorizedFetch("/api/chat", {
    ...init,
    method: "POST",
    headers: {
      ...(init.headers || {}),
      Accept: "text/event-stream, application/json",
      "Content-Type": "application/json",
      "Idempotency-Key": clientRequestId,
    },
    body: JSON.stringify(request),
  });
}

async function forwardConversation(url, init) {
  const suffix = url.pathname.slice("/api/demo/conversations".length);
  const directPath = `/api/chat/conversations${suffix}`;
  const query = suffix.endsWith("/state") ? url.search : "";
  const response = await authorizedFetch(`${directPath}${query}`, init);
  if (!response.ok || init.method === "DELETE" || suffix.endsWith("/state") || !suffix) return response;
  const payload = await response.json();
  return jsonResponse(Array.isArray(payload) ? payload : payload?.messages || [], response.status);
}

async function studioFetch(input, init = {}) {
  const url = new URL(typeof input === "string" ? input : input.url, location.href);
  if (url.origin !== location.origin) return nativeFetch(input, init);

  if (url.pathname === "/api/demo/config") {
    return jsonResponse({
      transport: "nyxid-session",
      authMode: "oidc-pkce",
      surface: "nyxid-chat",
      workflow: "direct",
      directBaseUrl: location.origin,
      proxyBaseUrl: config.resources[0] || "",
      ornnWebUrl: "",
      nyxidWebUrl: config.nyxidWeb,
      servicesUrl: config.nyxidWeb ? new URL("/keys", config.nyxidWeb).toString() : "",
      environment: "production",
      transportLocked: true,
      enableStudioWireInspector: config.enableStudioWireInspector,
    });
  }
  if (url.pathname === "/api/auth/session") return await userSessionResponse();
  if (url.pathname === "/api/auth/services") {
    const result = await nyxidJson("/api/v1/user-services", { cache: "no-store" });
    return result.response.ok
      ? jsonResponse({ services: normalizeServices(result.payload) })
      : result.response;
  }
  if (url.pathname === "/api/nyxid/readiness") {
    const result = await nyxidJson("/api/v1/assistant/readiness", { cache: "no-store" });
    if (!result.response.ok) return result.response;
    try {
      return jsonResponse(normalizeReadinessSnapshot(result.payload, {
        managementOrigins: [config.nyxidWeb, config.authority, config.nyxidApi],
      }));
    } catch (error) {
      return jsonResponse({
        code: error?.code || "READINESS_INVALID",
        message: "NyxID readiness evidence is invalid.",
        reason: typeof error?.reason === "string" ? error.reason : "",
      }, 502);
    }
  }
  if (url.pathname === "/api/nyxid/connectors") {
    const [keysResult, catalogResult] = await Promise.all([
      nyxidJson("/api/v1/keys", { cache: "no-store" }),
      nyxidJson("/api/v1/catalog", { cache: "no-store" }),
    ]);
    if (!keysResult.response.ok) return keysResult.response;
    if (!catalogResult.response.ok) return catalogResult.response;
    const keys = Array.isArray(keysResult.payload?.keys) ? keysResult.payload.keys : [];
    const catalog = Array.isArray(catalogResult.payload?.entries)
      ? catalogResult.payload.entries
      : Array.isArray(catalogResult.payload)
        ? catalogResult.payload
        : [];
    return jsonResponse(deriveConnectors(keys, catalog));
  }
  if ([
    "/api/nyxid/assistant-actions/key-create",
    "/api/nyxid/assistant-actions/key-rotate",
  ].includes(url.pathname)) {
    if (String(init.method || "GET").toUpperCase() !== "POST") {
      return keyActionErrorResponse(405, "NYXID_KEY_ACTION_METHOD_INVALID");
    }
    try {
      const action = url.pathname.endsWith("key-create") ? "key.create" : "key.rotate";
      const mutation = buildKeyActionMutation(action, JSON.parse(String(init.body || "{}")));
      const result = await keyActionJson(mutation.path, {
        method: "POST",
        cache: "no-store",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(mutation.body),
      });
      if (!result.payload) return result.response;
      return jsonResponse(normalizeKeyActionEffect(action, result.payload));
    } catch {
      return keyActionErrorResponse(400, "NYXID_KEY_ACTION_INVALID");
    }
  }
  if (url.pathname.startsWith("/api/nyxid/api-keys/")) {
    if (String(init.method || "GET").toUpperCase() !== "GET") {
      return keyActionErrorResponse(405, "NYXID_KEY_ACTION_METHOD_INVALID");
    }
    try {
      const keyId = keyActionPathIdentity(url.pathname, "/api/nyxid/api-keys/");
      const result = await keyActionJson(`/api/v1/api-keys/${encodeURIComponent(keyId)}`, {
        cache: "no-store",
      });
      if (!result.payload) return result.response;
      assertKeyActionSecretFree(result.payload);
      return jsonResponse(result.payload);
    } catch {
      return keyActionErrorResponse(502, "NYXID_KEY_READ_BACK_INVALID");
    }
  }
  if (url.pathname.startsWith("/api/nyxid/keys/")) {
    if (String(init.method || "GET").toUpperCase() !== "GET") {
      return keyActionErrorResponse(405, "NYXID_KEY_ACTION_METHOD_INVALID");
    }
    try {
      const serviceId = keyActionPathIdentity(url.pathname, "/api/nyxid/keys/");
      const result = await keyActionJson(`/api/v1/keys/${encodeURIComponent(serviceId)}`, {
        cache: "no-store",
      });
      if (!result.payload) return result.response;
      assertKeyActionSecretFree(result.payload);
      return jsonResponse(result.payload);
    } catch {
      return keyActionErrorResponse(502, "NYXID_KEY_SERVICE_READ_BACK_INVALID");
    }
  }
  if (url.pathname === "/api/nyxid/keys" && String(init.method || "GET").toUpperCase() === "POST") {
    const body = JSON.parse(String(init.body || "{}"));
    const result = await nyxidJson("/api/v1/keys", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        service_slug: String(body.serviceSlug || ""),
        credential: String(body.credential || ""),
        label: String(body.label || body.serviceSlug || ""),
      }),
    });
    if (!result.response.ok) return result.response;
    const userServiceId = String(result.payload?.id || "");
    return jsonResponse({
      ok: Boolean(userServiceId),
      userService: {
        userServiceId,
        apiKeyId: String(result.payload?.api_key_id || ""),
        slug: String(result.payload?.slug || body.serviceSlug || ""),
        label: String(result.payload?.label || body.label || body.serviceSlug || ""),
      },
    }, userServiceId ? 200 : 502);
  }
  if (url.pathname === "/api/auth/logout") {
    clearToken();
    return jsonResponse({ ok: true });
  }
  if (url.pathname === "/api/demo/health") {
    const startedAt = performance.now();
    const response = await authorizedFetch("/api/health", { cache: "no-store" });
    const payload = await response.json().catch(() => ({}));
    return jsonResponse({
      ok: response.ok,
      latencyMs: Math.max(0, Math.round(performance.now() - startedAt)),
      components: { aevatar: { ok: response.ok, ...payload }, ornn: { ok: true } },
    }, response.status);
  }
  if (url.pathname === "/api/demo/chat") return await forwardAssistant(init.body || "{}", init);
  if (url.pathname.startsWith("/api/demo/conversations")) return await forwardConversation(url, init);
  return await authorizedFetch(`${url.pathname}${url.search}`, init);
}

globalThis.AevatarStudioAuth = Object.freeze({
  beginLogin,
  clearToken,
  getToken,
  notifyAdminShellAuth,
});

await completeLoginIfCallback();
globalThis.fetch = studioFetch;
