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
import { Body, Controller, Delete, Get, Header, Path, Post, Put, Query, Route, Response, SuccessResponse, Tags, } from "tsoa";
import pino from "pino";
import { randomUUID } from "node:crypto";
import { getDb } from "../db.js";
import { getConnectorCollection, toDTO, } from "../models/connector-definition.js";
import { getWorkflowCollection } from "../models/workflow-definition.js";
import { mapConnectorToMainnet } from "../dto-mappers.js";
import { uploadConnectors } from "../mainnet-client.js";
const logger = pino({ name: "workflow:connector-controller" });
let ConnectorController = class ConnectorController extends Controller {
    /**
     * Create a new connector definition.
     * @summary Create connector
     */
    async createConnector(body) {
        const db = getDb();
        const col = getConnectorCollection(db);
        const now = new Date().toISOString();
        const doc = {
            id: randomUUID(),
            name: body.name,
            description: body.description,
            type: body.type,
            baseUrl: body.baseUrl,
            authConfig: body.authConfig,
            endpoints: body.endpoints,
            mcpConfig: body.mcpConfig,
            createdAt: now,
            updatedAt: now,
        };
        await col.insertOne(doc);
        logger.info({ id: doc.id, name: doc.name, type: doc.type }, "Connector created");
        this.setStatus(201);
        return toDTO(doc);
    }
    /**
     * List all connector definitions with pagination.
     * @summary List connectors
     */
    async listConnectors(page = 1, pageSize = 20) {
        pageSize = Math.min(pageSize, 100);
        const db = getDb();
        const col = getConnectorCollection(db);
        const skip = (page - 1) * pageSize;
        const [connectors, total] = await Promise.all([
            col.find().skip(skip).limit(pageSize).toArray(),
            col.countDocuments(),
        ]);
        return {
            connectors: connectors.map(toDTO),
            total,
            page,
            pageSize,
        };
    }
    /**
     * Get a single connector by ID.
     * @summary Get connector
     */
    async getConnector(connectorId) {
        const db = getDb();
        const col = getConnectorCollection(db);
        const doc = await col.findOne({ id: connectorId });
        if (!doc) {
            this.setStatus(404);
            return { error: "Connector not found" };
        }
        return toDTO(doc);
    }
    /**
     * Update a connector definition.
     * @summary Update connector
     */
    async updateConnector(connectorId, body) {
        const db = getDb();
        const col = getConnectorCollection(db);
        const existing = await col.findOne({ id: connectorId });
        if (!existing) {
            this.setStatus(404);
            return { error: "Connector not found" };
        }
        const updateFields = {
            updatedAt: new Date().toISOString(),
        };
        if (body.name !== undefined)
            updateFields.name = body.name;
        if (body.description !== undefined)
            updateFields.description = body.description;
        if (body.type !== undefined)
            updateFields.type = body.type;
        if (body.baseUrl !== undefined)
            updateFields.baseUrl = body.baseUrl;
        if (body.authConfig !== undefined)
            updateFields.authConfig = body.authConfig;
        if (body.endpoints !== undefined)
            updateFields.endpoints = body.endpoints;
        if (body.mcpConfig !== undefined)
            updateFields.mcpConfig = body.mcpConfig;
        await col.updateOne({ id: connectorId }, { $set: updateFields });
        // Cascade: mark workflows referencing this connector as out_of_sync
        const connectorName = body.name ?? existing.name;
        const workflowCol = getWorkflowCollection(db);
        const cascadeResult = await workflowCol.updateMany({
            "steps.connectorRef": existing.name,
            "deploymentState.status": { $in: ["compiled", "deployed"] },
        }, { $set: { "deploymentState.status": "out_of_sync" } });
        if (cascadeResult.modifiedCount > 0) {
            logger.info({ connectorId, connectorName: existing.name, affectedWorkflows: cascadeResult.modifiedCount }, "Cascaded out_of_sync to workflows referencing updated connector");
        }
        const updated = await col.findOne({ id: connectorId });
        logger.info({ id: connectorId }, "Connector updated");
        return toDTO(updated);
    }
    /**
     * Delete a connector definition.
     * @summary Delete connector
     */
    async deleteConnector(connectorId) {
        const db = getDb();
        const col = getConnectorCollection(db);
        const existing = await col.findOne({ id: connectorId });
        if (!existing) {
            this.setStatus(404);
            return;
        }
        await col.deleteOne({ id: connectorId });
        // Cascade: mark workflows referencing this connector as out_of_sync
        const workflowCol = getWorkflowCollection(db);
        const cascadeResult = await workflowCol.updateMany({
            "steps.connectorRef": existing.name,
            "deploymentState.status": { $in: ["compiled", "deployed"] },
        }, { $set: { "deploymentState.status": "out_of_sync" } });
        if (cascadeResult.modifiedCount > 0) {
            logger.info({ connectorName: existing.name, affectedWorkflows: cascadeResult.modifiedCount }, "Cascaded out_of_sync to workflows referencing deleted connector");
        }
        logger.info({ id: connectorId, name: existing.name }, "Connector deleted");
        this.setStatus(204);
    }
    /**
     * Compile a single connector to mainnet ConnectorDefinitionDto format.
     * @summary Compile connector
     */
    async compileConnector(connectorId) {
        const db = getDb();
        const col = getConnectorCollection(db);
        const doc = await col.findOne({ id: connectorId });
        if (!doc) {
            this.setStatus(404);
            return { error: "Connector not found" };
        }
        const compiled = mapConnectorToMainnet(doc);
        logger.info({ connectorId, name: doc.name }, "Connector compiled to mainnet format");
        return compiled;
    }
    /**
     * Sync ALL connectors to Aevatar mainnet. Loads all connectors from DB,
     * maps to mainnet format, and PUTs the full catalog.
     * @summary Sync connectors to Aevatar
     */
    async syncConnectors(authorization) {
        const db = getDb();
        const col = getConnectorCollection(db);
        const allConnectors = await col.find().toArray();
        const mainnetDtos = allConnectors.map(mapConnectorToMainnet);
        logger.info({ count: mainnetDtos.length }, "Syncing all connectors to Aevatar mainnet");
        try {
            await uploadConnectors(mainnetDtos, authorization);
            return { success: true, count: mainnetDtos.length };
        }
        catch (err) {
            const message = err instanceof Error ? err.message : "Unknown error";
            logger.error({ err: message }, "Connector sync failed");
            this.setStatus(502);
            return { error: `Sync failed: ${message}` };
        }
    }
};
__decorate([
    Post(),
    SuccessResponse(201, "Connector created"),
    __param(0, Body()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [Object]),
    __metadata("design:returntype", Promise)
], ConnectorController.prototype, "createConnector", null);
__decorate([
    Get(),
    __param(0, Query()),
    __param(1, Query()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [Number, Number]),
    __metadata("design:returntype", Promise)
], ConnectorController.prototype, "listConnectors", null);
__decorate([
    Get("{connectorId}"),
    Response(404, "Connector not found"),
    __param(0, Path()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String]),
    __metadata("design:returntype", Promise)
], ConnectorController.prototype, "getConnector", null);
__decorate([
    Put("{connectorId}"),
    Response(404, "Connector not found"),
    __param(0, Path()),
    __param(1, Body()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String, Object]),
    __metadata("design:returntype", Promise)
], ConnectorController.prototype, "updateConnector", null);
__decorate([
    Delete("{connectorId}"),
    SuccessResponse(204, "Connector deleted"),
    Response(404, "Connector not found"),
    __param(0, Path()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String]),
    __metadata("design:returntype", Promise)
], ConnectorController.prototype, "deleteConnector", null);
__decorate([
    Get("{connectorId}/compile"),
    Response(404, "Connector not found"),
    __param(0, Path()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String]),
    __metadata("design:returntype", Promise)
], ConnectorController.prototype, "compileConnector", null);
__decorate([
    Post("sync"),
    Response(502, "Sync failed"),
    __param(0, Header("Authorization")),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String]),
    __metadata("design:returntype", Promise)
], ConnectorController.prototype, "syncConnectors", null);
ConnectorController = __decorate([
    Route("connectors"),
    Tags("Connectors")
], ConnectorController);
export { ConnectorController };
//# sourceMappingURL=ConnectorController.js.map