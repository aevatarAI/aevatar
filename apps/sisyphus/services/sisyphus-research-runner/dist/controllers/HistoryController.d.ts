import { Controller } from "tsoa";
import { type SessionDTO } from "../models/session.js";
import { type AgUiEvent } from "../models/event-log.js";
interface HistoryListResponse {
    records: SessionDTO[];
    total: number;
    page: number;
    pageSize: number;
}
interface SessionDetailResponse {
    session: SessionDTO;
    events: AgUiEvent[];
}
export declare class HistoryController extends Controller {
    /**
     * Get paginated list of all workflow trigger records.
     * Supports filtering by workflow_type, status, and date range.
     * @summary List trigger history
     */
    listHistory(page?: number, pageSize?: number, workflowType?: string, status?: string, startDate?: string, endDate?: string): Promise<HistoryListResponse>;
    /**
     * Get the full AG-UI event log for a session.
     * @summary Get session detail with events
     */
    getSessionDetail(sessionId: string): Promise<SessionDetailResponse>;
}
export {};
//# sourceMappingURL=HistoryController.d.ts.map