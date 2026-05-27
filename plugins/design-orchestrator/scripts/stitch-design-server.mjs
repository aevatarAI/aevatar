import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
  ResultSchema
} from "@modelcontextprotocol/sdk/types.js";
import {
  readGoogleCloudAccessToken,
  resolveGoogleCloudProject,
  stitchMcpUrl
} from "./lib/google-stitch-auth.mjs";

let remoteClientPromise;

function createRemoteClient() {
  const transport = new StreamableHTTPClientTransport(new URL(stitchMcpUrl), {
    requestInit: {
      headers: {
        Authorization: `Bearer ${readGoogleCloudAccessToken()}`,
        "X-Goog-User-Project": resolveGoogleCloudProject()
      }
    }
  });

  const client = new Client(
    {
      name: "design-orchestrator-stitch-remote-client",
      version: "0.1.0"
    },
    {
      capabilities: {}
    }
  );

  return client.connect(transport).then(() => client);
}

async function getRemoteClient() {
  if (!remoteClientPromise) {
    remoteClientPromise = createRemoteClient();
  }

  return remoteClientPromise;
}

function sanitizeSchema(schema) {
  if (!schema || typeof schema !== "object") {
    return {
      type: "object",
      properties: {}
    };
  }

  const clone = structuredClone(schema);

  delete clone.outputSchema;

  if (clone.type !== "object") {
    clone.type = "object";
  }

  if (!clone.properties || typeof clone.properties !== "object") {
    clone.properties = {};
  }

  return clone;
}

function sanitizeTool(tool) {
  return {
    name: tool.name,
    description: tool.description,
    inputSchema: sanitizeSchema(tool.inputSchema),
    annotations: tool.annotations
  };
}

function toMcpToolResult(remoteResult) {
  if (Array.isArray(remoteResult?.content)) {
    return {
      content: remoteResult.content,
      isError: Boolean(remoteResult.isError),
      structuredContent: remoteResult.structuredContent
    };
  }

  return {
    content: [
      {
        type: "text",
        text: JSON.stringify(remoteResult, null, 2)
      }
    ]
  };
}

async function listRemoteTools() {
  const client = await getRemoteClient();
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

  return (result.tools || []).map(sanitizeTool);
}

async function callRemoteTool(name, args) {
  const client = await getRemoteClient();
  const result = await client.request(
    {
      method: "tools/call",
      params: {
        name,
        arguments: args || {}
      }
    },
    ResultSchema,
    {
      timeout: 120000
    }
  );

  return toMcpToolResult(result);
}

const server = new Server(
  {
    name: "design-orchestrator-stitch-design",
    version: "0.1.0"
  },
  {
    capabilities: {
      tools: {}
    }
  }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: await listRemoteTools()
}));

server.setRequestHandler(CallToolRequestSchema, async (request) => (
  callRemoteTool(request.params.name, request.params.arguments)
));

const transport = new StdioServerTransport();
await server.connect(transport);
