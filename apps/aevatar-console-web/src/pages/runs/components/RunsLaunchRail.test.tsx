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
        description: "Baseline direct workflow for quick validation of the chat stream.",
        key: "direct",
        prompt: "Summarize what this workflow can do.",
        tags: ["baseline", "llm"],
        title: "Direct chat",
        workflow: "direct",
      },
    ];
    const recentRunRows: RecentRunTableRow[] = [];
    const onUsePreset = jest.fn();

    render(
      <RunsLaunchRail
        catalogSearch=""
        composerFormRef={composerFormRef}
        initialFormValues={{
          prompt: "",
          scopeId: "scope-1",
          serviceId: "service-1",
          transport: "sse",
          workflow: "direct",
        }}
        recentRunRows={recentRunRows}
        selectedTransport="sse"
        selectedWorkflowDetailsPrimitives={[]}
        streaming={false}
        submitPathLabel="/api/scopes/{scopeId}/invoke/chat:stream"
        transportOptions={[{ label: "Service SSE stream", value: "sse" }]}
        visiblePresets={visiblePresets}
        workflowCatalogLoading={false}
        workflowOptions={[{ label: "direct", value: "direct" }]}
        onAbortRun={jest.fn()}
        onCatalogSearchChange={jest.fn()}
        onClearRecentRuns={jest.fn()}
        onSelectWorkflowName={jest.fn()}
        onSubmitRun={async () => {}}
        onTransportChange={jest.fn()}
        onUsePreset={onUsePreset}
      />
    );

    fireEvent.click(screen.getByRole("tab", { name: "Presets (1)" }));

    expect(screen.getByText("Direct chat")).toBeInTheDocument();
    expect(screen.getByText("Baseline direct workflow for quick validation of the chat stream.")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Use preset" }));

    expect(onUsePreset).toHaveBeenCalledTimes(1);
    expect(onUsePreset).toHaveBeenCalledWith(visiblePresets[0]);
  });
});
