var __decorate = (this && this.__decorate) || function (decorators, target, key, desc) {
    var c = arguments.length, r = c < 3 ? target : desc === null ? desc = Object.getOwnPropertyDescriptor(target, key) : desc, d;
    if (typeof Reflect === "object" && typeof Reflect.decorate === "function") r = Reflect.decorate(decorators, target, key, desc);
    else for (var i = decorators.length - 1; i >= 0; i--) if (d = decorators[i]) r = (c < 3 ? d(r) : c > 3 ? d(target, key, r) : d(target, key)) || r;
    return c > 3 && r && Object.defineProperty(target, key, r), r;
};
var __metadata = (this && this.__metadata) || function (k, v) {
    if (typeof Reflect === "object" && typeof Reflect.metadata === "function") return Reflect.metadata(k, v);
};
var __param = (this && this.__param) || function (paramIndex, decorator) {
    return function (target, key) { decorator(target, key, paramIndex); }
};
import { Body, Controller, Delete, Get, Path, Post, Put, Query, Route, Response, SuccessResponse, Tags, } from "tsoa";
import pino from "pino";
import { randomUUID } from "node:crypto";
import { getDb } from "../db.js";
import { getWorkflowCollection, toDTO, } from "../models/workflow-definition.js";
import { getCompiledArtifactCollection } from "../models/compiled-artifact.js";
const logger = pino({ name: "workflow:workflow-controller" });
let WorkflowController = class WorkflowController extends Controller {
    /**
     * Create a new workflow definition.
     * @summary Create workflow
     */
    async createWorkflow(body) {
        const db = getDb();
        const col = getWorkflowCollection(db);
        const now = new Date().toISOString();
        const doc = {
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
    async listWorkflows(page = 1, pageSize = 20) {
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
    async getWorkflow(workflowId) {
        const db = getDb();
        const col = getWorkflowCollection(db);
        const doc = await col.findOne({ id: workflowId });
        if (!doc) {
            this.setStatus(404);
            return { error: "Workflow not found" };
        }
        return toDTO(doc);
    }
    /**
     * Update a workflow definition (roles, steps, parameters).
     * @summary Update workflow
     */
    async updateWorkflow(workflowId, body) {
        const db = getDb();
        const col = getWorkflowCollection(db);
        const existing = await col.findOne({ id: workflowId });
        if (!existing) {
            this.setStatus(404);
            return { error: "Workflow not found" };
        }
        const updateFields = {
            updatedAt: new Date().toISOString(),
        };
        if (body.name !== undefined)
            updateFields.name = body.name;
        if (body.description !== undefined)
            updateFields.description = body.description;
        if (body.roles !== undefined)
            updateFields.roles = body.roles;
        if (body.steps !== undefined)
            updateFields.steps = body.steps;
        if (body.parameters !== undefined)
            updateFields.parameters = body.parameters;
        if (body.yaml !== undefined)
            updateFields.yaml = body.yaml;
        // Mark out-of-sync if previously compiled or deployed
        const currentStatus = existing.deploymentState?.status;
        if (currentStatus === "compiled" || currentStatus === "deployed") {
            updateFields["deploymentState.status"] = "out_of_sync";
            updateFields["deploymentState.deployError"] = undefined;
        }
        await col.updateOne({ id: workflowId }, { $set: updateFields });
        const updated = await col.findOne({ id: workflowId });
        logger.info({ id: workflowId, syncStatus: updated?.deploymentState?.status }, "Workflow updated");
        return toDTO(updated);
    }
    /**
     * Delete a workflow and its associated roles/steps.
     * @summary Delete workflow
     */
    async deleteWorkflow(workflowId) {
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
    async getDeploymentStatus(workflowId) {
        const db = getDb();
        const col = getWorkflowCollection(db);
        const doc = await col.findOne({ id: workflowId });
        if (!doc) {
            this.setStatus(404);
            return { error: "Workflow not found" };
        }
        const deploymentState = doc.deploymentState ?? { status: "draft" };
        // Fetch last compiled artifact if exists
        const artifactCol = getCompiledArtifactCollection(db);
        const artifact = await artifactCol.findOne({ workflowId });
        const lastArtifact = artifact ? (() => { const { _id, ...rest } = artifact; return rest; })() : undefined;
        return { deploymentState, lastArtifact };
    }
};
__decorate([
    Post(),
    SuccessResponse(201, "Workflow created"),
    Response(400, "Bad request"),
    __param(0, Body()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [Object]),
    __metadata("design:returntype", Promise)
], WorkflowController.prototype, "createWorkflow", null);
__decorate([
    Get(),
    __param(0, Query()),
    __param(1, Query()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [Number, Number]),
    __metadata("design:returntype", Promise)
], WorkflowController.prototype, "listWorkflows", null);
__decorate([
    Get("{workflowId}"),
    Response(404, "Workflow not found"),
    __param(0, Path()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String]),
    __metadata("design:returntype", Promise)
], WorkflowController.prototype, "getWorkflow", null);
__decorate([
    Put("{workflowId}"),
    Response(404, "Workflow not found"),
    __param(0, Path()),
    __param(1, Body()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String, Object]),
    __metadata("design:returntype", Promise)
], WorkflowController.prototype, "updateWorkflow", null);
__decorate([
    Delete("{workflowId}"),
    SuccessResponse(204, "Workflow deleted"),
    Response(404, "Workflow not found"),
    __param(0, Path()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String]),
    __metadata("design:returntype", Promise)
], WorkflowController.prototype, "deleteWorkflow", null);
__decorate([
    Get("{workflowId}/deployment-status"),
    Response(404, "Workflow not found"),
    __param(0, Path()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String]),
    __metadata("design:returntype", Promise)
], WorkflowController.prototype, "getDeploymentStatus", null);
WorkflowController = __decorate([
    Route("workflows"),
    Tags("Workflows")
], WorkflowController);
export { WorkflowController };
//# sourceMappingURL=WorkflowController.js.map