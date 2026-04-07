import pino from "pino";
const logger = pino({ name: "runner:aevatar-client" });
const AEVATAR_API = process.env["AEVATAR_API_URL"] ?? "http://localhost:6688";
const WORKFLOW_SERVICE_URL = process.env["WORKFLOW_SERVICE_URL"] ?? "http://localhost:8081";
function buildHeaders(authorization) {
    const headers = {};
    if (authorization)
        headers["Authorization"] = authorization;
    return headers;
}
async function resolveAuthenticatedScopeId(authorization) {
    if (!authorization) {
        throw new Error("Workflow start requires Authorization.");
    }
    const url = `${AEVATAR_API}/api/auth/me`;
    logger.info({ url }, "Resolving authenticated scope for deployed workflow start");
    const resp = await fetch(url, {
        method: "GET",
        headers: buildHeaders(authorization),
        signal: AbortSignal.timeout(10000),
    });
    if (!resp.ok) {
        const body = await resp.text();
        logger.error({ status: resp.status, body }, "Failed to resolve authenticated scope");
        throw new Error(`Resolve authenticated scope failed: HTTP ${resp.status} — ${body}`);
    }
    const payload = await resp.json();
    if (!payload.authenticated || !payload.scopeId?.trim()) {
        logger.error({ payload }, "Authenticated scope is unavailable for deployed workflow start");
        throw new Error("Authenticated scope is unavailable.");
    }
    return payload.scopeId.trim();
}
/**
 * Fetch compiled YAML from sisyphus-workflow service.
 */
export async function fetchCompiledWorkflow(workflowId) {
    const url = `${WORKFLOW_SERVICE_URL}/compile/${workflowId}`;
    logger.info({ workflowId, url }, "Fetching compiled workflow");
    const resp = await fetch(url, { signal: AbortSignal.timeout(15000) });
    if (!resp.ok) {
        const body = await resp.text();
        throw new Error(`Failed to fetch compiled workflow: HTTP ${resp.status} — ${body}`);
    }
    return await resp.json();
}
/**
 * Submit compiled workflow to Aevatar Engine for execution.
 * Returns an SSE stream of AG-UI events.
 */
export async function startExecution(workflowYaml, prompt, signal) {
    const url = `${AEVATAR_API}/api/executions`;
    logger.info({ url }, "Starting workflow execution");
    const resp = await fetch(url, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
            workflowName: "sisyphus",
            prompt,
            workflowYamls: [workflowYaml],
        }),
        signal,
    });
    if (!resp.ok) {
        const body = await resp.text();
        throw new Error(`Failed to start execution: HTTP ${resp.status} — ${body}`);
    }
    const result = await resp.json();
    const executionId = result.executionId;
    logger.info({ executionId }, "Execution started");
    return { executionId, stream: null };
}
/**
 * Stream AG-UI events from Aevatar Engine via SSE.
 */
export async function streamEvents(executionId, authorization, signal) {
    const url = `${AEVATAR_API}/api/executions/${executionId}/events`;
    logger.info({ executionId, url }, "Connecting to AG-UI event stream");
    const headers = { Accept: "text/event-stream" };
    if (authorization)
        headers["Authorization"] = authorization;
    const resp = await fetch(url, { headers, signal });
    if (!resp.ok) {
        const body = await resp.text();
        throw new Error(`Failed to stream events: HTTP ${resp.status} — ${body}`);
    }
    return resp.body;
}
/**
 * Fetch deployment status from sisyphus-workflow service.
 */
export async function fetchWorkflowDeploymentStatus(workflowId) {
    const url = `${WORKFLOW_SERVICE_URL}/workflows/${workflowId}/deployment-status`;
    logger.info({ workflowId, url }, "Fetching workflow deployment status");
    const resp = await fetch(url, { signal: AbortSignal.timeout(10000) });
    if (!resp.ok) {
        const body = await resp.text();
        throw new Error(`Failed to fetch deployment status: HTTP ${resp.status} — ${body}`);
    }
    return await resp.json();
}
/**
 * Start execution for a deployed workflow via scope chat:stream endpoint.
 * Returns the SSE stream directly — caller should consume events from it.
 */
export async function startDeployedExecution(workflowName, prompt, authorization, signal) {
    const scopeId = await resolveAuthenticatedScopeId(authorization);
    const url = `${AEVATAR_API}/api/scopes/${scopeId}/invoke/chat:stream`;
    logger.info({ url, workflowName, scopeId }, "Starting deployed workflow execution (SSE stream)");
    const headers = {
        "Content-Type": "application/json",
        ...buildHeaders(authorization),
    };
    const resp = await fetch(url, {
        method: "POST",
        headers,
        body: JSON.stringify({
            Prompt: prompt,
        }),
        signal,
    });
    if (!resp.ok) {
        const body = await resp.text();
        throw new Error(`Failed to start deployed execution: HTTP ${resp.status} — ${body}`);
    }
    // The response IS the SSE stream — extract executionId from X-Correlation-Id header or first event
    const executionId = resp.headers.get("X-Correlation-Id") ?? `exec-${Date.now()}`;
    logger.info({ executionId, workflowName }, "Deployed execution started with SSE stream");
    return { executionId, stream: resp.body };
}
/**
 * Terminate an execution on Aevatar Engine.
 */
export async function terminateExecution(executionId) {
    const url = `${AEVATAR_API}/api/executions/${executionId}`;
    logger.info({ executionId }, "Terminating execution");
    try {
        const resp = await fetch(url, {
            method: "DELETE",
            signal: AbortSignal.timeout(10000),
        });
        if (!resp.ok) {
            const body = await resp.text();
            logger.error({ executionId, status: resp.status, body }, "Failed to terminate execution");
        }
        else {
            logger.info({ executionId }, "Execution terminated");
        }
    }
    catch (err) {
        logger.error({ executionId, err: err.message }, "Error terminating execution");
    }
}
//# sourceMappingURL=aevatar-client.js.map