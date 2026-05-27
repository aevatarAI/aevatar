import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema
} from "@modelcontextprotocol/sdk/types.js";

export async function startToolServer({
  name,
  version,
  tools,
  onCallTool
}) {
  const server = new Server(
    {
      name,
      version
    },
    {
      capabilities: {
        tools: {}
      }
    }
  );

  server.setRequestHandler(ListToolsRequestSchema, async () => ({
    tools
  }));

  server.setRequestHandler(CallToolRequestSchema, async (request) => {
    const result = await onCallTool(request.params.name, request.params.arguments || {});
    return {
      content: [
        {
          type: "text",
          text: typeof result === "string" ? result : JSON.stringify(result, null, 2)
        }
      ]
    };
  });

  const transport = new StdioServerTransport();
  await server.connect(transport);
}
