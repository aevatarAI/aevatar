import { readResponseError } from "./error";

describe("readResponseError", () => {
  it("prefers actionable detail over a machine error code", async () => {
    await expect(
      readResponseError({
        status: 409,
        statusText: "Conflict",
        text: async () =>
          JSON.stringify({
            error: "required_service_access_missing",
            detail:
              "Return to login and allow access to the Aevatar service in NyxID.",
          }),
      }),
    ).resolves.toBe(
      "Return to login and allow access to the Aevatar service in NyxID.",
    );
  });

  it("uses the error field when no user-facing detail is available", async () => {
    await expect(
      readResponseError({
        status: 409,
        statusText: "Conflict",
        text: async () =>
          JSON.stringify({ error: "required_service_access_missing" }),
      }),
    ).resolves.toBe("required_service_access_missing");
  });

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
