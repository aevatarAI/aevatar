import {
  encodeAppScriptCommandBase64,
  typeUrlToEndpointId,
} from "./protobufPayload";

describe("protobufPayload", () => {
  it("normalizes type urls into endpoint ids", () => {
    expect(
      typeUrlToEndpointId(
        "type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand"
      )
    ).toBe("aevatar.tools.cli.hosting.AppScriptCommand");
  });

  it("encodes AppScriptCommand payloads as protobuf base64", () => {
    expect(
      encodeAppScriptCommandBase64({
        commandId: "",
        input: "hello",
      })
    ).toBe("EgVoZWxsbw==");
  });
});
