const COLLECTION = "agui_events";
const TTL_SECONDS = parseInt(process.env["EVENT_TTL_DAYS"] ?? "30", 10) * 24 * 60 * 60;
export function getEventLogCollection(db) {
    return db.collection(COLLECTION);
}
export async function ensureEventLogIndexes(db) {
    const col = getEventLogCollection(db);
    await col.createIndex({ sessionId: 1, timestamp: 1 });
    await col.createIndex({ createdAt: 1 }, { expireAfterSeconds: TTL_SECONDS });
}
//# sourceMappingURL=event-log.js.map