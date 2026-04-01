const COLLECTION = "workflow_definitions";
export function getWorkflowCollection(db) {
    return db.collection(COLLECTION);
}
export async function ensureWorkflowIndexes(db) {
    const col = getWorkflowCollection(db);
    await col.createIndex({ id: 1 }, { unique: true });
    await col.createIndex({ name: 1 });
}
export function toDTO(doc) {
    const { _id, ...rest } = doc;
    return rest;
}
//# sourceMappingURL=workflow-definition.js.map