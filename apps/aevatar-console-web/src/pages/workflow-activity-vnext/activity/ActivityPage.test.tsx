import { screen, waitFor } from "@testing-library/react";
import * as React from "react";
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from "../../../../tests/reactQueryTestUtils";
import ActivityPage from "./ActivityPage";

let mockSearch = "";

jest.mock("@umijs/max", () => ({
  getIntl: () => ({
    formatMessage: ({ defaultMessage, id }: { defaultMessage?: string; id: string }) =>
      defaultMessage ?? id,
  }),
  getLocale: () => "en-US",
  history: {},
  setLocale: jest.fn(),
  useIntl: () => ({
    formatMessage: ({ defaultMessage, id }: { defaultMessage?: string; id: string }) =>
      defaultMessage ?? id,
  }),
  useModel: () => ({ initialState: { auth: { authenticated: true } } }),
}));

jest.mock("@/shared/api/workflowActivityApi", () => {
  class WorkflowActivityApiError extends Error {
    status: number;

    constructor(message: string, status: number) {
      super(message);
      this.status = status;
    }
  }

  return {
    WorkflowActivityApiError,
    workflowActivityApi: { listRuns: jest.fn() },
  };
});

jest.mock("@/shared/navigation/history", () => ({
  history: { push: jest.fn(), replace: jest.fn() },
}));

jest.mock("@/shared/ui/ConsoleHeaderActions", () => ({
  ConsoleAuthActions: () => <button type="button">Account</button>,
  ConsoleLanguageSwitch: () => <button type="button">Language</button>,
}));

jest.mock("../hooks/useConsoleLocation", () => ({
  useConsoleLocation: () => ({
    hash: "",
    pathname: "/scopes/scope-alpha/workflow-activity-vnext/activity",
    search: mockSearch,
  }),
}));

const mockListRuns = jest.requireMock("@/shared/api/workflowActivityApi")
  .workflowActivityApi.listRuns as jest.Mock;

describe("Workflow Activity vNext Activity ledger", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockSearch = "";
    mockListRuns.mockResolvedValue([]);
  });

  afterEach(() => cleanupTestQueryClients());

  it("preserves the honest unavailable notice for a workflow without definition identity", async () => {
    mockSearch = "?workflowFilter=unavailable";

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(
      await screen.findByText("Workflow filter unavailable; showing unfiltered Activity"),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(mockListRuns).toHaveBeenCalledWith("scope-alpha", {
        status: undefined,
        origins: undefined,
        definitionActorIds: undefined,
        take: 100,
      }),
    );
  });

  it("sends only URL-backed supported filters to the observatory API", async () => {
    mockSearch = "?status=failed&origin=draft&definition=definition-alpha";

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    await waitFor(() =>
      expect(mockListRuns).toHaveBeenCalledWith("scope-alpha", {
        status: "failed",
        origins: ["draft"],
        definitionActorIds: ["definition-alpha"],
        take: 100,
      }),
    );
    expect(screen.getByRole("button", { name: "Clear workflow filter" })).toBeEnabled();
  });
});
