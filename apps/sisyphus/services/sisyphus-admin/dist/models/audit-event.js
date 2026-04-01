const COLLECTION = "admin_audit_events";
export function getAuditEventCollection(db) {
    return db.collection(COLLECTION);
}
export async function ensureAuditEventIndexes(db) {
    const col = getAuditEventCollection(db);
    await col.createIndex({ id: 1 }, { unique: true });
    await col.createIndex({ timestamp: -1 });
    await col.createIndex({ userId: 1, timestamp: -1 });
    await col.createIndex({ resource: 1, action: 1, timestamp: -1 });
    await col.createIndex({ service: 1, timestamp: -1 });
    // TTL: auto-delete after 365 days
    await col.createIndex({ createdAt: 1 }, { expireAfterSeconds: 365 * 24 * 60 * 60 });
}
export function toDTO(doc) {
    const { _id, createdAt, ...rest } = doc;
    return { ...rest, createdAt: createdAt.toISOString() };
}
//# sourceMappingURL=audit-event.js.map