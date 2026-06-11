import { fireEvent, render, screen } from "@testing-library/react";
import React from "react";
import type { ProFormInstance } from "@ant-design/pro-components";
import type { RecentRunTableRow, RunFormValues, RunPreset } from "../runWorkbenchConfig";
import RunsLaunchRail from "./RunsLaunchRail";
import { setLocale } from "@umijs/max";

describe("RunsLaunchRail", () => {
  beforeEach(() => {
    setLocale("en-US");
  });

  it("renders preset cards without relying on ProList layout columns", () => {
    const composerFormRef = {
      current: undefined,
    } as React.RefObject<ProFormInstance<RunFormValues> | undefined>;
    const visiblePresets: RunPreset[] = [
      {
        key: "direct",
        prompt: "Summarize what this chat bundle can do.",
        routeName: "direct",
        tags: ["baseline", "llm"],
        title: {
          defaultMessage: "Direct chat",
          id: "test.direct.chat",
        },
        description: {
          defaultMessage: "Baseline direct chat bundle for quick validation of the chat stream.",
          id: "test.direct.chat.description",
        },
      },
    ];
    const recentRunRows: RecentRunTableRow[] = [];
    const onUsePreset = jest.fn();

    render(
      <RunsLaunchRail
        catalogSearch=""
        activeEndpointId="chat"
        activeEndpointKind="chat"
        composerFormRef={composerFormRef}
        initialFormValues={{
          prompt: "",
          endpointId: "chat",
          endpointKind: "chat",
          scopeId: "scope-1",
          serviceOverrideId: "service-1",
          transport: "sse",
          routeName: "direct",
        }}
        recentRunRows={recentRunRows}
        selectedTransport="sse"
        selectedRouteDetailsPrimitives={[]}
        streaming={false}
        submitPathLabel="/api/scopes/{scopeId}/invoke/chat:stream"
        transportOptions={[{ label: "Service SSE stream", value: "sse" }]}
        visiblePresets={visiblePresets}
        workflowCatalogLoading={false}
        routeOptions={[{ label: "direct", value: "direct" }]}
        onAbortRun={jest.fn()}
        onCatalogSearchChange={jest.fn()}
        onClearRecentRuns={jest.fn()}
        onEndpointChange={jest.fn()}
        onEndpointKindChange={jest.fn()}
        onSelectRouteName={jest.fn()}
        onScopeIdChange={jest.fn()}
        onSubmitRun={async () => {}}
        onTransportChange={jest.fn()}
        onUsePreset={onUsePreset}
      />
    );

    expect(screen.getByText("Target: direct")).toBeInTheDocument();
    expect(screen.getByText("Workspace, route/default binding, prompt")).toBeInTheDocument();
    expect(
      screen.getByLabelText("Chat route (optional)")
    ).toBeInTheDocument();
    expect(screen.queryByLabelText("Endpoint")).toBeNull();
    expect(screen.queryByLabelText("Binding override (optional)")).toBeNull();
    fireEvent.click(screen.getByText("Advanced endpoint and payload options"));
    expect(screen.getByLabelText("Endpoint")).toBeInTheDocument();
    expect(screen.getByLabelText("Binding override (optional)")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Presets (1)" }));

    expect(screen.getByText("Direct chat")).toBeInTheDocument();
    expect(screen.getByText("Baseline direct chat bundle for quick validation of the chat stream.")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Use preset" }));

    expect(onUsePreset).toHaveBeenCalledTimes(1);
    expect(onUsePreset).toHaveBeenCalledWith(visiblePresets[0]);
  });

  it("hides advanced chat configuration behind a collapsible section in chat mode", () => {
    const composerFormRef = {
      current: undefined,
    } as React.RefObject<ProFormInstance<RunFormValues> | undefined>;

    render(
      <RunsLaunchRail
        catalogSearch=""
        activeEndpointId="chat"
        activeEndpointKind="chat"
        composerFormRef={composerFormRef}
        initialFormValues={{
          prompt: "",
          endpointId: "chat",
          endpointKind: "chat",
          scopeId: "scope-1",
          serviceOverrideId: "",
          transport: "sse",
          routeName: "direct",
        }}
        recentRunRows={[]}
        selectedTransport="sse"
        selectedRouteDetailsPrimitives={[]}
        streaming={false}
        submitPathLabel="/api/scopes/{scopeId}/invoke/chat:stream"
        transportOptions={[{ label: "Service SSE stream", value: "sse" }]}
        variant="chat"
        visiblePresets={[]}
        workflowCatalogLoading={false}
        routeOptions={[{ label: "direct", value: "direct" }]}
        runReadiness={{
          ready: false,
          blockingReason: "Workspace is required before the prompt can be sent.",
          items: [
            {
              key: "workspace",
              label: "Workspace",
              value: "Required",
              status: "required",
              helper: "Add a workspace ID to unlock Send.",
            },
            {
              key: "route",
              label: "Route",
              value: "direct",
              status: "context",
              helper: "The prompt will target this chat route.",
            },
            {
              key: "endpoint",
              label: "Endpoint",
              value: "chat",
              status: "context",
              helper: "Advanced endpoint and payload controls stay available below.",
            },
          ],
        }}
        onAbortRun={jest.fn()}
        onCatalogSearchChange={jest.fn()}
        onClearRecentRuns={jest.fn()}
        onEndpointChange={jest.fn()}
        onEndpointKindChange={jest.fn()}
        onSelectRouteName={jest.fn()}
        onScopeIdChange={jest.fn()}
        onSubmitRun={async () => {}}
        onTransportChange={jest.fn()}
        onUsePreset={jest.fn()}
      />
    );

    expect(screen.getByLabelText("Chat route (optional)")).toBeInTheDocument();
    expect(screen.getByLabelText("Workspace ID")).toBeInTheDocument();
    expect(screen.getByText("Send readiness")).toBeInTheDocument();
    expect(screen.getByText("Blocked")).toBeInTheDocument();
    expect(screen.getByText("Workspace is required before the prompt can be sent.")).toBeInTheDocument();
    expect(screen.getByText("Add a workspace ID to unlock Send.")).toBeInTheDocument();
    expect(screen.getAllByText("Required").length).toBeGreaterThan(0);
    expect(screen.queryByLabelText("Endpoint")).toBeNull();
    expect(screen.queryByText("Requests go through /api/scopes/{scopeId}/invoke/chat:stream")).toBeNull();

    fireEvent.click(screen.getByText("Advanced payload and transport"));

    expect(screen.getByLabelText("Endpoint")).toBeInTheDocument();
    expect(screen.getByLabelText("Binding override (optional)")).toBeInTheDocument();
  });

  it("shows ready chat context without expanding advanced payload controls", () => {
    const composerFormRef = {
      current: undefined,
    } as React.RefObject<ProFormInstance<RunFormValues> | undefined>;

    render(
      <RunsLaunchRail
        catalogSearch=""
        activeEndpointId="chat"
        activeEndpointKind="chat"
        composerFormRef={composerFormRef}
        initialFormValues={{
          prompt: "",
          endpointId: "chat",
          endpointKind: "chat",
          scopeId: "scope-1",
          serviceOverrideId: "",
          transport: "sse",
          routeName: "direct",
        }}
        recentRunRows={[]}
        selectedTransport="sse"
        selectedRouteDetailsPrimitives={[]}
        streaming={false}
        submitPathLabel="/api/scopes/{scopeId}/invoke/chat:stream"
        transportOptions={[{ label: "Service SSE stream", value: "sse" }]}
        variant="chat"
        visiblePresets={[]}
        workflowCatalogLoading={false}
        routeOptions={[{ label: "direct", value: "direct" }]}
        runReadiness={{
          ready: true,
          items: [
            {
              key: "workspace",
              label: "Workspace",
              value: "scope-1",
              status: "ready",
              helper: "Run requests are scoped to this workspace.",
            },
            {
              key: "route",
              label: "Route",
              value: "direct",
              status: "context",
              helper: "The prompt will target this chat route.",
            },
            {
              key: "endpoint",
              label: "Endpoint",
              value: "chat",
              status: "context",
              helper: "Advanced endpoint and payload controls stay available below.",
            },
          ],
        }}
        onAbortRun={jest.fn()}
        onCatalogSearchChange={jest.fn()}
        onClearRecentRuns={jest.fn()}
        onEndpointChange={jest.fn()}
        onEndpointKindChange={jest.fn()}
        onSelectRouteName={jest.fn()}
        onScopeIdChange={jest.fn()}
        onSubmitRun={async () => {}}
        onTransportChange={jest.fn()}
        onUsePreset={jest.fn()}
      />
    );

    expect(screen.getByText("Ready to send")).toBeInTheDocument();
    expect(screen.getByText("Prompt runs will use this workspace context.")).toBeInTheDocument();
    expect(screen.getAllByText("Ready").length).toBeGreaterThan(0);
    expect(screen.getByText("scope-1")).toBeInTheDocument();
    expect(screen.getByText("Run requests are scoped to this workspace.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Endpoint")).toBeNull();
  });

  it("keeps command endpoint payload controls behind advanced options", () => {
    const composerFormRef = {
      current: undefined,
    } as React.RefObject<ProFormInstance<RunFormValues> | undefined>;

    render(
      <RunsLaunchRail
        catalogSearch=""
        activeEndpointId="submit"
        activeEndpointKind="command"
        composerFormRef={composerFormRef}
        initialFormValues={{
          prompt: "",
          endpointId: "submit",
          endpointKind: "command",
          scopeId: "scope-1",
          serviceOverrideId: "service-1",
          transport: "sse",
          routeName: "",
        }}
        recentRunRows={[]}
        selectedTransport="sse"
        selectedRouteDetailsPrimitives={[]}
        streaming={false}
        submitPathLabel="/api/scopes/{scopeId}/members/{memberId}/invoke/submit"
        transportOptions={[{ label: "Service SSE stream", value: "sse" }]}
        visiblePresets={[]}
        workflowCatalogLoading={false}
        routeOptions={[]}
        onAbortRun={jest.fn()}
        onCatalogSearchChange={jest.fn()}
        onClearRecentRuns={jest.fn()}
        onEndpointChange={jest.fn()}
        onEndpointKindChange={jest.fn()}
        onSelectRouteName={jest.fn()}
        onScopeIdChange={jest.fn()}
        onSubmitRun={async () => {}}
        onTransportChange={jest.fn()}
        onUsePreset={jest.fn()}
      />
    );

    expect(screen.getByText("Target: submit")).toBeInTheDocument();
    expect(screen.getAllByText("Command invoke").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("Workspace, endpoint, prompt or payload")).toBeInTheDocument();
    expect(screen.queryByLabelText("Payload base64 (advanced)")).toBeNull();

    fireEvent.click(screen.getByText("Advanced endpoint and payload options"));

    expect(screen.getByLabelText("Endpoint")).toBeInTheDocument();
    expect(screen.getByLabelText("Payload base64 (advanced)")).toBeInTheDocument();
  });
});
