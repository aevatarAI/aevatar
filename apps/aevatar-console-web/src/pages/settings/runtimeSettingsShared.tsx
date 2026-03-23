import type { ProDescriptionsItemProps } from "@ant-design/pro-components";
import { Tag } from "antd";
import React from "react";
import type {
  ConfigurationLlmProbeResult,
  ConfigurationPathStatus,
  ConfigurationWorkflowFile,
} from "@/shared/models/platform/configuration";

export type SettingsSummaryRecord = {
  configStatus: "ready" | "unavailable";
  configMode: string;
  runtimeWorkflowFiles: number;
  primitiveCount: number;
  defaultProvider: string;
};

export type ConfigurationPathRecord = {
  id: string;
  label: string;
  status: ConfigurationPathStatus;
};

export type WorkflowDraftSource = "home" | "repo";

const configurationValueEnum = {
  ready: { text: "Ready", status: "Success" },
  unavailable: { text: "Unavailable", status: "Error" },
} as const;

export const settingsSummaryColumns: ProDescriptionsItemProps<SettingsSummaryRecord>[] =
  [
    {
      title: "Configuration API",
      dataIndex: "configStatus",
      valueType: "status" as any,
      valueEnum: configurationValueEnum,
    },
    {
      title: "Configuration mode",
      dataIndex: "configMode",
      render: (_, record) => record.configMode || "unknown",
    },
    {
      title: "Runtime workflow files",
      dataIndex: "runtimeWorkflowFiles",
      valueType: "digit",
    },
    {
      title: "Primitive count",
      dataIndex: "primitiveCount",
      valueType: "digit",
    },
    {
      title: "Default provider",
      dataIndex: "defaultProvider",
      render: (_, record) => <Tag>{record.defaultProvider || "default"}</Tag>,
    },
  ];

export function workflowKey(
  item: Pick<ConfigurationWorkflowFile, "filename" | "source">
): string {
  return `${item.source}:${item.filename}`;
}

export function normalizeWorkflowSource(source: string): WorkflowDraftSource {
  return source === "repo" ? "repo" : "home";
}

export function buildNewWorkflowTemplate(filename: string): string {
  const normalizedName = filename
    .replace(/\.(yaml|yml)$/i, "")
    .replace(/[^A-Za-z0-9_]+/g, "_")
    .replace(/^_+|_+$/g, "")
    .toLowerCase();
  const workflowName = normalizedName || "new_workflow";

  return `name: ${workflowName}\ndescription: Draft workflow\nsteps:\n  - id: start\n    type: assign\n    parameters:\n      target: status\n      value: ready\n`;
}

export function formatMcpArgs(args: string[]): string {
  return args.join("\n");
}

export function formatMcpEnv(env: Record<string, string>): string {
  return JSON.stringify(env, null, 2);
}

export function parseMcpArgs(value: string): string[] {
  return value
    .split(/\r?\n/)
    .map((entry) => entry.trim())
    .filter(Boolean);
}

export function parseMcpEnv(value: string): Record<string, string> {
  const trimmed = value.trim();
  if (!trimmed) {
    return {};
  }

  const parsed = JSON.parse(trimmed) as unknown;
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error("MCP env must be a JSON object.");
  }

  return Object.fromEntries(
    Object.entries(parsed as Record<string, unknown>).map(([key, entry]) => {
      if (typeof entry !== "string") {
        throw new Error(`MCP env value for "${key}" must be a string.`);
      }

      return [key, entry];
    })
  );
}

export function formatProbeSummary(
  result: ConfigurationLlmProbeResult
): string {
  if (!result.ok) {
    return result.error || "Probe failed.";
  }

  if (result.models && result.models.length > 0) {
    return `Discovered ${result.models.length} models.`;
  }

  if (result.modelsCount !== undefined) {
    return `Probe succeeded with ${result.modelsCount} models.`;
  }

  return "Probe succeeded.";
}
