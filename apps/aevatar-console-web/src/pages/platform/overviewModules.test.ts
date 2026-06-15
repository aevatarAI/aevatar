import { setLocale } from "@umijs/max";
import { getPlatformOverviewModules } from "./overviewModules";

describe("getPlatformOverviewModules", () => {
  beforeEach(() => {
    setLocale("en-US", false);
  });

  it("keeps the platform overview descriptors stable and pointed at existing deep links", () => {
    const modules = getPlatformOverviewModules();

    expect(modules.map((module) => module.key)).toEqual([
      "capabilities",
      "accessRules",
      "releases",
      "runs",
      "runtimeMap",
    ]);
    expect(modules.map((module) => module.title)).toEqual([
      "Capabilities",
      "Access & Rules",
      "Releases",
      "Runs",
      "Runtime Map",
    ]);
    expect(modules.map((module) => module.href)).toEqual([
      "/services",
      "/governance",
      "/deployments",
      "/runtime/runs",
      "/runtime/explorer",
    ]);
    expect(modules.every((module) => module.description.length > 40)).toBe(true);
    expect(
      modules.find((module) => module.key === "runtimeMap")?.summary,
    ).toBe("Starts from explicit runtime context; no actor graph is guessed on the overview.");
  });
});
