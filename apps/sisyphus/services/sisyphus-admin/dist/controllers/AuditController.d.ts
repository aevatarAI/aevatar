import { Controller } from "tsoa";
import { type AuditEventDTO, type AuditAction, type AuditResource } from "../models/audit-event.js";
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
export declare class AuditController extends Controller {
    /**
     * Record an audit event. Called by other Sisyphus services after operations.
     * @summary Record audit event
     */
    recordEvent(body: CreateAuditEventRequest): Promise<AuditEventDTO>;
    /**
     * List audit events with filtering and pagination.
     * @summary List audit events
     */
    listEvents(page?: number, pageSize?: number, userId?: string, action?: AuditAction, resource?: AuditResource, service?: string, since?: string, until?: string): Promise<AuditListResponse>;
}
export {};
//# sourceMappingURL=AuditController.d.ts.map