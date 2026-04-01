import { type Collection, type Db } from "mongodb";
import type { MainnetConnectorDto, MainnetRoleDto } from "../types.js";
export interface CompiledArtifact {
    id: string;
    workflowId: string;
    workflowYaml: string;
    connectorJson: Record<string, unknown>[];
    mainnetConnectors: MainnetConnectorDto[];
    mainnetRoles: MainnetRoleDto[];
    contentHash: string;
    compiledAt: string;
}
export declare function getCompiledArtifactCollection(db: Db): Collection<CompiledArtifact>;
export declare function ensureCompiledArtifactIndexes(db: Db): Promise<void>;
export declare function toDTO(doc: CompiledArtifact & {
    _id?: unknown;
}): Omit<CompiledArtifact, "_id">;
//# sourceMappingURL=compiled-artifact.d.ts.map