import {
  decodeWorkflowActorGraphEdgesResponse,
  decodeWorkflowActorGraphEnrichedResponse,
  decodeWorkflowActorGraphSubgraphResponse,
  decodeWorkflowActorSnapshotResponse,
  decodeWorkflowActorTimelineResponse,
} from "./runtimeDecoders";
import { requestJson, withQuery } from "./http/client";
import type {
  WorkflowActorGraphEdge,
  WorkflowActorGraphEnrichedSnapshot,
  WorkflowActorGraphSubgraph,
  WorkflowActorSnapshot,
  WorkflowActorTimelineItem,
} from "@/shared/models/runtime/actors";

export type ActorGraphDirection = "Both" | "Outbound" | "Inbound";

type ActorGraphOptions = {
  depth?: number;
  take?: number;
  direction?: ActorGraphDirection;
  edgeTypes?: string[];
};

type ActorTimelineOptions = {
  take?: number;
};

function normalizeEdgeTypes(edgeTypes?: string[]): string[] {
  return (edgeTypes ?? [])
    .map((value) => value.trim())
    .filter((value) => value.length > 0);
}

export const runtimeActorsApi = {
  getActorSnapshot(actorId: string): Promise<WorkflowActorSnapshot> {
    return requestJson(
      `/api/actors/${encodeURIComponent(actorId)}`,
      decodeWorkflowActorSnapshotResponse
    );
  },

  getActorTimeline(
    actorId: string,
    options?: ActorTimelineOptions
  ): Promise<WorkflowActorTimelineItem[]> {
    return requestJson(
      withQuery(`/api/actors/${encodeURIComponent(actorId)}/timeline`, {
        take: options?.take,
      }),
      decodeWorkflowActorTimelineResponse
    );
  },

  getActorGraphEnriched(
    actorId: string,
    options?: ActorGraphOptions
  ): Promise<WorkflowActorGraphEnrichedSnapshot> {
    return requestJson(
      withQuery(`/api/actors/${encodeURIComponent(actorId)}/graph-enriched`, {
        depth: options?.depth,
        take: options?.take,
        direction: options?.direction,
        edgeTypes: normalizeEdgeTypes(options?.edgeTypes),
      }),
      decodeWorkflowActorGraphEnrichedResponse
    );
  },

  getActorGraphEdges(
    actorId: string,
    options?: Omit<ActorGraphOptions, "depth">
  ): Promise<WorkflowActorGraphEdge[]> {
    return requestJson(
      withQuery(`/api/actors/${encodeURIComponent(actorId)}/graph-edges`, {
        take: options?.take,
        direction: options?.direction,
        edgeTypes: normalizeEdgeTypes(options?.edgeTypes),
      }),
      decodeWorkflowActorGraphEdgesResponse
    );
  },

  getActorGraphSubgraph(
    actorId: string,
    options?: ActorGraphOptions
  ): Promise<WorkflowActorGraphSubgraph> {
    return requestJson(
      withQuery(`/api/actors/${encodeURIComponent(actorId)}/graph-subgraph`, {
        depth: options?.depth,
        take: options?.take,
        direction: options?.direction,
        edgeTypes: normalizeEdgeTypes(options?.edgeTypes),
      }),
      decodeWorkflowActorGraphSubgraphResponse
    );
  },
};
