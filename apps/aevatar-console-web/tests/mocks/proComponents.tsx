import { ArrowLeftOutlined } from '@ant-design/icons';
import {
  Descriptions,
  Empty,
  Form,
  Input,
  Select,
  Space,
  Spin,
  Switch,
  Typography,
} from 'antd';
import React from 'react';

type KeyLike = string | number;

type MetaConfig<T> = {
  dataIndex?: keyof T | string;
  render?: (dom: React.ReactNode, record: T, index: number) => React.ReactNode;
};

type ProListProps<T> = {
  dataSource?: T[];
  itemCardProps?: {
    bodyStyle?: React.CSSProperties;
    style?: React.CSSProperties;
  };
  locale?: {
    emptyText?: React.ReactNode;
  };
  metas?: {
    actions?: MetaConfig<T>;
    avatar?: MetaConfig<T>;
    content?: MetaConfig<T>;
    description?: MetaConfig<T>;
    subTitle?: MetaConfig<T>;
    title?: MetaConfig<T>;
  };
  renderItem?: (item: T, index: number) => React.ReactNode;
  rowKey?: keyof T | ((item: T) => KeyLike);
};

type ProCardProps = {
  bodyStyle?: React.CSSProperties;
  children?: React.ReactNode;
  extra?: React.ReactNode;
  ghost?: boolean;
  style?: React.CSSProperties;
  title?: React.ReactNode;
};

type PageContainerProps = {
  children?: React.ReactNode;
  childrenContentStyle?: React.CSSProperties;
  className?: string;
  content?: React.ReactNode;
  extra?: React.ReactNode | React.ReactNode[];
  onBack?: () => void;
  pageHeaderRender?: false;
  style?: React.CSSProperties;
  title?: React.ReactNode;
};

type ProDescriptionsProps<T> = {
  column?: number;
  dataSource?: T;
  items?: Array<{
    dataIndex?: keyof T | string;
    key?: React.Key;
    render?: (dom: React.ReactNode, record: T) => React.ReactNode;
    title?: React.ReactNode;
  }>;
};

type ProFormProps<T extends object> = {
  children?: React.ReactNode;
  formRef?: React.Ref<any>;
  initialValues?: Partial<T>;
  onFinish?: (values: T) => void | Promise<void>;
};

type ProFieldProps = {
  fieldProps?: Record<string, unknown>;
  label?: React.ReactNode;
  name?: string;
  options?: Array<{ label: React.ReactNode; value: string }>;
};

function resolveRecordValue<T>(
  record: T,
  config: MetaConfig<T> | undefined,
  index: number,
): React.ReactNode {
  if (!config) {
    return null;
  }

  const value =
    config.dataIndex && record
      ? (record as Record<string, unknown>)[String(config.dataIndex)]
      : null;

  return config.render
    ? config.render(value as React.ReactNode, record, index)
    : (value as React.ReactNode);
}

function resolveKey<T>(item: T, rowKey: ProListProps<T>['rowKey'], index: number): React.Key {
  if (typeof rowKey === 'function') {
    return rowKey(item);
  }
  if (typeof rowKey === 'string') {
    return ((item as Record<string, unknown>)[rowKey] as React.Key) ?? index;
  }
  return index;
}

function normalizeActionNode(action: React.ReactNode): React.ReactNode {
  if (React.isValidElement(action) && 'icon' in (action.props as Record<string, unknown>)) {
    return React.cloneElement(
      action as React.ReactElement<Record<string, unknown>>,
      { icon: undefined },
    );
  }

  return action;
}

export const enUSIntl = {
  getMessage: (_id: string, defaultMessage: string) => defaultMessage,
  locale: 'en-US',
};

export const ProConfigProvider: React.FC<{ children?: React.ReactNode }> = ({
  children,
}) => <>{children}</>;

export const PageLoading: React.FC<{ fullscreen?: boolean; tip?: React.ReactNode }> = ({
  tip,
}) => (
  <div aria-busy="true" data-testid="page-loading">
    <Spin />
    {tip ? <span>{tip}</span> : null}
  </div>
);

export const PageContainer: React.FC<PageContainerProps> = ({
  children,
  childrenContentStyle,
  className,
  content,
  extra,
  onBack,
  pageHeaderRender,
  style,
  title,
}) => (
  <div className={className} style={style}>
    {pageHeaderRender === false ? null : (
      <div data-testid="page-container-header">
        <Space align="start" direction="vertical" size={8} style={{ width: '100%' }}>
          <div style={{ alignItems: 'center', display: 'flex', gap: 8 }}>
            {onBack ? (
              <button aria-label="Back" onClick={onBack} type="button">
                <ArrowLeftOutlined />
              </button>
            ) : null}
            {title ? <div>{title}</div> : null}
          </div>
          {content ? <div>{content}</div> : null}
          {Array.isArray(extra) ? <div>{extra}</div> : extra ? <div>{extra}</div> : null}
        </Space>
      </div>
    )}
    <div style={childrenContentStyle}>{children}</div>
  </div>
);

export const ProCard: React.FC<ProCardProps> = ({
  bodyStyle,
  children,
  extra,
  style,
  title,
}) => (
  <div style={style}>
    {title || extra ? (
      <div
        style={{
          alignItems: 'flex-start',
          display: 'flex',
          gap: 12,
          justifyContent: 'space-between',
        }}
      >
        {title ? <div>{title}</div> : null}
        {extra ? <div>{extra}</div> : null}
      </div>
    ) : null}
    <div style={bodyStyle}>{children}</div>
  </div>
);

export function ProList<T>({
  dataSource = [],
  itemCardProps,
  locale,
  metas,
  renderItem,
  rowKey,
}: ProListProps<T>) {
  if (dataSource.length === 0) {
    return <>{locale?.emptyText ?? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} />}</>;
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      {dataSource.map((item, index) => {
        if (renderItem) {
          return (
            <React.Fragment key={resolveKey(item, rowKey, index)}>
              {renderItem(item, index)}
            </React.Fragment>
          );
        }

        const title = resolveRecordValue(item, metas?.title, index);
        const subTitle = resolveRecordValue(item, metas?.subTitle, index);
        const description = resolveRecordValue(item, metas?.description, index);
        const content = resolveRecordValue(item, metas?.content, index);
        const avatar = resolveRecordValue(item, metas?.avatar, index);
        const actions = resolveRecordValue(item, metas?.actions, index);

        const normalizedActions = Array.isArray(actions)
          ? actions.map(normalizeActionNode)
          : actions
            ? [normalizeActionNode(actions)]
            : [];

        return (
          <div
            key={resolveKey(item, rowKey, index)}
            style={{
              ...itemCardProps?.style,
              display: 'flex',
              flexDirection: 'column',
              gap: 12,
            }}
          >
            <div style={itemCardProps?.bodyStyle}>
              <div style={{ display: 'flex', gap: 12 }}>
                {avatar ? <div>{avatar}</div> : null}
                <div style={{ display: 'flex', flex: 1, flexDirection: 'column', gap: 8 }}>
                  {title ? <div>{title}</div> : null}
                  {subTitle ? <div>{subTitle}</div> : null}
                  {description ? <div>{description}</div> : null}
                  {content ? <div>{content}</div> : null}
                </div>
              </div>
            </div>
            {normalizedActions.length > 0 ? <div>{normalizedActions}</div> : null}
          </div>
        );
      })}
    </div>
  );
}

export function ProDescriptions<T>({
  column,
  dataSource,
  items = [],
}: ProDescriptionsProps<T>) {
  return (
    <Descriptions column={column}>
      {items.map((item, index) => {
        const rawValue =
          item.dataIndex && dataSource
            ? (dataSource as Record<string, unknown>)[String(item.dataIndex)]
            : null;
        const value = item.render && dataSource
          ? item.render(rawValue as React.ReactNode, dataSource)
          : rawValue;
        const itemKey =
          item.key ??
          (typeof item.dataIndex === 'string' ? item.dataIndex : undefined) ??
          index;

        return (
          <Descriptions.Item
            key={itemKey}
            label={item.title}
          >
            {value as React.ReactNode}
          </Descriptions.Item>
        );
      })}
    </Descriptions>
  );
}

export const ProForm = React.forwardRef<any, ProFormProps<any>>(function ProForm(
  { children, formRef, initialValues, onFinish },
  ref,
) {
  const [form] = Form.useForm();
  const targetRef = formRef ?? ref;

  React.useImperativeHandle(targetRef, () => ({
    getFieldValue: form.getFieldValue,
    getFieldsValue: form.getFieldsValue,
    resetFields: form.resetFields,
    setFieldValue: form.setFieldValue,
    setFieldsValue: form.setFieldsValue,
    submit: form.submit,
    validateFields: form.validateFields,
  }));

  return (
    <Form form={form} initialValues={initialValues} layout="vertical" onFinish={onFinish}>
      {children}
    </Form>
  );
});

export const ProFormText: React.FC<ProFieldProps> = ({
  fieldProps,
  label,
  name,
}) => (
  <Form.Item label={label} name={name}>
    <Input {...fieldProps} />
  </Form.Item>
);

export const ProFormTextArea: React.FC<ProFieldProps> = ({
  fieldProps,
  label,
  name,
}) => (
  <Form.Item label={label} name={name}>
    <Input.TextArea {...fieldProps} />
  </Form.Item>
);

export const ProFormSelect: React.FC<ProFieldProps> = ({
  fieldProps,
  label,
  name,
  options,
}) => (
  <Form.Item label={label} name={name}>
    <Select options={options} {...fieldProps} />
  </Form.Item>
);

export const ProFormSwitch: React.FC<ProFieldProps> = ({
  fieldProps,
  label,
  name,
}) => (
  <Form.Item label={label} name={name} valuePropName="checked">
    <Switch {...fieldProps} />
  </Form.Item>
);

export default {
  PageContainer,
  PageLoading,
  ProCard,
  ProConfigProvider,
  ProDescriptions,
  ProForm,
  ProFormSelect,
  ProFormSwitch,
  ProFormText,
  ProFormTextArea,
  ProList,
  enUSIntl,
};
