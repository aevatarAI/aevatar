export const channelsCss = `
.channels__main {
  --channels-space: 24px;
  --channels-border: #e4e7ec;
  --channels-action-height: 36px;
  --channels-action-shadow: 0 1px 2px rgb(16 24 40 / 5%);
  background: var(--wa-subtle);
}
.channels__main .wa-vnext__header, .channels__content { margin-inline: auto; max-width: 1160px; width: 100%; }
.channels__main .wa-vnext__header { padding-top: 40px; }
.channels__main .wa-vnext__header h1 { font-size: 24px; line-height: 32px; }
.channels__content { padding-top: 0; }
.channels__icon { align-items: center; background: var(--wa-blue); border-radius: var(--wa-radius); color: white; display: inline-flex; flex: 0 0 auto; font-size: 22px; height: 42px; justify-content: center; width: 42px; }
.channels__connect { align-items: center; color: var(--wa-blue); display: inline-flex; font-weight: 500; gap: 8px; min-height: 28px; width: fit-content; }
.channels__connections { margin-top: 32px; }
.channels__section-heading { align-items: center; display: flex; gap: 16px; justify-content: space-between; margin-bottom: 16px; }
.channels__section-heading h2 { align-items: center; display: flex; font-size: 16px; gap: 10px; line-height: 24px; margin: 0; }
.channels__section-heading p { color: var(--wa-muted); line-height: 20px; margin: 4px 0 0; }
.channels__count { background: var(--wa-surface); border: 1px solid var(--channels-border); border-radius: 5px; color: var(--wa-muted); font-size: 11px; font-weight: 500; line-height: 20px; min-width: 24px; padding: 0 6px; text-align: center; }
.channels__table-wrap { background: var(--wa-surface); border: 1px solid var(--channels-border); border-radius: var(--wa-radius); overflow: hidden; position: relative; }
.channels__table-scroll { overflow-x: auto; }
.channels__table { border-collapse: collapse; table-layout: fixed; min-width: 960px; width: 100%; }
.channels__table th:nth-child(1) { width: 23%; }
.channels__table th:nth-child(2) { width: 10%; }
.channels__table th:nth-child(3) { width: 18%; }
.channels__table th:nth-child(4) { width: 12%; }
.channels__table th:nth-child(5) { width: 14%; }
.channels__table th:nth-child(6) { width: 10%; }
.channels__table th:last-child { text-align: right; width: 13%; }
.channels__guide { background: var(--wa-surface); border-bottom: 1px solid var(--channels-border); padding: 24px 0; }
.channels__guide-steps { display: grid; gap: 24px; grid-template-columns: repeat(3, minmax(0, 1fr)); list-style: none; margin: 24px 0 0; padding: 0; }
.channels__guide-steps li { align-items: center; display: flex; gap: 14px; min-width: 0; padding-right: 22px; position: relative; }
.channels__guide-icon { align-items: center; background: var(--wa-blue-bg); border-radius: 8px; color: var(--wa-blue); display: flex; flex: 0 0 60px; font-size: 24px; height: 60px; justify-content: center; }
.channels__guide-number { color: var(--wa-muted); font-size: 10px; font-weight: 600; }
.channels__guide-steps h3 { font-size: 13px; line-height: 20px; margin: 3px 0; }
.channels__guide-steps p { color: var(--wa-muted); font-size: 11px; line-height: 18px; margin: 0; }
.channels__guide-arrow { color: var(--wa-muted); position: absolute; right: 0; }
.channels__bind { align-items: center; background: var(--wa-blue); border: 1px solid var(--wa-blue); border-radius: var(--wa-radius); color: white; display: inline-flex; justify-content: center; min-height: 32px; padding: 0 12px; }
.channels__bind:hover { color: white; filter: brightness(.92); }
.channels__bot-summary { align-items: center; border-bottom: 1px solid var(--channels-border); display: flex; gap: 16px; justify-content: space-between; margin-bottom: 24px; padding-bottom: 24px; }
.channels__bot-summary .channels__identity { min-width: 0; }
.channels__skill-controls { align-items: center; display: flex; gap: 8px; }
.channels__skill-controls .ant-select { flex: 1; min-width: 0; height: 40px; }
.channels__skill-controls > .ant-btn { flex: 0 0 36px; height: 36px; }
.channels__skill-create { align-items: center; border-top: 1px solid #e4e7ec; display: flex; gap: 8px; padding: 12px 8px; }
.channels__skill-create:focus-visible { outline: 2px solid #2563eb; outline-offset: -2px; }
.channels__table th { background: var(--wa-subtle); border-bottom: 1px solid var(--channels-border); color: var(--wa-muted); font-size: 10px; font-weight: 600; line-height: 16px; padding: 12px 16px; text-align: left; text-transform: uppercase; }
.channels__table td { border-bottom: 1px solid var(--channels-border); height: 64px; overflow-wrap: anywhere; padding: 12px; vertical-align: middle; }
.channels__table tr:last-child td { border-bottom: 0; }
.channels__table tbody tr:hover { background: var(--wa-subtle); }
.channels__identity { align-items: center; display: flex; gap: 12px; min-width: 0; }
.channels__identity .channels__icon { font-size: 18px; height: 36px; width: 36px; }
.channels__identity-copy { display: flex; flex: 1; flex-direction: column; gap: 4px; min-width: 0; }
.channels__identity-copy strong { font-size: 13px; font-weight: 600; }
.channels__identifier { color: var(--wa-muted); font-family: 'SFMono-Regular', Consolas, 'Liberation Mono', monospace; font-size: 11px; overflow-wrap: anywhere; }
.channels__identity .channels__identifier { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.channels__identifier-trigger { align-self: flex-start; background: transparent; border: 0; border-radius: 3px; cursor: help; max-width: 100%; padding: 0; text-align: left; }
.channels__identifier-trigger:hover { color: var(--wa-ink); }
.channels__identifier-trigger:focus-visible { outline: 2px solid var(--wa-blue); outline-offset: 3px; }
.channels__skill { display: flex; flex-direction: column; gap: 6px; overflow-wrap: anywhere; }
.channels__skill a { color: var(--wa-blue); font-weight: 500; }
.channels__skill .anticon { font-size: 10px; }
.channels__muted { color: var(--wa-muted); font-size: 11px; }
.channels__badge { align-items: center; background: var(--wa-subtle); border: 1px solid var(--channels-border); border-radius: 5px; color: var(--wa-muted); display: inline-flex; font-size: 10px; gap: 5px; line-height: 16px; max-width: 100%; padding: 1px 6px; }
.channels__badge--success { background: var(--wa-green-bg); border-color: transparent; color: var(--wa-green); }
.channels__badge--warning { background: var(--wa-amber-bg); border-color: transparent; color: var(--wa-amber); }
.channels__badge--danger { background: var(--wa-red-bg); border-color: transparent; color: var(--wa-red); }
.channels__dot { background: currentColor; border-radius: 50%; flex: 0 0 auto; height: 4px; width: 4px; }
.channels__row-action { text-align: right; }
.channels__manage { align-items: center; background: var(--wa-surface); border: 1px solid var(--wa-line); border-radius: var(--wa-radius); box-shadow: var(--channels-action-shadow); color: var(--wa-ink); display: inline-flex; font-size: 12px; font-weight: 500; gap: 8px; justify-content: center; min-height: var(--channels-action-height); padding: 0 12px; white-space: nowrap; }
.channels__manage .anticon { font-size: 10px; }
.channels__manage:hover { background: var(--wa-blue-bg); border-color: var(--wa-blue); color: var(--wa-blue); }
.channels__manage:active { box-shadow: none; }
.channels__state { background: var(--wa-surface); border: 1px solid var(--channels-border); border-radius: var(--wa-radius); color: var(--wa-muted); padding: 36px var(--channels-space); text-align: center; }
.channels__state h3 { color: var(--wa-ink); margin: 16px 0 8px; }
.channels__state p { line-height: 20px; margin: 0 0 16px; }
.channels__main--detail .wa-vnext__header { display: none; }
.channels__main--detail .channels__content { padding-top: 40px; }
.channels__breadcrumb { align-items: center; color: var(--wa-muted); display: flex; flex-wrap: wrap; font-size: 12px; gap: 12px; margin-bottom: 24px; }
.channels__breadcrumb a { align-items: center; color: var(--wa-blue); display: inline-flex; gap: 8px; min-height: 28px; }
.channels__detail-toolbar { align-items: center; display: flex; flex-wrap: wrap; gap: 16px; justify-content: space-between; margin-bottom: var(--channels-space); }
.channels__detail-toolbar .channels__breadcrumb { margin-bottom: 0; }
.channels__detail { background: var(--wa-surface); border: 1px solid var(--channels-border); border-radius: var(--wa-radius); padding: var(--channels-space); }
.channels__detail-heading { align-items: center; display: flex; gap: 14px; margin-bottom: 24px; }
.channels__detail-heading h1 { font-size: 16px; font-weight: 600; line-height: 24px; margin: 0; overflow-wrap: anywhere; }
.channels__detail-heading p { margin: 0; }
.channels__facts { margin: 0; }
.channels__facts > div { align-items: baseline; display: grid; gap: 16px; grid-template-columns: 160px minmax(0, 1fr); padding: 10px 0; }
.channels__facts dt { color: var(--wa-muted); font-size: 12px; }
.channels__facts dd { font-size: 12px; margin: 0; overflow-wrap: anywhere; text-align: right; }
.channels__authorized-services ul { display: flex; flex-wrap: wrap; gap: 8px; justify-content: flex-end; list-style: none; margin: 0; padding: 0; }
.channels__authorized-services li { background: var(--wa-subtle); border: 1px solid var(--channels-border); border-radius: var(--wa-radius); max-width: 100%; min-width: 0; padding: 4px 8px; text-align: left; }
.channels__authorized-services strong { font-weight: 500; }
.channels__authorized-services > .ant-btn { margin-top: 12px; }
.channels__identifier-link { align-items: baseline; color: var(--wa-blue); display: inline-flex; gap: 6px; max-width: 100%; }
.channels__identifier-link span:first-child { min-width: 0; text-decoration: underline; text-underline-offset: 3px; }
.channels__identifier-link .anticon { flex: 0 0 auto; font-size: 11px; }
.channels__identifier-link:hover { color: var(--wa-blue); text-decoration-thickness: 2px; }
.channels__facts .channels__badge { background: none; border: none; color: var(--wa-ink); font-size: 12px; padding: 0; }
.channels__facts .channels__dot { display: none; }
.channels__detail-actions { align-items: center; display: flex; flex: 0 0 auto; gap: 12px; margin-left: auto; }
.channels__detail-actions .ant-btn { background: transparent; min-height: var(--channels-action-height); }
.channels__removal { align-items: center; display: flex; flex-wrap: wrap; gap: 16px; margin-top: 20px; }
.channels__removal p { color: var(--wa-muted); line-height: 20px; margin: 0; }
.channels__content a:focus-visible { border-radius: 3px; outline: 2px solid var(--wa-blue); outline-offset: 4px; }
@media (max-width: 1199px) {
  .channels__table th, .channels__table td { padding-left: 12px; padding-right: 12px; }
}
@media (max-width: 767px) {
  .channels__guide .channels__section-heading { align-items: flex-start; flex-wrap: wrap; }
  .channels__guide-steps { gap: 32px; grid-template-columns: minmax(0, 1fr); }
  .channels__guide-steps li { padding: 0; }
  .channels__guide-arrow { bottom: -24px; left: 23px; right: auto; transform: rotate(90deg); }
  .channels__bot-summary { flex-wrap: wrap; }
  .channels__main { --channels-space: 20px; --channels-action-height: 44px; }
  .channels__main .wa-vnext__header { padding-top: 24px; }
  .channels__main .wa-vnext__header h1 { font-size: 22px; line-height: 30px; }
  .channels__platform-top { flex-wrap: wrap; }
  .channels__main--detail .channels__content { padding-top: 20px; }
  .channels__detail-heading { align-items: flex-start; flex-wrap: wrap; }
  .channels__detail-heading .channels__badge { margin-left: 56px; }
  .channels__facts > div { grid-template-columns: 108px minmax(0, 1fr); }
  .channels__detail-heading h1 { font-size: 14px; }
}
`;
