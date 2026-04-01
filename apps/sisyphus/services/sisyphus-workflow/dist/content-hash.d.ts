import type { WorkflowDefinition } from "./models/workflow-definition.js";
import type { ConnectorDefinition } from "./models/connector-definition.js";
/**
 * Compute a deterministic SHA-256 hash of a workflow definition and its referenced connectors.
 * Used to detect whether a re-deploy is needed.
 *
 * Excludes: id, createdAt, updatedAt, deploymentState (meta fields that don't affect compilation output).
 * Excludes: skill content from chrono-ornn (always fetched fresh on compile).
 */
export declare function computeContentHash(workflow: WorkflowDefinition, referencedConnectors: ConnectorDefinition[]): string;
//# sourceMappingURL=content-hash.d.ts.map