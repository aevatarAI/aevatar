import { Controller } from "tsoa";
import { type WorkflowDefinitionDTO, type RoleDefinition, type StepDefinition } from "../models/workflow-definition.js";
import type { DeploymentState } from "../types.js";
interface CreateWorkflowRequest {
    name: string;
    description: string;
    yaml?: string;
    roles: RoleDefinition[];
    steps: StepDefinition[];
    parameters?: Record<string, unknown>;
}
interface UpdateWorkflowRequest {
    name?: string;
    description?: string;
    yaml?: string;
    roles?: RoleDefinition[];
    steps?: StepDefinition[];
    parameters?: Record<string, unknown>;
}
interface WorkflowListResponse {
    workflows: WorkflowDefinitionDTO[];
    total: number;
    page: number;
    pageSize: number;
}
export declare class WorkflowController extends Controller {
    /**
     * Create a new workflow definition.
     * @summary Create workflow
     */
    createWorkflow(body: CreateWorkflowRequest): Promise<WorkflowDefinitionDTO>;
    /**
     * List all workflow definitions with pagination.
     * @summary List workflows
     */
    listWorkflows(page?: number, pageSize?: number): Promise<WorkflowListResponse>;
    /**
     * Get a single workflow by ID, including roles and steps.
     * @summary Get workflow
     */
    getWorkflow(workflowId: string): Promise<WorkflowDefinitionDTO>;
    /**
     * Update a workflow definition (roles, steps, parameters).
     * @summary Update workflow
     */
    updateWorkflow(workflowId: string, body: UpdateWorkflowRequest): Promise<WorkflowDefinitionDTO>;
    /**
     * Delete a workflow and its associated roles/steps.
     * @summary Delete workflow
     */
    deleteWorkflow(workflowId: string): Promise<void>;
    /**
     * Get deployment status and last compiled artifact for a workflow.
     * @summary Get deployment status
     */
    getDeploymentStatus(workflowId: string): Promise<{
        deploymentState: DeploymentState;
        lastArtifact?: Record<string, unknown>;
    }>;
}
export {};
//# sourceMappingURL=WorkflowController.d.ts.map