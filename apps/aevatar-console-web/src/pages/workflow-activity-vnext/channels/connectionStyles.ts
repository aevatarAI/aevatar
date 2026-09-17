export const channelConnectionCss = `
.channels__main--connect .wa-vnext__header { display: none; }
.channels__main--connect .channels__content { max-width: 1080px; padding-top: 32px; }
.channels__connect-heading { margin-bottom: 28px; }
.channels__connect-heading .channels__breadcrumb { margin-bottom: 8px; }
.channels__connect-heading h1 { color: var(--wa-ink); font-size: 28px; font-weight: 650; line-height: 36px; margin: 0 0 8px; }
.channels__connect-heading > p { color: var(--wa-muted); font-size: 14px; line-height: 22px; margin: 0; }
.channels__connection-form { --channel-field-height: 44px; background: var(--wa-surface); border: 1px solid var(--channels-border); border-radius: var(--wa-radius); padding: 28px; }
.channels__connection-form h2 { color: var(--wa-ink); font-size: 14px; line-height: 22px; margin: 0; }
.channels__form-section-title { margin-bottom: 20px !important; }
.channels__field { margin-bottom: 24px; min-width: 0; }
.channels__name-fields { display: grid; gap: 20px; grid-template-columns: repeat(2, minmax(0, 1fr)); }
.channels__field-heading { align-items: baseline; display: flex; flex-wrap: wrap; gap: 8px; justify-content: space-between; margin-bottom: 8px; }
.channels__field-heading label { color: var(--wa-ink); font-size: 12px; font-weight: 600; }
.channels__field-heading label > span { color: var(--wa-muted); font-size: 11px; font-weight: 400; margin-left: 5px; }
.channels__field-heading label > .channels__required { color: var(--wa-blue); margin-left: 2px; }
.channels__field-heading a { color: var(--wa-blue); font-size: 11px; }
.channels__connection-form .ant-input, .channels__connection-form .ant-input-affix-wrapper { border-color: var(--channels-border); border-radius: 5px; min-height: var(--channel-field-height); }
.channels__connection-form .ant-input-affix-wrapper .ant-input { min-height: auto; }
.channels__form-help { color: var(--wa-muted); font-size: 11px; line-height: 18px; margin: 8px 0 0; }
.channels__form-error { color: var(--wa-red); font-size: 12px; line-height: 20px; margin: 8px 0 0; }
.channels__connection-form button.ant-input-password-icon { background: transparent; border: 0; cursor: pointer; padding: 4px; }
.channels__connection-form button.ant-input-password-icon:focus-visible { outline: 2px solid var(--wa-blue); outline-offset: 2px; }
.channels__services-heading { align-items: center; display: flex; gap: 16px; justify-content: space-between; }
.channels__services-heading > span { color: var(--wa-blue); font-size: 11px; }
.channels__service-picker { border: 1px solid var(--channels-border); border-radius: 5px; margin-top: 12px; padding: 10px 10px 0; }
.channels__service-picker > .ant-input-affix-wrapper { background: var(--wa-subtle); min-height: 34px; }
.channels__service-picker .ant-input { background: transparent; font-size: 12px; }
.channels__service-picker .ant-input-prefix { color: var(--wa-muted); margin-right: 8px; }
.channels__service-bulk { align-items: center; border-bottom: 1px solid var(--channels-border); display: flex; min-height: var(--channel-field-height); padding: 8px; }
.channels__service-bulk .ant-checkbox-wrapper { flex: 1; font-size: 12px; font-weight: 500; }
.channels__service-options { max-height: 240px; overflow-y: auto; padding-top: 8px; scrollbar-gutter: stable; }
.channels__service-option { align-items: center; border-bottom: 1px solid var(--channels-border); display: flex; gap: 12px; min-height: 56px; padding: 8px; }
.channels__service-option:last-child { border-bottom: 0; }
.channels__service-option--selected { background: var(--wa-blue-bg); }
.channels__service-option .ant-checkbox-wrapper { align-items: flex-start; flex: 1; min-width: 0; }
.channels__service-option .ant-checkbox { margin-top: 3px; }
.channels__service-name { display: block; font-size: 12px; font-weight: 600; line-height: 20px; overflow-wrap: anywhere; }
.channels__service-slug { color: var(--wa-muted); display: block; font-size: 11px; line-height: 18px; overflow-wrap: anywhere; }
.channels__service-source { color: var(--wa-muted); flex: 0 1 150px; font-size: 11px; overflow-wrap: anywhere; text-align: right; }
.channels__service-state { color: var(--wa-muted); font-size: 12px; line-height: 20px; margin: 12px 0 0; padding: 20px 12px; }
.channels__service-state p { margin: 0 0 12px; }
.channels__form-actions { align-items: center; border-top: 1px solid var(--channels-border); display: flex; gap: 12px; justify-content: space-between; margin-top: 28px; padding-top: 20px; }
.channels__form-actions .ant-btn { font-size: 12px; min-height: 40px; padding-inline: 18px; }
.channels__form-actions .ant-btn-primary { min-width: 128px; }
.channels__form-actions .ant-btn-primary:not(:disabled) { background: var(--wa-blue); border-color: var(--wa-blue); }
.channels__form-actions .ant-btn-primary:not(:disabled):hover { background: var(--wa-blue); border-color: var(--wa-blue); filter: brightness(.94); }
.channels__connection-form .ant-select { font-size: 13px; }
.channels__connection-form .ant-select-outlined { border-color: var(--channels-border); }
.channels__connection-status { border-top: 1px solid var(--channels-border); color: var(--wa-muted); font-size: 12px; line-height: 20px; margin-top: 24px; padding-top: 16px; }
.channels__connection-status p { margin: 0 0 12px; }
.channels__connection-form .ant-btn { white-space: normal; height: auto; }
.channels__connection-form .channels__skill-controls > .ant-btn { height: 40px; box-shadow: none; }
.channels__bot-summary .channels__dot { display: none; }
@media (max-width: 767px) {
  .channels__name-fields { gap: 0; grid-template-columns: 1fr; }
  .channels__main--connect .channels__content { padding-top: 20px; }
  .channels__connect-heading h1 { font-size: 24px; line-height: 32px; }
  .channels__connect-heading { margin-bottom: 20px; }
  .channels__connection-form { padding: 20px; }
  .channels__service-option { align-items: flex-start; gap: 8px; padding-inline: 2px; }
  .channels__service-bulk { padding-inline: 2px; }
  .channels__service-source { flex-basis: 80px; padding-top: 3px; }
  .channels__form-actions .ant-btn { padding-inline: 12px; }
}
`;
