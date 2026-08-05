export const workflowActivityVNextCss = `
.wa-vnext {
  --wa-ink: #17202a;
  --wa-muted: #667085;
  --wa-faint: #98a2b3;
  --wa-line: #d0d5dd;
  --wa-surface: #ffffff;
  --wa-subtle: #f8fafc;
  --wa-sidebar: #101828;
  --wa-blue: #175cd3;
  --wa-blue-bg: #eff8ff;
  --wa-green: #067647;
  --wa-green-bg: #ecfdf3;
  --wa-amber: #b54708;
  --wa-amber-bg: #fffaeb;
  --wa-red: #b42318;
  --wa-red-bg: #fef3f2;
  color: var(--wa-ink);
  display: grid;
  font-family: "Avenir Next", "Segoe UI", sans-serif;
  grid-template-columns: 216px minmax(0, 1fr);
  height: 100%;
  min-height: 0;
  overflow: hidden;
  width: 100%;
}
.wa-vnext * { box-sizing: border-box; letter-spacing: 0; }
.wa-vnext button, .wa-vnext a, .wa-vnext input, .wa-vnext select { touch-action: manipulation; }
.wa-vnext__rail {
  background: var(--wa-sidebar);
  border-right: 1px solid #344054;
  color: #fff;
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  padding: max(22px, env(safe-area-inset-top)) 12px max(18px, env(safe-area-inset-bottom));
}
.wa-vnext__brand { padding: 0 12px 28px; }
.wa-vnext__brand strong { display: block; font-size: 18px; }
.wa-vnext__brand span { color: #98a2b3; display: block; font: 11px ui-monospace, monospace; margin-top: 4px; }
.wa-vnext__nav { display: grid; gap: 6px; }
.wa-vnext__nav-button {
  align-items: center; background: transparent; border: 1px solid transparent;
  border-radius: 6px; color: #d0d5dd; display: flex; gap: 11px; min-height: 42px;
  padding: 0 14px; text-decoration: none;
}
.wa-vnext__nav-button:hover { background: #1d2939; color: #fff; }
.wa-vnext__nav-button[aria-current="page"] { background: #344054; border-color: #475467; color: #fff; }
.wa-vnext__main { background: var(--wa-surface); min-height: 0; min-width: 0; overflow-x: hidden; overflow-y: auto; }
.wa-vnext__mobile-nav { display: none; }
.wa-vnext__header { align-items: center; border-bottom: 1px solid var(--wa-line); display: flex; gap: 18px; justify-content: space-between; min-height: 84px; padding: 18px 30px; }
.wa-vnext__header h1 { font-size: 22px; line-height: 1.25; margin: 0; text-wrap: balance; }
.wa-vnext__header p { color: var(--wa-muted); font-size: 13px; margin: 5px 0 0; }
.wa-vnext__header-actions { align-items: center; display: flex; flex-wrap: wrap; gap: 8px; justify-content: flex-end; }
.wa-vnext__content { min-width: 0; padding: 24px 30px 56px; }
.wa-vnext__toolbar { align-items: end; display: flex; flex-wrap: wrap; gap: 12px; justify-content: space-between; margin-bottom: 18px; }
.wa-vnext__table-wrap {
  border: 1px solid var(--wa-line);
  max-height: min(640px, calc(100dvh - 240px));
  max-width: 100%;
  overscroll-behavior: contain;
  overflow: auto;
  scrollbar-gutter: stable;
}
.wa-vnext__table { border-collapse: collapse; min-width: 720px; table-layout: fixed; width: 100%; }
.wa-vnext__table th { background: var(--wa-subtle); border-bottom: 1px solid var(--wa-line); color: var(--wa-muted); font: 11px ui-monospace, monospace; height: 42px; padding: 0 12px; position: sticky; text-align: left; text-transform: uppercase; top: 0; z-index: 2; }
.wa-vnext__table td { border-bottom: 1px solid var(--wa-line); height: 72px; overflow-wrap: anywhere; padding: 10px 12px; vertical-align: middle; }
.wa-vnext__table tr:last-child td { border-bottom: 0; }
.wa-vnext__table pre { margin: 0; max-width: 100%; white-space: pre-wrap; word-break: break-word; }
.wa-vnext__run-link { max-width: 100%; min-width: 0; overflow: hidden; }
.wa-vnext__title { display: block; font-weight: 650; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.wa-vnext__sub { color: var(--wa-muted); display: block; font-size: 12px; margin-top: 5px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.wa-vnext__status { align-items: center; border: 1px solid currentColor; border-radius: 5px; display: inline-flex; font-size: 12px; font-weight: 650; gap: 6px; min-height: 26px; padding: 0 9px; white-space: nowrap; }
.wa-vnext__status::before { background: currentColor; border-radius: 50%; content: ""; height: 7px; width: 7px; }
.wa-vnext__status--draft { background: #f4f3ff; color: #6941c6; }
.wa-vnext__status--committed, .wa-vnext__status--succeeded { background: var(--wa-green-bg); color: var(--wa-green); }
.wa-vnext__status--running, .wa-vnext__status--accepted { background: var(--wa-blue-bg); color: var(--wa-blue); }
.wa-vnext__status--failed { background: var(--wa-red-bg); color: var(--wa-red); }
.wa-vnext__status--pending, .wa-vnext__status--unknown { background: var(--wa-amber-bg); color: var(--wa-amber); }
.wa-vnext__state { background: var(--wa-subtle); border: 1px dashed var(--wa-line); display: grid; min-height: 260px; padding: 32px; place-items: center; text-align: center; }
.wa-vnext__state h2 { font-size: 18px; margin: 0 0 8px; }
.wa-vnext__state p { color: var(--wa-muted); margin: 0 0 18px; }
.wa-vnext__state--compact { border-style: solid; justify-items: start; min-height: 0; padding: 24px; place-items: initial; text-align: left; }
.wa-vnext__state--compact h3 { font-size: 16px; margin: 0 0 8px; text-wrap: balance; }
.wa-vnext__state--compact p { max-width: 560px; text-wrap: pretty; }
.wa-vnext__notice { border: 1px solid #fdb022; background: var(--wa-amber-bg); color: #7a2e0e; margin-bottom: 14px; padding: 11px 13px; }
.wa-vnext__notice--error { background: var(--wa-red-bg); border-color: #fda29b; color: var(--wa-red); }
.wa-vnext__panel { border: 1px solid var(--wa-line); padding: 20px; }
.wa-vnext__form { display: grid; gap: 18px; }
.wa-vnext__form-actions { display: flex; flex-wrap: wrap; gap: 8px; }
.wa-vnext__mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; overflow-wrap: anywhere; }
.wa-vnext__split { display: grid; gap: 20px; grid-template-columns: minmax(0, 1fr) minmax(280px, 34%); }
.wa-vnext__settings-layout { display: grid; gap: 42px; grid-template-columns: 190px minmax(0, 1fr); margin: 0 auto; max-width: 1160px; }
.wa-vnext__settings-nav { align-content: start; display: grid; gap: 4px; }
.wa-vnext__settings-nav-link { border-radius: 6px; color: var(--wa-muted); font-size: 13px; font-weight: 650; min-height: 44px; padding: 13px 12px; text-decoration: none; }
.wa-vnext__settings-nav-link:hover { background: var(--wa-subtle); color: var(--wa-ink); }
.wa-vnext__settings-nav-link[aria-current="page"] { background: var(--wa-blue-bg); color: var(--wa-blue); }
.wa-vnext__settings-panel { min-width: 0; }
.wa-vnext__settings-heading { border-bottom: 1px solid var(--wa-line); margin-bottom: 0; padding: 0 0 18px; }
.wa-vnext__settings-heading h2 { font-size: 18px; line-height: 1.35; margin: 0; text-wrap: balance; }
.wa-vnext__settings-heading p { color: var(--wa-muted); font-size: 13px; margin: 5px 0 0; max-width: 760px; text-wrap: pretty; }
.wa-vnext__settings-fields { display: grid; }
.wa-vnext__settings-field { align-items: center; border-bottom: 1px solid #e4e7ec; display: grid; gap: 32px; grid-template-columns: 220px minmax(0, 1fr); min-height: 86px; padding: 18px 0; }
.wa-vnext__settings-field-copy { min-width: 0; }
.wa-vnext__settings-field-copy strong { display: block; font-size: 13px; }
.wa-vnext__settings-field-copy span { color: var(--wa-muted); display: block; font-size: 12px; line-height: 1.45; margin-top: 4px; text-wrap: pretty; }
.wa-vnext__settings-field .ant-select { max-width: 520px; width: 100%; }
.wa-vnext__settings-savebar { align-items: center; background: #1d2939; border: 1px solid #344054; bottom: 18px; box-shadow: 0 12px 30px rgba(16, 24, 40, .2); color: #fff; display: flex; gap: 24px; justify-content: space-between; margin-top: 34px; padding: 12px 14px; position: sticky; z-index: 8; }
.wa-vnext__settings-savebar strong { display: block; font-size: 13px; }
.wa-vnext__settings-savebar span { color: #d0d5dd; display: block; font-size: 11px; margin-top: 2px; }
.wa-vnext__settings-savebar .ant-btn-default { background: transparent; border-color: #667085; color: #fff; }
.wa-vnext__settings-facts { padding-top: 20px; }
.wa-vnext__settings-facts .ant-descriptions-item-label { color: var(--wa-muted); font-size: 12px; }
.wa-vnext__settings-facts .ant-descriptions-item-content { color: var(--wa-ink); min-width: 0; overflow-wrap: anywhere; }
.wa-vnext__technical-details { color: var(--wa-muted); font-size: 12px; margin-top: 18px; max-width: 100%; }
.wa-vnext__technical-details summary { cursor: pointer; font-weight: 650; }
.wa-vnext__technical-details-body { background: #fff; border: 1px solid var(--wa-line); display: block; margin-top: 8px; max-width: 100%; overflow-wrap: anywhere; padding: 10px; }
.wa-vnext button:focus-visible, .wa-vnext a:focus-visible, .wa-vnext input:focus-visible, .wa-vnext textarea:focus-visible, .wa-vnext select:focus-visible, .wa-vnext__table-wrap:focus-visible { outline: 3px solid rgba(23, 92, 211, .25); outline-offset: 2px; }
@media (max-width: 900px) {
  .wa-vnext { grid-template-columns: 1fr; }
  .wa-vnext__rail { display: none; }
  .wa-vnext__mobile-nav { background: var(--wa-sidebar); display: flex; gap: 4px; overflow-x: auto; padding: 8px 10px; }
  .wa-vnext__mobile-nav .wa-vnext__nav { display: flex; gap: 4px; min-width: max-content; width: 100%; }
  .wa-vnext__mobile-nav .wa-vnext__nav-button { flex: 1 0 auto; justify-content: center; min-height: 38px; white-space: nowrap; }
  .wa-vnext__split { grid-template-columns: 1fr; }
  .wa-vnext__settings-layout { gap: 24px; grid-template-columns: 1fr; max-width: none; }
  .wa-vnext__settings-nav { border-bottom: 1px solid var(--wa-line); display: flex; gap: 8px; }
  .wa-vnext__settings-nav-link { flex: 1 1 0; text-align: center; }
}
@media (max-width: 600px) {
  .wa-vnext__header { align-items: flex-start; flex-direction: column; min-height: 0; padding: 16px; }
  .wa-vnext__header-actions { justify-content: flex-start; width: 100%; }
  .wa-vnext__content { padding: 18px 16px 44px; }
  .wa-vnext__toolbar { align-items: stretch; flex-direction: column; }
  .wa-vnext__toolbar .ant-input-affix-wrapper { width: 100% !important; }
  .wa-vnext__settings-layout { gap: 20px; }
  .wa-vnext__settings-nav { gap: 4px; }
  .wa-vnext__settings-nav-link { font-size: 12px; padding-inline: 6px; }
  .wa-vnext__settings-heading { padding-bottom: 17px; }
  .wa-vnext__settings-field { align-items: stretch; gap: 10px; grid-template-columns: 1fr; min-height: 0; padding: 20px 0; }
  .wa-vnext__settings-field .ant-select { max-width: none; }
  .wa-vnext__settings-savebar { align-items: stretch; bottom: max(12px, env(safe-area-inset-bottom)); flex-direction: column; gap: 12px; }
  .wa-vnext__settings-savebar .ant-space { display: grid; grid-template-columns: 1fr 1fr; width: 100%; }
  .wa-vnext__settings-savebar .ant-btn { width: 100%; }
  .wa-vnext__state--compact { padding: 20px; }
  .wa-vnext__table-wrap--cards:not(.wa-vnext__activity-table) { border: 0; max-height: none; overflow: visible; scrollbar-gutter: auto; }
  .wa-vnext__table-wrap--cards:not(.wa-vnext__activity-table) .wa-vnext__table { display: block; min-width: 0; }
  .wa-vnext__table-wrap--cards:not(.wa-vnext__activity-table) thead { display: none; }
  .wa-vnext__table-wrap--cards:not(.wa-vnext__activity-table) tbody { display: grid; gap: 12px; }
  .wa-vnext__table-wrap--cards:not(.wa-vnext__activity-table) tr { border: 1px solid var(--wa-line); display: block; padding: 8px 0; }
  .wa-vnext__table-wrap--cards:not(.wa-vnext__activity-table) td {
    align-items: start;
    border: 0;
    display: grid;
    gap: 12px;
    grid-template-columns: minmax(96px, 34%) minmax(0, 1fr);
    height: auto;
    min-width: 0;
    padding: 8px 14px;
  }
  .wa-vnext__table-wrap--cards:not(.wa-vnext__activity-table) td::before {
    color: var(--wa-muted);
    content: attr(data-label);
    font: 11px ui-monospace, monospace;
    text-transform: uppercase;
  }
  .wa-vnext__table-wrap--cards:not(.wa-vnext__activity-table) td .ant-space { display: flex; flex-wrap: wrap; }
  .wa-vnext__activity-table { border: 0; overflow: visible; }
  .wa-vnext__activity-table .wa-vnext__table { display: block; min-width: 0; }
  .wa-vnext__activity-table thead { display: none; }
  .wa-vnext__activity-table tbody { border: 1px solid var(--wa-line); display: block; }
  .wa-vnext__activity-table tr {
    display: grid;
    gap: 8px 12px;
    grid-template-columns: minmax(0, 1fr) auto;
    padding: 14px;
  }
  .wa-vnext__activity-table tr:not(:last-child) { border-bottom: 1px solid var(--wa-line); }
  .wa-vnext__activity-table td { border: 0; height: auto; min-width: 0; padding: 0; }
  .wa-vnext__activity-table td:nth-child(1) { grid-column: 1 / -1; grid-row: 2; }
  .wa-vnext__activity-table td:nth-child(2) { grid-column: 1; grid-row: 1; }
  .wa-vnext__activity-table td:nth-child(3) { color: var(--wa-muted); font-size: 12px; grid-column: 1; grid-row: 3; }
  .wa-vnext__activity-table td:nth-child(4) { color: var(--wa-muted); font: 12px ui-monospace, monospace; grid-column: 2; grid-row: 1; white-space: nowrap; }
  .wa-vnext__activity-table td:nth-child(5) { color: var(--wa-muted); font: 12px ui-monospace, monospace; grid-column: 2; grid-row: 3; text-align: right; }
}
@media (prefers-reduced-motion: reduce) { .wa-vnext *, .wa-vnext *::before, .wa-vnext *::after { scroll-behavior: auto !important; transition: none !important; } }
`;
