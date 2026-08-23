import { normalizeReadinessSnapshot } from "./readiness.js?v=20260823-m62-studio-redesign";

const nativeFetch = globalThis.fetch.bind(globalThis);
const backendConfig = globalThis.__AEVATAR_ASSISTANT_CONFIG__ || {};

const config = Object.freeze({
  authority: trimBaseUrl(backendConfig.authority),
  clientId: String(backendConfig.clientId || "").trim(),
  scope: String(backendConfig.scope || "").trim(),
  resources: uniqueStrings(backendConfig.resources),
  nyxidApi: trimBaseUrl(backendConfig.nyxidApi),
  nyxidWeb: trimBaseUrl(backendConfig.nyxidWeb || backendConfig.nyxidApi),
  storageKey: String(backendConfig.storageKey || "aevatar-studio:session"),
  enableStudioWireInspector: backendConfig.enableStudioWireInspector === true,
  redirectUri: `${location.origin}/auto/callback`,
});

const TOKEN_KEY = `${config.storageKey}:token`;
const SERVICE_ACCESS_REVIEW_TOKEN_KEY = `${config.storageKey}:service-access-review:token`;
const PKCE_KEY = `${config.storageKey}:pkce`;
const SERVICE_ACCESS_REVIEW_FLOW = "service-access-review";
const SERVICE_ACCESS_REVIEW_MESSAGE_SOURCE = "aevatar-service-access-review-auth";
const SERVICE_ACCESS_REVIEW_POPUP_PREFIX = "aevatar-nyxid-service-review-";
const ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED_CODE =
  "NYXID_ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED";

const serviceAccessReviewListeners = new Set();
let serviceAccessReviewPopup = null;
let serviceAccessReviewRequestId = "";
let serviceAccessReviewPopupTimer = null;

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

function getServiceAccessReviewToken() {
  try {
    const value = JSON.parse(localStorage.getItem(SERVICE_ACCESS_REVIEW_TOKEN_KEY) || "null");
    return value?.access_token ? value : null;
  } catch {
    return null;
  }
}

function setServiceAccessReviewToken(token) {
  localStorage.setItem(SERVICE_ACCESS_REVIEW_TOKEN_KEY, JSON.stringify(token));
}

function clearServiceAccessReviewToken() {
  localStorage.removeItem(SERVICE_ACCESS_REVIEW_TOKEN_KEY);
}

function emitServiceAccessReviewResult(result) {
  for (const listener of serviceAccessReviewListeners) {
    try {
      listener(result);
    } catch {
      // One UI listener must not prevent the remaining subscribers from resuming.
    }
  }
}

function clearServiceAccessReviewPopupTracking() {
  if (serviceAccessReviewPopupTimer !== null) {
    clearInterval(serviceAccessReviewPopupTimer);
    serviceAccessReviewPopupTimer = null;
  }
  serviceAccessReviewPopup = null;
  serviceAccessReviewRequestId = "";
}

function trackServiceAccessReviewPopup(popup, requestId) {
  if (serviceAccessReviewPopup && serviceAccessReviewPopup !== popup &&
      !serviceAccessReviewPopup.closed) {
    try {
      serviceAccessReviewPopup.close();
    } catch {
      // The new review supersedes the previous transient window either way.
    }
  }
  clearServiceAccessReviewPopupTracking();
  serviceAccessReviewPopup = popup;
  serviceAccessReviewRequestId = requestId;
  serviceAccessReviewPopupTimer = setInterval(() => {
    if (serviceAccessReviewPopup && !serviceAccessReviewPopup.closed) return;
    const closedRequestId = serviceAccessReviewRequestId;
    clearServiceAccessReviewPopupTracking();
    notifyAdminShellAuth("service-access-review-finished", "", "", {
      authRequestId: closedRequestId,
    });
    emitServiceAccessReviewResult({
      status: "failed",
      requestId: closedRequestId,
      message: "NyxID 授权窗口已关闭；原 action 与会话仍然保留。",
    });
  }, 500);
}

function onServiceAccessReviewResult(listener) {
  if (typeof listener !== "function") return () => {};
  serviceAccessReviewListeners.add(listener);
  return () => serviceAccessReviewListeners.delete(listener);
}

function handleServiceAccessReviewMessage(event) {
  const message = event.data || {};
  const trustedSource = event.source === serviceAccessReviewPopup ||
    (isEmbeddedInAdmin() && event.source === window.parent);
  if (event.origin !== location.origin ||
      !trustedSource ||
      message.source !== SERVICE_ACCESS_REVIEW_MESSAGE_SOURCE ||
      message.type !== "service-access-review-result" ||
      !serviceAccessReviewRequestId ||
      message.requestId !== serviceAccessReviewRequestId) {
    return;
  }
  const status = message.status === "succeeded" ? "succeeded" : "failed";
  const requestId = serviceAccessReviewRequestId;
  clearServiceAccessReviewPopupTracking();
  notifyAdminShellAuth("service-access-review-finished", "", "", {
    authRequestId: requestId,
  });
  emitServiceAccessReviewResult({
    status,
    requestId,
    message: String(message.message || ""),
  });
}

window.addEventListener("message", handleServiceAccessReviewMessage);

function isEmbeddedInAdmin() {
  try {
    return window.parent !== window &&
      window.frameElement?.getAttribute("data-console-frame") === "1";
  } catch {
    return false;
  }
}

function notifyAdminShellAuth(type, reason = "", rejectedAccessToken = "", details = {}) {
  if (!isEmbeddedInAdmin()) return false;
  window.parent.postMessage({
    source: "aevatar-backend-console-suite",
    type,
    reason,
    rejectedAccessToken,
    ...details,
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

  const requiresServiceAccessReview = async (response) => {
    if (response.status !== 401) return false;
    try {
      const payload = await response.clone().json();
      return payload?.code === ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED_CODE;
    } catch {
      return false;
    }
  };

  const request = (activeToken) => nativeFetch(input, {
    ...init,
    headers: {
      ...(init.headers || {}),
      Authorization: `Bearer ${activeToken.access_token}`,
    },
  });
  let response = await request(token);
  if (response.status !== 401 || await requiresServiceAccessReview(response)) return response;

  const currentReplacement = replacementToken(token.access_token);
  const refreshResult = currentReplacement
    ? { token: currentReplacement, errorCode: "", reason: "" }
    : await requestAdminShellTokenRefresh(token.access_token);
  const replacement = refreshResult.token;
  if (replacement?.access_token && replacement.access_token !== token.access_token) {
    token = replacement;
    response = await request(token);
    if (response.status !== 401 || await requiresServiceAccessReview(response)) return response;
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

async function serviceAccessReviewAuthorizedFetch(input, init = {}) {
  const token = getServiceAccessReviewToken();
  if (!token) {
    return jsonResponse({
      code: "NYXID_SERVICE_ACCESS_REVIEW_REQUIRED",
      message: "NyxID service access review is required.",
    }, 401);
  }
  const response = await nativeFetch(input, {
    ...init,
    headers: {
      ...(init.headers || {}),
      Authorization: `Bearer ${token.access_token}`,
    },
  });
  if (response.status === 401) clearServiceAccessReviewToken();
  return response;
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

function decodeJwtPayload(value) {
  try {
    const encoded = String(value || "").split(".")[1];
    if (!encoded) return null;
    const base64 = encoded.replace(/-/g, "+").replace(/_/g, "/") +
      "=".repeat((4 - encoded.length % 4) % 4);
    const bytes = Uint8Array.from(atob(base64), (character) => character.charCodeAt(0));
    return JSON.parse(new TextDecoder().decode(bytes));
  } catch {
    return null;
  }
}

function tokenResponseResources(token) {
  const claims = decodeJwtPayload(token?.access_token);
  const groups = [token?.resource, claims?.resources];
  const explicit = groups.some((group) => typeof group === "string" || Array.isArray(group));
  if (!explicit) return null;
  return uniqueStrings(groups.flatMap((group) => typeof group === "string" ? [group] : group || []));
}

function resourcesCover(granted, requested) {
  return Array.isArray(granted) && requested.every((resource) => granted.includes(resource));
}

async function beginLogin(options = {}) {
  const authFlow = options?.authFlow === SERVICE_ACCESS_REVIEW_FLOW
    ? SERVICE_ACCESS_REVIEW_FLOW
    : "";
  const resources = authFlow
    ? uniqueStrings([...config.resources, ...(Array.isArray(options?.resources) ? options.resources : [])])
    : [];
  const popupName = authFlow && typeof options?.popupName === "string"
    ? options.popupName
    : "";
  const authRequestId = authFlow && typeof options?.authRequestId === "string"
    ? options.authRequestId
    : "";
  if (notifyAdminShellAuth("login-request", "", "", authFlow
    ? { authFlow, resources, popupName, authRequestId }
    : {})) return;
  if (!config.authority || !config.clientId) {
    throw new Error("NyxID OIDC is not configured for this host.");
  }
  const verifier = randomString(64);
  const state = randomString(32);
  const challenge = b64url(await crypto.subtle.digest(
    "SHA-256",
    new TextEncoder().encode(verifier),
  ));
  const pending = {
    verifier,
    state,
    returnTo: "/workflow/studio",
    resources,
    authFlow,
    authRequestId,
  };
  sessionStorage.setItem(PKCE_KEY, JSON.stringify(pending));
  // ADR-0018: ordinary session login never sends explicit `resource` parameters —
  // NyxID would narrow the grant to exactly that list (RFC 8707 intersection)
  // and the token could no longer reach other consented services such as the
  // deployment's default LLM route. Service-access review is the controlled
  // exception: it requests the complete resource union with fresh consent.
  const url = new URL(`${config.authority}/oauth/authorize`);
  url.searchParams.set("response_type", "code");
  url.searchParams.set("client_id", config.clientId);
  url.searchParams.set("redirect_uri", config.redirectUri);
  url.searchParams.set("scope", config.scope);
  url.searchParams.set("state", state);
  url.searchParams.set("code_challenge", challenge);
  url.searchParams.set("code_challenge_method", "S256");
  if (authFlow) {
    resources.forEach((resource) => url.searchParams.append("resource", resource));
    url.searchParams.set("prompt", "consent");
  }
  if (authFlow && popupName && serviceAccessReviewPopup && !serviceAccessReviewPopup.closed) {
    try {
      serviceAccessReviewPopup.sessionStorage.setItem(PKCE_KEY, JSON.stringify(pending));
      serviceAccessReviewPopup.location.replace(url.toString());
      serviceAccessReviewPopup.focus?.();
      return;
    } catch {
      try {
        serviceAccessReviewPopup.close();
      } catch {
        // Fall through to the full-page fallback when the popup cannot be navigated.
      }
      clearServiceAccessReviewPopupTracking();
    }
  }
  location.assign(url.toString());
}

async function beginServiceAccessReview(resources) {
  const authRequestId = `${Date.now().toString(36)}-${randomString(16)}`;
  const popupName = `${SERVICE_ACCESS_REVIEW_POPUP_PREFIX}${authRequestId}`;
  let popup = null;
  try {
    popup = window.open(
      "",
      popupName,
      "popup=yes,width=560,height=720,resizable=yes,scrollbars=yes",
    );
  } catch {
    popup = null;
  }
  if (popup) {
    trackServiceAccessReviewPopup(popup, authRequestId);
    popup.focus?.();
  }
  try {
    return await beginLogin({
      authFlow: SERVICE_ACCESS_REVIEW_FLOW,
      resources,
      popupName: popup ? popupName : "",
      authRequestId: popup ? authRequestId : "",
    });
  } catch (error) {
    if (popup && !popup.closed) popup.close();
    clearServiceAccessReviewPopupTracking();
    throw error;
  }
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
  const requestedResources = pending.authFlow === SERVICE_ACCESS_REVIEW_FLOW
    ? uniqueStrings(pending.resources)
    : [];
  requestedResources.forEach((resource) => form.append("resource", resource));
  const response = await nativeFetch(`${config.authority}/oauth/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: form.toString(),
  });
  sessionStorage.removeItem(PKCE_KEY);
  if (!response.ok) return;
  const token = await response.json();
  if (requestedResources.length &&
      !resourcesCover(tokenResponseResources(token), requestedResources)) {
    return;
  }
  token.obtained_at = Date.now();
  token.oauth_resources = requestedResources;
  if (pending.authFlow === SERVICE_ACCESS_REVIEW_FLOW) {
    setServiceAccessReviewToken(token);
    return;
  }
  setToken(token);
  return;
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

function proxyCatalogResourceUri(proxyBaseUrl, serviceSlug) {
  const baseUrl = trimBaseUrl(proxyBaseUrl);
  const slug = String(serviceSlug || "").trim();
  return baseUrl && slug ? `${baseUrl}/s/${encodeURIComponent(slug)}` : "";
}

export function normalizeProxyCatalog(payload, coreResources = config.resources) {
  const proxyBaseUrl = trimBaseUrl(payload?.proxy_base_url);
  const services = (Array.isArray(payload?.services) ? payload.services : [])
    .map((service) => {
      const serviceSlug = String(service?.service_slug || "").trim();
      return {
        userServiceId: String(service?.service_id || "").trim(),
        serviceSlug,
        serviceName: String(service?.service_name || serviceSlug).trim(),
        resourceUri: proxyCatalogResourceUri(proxyBaseUrl, serviceSlug),
        isUserService: service?.is_user_service === true,
      };
    })
    .filter((service) => service.userServiceId && service.serviceSlug && service.resourceUri);
  return {
    proxyBaseUrl,
    services,
    resources: uniqueStrings([
      ...uniqueStrings(coreResources),
      ...services.map((service) => service.resourceUri),
    ]),
  };
}

async function fetchServiceAccessReviewCatalog() {
  const response = await serviceAccessReviewAuthorizedFetch(
    `${config.nyxidApi}/api/v1/mcp/config`,
    { cache: "no-store" },
  );
  if (!response.ok) return response;
  return jsonResponse(normalizeProxyCatalog(await response.json()), response.status);
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

async function continueServiceAccessReview(body, init = {}) {
  const request = canonicalAssistantRequest(body);
  if (request.type !== "action.continue") {
    return jsonResponse({
      code: "NYXID_SERVICE_ACCESS_REVIEW_REQUEST_INVALID",
      message: "The service access review bearer may only continue a typed NyxID action.",
    }, 400);
  }
  const clientRequestId = request.clientRequestId || crypto.randomUUID();
  return await serviceAccessReviewAuthorizedFetch("/api/chat", {
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
  if (url.pathname === "/api/nyxid/proxy-catalog") {
    const result = await nyxidJson("/api/v1/mcp/config", { cache: "no-store" });
    return result.response.ok
      ? jsonResponse(normalizeProxyCatalog(result.payload))
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
    clearServiceAccessReviewToken();
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
  beginServiceAccessReview,
  clearServiceAccessReviewToken,
  clearToken,
  continueServiceAccessReview,
  fetchServiceAccessReviewCatalog,
  getToken,
  notifyAdminShellAuth,
  onServiceAccessReviewResult,
  serviceResourceUri: proxyResourceForSlug,
});

await completeLoginIfCallback();
globalThis.fetch = studioFetch;
