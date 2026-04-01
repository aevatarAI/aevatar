const COLLECTION = "connector_definitions";
export function getConnectorCollection(db) {
    return db.collection(COLLECTION);
}
export async function ensureConnectorIndexes(db) {
    const col = getConnectorCollection(db);
    await col.createIndex({ id: 1 }, { unique: true });
    await col.createIndex({ name: 1 });
}
export function toDTO(doc) {
    const { _id, ...rest } = doc;
    return rest;
}
//# sourceMappingURL=connector-definition.js.map