export const STUDIO_AUTHORING_ROUTE_METADATA_KEY = "nyxid.route_preference";
export const STUDIO_AUTHORING_GATEWAY_ROUTE = "";

export function buildStudioAuthoringLlmMetadata(
  base: Readonly<Record<string, string>>
): Record<string, string> {
  return {
    ...base,
    [STUDIO_AUTHORING_ROUTE_METADATA_KEY]: STUDIO_AUTHORING_GATEWAY_ROUTE,
  };
}
