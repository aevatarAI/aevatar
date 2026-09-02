import {
  Handle,
  Position,
  type NodeProps,
  type Node,
} from "@xyflow/react";
import React from "react";
import type { MissionWallWorkflowStepNode } from "../models";
import {
  formatLatency,
  formatStepStatus,
  stepTone,
} from "../missionWallFormatters";

type WorkflowReplayNodeData = {
  readonly node: MissionWallWorkflowStepNode;
};

type WorkflowReplayNode = Node<WorkflowReplayNodeData>;

function stepInitial(stepType: string): string {
  if (stepType === "connector_call" || stepType === "tool_call") {
    return "API";
  }

  if (stepType === "human_approval") {
    return "HM";
  }

  if (stepType === "emit") {
    return "EV";
  }

  if (stepType === "retrieve_facts") {
    return "DB";
  }

  return "AI";
}

export function WorkflowStepNode({
  data,
}: NodeProps<WorkflowReplayNode>) {
  const node = data.node;
  const tone = stepTone(node.status);
  const latency = formatLatency(node.latencyMs);
  const className = [
    "mission-wall-step-node",
    node.focused ? "mission-wall-step-node--focused" : "",
    node.status === "active" ? "mission-wall-step-node--active" : "",
    node.status === "waiting" ? "mission-wall-step-node--waiting" : "",
    node.status === "failed" ? "mission-wall-step-node--failed" : "",
  ]
    .filter(Boolean)
    .join(" ");

  return (
    <article className={className}>
      <Handle
        className="mission-wall-step-node__handle mission-wall-step-node__handle--target"
        position={Position.Left}
        type="target"
      />
      <div className="mission-wall-step-node__top">
        <span className="mission-wall-step-node__icon">
          {stepInitial(node.stepType)}
        </span>
        <div className="mission-wall-step-node__identity">
          <div className="mission-wall-step-node__name">{node.stepId}</div>
          <div className="mission-wall-step-node__type">{node.stepType}</div>
        </div>
        <span className={`mission-wall-pill mission-wall-pill--${tone}`}>
          {formatStepStatus(node.status)}
        </span>
      </div>
      <div className="mission-wall-step-node__meta">
        {node.targetRole ? (
          <span>{node.targetRole}</span>
        ) : (
          <span>{node.parametersSummary || node.stepType}</span>
        )}
        {latency ? <span>{latency}</span> : null}
      </div>
      <Handle
        className="mission-wall-step-node__handle mission-wall-step-node__handle--source"
        position={Position.Right}
        type="source"
      />
    </article>
  );
}
