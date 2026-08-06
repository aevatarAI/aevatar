export const workflowActivityVNextCss = `
.wa-vnext {
  --wa-ink: #17202a;
  --wa-muted: #667085;
  --wa-faint: #98a2b3;
  --wa-line: #d0d5dd;
  --wa-surface: #ffffff;
  --wa-subtle: #f8fafc;
  --wa-sidebar: #101828;
  --wa-sidebar-hover: #1d2939;
  --wa-blue: #175cd3;
  --wa-blue-bg: #eff8ff;
  --wa-green: #067647;
  --wa-green-bg: #ecfdf3;
  --wa-amber: #b54708;
  --wa-amber-bg: #fffaeb;
  --wa-red: #b42318;
  --wa-red-bg: #fef3f2;
  --wa-radius: 8px;
  color: var(--wa-ink);
  display: grid;
  font-family: AlibabaSans, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
  font-size: 12px;
  grid-template-columns: 200px minmax(0, 1fr);
  grid-template-rows: 52px minmax(0, 1fr);
  height: 100%;
  min-height: 0;
  overflow: hidden;
  width: 100%;
}
.wa-vnext * { box-sizing: border-box; letter-spacing: 0; }
.wa-vnext button, .wa-vnext a, .wa-vnext input, .wa-vnext select { touch-action: manipulation; }
.wa-vnext .ant-btn, .wa-vnext .ant-input-affix-wrapper, .wa-vnext .ant-select-single { min-height: 32px; }
.wa-vnext .ant-btn-icon-only { min-width: 32px; width: 32px; }
.wa-vnext .ant-btn, .wa-vnext .ant-input, .wa-vnext .ant-select, .wa-vnext .ant-segmented { font-size: 12px; }
.wa-vnext .ant-btn, .wa-vnext .ant-input, .wa-vnext .ant-input-affix-wrapper, .wa-vnext .ant-select-selector, .wa-vnext .ant-segmented, .wa-vnext .ant-alert, .wa-vnext .ant-modal-content { border-radius: var(--wa-radius); }
.wa-vnext__topbar {
  align-items: center;
  background: var(--wa-surface);
  border-bottom: 1px solid var(--wa-line);
  display: flex;
  gap: 16px;
  grid-column: 1 / -1;
  grid-row: 1;
  justify-content: space-between;
  min-width: 0;
  padding: 0 16px;
  z-index: 30;
}
.wa-vnext__topbar-leading, .wa-vnext__topbar-actions { align-items: center; display: flex; min-width: 0; }
.wa-vnext__topbar-leading { gap: 12px; }
.wa-vnext__topbar-actions { flex: 0 0 auto; gap: 6px; }
.wa-vnext__brand { color: var(--wa-ink); font-size: 16px; font-weight: 700; line-height: 20px; text-decoration: none; }
.wa-vnext__brand:hover { color: var(--wa-blue); }
.wa-vnext__topbar-divider { background: var(--wa-line); height: 18px; width: 1px; }
.wa-vnext__topbar-context { color: var(--wa-muted); font-size: 12px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.wa-vnext__menu-button { display: none; }
.wa-vnext__rail {
  background: var(--wa-sidebar);
  border-right: 1px solid #344054;
  color: #fff;
  display: flex;
  flex-direction: column;
  grid-column: 1;
  grid-row: 2;
  min-height: 0;
  padding: 14px 10px max(16px, env(safe-area-inset-bottom));
}
.wa-vnext__nav { display: grid; gap: 4px; }
.wa-vnext__nav-button {
  align-items: center;
  background: transparent;
  border: 1px solid transparent;
  border-radius: 6px;
  color: #d0d5dd;
  display: flex;
  font-size: 12px;
  font-weight: 500;
  gap: 10px;
  min-height: 36px;
  padding: 0 12px;
  text-decoration: none;
}
.wa-vnext__nav-button:hover { background: var(--wa-sidebar-hover); color: #fff; }
.wa-vnext__nav-button[aria-current="page"] { background: #344054; border-color: #475467; color: #fff; }
.wa-vnext__main {
  background: var(--wa-surface);
  grid-column: 2;
  grid-row: 2;
  min-height: 0;
  min-width: 0;
  overflow-x: hidden;
  overflow-y: auto;
  overscroll-behavior: contain;
}
.wa-vnext__main--with-footer { display: grid; grid-template-rows: minmax(0, 1fr) auto; overflow: hidden; }
.wa-vnext__main-scroll { min-height: 0; min-width: 0; overflow-x: hidden; overflow-y: auto; overscroll-behavior: contain; }
.wa-vnext__main-footer { min-width: 0; }
.wa-vnext__header {
  align-items: flex-end;
  display: flex;
  gap: 20px;
  justify-content: space-between;
  padding: 24px 40px 0;
}
.wa-vnext__heading-copy { min-width: 0; }
.wa-vnext__header h1 { font-size: 28px; font-weight: 700; line-height: 28px; margin: 0; text-wrap: balance; }
.wa-vnext__header p { color: var(--wa-muted); font-size: 12px; line-height: 17px; margin: 7px 0 0; max-width: 760px; text-wrap: pretty; }
.wa-vnext__header-actions { align-items: center; display: flex; flex: 0 0 auto; flex-wrap: wrap; gap: 8px; justify-content: flex-end; }
.wa-vnext__content { min-width: 0; padding: 18px 40px 48px; }
.wa-vnext__toolbar { align-items: center; display: flex; flex-wrap: wrap; gap: 10px; justify-content: space-between; margin-bottom: 12px; min-height: 32px; }
.wa-vnext__toolbar-search { flex: 0 1 320px; max-width: 100%; width: 320px; }
.wa-vnext__toolbar-filters { justify-content: flex-end; }
.wa-vnext__toolbar-filters .ant-select { min-width: 150px; }
.wa-vnext__table-wrap {
  border: 1px solid var(--wa-line);
  border-radius: var(--wa-radius);
  max-height: min(560px, calc(100dvh - 226px));
  max-width: 100%;
  min-height: 0;
  overscroll-behavior: contain;
  overflow: auto;
  scrollbar-gutter: stable;
}
.wa-vnext__table { border-collapse: separate; border-spacing: 0; font-size: 12px; line-height: 17px; min-width: 720px; table-layout: fixed; width: 100%; }
.wa-vnext__table th {
  background: var(--wa-subtle);
  border-bottom: 1px solid var(--wa-line);
  color: var(--wa-muted);
  font-size: 10px;
  font-weight: 600;
  height: 32px;
  letter-spacing: 1.5px;
  line-height: 14px;
  padding: 0 12px;
  position: sticky;
  text-align: left;
  text-transform: uppercase;
  top: 0;
  z-index: 2;
}
.wa-vnext__table th:first-child { border-top-left-radius: 7px; }
.wa-vnext__table th:last-child { border-top-right-radius: 7px; }
.wa-vnext__table td { border-bottom: 1px solid var(--wa-line); height: 76px; overflow-wrap: anywhere; padding: 10px 12px; vertical-align: middle; }
.wa-vnext__table tr:last-child td { border-bottom: 0; }
.wa-vnext__table tbody tr:hover { background: #f9fafb; }
.wa-vnext__table pre { margin: 0; max-width: 100%; white-space: pre-wrap; word-break: break-word; }
.wa-vnext__run-link { max-width: 100%; min-width: 0; overflow: hidden; }
.wa-vnext__title { display: block; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.wa-vnext__sub { color: var(--wa-muted); display: block; font-size: 12px; line-height: 17px; margin-top: 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.wa-vnext__workflow-context { align-items: center; color: var(--wa-muted); display: flex; flex-wrap: wrap; font-size: 11px; gap: 4px 7px; line-height: 16px; margin-top: 7px; }
.wa-vnext__workflow-context .wa-vnext__status { min-height: 20px; }
.wa-vnext__status { align-items: center; border: 1px solid currentColor; border-radius: 5px; display: inline-flex; font-size: 11px; font-weight: 600; gap: 6px; min-height: 24px; padding: 0 8px; white-space: nowrap; }
.wa-vnext__status::before { background: currentColor; border-radius: 50%; content: ""; height: 6px; width: 6px; }
.wa-vnext__status--draft { background: #f4f3ff; color: #6941c6; }
.wa-vnext__status--committed, .wa-vnext__status--succeeded { background: var(--wa-green-bg); color: var(--wa-green); }
.wa-vnext__status--running, .wa-vnext__status--accepted { background: var(--wa-blue-bg); color: var(--wa-blue); }
.wa-vnext__status--failed { background: var(--wa-red-bg); color: var(--wa-red); }
.wa-vnext__status--pending, .wa-vnext__status--unknown { background: var(--wa-amber-bg); color: var(--wa-amber); }
.wa-vnext__state { background: var(--wa-subtle); border: 1px dashed var(--wa-line); border-radius: var(--wa-radius); display: grid; min-height: 240px; padding: 28px; place-items: center; text-align: center; }
.wa-vnext__state h2 { font-size: 18px; line-height: 24px; margin: 0 0 7px; }
.wa-vnext__state p { color: var(--wa-muted); font-size: 12px; line-height: 17px; margin: 0 0 16px; }
.wa-vnext__state--compact { border-style: solid; justify-items: start; min-height: 0; padding: 20px; place-items: initial; text-align: left; }
.wa-vnext__state--compact h3 { font-size: 16px; margin: 0 0 7px; text-wrap: balance; }
.wa-vnext__state--compact p { max-width: 560px; text-wrap: pretty; }
.wa-vnext__notice { background: var(--wa-amber-bg); border: 1px solid #fdb022; border-radius: var(--wa-radius); color: #7a2e0e; margin-bottom: 12px; padding: 10px 12px; }
.wa-vnext__notice--error { background: var(--wa-red-bg); border-color: #fda29b; color: var(--wa-red); }
.wa-vnext__panel { border: 1px solid var(--wa-line); border-radius: var(--wa-radius); padding: 20px; }
.wa-vnext__form { display: grid; gap: 16px; }
.wa-vnext__form > div > span:first-child { font-size: 12px; font-weight: 600; }
.wa-vnext__form-actions { display: flex; flex-wrap: wrap; gap: 8px; }
.wa-vnext__field-control { display: block; margin-top: 6px; width: 100%; }
.wa-vnext__duplicate-warning { color: var(--wa-amber); font-size: 12px; line-height: 17px; margin: 6px 0 0; }
.wa-vnext__modal-field { display: grid; font-size: 12px; font-weight: 600; gap: 6px; }
.wa-vnext__mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; overflow-wrap: anywhere; }
.wa-vnext__split { display: grid; gap: 20px; grid-template-columns: minmax(0, 1fr) minmax(280px, 34%); }
.wa-vnext__creation-options { border: 0; display: grid; gap: 12px; grid-template-columns: repeat(4, minmax(0, 1fr)); margin: 0; min-width: 0; padding: 0; }
.wa-vnext__creation-option { background: var(--wa-surface); border: 1px solid var(--wa-line); border-radius: var(--wa-radius); min-height: 142px; padding: 18px; text-align: left; transition: border-color .15s ease, box-shadow .15s ease; }
.wa-vnext__creation-option:hover:not(:disabled) { border-color: var(--wa-blue); box-shadow: 0 3px 12px rgba(16, 24, 40, .08); }
.wa-vnext__creation-option-icon { color: var(--wa-blue); display: block; font-size: 20px; line-height: 24px; }
.wa-vnext__creation-option-title { display: block; font-size: 14px; font-weight: 700; line-height: 20px; margin-top: 13px; }
.wa-vnext__creation-option-description { color: var(--wa-muted); display: block; font-size: 12px; line-height: 17px; margin-top: 5px; }
.wa-vnext__form-title { font-size: 16px; font-weight: 700; line-height: 22px; margin: 0; }
.wa-vnext__editor-toolbar { flex-wrap: nowrap; min-width: 0; overflow: visible; }
.wa-vnext__editor-toolbar > * { flex: 0 1 auto; min-width: 0; }
.wa-vnext__editor-name { flex: 1 1 220px !important; max-width: 420px; min-width: 0; }
.wa-vnext__editor-toolbar-meta { align-items: center; display: flex; flex: 0 1 auto; gap: 8px; min-width: 0; }
.wa-vnext__publish-readiness { max-width: min(360px, calc(100vw - 32px)); }
.wa-vnext__publish-readiness ul { display: grid; gap: 4px; list-style: none; margin: 0; padding: 0; }
.wa-vnext__publish-readiness .ant-btn { height: auto; line-height: 1.35; padding: 4px 0; text-align: left; white-space: normal; }
.wa-vnext__publication-identities { display: grid; gap: 6px; margin: 12px 0 0; }
.wa-vnext__publication-identities > div { display: grid; gap: 2px; grid-template-columns: minmax(116px, max-content) minmax(0, 1fr); }
.wa-vnext__publication-identities dt { color: var(--wa-muted); font-size: 11px; font-weight: 700; }
.wa-vnext__publication-identities dd { font-family: var(--wa-font-mono); margin: 0; overflow-wrap: anywhere; }
.wa-vnext__editor-mode-control { flex: 0 1 auto; min-width: 0; }
.wa-vnext__editor-alerts { display: grid; gap: 6px; margin-bottom: 12px; }
.wa-vnext__publish-review-item {
  border-top: 1px solid var(--wa-line, #d0d5dd);
  display: grid;
  gap: 4px;
  min-width: 0;
  padding: 12px 0;
}
.wa-vnext__publish-review-item .ant-typography {
  display: block;
  line-height: 17px;
  max-width: 100%;
  overflow-wrap: anywhere;
}
.wa-vnext__publish-review-item .ant-typography:first-child {
  color: var(--wa-ink, #17202a);
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
}
.wa-vnext__editor-surface { min-height: 500px; overflow: hidden; position: relative; }
.wa-vnext__editor-add { left: 16px; position: absolute; top: 16px; z-index: 5; }
.wa-vnext__editor-yaml { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; width: 100%; }
.wa-vnext__run-panel { margin-top: 16px; }
.wa-vnext__run-panel-content { width: 100%; }
.wa-vnext__run-input-field { display: grid; gap: 6px; }
.wa-vnext__run-input-heading { align-items: center; display: flex; gap: 8px; }
.wa-vnext__run-input-heading label { font-size: 12px; font-weight: 700; }
.wa-vnext__run-input-heading span { background: var(--wa-red-bg); border-radius: 3px; color: var(--wa-red); font-size: 10px; font-weight: 700; padding: 1px 5px; }
.wa-vnext__run-input-field > p { color: var(--wa-muted); font-size: 11px; margin: 0; }
.wa-vnext__run-input-field > .wa-vnext__field-error { color: var(--wa-red); }
.wa-vnext__run-result { border-top: 1px solid var(--wa-line); display: grid; gap: 12px; padding-top: 16px; }
.wa-vnext__run-result-heading { align-items: center; display: flex; flex-wrap: wrap; gap: 10px; }
.wa-vnext__run-result p { margin: 0; }
.wa-vnext__run-snapshot, .wa-vnext__run-outcome { background: var(--wa-subtle); border-left: 3px solid var(--wa-line); display: grid; gap: 4px; min-width: 0; padding: 10px 12px; }
.wa-vnext__run-snapshot span, .wa-vnext__run-outcome p { overflow-wrap: anywhere; white-space: pre-wrap; }
.wa-vnext__run-snapshot small, .wa-vnext__run-details-note { color: var(--wa-muted); font-size: 11px; }
.wa-vnext__run-summary { display: grid; gap: 12px; margin-bottom: 16px; }
.wa-vnext__run-summary .ant-descriptions { border-radius: var(--wa-radius); overflow: hidden; }
.wa-vnext__run-tabs { margin-top: 16px; }
.wa-vnext__run-tabs > .ant-tabs-nav { margin: 0 0 16px; min-height: 32px; }
.wa-vnext__run-tabs > .ant-tabs-nav::before { border-color: var(--wa-line); }
.wa-vnext__run-tabs .ant-tabs-tab { font-size: 12px; min-height: 32px; padding: 7px 2px; }
.wa-vnext__settings-layout { margin: 0 auto; max-width: 1120px; min-width: 0; }
.wa-vnext__settings-nav { border-bottom: 1px solid var(--wa-line); display: flex; gap: 22px; margin-bottom: 16px; min-height: 32px; overflow-x: auto; scrollbar-width: thin; }
.wa-vnext__settings-nav-link { border-bottom: 2px solid transparent; color: var(--wa-muted); flex: 0 0 auto; font-size: 12px; font-weight: 600; line-height: 17px; min-height: 32px; padding: 7px 2px 6px; text-decoration: none; }
.wa-vnext__settings-nav-link:hover { color: var(--wa-ink); }
.wa-vnext__settings-nav-link[aria-current="page"] { border-bottom-color: var(--wa-blue); color: var(--wa-blue); }
.wa-vnext__settings-panel { border: 1px solid var(--wa-line); border-radius: var(--wa-radius); min-width: 0; padding: 20px; }
.wa-vnext__settings-heading { border-bottom: 1px solid var(--wa-line); padding: 0 0 14px; }
.wa-vnext__settings-heading h2 { font-size: 16px; line-height: 22px; margin: 0; text-wrap: balance; }
.wa-vnext__settings-heading p { color: var(--wa-muted); font-size: 12px; line-height: 17px; margin: 4px 0 0; max-width: 760px; text-wrap: pretty; }
.wa-vnext__settings-fields { display: grid; }
.wa-vnext__settings-field { align-items: center; border-bottom: 1px solid #e4e7ec; display: grid; gap: 28px; grid-template-columns: 210px minmax(0, 1fr); min-height: 72px; padding: 14px 0; }
.wa-vnext__settings-field-copy { min-width: 0; }
.wa-vnext__settings-field-copy strong { display: block; font-size: 12px; line-height: 17px; }
.wa-vnext__settings-field-copy span { color: var(--wa-muted); display: block; font-size: 12px; line-height: 17px; margin-top: 3px; text-wrap: pretty; }
.wa-vnext__settings-field .ant-select { max-width: 520px; width: 100%; }
.wa-vnext__settings-footer { background: var(--wa-surface); padding: 0 40px max(12px, env(safe-area-inset-bottom)); }
.wa-vnext__settings-savebar { align-items: center; background: #1d2939; border: 1px solid #344054; border-radius: var(--wa-radius); box-shadow: 0 12px 30px rgba(16, 24, 40, .2); color: #fff; display: flex; gap: 20px; justify-content: space-between; margin: 0 auto; max-width: 1120px; padding: 10px 12px; }
.wa-vnext__settings-savebar strong { display: block; font-size: 12px; }
.wa-vnext__settings-savebar span { color: #d0d5dd; display: block; font-size: 11px; margin-top: 2px; }
.wa-vnext__settings-savebar .ant-btn-default { background: transparent; border-color: #667085; color: #fff; }
.wa-vnext__settings-facts { padding-top: 16px; }
.wa-vnext__settings-facts .ant-descriptions { border-radius: var(--wa-radius); overflow: hidden; }
.wa-vnext__settings-facts .ant-descriptions-item-label { color: var(--wa-muted); font-size: 12px; }
.wa-vnext__settings-facts .ant-descriptions-item-content { color: var(--wa-ink); font-size: 12px; min-width: 0; overflow-wrap: anywhere; }
.wa-vnext__account { display: flex; flex-direction: column; gap: 18px; padding-top: 16px; }
.wa-vnext__account-profile { align-items: center; display: flex; gap: 14px; min-width: 0; }
.wa-vnext__account-profile > div { min-width: 0; }
.wa-vnext__account-profile .ant-typography { margin: 0; overflow-wrap: anywhere; }
.wa-vnext__account .ant-descriptions { border-radius: var(--wa-radius); overflow: hidden; }
.wa-vnext__account .ant-descriptions-item-label { color: var(--wa-muted); font-size: 12px; }
.wa-vnext__account .ant-descriptions-item-content { color: var(--wa-ink); font-size: 12px; min-width: 0; overflow-wrap: anywhere; }
.wa-vnext__account-contract-note { font-size: 12px; margin: 0 !important; }
.wa-vnext__technical-details { color: var(--wa-muted); font-size: 12px; margin-top: 16px; max-width: 100%; }
.wa-vnext__technical-details summary { cursor: pointer; font-weight: 600; }
.wa-vnext__technical-details-body { background: #fff; border: 1px solid var(--wa-line); border-radius: 6px; display: block; margin-top: 8px; max-width: 100%; overflow-wrap: anywhere; padding: 10px; }
.wa-vnext__node-inspector { background: var(--wa-surface); border: 1px solid var(--wa-line); border-radius: var(--wa-radius); bottom: 16px; box-shadow: 0 16px 36px rgba(16, 24, 40, .16); display: flex; flex-direction: column; max-width: calc(100% - 32px); min-height: 0; overflow: hidden; position: absolute; right: 16px; top: 16px; width: min(400px, calc(100% - 32px)); z-index: 20; }
.wa-vnext__node-inspector-header { align-items: flex-start; border-bottom: 1px solid var(--wa-line); display: flex; gap: 12px; justify-content: space-between; padding: 16px 16px 14px; }
.wa-vnext__node-inspector-title.ant-typography { font-size: 15px; line-height: 1.35; margin: 0; }
.wa-vnext__node-inspector-subtitle { color: var(--wa-muted); display: block; font: 12px ui-monospace, SFMono-Regular, Menlo, monospace; margin-top: 4px; overflow-wrap: anywhere; }
.wa-vnext__node-inspector-body { display: grid; gap: 14px; min-height: 0; overflow: auto; overscroll-behavior: contain; padding: 16px; }
.wa-vnext__node-inspector-section-title.ant-typography { font-size: 14px; line-height: 1.4; margin: 0; }
.wa-vnext__node-inspector-description.ant-typography { color: var(--wa-muted); font-size: 12px; line-height: 1.5; margin: 5px 0 14px; text-wrap: pretty; }
.wa-vnext__node-inspector-fields { display: grid; gap: 14px; }
.wa-vnext__node-inspector-field { display: grid; gap: 6px; min-width: 0; }
.wa-vnext__node-inspector-field > span { color: var(--wa-ink); font-size: 12px; font-weight: 650; }
.wa-vnext__node-inspector-field small { color: var(--wa-muted); font-size: 11px; line-height: 1.45; }
.wa-vnext__node-inspector-error { margin-top: 14px; }
.wa-vnext__node-inspector-disclosure.ant-collapse { background: var(--wa-subtle); border: 1px solid var(--wa-line); border-radius: 4px; }
.wa-vnext__node-inspector-disclosure .ant-collapse-header { align-items: center; color: var(--wa-ink); font-size: 12px; font-weight: 650; min-height: 42px; }
.wa-vnext__node-inspector-disclosure .ant-collapse-content { border-top-color: var(--wa-line); }
.wa-vnext__node-inspector-disclosure .ant-collapse-content-box { padding-top: 12px; }
.wa-vnext__node-inspector-details { display: grid; gap: 12px; margin: 0; }
.wa-vnext__node-inspector-details div { display: grid; gap: 3px; min-width: 0; }
.wa-vnext__node-inspector-details dt { color: var(--wa-muted); font-size: 11px; font-weight: 650; }
.wa-vnext__node-inspector-details dd { color: var(--wa-ink); font-size: 12px; margin: 0; overflow-wrap: anywhere; }
.wa-vnext__node-inspector-advanced { display: grid; gap: 10px; }
.wa-vnext__node-inspector-advanced .ant-input { font: 12px ui-monospace, SFMono-Regular, Menlo, monospace; }
.wa-vnext__node-inspector-actions { align-items: center; border-top: 1px solid var(--wa-line); display: flex; gap: 8px; justify-content: flex-end; padding: 12px 16px max(12px, env(safe-area-inset-bottom)); }
.wa-vnext button:focus-visible, .wa-vnext a:focus-visible, .wa-vnext input:focus-visible, .wa-vnext textarea:focus-visible, .wa-vnext select:focus-visible, .wa-vnext__table-wrap:focus-visible { outline: 3px solid rgba(23, 92, 211, .25); outline-offset: 2px; }
.wa-vnext-drawer .ant-drawer-content { background: var(--wa-sidebar, #101828); color: #fff; }
.wa-vnext-drawer .ant-drawer-header { border-bottom-color: #344054; min-height: 52px; padding: 0 16px; }
.wa-vnext-drawer .ant-drawer-title, .wa-vnext-drawer .ant-drawer-close { color: #fff; }
.wa-vnext-drawer .ant-drawer-body { padding: 14px 10px; }
.wa-vnext-drawer .ant-drawer-content-wrapper { width: 240px !important; }
.wa-vnext-drawer .wa-vnext__nav { display: grid; gap: 4px; }
.wa-vnext-drawer .wa-vnext__nav-button { align-items: center; border: 1px solid transparent; border-radius: 6px; color: #d0d5dd; display: flex; font-size: 12px; gap: 10px; min-height: 36px; padding: 0 12px; text-decoration: none; }
.wa-vnext-drawer .wa-vnext__nav-button[aria-current="page"] { background: #344054; border-color: #475467; color: #fff; }
.wa-vnext__drawer-actions { border-top: 1px solid #344054; display: grid; gap: 4px; margin: 16px 2px 0; padding: 12px 0 0; }
.wa-vnext-drawer .wa-vnext__drawer-actions .console-header-actions__language { color: #f2f4f7; justify-content: flex-start; padding-inline: 12px; width: 100%; }
.wa-vnext-drawer .wa-vnext__drawer-actions .console-header-actions__login { color: #f2f4f7; justify-content: flex-start; padding-inline: 12px; }
.wa-vnext-drawer .wa-vnext__drawer-actions .console-header-actions__user { background: #1d2939 !important; border-color: #475467 !important; color: #f2f4f7; max-width: none !important; width: 100%; }
.wa-vnext-drawer .wa-vnext__drawer-actions .console-header-actions__user-name { color: #f2f4f7 !important; }
@media (max-width: 1100px) {
  .wa-vnext__header { padding-inline: 32px; }
  .wa-vnext__content { padding-inline: 32px; }
  .wa-vnext__settings-footer { padding-inline: 32px; }
  .wa-vnext__creation-options { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .wa-vnext__split { grid-template-columns: 1fr; }
}
@media (max-width: 767px) {
  .wa-vnext { grid-template-columns: minmax(0, 1fr); }
  .wa-vnext__topbar { padding-inline: 8px 12px; }
  .wa-vnext__menu-button { display: inline-flex; }
  .wa-vnext__topbar-leading { gap: 8px; }
  .wa-vnext__topbar-divider, .wa-vnext__topbar-context { display: none; }
  .wa-vnext__topbar-actions { display: none; }
  .wa-vnext__rail { display: none; }
  .wa-vnext__main { grid-column: 1; }
  .wa-vnext__header { align-items: flex-start; gap: 14px; padding: 16px 16px 0; }
  .wa-vnext__header h1 { font-size: 22px; line-height: 22px; }
  .wa-vnext__header-actions { max-width: 50%; }
  .wa-vnext__content { padding: 16px 16px 40px; }
  .wa-vnext__settings-footer { padding-inline: 16px; }
  .wa-vnext__table-wrap { max-height: min(560px, calc(100dvh - 240px)); }
  .wa-vnext__settings-layout { max-width: none; }
  .wa-vnext__node-inspector { bottom: 12px; left: 12px; max-height: calc(100% - 24px); max-width: none; right: 12px; top: auto; width: auto; }
}
@media (max-width: 600px) {
  .wa-vnext__header { flex-direction: column; }
  .wa-vnext__header-actions { justify-content: flex-start; max-width: 100%; width: 100%; }
  .wa-vnext__toolbar { align-items: stretch; flex-direction: column; }
  .wa-vnext__editor-toolbar { align-items: stretch; flex-direction: row; flex-wrap: wrap; overflow: visible; }
  .wa-vnext__editor-toolbar > * { flex: 1 1 100%; }
  .wa-vnext__editor-name { flex-basis: 100% !important; height: 32px; max-width: none; width: 100%; }
  .wa-vnext__editor-toolbar-meta { display: grid; gap: 8px; grid-template-columns: max-content minmax(0, 1fr); width: 100%; }
  .wa-vnext__editor-mode-control { width: 100%; }
  .wa-vnext__editor-mode-control .ant-segmented-group { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); width: 100%; }
  .wa-vnext__editor-mode-control .ant-segmented-item { min-width: 0; }
  .wa-vnext__editor-mode-control .ant-segmented-item-label { display: flex; justify-content: center; min-width: 0; }
  .wa-vnext__toolbar-search { flex-basis: auto; width: 100%; }
  .wa-vnext__toolbar-filters { display: grid; grid-template-columns: 1fr; width: 100%; }
  .wa-vnext__toolbar-filters .ant-space-item, .wa-vnext__toolbar-filters .ant-select, .wa-vnext__toolbar-filters .ant-btn { width: 100%; }
  .wa-vnext__creation-options { grid-template-columns: 1fr; }
  .wa-vnext__creation-option { min-height: 118px; }
  .wa-vnext__settings-panel { padding: 16px; }
  .wa-vnext__settings-nav { gap: 18px; }
  .wa-vnext__settings-field { align-items: stretch; gap: 8px; grid-template-columns: 1fr; min-height: 0; padding: 16px 0; }
  .wa-vnext__settings-field .ant-select { max-width: none; }
  .wa-vnext__settings-savebar { align-items: stretch; flex-direction: column; gap: 10px; }
  .wa-vnext__settings-actions.ant-space { display: grid; gap: 8px !important; grid-template-columns: 1fr 1fr; width: 100%; }
  .wa-vnext__settings-savebar .ant-btn { width: 100%; }
  .wa-vnext__state--compact { padding: 18px; }
  .wa-vnext__publish-review-item { gap: 6px; padding-block: 10px; }
}
@media (max-width: 360px) {
  .wa-vnext__settings-actions.ant-space { grid-template-columns: 1fr; }
}
@media (prefers-reduced-motion: reduce) { .wa-vnext *, .wa-vnext *::before, .wa-vnext *::after { scroll-behavior: auto !important; transition: none !important; } }
`;
