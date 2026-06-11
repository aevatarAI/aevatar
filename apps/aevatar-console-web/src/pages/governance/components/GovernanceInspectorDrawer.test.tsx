import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { setLocale } from "@umijs/max";
import React from "react";
import GovernanceInspectorDrawer from "./GovernanceInspectorDrawer";

describe("GovernanceInspectorDrawer", () => {
  beforeEach(() => {
    setLocale("zh-CN", false);
  });

  afterEach(() => {
    setLocale("en-US", false);
  });

  it("submits a new service binding with the current governance identity as fallback scope", async () => {
    const onCreateBinding = jest.fn(async () => undefined);

    render(
      <GovernanceInspectorDrawer
        busyAction={null}
        endpointCatalog={null}
        identity={{
          tenantId: "tenant-a",
          appId: "app-a",
          namespace: "default",
        }}
        onClose={jest.fn()}
        onCreateBinding={onCreateBinding}
        onCreateEndpoint={jest.fn(async () => undefined)}
        onCreatePolicy={jest.fn(async () => undefined)}
        onRetireBinding={jest.fn(async () => undefined)}
        onRetirePolicy={jest.fn(async () => undefined)}
        onSetEndpointExposure={jest.fn(async () => undefined)}
        onUpdateEndpoint={jest.fn(async () => undefined)}
        onUpdateBinding={jest.fn(async () => undefined)}
        onUpdatePolicy={jest.fn(async () => undefined)}
        open
        policyOptions={["policy-a"]}
        serviceId="service-alpha"
        target={{
          kind: "binding",
          mode: "create",
          record: {
            bindingId: "",
            bindingKind: "service",
            connectorRef: null,
            displayName: "",
            policyIds: [],
            retired: false,
            secretRef: null,
            serviceRef: null,
          },
        }}
      />,
    );

    fireEvent.change(screen.getByLabelText("绑定 ID"), {
      target: { value: "binding-orders-chat" },
    });
    fireEvent.change(screen.getByLabelText("显示名称"), {
      target: { value: "Orders Chat Dependency" },
    });
    fireEvent.change(screen.getByLabelText("目标服务 ID"), {
      target: { value: "orders-service" },
    });
    fireEvent.change(screen.getByLabelText("目标 Endpoint"), {
      target: { value: "chat" },
    });
    fireEvent.click(screen.getByRole("button", { name: "创建绑定" }));

    await waitFor(() => {
      expect(onCreateBinding).toHaveBeenCalledWith({
        appId: "app-a",
        bindingId: "binding-orders-chat",
        bindingKind: "service",
        displayName: "Orders Chat Dependency",
        namespace: "default",
        policyIds: [],
        service: {
          appId: "app-a",
          endpointId: "chat",
          namespace: "default",
          serviceId: "orders-service",
          tenantId: "tenant-a",
        },
        tenantId: "tenant-a",
      });
    });
  });

  it("submits endpoint metadata updates back through the endpoint catalog callback", async () => {
    const onUpdateEndpoint = jest.fn(async () => undefined);

    render(
      <GovernanceInspectorDrawer
        busyAction={null}
        endpointCatalog={{
          endpoints: [],
          serviceKey: "tenant-a/app-a/default/service-alpha",
          updatedAt: "2026-04-16T10:00:00Z",
        }}
        identity={{
          tenantId: "tenant-a",
          appId: "app-a",
          namespace: "default",
        }}
        onClose={jest.fn()}
        onCreateBinding={jest.fn(async () => undefined)}
        onCreateEndpoint={jest.fn(async () => undefined)}
        onCreatePolicy={jest.fn(async () => undefined)}
        onRetireBinding={jest.fn(async () => undefined)}
        onRetirePolicy={jest.fn(async () => undefined)}
        onSetEndpointExposure={jest.fn(async () => undefined)}
        onUpdateEndpoint={onUpdateEndpoint}
        onUpdateBinding={jest.fn(async () => undefined)}
        onUpdatePolicy={jest.fn(async () => undefined)}
        open
        policyOptions={["policy-a"]}
        serviceId="service-alpha"
        target={{
          kind: "endpoint",
          mode: "edit",
          record: {
            description: "chat entry",
            displayName: "Chat",
            endpointId: "chat",
            exposureKind: "public",
            kind: "chat",
            policyIds: ["policy-a"],
            requestTypeUrl: "type.googleapis.com/demo.ChatRequest",
            responseTypeUrl: "type.googleapis.com/demo.ChatResponse",
          },
        }}
      />,
    );

    fireEvent.change(screen.getByLabelText("显示名称"), {
      target: { value: "Chat API" },
    });
    fireEvent.change(screen.getByLabelText("描述"), {
      target: { value: "public chat entry" },
    });
    fireEvent.click(screen.getByRole("button", { name: "保存入口" }));

    await waitFor(() => {
      expect(onUpdateEndpoint).toHaveBeenCalledWith("chat", {
        description: "public chat entry",
        displayName: "Chat API",
        endpointId: "chat",
        exposureKind: "public",
        kind: "chat",
        policyIds: ["policy-a"],
        requestTypeUrl: "type.googleapis.com/demo.ChatRequest",
        responseTypeUrl: "type.googleapis.com/demo.ChatResponse",
      });
    });
  });

  it("creates the first endpoint catalog entry when the service has no endpoint catalog yet", async () => {
    const onCreateEndpoint = jest.fn(async () => undefined);

    render(
      <GovernanceInspectorDrawer
        busyAction={null}
        endpointCatalog={null}
        identity={{
          tenantId: "tenant-a",
          appId: "app-a",
          namespace: "default",
        }}
        onClose={jest.fn()}
        onCreateBinding={jest.fn(async () => undefined)}
        onCreateEndpoint={onCreateEndpoint}
        onCreatePolicy={jest.fn(async () => undefined)}
        onRetireBinding={jest.fn(async () => undefined)}
        onRetirePolicy={jest.fn(async () => undefined)}
        onSetEndpointExposure={jest.fn(async () => undefined)}
        onUpdateEndpoint={jest.fn(async () => undefined)}
        onUpdateBinding={jest.fn(async () => undefined)}
        onUpdatePolicy={jest.fn(async () => undefined)}
        open
        policyOptions={[]}
        serviceId="service-alpha"
        target={{
          kind: "endpoint",
          mode: "create",
          record: {
            description: "",
            displayName: "",
            endpointId: "",
            exposureKind: "internal",
            kind: "command",
            policyIds: [],
            requestTypeUrl: "",
            responseTypeUrl: "",
          },
        }}
      />,
    );

    fireEvent.change(screen.getByLabelText("入口 ID"), {
      target: { value: "invoke" },
    });
    fireEvent.change(screen.getByLabelText("显示名称"), {
      target: { value: "Invoke Command" },
    });
    fireEvent.change(screen.getByLabelText("请求类型"), {
      target: { value: "type.googleapis.com/demo.Invoke" },
    });
    fireEvent.click(screen.getByRole("button", { name: "创建入口" }));

    await waitFor(() => {
      expect(onCreateEndpoint).toHaveBeenCalledWith({
        description: "",
        displayName: "Invoke Command",
        endpointId: "invoke",
        exposureKind: "internal",
        kind: "command",
        policyIds: [],
        requestTypeUrl: "type.googleapis.com/demo.Invoke",
        responseTypeUrl: "",
      });
    });
  });

  it("renders retired policies as read-only facts instead of editable controls", () => {
    render(
      <GovernanceInspectorDrawer
        busyAction={null}
        endpointCatalog={null}
        identity={{
          tenantId: "tenant-a",
          appId: "app-a",
          namespace: "default",
        }}
        onClose={jest.fn()}
        onCreateBinding={jest.fn(async () => undefined)}
        onCreateEndpoint={jest.fn(async () => undefined)}
        onCreatePolicy={jest.fn(async () => undefined)}
        onRetireBinding={jest.fn(async () => undefined)}
        onRetirePolicy={jest.fn(async () => undefined)}
        onSetEndpointExposure={jest.fn(async () => undefined)}
        onUpdateEndpoint={jest.fn(async () => undefined)}
        onUpdateBinding={jest.fn(async () => undefined)}
        onUpdatePolicy={jest.fn(async () => undefined)}
        open
        policyOptions={[]}
        serviceId="service-alpha"
        target={{
          kind: "policy",
          mode: "edit",
          record: {
            activationRequiredBindingIds: [],
            displayName: "Retired Policy",
            invokeAllowedCallerServiceKeys: [],
            invokeRequiresActiveDeployment: false,
            policyId: "policy-retired",
            retired: true,
          },
        }}
      />,
    );

    expect(
      screen.getByText("这条策略已经退役，是治理目录中的历史事实，不能继续保存或再次下线。"),
    ).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "保存策略" })).toBeNull();
    expect(screen.queryByRole("button", { name: "下线策略" })).toBeNull();
  });

  it("renders retired bindings as read-only facts instead of editable controls", () => {
    render(
      <GovernanceInspectorDrawer
        busyAction={null}
        endpointCatalog={null}
        identity={{
          tenantId: "tenant-a",
          appId: "app-a",
          namespace: "default",
        }}
        onClose={jest.fn()}
        onCreateBinding={jest.fn(async () => undefined)}
        onCreateEndpoint={jest.fn(async () => undefined)}
        onCreatePolicy={jest.fn(async () => undefined)}
        onRetireBinding={jest.fn(async () => undefined)}
        onRetirePolicy={jest.fn(async () => undefined)}
        onSetEndpointExposure={jest.fn(async () => undefined)}
        onUpdateEndpoint={jest.fn(async () => undefined)}
        onUpdateBinding={jest.fn(async () => undefined)}
        onUpdatePolicy={jest.fn(async () => undefined)}
        open
        policyOptions={[]}
        serviceId="service-alpha"
        target={{
          kind: "binding",
          mode: "edit",
          record: {
            bindingId: "binding-retired",
            bindingKind: "service",
            connectorRef: null,
            displayName: "Retired Binding",
            policyIds: [],
            retired: true,
            secretRef: null,
            serviceRef: null,
          },
        }}
      />,
    );

    expect(
      screen.getByText("这条绑定已经退役，是治理目录中的历史事实，不能继续保存或再次下线。"),
    ).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "保存绑定" })).toBeNull();
    expect(screen.queryByRole("button", { name: "下线绑定" })).toBeNull();
  });

  it("does not offer a repeated public exposure action for an already-public endpoint", () => {
    render(
      <GovernanceInspectorDrawer
        busyAction={null}
        endpointCatalog={{
          endpoints: [],
          serviceKey: "tenant-a/app-a/default/service-alpha",
          updatedAt: "2026-04-16T10:00:00Z",
        }}
        identity={{
          tenantId: "tenant-a",
          appId: "app-a",
          namespace: "default",
        }}
        onClose={jest.fn()}
        onCreateBinding={jest.fn(async () => undefined)}
        onCreateEndpoint={jest.fn(async () => undefined)}
        onCreatePolicy={jest.fn(async () => undefined)}
        onRetireBinding={jest.fn(async () => undefined)}
        onRetirePolicy={jest.fn(async () => undefined)}
        onSetEndpointExposure={jest.fn(async () => undefined)}
        onUpdateEndpoint={jest.fn(async () => undefined)}
        onUpdateBinding={jest.fn(async () => undefined)}
        onUpdatePolicy={jest.fn(async () => undefined)}
        open
        policyOptions={[]}
        serviceId="service-alpha"
        target={{
          kind: "endpoint",
          mode: "edit",
          record: {
            description: "",
            displayName: "Public Invoke",
            endpointId: "invoke",
            exposureKind: "public",
            kind: "command",
            policyIds: [],
            requestTypeUrl: "type.googleapis.com/demo.Invoke",
            responseTypeUrl: "",
          },
        }}
      />,
    );

    expect(screen.getByRole("button", { name: "已公开" })).toBeDisabled();
    expect(
      screen.getByText("当前入口目录已经观察到公开状态，不再显示重复的公开切换。"),
    ).toBeInTheDocument();
  });
});
