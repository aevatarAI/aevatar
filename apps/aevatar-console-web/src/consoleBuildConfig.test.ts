describe("console build config", () => {
  const originalEnv = process.env;

  beforeEach(() => {
    jest.resetModules();
    process.env = {
      ...originalEnv,
      AEVATAR_CONSOLE_PUBLIC_PATH: "/",
    };
  });

  afterEach(() => {
    process.env = originalEnv;
  });

  it("keeps dynamic Team routes on the SPA fallback instead of static route exports", () => {
    let config!: Record<string, unknown>;

    jest.isolateModules(() => {
      jest.doMock("@umijs/max", () => ({
        defineConfig: (value: Record<string, unknown>) => value,
      }));
      config = require("../config/config").default as Record<string, unknown>;
    });

    expect(config.base).toBe("/");
    expect(config.publicPath).toBe("/");
    expect(config).not.toHaveProperty("exportStatic");
  });
});
