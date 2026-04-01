import { WebSocketServer } from "ws";
import type { Server } from "node:http";
import type { WorkflowType } from "./types.js";
import type { AgUiEvent } from "./models/event-log.js";
export declare function initWebSocket(server: Server): WebSocketServer;
/**
 * Broadcast an AG-UI event to all WebSocket clients subscribed to a workflow type.
 */
export declare function broadcastEvent(workflowType: WorkflowType, event: AgUiEvent): void;
//# sourceMappingURL=ws-server.d.ts.map