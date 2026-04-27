import fs from "node:fs";
import path from "node:path";
import defaultSettings from "../config/defaultSettings";
import { layout } from "./app";

describe("layout menu collapse behavior", () => {
  beforeEach(() => {
    window.history.replaceState({}, "", "/teams");
  });

  it("keeps grouped navigation titles hidden in collapsed mode", () => {
    const runtimeLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });

    expect(runtimeLayout.menu).toMatchObject({
      collapsedWidth: 40,
      collapsedShowGroupTitle: false,
      collapsedShowTitle: false,
      type: "group",
    });
  });

  it("collapses the global menu for Studio create-member intent", () => {
    window.history.replaceState(
      {},
      "",
      "/studio?tab=studio&intent=create-member",
    );

    const runtimeLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });

    expect(runtimeLayout.defaultCollapsed).toBe(true);
    expect(runtimeLayout.collapsed).toBe(true);
  });

  it("leaves the global menu uncontrolled for ordinary Studio entry", () => {
    window.history.replaceState({}, "", "/studio?tab=studio");

    const runtimeLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });

    expect(runtimeLayout.defaultCollapsed).toBe(false);
    expect(runtimeLayout.collapsed).toBeUndefined();
  });

  it("updates the controlled global menu collapse state after SPA route changes", () => {
    window.history.replaceState({}, "", "/teams?scopeId=scope-a");
    const teamsLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });

    window.history.pushState({}, "", "/studio?tab=studio&intent=create-member");
    const studioLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });

    expect(teamsLayout.collapsed).toBeUndefined();
    expect(studioLayout.collapsed).toBe(true);
  });

  it("styles collapsed menu items without icons as visible tokens", () => {
    const globalStyles = fs.readFileSync(
      path.resolve(__dirname, "./global.less"),
      "utf8",
    );

    expect(globalStyles).toContain(".ant-menu-inline-collapsed-noicon");
    expect(globalStyles).toContain(
      ".ant-pro-sider.ant-layout-sider.ant-pro-sider-fixed.ant-pro-sider-collapsed",
    );
    expect(globalStyles).toContain("flex: 0 0 40px !important;");
    expect(globalStyles).toContain("inset-inline-start: 8px;");
    expect(globalStyles).toContain("text-transform: uppercase;");
    expect(globalStyles).toContain("min-width: 20px;");
  });
});
