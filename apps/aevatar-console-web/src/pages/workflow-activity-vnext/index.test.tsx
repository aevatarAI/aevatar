import { fireEvent, screen, waitFor } from "@testing-library/react";
import * as React from "react";
import { history } from "@/shared/navigation/history";
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from "../../../tests/reactQueryTestUtils";
import WorkflowActivityVNextPage from "./index";

let mockPathname =
  "/scopes/scope-alpha/workflow-activity-vnext/workflows";

jest.mock("@umijs/max", () => ({
  getIntl: () => ({
    formatMessage: ({ defaultMessage, id }: { defaultMessage?: string; id: string }, values?: Record<string, unknown>) =>
      (defaultMessage ?? id).replace(/\{(\w+)\}/g, (_match: string, key: string) => String(values?.[key] ?? "")),
  }),
  getLocale: () => "en-US",
  history: {},
  setLocale: jest.fn(),
  useIntl: () => ({
    formatMessage: ({ defaultMessage, id }: { defaultMessage?: string; id: string }) =>
      defaultMessage ?? id,
  }),
  useLocation: () => ({ hash: "", pathname: mockPathname, search: "" }),
  useModel: () => ({ initialState: { auth: { authenticated: true } } }),
  useParams: () => ({ scopeId: "scope-alpha" }),
}));

jest.mock("@/shared/studio/api", () => ({
  isStudioApiStatus: (error: unknown, status: number) =>
    Boolean(error && typeof error === "object" && "status" in error && error.status === status),
  studioApi: {
    authorWorkflow: jest.fn(),
    createWorkflowDraft: jest.fn(),
    getWorkspaceSettings: jest.fn(),
    getAuthSession: jest.fn(),
    getUserConfigRuntime: jest.fn(),
    getUserLlmSettings: jest.fn(),
    getWorkflow: jest.fn(),
    getWorkflowDraftFile: jest.fn(),
    listWorkflowDrafts: jest.fn(),
    parseYaml: jest.fn(),
    saveWorkflow: jest.fn(),
    saveUserLlmSettings: jest.fn(),
    serializeYaml: jest.fn(),
  },
}));

jest.mock("@/shared/api/scopesApi", () => ({
  scopesApi: {
    getWorkflowDetail: jest.fn(),
    listWorkflows: jest.fn(),
  },
}));

jest.mock("@/shared/navigation/history", () => ({
  getLocationSnapshot: () => mockPathname,
  history: { push: jest.fn(), replace: jest.fn() },
  subscribeToLocationChanges: () => () => undefined,
}));

jest.mock("@/shared/ui/ConsoleHeaderActions", () => ({
  ConsoleAuthActions: () => <button type="button">Account</button>,
  ConsoleLanguageSwitch: () => <button type="button">Language</button>,
}));

jest.mock("@/pages/team-member-workflow-studio/components/WorkflowStudioCanvas", () => ({
  __esModule: true,
  default: ({
    nodes,
    onNodeSelect,
  }: {
    nodes: readonly { readonly id: string }[];
    onNodeSelect?: (nodeId: string) => void;
  }) => (
    <div data-testid="workflow-studio-canvas">
      {nodes.map((node) => (
        <button key={node.id} onClick={() => onNodeSelect?.(node.id)} type="button">
          Select {node.id}
        </button>
      ))}
    </div>
  ),
}));

jest.mock("@/pages/team-member-workflow-studio/components/WorkflowStudioNodeDetailPanel", () => ({
  __esModule: true,
  default: ({
    onConfigurationChange,
    stepDraft,
  }: {
    onConfigurationChange: (parametersText: string) => void;
    stepDraft: { readonly id: string } | null;
  }) => stepDraft ? (
    <section aria-label="Node configuration">
      <span>Configuring {stepDraft.id}</span>
      <button
        onClick={() => onConfigurationChange('{"prompt_prefix":"Updated prompt"}')}
        type="button"
      >
        Apply node configuration
      </button>
    </section>
  ) : null,
}));

const mockStudioApi = jest.requireMock("@/shared/studio/api").studioApi as {
  authorWorkflow: jest.Mock;
  createWorkflowDraft: jest.Mock;
  getWorkspaceSettings: jest.Mock;
  getAuthSession: jest.Mock;
  getUserConfigRuntime: jest.Mock;
  getUserLlmSettings: jest.Mock;
  getWorkflow: jest.Mock;
  getWorkflowDraftFile: jest.Mock;
  listWorkflowDrafts: jest.Mock;
  parseYaml: jest.Mock;
  saveWorkflow: jest.Mock;
  saveUserLlmSettings: jest.Mock;
  serializeYaml: jest.Mock;
};
const mockScopesApi = jest.requireMock("@/shared/api/scopesApi").scopesApi as {
  getWorkflowDetail: jest.Mock;
  listWorkflows: jest.Mock;
};

describe("Workflow Activity vNext catalogue", () => {
  beforeEach(() => {
    mockPathname = "/scopes/scope-alpha/workflow-activity-vnext/workflows";
    jest.clearAllMocks();
  });

  afterEach(() => cleanupTestQueryClients());

  it("renders authoritative draft and committed rows, searches, and navigates by the real workflow id", async () => {
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([
      {
        workflowId: "wf-draft-alpha",
        name: "Support triage",
        description: "Route support requests",
        fileName: "support.yaml",
        filePath: "/support.yaml",
        directoryId: "directory-alpha",
        directoryLabel: "Workflows",
        stepCount: 3,
        hasLayout: true,
        updatedAtUtc: "2026-08-04T10:00:00Z",
      },
    ]);
    mockScopesApi.listWorkflows.mockResolvedValue([
      {
        scopeId: "scope-alpha",
        workflowId: "wf-committed-beta",
        displayName: "Invoice review",
        serviceKey: "",
        workflowName: "invoice_review",
        actorId: "summary-actor-beta",
        activeRevisionId: "revision-beta",
        deploymentId: "deployment-beta",
        deploymentStatus: "active",
        updatedAt: "2026-08-03T10:00:00Z",
      },
    ]);
    mockScopesApi.getWorkflowDetail.mockResolvedValue({
      available: true,
      scopeId: "scope-alpha",
      workflow: null,
      source: {
        workflowYaml: "",
        definitionActorId: "definition-beta",
        inlineWorkflowYamls: null,
      },
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(screen.getByText("Loading workflows")).toBeInTheDocument();
    expect(await screen.findByText("Support triage")).toBeInTheDocument();
    expect(screen.getByText("Invoice review")).toBeInTheDocument();

    fireEvent.change(screen.getByRole("searchbox", { name: "Search workflows" }), {
      target: { value: "invoice" },
    });
    expect(screen.queryByText("Support triage")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Activity" }));
    await waitFor(() => {
      expect(mockScopesApi.getWorkflowDetail).toHaveBeenCalledWith(
        "scope-alpha",
        "wf-committed-beta",
      );
      expect(history.push).toHaveBeenCalledWith(
        "/scopes/scope-alpha/workflow-activity-vnext/activity?definition=definition-beta",
      );
    });

    fireEvent.click(screen.getByRole("button", { name: "Open Invoice review" }));
    expect(history.push).toHaveBeenCalledWith(
      "/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-committed-beta",
    );
  });

  it("opens unfiltered Activity with an unavailable notice for a draft-only row", async () => {
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([
      {
        workflowId: "wf-draft-alpha",
        name: "Support triage",
        description: "Route support requests",
        fileName: "support.yaml",
        filePath: "/support.yaml",
        directoryId: "directory-alpha",
        directoryLabel: "Workflows",
        stepCount: 3,
        hasLayout: true,
        updatedAtUtc: "2026-08-04T10:00:00Z",
      },
    ]);
    mockScopesApi.listWorkflows.mockResolvedValue([]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText("Support triage")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Activity" }));
    expect(mockScopesApi.getWorkflowDetail).not.toHaveBeenCalled();
    expect(history.push).toHaveBeenCalledWith(
      "/scopes/scope-alpha/workflow-activity-vnext/activity?workflowFilter=unavailable",
    );
  });

  it("keeps successful rows and names the failed source", async () => {
    mockStudioApi.listWorkflowDrafts.mockRejectedValue(new Error("draft source down"));
    mockScopesApi.listWorkflows.mockResolvedValue([
      {
        scopeId: "scope-alpha",
        workflowId: "wf-committed-beta",
        displayName: "Invoice review",
        serviceKey: "",
        workflowName: "invoice_review",
        actorId: "definition-beta",
        activeRevisionId: "revision-beta",
        deploymentId: "deployment-beta",
        deploymentStatus: "active",
        updatedAt: "2026-08-03T10:00:00Z",
      },
    ]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText("Invoice review")).toBeInTheDocument();
    expect(screen.getByText("Draft catalogue unavailable")).toBeInTheDocument();
    expect(screen.queryByText("No workflows yet")).not.toBeInTheDocument();
  });

  it("renders a successful empty result", async () => {
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([]);
    mockScopesApi.listWorkflows.mockResolvedValue([]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);
    expect(await screen.findByText("No workflows yet")).toBeInTheDocument();
  });

  it("renders total source failure and supports retry", async () => {
    mockStudioApi.listWorkflowDrafts.mockRejectedValue(new Error("offline"));
    mockScopesApi.listWorkflows.mockRejectedValue(new Error("offline"));
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText("Workflows unavailable")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Retry workflows" })).toBeEnabled();
  });
});

describe("Workflow Activity vNext settings", () => {
  beforeEach(() => {
    mockPathname = "/scopes/scope-alpha/workflow-activity-vnext/settings";
    jest.clearAllMocks();
    mockStudioApi.getUserLlmSettings.mockResolvedValue({
      savedSelection: null,
      savedRouteLabel: "System default",
      selectionStatus: "system_default",
      catalogDiagnostic: "unspecified",
      remediation: "none",
      routeOptions: [],
      modelGroupsByRoute: [],
      catalogStatus: "empty",
      capabilities: {
        canEditRoute: true,
        canEditModel: true,
        canSave: true,
        canRetryCatalog: true,
      },
    });
    mockStudioApi.getAuthSession.mockResolvedValue({
      enabled: true,
      authenticated: true,
      providerDisplayName: "NyxID",
      profile: {
        subject: "user-subject-alpha",
        name: "Ada Operator",
        email: "ada@example.test",
        emailVerified: true,
        picture: null,
        roles: ["operator"],
        groups: ["platform"],
      },
      session: {
        authenticated: true,
        scopeId: "scope-alpha",
        scopeSource: "nyxid-session",
        expiresAtUtc: "2026-08-05T10:00:00Z",
      },
    });
    mockStudioApi.getUserConfigRuntime.mockResolvedValue({
      runtimeMode: "remote",
      activeRuntimeBaseUrl: "https://runtime.example.test",
      localRuntimeBaseUrl: "http://localhost:5100",
      remoteRuntimeBaseUrl: "https://runtime.example.test",
      runtimeDefaults: {
        localRuntimeBaseUrl: "http://localhost:5100",
        remoteRuntimeBaseUrl: "https://runtime.example.test",
        localMode: "local",
        remoteMode: "remote",
      },
    });
  });

  afterEach(() => cleanupTestQueryClients());

  it("renders real AI, account, and runtime facts in one scoped settings surface", async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect((await screen.findAllByText("System default")).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("tab", { name: "Account" }));
    expect(await screen.findByText("Ada Operator")).toBeInTheDocument();
    expect(screen.getByText("user-subject-alpha")).toBeInTheDocument();
    expect(screen.getByText("NyxID")).toBeInTheDocument();
    expect(screen.getByText("operator")).toBeInTheDocument();
    expect(screen.getByText("platform")).toBeInTheDocument();
    expect(screen.getByText("nyxid-session")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: "Advanced" }));
    expect(await screen.findAllByText("https://runtime.example.test")).toHaveLength(2);
    expect(screen.getByText("remote")).toBeInTheDocument();
  });

  it("discards dirty AI defaults back to the last authoritative selection", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValue({
      savedSelection: {
        routeKind: "gateway",
        routeValue: "/api/v1/llm/gateway/v1",
        modelSelection: { kind: "provider_default" },
      },
      savedRouteLabel: "Gateway",
      selectionStatus: "ready",
      catalogDiagnostic: "unspecified",
      remediation: "none",
      catalogStatus: "ready",
      capabilities: {
        canEditRoute: true,
        canEditModel: true,
        canSave: true,
        canRetryCatalog: true,
      },
      routeOptions: [
        {
          routeValue: "/api/v1/llm/gateway/v1",
          label: "Gateway",
          source: "gateway_provider",
          status: "ready",
          allowed: true,
          ready: true,
          userServiceId: null,
          serviceSlug: null,
          modelCatalog: {
            certainty: "not_verifiable",
            modelIds: [],
            defaultModelId: null,
            diagnostic: "not_published",
          },
          description: null,
        },
        {
          routeValue: "/api/v1/proxy/s/service-alpha",
          label: "Service alpha",
          source: "user_service",
          status: "ready",
          allowed: true,
          ready: true,
          userServiceId: "us-alpha",
          serviceSlug: "service-alpha",
          modelCatalog: {
            certainty: "enumerated",
            modelIds: ["model-alpha"],
            defaultModelId: "model-alpha",
            diagnostic: "unspecified",
          },
          description: null,
        },
      ],
      modelGroupsByRoute: [],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const routeSelect = await screen.findByRole("combobox", { name: "AI route" });
    fireEvent.mouseDown(routeSelect);
    fireEvent.click(await screen.findByText("Service alpha"));
    expect(screen.getByRole("button", { name: "Save AI defaults" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Discard changes" }));
    expect(screen.getByRole("button", { name: "Save AI defaults" })).toBeDisabled();
    expect(mockStudioApi.saveUserLlmSettings).not.toHaveBeenCalled();
  });
});

describe("Workflow Activity vNext editor", () => {
  beforeEach(() => {
    mockPathname =
      "/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-committed-source";
    jest.clearAllMocks();
    mockStudioApi.getWorkspaceSettings.mockResolvedValue({
      runtimeBaseUrl: "",
      directories: [
        { directoryId: "directory-alpha", label: "Workflows", path: "/workflows", isBuiltIn: true },
      ],
    });
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: "wf-committed-source",
      name: "Committed source",
      fileName: "committed-source.yaml",
      filePath: "",
      directoryId: "",
      directoryLabel: "",
      yaml: "name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n    parameters:\n      prompt_prefix: Original prompt\n",
      updatedAtUtc: "2026-08-04T10:00:00Z",
      document: {
        name: "committed_source",
        roles: [],
        steps: [
          {
            id: "step-root",
            type: "llm_call",
            parameters: { prompt_prefix: "Original prompt" },
          },
        ],
      },
      draftExists: false,
      findings: [],
    });
    mockStudioApi.parseYaml.mockResolvedValue({
      document: { name: "committed_source", roles: [], steps: [] },
      findings: [],
    });
    mockStudioApi.serializeYaml.mockResolvedValue({
      yaml: "name: committed_source\nroles: []\nsteps: []\n",
      document: { name: "committed_source", roles: [], steps: [] },
      findings: [],
    });
    mockStudioApi.saveWorkflow.mockResolvedValue({
      kind: "materialized",
      workflow: {
        workflowId: "wf-draft-new",
        name: "Committed source",
        fileName: "committed-source.yaml",
        filePath: "/workflows/committed-source.yaml",
        directoryId: "directory-alpha",
        directoryLabel: "Workflows",
        yaml: "name: committed_source\nroles: []\nsteps: []\n",
        updatedAtUtc: "2026-08-04T10:01:00Z",
        document: { name: "committed_source", roles: [], steps: [] },
        draftExists: true,
        findings: [],
      },
    });
  });

  afterEach(() => cleanupTestQueryClients());

  it("creates on first save for committed-only source and adopts the API-returned draft id", async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByDisplayValue("Committed source")).toBeInTheDocument();
    fireEvent.click(screen.getByText("YAML"));
    fireEvent.change(screen.getByLabelText("Workflow YAML"), {
      target: { value: "name: committed_source\nroles: []\nsteps: []\n\n" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save workflow" }));

    await waitFor(() => expect(mockStudioApi.saveWorkflow).toHaveBeenCalledTimes(1));
    expect(mockStudioApi.saveWorkflow).toHaveBeenCalledWith(
      expect.objectContaining({
        draftExists: false,
        workflowId: "wf-committed-source",
        directoryId: "directory-alpha",
      }),
    );
    expect(history.replace).toHaveBeenCalledWith(
      "/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-new",
    );
  });

  it("edits a selected canvas node through the shared document state", async () => {
    mockStudioApi.serializeYaml.mockImplementation(async ({ document }) => ({
      yaml: "name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n    parameters:\n      prompt_prefix: Updated prompt\n",
      document,
      findings: [],
    }));

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole("button", { name: "Select step:step-root" }));
    expect(screen.getByText("Configuring step-root")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Apply node configuration" }));

    await waitFor(() =>
      expect(mockStudioApi.serializeYaml).toHaveBeenCalledWith({
        document: expect.objectContaining({
          steps: [
            expect.objectContaining({
              id: "step-root",
              parameters: { prompt_prefix: "Updated prompt" },
            }),
          ],
        }),
      }),
    );
  });

  it("offers Save, Discard, and Stay before vNext navigation with unsaved changes", async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByDisplayValue("Committed source")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Workflow name"), {
      target: { value: "Unsaved title" },
    });
    fireEvent.click(screen.getAllByRole("link", { name: "Activity" })[0]);

    expect(history.push).not.toHaveBeenCalledWith(
      "/scopes/scope-alpha/workflow-activity-vnext/activity",
    );
    expect(screen.getByRole("dialog", { name: "Unsaved workflow changes" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save and leave" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Discard and leave" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Stay" }));
    expect(screen.queryByRole("dialog", { name: "Unsaved workflow changes" })).not.toBeInTheDocument();

    fireEvent.click(screen.getAllByRole("link", { name: "Activity" })[0]);
    fireEvent.click(screen.getByRole("button", { name: "Discard and leave" }));
    expect(history.push).toHaveBeenCalledWith(
      "/scopes/scope-alpha/workflow-activity-vnext/activity",
    );
  });
});

describe("Workflow Activity vNext creation", () => {
  beforeEach(() => {
    mockPathname = "/scopes/scope-alpha/workflow-activity-vnext/workflows/new";
    jest.clearAllMocks();
    mockStudioApi.getWorkspaceSettings.mockResolvedValue({
      runtimeBaseUrl: "",
      directories: [
        { directoryId: "directory-alpha", label: "Workflows", path: "/workflows", isBuiltIn: true },
      ],
    });
  });

  afterEach(() => cleanupTestQueryClients());

  it("creates a blank draft with a server directory and navigates only after materialization", async () => {
    mockStudioApi.createWorkflowDraft.mockResolvedValue({
      kind: "materialized",
      workflow: {
        workflowId: "wf-created-alpha",
        name: "Incident review",
        fileName: "incident-review.yaml",
        filePath: "/workflows/incident-review.yaml",
        directoryId: "directory-alpha",
        directoryLabel: "Workflows",
        yaml: "name: incident_review\nroles: []\nsteps: []\n",
        updatedAtUtc: "2026-08-04T10:00:00Z",
        document: { name: "incident_review", roles: [], steps: [] },
        draftExists: true,
        findings: [],
      },
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);
    const blankButton = await screen.findByRole("button", { name: "Start blank" });
    await waitFor(() => expect(blankButton).toBeEnabled());
    fireEvent.click(blankButton);
    fireEvent.change(screen.getByLabelText("Workflow name"), {
      target: { value: "Incident review" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Create blank draft" }));

    await waitFor(() => expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledTimes(1));
    expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith(
      expect.objectContaining({ directoryId: "directory-alpha", scopeId: "scope-alpha" }),
    );
    expect(history.push).toHaveBeenCalledWith(
      "/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-created-alpha",
    );
  });

  it("validates imported YAML before creating and preserves invalid input", async () => {
    mockStudioApi.parseYaml.mockResolvedValue({
      document: null,
      findings: [{ level: "error", code: "YAML_INVALID", message: "Invalid YAML" }],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);
    const importButton = await screen.findByRole("button", { name: "Import YAML" });
    await waitFor(() => expect(importButton).toBeEnabled());
    fireEvent.click(importButton);
    fireEvent.change(screen.getByLabelText("Workflow YAML"), {
      target: { value: "name: [broken" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Validate and create" }));

    expect(await screen.findByText("Invalid YAML")).toBeInTheDocument();
    expect(mockStudioApi.createWorkflowDraft).not.toHaveBeenCalled();
    expect(screen.getByLabelText("Workflow YAML")).toHaveValue("name: [broken");
  });
});
