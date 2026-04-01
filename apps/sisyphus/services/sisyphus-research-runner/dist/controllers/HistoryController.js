var __decorate = (this && this.__decorate) || function (decorators, target, key, desc) {
    var c = arguments.length, r = c < 3 ? target : desc === null ? desc = Object.getOwnPropertyDescriptor(target, key) : desc, d;
    if (typeof Reflect === "object" && typeof Reflect.decorate === "function") r = Reflect.decorate(decorators, target, key, desc);
    else for (var i = decorators.length - 1; i >= 0; i--) if (d = decorators[i]) r = (c < 3 ? d(r) : c > 3 ? d(target, key, r) : d(target, key)) || r;
    return c > 3 && r && Object.defineProperty(target, key, r), r;
};
var __metadata = (this && this.__metadata) || function (k, v) {
    if (typeof Reflect === "object" && typeof Reflect.metadata === "function") return Reflect.metadata(k, v);
};
var __param = (this && this.__param) || function (paramIndex, decorator) {
    return function (target, key) { decorator(target, key, paramIndex); }
};
import { Controller, Get, Path, Query, Route, Response, Tags, } from "tsoa";
import { getDb } from "../db.js";
import { getSessionCollection, toDTO } from "../models/session.js";
import { getEventLogCollection } from "../models/event-log.js";
let HistoryController = class HistoryController extends Controller {
    /**
     * Get paginated list of all workflow trigger records.
     * Supports filtering by workflow_type, status, and date range.
     * @summary List trigger history
     */
    async listHistory(page = 1, pageSize = 20, workflowType, status, startDate, endDate) {
        pageSize = Math.min(pageSize, 100);
        const db = getDb();
        const col = getSessionCollection(db);
        const filter = {};
        if (workflowType)
            filter.workflowType = workflowType;
        if (status)
            filter.status = status;
        if (startDate || endDate) {
            const dateFilter = {};
            if (startDate)
                dateFilter.$gte = startDate;
            if (endDate)
                dateFilter.$lte = endDate;
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
    async getSessionDetail(sessionId) {
        const db = getDb();
        const sessionCol = getSessionCollection(db);
        const eventCol = getEventLogCollection(db);
        const session = await sessionCol.findOne({ id: sessionId });
        if (!session) {
            this.setStatus(404);
            return { error: "Session not found" };
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
};
__decorate([
    Get(),
    __param(0, Query()),
    __param(1, Query()),
    __param(2, Query()),
    __param(3, Query()),
    __param(4, Query()),
    __param(5, Query()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [Number, Number, String, String, String, String]),
    __metadata("design:returntype", Promise)
], HistoryController.prototype, "listHistory", null);
__decorate([
    Get("{sessionId}"),
    Response(404, "Session not found"),
    __param(0, Path()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String]),
    __metadata("design:returntype", Promise)
], HistoryController.prototype, "getSessionDetail", null);
HistoryController = __decorate([
    Route("history"),
    Tags("History")
], HistoryController);
export { HistoryController };
//# sourceMappingURL=HistoryController.js.map