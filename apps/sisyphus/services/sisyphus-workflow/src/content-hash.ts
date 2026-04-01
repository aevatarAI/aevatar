import { createHash } from "node:crypto";
import type { WorkflowDefinition } from "./models/workflow-definition.js";
import type { ConnectorDefinition } from "./models/connector-definition.js";

/**
 * Compute a deterministic SHA-256 hash of a workflow definition and its referenced connectors.
 * Used to detect whether a re-deploy is needed.
 *
 * Excludes: id, createdAt, updatedAt, deploymentState (meta fields that don't affect compilation output).
 * Excludes: skill content from chrono-ornn (always fetched fresh on compile).
 */
export function computeContentHash(
  workflow: WorkflowDefinition,
  referencedConnectors: ConnectorDefinition[],
): string {
  const canonical = {
    name: workflow.name,
    description: workflow.description,
    roles: [...workflow.roles].sort((a, b) => a.name.localeCompare(b.name)),
    steps: [...workflow.steps].sort((a, b) => a.order - b.order),
    parameters: workflow.parameters ?? {},
    connectors: [...referencedConnectors]
      .sort((a, b) => a.name.localeCompare(b.name))
      .map(({ name, description, type, baseUrl, authConfig, endpoints, mcpConfig }) => ({
        name, description, type, baseUrl, authConfig, endpoints, mcpConfig,
      })),
  };

  return createHash("sha256").update(JSON.stringify(canonical)).digest("hex");
}
