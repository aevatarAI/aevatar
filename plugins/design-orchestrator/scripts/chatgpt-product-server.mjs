import OpenAI from "openai";
import { readOptionalEnv } from "./lib/env.mjs";
import { startToolServer } from "./lib/mcp-helpers.mjs";

const baseURL = readOptionalEnv("OPENAI_BASE_URL", "https://api.openai.com/v1");
const apiKey = readOptionalEnv("OPENAI_API_KEY");

const client = apiKey
  ? new OpenAI({
    apiKey,
    baseURL
  })
  : undefined;

const tools = [
  {
    name: "review_product_logic",
    description: "Review a scoped product flow and return product-improvement guidance grounded in the repository context.",
    inputSchema: {
      type: "object",
      properties: {
        product_context: {
          type: "string",
          description: "Concrete repository and product context for the feature under review."
        },
        observed_problem: {
          type: "string",
          description: "What appears weak, confusing, or under-specified."
        },
        constraints: {
          type: "string",
          description: "Technical or product constraints that must be respected."
        },
        scope_limit: {
          type: "string",
          description: "What this iteration is allowed to touch."
        }
      },
      required: [
        "product_context",
        "observed_problem"
      ]
    }
  }
];

function buildPrompt(args) {
  return [
    "You are a senior product designer and product thinker.",
    "Improve the product logic of an existing software product without inventing backend capabilities not supported by the repository.",
    "",
    "Product context:",
    args.product_context,
    "",
    "Observed problem:",
    args.observed_problem,
    "",
    "Constraints:",
    args.constraints || "None provided.",
    "",
    "Scope limit:",
    args.scope_limit || "One bounded iteration.",
    "",
    "Return sections titled exactly:",
    "1. Recommended direction",
    "2. Why it is better",
    "3. Proposed flow",
    "4. Key UI/content changes",
    "5. Edge cases and states",
    "6. Implementation notes for engineering",
    "7. What to defer until later"
  ].join("\n");
}

async function reviewProductLogic(args) {
  const prompt = buildPrompt(args);

  if (!client) {
    return {
      state: "browser_channel_required",
      reason: "OPENAI_API_KEY is not configured. Send prompt_to_send through the logged-in ChatGPT web channel instead.",
      prompt_to_send: prompt
    };
  }

  const response = await client.responses.create({
    model: readOptionalEnv("OPENAI_MODEL", "gpt-5.4"),
    input: prompt
  });

  return response.output_text || JSON.stringify(response, null, 2);
}

await startToolServer({
  name: "design-orchestrator-chatgpt-product",
  version: "0.1.0",
  tools,
  onCallTool: async (toolName, args) => {
    if (toolName !== "review_product_logic") {
      throw new Error(`Unknown tool: ${toolName}`);
    }

    return reviewProductLogic(args);
  }
});
