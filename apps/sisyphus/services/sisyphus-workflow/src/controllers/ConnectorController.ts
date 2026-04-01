import {
  Body,
  Controller,
  Delete,
  Get,
  Header,
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
  getConnectorCollection,
  toDTO,
  type ConnectorDefinition,
  type ConnectorDefinitionDTO,
  type HttpEndpoint,
  type AuthConfig,
} from "../models/connector-definition.js";
import { getWorkflowCollection } from "../models/workflow-definition.js";
import { mapConnectorToMainnet } from "../dto-mappers.js";
import { uploadConnectors } from "../mainnet-client.js";
import type { ErrorResponse, MainnetConnectorDto } from "../types.js";

const logger = pino({ name: "workflow:connector-controller" });

interface CreateConnectorRequest {
  name: string;
  description: string;
  type: "http" | "mcp";
  baseUrl?: string;
  authConfig?: AuthConfig;
  endpoints?: HttpEndpoint[];
  mcpConfig?: Record<string, unknown>;
}

interface UpdateConnectorRequest {
  name?: string;
  description?: string;
  type?: "http" | "mcp";
  baseUrl?: string;
  authConfig?: AuthConfig;
  endpoints?: HttpEndpoint[];
  mcpConfig?: Record<string, unknown>;
}

interface ConnectorListResponse {
  connectors: ConnectorDefinitionDTO[];
  total: number;
  page: number;
  pageSize: number;
}

@Route("connectors")
@Tags("Connectors")
export class ConnectorController extends Controller {
  /**
   * Create a new connector definition.
   * @summary Create connector
   */
  @Post()
  @SuccessResponse(201, "Connector created")
  public async createConnector(
    @Body() body: CreateConnectorRequest
  ): Promise<ConnectorDefinitionDTO> {
    const db = getDb();
    const col = getConnectorCollection(db);

    const now = new Date().toISOString();
    const doc: ConnectorDefinition = {
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
  @Get()
  public async listConnectors(
    @Query() page: number = 1,
    @Query() pageSize: number = 20
  ): Promise<ConnectorListResponse> {
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
  @Get("{connectorId}")
  @Response<ErrorResponse>(404, "Connector not found")
  public async getConnector(
    @Path() connectorId: string
  ): Promise<ConnectorDefinitionDTO> {
    const db = getDb();
    const col = getConnectorCollection(db);

    const doc = await col.findOne({ id: connectorId });
    if (!doc) {
      this.setStatus(404);
      return { error: "Connector not found" } as unknown as ConnectorDefinitionDTO;
    }

    return toDTO(doc);
  }

  /**
   * Update a connector definition.
   * @summary Update connector
   */
  @Put("{connectorId}")
  @Response<ErrorResponse>(404, "Connector not found")
  public async updateConnector(
    @Path() connectorId: string,
    @Body() body: UpdateConnectorRequest
  ): Promise<ConnectorDefinitionDTO> {
    const db = getDb();
    const col = getConnectorCollection(db);

    const existing = await col.findOne({ id: connectorId });
    if (!existing) {
      this.setStatus(404);
      return { error: "Connector not found" } as unknown as ConnectorDefinitionDTO;
    }

    const updateFields: Record<string, unknown> = {
      updatedAt: new Date().toISOString(),
    };
    if (body.name !== undefined) updateFields.name = body.name;
    if (body.description !== undefined) updateFields.description = body.description;
    if (body.type !== undefined) updateFields.type = body.type;
    if (body.baseUrl !== undefined) updateFields.baseUrl = body.baseUrl;
    if (body.authConfig !== undefined) updateFields.authConfig = body.authConfig;
    if (body.endpoints !== undefined) updateFields.endpoints = body.endpoints;
    if (body.mcpConfig !== undefined) updateFields.mcpConfig = body.mcpConfig;

    await col.updateOne({ id: connectorId }, { $set: updateFields });

    // Cascade: mark workflows referencing this connector as out_of_sync
    const connectorName = body.name ?? existing.name;
    const workflowCol = getWorkflowCollection(db);
    const cascadeResult = await workflowCol.updateMany(
      {
        "steps.connectorRef": existing.name,
        "deploymentState.status": { $in: ["compiled", "deployed"] },
      },
      { $set: { "deploymentState.status": "out_of_sync" } },
    );
    if (cascadeResult.modifiedCount > 0) {
      logger.info(
        { connectorId, connectorName: existing.name, affectedWorkflows: cascadeResult.modifiedCount },
        "Cascaded out_of_sync to workflows referencing updated connector",
      );
    }

    const updated = await col.findOne({ id: connectorId });
    logger.info({ id: connectorId }, "Connector updated");

    return toDTO(updated!);
  }

  /**
   * Delete a connector definition.
   * @summary Delete connector
   */
  @Delete("{connectorId}")
  @SuccessResponse(204, "Connector deleted")
  @Response<ErrorResponse>(404, "Connector not found")
  public async deleteConnector(
    @Path() connectorId: string
  ): Promise<void> {
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
    const cascadeResult = await workflowCol.updateMany(
      {
        "steps.connectorRef": existing.name,
        "deploymentState.status": { $in: ["compiled", "deployed"] },
      },
      { $set: { "deploymentState.status": "out_of_sync" } },
    );
    if (cascadeResult.modifiedCount > 0) {
      logger.info(
        { connectorName: existing.name, affectedWorkflows: cascadeResult.modifiedCount },
        "Cascaded out_of_sync to workflows referencing deleted connector",
      );
    }

    logger.info({ id: connectorId, name: existing.name }, "Connector deleted");

    this.setStatus(204);
  }

  /**
   * Compile a single connector to mainnet ConnectorDefinitionDto format.
   * @summary Compile connector
   */
  @Get("{connectorId}/compile")
  @Response<ErrorResponse>(404, "Connector not found")
  public async compileConnector(
    @Path() connectorId: string
  ): Promise<MainnetConnectorDto> {
    const db = getDb();
    const col = getConnectorCollection(db);

    const doc = await col.findOne({ id: connectorId });
    if (!doc) {
      this.setStatus(404);
      return { error: "Connector not found" } as unknown as MainnetConnectorDto;
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
  @Post("sync")
  @Response<ErrorResponse>(502, "Sync failed")
  public async syncConnectors(
    @Header("Authorization") authorization?: string
  ): Promise<{ success: boolean; count: number }> {
    const db = getDb();
    const col = getConnectorCollection(db);

    const allConnectors = await col.find().toArray();
    const mainnetDtos = allConnectors.map(mapConnectorToMainnet);

    logger.info({ count: mainnetDtos.length }, "Syncing all connectors to Aevatar mainnet");

    try {
      await uploadConnectors(mainnetDtos, authorization);
      return { success: true, count: mainnetDtos.length };
    } catch (err) {
      const message = err instanceof Error ? err.message : "Unknown error";
      logger.error({ err: message }, "Connector sync failed");
      this.setStatus(502);
      return { error: `Sync failed: ${message}` } as unknown as { success: boolean; count: number };
    }
  }
}
