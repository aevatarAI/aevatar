import type { ProFormInstance } from "@ant-design/pro-components";
import {
  ProDescriptions,
  ProForm,
  ProFormSwitch,
  ProFormTextArea,
} from "@ant-design/pro-components";
import { Button, Space, Tag, Typography } from "antd";
import React, { useRef } from "react";
import {
  embeddedPanelStyle,
} from "@/shared/ui/proComponents";
import { AevatarCompactTag } from "@/shared/ui/compactText";
import type {
  HumanInputRecord,
  ResumeFormValues,
  SignalFormValues,
  WaitingSignalRecord,
} from "../runWorkbenchConfig";
import {
  humanInputColumns,
  waitingSignalColumns,
} from "../runWorkbenchConfig";
import { isHumanApprovalSuspension } from "../runEventPresentation";
import { t } from "@/shared/i18n/messages";

type RunsActionRequiredPanelProps = {
  humanInputRecord?: HumanInputRecord;
  onSubmitResume: (values: ResumeFormValues) => Promise<boolean>;
  onSubmitSignal: (values: SignalFormValues) => Promise<boolean>;
  resuming: boolean;
  signaling: boolean;
  variant?: "chat" | "card";
  waitingSignalRecord?: WaitingSignalRecord;
};

const actionRequiredCardStyle: React.CSSProperties = {
  background: "rgba(250, 173, 20, 0.08)",
  border: "1px solid rgba(250, 173, 20, 0.28)",
  borderRadius: 16,
  display: "flex",
  flexDirection: "column",
  gap: 14,
  padding: 16,
};

const actionRequiredHeaderStyle: React.CSSProperties = {
  alignItems: "flex-start",
  display: "flex",
  flexDirection: "column",
  gap: 8,
  width: "100%",
};

const RunsActionRequiredPanel: React.FC<RunsActionRequiredPanelProps> = ({
  humanInputRecord,
  onSubmitResume,
  onSubmitSignal,
  resuming,
  signaling,
  variant = "card",
  waitingSignalRecord,
}) => {
  const resumeFormRef = useRef<
    ProFormInstance<ResumeFormValues> | undefined
  >(undefined);
  const signalFormRef = useRef<
    ProFormInstance<SignalFormValues> | undefined
  >(undefined);

  if (!humanInputRecord && !waitingSignalRecord) {
    return null;
  }

  return (
    <div style={actionRequiredCardStyle}>
      <div style={actionRequiredHeaderStyle}>
        <Typography.Text strong>{t("pages.runs.runsactionrequiredpanel.action.required", "Action required")}</Typography.Text>
        <Space wrap size={[6, 6]}>
          {humanInputRecord ? (
            <Tag color="warning">
              {isHumanApprovalSuspension(humanInputRecord.suspensionType)
                ? t("pages.runs.runsactionrequiredpanel.approval.required", "Approval required")
                : t("pages.runs.runsactionrequiredpanel.human.input.required", "Human input required")}
            </Tag>
          ) : null}
          {waitingSignalRecord ? (
            <Tag color="warning">{t("pages.runs.runsactionrequiredpanel.signal.required", "Signal required")}</Tag>
          ) : null}
        </Space>
      </div>
      <Space direction="vertical" size={12} style={{ width: "100%" }}>
        {humanInputRecord ? (
          <div style={embeddedPanelStyle}>
            <Space direction="vertical" size={16} style={{ width: "100%" }}>
              <div>
                <Typography.Text strong>
                  {isHumanApprovalSuspension(humanInputRecord.suspensionType)
                    ? t("pages.runs.runsactionrequiredpanel.review.and.continue", "Review and continue the run")
                    : t("pages.runs.runsactionrequiredpanel.respond.before.continue", "Respond before the run can continue")}
                </Typography.Text>
                <Typography.Paragraph
                  style={{ margin: "4px 0 0" }}
                  type="secondary"
                >
                  {t("pages.runs.runsactionrequiredpanel.this.run.is.blocked.on", "This run is blocked on operator action.")}</Typography.Paragraph>
              </div>
              {variant === "card" ? (
                <ProDescriptions<HumanInputRecord>
                  column={1}
                  dataSource={humanInputRecord}
                  columns={humanInputColumns}
                />
              ) : (
                <Space wrap size={[6, 6]}>
                  <AevatarCompactTag
                    color="default"
                    value={humanInputRecord.stepId || "step unknown"}
                  />
                  <AevatarCompactTag
                    color="default"
                    value={humanInputRecord.runId || "run unknown"}
                  />
                  <Tag>{`${humanInputRecord.timeoutSeconds || 0}s timeout`}</Tag>
                </Space>
              )}
              <ProForm<ResumeFormValues>
                key={`${humanInputRecord.runId}-${humanInputRecord.stepId}`}
                formRef={resumeFormRef}
                initialValues={{ approved: true, userInput: "" }}
                layout="vertical"
                onFinish={async (values) => {
                  const succeeded = await onSubmitResume(values);
                  if (succeeded) {
                    resumeFormRef.current?.setFieldsValue({
                      approved: true,
                      userInput: "",
                    });
                  }
                  return succeeded;
                }}
                submitter={false}
              >
                <ProFormSwitch
	                  label={
	                    isHumanApprovalSuspension(humanInputRecord.suspensionType)
	                      ? t("pages.runs.runsactionrequiredpanel.approved", "Approved")
	                      : t("pages.runs.runsactionrequiredpanel.continue.run", "Continue run")
	                  }
                  name="approved"
                />
                <ProFormTextArea
                  fieldProps={{ rows: 4 }}
                  label={t("pages.runs.runsactionrequiredpanel.operator.response", "Operator response")}
                  name="userInput"
                  placeholder={t("pages.runs.runsactionrequiredpanel.optional.response.for.the.blocked", "Optional response for the blocked step")}
                />
              </ProForm>
              <Space wrap>
                <Button
                  loading={resuming}
                  onClick={() => resumeFormRef.current?.submit?.()}
                  type="primary"
                >
                  {isHumanApprovalSuspension(humanInputRecord.suspensionType)
                    ? t("pages.runs.runsactionrequiredpanel.approve.and.continue", "Approve and continue")
                    : t("pages.runs.runsactionrequiredpanel.submit.response", "Submit response")}
                </Button>
              </Space>
            </Space>
          </div>
        ) : null}

        {waitingSignalRecord ? (
          <div style={embeddedPanelStyle}>
            <Space direction="vertical" size={16} style={{ width: "100%" }}>
              <div>
                <Typography.Text strong>{t("pages.runs.runsactionrequiredpanel.send.the.expected.signal", "Send the expected signal")}</Typography.Text>
                <Typography.Paragraph
                  style={{ margin: "4px 0 0" }}
                  type="secondary"
                >
                  {t("pages.runs.runsactionrequiredpanel.the.run.is.paused.until", "The run is paused until this signal arrives.")}</Typography.Paragraph>
              </div>
              {variant === "card" ? (
                <ProDescriptions<WaitingSignalRecord>
                  column={1}
                  dataSource={waitingSignalRecord}
                  columns={waitingSignalColumns}
                />
              ) : (
                <Space wrap size={[6, 6]}>
                  <AevatarCompactTag
                    color="default"
                    value={waitingSignalRecord.signalName || "signal unknown"}
                  />
                  <AevatarCompactTag
                    color="default"
                    value={waitingSignalRecord.stepId || "step unknown"}
                  />
                  <AevatarCompactTag
                    color="default"
                    value={waitingSignalRecord.runId || "run unknown"}
                  />
                </Space>
              )}
              <ProForm<SignalFormValues>
                key={`${waitingSignalRecord.runId}-${waitingSignalRecord.stepId}`}
                formRef={signalFormRef}
                initialValues={{ payload: "" }}
                layout="vertical"
                onFinish={async (values) => {
                  const succeeded = await onSubmitSignal(values);
                  if (succeeded) {
                    signalFormRef.current?.setFieldsValue({ payload: "" });
                  }
                  return succeeded;
                }}
                submitter={false}
              >
                <ProFormTextArea
                  fieldProps={{ rows: 4 }}
                  label={t("pages.runs.runsactionrequiredpanel.signal.payload", "Signal payload")}
                  name="payload"
                  placeholder={t("pages.runs.runsactionrequiredpanel.optional.payload.for.the.expected", "Optional payload for the expected signal")}
                />
              </ProForm>
              <Space wrap>
                <Button
                  loading={signaling}
                  onClick={() => signalFormRef.current?.submit?.()}
                  type="primary"
                >
                  {t("pages.runs.runsactionrequiredpanel.send.signal", "Send signal")}</Button>
              </Space>
            </Space>
          </div>
        ) : null}
      </Space>
    </div>
  );
};

export default RunsActionRequiredPanel;
