import {
  Controller,
  Get,
  Path,
  Query,
  Route,
  Response,
  Tags,
} from "tsoa";
import { getDb } from "../db.js";
import { getSessionCollection, toDTO, type SessionDTO } from "../models/session.js";
import { getEventLogCollection, type AgUiEvent } from "../models/event-log.js";
import type { ErrorResponse } from "../types.js";

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

@Route("history")
@Tags("History")
export class HistoryController extends Controller {
  /**
   * Get paginated list of all workflow trigger records.
   * Supports filtering by workflow_type, status, and date range.
   * @summary List trigger history
   */
  @Get()
  public async listHistory(
    @Query() page: number = 1,
    @Query() pageSize: number = 20,
    @Query() workflowType?: string,
    @Query() status?: string,
    @Query() startDate?: string,
    @Query() endDate?: string
  ): Promise<HistoryListResponse> {
    pageSize = Math.min(pageSize, 100);
    const db = getDb();
    const col = getSessionCollection(db);

    const filter: Record<string, unknown> = {};
    if (workflowType) filter.workflowType = workflowType;
    if (status) filter.status = status;
    if (startDate || endDate) {
      const dateFilter: Record<string, string> = {};
      if (startDate) dateFilter.$gte = startDate;
      if (endDate) dateFilter.$lte = endDate;
      filter.startedAt = dateFilter;
    }

    const skip = (page - 1) * pageSize;
    const [records, total] = await Promise.all([
      col.find(filter).sort({ startedAt: -1 }).skip(skip).limit(pageSize).toArray(),
      col.countDocuments(filter),
    ]);

    return {
      records: records.map(toDTO),
      total,
      page,
      pageSize,
    };
  }

  /**
   * Get the full AG-UI event log for a session.
   * @summary Get session detail with events
   */
  @Get("{sessionId}")
  @Response<ErrorResponse>(404, "Session not found")
  public async getSessionDetail(
    @Path() sessionId: string
  ): Promise<SessionDetailResponse> {
    const db = getDb();
    const sessionCol = getSessionCollection(db);
    const eventCol = getEventLogCollection(db);

    const session = await sessionCol.findOne({ id: sessionId });
    if (!session) {
      this.setStatus(404);
      return { error: "Session not found" } as unknown as SessionDetailResponse;
    }

    const events = await eventCol
      .find({ sessionId })
      .sort({ timestamp: 1 })
      .toArray();

    return {
      session: toDTO(session),
      events: events.map(({ _id, ...rest }) => rest),
    };
  }
}
