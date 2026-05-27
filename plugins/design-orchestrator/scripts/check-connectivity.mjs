import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { ResultSchema } from "@modelcontextprotocol/sdk/types.js";
import { readOptionalEnv, redactSecret } from "./lib/env.mjs";
import {
  readGoogleCloudAccessToken,
  resolveGoogleCloudProject,
  stitchMcpUrl
} from "./lib/google-stitch-auth.mjs";

function checkChatGptProductChannel() {
  const channel = readOptionalEnv("CHATGPT_CHANNEL", "browser");

  if (channel === "api") {
    const apiKey = readOptionalEnv("OPENAI_API_KEY");

    return {
      configured: Boolean(apiKey),
      mode: "OpenAI Responses API",
      value_preview: redactSecret(apiKey),
      error: apiKey ? "" : "OPENAI_API_KEY is required when CHATGPT_CHANNEL=api."
    };
  }

  return {
    configured: false,
    mode: "ChatGPT web login via Codex in-app browser",
    value_preview: "runtime browser smoke test required",
    error: "Send a short prompt through chatgpt.com in the Codex in-app browser before treating this channel as connected."
  };
}

function readStitchAuthState() {
  try {
    const explicitToken = readOptionalEnv("STITCH_ACCESS_TOKEN");
    const token = readGoogleCloudAccessToken();

    return {
      configured: true,
      mode: explicitToken ? "STITCH_ACCESS_TOKEN" : "gcloud auth print-access-token",
      value_preview: redactSecret(token)
    };
  } catch (error) {
    return {
      configured: false,
      mode: "gcloud auth print-access-token",
      error: error instanceof Error ? error.message : String(error)
    };
  }
}

async function checkStitchMcp() {
  try {
    const project = resolveGoogleCloudProject();
    if (!project) {
      return {
        configured: false,
        mode: "remote MCP tools/list",
        error: "GOOGLE_CLOUD_PROJECT is required for Stitch MCP calls."
      };
    }

    const transport = new StreamableHTTPClientTransport(new URL(stitchMcpUrl), {
      requestInit: {
        headers: {
          Authorization: `Bearer ${readGoogleCloudAccessToken()}`,
          "X-Goog-User-Project": project
        }
      }
    });
    const client = new Client(
      {
        name: "design-orchestrator-connectivity-check",
        version: "0.1.0"
      },
      {
        capabilities: {}
      }
    );

    await client.connect(transport);
    const result = await client.request(
      {
        method: "tools/list",
        params: {}
      },
      ResultSchema,
      {
        timeout: 30000
      }
    );
    await client.close();

    return {
      configured: true,
      mode: "remote MCP tools/list",
      value_preview: `${result.tools?.length || 0} tools`
    };
  } catch (error) {
    return {
      configured: false,
      mode: "remote MCP tools/list",
      error: error instanceof Error ? error.message : String(error)
    };
  }
}

const checks = [
  {
    name: "CHATGPT_PRODUCT",
    ...checkChatGptProductChannel()
  },
  {
    name: "GOOGLE_CLOUD_PROJECT",
    value: resolveGoogleCloudProject()
  },
  {
    name: "STITCH_AUTH",
    ...readStitchAuthState()
  },
  {
    name: "STITCH_MCP",
    ...await checkStitchMcp()
  }
];

const report = checks.map((item) => ({
  name: item.name,
  configured: "configured" in item ? item.configured : Boolean(item.value),
  mode: item.mode || "",
  value_preview: item.value_preview || (item.name.endsWith("_KEY") ? redactSecret(item.value) : item.value || ""),
  error: item.error || ""
}));

console.log(JSON.stringify(report, null, 2));
