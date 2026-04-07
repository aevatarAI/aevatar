import {
  buildAevatarViewportStyle,
  type AevatarThemeSurfaceToken,
} from "./aevatarWorkbench";

const token: AevatarThemeSurfaceToken = {
  borderRadius: 4,
  borderRadiusLG: 8,
  boxShadowSecondary: "0 0 0 rgba(0, 0, 0, 0)",
  colorBgContainer: "#ffffff",
  colorBgElevated: "#ffffff",
  colorBgLayout: "#f5f5f5",
  colorBorder: "#d9d9d9",
  colorBorderSecondary: "#f0f0f0",
  colorError: "#ff4d4f",
  colorErrorBg: "#fff2f0",
  colorErrorBorder: "#ffccc7",
  colorErrorText: "#a8071a",
  colorFillAlter: "#fafafa",
  colorFillSecondary: "#f5f5f5",
  colorFillTertiary: "#f0f0f0",
  colorPrimary: "#1677ff",
  colorPrimaryBg: "#e6f4ff",
  colorPrimaryBorder: "#91caff",
  colorSuccess: "#52c41a",
  colorSuccessBg: "#f6ffed",
  colorSuccessBorder: "#b7eb8f",
  colorSuccessText: "#389e0d",
  colorText: "#1f2937",
  colorTextHeading: "#111827",
  colorTextLightSolid: "#ffffff",
  colorTextQuaternary: "#98a2b3",
  colorTextSecondary: "#4b5563",
  colorTextTertiary: "#667085",
  colorWarning: "#faad14",
  colorWarningBg: "#fffbe6",
  colorWarningBorder: "#ffe58f",
  colorWarningText: "#d48806",
};

describe("buildAevatarViewportStyle", () => {
  it("keeps the console viewport vertically scrollable", () => {
    const style = buildAevatarViewportStyle(token);

    expect(style.height).toBe("100%");
    expect(style.overflowX).toBe("hidden");
    expect(style.overflowY).toBe("auto");
    expect(style.overflow).toBeUndefined();
  });
});
