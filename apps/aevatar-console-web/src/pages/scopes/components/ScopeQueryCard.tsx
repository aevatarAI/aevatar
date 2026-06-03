import { ProCard } from '@ant-design/pro-components';
import { Button, Input, Space, Typography, theme } from 'antd';
import React from 'react';
import { moduleCardProps } from '@/shared/ui/proComponents';
import type { ScopeQueryDraft } from './scopeQuery';
import { t } from "@/shared/i18n/messages";

type ScopeQueryCardProps = {
  activeScopeId?: string | null;
  draft: ScopeQueryDraft;
  onChange: (draft: ScopeQueryDraft) => void;
  onLoad: () => void;
  onReset?: () => void;
  resetDisabled?: boolean;
  loadLabel?: string;
  resolvedScopeId?: string | null;
  resolvedScopeSource?: string | null;
  onUseResolvedScope?: () => void;
};

const ScopeQueryCard: React.FC<ScopeQueryCardProps> = ({
  activeScopeId,
  draft,
  onChange,
  onLoad,
  onReset,
  resetDisabled,
  loadLabel = 'Load workspace',
  resolvedScopeId,
  resolvedScopeSource,
  onUseResolvedScope,
}) => {
  const normalizedDraftScopeId = draft.scopeId.trim();
  const normalizedActiveScopeId = activeScopeId?.trim() ?? '';
  const normalizedResolvedScopeId = resolvedScopeId?.trim() ?? '';
  const normalizedResolvedScopeSource = resolvedScopeSource?.trim() ?? '';
  const canUseResolvedScope =
    normalizedResolvedScopeId.length > 0 &&
    normalizedDraftScopeId !== normalizedResolvedScopeId &&
    onUseResolvedScope;
  const loadIsNoOp =
    normalizedDraftScopeId.length > 0 &&
    normalizedDraftScopeId === normalizedActiveScopeId;
  const computedResetDisabled =
    normalizedDraftScopeId === normalizedResolvedScopeId &&
    normalizedActiveScopeId === normalizedResolvedScopeId;
  const resetIsNoOp = (resetDisabled ?? computedResetDisabled) === true;
  const { token } = theme.useToken();
  const helperLabelStyle = {
    color: token.colorTextSecondary,
    fontWeight: 500,
  };
  const helperCopyStyle = {
    color: token.colorTextTertiary,
  };
  const resolvedScopeValueStyle = {
    background: token.colorFillAlter,
    border: `1px solid ${token.colorBorderSecondary}`,
    borderRadius: token.borderRadius,
    color: token.colorText,
    display: 'block',
    fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
    fontSize: 12,
    margin: 0,
    maxWidth: '100%',
    overflowWrap: 'anywhere' as const,
    padding: '6px 8px',
    whiteSpace: 'normal' as const,
    wordBreak: 'break-word' as const,
  };

  return (
    <ProCard {...moduleCardProps}>
      <div
        style={{
          alignItems: 'center',
          display: 'flex',
          flexWrap: 'wrap',
          gap: 8,
          width: '100%',
        }}
      >
        <Input
          allowClear
          placeholder={t("pages.scopes.scopequerycard.enter.workspace.id", "Enter workspace ID")}
          style={{ flex: '1 1 240px', minWidth: 0, width: '100%' }}
          value={draft.scopeId}
          onChange={(event) =>
            onChange({
              scopeId: event.target.value,
            })
          }
          onPressEnter={onLoad}
        />
        <Button disabled={!normalizedDraftScopeId || loadIsNoOp} type="primary" onClick={onLoad}>
          {loadLabel}
        </Button>
        {onReset ? (
          <Button disabled={resetDisabled ?? computedResetDisabled} onClick={onReset}>
            {t("pages.scopes.scopequerycard.reset", "reset")}</Button>
        ) : null}
      </div>
      <div
        style={{
          display: 'grid',
          gap: 8,
          marginTop: 12,
          minWidth: 0,
        }}
      >
        {normalizedResolvedScopeId ? (
          <>
            <Typography.Text style={helperLabelStyle}>
              {t("pages.scopes.scopequerycard.resolved.workspace", "Resolved workspace")}</Typography.Text>
            <Typography.Paragraph
              copyable={{ text: normalizedResolvedScopeId }}
              style={resolvedScopeValueStyle}
            >
              {normalizedResolvedScopeId}
            </Typography.Paragraph>
            {normalizedResolvedScopeSource ? (
              <Typography.Text
                style={{
                  ...helperCopyStyle,
                  display: 'block',
                  maxWidth: '100%',
                  overflowWrap: 'anywhere',
                  whiteSpace: 'normal',
                  wordBreak: 'break-word',
                }}
              >
                {t("pages.scopes.scopequerycard.the.current.session.has", "The current session has passed")}{normalizedResolvedScopeSource} {t("pages.scopes.scopequerycard.parse.out.this.workspace", "Parse out this workspace")}</Typography.Text>
            ) : null}
            {loadIsNoOp ? (
              <Typography.Text style={helperCopyStyle}>
                {t("pages.scopes.scopequerycard.this.workspace.is.currently", "This workspace is currently loaded, so \"")}{loadLabel}{t("pages.scopes.scopequerycard.will.no.longer.trigger", "\" will no longer trigger the change.")}</Typography.Text>
            ) : null}
            {resetIsNoOp ? (
              <Typography.Text style={helperCopyStyle}>
                {t("pages.scopes.scopequerycard.you.are.now.back", "You are now back to the session-resolved workspace, so \"reset\" will no longer trigger changes.")}</Typography.Text>
            ) : null}
            {canUseResolvedScope ? (
              <div>
                <Button size="small" onClick={onUseResolvedScope}>
                  {t("pages.scopes.scopequerycard.use.session.workspace", "Use session workspace")}</Button>
              </div>
            ) : null}
          </>
        ) : (
          <Typography.Text
            style={{
              ...helperCopyStyle,
              display: 'block',
              maxWidth: '100%',
              overflowWrap: 'anywhere',
              whiteSpace: 'normal',
              wordBreak: 'break-word',
            }}
          >
            {t("pages.scopes.scopequerycard.the.workspace.is.not", "The workspace is not automatically resolved in the current session. Please enter a workspace ID manually.")}</Typography.Text>
        )}
      </div>
    </ProCard>
  );
};

export default ScopeQueryCard;
