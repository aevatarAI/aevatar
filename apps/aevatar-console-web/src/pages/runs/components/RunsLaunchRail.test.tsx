import { fireEvent, render, screen } from "@testing-library/react";
import React from "react";
import type { ProFormInstance } from "@ant-design/pro-components";
import type { RecentRunTableRow, RunFormValues, RunPreset } from "../runWorkbenchConfig";
import RunsLaunchRail from "./RunsLaunchRail";

describe("RunsLaunchRail", () => {
  it("renders preset cards without relying on ProList layout columns", () => {
    const composerFormRef = {
      current: undefined,
    } as React.RefObject<ProFormInstance<RunFormValues> | undefined>;
    const visiblePresets: RunPreset[] = [
      {
        description: "Baseline direct chat bundle for quick validation of the chat stream.",
        key: "direct",
        prompt: "Summarize what this chat bundle can do.",
        routeName: "direct",
        tags: ["baseline", "llm"],
        title: "Direct chat",
      },
    ];
    const recentRunRows: RecentRunTableRow[] = [];
    const onUsePreset = jest.fn();

    render(
      <RunsLaunchRail
        catalogSearch=""
        activeEndpointId="chat"
        composerFormRef={composerFormRef}
        initialFormValues={{
          prompt: "",
          endpointId: "chat",
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
        onSelectRouteName={jest.fn()}
        onSubmitRun={async () => {}}
        onTransportChange={jest.fn()}
        onUsePreset={onUsePreset}
      />
    );

    expect(
      screen.getByLabelText("Chat route (optional)")
    ).toBeInTheDocument();
    expect(screen.getByLabelText("Endpoint")).toBeInTheDocument();
    expect(
      screen.getByText(
        "Selecting a route targets the published scope service with the same id. Leave it empty to use the scope default binding; binding override wins when provided."
      )
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Presets (1)" }));

    expect(screen.getByText("Direct chat")).toBeInTheDocument();
    expect(screen.getByText("Baseline direct chat bundle for quick validation of the chat stream.")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Use preset" }));

    expect(onUsePreset).toHaveBeenCalledTimes(1);
    expect(onUsePreset).toHaveBeenCalledWith(visiblePresets[0]);
  });
});
