import {
  decodeWorkflowCapabilitiesResponse,
  decodeWorkflowCatalogItemDetailResponse,
} from "../runtimeDecoders";

describe("decodeWorkflowCapabilitiesResponse", () => {
  it("decodes primitive capabilities without the removed closedWorldBlocked field", () => {
    const decoded = decodeWorkflowCapabilitiesResponse({
      schemaVersion: "1",
      generatedAtUtc: "2026-05-24T00:00:00Z",
      primitives: [
        {
          name: "llm_call",
          aliases: ["llm"],
          category: "ai",
          description: "Invoke an LLM provider.",
          runtimeModule: "LlmCallModule",
          parameters: [
            {
              name: "prompt",
              type: "string",
              required: true,
              description: "Prompt text.",
              default: "",
              enum: [],
            },
          ],
        },
      ],
      connectors: [],
      workflows: [],
    });

    expect(decoded.primitives).toHaveLength(1);
    expect(decoded.primitives[0].name).toBe("llm_call");
    expect(decoded.primitives[0]).not.toHaveProperty("closedWorldBlocked");
  });
});

describe("decodeWorkflowCatalogItemDetailResponse", () => {
  it("treats the retired streamBufferCapacity role field as null when omitted", () => {
    const decoded = decodeWorkflowCatalogItemDetailResponse({
      catalog: {
        name: "direct",
        description: "Direct chat workflow.",
        category: "chat",
        group: "starter",
        groupLabel: "Starter Workflows",
        sortOrder: 1,
        source: "BuiltIn",
        sourceLabel: "Built-in",
        showInLibrary: true,
        isPrimitiveExample: false,
        requiresLlmProvider: true,
        primitives: ["llm_call"],
      },
      yaml: "name: direct\n",
      definition: {
        name: "direct",
        description: "Direct chat workflow.",
        closedWorldMode: true,
        roles: [
          {
            id: "assistant",
            name: "Assistant",
            systemPrompt: "Help the user.",
            provider: "",
            model: "",
            temperature: null,
            maxTokens: null,
            maxToolRounds: null,
            maxHistoryMessages: null,
            eventModules: [],
            eventRoutes: "",
            connectors: [],
          },
        ],
        steps: [],
      },
      edges: [],
    });

    expect(decoded.definition.roles[0].streamBufferCapacity).toBeNull();
  });
});
