import {
  Body,
  Controller,
  Get,
  Path,
  Post,
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
  getUploadHistoryCollection,
  type UploadHistory,
  type UploadHistoryDTO,
} from "../models/upload-history.js";
import {
  writeNodes,
  writeEdges,
  type GraphNode,
  type GraphEdge,
} from "../chrono-graph-client.js";

const logger = pino({ name: "ingestor:ingest-controller" });

interface IngestRequest {
  nodes: GraphNode[];
  edges?: GraphEdge[];
}

interface IngestResponse {
  uploadId: string;
  nodeIds: string[];
  edgeIds: string[];
}

interface HistoryListResponse {
  records: UploadHistoryDTO[];
  total: number;
  page: number;
  pageSize: number;
}

interface ErrorResponse {
  error: string;
}

@Route("ingest")
@Tags("Ingestion")
export class IngestController extends Controller {
  /**
   * Ingest knowledge content — nodes and edges are written to chrono-graph as raw (red) entries.
   * Each node gets `sisyphus_status = "raw"` and a timestamp automatically.
   * @summary Ingest knowledge content
   */
  @Post()
  @SuccessResponse(200, "Ingested successfully")
  @Response<ErrorResponse>(400, "Bad request")
  @Response<ErrorResponse>(500, "Ingestion failed")
  public async ingest(
    @Body() body: IngestRequest
  ): Promise<IngestResponse> {
    if (!body.nodes || !Array.isArray(body.nodes) || body.nodes.length === 0) {
      this.setStatus(400);
      return { error: "Request must include a non-empty 'nodes' array" } as unknown as IngestResponse;
    }

    const uploadId = randomUUID();
    const timestamp = new Date().toISOString();
    const edges = Array.isArray(body.edges) ? body.edges : [];

    logger.info(
      { uploadId, nodeCount: body.nodes.length, edgeCount: edges.length },
      "Starting ingestion"
    );

    // Add sisyphus_status and timestamp to each node
    const enrichedNodes = body.nodes.map((node) => ({
      ...node,
      sisyphus_status: "raw",
      timestamp,
    }));

    // Add timestamp to each edge
    const enrichedEdges = edges.map((edge) => ({
      ...edge,
      timestamp,
    }));

    let nodeIds: string[] = [];
    let edgeIds: string[] = [];
    let status: "success" | "partial" | "failed" = "success";
    let error: string | undefined;

    try {
      const nodeResult = await writeNodes(enrichedNodes);
      nodeIds = nodeResult.nodeIds;

      if (enrichedEdges.length > 0) {
        const edgeResult = await writeEdges(enrichedEdges);
        edgeIds = edgeResult.edgeIds;
      }

      logger.info(
        { uploadId, nodeIds: nodeIds.length, edgeIds: edgeIds.length },
        "Ingestion completed successfully"
      );
    } catch (err) {
      error = err instanceof Error ? err.message : "Unknown error";
      status = nodeIds.length > 0 ? "partial" : "failed";
      logger.error(
        { uploadId, err: error, nodeIds: nodeIds.length, edgeIds: edgeIds.length },
        "Ingestion failed"
      );

      if (status === "failed") {
        // Record history even on failure
        await this.recordHistory(uploadId, timestamp, body.nodes.length, edges.length, nodeIds, edgeIds, status, error);
        this.setStatus(500);
        return { error: `Ingestion failed: ${error}` } as unknown as IngestResponse;
      }
    }

    await this.recordHistory(uploadId, timestamp, body.nodes.length, edges.length, nodeIds, edgeIds, status, error);

    return { uploadId, nodeIds, edgeIds };
  }

  private async recordHistory(
    uploadId: string,
    timestamp: string,
    nodeCount: number,
    edgeCount: number,
    nodeIds: string[],
    edgeIds: string[],
    status: "success" | "partial" | "failed",
    error?: string,
  ): Promise<void> {
    try {
      const db = getDb();
      const col = getUploadHistoryCollection(db);
      const record: UploadHistory = {
        id: uploadId,
        user: "system", // TODO: extract from auth token when NyxID proxy passes user info
        uploadTime: timestamp,
        nodeCount,
        edgeCount,
        nodeIds,
        edgeIds,
        status,
        error,
      };
      await col.insertOne(record);
    } catch (err) {
      logger.error({ uploadId, err }, "Failed to record upload history");
    }
  }
}

@Route("history")
@Tags("History")
export class HistoryController extends Controller {
  /**
   * Get paginated upload history.
   * @summary List upload history
   */
  @Get()
  public async listHistory(
    @Query() page: number = 1,
    @Query() pageSize: number = 20
  ): Promise<HistoryListResponse> {
    pageSize = Math.min(pageSize, 100);
    const db = getDb();
    const col = getUploadHistoryCollection(db);

    const skip = (page - 1) * pageSize;
    const [records, total] = await Promise.all([
      col.find().sort({ uploadTime: -1 }).skip(skip).limit(pageSize).toArray(),
      col.countDocuments(),
    ]);

    return {
      records: records.map(({ _id, ...rest }) => rest),
      total,
      page,
      pageSize,
    };
  }

  /**
   * Get detailed trace for a single upload.
   * @summary Get upload detail
   */
  @Get("{uploadId}")
  @Response<ErrorResponse>(404, "Upload not found")
  public async getUpload(
    @Path() uploadId: string
  ): Promise<UploadHistoryDTO> {
    const db = getDb();
    const col = getUploadHistoryCollection(db);

    const record = await col.findOne({ id: uploadId });
    if (!record) {
      this.setStatus(404);
      return { error: "Upload not found" } as unknown as UploadHistoryDTO;
    }

    const { _id, ...rest } = record;
    return rest;
  }
}
