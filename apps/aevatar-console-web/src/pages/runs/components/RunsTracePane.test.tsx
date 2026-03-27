import { render } from "@testing-library/react";
import React from "react";
import RunsTracePane from "./RunsTracePane";

describe("RunsTracePane", () => {
  it("keeps the tab viewport stretchable so inner panes can scroll", () => {
    const { container } = render(
      <RunsTracePane
        consoleView="timeline"
        eventConsoleView={<div>events</div>}
        eventCount={3}
        hasPendingInteraction={false}
        messageConsoleView={<div>messages</div>}
        messageCount={2}
        onConsoleViewChange={() => undefined}
        timelineView={<div>timeline</div>}
      />
    );

    expect(container.querySelector(".ant-tabs")).toHaveStyle({
      flex: "1",
      minHeight: "0",
    });
    expect(container.querySelector(".ant-tabs-content-holder")).toHaveStyle({
      flex: "1",
      minHeight: "0",
      overflow: "hidden",
    });
  });
});
