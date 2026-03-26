import { fireEvent, render, screen } from "@testing-library/react";
import React from "react";
import type { RunTimelineGroup } from "../runEventPresentation";
import RunsTimelineView from "./RunsTimelineView";

describe("RunsTimelineView", () => {
  it("highlights the selected item and emits selection on click", () => {
    const onSelectItem = jest.fn();
    const groups: RunTimelineGroup[] = [
      {
        eventCount: 2,
        items: [
          {
            agentId: "actor-1",
            description: "Step completed: triage",
            eventCategory: "lifecycle",
            eventStatus: "success",
            eventType: "CUSTOM · StepCompleted",
            key: "item-2",
            payloadPreview: '{"stepId":"triage","success":true}',
            payloadText: '{"stepId":"triage","success":true}',
            stepId: "triage",
            stepType: "",
            timelineKey: "step:triage",
            timelineLabel: "Step · triage",
            timestamp: "2026-03-25 10:00:02",
          },
          {
            agentId: "actor-1",
            description: "Step request: triage",
            eventCategory: "lifecycle",
            eventStatus: "processing",
            eventType: "CUSTOM · StepRequest",
            key: "item-1",
            payloadPreview: '{"stepId":"triage"}',
            payloadText: '{"stepId":"triage"}',
            stepId: "triage",
            stepType: "classify",
            timelineKey: "step:triage",
            timelineLabel: "Step · triage",
            timestamp: "2026-03-25 10:00:00",
          },
        ],
        key: "step:triage",
        label: "Step · triage",
        latestTimestamp: "2026-03-25 10:00:02",
        status: "processing",
      },
    ];

    render(
      <RunsTimelineView
        groups={groups}
        onSelectItem={onSelectItem}
        selectedItemKey="item-1"
      />
    );

    const selectedButton = screen.getByRole("button", {
      name: "Select trace item CUSTOM · StepRequest",
    });
    const secondButton = screen.getByRole("button", {
      name: "Select trace item CUSTOM · StepCompleted",
    });

    expect(selectedButton).toHaveAttribute("aria-pressed", "true");
    expect(secondButton).toHaveAttribute("aria-pressed", "false");
    expect(
      screen.getByText("CUSTOM · StepRequest -> CUSTOM · StepCompleted")
    ).toBeInTheDocument();
    expect(screen.getByText("Step type classify")).toBeInTheDocument();
    expect(screen.getByText('{"stepId":"triage"}')).toBeInTheDocument();
    expect(
      screen.queryByText('{"stepId":"triage","success":true}')
    ).not.toBeInTheDocument();

    fireEvent.click(secondButton);

    expect(onSelectItem).toHaveBeenCalledTimes(1);
    expect(onSelectItem).toHaveBeenCalledWith(groups[0].items[0]);
  });
});
