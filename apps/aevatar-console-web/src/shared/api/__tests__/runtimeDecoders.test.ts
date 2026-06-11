import { decodeWorkflowCapabilitiesResponse } from "../runtimeDecoders";

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
