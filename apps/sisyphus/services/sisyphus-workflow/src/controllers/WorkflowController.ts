import {
  Body,
  Controller,
  Delete,
  Get,
  Path,
  Post,
  Put,
  Query,
  Route,
  Response,
  SuccessResponse,
  Tags,
} from "tsoa";
import pino from "pino";
import { randomUUID } from "node:crypto";
import { getDb } from "../db.js";
import {
  getWorkflowCollection,
  toDTO,
  type WorkflowDefinition,
  type WorkflowDefinitionDTO,
  type RoleDefinition,
  type StepDefinition,
} from "../models/workflow-definition.js";
import { getCompiledArtifactCollection } from "../models/compiled-artifact.js";
import type { ErrorResponse, DeploymentState } from "../types.js";

const logger = pino({ name: "workflow:workflow-controller" });

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

@Route("workflows")
@Tags("Workflows")
export class WorkflowController extends Controller {
  /**
   * Create a new workflow definition.
   * @summary Create workflow
   */
  @Post()
  @SuccessResponse(201, "Workflow created")
  @Response<ErrorResponse>(400, "Bad request")
  public async createWorkflow(
    @Body() body: CreateWorkflowRequest
  ): Promise<WorkflowDefinitionDTO> {
    const db = getDb();
    const col = getWorkflowCollection(db);

    const now = new Date().toISOString();
    const doc: WorkflowDefinition = {
      id: randomUUID(),
      name: body.name,
      description: body.description,
      yaml: body.yaml,
      roles: body.roles,
      steps: body.steps,
      parameters: body.parameters,
      createdAt: now,
      updatedAt: now,
    };

    await col.insertOne(doc);
    logger.info({ id: doc.id, name: doc.name }, "Workflow created");

    this.setStatus(201);
    return toDTO(doc);
  }

  /**
   * List all workflow definitions with pagination.
   * @summary List workflows
   */
  @Get()
  public async listWorkflows(
    @Query() page: number = 1,
    @Query() pageSize: number = 20
  ): Promise<WorkflowListResponse> {
    pageSize = Math.min(pageSize, 100);
    const db = getDb();
    const col = getWorkflowCollection(db);

    const skip = (page - 1) * pageSize;
    const [workflows, total] = await Promise.all([
      col.find().skip(skip).limit(pageSize).toArray(),
      col.countDocuments(),
    ]);

    return {
      workflows: workflows.map(toDTO),
      total,
      page,
      pageSize,
    };
  }

  /**
   * Get a single workflow by ID, including roles and steps.
   * @summary Get workflow
   */
  @Get("{workflowId}")
  @Response<ErrorResponse>(404, "Workflow not found")
  public async getWorkflow(
    @Path() workflowId: string
  ): Promise<WorkflowDefinitionDTO> {
    const db = getDb();
    const col = getWorkflowCollection(db);

    const doc = await col.findOne({ id: workflowId });
    if (!doc) {
      this.setStatus(404);
      return { error: "Workflow not found" } as unknown as WorkflowDefinitionDTO;
    }

    return toDTO(doc);
  }

  /**
   * Update a workflow definition (roles, steps, parameters).
   * @summary Update workflow
   */
  @Put("{workflowId}")
  @Response<ErrorResponse>(404, "Workflow not found")
  public async updateWorkflow(
    @Path() workflowId: string,
    @Body() body: UpdateWorkflowRequest
  ): Promise<WorkflowDefinitionDTO> {
    const db = getDb();
    const col = getWorkflowCollection(db);

    const existing = await col.findOne({ id: workflowId });
    if (!existing) {
      this.setStatus(404);
      return { error: "Workflow not found" } as unknown as WorkflowDefinitionDTO;
    }

    const updateFields: Record<string, unknown> = {
      updatedAt: new Date().toISOString(),
    };
    if (body.name !== undefined) updateFields.name = body.name;
    if (body.description !== undefined) updateFields.description = body.description;
    if (body.roles !== undefined) updateFields.roles = body.roles;
    if (body.steps !== undefined) updateFields.steps = body.steps;
    if (body.parameters !== undefined) updateFields.parameters = body.parameters;
    if (body.yaml !== undefined) updateFields.yaml = body.yaml;

    // Mark out-of-sync if previously compiled or deployed
    const currentStatus = existing.deploymentState?.status;
    if (currentStatus === "compiled" || currentStatus === "deployed") {
      updateFields["deploymentState.status"] = "out_of_sync";
      updateFields["deploymentState.deployError"] = undefined;
    }

    await col.updateOne({ id: workflowId }, { $set: updateFields });

    const updated = await col.findOne({ id: workflowId });
    logger.info({ id: workflowId, syncStatus: updated?.deploymentState?.status }, "Workflow updated");

    return toDTO(updated!);
  }

  /**
   * Delete a workflow and its associated roles/steps.
   * @summary Delete workflow
   */
  @Delete("{workflowId}")
  @SuccessResponse(204, "Workflow deleted")
  @Response<ErrorResponse>(404, "Workflow not found")
  public async deleteWorkflow(
    @Path() workflowId: string
  ): Promise<void> {
    const db = getDb();
    const col = getWorkflowCollection(db);

    const existing = await col.findOne({ id: workflowId });
    if (!existing) {
      this.setStatus(404);
      return;
    }

    await col.deleteOne({ id: workflowId });
    logger.info({ id: workflowId, name: existing.name }, "Workflow deleted");

    this.setStatus(204);
  }

  /**
   * Get deployment status and last compiled artifact for a workflow.
   * @summary Get deployment status
   */
  @Get("{workflowId}/deployment-status")
  @Response<ErrorResponse>(404, "Workflow not found")
  public async getDeploymentStatus(
    @Path() workflowId: string
  ): Promise<{ deploymentState: DeploymentState; lastArtifact?: Record<string, unknown> }> {
    const db = getDb();
    const col = getWorkflowCollection(db);

    const doc = await col.findOne({ id: workflowId });
    if (!doc) {
      this.setStatus(404);
      return { error: "Workflow not found" } as unknown as { deploymentState: DeploymentState };
    }

    const deploymentState: DeploymentState = doc.deploymentState ?? { status: "draft" };

    // Fetch last compiled artifact if exists
    const artifactCol = getCompiledArtifactCollection(db);
    const artifact = await artifactCol.findOne({ workflowId });
    const lastArtifact = artifact ? (() => { const { _id, ...rest } = artifact; return rest; })() : undefined;

    return { deploymentState, lastArtifact };
  }
}
