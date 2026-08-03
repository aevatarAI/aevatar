import React, { useState } from "react";
import { t } from "@/shared/i18n/messages";
import type {
  ChatActionSummary,
  ChatActorProjection,
  ChatActorStep,
  ChatPendingApproval,
  ChatPendingInput,
  ChatServiceConnectActionRequest,
} from "./chatActorState";
import { chatActionIdentityKey } from "./chatActorState";
import type { ChatInputAnswer } from "./chatApi";

type ActionReport = {
  actionRequestId: string;
  originTurnId: string;
  disposition: "completed" | "declined" | "failed" | "cancelled" | "expired";
  resource?: { userService: { userServiceId: string } };
};

export type ChatActionJourney = {
  report?: ActionReport;
  busy?: boolean;
  error?: string;
  baseline?: ReadonlySet<string>;
};

type Props = {
  projection: ChatActorProjection | null;
  actionJourneys?: ReadonlyMap<string, ChatActionJourney>;
  disabled?: boolean;
  onInputResolve: (answer: ChatInputAnswer, input: ChatPendingInput) => void;
  onApprovalResolve: (
    approved: boolean,
    approval: ChatPendingApproval,
    reason?: string
  ) => void;
  onStop: () => void;
  onSteer: (instruction: string) => void;
  onRetry: (step: ChatActorStep) => void;
  onSkip: (step: ChatActorStep) => void;
  onActionOpen: (request: ChatServiceConnectActionRequest) => void;
  onActionRefresh: (request: ChatServiceConnectActionRequest) => void;
  onActionConnectCredential: (
    request: ChatServiceConnectActionRequest,
    credential: string
  ) => Promise<void>;
  onActionReport: (
    request: ChatServiceConnectActionRequest,
    disposition: ActionReport["disposition"]
  ) => void;
};

const buttonStyle: React.CSSProperties = {
  background: "#fff",
  border: "1px solid #d8dee8",
  borderRadius: 7,
  cursor: "pointer",
  fontSize: 12,
  minHeight: 30,
  padding: "5px 10px",
};

export function ChatActorControls({
  projection,
  actionJourneys = new Map(),
  disabled = false,
  onInputResolve,
  onApprovalResolve,
  onStop,
  onSteer,
  onRetry,
  onSkip,
  onActionOpen,
  onActionRefresh,
  onActionConnectCredential,
  onActionReport,
}: Props): React.ReactElement | null {
  const [selectedOptionIds, setSelectedOptionIds] = useState<string[]>([]);
  const [freeText, setFreeText] = useState("");
  const [approvalReason, setApprovalReason] = useState("");
  const [steering, setSteering] = useState("");
  const steps = [...(projection?.steps.values() ?? [])];
  const canStop = steps.some((step) => step.availableActions?.stop === true);
  const active = projection?.activeTurn?.status === "active";
  const actions = [...(projection?.actions.values() ?? [])].filter(
    (action) => action.action === "service.connect" && action.request
  );
  const hasControls = Boolean(
    projection?.pendingInput ||
      projection?.pendingApproval ||
      canStop ||
      active ||
      actions.length ||
      steps.some(
        (step) =>
          step.availableActions?.retry || step.availableActions?.skip
      )
  );
  if (!projection || !hasControls) return null;

  const pendingInput = projection.pendingInput;
  const pendingApproval = projection.pendingApproval;
  return (
    <section
      aria-label={t(
        "pages.chat.actorControls.actorControls",
        "Actor controls"
      )}
      style={{ display: "flex", flexDirection: "column", gap: 10 }}
    >
      {pendingInput ? (
        <ControlCard
          title={t(
            "pages.chat.actorControls.inputRequired",
            "Input required"
          )}
        >
          <div>{pendingInput.prompt}</div>
          {pendingInput.options.map((option) => (
            <label key={option.optionId} style={{ display: "block" }}>
              <input
                checked={selectedOptionIds.includes(option.optionId)}
                disabled={disabled}
                name={`actor-input-${pendingInput.requestId}`}
                onChange={(event) => {
                  if (pendingInput.multiSelect) {
                    setSelectedOptionIds((current) =>
                      event.target.checked
                        ? [...new Set([...current, option.optionId])]
                        : current.filter((id) => id !== option.optionId)
                    );
                  } else {
                    setSelectedOptionIds(
                      event.target.checked ? [option.optionId] : []
                    );
                  }
                }}
                type={pendingInput.multiSelect ? "checkbox" : "radio"}
              />{" "}
              {option.label}
              {option.description ? ` — ${option.description}` : ""}
            </label>
          ))}
          {pendingInput.allowFreeText ? (
            <input
              aria-label={t(
                "pages.chat.actorControls.freeTextAnswer",
                "Free text answer"
              )}
              disabled={disabled}
              onChange={(event) => setFreeText(event.target.value)}
              value={freeText}
            />
          ) : null}
          <button
            disabled={
              disabled || (!selectedOptionIds.length && !freeText.trim())
            }
            onClick={() =>
              onInputResolve(
                freeText.trim()
                  ? { freeText: freeText.trim() }
                  : { selectedOptionIds },
                pendingInput
              )
            }
            style={buttonStyle}
            type="button"
          >
            {t(
              "pages.chat.actorControls.submitAnswer",
              "Submit answer"
            )}
          </button>
        </ControlCard>
      ) : null}

      {pendingApproval ? (
        <ControlCard
          title={t(
            "pages.chat.actorControls.approvalRequired",
            "Approval required"
          )}
        >
          <div>
            {pendingApproval.action || pendingApproval.toolName}
            {pendingApproval.target
              ? ` · ${pendingApproval.target}`
              : ""}
          </div>
          <input
            aria-label={t(
              "pages.chat.actorControls.approvalReason",
              "Approval reason"
            )}
            disabled={disabled}
            onChange={(event) => setApprovalReason(event.target.value)}
            placeholder={t(
              "pages.chat.actorControls.optionalReason",
              "Optional reason"
            )}
            value={approvalReason}
          />
          <div style={{ display: "flex", gap: 8 }}>
            <button
              disabled={disabled}
              onClick={() =>
                onApprovalResolve(
                  true,
                  pendingApproval,
                  approvalReason.trim() || undefined
                )
              }
              style={buttonStyle}
              type="button"
            >
              {t("pages.chat.actorControls.approve", "Approve")}
            </button>
            <button
              disabled={disabled}
              onClick={() =>
                onApprovalResolve(
                  false,
                  pendingApproval,
                  approvalReason.trim() || undefined
                )
              }
              style={buttonStyle}
              type="button"
            >
              {t("pages.chat.actorControls.reject", "Reject")}
            </button>
          </div>
        </ControlCard>
      ) : null}

      {steps.map((step) =>
        step.availableActions?.retry || step.availableActions?.skip ? (
          <ControlCard
            key={step.stepId}
            title={String(step.description || step.stepId)}
          >
            <div style={{ display: "flex", gap: 8 }}>
              {step.availableActions.retry ? (
                <button
                  aria-label={t(
                    "pages.chat.actorControls.retryStep",
                    "Retry {step}",
                    { step: String(step.description || step.stepId) }
                  )}
                  disabled={disabled}
                  onClick={() => onRetry(step)}
                  style={buttonStyle}
                  type="button"
                >
                  {t("pages.chat.actorControls.retry", "Retry")}
                </button>
              ) : null}
              {step.availableActions.skip ? (
                <button
                  aria-label={t(
                    "pages.chat.actorControls.skipStep",
                    "Skip {step}",
                    { step: String(step.description || step.stepId) }
                  )}
                  disabled={disabled}
                  onClick={() => onSkip(step)}
                  style={buttonStyle}
                  type="button"
                >
                  {t("pages.chat.actorControls.skip", "Skip")}
                </button>
              ) : null}
            </div>
          </ControlCard>
        ) : null
      )}

      {actions.map((action) => (
        <ActionCard
          action={action}
          actorConfirmed={steps.some(
            (step) =>
              step.actionRequestId === action.actionRequestId &&
              step.kind === "postcondition" &&
              step.status === "done" &&
              step.externalEffect === "confirmed"
          )}
          disabled={disabled}
          journey={actionJourneys.get(
            chatActionIdentityKey(action.actorId, action.actionRequestId)
          )}
          key={chatActionIdentityKey(action.actorId, action.actionRequestId)}
          onOpen={onActionOpen}
          onRefresh={onActionRefresh}
          onConnectCredential={onActionConnectCredential}
          onReport={onActionReport}
        />
      ))}

      {active ? (
        <ControlCard
          title={t(
            "pages.chat.actorControls.activeTask",
            "Active task"
          )}
        >
          <input
            aria-label={t(
              "pages.chat.actorControls.steeringInstruction",
              "Steering instruction"
            )}
            disabled={disabled}
            onChange={(event) => setSteering(event.target.value)}
            value={steering}
          />
          <div style={{ display: "flex", gap: 8 }}>
            <button
              disabled={disabled || !steering.trim()}
              onClick={() => onSteer(steering.trim())}
              style={buttonStyle}
              type="button"
            >
              {t("pages.chat.actorControls.steerTask", "Steer task")}
            </button>
            {canStop ? (
              <button
                disabled={disabled}
                onClick={onStop}
                style={buttonStyle}
                type="button"
              >
                {t("pages.chat.actorControls.stopTask", "Stop task")}
              </button>
            ) : null}
          </div>
        </ControlCard>
      ) : null}
    </section>
  );
}

function ControlCard({
  children,
  title,
}: {
  children: React.ReactNode;
  title: string;
}): React.ReactElement {
  return (
    <div
      style={{
        background: "#f8fafc",
        border: "1px solid #d8dee8",
        borderRadius: 9,
        display: "flex",
        flexDirection: "column",
        gap: 8,
        padding: 12,
      }}
    >
      <strong>{title}</strong>
      {children}
    </div>
  );
}

function ActionCard({
  action,
  actorConfirmed,
  journey,
  disabled,
  onOpen,
  onRefresh,
  onConnectCredential,
  onReport,
}: {
  action: ChatActionSummary;
  actorConfirmed: boolean;
  journey?: ChatActionJourney;
  disabled: boolean;
  onOpen: Props["onActionOpen"];
  onRefresh: Props["onActionRefresh"];
  onConnectCredential: Props["onActionConnectCredential"];
  onReport: Props["onActionReport"];
}): React.ReactElement | null {
  const [credential, setCredential] = useState("");
  const request = action.request;
  if (!request) return null;
  if (action.conflicted) {
    const serviceName =
      "catalogService" in request.params
        ? request.params.catalogService.serviceSlug
        : request.params.customService.name;
    return (
      <ControlCard
        title={t(
          "pages.chat.actorControls.connectService",
          "Connect {service}",
          { service: serviceName }
        )}
      >
        <div role="alert">
          {t(
            "pages.chat.actorControls.actionIdentityConflict",
            "Action identity conflict; this browser journey is disabled."
          )}
        </div>
      </ControlCard>
    );
  }
  const actorReport = [...(action.reports ?? [])].reverse().find((candidate) =>
    reportMatchesRequest(candidate, request)
  );
  const localReport = journey?.report;
  const report = actorReport ??
    (localReport && reportMatchesRequest(localReport, request)
      ? localReport
      : null);
  const expectedId = readUserServiceId(report?.resource);
  const proof = action.postconditionResult;
  const verified = Boolean(
    report?.disposition === "completed" &&
      (actorConfirmed ||
        (expectedId &&
          proof?.verified === true &&
          proof.actionRequestId === request.actionRequestId &&
          proof.disposition === report.disposition &&
          readUserServiceId(proof.resource) === expectedId))
  );
  const serviceName =
    "catalogService" in request.params
      ? request.params.catalogService.serviceSlug
      : request.params.customService.name;
  return (
    <ControlCard
      title={t(
        "pages.chat.actorControls.connectService",
        "Connect {service}",
        { service: serviceName }
      )}
    >
      {verified ? (
        <div>{t("pages.chat.actorControls.actorVerified", "Actor verified")}</div>
      ) : report ? (
        <div>
          {t(
            "pages.chat.actorControls.reportedWaitingProof",
            "Reported; waiting for actor verification"
          )}
        </div>
      ) : null}
      {journey?.error ? <div role="alert">{journey.error}</div> : null}
      {!verified ? (
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          {"catalogService" in request.params && !report ? (
            <>
              <input
                aria-label={t(
                  "pages.chat.actorControls.serviceCredential",
                  "{service} credential",
                  { service: serviceName }
                )}
                autoComplete="off"
                disabled={disabled || journey?.busy}
                onChange={(event) => setCredential(event.target.value)}
                type="password"
                value={credential}
              />
              <button
                disabled={disabled || journey?.busy || !credential.trim()}
                onClick={() => {
                  const value = credential;
                  setCredential("");
                  void onConnectCredential(request, value);
                }}
                style={buttonStyle}
                type="button"
              >
                {t(
                  "pages.chat.actorControls.connectNow",
                  "Connect {service}",
                  { service: serviceName }
                )}
              </button>
            </>
          ) : null}
          {!report ? (
            <button
              disabled={disabled || journey?.busy}
              onClick={() => onOpen(request)}
              style={buttonStyle}
              type="button"
            >
              {t(
                "pages.chat.actorControls.openNyxId",
                "Open NyxID connection"
              )}
            </button>
          ) : null}
          <button
            aria-label={t(
              "pages.chat.actorControls.refreshConnection",
              "Refresh connection"
            )}
            disabled={disabled || journey?.busy}
            onClick={() => onRefresh(request)}
            style={buttonStyle}
            type="button"
          >
            {t("pages.chat.actorControls.refresh", "Refresh")}
          </button>
          {!report ? (
            <>
              <button
                disabled={disabled}
                onClick={() => onReport(request, "declined")}
                style={buttonStyle}
                type="button"
              >
                {t("pages.chat.actorControls.decline", "Decline")}
              </button>
              <button
                disabled={disabled}
                onClick={() => onReport(request, "cancelled")}
                style={buttonStyle}
                type="button"
              >
                {t("pages.chat.actorControls.cancel", "Cancel")}
              </button>
            </>
          ) : null}
        </div>
      ) : null}
    </ControlCard>
  );
}

function reportMatchesRequest(
  input: unknown,
  request: ChatServiceConnectActionRequest
): input is Record<string, unknown> {
  if (!input || typeof input !== "object" || Array.isArray(input)) return false;
  const report = input as Record<string, unknown>;
  return (
    report.actionRequestId === request.actionRequestId &&
    report.originTurnId === request.originTurnId &&
    ["completed", "declined", "failed", "cancelled", "expired"].includes(
      String(report.disposition)
    )
  );
}

function readUserServiceId(input: unknown): string {
  if (!input || typeof input !== "object" || Array.isArray(input)) return "";
  const resource = input as Record<string, unknown>;
  const nested = resource.userService;
  const nestedId =
    nested && typeof nested === "object" && !Array.isArray(nested)
      ? (nested as Record<string, unknown>).userServiceId
      : undefined;
  const value = nestedId ?? resource.userServiceId;
  return typeof value === "string" ? value.trim() : "";
}
