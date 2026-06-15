export type PlatformOverviewModuleKey =
  | "capabilities"
  | "accessRules"
  | "releases"
  | "runs"
  | "runtimeMap";

export type PlatformOverviewModule = {
  readonly ctaLabel: string;
  readonly description: string;
  readonly href: string;
  readonly key: PlatformOverviewModuleKey;
  readonly summary: string;
  readonly title: string;
};
