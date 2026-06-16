import { readResponseError } from "./error";

describe("readResponseError", () => {
  it("includes ASP.NET validation problem details", async () => {
    await expect(
      readResponseError({
        status: 400,
        statusText: "Bad Request",
        text: async () =>
          JSON.stringify({
            title: "One or more validation errors occurred.",
            status: 400,
            errors: {
              "$.serviceInvocation.payload.value": [
                "The JSON value could not be converted to Google.Protobuf.ByteString.",
              ],
            },
          }),
      }),
    ).resolves.toBe(
      "One or more validation errors occurred.: $.serviceInvocation.payload.value: The JSON value could not be converted to Google.Protobuf.ByteString.",
    );
  });
});
