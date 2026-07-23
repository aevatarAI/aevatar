import { readResponseError, readResponseErrorDetails } from "./error";

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

  it("reads machine error codes from the backend error field", async () => {
    await expect(
      readResponseErrorDetails({
        status: 502,
        statusText: "Bad Gateway",
        text: async () =>
          JSON.stringify({
            error: "issued_binding_invalid",
            detail: "The issued binding could not be adopted.",
          }),
      }),
    ).resolves.toEqual({
      code: "issued_binding_invalid",
      message: "issued_binding_invalid",
      status: 502,
    });
  });
});
