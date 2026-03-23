import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { savePlaygroundDraft } from "@/shared/playground/playgroundDraft";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import StudioPage from "./index";

const PROMPT_HISTORY_STORAGE_KEY = "aevatar-console-playground-prompt-history";

type MockChildrenProps = {
  readonly children?: any;
};

type MockNotice =
  | {
      readonly type: "success" | "info" | "warning" | "error";
    }
  | null
  | undefined;

type MockValueEvent = {
  readonly target: {
    readonly value: string;
  };
};

const mockWorkflowDocument = {
  name: "workspace-demo",
  description: "Workspace workflow",
  roles: [
    {
      id: "assistant",
      name: "Assistant",
      systemPrompt: "Help the operator.",
      provider: "tornado",
      model: "gpt-test",
      connectors: ["web-search"],
    },
  ],
  steps: [
    {
      id: "draft_step",
      type: "llm_call",
      targetRole: "assistant",
      parameters: {
        prompt_prefix: "Draft the response",
      },
      next: "approve_step",
      branches: {},
    },
    {
      id: "approve_step",
      type: "human_approval",
      targetRole: "",
      parameters: {
        reviewer: "operator",
      },
      next: null,
      branches: {},
    },
  ],
};

function mockCloneValue<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

function mockBuildWorkflowYaml(document: typeof mockWorkflowDocument): string {
  const roleLines = document.roles.flatMap((role) => {
    const lines = [`  - id: ${role.id}`];
    if (role.name) {
      lines.push(`    name: ${role.name}`);
    }
    if (role.provider) {
      lines.push(`    provider: ${role.provider}`);
    }
    if (role.model) {
      lines.push(`    model: ${role.model}`);
    }
    return lines;
  });

  const stepLines = document.steps.flatMap((step) => {
    const lines = [`  - id: ${step.id}`, `    type: ${step.type}`];
    if (step.targetRole) {
      lines.push(`    targetRole: ${step.targetRole}`);
    }
    if (step.next) {
      lines.push(`    next: ${step.next}`);
    }
    return lines;
  });

  return [
    `name: ${document.name}`,
    "roles:",
    ...roleLines,
    "steps:",
    ...stepLines,
    "",
  ].join("\n");
}

let mockParsedDocument = mockCloneValue(mockWorkflowDocument);
let mockWorkflowFile: any;
let mockConnectorCatalog: any;
let mockConnectorDraftResponse: any;
let mockRoleCatalog: any;
let mockRoleDraftResponse: any;
let mockSettings: any;

function resetMockState(): void {
  mockParsedDocument = mockCloneValue(mockWorkflowDocument);
  mockWorkflowFile = {
    workflowId: "workflow-1",
    name: "workspace-demo",
    fileName: "workspace-demo.yaml",
    filePath: "/tmp/workflows/workspace-demo.yaml",
    directoryId: "dir-1",
    directoryLabel: "Workspace",
    yaml: mockBuildWorkflowYaml(mockParsedDocument),
    findings: [],
    updatedAtUtc: "2026-03-18T00:00:00Z",
    document: mockParsedDocument,
  };
  mockConnectorCatalog = {
    homeDirectory: "/tmp/.aevatar",
    filePath: "/tmp/.aevatar/connectors.json",
    fileExists: true,
    connectors: [
      {
        name: "web-search",
        type: "http",
        enabled: true,
        timeoutMs: 10000,
        retry: 1,
        http: {
          baseUrl: "https://example.test",
          allowedMethods: ["GET"],
          allowedPaths: ["/search"],
          allowedInputKeys: ["query"],
          defaultHeaders: {},
        },
        cli: {
          command: "",
          fixedArguments: [],
          allowedOperations: [],
          allowedInputKeys: [],
          workingDirectory: "",
          environment: {},
        },
        mcp: {
          serverName: "",
          command: "",
          arguments: [],
          environment: {},
          defaultTool: "",
          allowedTools: [],
          allowedInputKeys: [],
        },
      },
    ],
  };
  mockConnectorDraftResponse = {
    homeDirectory: "/tmp/.aevatar",
    filePath: "/tmp/.aevatar/connectors.draft.json",
    fileExists: false,
    updatedAtUtc: null,
    draft: null,
  };
  mockRoleCatalog = {
    homeDirectory: "/tmp/.aevatar",
    filePath: "/tmp/.aevatar/roles.json",
    fileExists: true,
    roles: [
      {
        id: "assistant",
        name: "Assistant",
        systemPrompt: "Help the operator.",
        provider: "tornado",
        model: "gpt-test",
        connectors: ["web-search"],
      },
    ],
  };
  mockRoleDraftResponse = {
    homeDirectory: "/tmp/.aevatar",
    filePath: "/tmp/.aevatar/roles.draft.json",
    fileExists: false,
    updatedAtUtc: null,
    draft: null,
  };
  mockSettings = {
    runtimeBaseUrl: "http://127.0.0.1:5100",
    defaultProviderName: "tornado",
    providerTypes: [
      {
        id: "openai",
        displayName: "OpenAI",
        category: "llm",
        description: "OpenAI compatible provider",
        recommended: true,
        defaultEndpoint: "https://api.openai.test",
        defaultModel: "gpt-4.1-mini",
      },
    ],
    providers: [
      {
        providerName: "tornado",
        providerType: "openai",
        displayName: "Tornado",
        category: "llm",
        description: "Local provider",
        model: "gpt-test",
        endpoint: "http://127.0.0.1:5100",
        apiKey: "",
        apiKeyConfigured: true,
      },
    ],
  };
}

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAppContext: jest.fn(async () => ({
      mode: "proxy",
      scopeId: null,
      scopeResolved: false,
      scopeSource: "",
      workflowStorageMode: "workspace",
      features: {
        publishedWorkflows: true,
        scripts: false,
      },
    })),
    getAuthSession: jest.fn(async () => ({
      enabled: false,
      authenticated: false,
      providerDisplayName: "NyxID",
    })),
    getWorkspaceSettings: jest.fn(async () => ({
      runtimeBaseUrl: "http://127.0.0.1:5100",
      directories: [
        {
          directoryId: "dir-1",
          label: "Workspace",
          path: "/tmp/workflows",
          isBuiltIn: false,
        },
      ],
    })),
    listWorkflows: jest.fn(async () => [
      {
        workflowId: "workflow-1",
        name: "workspace-demo",
        description: "Workspace workflow",
        fileName: "workspace-demo.yaml",
        filePath: "/tmp/workflows/workspace-demo.yaml",
        directoryId: "dir-1",
        directoryLabel: "Workspace",
        stepCount: 2,
        hasLayout: true,
        updatedAtUtc: "2026-03-18T00:00:00Z",
      },
    ]),
    getTemplateWorkflow: jest.fn(async () => ({
      catalog: {
        name: "published-demo",
        description: "Published demo workflow",
      },
      yaml: "name: published-demo\ndescription: Published demo workflow\nsteps: []\n",
      definition: {
        name: "published-demo",
        description: "Published demo workflow",
        closedWorldMode: false,
        roles: [],
        steps: [],
      },
      edges: [],
    })),
    getWorkflow: jest.fn(async () => mockWorkflowFile),
    saveWorkflow: jest.fn(
      async (input: {
        workflowId?: string;
        directoryId: string;
        workflowName: string;
        fileName?: string | null;
        yaml: string;
      }) => {
        mockWorkflowFile = {
          ...mockWorkflowFile,
          workflowId: input.workflowId || mockWorkflowFile.workflowId,
          name: input.workflowName,
          fileName: input.fileName || mockWorkflowFile.fileName,
          directoryId: input.directoryId,
          yaml: input.yaml,
          updatedAtUtc: "2026-03-18T00:05:00Z",
          document: {
            ...mockWorkflowFile.document,
            name: input.workflowName,
          },
        };

        return mockWorkflowFile;
      }
    ),
    parseYaml: jest.fn(async (input: { yaml: string }) => ({
      document: input.yaml.includes("name: legacy_draft")
        ? {
            name: "legacy_draft",
            description: "",
            roles: [],
            steps: [],
          }
        : input.yaml.includes("name: published-demo")
        ? {
            name: "published-demo",
            description: "Published demo workflow",
            roles: [],
            steps: [],
          }
        : input.yaml.includes("name: draft")
        ? {
            name: "draft",
            description: "",
            roles: [],
            steps: [],
          }
        : input.yaml.includes("name: ai-generated")
        ? {
            name: "ai-generated",
            description: "Generated by Studio AI",
            roles: [],
            steps: [],
          }
        : mockParsedDocument,
      findings: [],
    })),
    serializeYaml: jest.fn(
      async (input: { document: typeof mockWorkflowDocument }) => {
        mockParsedDocument = mockCloneValue(input.document);
        mockWorkflowFile = {
          ...mockWorkflowFile,
          yaml: mockBuildWorkflowYaml(mockParsedDocument),
          document: mockParsedDocument,
        };

        return {
          yaml: mockWorkflowFile.yaml,
          document: mockParsedDocument,
          findings: [],
        };
      }
    ),
    listExecutions: jest.fn(async () => [
      {
        executionId: "execution-1",
        workflowName: "workspace-demo",
        prompt: "Run the demo workflow.",
        status: "running",
        startedAtUtc: "2026-03-18T00:00:00Z",
        completedAtUtc: null,
        actorId: "actor-1",
        error: null,
      },
    ]),
    getExecution: jest.fn(async (executionId: string) => ({
      executionId,
      workflowName: "workspace-demo",
      prompt:
        executionId === "execution-2"
          ? "Run the active draft from Studio."
          : "Run the demo workflow.",
      status: "running",
      startedAtUtc:
        executionId === "execution-2"
          ? "2026-03-18T00:06:00Z"
          : "2026-03-18T00:00:00Z",
      completedAtUtc: null,
      actorId: executionId === "execution-2" ? "actor-2" : "actor-1",
      error: null,
      frames: [],
    })),
    startExecution: jest.fn(
      async (input: { workflowName: string; prompt: string }) => ({
        executionId: "execution-2",
        workflowName: input.workflowName,
        prompt: input.prompt,
        runtimeBaseUrl: "http://127.0.0.1:5100",
        status: "running",
        startedAtUtc: "2026-03-18T00:06:00Z",
        completedAtUtc: null,
        actorId: "actor-2",
        error: null,
        frames: [
          {
            receivedAtUtc: "2026-03-18T00:06:01Z",
            payload: '{"event":"started"}',
          },
        ],
      })
    ),
    stopExecution: jest.fn(async (executionId: string) => ({
      executionId,
      workflowName: "workspace-demo",
      prompt: "Run the demo workflow.",
      runtimeBaseUrl: "http://127.0.0.1:5100",
      status: "stopped",
      startedAtUtc: "2026-03-18T00:00:00Z",
      completedAtUtc: "2026-03-18T00:07:00Z",
      actorId: "actor-1",
      error: null,
      frames: [],
    })),
    getConnectorCatalog: jest.fn(async () => ({
      ...mockConnectorCatalog,
    })),
    getConnectorDraft: jest.fn(async () => ({
      ...mockConnectorDraftResponse,
    })),
    saveConnectorDraft: jest.fn(async (input: { draft: any }) => {
      mockConnectorDraftResponse = {
        ...mockConnectorDraftResponse,
        fileExists: true,
        updatedAtUtc: "2026-03-18T00:03:00Z",
        draft: input.draft,
      };
      return mockConnectorDraftResponse;
    }),
    deleteConnectorDraft: jest.fn(async () => {
      mockConnectorDraftResponse = {
        ...mockConnectorDraftResponse,
        fileExists: false,
        updatedAtUtc: null,
        draft: null,
      };
    }),
    saveConnectorCatalog: jest.fn(
      async (input: { connectors: typeof mockConnectorCatalog.connectors }) => {
        mockConnectorCatalog = {
          ...mockConnectorCatalog,
          connectors: input.connectors,
        };
        return mockConnectorCatalog;
      }
    ),
    importConnectorCatalog: jest.fn(async (file: File) => {
      mockConnectorCatalog = {
        ...mockConnectorCatalog,
        connectors: [
          {
            name: "imported-search",
            type: "http",
            enabled: true,
            timeoutMs: 15000,
            retry: 2,
            http: {
              baseUrl: "https://imported.example.test",
              allowedMethods: ["POST"],
              allowedPaths: ["/catalog"],
              allowedInputKeys: ["query"],
              defaultHeaders: {},
            },
            cli: {
              command: "",
              fixedArguments: [],
              allowedOperations: [],
              allowedInputKeys: [],
              workingDirectory: "",
              environment: {},
            },
            mcp: {
              serverName: "",
              command: "",
              arguments: [],
              environment: {},
              defaultTool: "",
              allowedTools: [],
              allowedInputKeys: [],
            },
          },
        ],
      };
      return {
        ...mockConnectorCatalog,
        sourceFilePath: file.name,
        sourceFileExists: true,
        importedCount: mockConnectorCatalog.connectors.length,
      };
    }),
    getRoleCatalog: jest.fn(async () => ({
      ...mockRoleCatalog,
    })),
    getRoleDraft: jest.fn(async () => ({
      ...mockRoleDraftResponse,
    })),
    saveRoleDraft: jest.fn(async (input: { draft: any }) => {
      mockRoleDraftResponse = {
        ...mockRoleDraftResponse,
        fileExists: true,
        updatedAtUtc: "2026-03-18T00:03:00Z",
        draft: input.draft,
      };
      return mockRoleDraftResponse;
    }),
    deleteRoleDraft: jest.fn(async () => {
      mockRoleDraftResponse = {
        ...mockRoleDraftResponse,
        fileExists: false,
        updatedAtUtc: null,
        draft: null,
      };
    }),
    saveRoleCatalog: jest.fn(
      async (input: { roles: typeof mockRoleCatalog.roles }) => {
        mockRoleCatalog = {
          ...mockRoleCatalog,
          roles: input.roles,
        };
        return mockRoleCatalog;
      }
    ),
    importRoleCatalog: jest.fn(async (file: File) => {
      mockRoleCatalog = {
        ...mockRoleCatalog,
        roles: [
          {
            id: "reviewer",
            name: "Reviewer",
            systemPrompt: "Review imported workflow outputs carefully.",
            provider: "tornado",
            model: "gpt-review",
            connectors: ["imported-search"],
          },
        ],
      };
      return {
        ...mockRoleCatalog,
        sourceFilePath: file.name,
        sourceFileExists: true,
        importedCount: mockRoleCatalog.roles.length,
      };
    }),
    getSettings: jest.fn(async () => ({
      ...mockSettings,
    })),
    saveSettings: jest.fn(
      async (input: {
        runtimeBaseUrl?: string;
        defaultProviderName?: string;
        providers?: typeof mockSettings.providers;
      }) => {
        mockSettings = {
          ...mockSettings,
          runtimeBaseUrl: input.runtimeBaseUrl || mockSettings.runtimeBaseUrl,
          defaultProviderName:
            input.defaultProviderName || mockSettings.defaultProviderName,
          providers: input.providers || mockSettings.providers,
        };
        return mockSettings;
      }
    ),
    testRuntimeConnection: jest.fn(
      async (input: { runtimeBaseUrl?: string }) => ({
        runtimeBaseUrl: input.runtimeBaseUrl || mockSettings.runtimeBaseUrl,
        reachable: true,
        checkedUrl: `${
          input.runtimeBaseUrl || mockSettings.runtimeBaseUrl
        }/health`,
        statusCode: 200,
        message: "Runtime responded with 200 OK.",
      })
    ),
    addWorkflowDirectory: jest.fn(async () => ({
      runtimeBaseUrl: "http://127.0.0.1:5100",
      directories: [
        {
          directoryId: "dir-1",
          label: "Workspace",
          path: "/tmp/workflows",
          isBuiltIn: false,
        },
      ],
    })),
    removeWorkflowDirectory: jest.fn(async () => undefined),
    authorWorkflow: jest.fn(
      async (
        _input: { prompt: string },
        options?: {
          onText?: (text: string) => void;
          onReasoning?: (text: string) => void;
        }
      ) => {
        options?.onReasoning?.("Thinking through the workflow structure.");
        options?.onText?.("name: ai-generated\nsteps: []\n");
        return "name: ai-generated\nsteps: []\n";
      }
    ),
  },
}));

jest.mock("./components/StudioBootstrapGate", () => ({
  __esModule: true,
  default: ({ children }: MockChildrenProps) => children,
}));

jest.mock("./components/StudioShell", () => ({
  __esModule: true,
  default: ({ children }: MockChildrenProps) => {
    const React = require("react");
    return React.createElement(
      "div",
      null,
      React.createElement("div", null, "Workbench"),
      children
    );
  },
}));

jest.mock("./components/StudioWorkbenchSections", () => {
  const React = require("react");

  const renderNoticeTitle = (
    key: string,
    notice: MockNotice,
    successTitle: string,
    errorTitle: string
  ) => {
    if (!notice) {
      return null;
    }

    return React.createElement(
      "div",
      { key },
      notice.type === "error" ? errorTitle : successTitle
    );
  };

  const StudioWorkflowsPage = (props: any) =>
    React.createElement("div", null, [
      React.createElement("h2", { key: "title" }, "Workflows"),
      React.createElement("div", { key: "draft" }, "Current draft"),
      React.createElement("input", {
        key: "search",
        placeholder: "Search workflows",
        value: props.workflowSearch ?? "",
        onChange: (event: MockValueEvent) =>
          props.onSetWorkflowSearch?.(event.target.value),
      }),
      ...(props.workflows.data ?? []).map((workflow: any) =>
        React.createElement(
          "button",
          {
            key: workflow.workflowId,
            type: "button",
            onClick: () => props.onOpenWorkflow?.(workflow.workflowId),
          },
          workflow.name
        )
      ),
      React.createElement(
        "button",
        {
          key: "blank",
          type: "button",
          onClick: () => props.onStartBlankDraft?.(),
        },
        "Start blank draft"
      ),
    ]);

  const StudioEditorPage = (props: any) => {
    const [runOpen, setRunOpen] = React.useState(false);
    const [askAiOpen, setAskAiOpen] = React.useState(false);
    const title =
      props.draftMode === "new"
        ? props.draftWorkflowName === "legacy_draft"
          ? "Imported local draft"
          : "Blank Studio draft"
        : props.templateWorkflowName
        ? "Published template draft"
        : "Current draft";

    if (!props.draftYaml) {
      return React.createElement("div", null, "No draft loaded");
    }

    return React.createElement(
      "div",
      null,
      [
        React.createElement("div", { key: "title" }, title),
        React.createElement("div", { key: "graph-title" }, "Workflow graph"),
        renderNoticeTitle(
          "save-notice",
          props.saveNotice,
          "Workflow saved",
          "Workflow save failed"
        ),
        renderNoticeTitle(
          "run-notice",
          props.runNotice,
          "Run started",
          "Run failed"
        ),
        renderNoticeTitle(
          "ask-ai-notice",
          props.askAiNotice,
          "Studio AI generation updated the draft",
          "Studio AI generation failed"
        ),
        React.createElement("input", {
          key: "workflow-name",
          "aria-label": "Workflow name",
          value: props.draftWorkflowName ?? "",
          onChange: (event: MockValueEvent) =>
            props.onSetDraftWorkflowName?.(event.target.value),
        }),
        React.createElement("textarea", {
          key: "workflow-yaml",
          "aria-label": "Workflow YAML",
          value: props.draftYaml ?? "",
          onChange: (event: MockValueEvent) =>
            props.onSetDraftYaml?.(event.target.value),
        }),
        React.createElement(
          "button",
          {
            key: "save",
            type: "button",
            onClick: () => props.onSaveDraft?.(),
          },
          "Save to workspace"
        ),
        React.createElement(
          "button",
          {
            key: "yaml",
            type: "button",
            onClick: () => props.onSetInspectorTab?.("yaml"),
          },
          "YAML"
        ),
        props.inspectorTab === "yaml"
          ? React.createElement("div", { key: "yaml-panel" }, [
              React.createElement(
                "div",
                { key: "yaml-title" },
                "Validated by Studio editor"
              ),
              React.createElement("textarea", {
                key: "yaml-view",
                "aria-label": "Studio workflow yaml panel",
                readOnly: true,
                value: props.draftYaml ?? "",
              }),
            ])
          : null,
        props.recentPromptHistory?.length
          ? React.createElement("div", { key: "recent-prompts" }, [
              React.createElement("div", { key: "label" }, "Recent prompts"),
              React.createElement(
                "button",
                {
                  key: "reuse",
                  type: "button",
                  onClick: () =>
                    props.onReusePrompt?.(props.recentPromptHistory[0].prompt),
                },
                "Reuse prompt"
              ),
            ])
          : null,
        React.createElement(
          "button",
          {
            key: "run-toggle",
            type: "button",
            onClick: () => setRunOpen(true),
          },
          "Run"
        ),
        runOpen
          ? React.createElement("div", { key: "run-dialog" }, [
              React.createElement("textarea", {
                key: "run-prompt",
                "aria-label": "Studio execution prompt",
                value: props.runPrompt ?? "",
                onChange: (event: MockValueEvent) =>
                  props.onRunPromptChange?.(event.target.value),
              }),
              React.createElement(
                "button",
                {
                  key: "run-submit",
                  type: "button",
                  onClick: () => props.onStartExecution?.(),
                },
                "Run"
              ),
            ])
          : null,
        React.createElement(
          "button",
          {
            key: "ask-ai-toggle",
            type: "button",
            onClick: () => setAskAiOpen(true),
          },
          "Open Ask AI"
        ),
        askAiOpen
          ? React.createElement("div", { key: "ask-ai-panel" }, [
              React.createElement("textarea", {
                key: "ask-ai-prompt",
                "aria-label": "Studio AI workflow prompt",
                value: props.askAiPrompt ?? "",
                onChange: (event: MockValueEvent) =>
                  props.onAskAiPromptChange?.(event.target.value),
              }),
              React.createElement(
                "button",
                {
                  key: "ask-ai-submit",
                  type: "button",
                  onClick: () => props.onAskAiGenerate?.(),
                },
                "Generate"
              ),
            ])
          : null,
      ].filter(Boolean)
    );
  };

  const StudioExecutionPage = (props: any) =>
    React.createElement(
      "div",
      null,
      [
        React.createElement("div", { key: "logs" }, "Logs"),
        renderNoticeTitle(
          "execution-notice",
          props.executionNotice,
          "Execution stop requested",
          "Execution stop failed"
        ),
        React.createElement(
          "button",
          {
            key: "stop",
            type: "button",
            onClick: () => props.onStopExecution?.(),
          },
          "Stop"
        ),
      ].filter(Boolean)
    );

  const StudioRolesPage = (props: any) => {
    const selectedRole =
      props.selectedRole ?? props.roleCatalogDraft?.[0] ?? null;
    return React.createElement(
      "div",
      null,
      [
        React.createElement("input", {
          key: "role-import",
          "aria-label": "Import role catalog file",
          type: "file",
          onChange: props.onRoleImportChange,
        }),
        React.createElement("div", { key: "label" }, "Saved roles"),
        React.createElement("input", {
          key: "search",
          placeholder: "Search roles",
          value: props.roleSearch ?? "",
          onChange: (event: MockValueEvent) =>
            props.onRoleSearchChange?.(event.target.value),
        }),
        selectedRole
          ? React.createElement("textarea", {
              key: "system-prompt",
              "aria-label": "System prompt",
              value: selectedRole.systemPrompt ?? "",
              onChange: (event: MockValueEvent) =>
                props.onUpdateRoleCatalog?.(selectedRole.key, (role: any) => ({
                  ...role,
                  systemPrompt: event.target.value,
                })),
            })
          : null,
        React.createElement(
          "button",
          {
            key: "use",
            type: "button",
            onClick: () => props.onApplyRoleToWorkflow?.(selectedRole?.key),
          },
          "Use"
        ),
        React.createElement(
          "button",
          {
            key: "save",
            type: "button",
            onClick: () => props.onSaveRoles?.(),
          },
          "Save"
        ),
      ].filter(Boolean)
    );
  };

  const StudioConnectorsPage = (props: any) => {
    const selectedConnector =
      props.selectedConnector ?? props.connectorCatalogDraft?.[0] ?? null;
    return React.createElement(
      "div",
      null,
      [
        React.createElement("input", {
          key: "connector-import",
          "aria-label": "Import connector catalog file",
          type: "file",
          onChange: props.onConnectorImportChange,
        }),
        React.createElement("input", {
          key: "search",
          placeholder: "Search connectors",
          value: props.connectorSearch ?? "",
          onChange: (event: MockValueEvent) =>
            props.onConnectorSearchChange?.(event.target.value),
        }),
        selectedConnector
          ? React.createElement("input", {
              key: "base-url",
              "aria-label": "Base URL",
              value: selectedConnector.http?.baseUrl ?? "",
              onChange: (event: MockValueEvent) =>
                props.onUpdateConnectorCatalog?.(
                  selectedConnector.key,
                  (connector: any) => ({
                    ...connector,
                    http: {
                      ...connector.http,
                      baseUrl: event.target.value,
                    },
                  })
                ),
            })
          : null,
        React.createElement(
          "button",
          {
            key: "save",
            type: "button",
            onClick: () => props.onSaveConnectors?.(),
          },
          "Save"
        ),
      ].filter(Boolean)
    );
  };

  const StudioSettingsPage = (props: any) =>
    React.createElement(
      "div",
      null,
      [
        React.createElement("div", { key: "label" }, "Provider settings"),
        React.createElement(
          "div",
          { key: "selected-provider" },
          `Selected provider: ${props.selectedProvider?.providerName ?? "none"}`
        ),
        React.createElement("input", {
          key: "runtime-base-url",
          "aria-label": "Studio runtime base URL",
          value: props.settingsDraft?.runtimeBaseUrl ?? "",
          disabled: props.hostMode !== "proxy",
          onChange: (event: MockValueEvent) => {
            const nextValue = event.target.value;
            props.onSetSettingsDraft?.(
              props.settingsDraft
                ? {
                    ...props.settingsDraft,
                    runtimeBaseUrl: nextValue,
                  }
                : props.settingsDraft
            );
          },
        }),
        React.createElement(
          "button",
          {
            key: "save",
            type: "button",
            disabled: !props.settingsDirty,
            onClick: () => props.onSaveSettings?.(),
          },
          "Save settings"
        ),
        React.createElement(
          "button",
          {
            key: "test-runtime",
            type: "button",
            onClick: () => props.onTestRuntime?.(),
          },
          props.hostMode === "proxy" ? "Test runtime" : "Check host runtime"
        ),
        renderNoticeTitle(
          "settings-notice",
          props.settingsNotice,
          "Settings updated",
          "Settings update failed"
        ),
      ].filter(Boolean)
    );

  return {
    __esModule: true,
    StudioConnectorsPage,
    StudioEditorPage,
    StudioExecutionPage,
    StudioRolesPage,
    StudioSettingsPage,
    StudioWorkspaceAlerts: () => null,
    StudioWorkflowsPage,
  };
});

function renderStudioPage(route = "/studio") {
  window.history.pushState({}, "", route);
  return renderWithQueryClient(React.createElement(StudioPage));
}

describe("StudioPage", () => {
  beforeEach(() => {
    window.history.pushState({}, "", "/studio");
    window.localStorage.clear();
    resetMockState();
    jest.clearAllMocks();
  });

  it("loads workspace data and shows the Studio workbench by default", async () => {
    renderStudioPage("/studio");

    await waitFor(() => {
      expect(studioApi.getAppContext).toHaveBeenCalled();
      expect(studioApi.listWorkflows).toHaveBeenCalled();
      expect(studioApi.listExecutions).toHaveBeenCalled();
      expect(studioApi.getConnectorCatalog).toHaveBeenCalled();
      expect(studioApi.getRoleCatalog).toHaveBeenCalled();
      expect(studioApi.getSettings).toHaveBeenCalled();
    });

    expect(await screen.findByText("workspace-demo")).toBeTruthy();
    expect(screen.getByText("Workbench")).toBeTruthy();
    expect(screen.getByText("Workflows")).toBeTruthy();
    expect(screen.getByText("Current draft")).toBeTruthy();
    expect(screen.getByPlaceholderText("Search workflows")).toBeTruthy();
  });

  it("saves edited workflow drafts back to the Studio workspace API", async () => {
    renderStudioPage("/studio?workflow=workflow-1&tab=studio");

    const editor = await screen.findByLabelText("Workflow YAML");
    fireEvent.change(editor, {
      target: {
        value: "name: workspace-demo\nsteps:\n  - id: approve_step\n",
      },
    });

    fireEvent.click(screen.getByRole("button", { name: "Save to workspace" }));

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowId: "workflow-1",
          directoryId: "dir-1",
          workflowName: "workspace-demo",
          yaml: "name: workspace-demo\nsteps:\n  - id: approve_step\n",
        })
      );
    });

    expect(await screen.findByText("Workflow saved")).toBeTruthy();
  });

  it("starts a blank draft when the Studio route requests draft mode", async () => {
    renderStudioPage("/studio?draft=new");

    expect(await screen.findByText("Blank Studio draft")).toBeTruthy();
    expect(
      (await screen.findByLabelText("Workflow name")) as HTMLInputElement
    ).toHaveValue("draft");
    expect(
      (await screen.findByLabelText("Workflow YAML")) as HTMLTextAreaElement
    ).toHaveValue("name: draft\nsteps: []\n");
  });

  it("hydrates a Studio draft from the legacy playground handoff", async () => {
    savePlaygroundDraft({
      yaml: "name: legacy_draft\nsteps:\n  - id: legacy_step\n",
      sourceWorkflow: "legacy_draft",
      prompt: "Carry this draft into Studio.",
    });

    renderStudioPage("/studio?draft=new&legacy=playground");

    expect(await screen.findByText("Imported local draft")).toBeTruthy();
    expect(
      (await screen.findByLabelText("Workflow name")) as HTMLInputElement
    ).toHaveValue("legacy_draft");
    expect(
      (await screen.findByLabelText("Workflow YAML")) as HTMLTextAreaElement
    ).toHaveValue("name: legacy_draft\nsteps:\n  - id: legacy_step\n");
  });

  it("hydrates the Studio execution prompt from the route query", async () => {
    renderStudioPage(
      "/studio?template=published-demo&prompt=Continue%20this%20workflow%20in%20Studio"
    );

    await waitFor(() => {
      expect(studioApi.getTemplateWorkflow).toHaveBeenCalledWith(
        "published-demo"
      );
    });

    fireEvent.click(await screen.findByRole("button", { name: "Run" }));

    expect(
      (await screen.findByLabelText(
        "Studio execution prompt"
      )) as HTMLTextAreaElement
    ).toHaveValue("Continue this workflow in Studio");
  });

  it("reuses legacy prompt history inside Studio", async () => {
    window.localStorage.setItem(
      PROMPT_HISTORY_STORAGE_KEY,
      JSON.stringify([
        {
          id: "workspace-demo:Review the current draft carefully.",
          prompt: "Review the current draft carefully.",
          workflowName: "workspace-demo",
          updatedAt: "2026-03-18T00:02:00Z",
        },
      ])
    );

    renderStudioPage("/studio?draft=new");

    expect(await screen.findByText("Recent prompts")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Reuse prompt" }));
    fireEvent.click(screen.getByRole("button", { name: "Run" }));

    expect(
      (await screen.findByLabelText(
        "Studio execution prompt"
      )) as HTMLTextAreaElement
    ).toHaveValue("Review the current draft carefully.");
  });

  it("starts a Studio execution from the active draft", async () => {
    renderStudioPage("/studio?workflow=workflow-1&tab=studio");

    fireEvent.click(await screen.findByRole("button", { name: "Run" }));
    fireEvent.change(await screen.findByLabelText("Studio execution prompt"), {
      target: {
        value: "Run the active draft from Studio.",
      },
    });
    fireEvent.click(screen.getAllByRole("button", { name: "Run" }).at(-1)!);

    await waitFor(() => {
      expect(studioApi.startExecution).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowName: "workspace-demo",
          prompt: "Run the active draft from Studio.",
          workflowYamls: [expect.stringContaining("name: workspace-demo")],
          runtimeBaseUrl: "http://127.0.0.1:5100",
        })
      );
    });

    expect(await screen.findByText("Logs")).toBeTruthy();
  });

  it("stops the selected Studio execution from the execution view", async () => {
    renderStudioPage(
      "/studio?workflow=workflow-1&tab=executions&execution=execution-1"
    );

    expect(await screen.findByText("Logs")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Stop" }));

    await waitFor(() => {
      expect(studioApi.stopExecution).toHaveBeenCalledWith("execution-1", {
        reason: "user requested stop",
      });
    });

    expect(await screen.findByText("Execution stop requested")).toBeTruthy();
  });

  it("generates workflow YAML with Studio AI and applies it to the draft", async () => {
    renderStudioPage("/studio?draft=new&tab=studio");

    fireEvent.click(await screen.findByRole("button", { name: "Open Ask AI" }));
    fireEvent.change(
      await screen.findByLabelText("Studio AI workflow prompt"),
      {
        target: {
          value: "Create a short review workflow",
        },
      }
    );
    fireEvent.click(screen.getByRole("button", { name: "Generate" }));

    await waitFor(() => {
      expect(studioApi.authorWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          prompt: "Create a short review workflow",
        }),
        expect.any(Object)
      );
    });

    fireEvent.click(screen.getByRole("button", { name: "YAML" }));

    await waitFor(() => {
      expect(
        (
          screen.getByLabelText(
            "Studio workflow yaml panel"
          ) as HTMLTextAreaElement
        ).value.trim()
      ).toBe("name: ai-generated\nsteps: []");
    });
  });

  it("saves edited role catalog entries through the Studio API", async () => {
    renderStudioPage("/studio?tab=roles");

    expect(await screen.findByPlaceholderText("Search roles")).toBeTruthy();
    expect(await screen.findByDisplayValue("Help the operator.")).toBeTruthy();

    fireEvent.change(screen.getByLabelText("System prompt"), {
      target: {
        value: "Answer carefully and keep responses concise.",
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(studioApi.saveRoleCatalog).toHaveBeenCalledWith(
        expect.objectContaining({
          roles: expect.arrayContaining([
            expect.objectContaining({
              id: "assistant",
              systemPrompt: "Answer carefully and keep responses concise.",
            }),
          ]),
        })
      );
    });
  });

  it("imports role catalog entries through the Studio upload API", async () => {
    renderStudioPage("/studio?tab=roles");

    const file = new File(['{"roles":[]}'], "roles-import.json", {
      type: "application/json",
    });

    fireEvent.change(await screen.findByLabelText("Import role catalog file"), {
      target: {
        files: [file],
      },
    });

    await waitFor(() => {
      expect(studioApi.importRoleCatalog).toHaveBeenCalledWith(file);
    });

    expect(
      await screen.findByDisplayValue(
        "Review imported workflow outputs carefully."
      )
    ).toBeTruthy();
  });

  it("saves edited connector catalog entries through the Studio API", async () => {
    renderStudioPage("/studio?tab=connectors");

    expect(
      await screen.findByPlaceholderText("Search connectors")
    ).toBeTruthy();
    expect(
      await screen.findByDisplayValue("https://example.test")
    ).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Base URL"), {
      target: {
        value: "https://console.example.test",
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(studioApi.saveConnectorCatalog).toHaveBeenCalledWith(
        expect.objectContaining({
          connectors: expect.arrayContaining([
            expect.objectContaining({
              name: "web-search",
              http: expect.objectContaining({
                baseUrl: "https://console.example.test",
              }),
            }),
          ]),
        })
      );
    });
  });

  it("imports connector catalog entries through the Studio upload API", async () => {
    renderStudioPage("/studio?tab=connectors");

    const file = new File(['{"connectors":[]}'], "connectors-import.json", {
      type: "application/json",
    });

    fireEvent.change(
      await screen.findByLabelText("Import connector catalog file"),
      {
        target: {
          files: [file],
        },
      }
    );

    await waitFor(() => {
      expect(studioApi.importConnectorCatalog).toHaveBeenCalledWith(file);
    });

    expect(
      await screen.findByDisplayValue("https://imported.example.test")
    ).toBeTruthy();
  });

  it("saves editable Studio settings and provider configuration", async () => {
    renderStudioPage("/studio?tab=settings");

    expect(await screen.findByText("Provider settings")).toBeTruthy();
    expect(await screen.findByText("Selected provider: tornado")).toBeTruthy();

    const runtimeBaseUrlInput = await screen.findByLabelText(
      "Studio runtime base URL"
    );
    fireEvent.change(runtimeBaseUrlInput, {
      target: {
        value: "http://127.0.0.1:5111",
      },
    });
    await waitFor(() => {
      expect(runtimeBaseUrlInput).toHaveValue("http://127.0.0.1:5111");
    });
    fireEvent.click(screen.getByRole("button", { name: "Save settings" }));

    await waitFor(() => {
      expect(studioApi.saveSettings).toHaveBeenCalledWith(
        expect.objectContaining({
          runtimeBaseUrl: "http://127.0.0.1:5111",
          defaultProviderName: "tornado",
        })
      );
    });

    expect(await screen.findByText("Settings updated")).toBeTruthy();
  });

  it("treats runtime connection as host-managed in embedded mode", async () => {
    (studioApi.getAppContext as jest.Mock).mockResolvedValueOnce({
      mode: "embedded",
      scopeId: null,
      scopeResolved: false,
      scopeSource: "",
      workflowStorageMode: "workspace",
      features: {
        publishedWorkflows: true,
        scripts: false,
      },
    });

    renderStudioPage("/studio?tab=settings");

    const runtimeBaseUrlInput = await screen.findByLabelText(
      "Studio runtime base URL"
    );
    expect(runtimeBaseUrlInput).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Save settings" })
    ).toBeDisabled();

    fireEvent.click(screen.getByRole("button", { name: "Save settings" }));

    expect(studioApi.saveSettings).not.toHaveBeenCalled();

    expect(
      screen.getByRole("button", { name: "Check host runtime" })
    ).toBeTruthy();
  });

  it("applies a saved role to the current workflow from the roles catalog", async () => {
    renderStudioPage("/studio?workflow=workflow-1&tab=roles");

    expect(await screen.findByText("Saved roles")).toBeTruthy();
    await waitFor(() => {
      expect(studioApi.parseYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          yaml: expect.stringContaining("name: workspace-demo"),
        })
      );
    });
    fireEvent.click(screen.getByRole("button", { name: "Use" }));

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          document: expect.objectContaining({
            roles: expect.arrayContaining([
              expect.objectContaining({
                id: "assistant_2",
              }),
            ]),
          }),
        })
      );
    });
  });
});
