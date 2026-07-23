import { fireEvent, render, screen } from "@testing-library/react";
import React from "react";
import {
  CONVERSATION_ROUTE_GATEWAY_VALUE,
  USER_LLM_ROUTE_GATEWAY,
} from "./chatConversationConfig";
import { ConversationLlmConfigBar } from "./chatPresentation";

describe("ConversationLlmConfigBar", () => {
  it("selects and emits the canonical Gateway conversation override", () => {
    const onRouteChange = jest.fn();
    render(
      <ConversationLlmConfigBar
        effectiveModel="gpt-5.4-mini"
        effectiveRoute={USER_LLM_ROUTE_GATEWAY}
        effectiveRouteLabel="Gateway"
        modelGroups={[]}
        modelValue={undefined}
        modelsLoading={false}
        onModelChange={jest.fn()}
        onReset={jest.fn()}
        onRouteChange={onRouteChange}
        routeOptions={[
          { label: "Gateway", value: USER_LLM_ROUTE_GATEWAY },
          { label: "OpenAI team", value: "/api/v1/proxy/s/openai-team" },
        ]}
        routeValue={USER_LLM_ROUTE_GATEWAY}
      />,
    );

    fireEvent.click(
      screen.getByRole("button", { name: "Conversation model settings" }),
    );
    const routeSelect = screen.getByLabelText(
      "Conversation route",
    ) as HTMLSelectElement;
    expect(routeSelect).toHaveValue(CONVERSATION_ROUTE_GATEWAY_VALUE);
    expect(
      Array.from(routeSelect.options).find(
        (option) => option.textContent === "Gateway",
      ),
    ).toHaveProperty("selected", true);
    expect(
      Array.from(routeSelect.options).find(
        (option) => option.textContent === "Config default",
      ),
    ).toHaveProperty("selected", false);

    fireEvent.change(routeSelect, {
      target: { value: CONVERSATION_ROUTE_GATEWAY_VALUE },
    });
    expect(onRouteChange).toHaveBeenCalledWith(USER_LLM_ROUTE_GATEWAY);
  });
});
