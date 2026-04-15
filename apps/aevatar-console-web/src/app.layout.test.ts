import defaultSettings from "../config/defaultSettings";
import { layout } from "./app";

describe("layout menu collapse behavior", () => {
  it("keeps grouped navigation titles hidden in collapsed mode", () => {
    const runtimeLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });

    expect(runtimeLayout.menu).toMatchObject({
      collapsedShowGroupTitle: false,
      collapsedShowTitle: false,
      type: "group",
    });
  });
});
