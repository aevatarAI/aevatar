import { act, render, waitFor } from "@testing-library/react";
import { ConfigProvider } from "antd";
import React from "react";
import MainLayout from "./MainLayout";
import { history } from "@/shared/navigation/history";
import { STUDIO_HOST_BODY_CLASS } from "@/shared/studio/studioLayout";
import { aevatarThemeConfig } from "@/shared/ui/aevatarWorkbench";

function renderMainLayout(): void {
  render(
    <ConfigProvider theme={aevatarThemeConfig}>
      <MainLayout>
        <div>layout content</div>
      </MainLayout>
    </ConfigProvider>,
  );
}

describe("MainLayout", () => {
  beforeEach(() => {
    document.body.classList.remove(STUDIO_HOST_BODY_CLASS);
  });

  it("clears stale Studio host styling on non-Studio routes", async () => {
    document.body.classList.add(STUDIO_HOST_BODY_CLASS);
    window.history.replaceState({}, "", "/teams");

    renderMainLayout();

    await waitFor(() => {
      expect(document.body.classList.contains(STUDIO_HOST_BODY_CLASS)).toBe(false);
    });
  });

  it("tracks Studio route transitions while the shell stays mounted", async () => {
    window.history.replaceState({}, "", "/teams");

    renderMainLayout();

    await waitFor(() => {
      expect(document.body.classList.contains(STUDIO_HOST_BODY_CLASS)).toBe(false);
    });

    act(() => {
      history.push("/studio");
    });

    await waitFor(() => {
      expect(document.body.classList.contains(STUDIO_HOST_BODY_CLASS)).toBe(true);
    });

    act(() => {
      history.push("/teams");
    });

    await waitFor(() => {
      expect(document.body.classList.contains(STUDIO_HOST_BODY_CLASS)).toBe(false);
    });
  });
});
