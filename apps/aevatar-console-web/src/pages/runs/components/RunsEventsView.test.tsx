import { fireEvent, render, screen } from "@testing-library/react";
import React from "react";
import type { RunEventRow } from "../runEventPresentation";
import RunsEventsView from "./RunsEventsView";

describe("RunsEventsView", () => {
  it("renders event cards, preserves selection, and opens payload previews for selected or error rows", () => {
    const onSelectItem = jest.fn();
    const rows: RunEventRow[] = [
      {
        agentId: "actor-1",
        description: "Step request: triage",
        eventCategory: "lifecycle",
        eventStatus: "processing",
        eventType: "CUSTOM · StepRequest",
        key: "item-1",
        payloadPreview: '{"stepId":"triage"}',
        payloadText: '{\n  "stepId": "triage"\n}',
        stepId: "triage",
        stepType: "classify",
        timelineKey: "step:triage",
        timelineLabel: "Step · triage",
        timestamp: "2026-04-02 16:26:23",
      },
      {
        agentId: "actor-1",
        description: "Run failed.",
        eventCategory: "error",
        eventStatus: "error",
        eventType: "RUN_ERROR",
        key: "item-2",
        payloadPreview: '{"message":"boom"}',
        payloadText: '{\n  "message": "boom"\n}',
        stepId: "",
        stepType: "",
        timelineKey: "run:lifecycle",
        timelineLabel: "Run lifecycle",
        timestamp: "2026-04-02 16:26:24",
      },
    ];

    render(
      <RunsEventsView
        onSelectItem={onSelectItem}
        rows={rows}
        selectedItemKey="item-1"
      />,
    );

    const selectedButton = screen.getByRole("button", {
      name: "Select event CUSTOM · StepRequest",
    });
    const secondButton = screen.getByRole("button", {
      name: "Select event RUN_ERROR",
    });

    expect(selectedButton).toHaveAttribute("aria-pressed", "true");
    expect(secondButton).toHaveAttribute("aria-pressed", "false");
    expect(screen.getByText("Step triage")).toBeInTheDocument();
    expect(screen.getByText("Mode classify")).toBeInTheDocument();
    expect(screen.getByText("Agent actor-1")).toBeInTheDocument();
    expect(screen.getByText(/"stepId": "triage"/)).toBeInTheDocument();
    expect(screen.getByText('{"message":"boom"}')).toBeInTheDocument();

    fireEvent.click(secondButton);

    expect(onSelectItem).toHaveBeenCalledTimes(1);
    expect(onSelectItem).toHaveBeenCalledWith(rows[1]);
  });
});
