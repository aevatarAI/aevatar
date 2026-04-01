import { type Session } from "./models/session.js";
import type { WorkflowType } from "./types.js";
/**
 * Start a workflow session.
 * @param options.runMode "deployed" (default) enforces workflow must be deployed; "draft" compiles on-the-fly
 */
export declare function startSession(workflowType: WorkflowType, triggeredBy: string, options?: {
    mode?: string;
    direction?: string;
    runMode?: "draft" | "deployed";
    authorization?: string;
}): Promise<Session>;
/**
 * Stop a running workflow session.
 */
export declare function stopSession(workflowType: WorkflowType): Promise<Session | null>;
/**
 * Get current status for a workflow type.
 */
export declare function getSessionStatus(workflowType: WorkflowType): {
    running: boolean;
    session: Session | null;
};
/**
 * Get status of all workflow types.
 */
export declare function getAllSessionStatus(): Record<WorkflowType, {
    running: boolean;
    session: Session | null;
}>;
/**
 * Crash recovery: check for stale running sessions and mark them as failed.
 */
export declare function recoverStaleSessions(): Promise<void>;
/**
 * Start the verify cron job.
 */
export declare function startVerifyCron(): void;
/**
 * Stop the verify cron job.
 */
export declare function stopVerifyCron(): void;
//# sourceMappingURL=session-manager.d.ts.map