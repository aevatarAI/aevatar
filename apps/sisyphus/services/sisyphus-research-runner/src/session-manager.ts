import pino from "pino";
import { randomUUID } from "node:crypto";
import cron from "node-cron";
import { getDb } from "./db.js";
import { getSessionCollection, type Session } from "./models/session.js";
import { getEventLogCollection, type AgUiEvent } from "./models/event-log.js";
import {
  fetchCompiledWorkflow,
  startExecution,
  startDeployedExecution,
  fetchWorkflowDeploymentStatus,
  streamEvents,
  terminateExecution,
} from "./aevatar-client.js";
import type { WorkflowType, SessionStatus } from "./types.js";
import { broadcastEvent } from "./ws-server.js";
import { parseSseStream } from "./sse-parser.js";

const logger = pino({ name: "runner:session-manager" });

const CHRONO_GRAPH_URL = process.env["CHRONO_GRAPH_URL"] ?? "http://localhost:3000";
const GRAPH_ID = process.env["GRAPH_ID"] ?? "";

const VERIFY_CRON_INTERVAL_HOURS = parseInt(
  process.env["VERIFY_CRON_INTERVAL_HOURS"] ?? "6",
  10
);

// Active sessions keyed by sessionId
const activeSessions = new Map<string, {
  session: Session;
  abortController: AbortController;
}>();

let verifyCronTask: cron.ScheduledTask | null = null;

/**
 * Start a workflow session.
 * @param options.runMode "deployed" (default) enforces workflow must be deployed; "draft" compiles on-the-fly
 */
export async function startSession(
  workflowType: WorkflowType,
  triggeredBy: string,
  options?: { mode?: string; direction?: string; runMode?: "draft" | "deployed"; authorization?: string }
): Promise<Session> {
  const runMode = options?.runMode ?? "deployed";
  const sessionId = randomUUID();
  const now = new Date().toISOString();

  logger.info({ sessionId, workflowType, triggeredBy, runMode }, "Starting workflow session");

  const prompt = buildPrompt(workflowType, options);
  let executionId: string;

  let sseStream: ReadableStream<Uint8Array> | null = null;

  if (runMode === "deployed") {
    const result = await startDeployedExecution(workflowType, prompt, options?.authorization);
    executionId = result.executionId;
    sseStream = result.stream; // SSE stream returned directly from chat:stream
  } else {
    const compiled = await fetchCompiledWorkflow(workflowType);
    const result = await startExecution(compiled.workflowYaml, prompt);
    executionId = result.executionId;
  }

  const session: Session = {
    id: sessionId,
    runId: executionId,
    workflowType,
    status: "running",
    triggeredBy,
    startedAt: now,
    mode: options?.mode,
    direction: options?.direction,
  };

  // Persist session
  const db = getDb();
  await getSessionCollection(db).insertOne(session);

  // Track active session
  const abortController = new AbortController();
  activeSessions.set(sessionId, { session, abortController });

  // Consume SSE events — use stream from chat:stream if available, otherwise try legacy events endpoint
  consumeEventsFromStream(session, sseStream, options?.authorization, abortController.signal).catch((err) => {
    logger.error({ sessionId, err: err.message }, "Event consumption failed");
  });

  logger.info({ sessionId, executionId, workflowType }, "Workflow session started");
  return session;
}

/**
 * Stop a running workflow session.
 */
export async function stopSession(workflowType: WorkflowType): Promise<Session | null> {
  // Find active session by workflowType (there may be multiple, stop the first one found)
  let foundKey: string | null = null;
  let active: { session: Session; abortController: AbortController } | null = null;
  for (const [key, val] of activeSessions) {
    if (val.session.workflowType === workflowType) { foundKey = key; active = val; break; }
  }
  if (!active || !foundKey) return null;

  const { session, abortController } = active;
  logger.info({ sessionId: session.id, workflowType }, "Stopping workflow session");

  abortController.abort();
  activeSessions.delete(foundKey);

  // Terminate on Aevatar Engine
  await terminateExecution(session.runId);

  // Update session in MongoDB
  const now = new Date().toISOString();
  const startMs = new Date(session.startedAt).getTime();
  const duration = Date.now() - startMs;

  const db = getDb();
  await getSessionCollection(db).updateOne(
    { id: session.id },
    {
      $set: {
        status: "stopped" as SessionStatus,
        stoppedAt: now,
        duration,
      },
    }
  );

  session.status = "stopped";
  session.stoppedAt = now;
  session.duration = duration;

  logger.info({ sessionId: session.id, duration }, "Workflow session stopped");
  return session;
}

/**
 * Get current status for a workflow type.
 */
export function getSessionStatus(workflowType: WorkflowType): {
  running: boolean;
  session: Session | null;
} {
  for (const [, val] of activeSessions) {
    if (val.session.workflowType === workflowType) return { running: true, session: val.session };
  }
  return { running: false, session: null };
}

/**
 * Get status of all workflow types.
 */
export function getAllSessionStatus(): Record<WorkflowType, { running: boolean; session: Session | null }> {
  const types: WorkflowType[] = ["research", "translate", "purify", "verify"];
  const result = {} as Record<WorkflowType, { running: boolean; session: Session | null }>;
  for (const type of types) {
    result[type] = getSessionStatus(type);
  }
  return result;
}

/**
 * Crash recovery: check for stale running sessions and mark them as failed.
 */
export async function recoverStaleSessions(): Promise<void> {
  const db = getDb();
  const col = getSessionCollection(db);

  const staleSessions = await col.find({ status: "running" }).toArray();
  if (staleSessions.length === 0) {
    logger.info("No stale sessions found during recovery");
    return;
  }

  logger.info({ count: staleSessions.length }, "Found stale sessions, recovering");

  for (const session of staleSessions) {
    try {
      await terminateExecution(session.runId);
    } catch (err) {
      logger.warn({ sessionId: session.id, err: (err as Error).message }, "Failed to terminate stale execution");
    }

    await col.updateOne(
      { id: session.id },
      {
        $set: {
          status: "failed" as SessionStatus,
          stoppedAt: new Date().toISOString(),
          error: "service_restart",
        },
      }
    );

    logger.info({ sessionId: session.id, runId: session.runId }, "Stale session marked as failed");
  }
}

/**
 * Start the verify cron job.
 */
export function startVerifyCron(): void {
  if (verifyCronTask) {
    logger.warn("Verify cron already running");
    return;
  }

  // Run every N hours
  const cronExpr = `0 */${VERIFY_CRON_INTERVAL_HOURS} * * *`;
  logger.info({ cronExpr, intervalHours: VERIFY_CRON_INTERVAL_HOURS }, "Starting verify cron job");

  verifyCronTask = cron.schedule(cronExpr, async () => {
    logger.info("Verify cron triggered");

    // Skip if verify is already running
    if (activeSessions.has("verify")) {
      logger.info("Verify workflow already running, skipping cron trigger");
      return;
    }

    // Check if unverified blue nodes exist before triggering
    const hasUnverified = await checkUnverifiedBlueNodes();
    if (!hasUnverified) {
      logger.info("No unverified blue nodes found, skipping verify cron trigger");
      return;
    }

    try {
      await startSession("verify", "cron");
    } catch (err) {
      logger.error({ err: (err as Error).message }, "Verify cron trigger failed");
    }
  });
}

/**
 * Stop the verify cron job.
 */
export function stopVerifyCron(): void {
  if (verifyCronTask) {
    verifyCronTask.stop();
    verifyCronTask = null;
    logger.info("Verify cron job stopped");
  }
}

/**
 * Check chrono-graph for blue (purified) nodes that haven't been verified yet.
 * Returns true if unverified blue nodes exist.
 */
async function checkUnverifiedBlueNodes(): Promise<boolean> {
  try {
    const url = `${CHRONO_GRAPH_URL}/api/graphs/${GRAPH_ID}/nodes?sisyphus_status=purified&limit=1`;
    const resp = await fetch(url, { signal: AbortSignal.timeout(10000) });

    if (!resp.ok) {
      logger.warn({ status: resp.status }, "Failed to check blue nodes, assuming unverified exist");
      return true; // Fail-open: trigger verify if we can't check
    }

    const result = await resp.json() as { nodes?: unknown[] };
    const hasUnverified = Array.isArray(result.nodes) && result.nodes.length > 0;
    logger.info({ hasUnverified }, "Checked for unverified blue nodes");
    return hasUnverified;
  } catch (err) {
    logger.warn({ err: (err as Error).message }, "Error checking blue nodes, assuming unverified exist");
    return true; // Fail-open
  }
}

/**
 * Consume AG-UI SSE events from Aevatar Engine and forward to WebSocket clients.
 */
async function consumeEventsFromStream(
  session: Session,
  sseStream: ReadableStream<Uint8Array> | null,
  authorization?: string,
  signal?: AbortSignal,
): Promise<void> {
  const db = getDb();
  const eventCol = getEventLogCollection(db);

  let stream = sseStream;
  if (!stream) {
    // Fallback: try legacy events endpoint
    try {
      stream = await streamEvents(session.runId, authorization, signal);
    } catch (err) {
      logger.error({ sessionId: session.id, err: (err as Error).message }, "Failed to connect to event stream");
      return;
    }
  }

  if (!stream) return;

  try {
    for await (const evt of parseSseStream(stream, signal)) {
      const eventType = detectEventType(evt);

      // Persist event
      const now = new Date();
      const agEvent: AgUiEvent = {
        sessionId: session.id,
        timestamp: now.toISOString(),
        eventType,
        createdAt: now,
        payload: evt,
      };
      await eventCol.insertOne(agEvent);

      // Forward to WebSocket clients
      broadcastEvent(session.workflowType, agEvent);

      // Check for completion
      if (evt.runFinished) {
        await markCompleted(session);
      }
      if (evt.runError) {
        await markFailed(session, (evt.runError as Record<string, unknown>)?.message as string ?? "workflow error");
      }
    }
  } catch (err) {
    if (!signal?.aborted) {
      logger.error({ sessionId: session.id, err: (err as Error).message }, "Event stream error");
    }
  }
}

function detectEventType(evt: Record<string, unknown>): string {
  if (evt.runFinished) return "run_finished";
  if (evt.runError) return "run_error";
  if (evt.stepStarted) return "step_started";
  if (evt.stepCompleted) return "step_completed";
  if (evt.textMessageStart) return "text_message_start";
  if (evt.textMessageContent) return "text_message_content";
  if (evt.textMessageEnd) return "text_message_end";
  return "unknown";
}

async function markCompleted(session: Session): Promise<void> {
  const db = getDb();
  const now = new Date().toISOString();
  const duration = Date.now() - new Date(session.startedAt).getTime();

  await getSessionCollection(db).updateOne(
    { id: session.id },
    { $set: { status: "completed" as SessionStatus, stoppedAt: now, duration } }
  );

  session.status = "completed";
  activeSessions.delete(session.id);
  logger.info({ sessionId: session.id, duration }, "Workflow session completed");
}

async function markFailed(session: Session, error: string): Promise<void> {
  const db = getDb();
  const now = new Date().toISOString();
  const duration = Date.now() - new Date(session.startedAt).getTime();

  await getSessionCollection(db).updateOne(
    { id: session.id },
    { $set: { status: "failed" as SessionStatus, stoppedAt: now, duration, error } }
  );

  session.status = "failed";
  session.error = error;
  activeSessions.delete(session.id);
  logger.error({ sessionId: session.id, error, duration }, "Workflow session failed");
}

function buildPrompt(workflowType: WorkflowType, options?: { mode?: string; direction?: string }): string {
  if (workflowType === "research" && options?.mode === "exploration" && options.direction) {
    return `Start exploration research: ${options.direction}`;
  }
  return `Start ${workflowType} workflow`;
}
