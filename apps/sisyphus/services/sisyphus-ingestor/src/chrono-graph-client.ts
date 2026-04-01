import pino from "pino";

const logger = pino({ name: "ingestor:chrono-graph" });

const CHRONO_GRAPH_URL = process.env["CHRONO_GRAPH_URL"] ?? "http://localhost:3000";
const GRAPH_ID = process.env["GRAPH_ID"] ?? "";

export interface GraphNode {
  [key: string]: unknown;
}

export interface GraphEdge {
  [key: string]: unknown;
}

export interface WriteNodesResult {
  nodeIds: string[];
}

export interface WriteEdgesResult {
  edgeIds: string[];
}

export async function writeNodes(nodes: GraphNode[]): Promise<WriteNodesResult> {
  const url = `${CHRONO_GRAPH_URL}/api/graphs/${GRAPH_ID}/nodes`;
  logger.info({ url, count: nodes.length }, "Writing nodes to chrono-graph");

  const resp = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ nodes }),
    signal: AbortSignal.timeout(30000),
  });

  if (!resp.ok) {
    const body = await resp.text();
    logger.error({ status: resp.status, body, nodeCount: nodes.length }, "Failed to write nodes to chrono-graph");
    throw new Error(`chrono-graph POST /nodes failed: HTTP ${resp.status} — ${body}`);
  }

  const result = await resp.json() as Record<string, unknown>;
  const nodeIds = (result.nodes as Array<{ id: string }> | undefined)?.map(n => n.id) ?? [];

  logger.info({ nodeIds: nodeIds.length }, "Nodes written successfully");
  return { nodeIds };
}

export async function writeEdges(edges: GraphEdge[]): Promise<WriteEdgesResult> {
  const url = `${CHRONO_GRAPH_URL}/api/graphs/${GRAPH_ID}/edges`;
  logger.info({ url, count: edges.length }, "Writing edges to chrono-graph");

  const resp = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ edges }),
    signal: AbortSignal.timeout(30000),
  });

  if (!resp.ok) {
    const body = await resp.text();
    logger.error({ status: resp.status, body, edgeCount: edges.length }, "Failed to write edges to chrono-graph");
    throw new Error(`chrono-graph POST /edges failed: HTTP ${resp.status} — ${body}`);
  }

  const result = await resp.json() as Record<string, unknown>;
  const edgeIds = (result.edges as Array<{ id: string }> | undefined)?.map(e => e.id) ?? [];

  logger.info({ edgeIds: edgeIds.length }, "Edges written successfully");
  return { edgeIds };
}
