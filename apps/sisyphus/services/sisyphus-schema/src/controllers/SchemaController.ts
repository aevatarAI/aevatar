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
  getSchemaCollection,
  toDTO,
  type SchemaDefinition,
  type SchemaDefinitionDTO,
} from "../models/schema-definition.js";
import { createValidator, type ValidationResult } from "../validator.js";

const logger = pino({ name: "schema-validator:schema-controller" });

interface CreateSchemaRequest {
  name: string;
  description: string;
  entityType: "node" | "edge";
  nodeType: string;
  applicableTypes: string[];
  jsonSchema: Record<string, unknown>;
}

interface UpdateSchemaRequest {
  name?: string;
  description?: string;
  entityType?: "node" | "edge";
  nodeType?: string;
  applicableTypes?: string[];
  jsonSchema?: Record<string, unknown>;
}

interface SchemaListResponse {
  schemas: SchemaDefinitionDTO[];
  total: number;
  page: number;
  pageSize: number;
}

interface ErrorResponse {
  error: string;
}

interface ValidateByNameRequest {
  data: unknown;
}

const validator = createValidator();

@Route("schemas")
@Tags("Schemas")
export class SchemaController extends Controller {
  /**
   * Create a new schema definition.
   * @summary Create schema
   */
  @Post()
  @SuccessResponse(201, "Schema created")
  @Response<ErrorResponse>(400, "Bad request")
  @Response<ErrorResponse>(409, "Schema with this name already exists")
  public async createSchema(
    @Body() body: CreateSchemaRequest
  ): Promise<SchemaDefinitionDTO> {
    const db = getDb();
    const col = getSchemaCollection(db);

    const existing = await col.findOne({ name: body.name });
    if (existing) {
      this.setStatus(409);
      return { error: "Schema with this name already exists" } as unknown as SchemaDefinitionDTO;
    }

    const now = new Date().toISOString();
    const doc: SchemaDefinition = {
      id: randomUUID(),
      name: body.name,
      description: body.description,
      entityType: body.entityType,
      nodeType: body.nodeType,
      applicableTypes: body.applicableTypes,
      jsonSchema: body.jsonSchema,
      createdAt: now,
      updatedAt: now,
    };

    await col.insertOne(doc);
    logger.info({ id: doc.id, name: doc.name }, "Schema created");

    this.setStatus(201);
    return toDTO(doc);
  }

  /**
   * List all schema definitions with pagination.
   * @summary List schemas
   */
  @Get()
  public async listSchemas(
    @Query() page: number = 1,
    @Query() pageSize: number = 20
  ): Promise<SchemaListResponse> {
    pageSize = Math.min(pageSize, 100);
    const db = getDb();
    const col = getSchemaCollection(db);

    const skip = (page - 1) * pageSize;
    const [schemas, total] = await Promise.all([
      col.find().skip(skip).limit(pageSize).toArray(),
      col.countDocuments(),
    ]);

    return {
      schemas: schemas.map(toDTO),
      total,
      page,
      pageSize,
    };
  }

  /**
   * Get a single schema by ID.
   * @summary Get schema
   */
  @Get("{schemaId}")
  @Response<ErrorResponse>(404, "Schema not found")
  public async getSchema(
    @Path() schemaId: string
  ): Promise<SchemaDefinitionDTO> {
    const db = getDb();
    const col = getSchemaCollection(db);

    const doc = await col.findOne({ id: schemaId });
    if (!doc) {
      this.setStatus(404);
      return { error: "Schema not found" } as unknown as SchemaDefinitionDTO;
    }

    return toDTO(doc);
  }

  /**
   * Update an existing schema. Invalidates the LRU cache for the old schema.
   * @summary Update schema
   */
  @Put("{schemaId}")
  @Response<ErrorResponse>(404, "Schema not found")
  public async updateSchema(
    @Path() schemaId: string,
    @Body() body: UpdateSchemaRequest
  ): Promise<SchemaDefinitionDTO> {
    const db = getDb();
    const col = getSchemaCollection(db);

    const existing = await col.findOne({ id: schemaId });
    if (!existing) {
      this.setStatus(404);
      return { error: "Schema not found" } as unknown as SchemaDefinitionDTO;
    }

    // Invalidate cache for old schema content
    if (body.jsonSchema || existing.jsonSchema) {
      validator.invalidate(existing.jsonSchema);
    }

    const updateFields: Record<string, unknown> = {
      updatedAt: new Date().toISOString(),
    };
    if (body.name !== undefined) updateFields.name = body.name;
    if (body.description !== undefined) updateFields.description = body.description;
    if (body.entityType !== undefined) updateFields.entityType = body.entityType;
    if (body.nodeType !== undefined) updateFields.nodeType = body.nodeType;
    if (body.applicableTypes !== undefined) updateFields.applicableTypes = body.applicableTypes;
    if (body.jsonSchema !== undefined) updateFields.jsonSchema = body.jsonSchema;

    await col.updateOne({ id: schemaId }, { $set: updateFields });

    const updated = await col.findOne({ id: schemaId });
    logger.info({ id: schemaId }, "Schema updated");

    return toDTO(updated!);
  }

  /**
   * Delete a schema. Invalidates the LRU cache for the deleted schema.
   * @summary Delete schema
   */
  @Delete("{schemaId}")
  @SuccessResponse(204, "Schema deleted")
  @Response<ErrorResponse>(404, "Schema not found")
  public async deleteSchema(
    @Path() schemaId: string
  ): Promise<void> {
    const db = getDb();
    const col = getSchemaCollection(db);

    const existing = await col.findOne({ id: schemaId });
    if (!existing) {
      this.setStatus(404);
      return;
    }

    // Invalidate cache for deleted schema
    validator.invalidate(existing.jsonSchema);

    await col.deleteOne({ id: schemaId });
    logger.info({ id: schemaId, name: existing.name }, "Schema deleted");

    this.setStatus(204);
  }
}

/**
 * Validates data against a named/stored schema without needing to pass the full schema.
 */
@Route("validate")
@Tags("Validation")
export class ValidateByNameController extends Controller {
  /**
   * Validate data against a stored schema by name.
   * @summary Validate against named schema
   */
  @Post("{schemaName}")
  @Response<ErrorResponse>(404, "Schema not found")
  public async validateByName(
    @Path() schemaName: string,
    @Body() body: ValidateByNameRequest
  ): Promise<ValidationResult> {
    const db = getDb();
    const col = getSchemaCollection(db);

    const schemaDef = await col.findOne({ name: schemaName });
    if (!schemaDef) {
      this.setStatus(404);
      return { valid: false, errors: [`Schema '${schemaName}' not found`] };
    }

    const result = validator.validate(schemaDef.jsonSchema, body.data);
    logger.info(
      { schemaName, valid: result.valid },
      "Validated against named schema"
    );

    return result;
  }
}
