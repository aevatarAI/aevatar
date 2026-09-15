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
.channels__intro { color: var(--wa-muted); line-height: 20px; margin: 4px 0 28px; }
.channels__section-label { color: var(--wa-muted); font-size: 12px; font-weight: 500; margin: 0 0 12px; }
.channels__platforms { display: grid; gap: 16px; grid-template-columns: repeat(4, minmax(0, 1fr)); }
.channels__platform { background: var(--wa-surface); border: 1px solid var(--channels-border); border-radius: var(--wa-radius); display: flex; flex-direction: column; min-height: 224px; min-width: 0; padding: 20px; }
.channels__platform-top { align-items: flex-start; display: flex; gap: 8px; justify-content: space-between; }
.channels__icon { align-items: center; background: var(--wa-blue); border-radius: var(--wa-radius); color: white; display: inline-flex; flex: 0 0 auto; font-size: 22px; height: 42px; justify-content: center; width: 42px; }
.channels__platform h3 { font-size: 15px; font-weight: 600; line-height: 22px; margin: 16px 0 8px; }
.channels__platform p { color: var(--wa-muted); flex: 1; font-size: 12px; line-height: 19px; margin: 0 0 24px; }
.channels__platform--soon { background: transparent; }
.channels__platform--soon .channels__icon { background: var(--wa-faint); }
.channels__platform--soon h3 { color: var(--wa-muted); }
.channels__connect { align-items: center; color: var(--wa-blue); display: inline-flex; font-weight: 500; gap: 8px; min-height: 28px; width: fit-content; }
.channels__connections { margin-top: 32px; }
.channels__section-heading { align-items: center; display: flex; gap: 16px; justify-content: space-between; margin-bottom: 16px; }
.channels__section-heading h2 { align-items: center; display: flex; font-size: 16px; gap: 10px; line-height: 24px; margin: 0; }
.channels__section-heading p { color: var(--wa-muted); line-height: 20px; margin: 4px 0 0; }
.channels__count { background: var(--wa-surface); border: 1px solid var(--channels-border); border-radius: 5px; color: var(--wa-muted); font-size: 11px; font-weight: 500; line-height: 20px; min-width: 24px; padding: 0 6px; text-align: center; }
.channels__table-wrap { background: var(--wa-surface); border: 1px solid var(--channels-border); border-radius: var(--wa-radius); overflow: hidden; position: relative; }
.channels__table { border-collapse: collapse; table-layout: fixed; width: 100%; }
.channels__table th { background: var(--wa-subtle); border-bottom: 1px solid var(--channels-border); color: var(--wa-muted); font-size: 10px; font-weight: 600; line-height: 16px; padding: 12px 16px; text-align: left; text-transform: uppercase; }
.channels__table th:nth-child(1) { width: auto; }
.channels__table th:nth-child(2) { width: 12%; }
.channels__table th:nth-child(3) { width: 22%; }
.channels__table th:nth-child(4) { width: 13%; }
.channels__table th:nth-child(5) { width: 15%; }
.channels__table th:last-child { text-align: right; width: 128px; }
.channels__table td { border-bottom: 1px solid var(--channels-border); height: 92px; overflow-wrap: anywhere; padding: 16px; vertical-align: middle; }
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
.channels__detail { background: var(--wa-surface); border: 1px solid var(--channels-border); border-radius: var(--wa-radius); padding: var(--channels-space); }
.channels__detail-heading { align-items: center; display: flex; gap: 14px; margin-bottom: 24px; }
.channels__detail-heading h1 { font-size: 16px; font-weight: 600; line-height: 24px; margin: 0; overflow-wrap: anywhere; }
.channels__detail-heading p { margin: 0; }
.channels__facts { margin: 0; }
.channels__facts > div { align-items: baseline; display: grid; gap: 16px; grid-template-columns: 160px minmax(0, 1fr); padding: 10px 0; }
.channels__facts dt { color: var(--wa-muted); font-size: 12px; }
.channels__facts dd { font-size: 12px; margin: 0; overflow-wrap: anywhere; text-align: right; }
.channels__authorized-services ul { display: grid; gap: 12px; list-style: none; margin: 0; padding: 0; }
.channels__authorized-services li { display: flex; flex-direction: column; gap: 2px; }
.channels__authorized-services strong { font-weight: 500; }
.channels__authorized-services > .ant-btn { margin-top: 12px; }
.channels__identifier-link { align-items: baseline; color: var(--wa-blue); display: inline-flex; gap: 6px; max-width: 100%; }
.channels__identifier-link span:first-child { min-width: 0; text-decoration: underline; text-underline-offset: 3px; }
.channels__identifier-link .anticon { flex: 0 0 auto; font-size: 11px; }
.channels__identifier-link:hover { color: var(--wa-blue); text-decoration-thickness: 2px; }
.channels__facts .channels__badge { background: none; border: none; color: var(--wa-ink); font-size: 12px; padding: 0; }
.channels__facts .channels__dot { display: none; }
.channels__detail-actions { margin-top: 20px; }
.channels__detail-actions .ant-btn { background: transparent; }
.channels__removal { align-items: center; display: flex; flex-wrap: wrap; gap: 16px; margin-top: 20px; }
.channels__removal p { color: var(--wa-muted); line-height: 20px; margin: 0; }
.channels__content a:focus-visible { border-radius: 3px; outline: 2px solid var(--wa-blue); outline-offset: 4px; }
@media (max-width: 1199px) {
  .channels__platforms { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .channels__table th, .channels__table td { padding-left: 12px; padding-right: 12px; }
  .channels__table th:nth-child(1) { width: auto; }
  .channels__table th:nth-child(2) { width: 12%; }
  .channels__table th:nth-child(3) { width: 20%; }
  .channels__table th:nth-child(4) { width: 14%; }
  .channels__table th:nth-child(5) { width: 16%; }
  .channels__table th:last-child { width: 120px; }
}
@media (max-width: 767px) {
  .channels__main { --channels-space: 20px; --channels-action-height: 44px; }
  .channels__main .wa-vnext__header { padding-top: 24px; }
  .channels__main .wa-vnext__header h1 { font-size: 22px; line-height: 30px; }
  .channels__platforms { gap: 12px; }
  .channels__platform { min-height: 210px; padding: 16px; }
  .channels__platform-top { flex-wrap: wrap; }
  .channels__table, .channels__table tbody, .channels__table tr, .channels__table td { display: block; width: 100%; }
  .channels__table thead { clip-path: inset(50%); height: 1px; overflow: hidden; position: absolute; width: 1px; }
  .channels__table tr { border-bottom: 1px solid var(--channels-border); padding: 16px; }
  .channels__table tr:last-child { border-bottom: none; }
  .channels__table td { border: 0; height: auto; padding: 6px 0; }
  .channels__table td[data-label]:not(:first-child) { align-items: baseline; display: grid; gap: 12px; grid-template-columns: 110px minmax(0, 1fr); }
  .channels__table td[data-label]:not(:first-child)::before { color: var(--wa-muted); content: attr(data-label); font-size: 11px; }
  .channels__table .channels__row-action { padding-top: 8px; }
  .channels__main--detail .channels__content { padding-top: 20px; }
  .channels__detail-heading { align-items: flex-start; flex-wrap: wrap; }
  .channels__detail-heading .channels__badge { margin-left: 56px; }
  .channels__facts > div { grid-template-columns: 108px minmax(0, 1fr); }
  .channels__detail-heading h1 { font-size: 14px; }
}
@media (max-width: 359px) {
  .channels__platforms { grid-template-columns: minmax(0, 1fr); }
}
`;
