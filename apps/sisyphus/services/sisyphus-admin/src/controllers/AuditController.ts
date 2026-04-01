import {
  Body,
  Controller,
  Get,
  Post,
  Query,
  Route,
  Response,
  SuccessResponse,
  Tags,
} from "tsoa";
import pino from "pino";
import { randomUUID } from "node:crypto";
import { getDb } from "../db.js";
import {
  getAuditEventCollection,
  toDTO,
  type AuditEvent,
  type AuditEventDTO,
  type AuditAction,
  type AuditResource,
} from "../models/audit-event.js";
import type { ErrorResponse } from "../types.js";

const logger = pino({ name: "admin:audit-controller" });

interface CreateAuditEventRequest {
  userId: string;
  userName?: string;
  action: AuditAction;
  resource: AuditResource;
  resourceId?: string;
  resourceName?: string;
  service: string;
  details?: Record<string, unknown>;
}

interface AuditListResponse {
  events: AuditEventDTO[];
  total: number;
  page: number;
  pageSize: number;
}

@Route("audit")
@Tags("Audit")
export class AuditController extends Controller {
  /**
   * Record an audit event. Called by other Sisyphus services after operations.
   * @summary Record audit event
   */
  @Post()
  @SuccessResponse(201, "Event recorded")
  @Response<ErrorResponse>(400, "Bad request")
  public async recordEvent(
    @Body() body: CreateAuditEventRequest,
  ): Promise<AuditEventDTO> {
    const db = getDb();
    const col = getAuditEventCollection(db);

    const now = new Date();
    const doc: AuditEvent = {
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
    logger.info(
      { id: doc.id, userId: doc.userId, action: doc.action, resource: doc.resource, service: doc.service },
      "Audit event recorded",
    );

    this.setStatus(201);
    return toDTO(doc);
  }

  /**
   * List audit events with filtering and pagination.
   * @summary List audit events
   */
  @Get()
  public async listEvents(
    @Query() page: number = 1,
    @Query() pageSize: number = 50,
    @Query() userId?: string,
    @Query() action?: AuditAction,
    @Query() resource?: AuditResource,
    @Query() service?: string,
    @Query() since?: string,
    @Query() until?: string,
  ): Promise<AuditListResponse> {
    pageSize = Math.min(pageSize, 200);
    const db = getDb();
    const col = getAuditEventCollection(db);

    const filter: Record<string, unknown> = {};
    if (userId) filter.userId = userId;
    if (action) filter.action = action;
    if (resource) filter.resource = resource;
    if (service) filter.service = service;
    if (since || until) {
      const tsFilter: Record<string, string> = {};
      if (since) tsFilter.$gte = since;
      if (until) tsFilter.$lte = until;
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
}
