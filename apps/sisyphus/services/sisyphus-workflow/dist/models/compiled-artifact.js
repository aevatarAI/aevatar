const COLLECTION = "compiled_artifacts";
export function getCompiledArtifactCollection(db) {
    return db.collection(COLLECTION);
}
export async function ensureCompiledArtifactIndexes(db) {
    const col = getCompiledArtifactCollection(db);
    await col.createIndex({ id: 1 }, { unique: true });
    await col.createIndex({ workflowId: 1 });
}
export function toDTO(doc) {
    const { _id, ...rest } = doc;
    return rest;
}
//# sourceMappingURL=compiled-artifact.js.map