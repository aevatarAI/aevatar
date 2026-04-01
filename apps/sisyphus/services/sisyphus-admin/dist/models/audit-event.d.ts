import { type Collection, type Db } from "mongodb";
export type AuditAction = "create" | "update" | "delete" | "compile" | "deploy" | "trigger" | "stop" | "login" | "logout";
export type AuditResource = "workflow" | "connector" | "schema" | "settings" | "session" | "user";
/**
 * A single audit trail entry recording who did what to which resource.
 * Other Sisyphus services POST events here after performing operations.
 */
export interface AuditEvent {
    id: string;
    timestamp: string;
    userId: string;
    userName?: string;
    action: AuditAction;
    resource: AuditResource;
    resourceId?: string;
    resourceName?: string;
    service: string;
    details?: Record<string, unknown>;
    createdAt: Date;
}
export type AuditEventDTO = Omit<AuditEvent, "_id" | "createdAt"> & {
    createdAt: string;
};
export declare function getAuditEventCollection(db: Db): Collection<AuditEvent>;
export declare function ensureAuditEventIndexes(db: Db): Promise<void>;
export declare function toDTO(doc: AuditEvent & {
    _id?: unknown;
}): AuditEventDTO;
//# sourceMappingURL=audit-event.d.ts.map