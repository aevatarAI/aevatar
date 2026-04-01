export const DEFAULT_SETTINGS = {
    id: "global",
    graphId: "",
    verifyCronIntervalHours: 6,
    eventRetentionDays: 30,
    defaultResearchMode: "graph_based",
    graphViewNodeLimit: 200,
    updatedAt: new Date().toISOString(),
};
const COLLECTION = "admin_settings";
export function getSettingsCollection(db) {
    return db.collection(COLLECTION);
}
export async function ensureSettingsIndexes(db) {
    const col = getSettingsCollection(db);
    await col.createIndex({ id: 1 }, { unique: true });
}
export function toDTO(doc) {
    const { _id, ...rest } = doc;
    return rest;
}
//# sourceMappingURL=settings.js.map