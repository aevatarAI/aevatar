import {
  encodeAppScriptCommandBase64,
  encodeChatRequestEventBase64,
  typeUrlToEndpointId,
} from "./protobufPayload";

describe("protobufPayload", () => {
  it("normalizes type urls into endpoint ids", () => {
    expect(
      typeUrlToEndpointId(
        "type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand",
      ),
    ).toBe("aevatar.tools.cli.hosting.AppScriptCommand");
  });

  it("encodes AppScriptCommand payloads as protobuf base64", () => {
    expect(
      encodeAppScriptCommandBase64({
        commandId: "",
        input: "hello",
      }),
    ).toBe("EgVoZWxsbw==");
  });

  it("encodes ChatRequestEvent prompt, session, and scope on stable protobuf fields", () => {
    expect(
      encodeChatRequestEventBase64({
        prompt: "hello",
        sessionId: "session-1",
        scopeId: "scope-1",
      }),
    ).toBe("CgVoZWxsbxIJc2Vzc2lvbi0xKgdzY29wZS0x");
  });
});
