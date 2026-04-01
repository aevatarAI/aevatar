import { WebSocketServer, WebSocket } from "ws";
import type { Server } from "node:http";
import pino from "pino";
import type { WorkflowType } from "./types.js";
import type { AgUiEvent } from "./models/event-log.js";

const logger = pino({ name: "runner:ws-server" });

// Clients subscribed per workflow type
const subscriptions = new Map<WorkflowType, Set<WebSocket>>();

export function initWebSocket(server: Server): WebSocketServer {
  const wss = new WebSocketServer({ server });

  wss.on("connection", (ws, req) => {
    const url = new URL(req.url ?? "/", `http://${req.headers.host}`);
    const pathMatch = url.pathname.match(/^\/ws\/workflows\/(\w+)\/events$/);

    if (!pathMatch) {
      logger.warn({ path: url.pathname }, "Invalid WebSocket path");
      ws.close(4000, "Invalid path. Use /ws/workflows/:type/events");
      return;
    }

    const workflowType = pathMatch[1] as WorkflowType;
    const validTypes: WorkflowType[] = ["research", "translate", "purify", "verify"];
    if (!validTypes.includes(workflowType)) {
      ws.close(4001, `Invalid workflow type: ${workflowType}`);
      return;
    }

    // Add to subscriptions
    if (!subscriptions.has(workflowType)) {
      subscriptions.set(workflowType, new Set());
    }
    subscriptions.get(workflowType)!.add(ws);

    logger.info({ workflowType, clients: subscriptions.get(workflowType)!.size }, "WebSocket client connected");

    ws.on("close", () => {
      subscriptions.get(workflowType)?.delete(ws);
      logger.info({ workflowType }, "WebSocket client disconnected");
    });

    ws.on("error", (err) => {
      logger.error({ workflowType, err: err.message }, "WebSocket error");
      subscriptions.get(workflowType)?.delete(ws);
    });
  });

  logger.info("WebSocket server initialized");
  return wss;
}

/**
 * Broadcast an AG-UI event to all WebSocket clients subscribed to a workflow type.
 */
export function broadcastEvent(workflowType: WorkflowType, event: AgUiEvent): void {
  const clients = subscriptions.get(workflowType);
  if (!clients || clients.size === 0) return;

  const message = JSON.stringify(event);
  let sent = 0;

  for (const ws of clients) {
    if (ws.readyState === WebSocket.OPEN) {
      ws.send(message);
      sent++;
    }
  }

  if (sent > 0) {
    logger.debug({ workflowType, sent, eventType: event.eventType }, "Broadcasted event");
  }
}
