import '@fontsource-variable/dm-sans/wght.css';

export const channelsCss = `
.wa-vnext:has(.channels__main), .channels__skill-popup, .channels__identifiers-popup {
  --wa-ink: #202834;
  --wa-muted: #7182a2;
  --wa-line: #d8dfed;
  --wa-subtle: #f7f8fa;
  --wa-blue: #3255d3;
  --wa-blue-bg: #eef2ff;
  --wa-radius: 6px;
  --channels-font: 'DM Sans Variable', AlibabaSans, sans-serif;
  font-family: var(--channels-font);
}
.channels__main .ant-btn, .channels__main .ant-input, .channels__main .ant-select { font-family: inherit; }
.channels__main {
  --channels-space: 24px;
  --channels-border: var(--wa-line);
  --channels-action-height: 40px;
  background: var(--wa-surface);
}
.channels__main .wa-vnext__header, .channels__content { margin-inline: auto; max-width: 1160px; width: 100%; }
.channels__main .wa-vnext__header { padding-top: 40px; }
.channels__main .wa-vnext__header h1 { font-size: 28px; line-height: 36px; }
.channels__main .wa-vnext__header p { font-size: 14px; line-height: 20px; margin-top: 8px; }
.channels__content { padding-top: 28px; }
.channels__icon { align-items: center; background: var(--wa-blue); border-radius: var(--wa-radius); color: white; display: inline-flex; flex: 0 0 auto; font-size: 22px; height: 42px; justify-content: center; width: 42px; }
.channels__connect { align-items: center; color: var(--wa-blue); display: inline-flex; font-weight: 500; gap: 8px; min-height: 28px; width: fit-content; }
.channels__connections { margin-top: 26px; }
.channels__section-heading { align-items: center; display: flex; gap: 16px; justify-content: space-between; margin-bottom: 16px; }
.channels__section-heading h2 { align-items: center; display: flex; font-size: 16px; font-weight: 700; gap: 10px; line-height: 24px; margin: 0; }
.channels__connections > .channels__section-heading { min-height: 40px; margin-bottom: 24px; }
.channels__connections > .channels__section-heading h2 { font-size: 20px; line-height: 28px; }
.channels__main .channels__section-heading .ant-btn { border-color: var(--channels-border); border-radius: 6px; box-shadow: none; color: var(--wa-ink); font-size: 13px; font-weight: 600; gap: 24px; height: 40px; padding-inline: 16px; }
.channels__main .channels__section-heading .channels__add-bot { background: var(--wa-blue); border-color: var(--wa-blue); color: white; min-width: 198px; justify-content: space-between; }
.channels__main .channels__section-heading .channels__add-bot:hover { background: var(--wa-blue); border-color: var(--wa-blue); color: white; filter: brightness(.94); }
.channels__refresh { min-width: 124px; }
.channels__section-heading p { color: var(--wa-muted); line-height: 20px; margin: 4px 0 0; }
.channels__count { background: var(--wa-subtle); border: 1px solid var(--channels-border); border-radius: 4px; color: var(--wa-muted); font-size: 11px; font-weight: 400; line-height: 22px; min-width: 30px; padding: 0 6px; text-align: center; }
.channels__table-wrap { position: relative; }
.channels__table-scroll { overflow-x: auto; }
.channels__table { border-collapse: separate; border-spacing: 0; table-layout: fixed; min-width: 1080px; width: 100%; }
.channels__table th:nth-child(1) { width: 20%; }
.channels__table th:nth-child(2) { width: 13%; }
.channels__table th:nth-child(3) { width: 9%; }
.channels__table th:nth-child(4) { width: 17%; }
.channels__table th:nth-child(5) { width: 8%; }
.channels__table th:nth-child(6) { width: 12%; }
.channels__table th:nth-child(7) { width: 11%; }
.channels__table th:last-child { text-align: center; width: 10%; }
.channels__guide { border-bottom: 1px solid var(--channels-border); padding-bottom: 24px; }
.channels__guide .channels__section-heading { min-height: 40px; margin-bottom: 0; }
.channels__guide-steps { display: grid; gap: 36px; grid-template-columns: repeat(3, minmax(0, 1fr)); list-style: none; margin: 24px 0 0; padding: 0; }
.channels__guide-steps li { align-items: center; display: flex; gap: 20px; min-height: 92px; min-width: 0; padding-right: 24px; position: relative; }
.channels__guide-steps li:last-child { padding-right: 0; }
.channels__guide-steps li > div { min-width: 0; }
.channels__guide-icon { align-items: center; background: var(--wa-blue-bg); border-radius: 8px; color: var(--wa-blue); display: flex; flex: 0 0 60px; font-size: 28px; height: 60px; justify-content: center; }
.channels__guide-number { color: var(--wa-muted); display: block; font-size: 11px; font-weight: 600; line-height: 16px; }
.channels__guide-steps h3 { font-size: 16px; font-weight: 700; line-height: 22px; margin: 4px 0 8px; overflow-wrap: anywhere; }
.channels__guide-steps p { color: var(--wa-muted); font-size: 14px; line-height: 20px; margin: 0; max-width: 210px; }
.channels__guide-arrow { color: var(--wa-muted); font-size: 20px; position: absolute; right: -17px; }
.channels__bind { align-items: center; background: var(--wa-blue); border: 1px solid var(--wa-blue); border-radius: var(--wa-radius); color: white; display: inline-flex; font-size: 13px; font-weight: 600; justify-content: center; min-height: 40px; min-width: 86px; padding: 0 16px; }
.channels__bind:hover { color: white; filter: brightness(.92); }
.channels__bound { align-items: center; color: var(--wa-green); display: inline-flex; font-size: 12px; font-weight: 600; gap: 10px; white-space: nowrap; }
.channels__bot-summary { align-items: center; border-bottom: 1px solid var(--channels-border); display: flex; gap: 16px; justify-content: space-between; margin-bottom: 24px; padding-bottom: 24px; }
.channels__bot-summary .channels__identity { min-width: 0; }
.channels__skill-controls { align-items: center; display: flex; gap: 8px; }
.channels__skill-controls .ant-select { flex: 1; min-width: 0; height: 40px; }
.channels__skill-popup { padding: 8px; }
.channels__skill-search { padding: 4px 4px 0; }
.channels__skill-search .ant-input-affix-wrapper { background: var(--wa-subtle); border-color: var(--wa-line); border-radius: 4px; font-family: inherit; min-height: 36px; }
.channels__skill-search .ant-input, .channels__skill-search .ant-input-prefix { font-family: inherit; font-size: 13px; }
.channels__skill-list-heading { align-items: center; display: flex; gap: 12px; justify-content: space-between; margin: 8px 0 4px 4px; }
.channels__skill-list-heading p { color: var(--wa-muted); font-size: 10px; font-weight: 600; margin: 0; text-transform: uppercase; }
.channels__skill-list-heading .ant-btn { color: var(--wa-muted); flex: 0 0 32px; height: 32px; padding: 0; width: 32px; }
.channels__skill-popup .ant-select-item { padding: 10px 8px; }
.channels__skill-option { display: flex; flex-direction: column; gap: 4px; white-space: normal; overflow-wrap: anywhere; }
.channels__skill-option strong { color: var(--wa-ink); font-size: 13px; font-weight: 500; line-height: 20px; }
.channels__skill-option span { color: var(--wa-muted); display: -webkit-box; font-size: 12px; line-height: 18px; overflow: hidden; -webkit-box-orient: vertical; -webkit-line-clamp: 2; }
.channels__skill-create { align-items: center; border-top: 1px solid var(--wa-line); color: var(--wa-blue); display: flex; font-size: 13px; gap: 8px; padding: 14px 8px 8px; }
.channels__skill-create .anticon:last-child { margin-left: auto; }
.channels__skill-create:focus-visible { outline: 2px solid var(--wa-blue); outline-offset: -2px; }
.channels__table th { background: var(--wa-subtle); border-block: 1px solid var(--channels-border); color: var(--wa-muted); font-size: 10px; font-weight: 600; height: 46px; line-height: 16px; padding: 12px; text-align: left; text-transform: uppercase; }
.channels__table th:first-child { border-left: 1px solid var(--channels-border); border-radius: 6px 0 0 6px; }
.channels__table th:last-child { border-right: 1px solid var(--channels-border); border-radius: 0 6px 6px 0; }
.channels__table td { border-bottom: 1px solid var(--channels-border); font-size: 13px; height: 88px; overflow-wrap: anywhere; padding: 20px 12px; vertical-align: middle; }
.channels__table .channels__badge { border: 0; border-radius: 4px; font-size: 11px; line-height: 20px; padding: 3px 10px; }
.channels__table .channels__badge .channels__dot { display: none; }
.channels__table tbody tr:hover { background: var(--wa-subtle); }
.channels__identity { align-items: center; display: flex; gap: 12px; min-width: 0; }
.channels__identity .channels__icon { font-size: 18px; height: 36px; width: 36px; }
.channels__identity-copy { display: flex; flex: 1; flex-direction: column; gap: 4px; min-width: 0; }
.channels__identity-copy strong { font-size: 13px; font-weight: 600; }
.channels__identifier { color: var(--wa-muted); font-family: 'SFMono-Regular', Consolas, 'Liberation Mono', monospace; font-size: 11px; overflow-wrap: anywhere; }
.channels__identity-heading { align-items: center; display: flex; gap: 4px; min-width: 0; }
.channels__identity-heading strong { min-width: 0; overflow-wrap: anywhere; }
.channels__identifier-action { align-items: center; background: transparent; border: 0; border-radius: var(--wa-radius); color: var(--wa-muted); cursor: pointer; display: inline-flex; flex: 0 0 32px; font-size: 14px; height: 32px; justify-content: center; padding: 0; touch-action: manipulation; width: 32px; }
.channels__identifier-action:hover, .channels__identifier-action[aria-expanded="true"] { background: var(--wa-blue-bg); color: var(--wa-blue); }
.channels__identifier-action:active { background: var(--wa-line); }
.channels__identifier-action:disabled { cursor: wait; opacity: .5; }
.channels__identifier-action:focus-visible { outline: 2px solid var(--wa-blue); outline-offset: 2px; }
.channels__identifiers-popup { max-width: calc(100vw - 32px); width: 340px; }
.channels__identifiers-heading { align-items: center; color: var(--wa-ink); display: flex; gap: 12px; justify-content: space-between; }
.channels__identifiers-fields { margin: 8px 0 0; }
.channels__identifiers-fields > div + div { border-top: 1px solid var(--wa-line); margin-top: 12px; padding-top: 12px; }
.channels__identifiers-fields dt { color: var(--wa-muted); font-size: 12px; }
.channels__identifiers-fields dd { align-items: center; display: flex; gap: 8px; margin: 4px 0 0; }
.channels__identifiers-fields code { color: var(--wa-ink); flex: 1; font-size: 12px; min-width: 0; overflow-wrap: anywhere; user-select: text; }
.channels__owner { overflow-wrap: anywhere; }
.channels__skill { display: flex; flex-direction: column; gap: 6px; overflow-wrap: anywhere; }
.channels__skill a { color: var(--wa-blue); font-weight: 500; }
.channels__skill .anticon { font-size: 10px; }
.channels__muted { color: var(--wa-muted); font-size: 11px; }
.channels__badge { align-items: center; background: var(--wa-subtle); border: 1px solid var(--channels-border); border-radius: 5px; color: var(--wa-muted); display: inline-flex; font-size: 10px; gap: 5px; line-height: 16px; max-width: 100%; padding: 1px 6px; }
.channels__badge--success { background: var(--wa-green-bg); border-color: transparent; color: var(--wa-green); }
.channels__badge--warning { background: var(--wa-amber-bg); border-color: transparent; color: var(--wa-amber); }
.channels__badge--danger { background: var(--wa-red-bg); border-color: transparent; color: var(--wa-red); }
.channels__dot { background: currentColor; border-radius: 50%; flex: 0 0 auto; height: 4px; width: 4px; }
.channels__row-action { text-align: center; }
.channels__manage { align-items: center; background: var(--wa-surface); border: 1px solid var(--wa-line); border-radius: var(--wa-radius); color: var(--wa-ink); display: inline-flex; font-size: 13px; font-weight: 600; gap: 8px; justify-content: center; min-height: var(--channels-action-height); padding: 0 16px; white-space: nowrap; }
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
@media (max-width: 767px) {
  .channels__identifier-action { flex-basis: 44px; height: 44px; width: 44px; }
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
