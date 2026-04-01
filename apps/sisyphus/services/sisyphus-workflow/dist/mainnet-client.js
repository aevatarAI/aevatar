import pino from "pino";
const logger = pino({ name: "workflow:mainnet-client" });
const AEVATAR_MAINNET_URL = process.env["AEVATAR_MAINNET_URL"] ?? "http://localhost:6688";
const SCOPE_ID = process.env["SCOPE_ID"] ?? "76fe9d91-1f1d-4234-9352-819a7c28f709";
function buildHeaders(authorization) {
    const headers = { "Content-Type": "application/json" };
    if (authorization)
        headers["Authorization"] = authorization;
    return headers;
}
/**
 * Upload the full connector catalog to the mainnet.
 * PUT /api/connectors — replaces entire catalog.
 */
export async function uploadConnectors(connectors, authorization) {
    const url = `${AEVATAR_MAINNET_URL}/api/connectors`;
    logger.info({ url, count: connectors.length }, "Uploading connector catalog to mainnet");
    const resp = await fetch(url, {
        method: "PUT",
        headers: buildHeaders(authorization),
        body: JSON.stringify({ Connectors: connectors }),
        signal: AbortSignal.timeout(30000),
    });
    if (!resp.ok) {
        const body = await resp.text();
        logger.error({ status: resp.status, body }, "Failed to upload connectors");
        throw new Error(`Upload connectors failed: HTTP ${resp.status} — ${body}`);
    }
    logger.info({ count: connectors.length }, "Connector catalog uploaded successfully");
}
/**
 * Upload the full role catalog to the mainnet.
 * PUT /api/roles — replaces entire catalog.
 */
export async function uploadRoles(roles, authorization) {
    const url = `${AEVATAR_MAINNET_URL}/api/roles`;
    logger.info({ url, count: roles.length }, "Uploading role catalog to mainnet");
    const resp = await fetch(url, {
        method: "PUT",
        headers: buildHeaders(authorization),
        body: JSON.stringify({ Roles: roles }),
        signal: AbortSignal.timeout(30000),
    });
    if (!resp.ok) {
        const body = await resp.text();
        logger.error({ status: resp.status, body }, "Failed to upload roles");
        throw new Error(`Upload roles failed: HTTP ${resp.status} — ${body}`);
    }
    logger.info({ count: roles.length }, "Role catalog uploaded successfully");
}
/**
 * Bind a compiled workflow to the scope's default service.
 * PUT /api/scopes/{scopeId}/binding
 * This makes the workflow available via POST /api/scopes/{scopeId}/invoke/chat:stream
 */
export async function uploadWorkflow(workflowId, yaml, name, inlineYamls, revisionId, authorization) {
    const url = `${AEVATAR_MAINNET_URL}/api/scopes/${SCOPE_ID}/binding`;
    logger.info({ url, workflowId, name }, "Binding workflow to scope");
    const body = {
        ImplementationKind: "Workflow",
        WorkflowYamls: [yaml],
        DisplayName: name,
    };
    if (revisionId)
        body.RevisionId = revisionId;
    const resp = await fetch(url, {
        method: "PUT",
        headers: buildHeaders(authorization),
        body: JSON.stringify(body),
        signal: AbortSignal.timeout(30000),
    });
    if (!resp.ok) {
        const respBody = await resp.text();
        logger.error({ workflowId, status: resp.status, body: respBody }, "Failed to bind workflow to scope");
        throw new Error(`Workflow binding failed: HTTP ${resp.status} — ${respBody}`);
    }
    const result = await resp.json();
    logger.info({ workflowId, result }, "Workflow bound to scope successfully");
    return result;
}
//# sourceMappingURL=mainnet-client.js.map