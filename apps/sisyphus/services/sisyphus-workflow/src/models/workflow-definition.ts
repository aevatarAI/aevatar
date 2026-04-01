import { type Collection, type Db } from "mongodb";
import type { DeploymentState } from "../types.js";

export interface RoleDefinition {
  name: string;
  skillId?: string;
  description?: string;
}

export interface StepDefinition {
  name: string;
  type: string;
  order: number;
  roleRef?: string;
  connectorRef?: string;
  parameters?: Record<string, unknown>;
}

export interface WorkflowDefinition {
  id: string;
  name: string;
  description: string;
  yaml?: string;
  roles: RoleDefinition[];
  steps: StepDefinition[];
  parameters?: Record<string, unknown>;
  deploymentState?: DeploymentState;
  createdAt: string;
  updatedAt: string;
}

export interface WorkflowDefinitionDTO {
  id: string;
  name: string;
  description: string;
  yaml?: string;
  roles: RoleDefinition[];
  steps: StepDefinition[];
  parameters?: Record<string, unknown>;
  deploymentState?: DeploymentState;
  createdAt: string;
  updatedAt: string;
}

const COLLECTION = "workflow_definitions";

export function getWorkflowCollection(db: Db): Collection<WorkflowDefinition> {
  return db.collection<WorkflowDefinition>(COLLECTION);
}

export async function ensureWorkflowIndexes(db: Db): Promise<void> {
  const col = getWorkflowCollection(db);
  await col.createIndex({ id: 1 }, { unique: true });
  await col.createIndex({ name: 1 });
}

export function toDTO(doc: WorkflowDefinition & { _id?: unknown }): WorkflowDefinitionDTO {
  const { _id, ...rest } = doc;
  return rest;
}
