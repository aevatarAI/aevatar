describe("console build feature definitions", () => {
  const originalWorkflowTemplatesFlag =
    process.env.AEVATAR_CONSOLE_WORKFLOW_TEMPLATES_ENABLED;

  afterEach(() => {
    jest.resetModules();
    if (originalWorkflowTemplatesFlag === undefined) {
      delete process.env.AEVATAR_CONSOLE_WORKFLOW_TEMPLATES_ENABLED;
    } else {
      process.env.AEVATAR_CONSOLE_WORKFLOW_TEMPLATES_ENABLED =
        originalWorkflowTemplatesFlag;
    }
  });

  it("injects the workflow templates flag into the browser build", () => {
    process.env.AEVATAR_CONSOLE_WORKFLOW_TEMPLATES_ENABLED = "true";
    jest.doMock("@umijs/max", () => ({
      defineConfig: (value: unknown) => value,
    }));

    const { default: config } = require("../../../config/config") as {
      default: {
        define?: Record<string, string | undefined>;
      };
    };

    expect(
      config.define?.[
        "process.env.AEVATAR_CONSOLE_WORKFLOW_TEMPLATES_ENABLED"
      ],
    ).toBe('"true"');
  });
});
