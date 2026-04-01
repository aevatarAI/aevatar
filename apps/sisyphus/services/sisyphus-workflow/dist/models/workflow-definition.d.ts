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
export declare function getWorkflowCollection(db: Db): Collection<WorkflowDefinition>;
export declare function ensureWorkflowIndexes(db: Db): Promise<void>;
export declare function toDTO(doc: WorkflowDefinition & {
    _id?: unknown;
}): WorkflowDefinitionDTO;
//# sourceMappingURL=workflow-definition.d.ts.map