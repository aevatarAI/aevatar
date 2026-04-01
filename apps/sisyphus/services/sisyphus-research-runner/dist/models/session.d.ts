import { type Collection, type Db } from "mongodb";
import type { WorkflowType, SessionStatus } from "../types.js";
export interface Session {
    id: string;
    runId: string;
    workflowType: WorkflowType;
    status: SessionStatus;
    triggeredBy: string;
    startedAt: string;
    stoppedAt?: string;
    duration?: number;
    error?: string;
    mode?: string;
    direction?: string;
}
export interface SessionDTO {
    id: string;
    runId: string;
    workflowType: WorkflowType;
    status: SessionStatus;
    triggeredBy: string;
    startedAt: string;
    stoppedAt?: string;
    duration?: number;
    error?: string;
    mode?: string;
    direction?: string;
}
export declare function getSessionCollection(db: Db): Collection<Session>;
export declare function ensureSessionIndexes(db: Db): Promise<void>;
export declare function toDTO(doc: Session & {
    _id?: unknown;
}): SessionDTO;
//# sourceMappingURL=session.d.ts.map