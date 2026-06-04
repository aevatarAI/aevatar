import type { ProLayoutProps } from "@ant-design/pro-components";
import type { ThemeConfig } from "antd";
import type { CSSProperties } from "react";

export const AEVATAR_GLOBAL_UI_SPEC = {
  name: "Aevatar global UI specification (TS)",
  philosophy: {
    viewport: "100vh viewport first, internal regions scroll independently",
    workspace:
      "Main workspace first, context enters through responsive drawers and focused modals",
    density: "High-signal metrics stay on stage, developer detail moves into inspectors",
    status: "Run, observation, governance, and asset states share one semantic color system",
  },
  modules: {
    assets: "ProList card flow with capability-first status framing",
    governance:
      "Two-column governance workbench with audit timeline, list-driven control surfaces, and action drawers",
    studio:
      "Two-panel script studio with file tree, editor stage, live console, and promotion modal",
    deployments:
      "List plus detail workbench with dual revision compare and extra-wide rollout drawer",
  },
  tokens: {
    headerHeight: 56,
    contentPadding: 20,
    sectionGap: 14,
    borderRadius: 6,
    compactRadius: 4,
    inspectorWidth: 720,
  },
} as const;

export const AEVATAR_DAYLIGHT_OPERATIONS_TOKENS = {
  colorBgContainer: '#ffffff',
  colorBgElevated: '#ffffff',
  colorBgLayout: '#f6f8fb',
  colorBorder: 'rgba(71, 85, 105, 0.22)',
  colorBorderSecondary: 'rgba(15, 23, 42, 0.08)',
  colorCritical: '#dc2626',
  colorGraphite: '#111827',
  colorGraphiteMuted: '#475467',
  colorHealthy: '#16a34a',
  colorIntervention: '#d97706',
  colorLive: '#0891b2',
  colorPrimary: '#0f766e',
  colorPorcelain: '#eef2f6',
  colorShell: '#f6f8fb',
  shadowPanel:
    '0 14px 34px rgba(15, 23, 42, 0.06), 0 1px 0 rgba(255, 255, 255, 0.74) inset',
  shadowTool: '0 8px 20px rgba(15, 23, 42, 0.05)',
} as const;

export type AevatarStatusDomain =
  | "asset"
  | "evolution"
  | "governance"
  | "observation"
  | "run";

export type AevatarAssetLifecycleStatus = "active" | "draft";

export const aevatarEvolutionStatuses = [
  "pending",
  "proposed",
  "build_requested",
  "validated",
  "validation_failed",
  "rejected",
  "promotion_failed",
  "promoted",
  "rollback_requested",
  "rolled_back",
] as const;

export type AevatarEvolutionStatus =
  (typeof aevatarEvolutionStatuses)[number];

export type AevatarSemanticTone =
  | "default"
  | "error"
  | "info"
  | "success"
  | "warning";

export interface AevatarThemeSurfaceToken {
  borderRadius: number;
  borderRadiusLG: number;
  boxShadowSecondary: string;
  colorBgContainer: string;
  colorBgElevated: string;
  colorBgLayout: string;
  colorBorder: string;
  colorBorderSecondary: string;
  colorError: string;
  colorErrorBg: string;
  colorErrorBorder: string;
  colorErrorText: string;
  colorFillAlter: string;
  colorFillSecondary: string;
  colorFillTertiary: string;
  colorPrimary: string;
  colorPrimaryBg: string;
  colorPrimaryBorder: string;
  colorSuccess: string;
  colorSuccessBg: string;
  colorSuccessBorder: string;
  colorSuccessText: string;
  colorText: string;
  colorTextHeading: string;
  colorTextLightSolid: string;
  colorTextQuaternary: string;
  colorTextSecondary: string;
  colorTextTertiary: string;
  colorWarning: string;
  colorWarningBg: string;
  colorWarningBorder: string;
  colorWarningText: string;
}

export interface AevatarStatusVisual {
  background: string;
  borderColor: string;
  color: string;
  dotColor: string;
}

export interface AevatarMetricVisual extends AevatarStatusVisual {
  iconColor: string;
  labelColor: string;
  secondaryColor: string;
  valueColor: string;
}

export const aevatarThemeConfig: ThemeConfig = {
  cssVar: {
    key: 'aevatar-console',
    prefix: 'ant',
  },
  components: {
    Button: {
      contentFontSize: 13,
      defaultActiveBorderColor: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorPrimary,
      defaultActiveColor: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorPrimary,
      defaultBg: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBgContainer,
      defaultBorderColor: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBorder,
      defaultColor: '#1f2937',
      defaultHoverBg: '#f8fafc',
      defaultHoverBorderColor: 'rgba(15, 118, 110, 0.34)',
      defaultHoverColor: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorPrimary,
      defaultShadow: 'none',
      fontWeight: 600,
      primaryShadow: '0 8px 18px rgba(15, 118, 110, 0.16)',
      textHoverBg: 'rgba(15, 118, 110, 0.08)',
      textTextHoverColor: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorPrimary,
    },
    Card: {
      actionsBg: '#f8fafc',
      bodyPadding: 16,
      bodyPaddingSM: 12,
      extraColor: '#475467',
      headerBg: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBgContainer,
      headerFontSize: 14,
      headerFontSizeSM: 13,
      headerHeight: 44,
      headerHeightSM: 38,
      headerPadding: 16,
      headerPaddingSM: 12,
    },
    Layout: {
      bodyBg: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBgLayout,
      footerBg: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBgLayout,
      headerBg: 'rgba(255, 255, 255, 0.92)',
      headerColor: '#1f2937',
      headerHeight: AEVATAR_GLOBAL_UI_SPEC.tokens.headerHeight,
      lightSiderBg: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBgLayout,
      siderBg: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBgLayout,
      triggerBg: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBgContainer,
      triggerColor: '#475467',
    },
    Menu: {
      activeBarHeight: 0,
      activeBarWidth: 0,
      fontSize: 13,
      groupTitleColor: '#667085',
      groupTitleFontSize: 11,
      itemBg: 'transparent',
      itemBorderRadius: 6,
      itemColor: '#475467',
      itemHeight: 38,
      itemHoverBg: 'rgba(15, 118, 110, 0.07)',
      itemHoverColor: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorGraphite,
      itemMarginBlock: 4,
      itemMarginInline: 10,
      itemPaddingInline: 12,
      itemSelectedBg: 'rgba(15, 118, 110, 0.1)',
      itemSelectedColor: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorGraphite,
      subMenuItemBg: 'transparent',
      subMenuItemBorderRadius: 6,
    },
    Segmented: {
      itemHoverBg: 'rgba(15, 118, 110, 0.07)',
      itemHoverColor: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorPrimary,
      itemSelectedBg: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBgContainer,
      itemSelectedColor: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorPrimary,
      trackBg: '#eef2f6',
      trackPadding: 3,
    },
    Table: {
      borderColor: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBorderSecondary,
      cellFontSize: 13,
      cellFontSizeMD: 13,
      cellPaddingBlock: 11,
      cellPaddingBlockMD: 9,
      cellPaddingBlockSM: 7,
      cellPaddingInline: 14,
      cellPaddingInlineMD: 12,
      cellPaddingInlineSM: 10,
      headerBg: '#f8fafc',
      headerBorderRadius: 6,
      headerColor: '#475467',
      headerSortActiveBg: '#eef2f6',
      headerSortHoverBg: '#eef2f6',
      rowHoverBg: 'rgba(15, 118, 110, 0.05)',
      rowSelectedBg: 'rgba(15, 118, 110, 0.09)',
      rowSelectedHoverBg: 'rgba(15, 118, 110, 0.12)',
    },
    Tag: {
      defaultBg: '#f8fafc',
      defaultColor: '#475467',
    },
  },
  token: {
    borderRadius: AEVATAR_GLOBAL_UI_SPEC.tokens.borderRadius,
    borderRadiusLG: AEVATAR_GLOBAL_UI_SPEC.tokens.borderRadius,
    borderRadiusSM: AEVATAR_GLOBAL_UI_SPEC.tokens.compactRadius,
    boxShadow: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.shadowPanel,
    boxShadowSecondary: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.shadowTool,
    colorBgContainer: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBgContainer,
    colorBgElevated: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBgElevated,
    colorBgLayout: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBgLayout,
    colorBorder: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBorder,
    colorBorderSecondary:
      AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorBorderSecondary,
    colorError: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorCritical,
    colorErrorBg: '#fef2f2',
    colorErrorBorder: '#fecaca',
    colorErrorText: '#b91c1c',
    colorFillAlter: '#f8fafc',
    colorFillQuaternary: 'rgba(15, 23, 42, 0.04)',
    colorFillSecondary: 'rgba(15, 23, 42, 0.06)',
    colorFillTertiary: 'rgba(15, 23, 42, 0.08)',
    colorInfo: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorLive,
    colorInfoBg: '#ecfeff',
    colorInfoBorder: '#a5f3fc',
    colorInfoText: '#0e7490',
    colorLink: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorPrimary,
    colorPrimary: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorPrimary,
    colorPrimaryBg: '#ecfdf5',
    colorPrimaryBorder: '#99f6e4',
    colorSuccess: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorHealthy,
    colorSuccessBg: '#f0fdf4',
    colorSuccessBorder: '#bbf7d0',
    colorSuccessText: '#15803d',
    colorWarning: AEVATAR_DAYLIGHT_OPERATIONS_TOKENS.colorIntervention,
    colorWarningBg: '#fffbeb',
    colorWarningBorder: '#fde68a',
    colorWarningText: '#b45309',
    colorText: "#1f2937",
    colorTextHeading: "#111827",
    colorTextQuaternary: "#98a2b3",
    colorTextSecondary: "#4b5563",
    colorTextTertiary: "#667085",
    fontFamily:
      "AlibabaSans, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif",
  },
};

export const aevatarProLayoutSettings: ProLayoutProps & {
  logo?: string;
  pwa?: boolean;
} = {
  colorWeak: false,
  contentWidth: "Fluid",
  fixSiderbar: true,
  fixedHeader: true,
  iconfontUrl: "",
  layout: "mix",
  menu: {
    autoClose: false,
    defaultOpenAll: true,
  },
  navTheme: "light",
  pwa: false,
  splitMenus: false,
};

export function formatAevatarStatusLabel(value: string) {
  return value
    .split(/[_-]/g)
    .filter(Boolean)
    .map((segment) => segment.charAt(0).toUpperCase() + segment.slice(1))
    .join(" ");
}

export function resolveAevatarSemanticTone(
  domain: AevatarStatusDomain,
  status: string,
): AevatarSemanticTone {
  const normalized = status.trim().toLowerCase();

  if (!normalized || normalized === "idle" || normalized === "unknown") {
    return "default";
  }

  if (domain === "asset") {
    if (normalized === "active" || normalized === "published") {
      return "success";
    }

    if (normalized === "draft") {
      return "warning";
    }

    return "default";
  }

  if (domain === "observation") {
    if (normalized === "projection_settled") {
      return "success";
    }

    if (normalized === "unavailable" || normalized === "disconnected") {
      return "error";
    }

    if (
      normalized === "delayed" ||
      normalized === "partial" ||
      normalized === "seeded" ||
      normalized === "snapshot_available"
    ) {
      return "warning";
    }

    if (normalized === "streaming" || normalized === "live") {
      return "info";
    }

    return "default";
  }

  if (domain === "evolution") {
    if (normalized === "validated" || normalized === "promoted") {
      return "success";
    }

    if (
      normalized === "validation_failed" ||
      normalized === "rejected" ||
      normalized === "promotion_failed"
    ) {
      return "error";
    }

    if (normalized === "rollback_requested" || normalized === "rolled_back") {
      return "warning";
    }

    if (
      normalized === "pending" ||
      normalized === "proposed" ||
      normalized === "build_requested"
    ) {
      return "info";
    }

    return "default";
  }

  if (domain === "governance") {
    if (
      normalized === "validated" ||
      normalized === "promoted" ||
      normalized === "published" ||
      normalized === "active" ||
      normalized === "public" ||
      normalized === "ready"
    ) {
      return "success";
    }

    if (
      normalized === "validation_failed" ||
      normalized === "rejected" ||
      normalized === "promotion_failed" ||
      normalized === "failed" ||
      normalized === "missing" ||
      normalized === "blocked"
    ) {
      return "error";
    }

    if (
      normalized === "retired" ||
      normalized === "disabled" ||
      normalized === "internal" ||
      normalized === "rollback_requested" ||
      normalized === "rolled_back" ||
      normalized === "build_requested" ||
      normalized === "proposed" ||
      normalized === "pending" ||
      normalized === "canary"
    ) {
      return "warning";
    }

    return "default";
  }

  if (normalized === "completed" || normalized === "published") {
    return "success";
  }

  if (
    normalized === "failed" ||
    normalized === "stopped" ||
    normalized === "disconnected"
  ) {
    return "error";
  }

  if (
    normalized === "waiting" ||
    normalized === "waiting_signal" ||
    normalized === "waiting_approval" ||
    normalized === "human_input" ||
    normalized === "human_approval" ||
    normalized === "suspended" ||
    normalized === "draft"
  ) {
    return "warning";
  }

  if (
    normalized === "running" ||
    normalized === "active" ||
    normalized === "streaming" ||
    normalized === "live"
  ) {
    return "info";
  }

  return "default";
}

export function resolveAevatarEvolutionProgress(
  status: string,
): number {
  switch (status.trim().toLowerCase()) {
    case "pending":
      return 10;
    case "proposed":
      return 24;
    case "build_requested":
      return 48;
    case "validated":
      return 78;
    case "promoted":
      return 100;
    case "validation_failed":
    case "rejected":
    case "promotion_failed":
      return 100;
    case "rollback_requested":
      return 62;
    case "rolled_back":
      return 100;
    default:
      return 0;
  }
}

export function resolveAevatarStatusVisual(
  token: AevatarThemeSurfaceToken,
  domain: AevatarStatusDomain,
  status: string,
): AevatarStatusVisual {
  const tone = resolveAevatarSemanticTone(domain, status);

  return resolveAevatarToneVisual(token, tone);
}

function resolveAevatarToneVisual(
  token: AevatarThemeSurfaceToken,
  tone: AevatarSemanticTone,
): AevatarStatusVisual {
  if (tone === "success") {
    return {
      background: token.colorSuccessBg,
      borderColor: token.colorSuccessBorder,
      color: token.colorSuccessText,
      dotColor: token.colorSuccess,
    };
  }

  if (tone === "warning") {
    return {
      background: token.colorWarningBg,
      borderColor: token.colorWarningBorder,
      color: token.colorWarningText,
      dotColor: token.colorWarning,
    };
  }

  if (tone === "error") {
    return {
      background: token.colorErrorBg,
      borderColor: token.colorErrorBorder,
      color: token.colorErrorText,
      dotColor: token.colorError,
    };
  }

  if (tone === "info") {
    return {
      background: token.colorPrimaryBg,
      borderColor: token.colorPrimaryBorder,
      color: token.colorPrimary,
      dotColor: token.colorPrimary,
    };
  }

  return {
    background: token.colorFillAlter,
    borderColor: token.colorBorderSecondary,
    color: token.colorTextSecondary,
    dotColor: token.colorTextTertiary,
  };
}

export function resolveAevatarMetricVisual(
  token: AevatarThemeSurfaceToken,
  tone: AevatarSemanticTone = "default",
): AevatarMetricVisual {
  const isDefaultTone = tone === "default";
  const surface =
    isDefaultTone
      ? {
          background: token.colorBgContainer,
          borderColor: token.colorBorderSecondary,
          color: token.colorText,
          dotColor: token.colorTextSecondary,
        }
      : resolveAevatarToneVisual(token, tone);
  const useLightText = isDarkSurfaceColor(surface.background);

  return {
    ...surface,
    iconColor: useLightText ? token.colorTextLightSolid : surface.color,
    labelColor: useLightText
      ? token.colorTextLightSolid
      : isDefaultTone
        ? token.colorTextSecondary
        : token.colorTextSecondary,
    secondaryColor: useLightText
      ? token.colorTextLightSolid
      : isDefaultTone
        ? token.colorTextSecondary
        : token.colorTextSecondary,
    valueColor: useLightText
      ? token.colorTextLightSolid
      : isDefaultTone
        ? token.colorText
        : token.colorTextHeading,
  };
}

function isDarkSurfaceColor(color: string): boolean {
  const rgb = parseColorToRgb(color);
  if (!rgb) {
    return false;
  }

  const [red, green, blue] = rgb.map((channel) => {
    const normalized = channel / 255;
    return normalized <= 0.03928
      ? normalized / 12.92
      : ((normalized + 0.055) / 1.055) ** 2.4;
  });
  const luminance = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
  return luminance < 0.42;
}

function parseColorToRgb(color: string): [number, number, number] | null {
  const normalized = color.trim().toLowerCase();

  if (normalized.startsWith("#")) {
    const hex = normalized.slice(1);
    if (hex.length === 3) {
      return [
        Number.parseInt(hex[0] + hex[0], 16),
        Number.parseInt(hex[1] + hex[1], 16),
        Number.parseInt(hex[2] + hex[2], 16),
      ];
    }

    if (hex.length === 6) {
      return [
        Number.parseInt(hex.slice(0, 2), 16),
        Number.parseInt(hex.slice(2, 4), 16),
        Number.parseInt(hex.slice(4, 6), 16),
      ];
    }

    return null;
  }

  const match = normalized.match(
    /^rgba?\(\s*([0-9]+)\s*,\s*([0-9]+)\s*,\s*([0-9]+)(?:\s*,\s*[0-9.]+\s*)?\)$/,
  );
  if (!match) {
    return null;
  }

  return [
    Number.parseInt(match[1], 10),
    Number.parseInt(match[2], 10),
    Number.parseInt(match[3], 10),
  ];
}

export function buildAevatarViewportStyle(
  token: AevatarThemeSurfaceToken,
): CSSProperties {
  return {
    background: `linear-gradient(180deg, ${token.colorBgLayout} 0%, #eef2f6 100%)`,
    boxSizing: "border-box",
    display: "flex",
    flex: 1,
    flexDirection: "column",
    gap: AEVATAR_GLOBAL_UI_SPEC.tokens.sectionGap,
    height: "100%",
    minHeight: 0,
    overflowX: "hidden",
    overflowY: "auto",
    padding: `${AEVATAR_GLOBAL_UI_SPEC.tokens.sectionGap}px ${AEVATAR_GLOBAL_UI_SPEC.tokens.contentPadding}px ${AEVATAR_GLOBAL_UI_SPEC.tokens.contentPadding}px`,
  };
}

export function buildAevatarPanelStyle(
  token: AevatarThemeSurfaceToken,
  options?: {
    background?: string;
    minHeight?: number | string;
    overflow?: CSSProperties['overflow'];
    padding?: number | string;
  },
): CSSProperties {
  return {
    background: options?.background ?? token.colorBgContainer,
    border: `1px solid ${token.colorBorderSecondary}`,
    borderRadius: token.borderRadiusLG,
    boxShadow: token.boxShadowSecondary,
    minHeight: options?.minHeight ?? 0,
    overflow: options?.overflow ?? "hidden",
    padding: options?.padding ?? 0,
  };
}

export function buildAevatarMetricCardStyle(
  token: AevatarThemeSurfaceToken,
  tone: AevatarSemanticTone = "default",
): CSSProperties {
  const visual = resolveAevatarMetricVisual(token, tone);

  return {
    background: visual.background,
    border: `1px solid ${visual.borderColor}`,
    borderRadius: token.borderRadiusLG,
    color: visual.valueColor,
    display: "flex",
    flexDirection: "column",
    gap: 4,
    minWidth: 0,
    padding: "10px 12px",
  };
}

export function buildAevatarTagStyle(
  token: AevatarThemeSurfaceToken,
  domain: AevatarStatusDomain,
  status: string,
): CSSProperties {
  const visual = resolveAevatarStatusVisual(token, domain, status);

  return {
    alignItems: "center",
    background: visual.background,
    borderColor: visual.borderColor,
    borderRadius: 999,
    color: visual.color,
    display: "inline-flex",
    fontWeight: 600,
    gap: 6,
    lineHeight: 1.2,
    paddingInline: 10,
  };
}

export const aevatarDrawerBodyStyle: CSSProperties = {
  display: "flex",
  flexDirection: "column",
  height: "100%",
  minHeight: 0,
  overflow: "hidden",
  padding: 16,
};

export const aevatarDrawerScrollStyle: CSSProperties = {
  display: "flex",
  flex: 1,
  flexDirection: "column",
  gap: 16,
  minHeight: 0,
  overflowX: "hidden",
  overflowY: "auto",
  paddingRight: 4,
};
