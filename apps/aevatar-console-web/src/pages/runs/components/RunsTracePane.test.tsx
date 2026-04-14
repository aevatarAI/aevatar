import { fireEvent, render, screen } from "@testing-library/react";
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

    expect(container.querySelector(".runs-trace-tabs")).toHaveStyle({
      flex: "1",
      minHeight: "0",
    });
    const contentNode = container.querySelector(
      ".runs-trace-tabs .ant-tabs-content-holder"
    );
    expect(contentNode).not.toBeNull();
    expect(contentNode).toHaveStyle({
      flex: "1",
      minHeight: "0",
      overflow: "hidden",
    });
    const styleNode = container.querySelector("style");
    expect(styleNode?.textContent).toContain(".ant-tabs-tabpane-hidden");
    expect(styleNode?.textContent).toContain("display: none !important");
  });

  it("shows only the active trace pane when switching tabs", () => {
    const Harness = () => {
      const [view, setView] = React.useState<"timeline" | "messages" | "events">(
        "timeline"
      );

      return (
        <RunsTracePane
          consoleView={view}
          eventConsoleView={<div>events panel</div>}
          eventCount={3}
          hasPendingInteraction={false}
          messageConsoleView={<div>messages panel</div>}
          messageCount={2}
          onConsoleViewChange={(key) => setView(key)}
          timelineView={<div>timeline panel</div>}
        />
      );
    };

    const { queryByText } = render(<Harness />);

    expect(screen.getByText("timeline panel")).toBeTruthy();
    expect(queryByText("messages panel")).toBeNull();
    expect(queryByText("events panel")).toBeNull();

    fireEvent.click(screen.getByRole("tab", { name: "Messages" }));

    expect(screen.getByText("messages panel")).toBeTruthy();
    expect(queryByText("timeline panel")).toBeNull();
    expect(queryByText("events panel")).toBeNull();
  });
});
