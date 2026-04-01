const COLLECTION = "sessions";
export function getSessionCollection(db) {
    return db.collection(COLLECTION);
}
export async function ensureSessionIndexes(db) {
    const col = getSessionCollection(db);
    await col.createIndex({ id: 1 }, { unique: true });
    await col.createIndex({ runId: 1 });
    await col.createIndex({ workflowType: 1, status: 1 });
    await col.createIndex({ startedAt: -1 });
}
export function toDTO(doc) {
    const { _id, ...rest } = doc;
    return rest;
}
//# sourceMappingURL=session.js.map