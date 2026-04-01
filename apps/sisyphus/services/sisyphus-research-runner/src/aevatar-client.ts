import pino from "pino";

const logger = pino({ name: "runner:aevatar-client" });

const AEVATAR_API = process.env["AEVATAR_API_URL"] ?? "http://localhost:6688";
const WORKFLOW_SERVICE_URL = process.env["WORKFLOW_SERVICE_URL"] ?? "http://localhost:8081";
const SCOPE_ID = process.env["SCOPE_ID"] ?? "76fe9d91-1f1d-4234-9352-819a7c28f709";

export interface StartResult {
  executionId: string;
  status: string;
}

/**
 * Fetch compiled YAML from sisyphus-workflow service.
 */
export async function fetchCompiledWorkflow(workflowId: string): Promise<{
  workflowYaml: string;
  connectorJson: Record<string, unknown>[];
}> {
  const url = `${WORKFLOW_SERVICE_URL}/compile/${workflowId}`;
  logger.info({ workflowId, url }, "Fetching compiled workflow");

  const resp = await fetch(url, { signal: AbortSignal.timeout(15000) });
  if (!resp.ok) {
    const body = await resp.text();
    throw new Error(`Failed to fetch compiled workflow: HTTP ${resp.status} — ${body}`);
  }

  return await resp.json() as { workflowYaml: string; connectorJson: Record<string, unknown>[] };
}

/**
 * Submit compiled workflow to Aevatar Engine for execution.
 * Returns an SSE stream of AG-UI events.
 */
export async function startExecution(
  workflowYaml: string,
  prompt: string,
  signal?: AbortSignal
): Promise<{ executionId: string; stream: ReadableStream<Uint8Array> | null }> {
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

  const result = await resp.json() as Record<string, unknown>;
  const executionId = result.executionId as string;

  logger.info({ executionId }, "Execution started");

  return { executionId, stream: null };
}

/**
 * Stream AG-UI events from Aevatar Engine via SSE.
 */
export async function streamEvents(
  executionId: string,
  authorization?: string,
  signal?: AbortSignal,
): Promise<ReadableStream<Uint8Array> | null> {
  const url = `${AEVATAR_API}/api/executions/${executionId}/events`;
  logger.info({ executionId, url }, "Connecting to AG-UI event stream");

  const headers: Record<string, string> = { Accept: "text/event-stream" };
  if (authorization) headers["Authorization"] = authorization;

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
export async function fetchWorkflowDeploymentStatus(workflowId: string): Promise<{
  deploymentState: { status: string; deployError?: string };
}> {
  const url = `${WORKFLOW_SERVICE_URL}/workflows/${workflowId}/deployment-status`;
  logger.info({ workflowId, url }, "Fetching workflow deployment status");

  const resp = await fetch(url, { signal: AbortSignal.timeout(10000) });
  if (!resp.ok) {
    const body = await resp.text();
    throw new Error(`Failed to fetch deployment status: HTTP ${resp.status} — ${body}`);
  }

  return await resp.json() as { deploymentState: { status: string; deployError?: string } };
}

/**
 * Start execution for a deployed workflow via scope chat:stream endpoint.
 * Returns the SSE stream directly — caller should consume events from it.
 */
export async function startDeployedExecution(
  workflowName: string,
  prompt: string,
  authorization?: string,
  signal?: AbortSignal,
): Promise<{ executionId: string; stream: ReadableStream<Uint8Array> | null }> {
  const url = `${AEVATAR_API}/api/scopes/${SCOPE_ID}/invoke/chat:stream`;
  logger.info({ url, workflowName, scopeId: SCOPE_ID }, "Starting deployed workflow execution (SSE stream)");

  const headers: Record<string, string> = { "Content-Type": "application/json" };
  if (authorization) headers["Authorization"] = authorization;

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
export async function terminateExecution(executionId: string): Promise<void> {
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
    } else {
      logger.info({ executionId }, "Execution terminated");
    }
  } catch (err) {
    logger.error({ executionId, err: (err as Error).message }, "Error terminating execution");
  }
}
