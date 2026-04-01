import { Controller } from "tsoa";
import type { WorkflowType } from "../types.js";
import type { SessionDTO } from "../models/session.js";
interface StartRequest {
    mode?: string;
    direction?: string;
    /** "deployed" (default) requires workflow to be deployed; "draft" compiles on-the-fly */
    runMode?: "draft" | "deployed";
    /** User's NyxID access token for aevatar mainnet auth */
    userToken?: string;
}
interface StartResponse {
    sessionId: string;
    runId: string;
    workflowType: string;
    status: string;
}
interface StopResponse {
    sessionId: string;
    workflowType: string;
    status: string;
}
interface StatusResponse {
    running: boolean;
    session: SessionDTO | null;
}
export declare class WorkflowRunController extends Controller {
    /**
     * Start a workflow session (research, translate, purify, verify).
     * For research type, accepts optional mode (graph_based/exploration) and direction.
     * @summary Start workflow
     */
    start(workflowType: WorkflowType, body: StartRequest): Promise<StartResponse>;
    /**
     * Stop a running workflow session.
     * @summary Stop workflow
     */
    stop(workflowType: WorkflowType): Promise<StopResponse>;
    /**
     * Get current status of a specific workflow type.
     * @summary Get workflow status
     */
    status(workflowType: WorkflowType): Promise<StatusResponse>;
    /**
     * Get status of all workflow types.
     * @summary Get all workflow statuses
     */
    allStatus(): Promise<Record<string, StatusResponse>>;
}
export {};
//# sourceMappingURL=WorkflowRunController.d.ts.map