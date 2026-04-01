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
import { Body, Controller, Get, Post, Query, Route, Response, SuccessResponse, Tags, } from "tsoa";
import pino from "pino";
import { randomUUID } from "node:crypto";
import { getDb } from "../db.js";
import { getAuditEventCollection, toDTO, } from "../models/audit-event.js";
const logger = pino({ name: "admin:audit-controller" });
let AuditController = class AuditController extends Controller {
    /**
     * Record an audit event. Called by other Sisyphus services after operations.
     * @summary Record audit event
     */
    async recordEvent(body) {
        const db = getDb();
        const col = getAuditEventCollection(db);
        const now = new Date();
        const doc = {
            id: randomUUID(),
            timestamp: now.toISOString(),
            userId: body.userId,
            userName: body.userName,
            action: body.action,
            resource: body.resource,
            resourceId: body.resourceId,
            resourceName: body.resourceName,
            service: body.service,
            details: body.details,
            createdAt: now,
        };
        await col.insertOne(doc);
        logger.info({ id: doc.id, userId: doc.userId, action: doc.action, resource: doc.resource, service: doc.service }, "Audit event recorded");
        this.setStatus(201);
        return toDTO(doc);
    }
    /**
     * List audit events with filtering and pagination.
     * @summary List audit events
     */
    async listEvents(page = 1, pageSize = 50, userId, action, resource, service, since, until) {
        pageSize = Math.min(pageSize, 200);
        const db = getDb();
        const col = getAuditEventCollection(db);
        const filter = {};
        if (userId)
            filter.userId = userId;
        if (action)
            filter.action = action;
        if (resource)
            filter.resource = resource;
        if (service)
            filter.service = service;
        if (since || until) {
            const tsFilter = {};
            if (since)
                tsFilter.$gte = since;
            if (until)
                tsFilter.$lte = until;
            filter.timestamp = tsFilter;
        }
        const skip = (page - 1) * pageSize;
        const [events, total] = await Promise.all([
            col.find(filter).sort({ timestamp: -1 }).skip(skip).limit(pageSize).toArray(),
            col.countDocuments(filter),
        ]);
        return {
            events: events.map(toDTO),
            total,
            page,
            pageSize,
        };
    }
};
__decorate([
    Post(),
    SuccessResponse(201, "Event recorded"),
    Response(400, "Bad request"),
    __param(0, Body()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [Object]),
    __metadata("design:returntype", Promise)
], AuditController.prototype, "recordEvent", null);
__decorate([
    Get(),
    __param(0, Query()),
    __param(1, Query()),
    __param(2, Query()),
    __param(3, Query()),
    __param(4, Query()),
    __param(5, Query()),
    __param(6, Query()),
    __param(7, Query()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [Number, Number, String, String, String, String, String, String]),
    __metadata("design:returntype", Promise)
], AuditController.prototype, "listEvents", null);
AuditController = __decorate([
    Route("audit"),
    Tags("Audit")
], AuditController);
export { AuditController };
//# sourceMappingURL=AuditController.js.map