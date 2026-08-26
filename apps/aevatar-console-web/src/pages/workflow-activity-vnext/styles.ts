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
.wa-vnext__main--run-detail { display: grid; grid-template-rows: auto minmax(0, 1fr); overflow: hidden; }
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
.wa-vnext__heading-copy--custom { flex: 1 1 auto; max-width: min(560px, 100%); width: 100%; }
.wa-vnext__heading-copy--custom h1 { min-width: 0; width: 100%; }
.wa-vnext__header h1 { font-size: 28px; font-weight: 700; line-height: 28px; margin: 0; text-wrap: balance; }
.wa-vnext__header p { color: var(--wa-muted); font-size: 12px; line-height: 17px; margin: 7px 0 0; max-width: 760px; text-wrap: pretty; }
.wa-vnext__header-actions { align-items: center; display: flex; flex: 0 0 auto; flex-wrap: wrap; gap: 8px; justify-content: flex-end; }
.wa-vnext__run-detail-refresh { min-width: 132px; }
.wa-vnext__content { min-width: 0; padding: 18px 40px 48px; }
.wa-vnext__content--run-detail { display: flex; min-height: 0; overflow: hidden; padding-bottom: 40px; }
.wa-vnext__activity-filter-context { margin-bottom: 12px; }
.wa-vnext__toolbar { align-items: center; display: flex; flex-wrap: wrap; gap: 10px; justify-content: space-between; margin-bottom: 12px; min-height: 32px; }
.wa-vnext__toolbar-search { flex: 0 1 320px; max-width: 100%; width: 320px; }
.wa-vnext__toolbar-filters { justify-content: flex-end; }
.wa-vnext__toolbar-filters .ant-select { min-width: 150px; }
.wa-vnext__activity-footer { align-items: center; color: var(--wa-muted); display: flex; font-size: 12px; gap: 12px; justify-content: space-between; min-height: 48px; padding-top: 12px; }
.wa-vnext__activity-footer .ant-alert { flex: 1 1 auto; }
.wa-vnext__pagination-actions { align-items: center; display: flex; gap: 12px; justify-content: flex-end; min-height: 32px; padding-top: 16px; }
.wa-vnext__pagination-actions p { color: var(--wa-danger); font-size: 12px; line-height: 17px; margin: 0; }
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
.wa-vnext__activity-table-region {
  height: clamp(420px, calc(100dvh - 300px), 720px);
}
.wa-vnext__table { border-collapse: separate; border-spacing: 0; font-size: 12px; line-height: 17px; min-width: 900px; table-layout: fixed; width: 100%; }
.wa-vnext__table--workflow-catalogue { min-width: 1160px; }
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
.wa-vnext__activity-column--workflow { width: 22%; }
.wa-vnext__activity-column--status { width: 18%; }
.wa-vnext__activity-column--started { width: 170px; }
.wa-vnext__activity-column--duration { width: 76px; }
.wa-vnext__activity-column--input { width: auto; }
.wa-vnext__table-column--status { width: 120px; }
.wa-vnext__table-column--updated { width: 190px; }
.wa-vnext__table-column--actions { width: 270px; }
.wa-vnext__table td { border-bottom: 1px solid var(--wa-line); height: 76px; overflow-wrap: anywhere; padding: 10px 12px; vertical-align: middle; }
.wa-vnext__table tr:last-child td { border-bottom: 0; }
.wa-vnext__table tbody tr:hover { background: #f9fafb; }
.wa-vnext__activity-row { cursor: pointer; }
.wa-vnext__activity-row:focus-visible { outline: 3px solid rgba(23, 92, 211, .25); outline-offset: -3px; }
.wa-vnext__table pre { margin: 0; max-width: 100%; white-space: pre-wrap; word-break: break-word; }
.wa-vnext__workflow-actions-cell { text-align: right; }
.wa-vnext__workflow-actions { align-items: center; display: inline-flex; white-space: nowrap; }
.wa-vnext__workflow-actions .ant-btn { flex: 0 0 auto; }
.wa-vnext__run-link { max-width: 100%; min-width: 0; overflow: hidden; }
.wa-vnext__title { display: block; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.wa-vnext__sub { color: var(--wa-muted); display: block; font-size: 12px; line-height: 17px; margin-top: 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.wa-vnext__input-preview {
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
  display: -webkit-box;
  line-height: 17px;
  max-height: 34px;
  overflow: hidden;
  overflow-wrap: anywhere;
}
.wa-vnext__workflow-name-trigger { background: transparent; border: 0; color: inherit; cursor: help; display: block; font: inherit; margin: 0; max-width: 100%; padding: 0; text-align: left; }
.wa-vnext__workflow-description-popover { max-width: calc(100vw - 48px); width: 320px; }
.wa-vnext__workflow-description { margin: 0; max-width: 100%; overflow-wrap: anywhere; white-space: normal; }
.wa-vnext__status { align-items: center; border: 1px solid currentColor; border-radius: 5px; display: inline-flex; font-size: 11px; font-weight: 600; gap: 6px; min-height: 24px; padding: 0 8px; white-space: nowrap; }
.wa-vnext__status::before { background: currentColor; border-radius: 50%; content: ""; height: 6px; width: 6px; }
.wa-vnext__status--draft { background: #f4f3ff; color: #6941c6; }
.wa-vnext__status--archived { background: var(--wa-subtle); color: var(--wa-muted); }
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
.wa-vnext__field-control.ant-select { align-items: center; display: flex; }
.wa-vnext__field-control.ant-select .ant-select-content { align-items: center; display: flex; flex: 1 1 auto; min-width: 0; }
.wa-vnext__field-control.ant-select .ant-select-suffix { align-items: center; display: flex; flex: 0 0 auto; }
.wa-vnext__duplicate-warning { color: var(--wa-amber); font-size: 12px; line-height: 17px; margin: 6px 0 0; }
.wa-vnext__modal-field { display: grid; font-size: 12px; font-weight: 600; gap: 6px; }
.wa-vnext__mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; overflow-wrap: anywhere; }
.wa-vnext__split { display: grid; gap: 20px; grid-template-columns: minmax(0, 1fr) minmax(280px, 34%); }
.wa-vnext__creation-options { border: 0; display: grid; gap: 12px; grid-template-columns: repeat(4, minmax(0, 1fr)); margin: 0 auto; max-width: 960px; min-width: 0; padding: 0; width: 100%; }
.wa-vnext__creation-option { background: var(--wa-surface); border: 1px solid var(--wa-line); border-radius: var(--wa-radius); min-height: 142px; padding: 18px; text-align: left; transition: border-color .15s ease, box-shadow .15s ease; }
.wa-vnext__creation-option:hover:not(:disabled) { border-color: var(--wa-blue); box-shadow: 0 3px 12px rgba(16, 24, 40, .08); }
.wa-vnext__creation-option-icon { color: var(--wa-blue); display: block; font-size: 20px; line-height: 24px; }
.wa-vnext__creation-option-title { display: block; font-size: 14px; font-weight: 700; line-height: 20px; margin-top: 13px; }
.wa-vnext__creation-option-description { color: var(--wa-muted); display: block; font-size: 12px; line-height: 17px; margin-top: 5px; }
.wa-vnext__creation-surface { margin: 0 auto; max-width: 720px; width: 100%; }
.wa-vnext__creation-form { display: grid; gap: 22px; padding-block: 4px 28px; }
.wa-vnext__creation-heading { align-items: center; border-bottom: 1px solid var(--wa-line); display: flex; gap: 10px; padding-bottom: 14px; }
.wa-vnext__creation-heading .ant-btn { margin-left: -12px; }
.wa-vnext__creation-field > span:first-child { color: var(--wa-ink); display: block; font-size: 12px; font-weight: 650; line-height: 18px; }
.wa-vnext__creation-actions { display: flex; justify-content: flex-end; padding-top: 2px; }
.wa-vnext__creation-actions .ant-btn { min-width: 156px; }
.wa-vnext__creation-template-preview { border-left: 2px solid var(--wa-blue); padding: 2px 0 2px 14px; }
.wa-vnext__template-browser { display: grid; gap: 18px; min-width: 0; }
.wa-vnext__template-toolbar { align-items: center; display: flex; flex-wrap: wrap; gap: 10px; justify-content: space-between; }
.wa-vnext__template-search { flex: 1 1 320px; max-width: 560px; }
.wa-vnext__template-sort { align-items: center; display: flex; flex: 0 0 auto; gap: 8px; }
.wa-vnext__template-sort-label { color: var(--wa-muted); font-size: 12px; font-weight: 600; white-space: nowrap; }
.wa-vnext__template-sort .ant-select { min-width: 224px; }
.wa-vnext__table-wrap.wa-vnext__template-table-region { border: 0; border-radius: 0; max-height: none; overflow-y: hidden; overscroll-behavior-y: auto; scrollbar-gutter: auto; }
.wa-vnext__template-table { min-width: 1160px; }
.wa-vnext__template-table th { background: #f4f6fa; border-bottom: 0; height: 52px; padding-inline: 14px; position: static; }
.wa-vnext__template-table th:first-child { border-radius: 8px 0 0 8px; }
.wa-vnext__template-table th:last-child { border-radius: 0 8px 8px 0; }
.wa-vnext__template-column--identity { width: 35%; }
.wa-vnext__template-column--reads { width: 12%; }
.wa-vnext__template-column--connection { width: 15%; }
.wa-vnext__template-column--does { width: 12%; }
.wa-vnext__template-column--updated { width: 110px; }
.wa-vnext__template-column--actions { width: 210px; }
.wa-vnext__template-header-label { align-items: center; display: inline-flex; gap: 10px; white-space: nowrap; }
.wa-vnext__template-header-label .anticon { color: #667085; font-size: 15px; }
.wa-vnext__template-table td { height: 106px; padding: 18px 14px; }
.wa-vnext__template-identity { align-items: center; display: grid; gap: 14px; grid-template-columns: 48px minmax(0, 1fr); min-width: 0; }
.wa-vnext__template-marker { align-items: center; border-radius: 8px; display: inline-flex; font-size: 24px; height: 48px; justify-content: center; width: 48px; }
.wa-vnext__template-marker--violet { background: #eee8ff; color: #6938ef; }
.wa-vnext__template-marker--teal { background: #dcf7f4; color: #0e9384; }
.wa-vnext__template-marker--green { background: #e3f8e7; color: #16a34a; }
.wa-vnext__template-marker--amber { background: #fff3d6; color: #d49400; }
.wa-vnext__template-marker--coral { background: #ffe4e1; color: #f04438; }
.wa-vnext__template-copy { min-width: 0; }
.wa-vnext__template-identity h2 { font-size: 15px; line-height: 20px; margin: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.wa-vnext__template-identity p { -webkit-box-orient: vertical; -webkit-line-clamp: 2; color: var(--wa-muted); display: -webkit-box; line-height: 18px; margin: 5px 0 0; max-height: 36px; overflow: hidden; overflow-wrap: anywhere; }
.wa-vnext__template-fact { min-width: 0; }
.wa-vnext__template-fact strong { font-size: 12px; font-weight: 600; line-height: 17px; overflow-wrap: anywhere; }
.wa-vnext__template-updated { color: var(--wa-muted); white-space: nowrap; }
.wa-vnext__template-actions-cell { text-align: right; }
.wa-vnext__template-actions { align-items: center; display: inline-flex; gap: 8px; justify-content: flex-end; white-space: nowrap; }
.wa-vnext__template-actions .ant-btn:first-child { min-width: 64px; }
.wa-vnext__template-actions .ant-btn:last-child { min-width: 112px; }
.wa-vnext__template-detail { display: grid; gap: 16px; padding-top: 4px; }
.wa-vnext__template-preview-heading { display: grid; gap: 3px; }
.wa-vnext__template-preview-heading strong { font-size: 14px; line-height: 20px; }
.wa-vnext__template-preview-heading span { color: var(--wa-muted); font-size: 12px; line-height: 18px; }
.wa-vnext__template-preview-empty { align-items: center; border: 1px solid var(--wa-line); border-radius: var(--wa-radius); color: var(--wa-muted); display: flex; justify-content: center; min-height: 220px; padding: 24px; text-align: center; }
.wa-vnext__template-description { border-bottom: 1px solid var(--wa-line); padding-bottom: 14px; }
.wa-vnext__template-description p { line-height: 19px; margin-bottom: 6px; }
.wa-vnext__form-title { font-size: 16px; font-weight: 700; line-height: 22px; margin: 0; }
.wa-vnext__editor-toolbar { flex-wrap: nowrap; justify-content: flex-end; min-width: 0; overflow: visible; }
.wa-vnext__editor-toolbar > * { flex: 0 1 auto; min-width: 0; }
.wa-vnext__editor-name.ant-input {
  color: var(--wa-ink);
  font-size: 28px;
  font-weight: 700;
  height: 36px;
  line-height: 28px;
  max-width: 100%;
  padding: 2px 4px;
}
.wa-vnext__editor-toolbar-meta { align-items: center; display: flex; flex: 0 1 auto; gap: 8px; min-width: 0; }
.wa-vnext__publish-readiness { max-width: min(360px, calc(100vw - 32px)); }
.wa-vnext__publish-readiness ul { display: grid; gap: 6px; list-style: none; margin: 0; padding: 0; }
.wa-vnext__publish-readiness li { line-height: 1.4; overflow-wrap: anywhere; }
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
.wa-vnext__run-workspace { display: flex; height: min(620px, calc(100dvh - 248px)); min-height: 440px; min-width: 0; overflow: hidden; width: 100%; }
.wa-vnext__run-workspace > .wa-vnext__editor-yaml { flex: 1 1 auto; min-width: 0; width: auto; }
.wa-vnext__logs-dock { background: var(--wa-surface); min-width: 0; }
.wa-vnext__logs-dock--expanded { display: flex; flex-direction: column; }
.wa-vnext__logs-dock-bar { align-items: center; background: var(--wa-surface); border-top: 1px solid var(--wa-line); display: flex; height: 44px; justify-content: space-between; padding: 0 14px; }
.wa-vnext__logs-dock-bar strong { color: var(--wa-ink); font-size: 13px; }
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
.wa-vnext__aria-disabled { cursor: not-allowed; opacity: .55; }
.wa-vnext__recovery-notices { display: grid; gap: 8px; margin-bottom: 12px; }
.wa-vnext__related-runs { border-top: 1px solid var(--wa-line); margin-top: 16px; padding-top: 16px; }
.wa-vnext__related-runs h2 { font-size: 16px; margin: 0 0 12px; }
.wa-vnext__related-runs h3 { font-size: 13px; margin: 0 0 8px; }
.wa-vnext__related-groups { display: grid; gap: 24px; grid-template-columns: repeat(2, minmax(0, 1fr)); }
.wa-vnext__related-list { display: grid; gap: 6px; list-style: none; margin: 0; padding: 0; }
.wa-vnext__related-list li { display: grid; gap: 2px; }
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
.wa-vnext__settings-facts-heading { font-size: 13px; line-height: 18px; margin: 0 0 10px; }
.wa-vnext__settings-facts .ant-descriptions { border-radius: var(--wa-radius); overflow: hidden; }
.wa-vnext__settings-facts .ant-descriptions-item-label { color: var(--wa-muted); font-size: 12px; }
.wa-vnext__settings-facts .ant-descriptions-item-content { color: var(--wa-ink); font-size: 12px; min-width: 0; overflow-wrap: anywhere; }
.wa-vnext__account { display: flex; flex-direction: column; gap: 18px; padding-top: 16px; }
.wa-vnext__account-profile { align-items: center; display: flex; gap: 14px; justify-content: space-between; min-width: 0; }
.wa-vnext__account-profile-identity { align-items: center; display: flex; gap: 14px; min-width: 0; }
.wa-vnext__account-profile-identity > div { min-width: 0; }
.wa-vnext__account-profile .ant-typography { margin: 0; overflow-wrap: anywhere; }
.wa-vnext__account-section { display: grid; gap: 10px; min-width: 0; }
.wa-vnext__account-section > h3, .wa-vnext__account-section-heading h3 { font-size: 13px; line-height: 18px; margin: 0; }
.wa-vnext__account-section-heading { align-items: center; display: flex; gap: 12px; justify-content: space-between; }
.wa-vnext__account-recovery { margin-top: 4px; }
.wa-vnext__account .ant-descriptions { border-radius: var(--wa-radius); overflow: hidden; }
.wa-vnext__account .ant-descriptions-item-label { color: var(--wa-muted); font-size: 12px; }
.wa-vnext__account .ant-descriptions-item-content { color: var(--wa-ink); font-size: 12px; min-width: 0; overflow-wrap: anywhere; }
.wa-vnext__technical-details { color: var(--wa-muted); font-size: 12px; margin-top: 16px; max-width: 100%; }
.wa-vnext__technical-details summary { cursor: pointer; font-weight: 600; }
.wa-vnext__technical-details-body { background: #fff; border: 1px solid var(--wa-line); border-radius: 6px; display: block; margin-top: 8px; max-width: 100%; overflow-wrap: anywhere; padding: 10px; }
.wa-vnext-run-detail {
  background: var(--wa-surface);
  border: 1px solid var(--wa-line);
  border-radius: var(--wa-radius);
  color: var(--wa-ink);
  display: grid;
  grid-template-columns: minmax(256px, 312px) minmax(0, 1fr);
  flex: 1 1 auto;
  height: 100%;
  max-height: 100%;
  min-height: 0;
  overflow: hidden;
  position: relative;
  width: 100%;
}
.wa-vnext-run-detail__refresh-content { display: contents; }
.wa-vnext-run-detail__rail {
  background: var(--wa-surface);
  border-right: 1px solid var(--wa-line);
  display: flex;
  flex-direction: column;
  min-height: 0;
  min-width: 0;
  overflow: hidden;
}
.wa-vnext-run-detail__rail-header {
  align-items: center;
  border-bottom: 1px solid var(--wa-line);
  display: flex;
  gap: 12px;
  justify-content: space-between;
  min-width: 0;
  padding: 12px 16px;
}
.wa-vnext-run-detail__rail-title {
  display: grid;
  gap: 3px;
  min-width: 0;
}
.wa-vnext-run-detail__rail-title h5.ant-typography {
  font-size: 14px;
  line-height: 20px;
}
.wa-vnext-run-detail__rail-title .ant-typography {
  font-size: 12px;
  line-height: 17px;
}
.wa-vnext-run-detail__rail-list {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  min-height: 0;
  overflow-x: hidden;
  overflow-y: auto;
  overscroll-behavior: contain;
  padding: 10px;
  scrollbar-gutter: stable;
}
.wa-vnext-run-detail__run {
  background: transparent;
  border: 1px solid transparent;
  border-radius: 6px;
  color: inherit;
  cursor: pointer;
  display: grid;
  gap: 3px;
  min-width: 0;
  padding: 8px 10px;
  text-align: left;
  transition: background 120ms ease, border-color 120ms ease, box-shadow 120ms ease;
  width: 100%;
}
.wa-vnext-run-detail__run:hover {
  background: var(--wa-subtle);
  border-color: var(--wa-line);
}
.wa-vnext-run-detail__run--selected { background: var(--wa-blue-bg); border-color: color-mix(in srgb, var(--wa-blue) 35%, var(--wa-line)); box-shadow: inset 2px 0 0 var(--wa-blue); }
.wa-vnext-run-detail__run-title {
  align-items: center;
  display: flex;
  gap: 8px;
  min-width: 0;
}
.wa-vnext-run-detail__run .ant-typography {
  font-size: 12px;
  line-height: 17px;
}
.wa-vnext-run-detail__stage {
  display: grid;
  grid-template-rows: auto minmax(280px, 1fr) minmax(220px, 32vh);
  min-height: 0;
  min-width: 0;
}
.wa-vnext-run-detail__stage-header {
  align-items: center;
  background: var(--wa-surface);
  border-bottom: 1px solid var(--wa-line);
  display: flex;
  gap: 12px;
  justify-content: space-between;
  min-width: 0;
  padding: 12px 16px;
}
.wa-vnext-run-detail__stage-title {
  display: grid;
  gap: 4px;
  min-width: 0;
}
.wa-vnext-run-detail__stage-title h4.ant-typography {
  font-size: 16px;
  line-height: 22px;
}
.wa-vnext-run-detail__stage-title .ant-typography {
  font-size: 12px;
  line-height: 17px;
}
.wa-vnext-run-detail__stage-actions {
  align-items: center;
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  justify-content: flex-end;
}
.wa-vnext-run-detail__graph {
  background: var(--wa-subtle);
  min-height: 0;
  min-width: 0;
  padding: 12px;
}
.wa-vnext-run-detail__graph > * {
  height: 100%;
  min-height: 0;
}
.wa-vnext-run-detail__details {
  background: var(--wa-surface);
  border-top: 1px solid var(--wa-line);
  display: grid;
  grid-template-columns: minmax(220px, 300px) minmax(0, 1fr);
  min-height: 0;
  min-width: 0;
}
.wa-vnext-run-detail__logs {
  border-right: 1px solid var(--wa-line);
  display: flex;
  flex-direction: column;
  min-height: 0;
  min-width: 0;
}
.wa-vnext-run-detail__logs-header,
.wa-vnext-run-detail__inspector-header {
  align-items: center;
  border-bottom: 1px solid var(--wa-line);
  display: flex;
  gap: 8px;
  justify-content: space-between;
  min-width: 0;
  padding: 10px 12px;
}
.wa-vnext-run-detail__step-list {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  min-height: 0;
  overflow-x: hidden;
  overflow-y: auto;
  overscroll-behavior: contain;
  padding: 8px;
  scrollbar-gutter: stable;
}
.wa-vnext-run-detail__step {
  align-items: center;
  background: transparent;
  border: 1px solid transparent;
  border-radius: 6px;
  color: inherit;
  cursor: pointer;
  display: grid;
  gap: 8px;
  grid-template-columns: 18px minmax(0, 1fr) auto;
  min-width: 0;
  padding: 8px 10px;
  text-align: left;
  width: 100%;
}
.wa-vnext-run-detail__step:hover { background: var(--wa-subtle); border-color: var(--wa-line); }
.wa-vnext-run-detail__step--selected { background: var(--wa-blue-bg); border-color: color-mix(in srgb, var(--wa-blue) 30%, var(--wa-line)); }
.wa-vnext-run-detail__inspector {
  display: flex;
  flex-direction: column;
  min-height: 0;
}
.wa-vnext-run-detail__inspector-body {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  overflow: auto;
  padding: 14px;
}
.wa-vnext-run-detail__inspector-body > .ant-tabs {
  min-height: 0;
}
.wa-vnext-run-detail__inspector-body > .ant-tabs > .ant-tabs-content-holder {
  min-height: 0;
}
.wa-vnext-run-detail__inspector-body .ant-tabs-tabpane {
  min-height: 0;
}
.wa-vnext-run-detail__pre,
.wa-vnext-run-detail__kv,
.wa-vnext-run-detail__timeline {
  background: var(--wa-subtle);
  border: 1px solid var(--wa-line);
  border-radius: var(--wa-radius);
  margin: 0;
  max-width: 100%;
  overflow-wrap: anywhere;
}
.wa-vnext-run-detail__pre {
  padding: 12px;
  white-space: pre-wrap;
  word-break: break-word;
}
.wa-vnext-run-detail__kv {
  display: grid;
  gap: 8px;
  padding: 12px;
}
.wa-vnext-run-detail__kv-row {
  display: grid;
  gap: 4px;
}
.wa-vnext-run-detail__kv-key { color: var(--wa-muted); font-size: 11px; font-weight: 700; text-transform: uppercase; }
.wa-vnext-run-detail__kv-value { color: var(--wa-ink); font-size: 12px; overflow-wrap: anywhere; white-space: pre-wrap; }
.wa-vnext-run-detail__timeline {
  display: grid;
  gap: 8px;
  padding: 12px;
}
.wa-vnext-run-detail__timeline-row {
  display: grid;
  gap: 4px;
  grid-template-columns: 120px minmax(0, 1fr);
}
.wa-vnext-run-detail__timeline-key {
  color: var(--wa-muted);
  font-size: 11px;
  font-weight: 700;
  overflow-wrap: anywhere;
}
.wa-vnext-run-detail__timeline-value {
  overflow-wrap: anywhere;
}
.wa-vnext-run-detail .wa-vnext__status {
  white-space: nowrap;
}
.wa-vnext-run-detail .ant-tag {
  margin-inline-end: 0;
}
@keyframes wa-vnext-run-detail-skeleton-pulse {
  0%, 100% { opacity: .55; }
  50% { opacity: 1; }
}
.wa-vnext-run-detail--loading {
  cursor: progress;
}
.wa-vnext-run-detail__stage--loading {
  cursor: progress;
}
.wa-vnext-run-detail--loading .wa-vnext-run-detail__rail-header,
.wa-vnext-run-detail__stage--loading .wa-vnext-run-detail__stage-header,
.wa-vnext-run-detail__stage--loading .wa-vnext-run-detail__logs-header,
.wa-vnext-run-detail__stage--loading .wa-vnext-run-detail__inspector-header {
  min-height: 54px;
}
.wa-vnext-run-detail__skeleton-line,
.wa-vnext-run-detail__skeleton-dot,
.wa-vnext-run-detail__skeleton-node {
  animation: wa-vnext-run-detail-skeleton-pulse 1.5s ease-in-out infinite;
}
.wa-vnext-run-detail__skeleton-line {
  background: color-mix(in srgb, var(--wa-line) 64%, var(--wa-subtle));
  border-radius: 4px;
  display: block;
  height: 10px;
  max-width: 100%;
}
.wa-vnext-run-detail__skeleton-line--title { height: 14px; width: 112px; }
.wa-vnext-run-detail__skeleton-line--short { width: 72px; }
.wa-vnext-run-detail__skeleton-line--run { width: 76%; }
.wa-vnext-run-detail__skeleton-line--meta { height: 9px; width: 48%; }
.wa-vnext-run-detail__skeleton-line--heading { height: 16px; width: 148px; }
.wa-vnext-run-detail__skeleton-line--pill { border-radius: 10px; height: 20px; width: 64px; }
.wa-vnext-run-detail__skeleton-line--subtitle { width: 196px; }
.wa-vnext-run-detail__skeleton-line--node-title { height: 12px; width: 62%; }
.wa-vnext-run-detail__skeleton-line--node-meta { height: 9px; width: 42%; }
.wa-vnext-run-detail__skeleton-line--label { height: 12px; width: 44px; }
.wa-vnext-run-detail__skeleton-line--duration { height: 9px; width: 42px; }
.wa-vnext-run-detail__skeleton-line--step { width: 68%; }
.wa-vnext-run-detail__skeleton-line--step-meta { height: 8px; width: 44%; }
.wa-vnext-run-detail__skeleton-line--inspector-title { height: 12px; width: 132px; }
.wa-vnext-run-detail__skeleton-line--tabs { height: 12px; margin-bottom: 10px; width: 180px; }
.wa-vnext-run-detail__skeleton-line--content { width: 82%; }
.wa-vnext-run-detail__skeleton-line--content-short { width: 58%; }
.wa-vnext-run-detail__skeleton-heading {
  align-items: center;
  display: flex;
  gap: 10px;
}
.wa-vnext-run-detail__run--loading {
  cursor: progress;
  min-height: 53px;
}
.wa-vnext-run-detail__graph--loading {
  overflow: hidden;
  position: relative;
}
.wa-vnext-run-detail__skeleton-node {
  background: var(--wa-surface);
  border: 1px solid var(--wa-line);
  border-radius: 6px;
  display: grid;
  gap: 9px;
  left: 50%;
  padding: 14px;
  position: absolute;
  transform: translateX(-50%);
  width: clamp(160px, 28%, 220px);
  z-index: 1;
}
.wa-vnext-run-detail__skeleton-node--1 { top: 10%; }
.wa-vnext-run-detail__skeleton-node--2 { top: 42%; }
.wa-vnext-run-detail__skeleton-node--3 { top: 74%; }
.wa-vnext-run-detail__skeleton-connector {
  background: var(--wa-line);
  height: 18%;
  left: 50%;
  position: absolute;
  top: 27%;
  width: 1px;
}
.wa-vnext-run-detail__skeleton-connector--second { top: 59%; }
.wa-vnext-run-detail__step--loading { cursor: progress; }
.wa-vnext-run-detail__skeleton-dot {
  background: color-mix(in srgb, var(--wa-line) 72%, var(--wa-subtle));
  border-radius: 50%;
  height: 12px;
  justify-self: center;
  width: 12px;
}
.wa-vnext-run-detail__skeleton-step-copy {
  display: grid;
  gap: 6px;
  min-width: 0;
}
.wa-vnext-run-detail__inspector-body--loading { gap: 10px; }
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
.wa-vnext-schedule-modal, .wa-vnext-schedule-drawer {
  --wa-ink: #17202a;
  --wa-muted: #667085;
  --wa-line: #d0d5dd;
  --wa-surface: #ffffff;
  --wa-subtle: #f8fafc;
  --wa-blue: #175cd3;
  --wa-radius: 8px;
  color: var(--wa-ink);
  font-family: AlibabaSans, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
  font-size: 12px;
}
.wa-vnext-schedule-modal .ant-modal-content { border-radius: var(--wa-radius); }
.wa-vnext-schedule-modal .ant-btn, .wa-vnext-schedule-drawer .ant-btn { font-size: 12px; }
.wa-vnext__schedule-surface-title { color: var(--wa-ink); font-size: 16px; font-weight: 650; line-height: 22px; margin: 0; }
.wa-vnext__schedule-form-title { color: var(--wa-ink); font-size: 15px; line-height: 21px; margin: 0; }
.wa-vnext__schedule-surface { display: grid; gap: 16px; min-width: 0; position: relative; }
.wa-vnext__schedule-toolbar { align-items: flex-start; display: flex; gap: 16px; justify-content: space-between; }
.wa-vnext__schedule-toolbar p { color: var(--wa-muted); margin: 4px 0 0; }
.wa-vnext__schedule-refresh-overlay { grid-column: 1; grid-row: 2; }
.wa-vnext__schedule-list { display: grid; gap: 10px; }
.wa-vnext__schedule-row { align-items: center; appearance: none; background: var(--wa-subtle); border: 1px solid var(--wa-line); border-radius: var(--wa-radius); color: var(--wa-ink); cursor: pointer; display: flex; font: inherit; gap: 16px; justify-content: space-between; min-width: 0; padding: 14px; text-align: left; transition: background-color .15s ease, border-color .15s ease, box-shadow .15s ease; width: 100%; }
.wa-vnext__schedule-row:hover { background: #f2f4f7; border-color: #98a2b3; box-shadow: 0 2px 8px rgba(16, 24, 40, .06); }
.wa-vnext__schedule-row:focus-visible { outline: 3px solid rgba(23, 92, 211, .25); outline-offset: 2px; }
.wa-vnext__schedule-row-main { display: grid; gap: 5px; min-width: 0; }
.wa-vnext__schedule-row-heading { align-items: center; display: flex; gap: 8px; min-width: 0; }
.wa-vnext__schedule-row-heading strong { overflow-wrap: anywhere; }
.wa-vnext__schedule-row-main code { color: var(--wa-ink); font: 12px ui-monospace, SFMono-Regular, Menlo, monospace; }
.wa-vnext__schedule-row-main > span { color: var(--wa-muted); font-size: 11px; overflow-wrap: anywhere; }
.wa-vnext__schedule-row-arrow { color: var(--wa-muted); flex: 0 0 auto; font-size: 22px; line-height: 1; }
.wa-vnext__schedule-selected-title { align-items: center; display: flex; gap: 8px; min-width: 0; }
.wa-vnext__schedule-selected-title > .ant-btn { flex: 0 0 auto; margin-inline-start: -8px; }
.wa-vnext__schedule-selected-heading { display: grid; gap: 1px; min-width: 0; }
.wa-vnext__schedule-selected-title strong { color: var(--wa-ink); font-size: 16px; line-height: 21px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.wa-vnext__schedule-selected-title span { color: var(--wa-muted); font-size: 11px; line-height: 16px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.wa-vnext__schedule-selected-heading--history { align-items: baseline; display: flex; font-weight: inherit; margin: 0; overflow: hidden; white-space: nowrap; }
.wa-vnext__schedule-selected-heading--history strong, .wa-vnext__schedule-selected-heading--history > span[aria-hidden="true"] { flex: 0 0 auto; }
.wa-vnext__schedule-selected-heading--history .wa-vnext__schedule-selected-heading-context { flex: 0 1 auto; font-size: 13px; line-height: 21px; min-width: 0; }
.wa-vnext__schedule-detail { display: grid; gap: 18px; min-width: 0; position: relative; }
.wa-vnext__schedule-detail > .ant-tabs { margin-bottom: -2px; }
.wa-vnext__schedule-overview { display: grid; gap: 14px; min-width: 0; }
.wa-vnext__schedule-overview-summary { background: var(--wa-subtle); border: 1px solid var(--wa-line); border-radius: var(--wa-radius); display: grid; gap: 8px; padding: 16px; }
.wa-vnext__schedule-overview-summary .ant-tag { justify-self: start; margin-inline-end: 0; }
.wa-vnext__schedule-overview-summary h2 { color: var(--wa-ink); font-size: 18px; line-height: 24px; margin: 0; overflow-wrap: anywhere; }
.wa-vnext__schedule-overview-summary p { color: var(--wa-muted); font-size: 12px; line-height: 17px; margin: 0; overflow-wrap: anywhere; }
.wa-vnext__schedule-overview-summary p span { font-weight: 650; }
.wa-vnext__schedule-overview-actions { align-items: center; display: flex; flex-wrap: wrap; gap: 8px; justify-content: flex-end; }
.wa-vnext__schedule-detail-facts { background: var(--wa-surface); border: 1px solid var(--wa-line); border-radius: var(--wa-radius); display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); margin: 0; overflow: hidden; }
.wa-vnext__schedule-detail-facts div { display: grid; gap: 4px; min-width: 0; padding: 12px 14px; }
.wa-vnext__schedule-detail-facts div:nth-child(odd) { border-right: 1px solid var(--wa-line); }
.wa-vnext__schedule-detail-facts div:nth-child(n + 3) { border-top: 1px solid var(--wa-line); }
.wa-vnext__schedule-detail-facts dt { color: var(--wa-muted); font-size: 11px; font-weight: 650; }
.wa-vnext__schedule-detail-facts dd { color: var(--wa-ink); font-size: 12px; margin: 0; overflow-wrap: anywhere; }
.wa-vnext__schedule-detail-facts code { font: 12px ui-monospace, SFMono-Regular, Menlo, monospace; }
.wa-vnext__schedule-run-input { display: grid; gap: 6px; }
.wa-vnext__schedule-run-input h3 { color: var(--wa-ink); font-size: 12px; line-height: 17px; margin: 0; }
.wa-vnext__schedule-run-input p { background: var(--wa-subtle); border: 1px solid var(--wa-line); border-radius: 6px; color: var(--wa-ink); line-height: 18px; margin: 0; max-height: 120px; overflow: auto; padding: 10px 12px; white-space: pre-wrap; }
.wa-vnext__schedule-advanced-details { color: var(--wa-muted); padding: 2px; }
.wa-vnext__schedule-advanced-details summary { cursor: pointer; font-size: 11px; font-weight: 650; }
.wa-vnext__schedule-advanced-details dl { display: grid; gap: 4px; margin: 10px 0 0; }
.wa-vnext__schedule-advanced-details div { align-items: baseline; display: grid; gap: 10px; grid-template-columns: max-content minmax(0, 1fr); }
.wa-vnext__schedule-advanced-details dt { font-size: 11px; }
.wa-vnext__schedule-advanced-details dd { color: var(--wa-ink); margin: 0; overflow-wrap: anywhere; }
.wa-vnext__schedule-advanced-details code { font: 12px ui-monospace, SFMono-Regular, Menlo, monospace; }
.wa-vnext__schedule-history { display: grid; gap: 14px; min-width: 0; }
.wa-vnext__schedule-history-header { align-items: flex-start; display: flex; gap: 16px; justify-content: space-between; }
.wa-vnext__schedule-history-header h2 { color: var(--wa-ink); font-size: 15px; line-height: 21px; margin: 0; }
.wa-vnext__schedule-history-header p { color: var(--wa-muted); line-height: 17px; margin: 4px 0 0; max-width: 520px; }
.wa-vnext__schedule-history-header a { flex: 0 0 auto; font-size: 12px; line-height: 17px; }
.wa-vnext__schedule-history-table-wrap { border: 1px solid var(--wa-line); border-radius: var(--wa-radius); max-height: min(440px, calc(100dvh - 320px)); max-width: 100%; overflow: auto; scrollbar-gutter: stable; }
.wa-vnext__schedule-history-table { border-collapse: separate; border-spacing: 0; font-size: 12px; min-width: 720px; table-layout: fixed; width: 100%; }
.wa-vnext__schedule-history-table th { background: var(--wa-subtle); border-bottom: 1px solid var(--wa-line); color: var(--wa-muted); font-size: 10px; font-weight: 650; letter-spacing: 1px; padding: 9px 12px; position: sticky; text-align: left; text-transform: uppercase; top: 0; z-index: 1; }
.wa-vnext__schedule-history-table td { border-bottom: 1px solid var(--wa-line); color: var(--wa-ink); line-height: 17px; padding: 11px 12px; vertical-align: top; }
.wa-vnext__schedule-history-table tbody tr:last-child td { border-bottom: 0; }
.wa-vnext__schedule-history-attempt-link { align-items: center; border-radius: 4px; color: var(--wa-blue); display: inline-flex; height: 28px; justify-content: center; text-decoration: none; width: 28px; }
.wa-vnext__schedule-history-attempt-link:hover { background: var(--wa-blue-bg); color: var(--wa-blue); text-decoration: none; }
.wa-vnext__schedule-history-attempt-link:focus-visible { border-radius: 2px; outline: 2px solid var(--wa-blue); outline-offset: 2px; }
.wa-vnext__schedule-history-table th:nth-child(1), .wa-vnext__schedule-history-table td:nth-child(1) { width: 27%; }
.wa-vnext__schedule-history-table th:nth-child(2), .wa-vnext__schedule-history-table td:nth-child(2) { width: 14%; }
.wa-vnext__schedule-history-table th:nth-child(3), .wa-vnext__schedule-history-table td:nth-child(3) { width: 19%; }
.wa-vnext__schedule-history-table th:nth-child(4), .wa-vnext__schedule-history-table td:nth-child(4) { width: 29%; }
.wa-vnext__schedule-history-table th:nth-child(5), .wa-vnext__schedule-history-table td:nth-child(5) { width: 11%; }
.wa-vnext__schedule-history-table th:nth-child(5), .wa-vnext__schedule-history-action { text-align: center; }
.wa-vnext__schedule-history-result { display: grid; gap: 6px; min-width: 0; }
.wa-vnext__schedule-history-result .ant-tag { justify-self: start; margin-inline-end: 0; }
.wa-vnext__schedule-history-failure { color: var(--wa-red); font-size: 11px; margin: 0; overflow-wrap: anywhere; }
.wa-vnext__schedule-history-result details { color: var(--wa-muted); }
.wa-vnext__schedule-history-result summary { cursor: pointer; font-size: 11px; font-weight: 650; }
.wa-vnext__schedule-history-result code { background: var(--wa-red-bg); border-radius: 4px; color: #7a271a; display: block; font: 11px/16px ui-monospace, SFMono-Regular, Menlo, monospace; margin-top: 6px; max-height: 96px; overflow: auto; overflow-wrap: anywhere; padding: 7px 8px; white-space: pre-wrap; }
.wa-vnext__schedule-history-completed { white-space: nowrap; }
.wa-vnext__schedule-history-table td.wa-vnext__schedule-history-action { vertical-align: middle; }
.wa-vnext__schedule-empty--history { padding: 14px; }
.wa-vnext__schedule-empty { background: var(--wa-subtle); border: 1px solid var(--wa-line); border-radius: var(--wa-radius); padding: 20px 22px; }
.wa-vnext__schedule-empty-title { font-size: 15px; line-height: 22px; margin: 0 0 6px; }
.wa-vnext__schedule-empty p { color: var(--wa-muted); margin: 0; max-width: 560px; }
.wa-vnext__schedule-form { display: grid; gap: 14px; }
.wa-vnext__schedule-enabled { align-items: center; display: flex; gap: 10px; justify-content: space-between; }
.wa-vnext__schedule-preview-list { margin: 0; padding-left: 18px; }
.wa-vnext__schedule-preview-list li { margin: 3px 0; }
.wa-vnext__schedule-create, .wa-vnext__schedule-review, .wa-vnext__schedule-accepted { display: grid; gap: 18px; min-width: 0; }
.wa-vnext__schedule-context { align-items: center; background: #eff6ff; border: 1px solid #bfdbfe; border-radius: 6px; display: flex; gap: 10px; justify-content: space-between; padding: 12px 14px; }
.wa-vnext__schedule-context strong { color: #172554; font-size: 13px; overflow-wrap: anywhere; text-align: right; }
.wa-vnext__schedule-section { border-bottom: 1px solid var(--wa-line); display: grid; gap: 12px; padding-bottom: 18px; }
.wa-vnext__schedule-section:last-of-type { border-bottom: 0; padding-bottom: 0; }
.wa-vnext__schedule-section h3 { color: var(--wa-ink); font-size: 13px; line-height: 18px; margin: 0; }
.wa-vnext__schedule-repeat-grid { display: grid; gap: 12px; grid-template-columns: minmax(0, 1fr) minmax(120px, .7fr) minmax(0, 1fr); }
.wa-vnext__schedule-cron-grid { display: grid; gap: 12px; grid-template-columns: minmax(0, 2fr) minmax(0, 1fr); }
.wa-vnext__schedule-repeat-grid .ant-select { width: 100%; }
.wa-vnext__schedule-repeat-detail { grid-column: 1 / -1; max-width: 260px; }
.wa-vnext__schedule-cron-toggle { justify-self: start; padding-inline: 0; }
.wa-vnext__schedule-previewing { color: #166534; font-size: 12px; font-weight: 650; }
.wa-vnext__schedule-footer { align-items: center; border-top: 1px solid var(--wa-line); display: flex; gap: 8px; justify-content: flex-end; padding-top: 14px; }
.wa-vnext__schedule-review-panel { background: #f0fdf4; border: 1px solid #bbf7d0; border-radius: 6px; display: grid; gap: 16px; padding: 16px; }
.wa-vnext__schedule-review-panel h2, .wa-vnext__schedule-accepted-panel h2 { color: var(--wa-ink); font-size: 15px; line-height: 21px; margin: 0; }
.wa-vnext__schedule-review-details { display: grid; gap: 12px; grid-template-columns: repeat(2, minmax(0, 1fr)); margin: 0; }
.wa-vnext__schedule-review-details div { display: grid; gap: 3px; min-width: 0; }
.wa-vnext__schedule-review-details dt { color: var(--wa-muted); font-size: 11px; font-weight: 650; }
.wa-vnext__schedule-review-details dd { color: var(--wa-ink); font-size: 12px; margin: 0; overflow-wrap: anywhere; }
.wa-vnext__schedule-fire-preview { border-top: 1px solid #bbf7d0; display: grid; gap: 8px; padding-top: 14px; }
.wa-vnext__schedule-fire-preview strong { color: #166534; font-size: 12px; }
.wa-vnext__schedule-accepted-panel { background: #f0fdf4; border: 1px solid #bbf7d0; border-radius: 6px; display: grid; gap: 8px; padding: 18px; }
.wa-vnext__schedule-accepted-panel p { color: #166534; font-size: 13px; font-weight: 650; margin: 0; }
.wa-vnext__schedule-accepted-panel span { color: #475467; font-size: 12px; line-height: 18px; }
.wa-vnext-schedule-drawer .ant-drawer-content { background: var(--wa-surface); }
.wa-vnext-schedule-drawer .ant-drawer-header { border-bottom-color: var(--wa-line); }
.wa-vnext-schedule-drawer .ant-drawer-title, .wa-vnext-schedule-drawer .ant-drawer-close { color: var(--wa-ink); }
.wa-vnext-schedule-drawer .ant-drawer-body { padding: 20px; }
.wa-vnext-schedule-modal .ant-modal-close { color: var(--wa-muted); }
.wa-vnext-schedule-modal .ant-modal-close:hover { background: var(--wa-subtle); color: var(--wa-ink); }
.wa-vnext-schedule-modal .ant-modal-close:focus-visible { outline: 2px solid color-mix(in srgb, var(--wa-blue) 45%, transparent); outline-offset: 1px; }
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
  .wa-vnext__editor-name.ant-input { font-size: 22px; height: 32px; line-height: 22px; }
  .wa-vnext__header-actions { max-width: 50%; }
  .wa-vnext__content { padding: 16px 16px 40px; }
  .wa-vnext__settings-footer { padding-inline: 16px; }
  .wa-vnext__table-wrap { max-height: min(560px, calc(100dvh - 240px)); }
  .wa-vnext__workflow-actions .ant-btn { min-height: 44px; }
  .wa-vnext__workflow-actions .ant-btn-icon-only { min-width: 44px; width: 44px; }
  .wa-vnext__settings-layout { max-width: none; }
  .wa-vnext__node-inspector { bottom: 12px; left: 12px; max-height: calc(100% - 24px); max-width: none; right: 12px; top: auto; width: auto; }
}
@media (max-width: 600px) {
  .wa-vnext__header { flex-direction: column; }
  .wa-vnext__header-actions { justify-content: flex-start; max-width: 100%; width: 100%; }
  .wa-vnext__toolbar { align-items: stretch; flex-direction: column; }
  .wa-vnext__related-groups { grid-template-columns: 1fr; }
  .wa-vnext__editor-toolbar { align-items: stretch; flex-direction: row; flex-wrap: wrap; overflow: visible; }
  .wa-vnext__editor-toolbar > * { flex: 1 1 100%; }
  .wa-vnext__editor-toolbar-meta { display: grid; gap: 8px; grid-template-columns: max-content minmax(0, 1fr); width: 100%; }
  .wa-vnext__run-workspace { display: grid; grid-template-columns: minmax(0, 1fr); height: auto; min-height: 0; overflow: visible; }
  .wa-vnext__run-workspace > section:first-child, .wa-vnext__run-workspace > .wa-vnext__editor-yaml { height: min(620px, calc(100dvh - 248px)) !important; min-height: 440px !important; }
  .wa-vnext__run-workspace > aside { height: auto !important; max-width: 100%; width: 100% !important; }
  .wa-vnext__panel-resize-handle { display: none; }
  .wa-vnext__editor-mode-control { grid-column: 1 / -1; width: 100%; }
  .wa-vnext__editor-mode-control .ant-segmented-group { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); width: 100%; }
  .wa-vnext__editor-mode-control .ant-segmented-item { min-width: 0; }
  .wa-vnext__editor-mode-control .ant-segmented-item-label { display: flex; justify-content: center; min-width: 0; }
  .wa-vnext__toolbar-search { flex-basis: auto; width: 100%; }
  .wa-vnext__toolbar-filters { display: grid; grid-template-columns: 1fr; width: 100%; }
  .wa-vnext__toolbar-filters .ant-space-item, .wa-vnext__toolbar-filters .ant-picker, .wa-vnext__toolbar-filters .ant-select, .wa-vnext__toolbar-filters .ant-btn { width: 100%; }
  .wa-vnext__activity-footer { align-items: stretch; flex-direction: column; }
  .wa-vnext__activity-footer > .ant-btn { width: 100%; }
  .wa-vnext__pagination-actions { align-items: stretch; flex-direction: column; }
  .wa-vnext__pagination-actions .ant-btn { width: 100%; }
  .wa-vnext__creation-options { grid-template-columns: 1fr; }
  .wa-vnext__creation-option { min-height: 118px; }
  .wa-vnext__creation-form { gap: 18px; }
  .wa-vnext__creation-actions { justify-content: stretch; }
  .wa-vnext__creation-actions .ant-btn { width: 100%; }
  .wa-vnext__settings-panel { padding: 16px; }
  .wa-vnext__settings-nav { gap: 18px; }
  .wa-vnext__settings-field { align-items: stretch; gap: 8px; grid-template-columns: 1fr; min-height: 0; padding: 16px 0; }
  .wa-vnext__settings-field .ant-select { max-width: none; }
  .wa-vnext__settings-savebar { align-items: stretch; flex-direction: column; gap: 10px; }
  .wa-vnext__settings-actions.ant-space { display: grid; gap: 8px !important; grid-template-columns: 1fr 1fr; width: 100%; }
  .wa-vnext__settings-savebar .ant-btn { width: 100%; }
  .wa-vnext__account-profile, .wa-vnext__account-section-heading { align-items: stretch; flex-direction: column; }
  .wa-vnext__account-profile > .ant-btn, .wa-vnext__account-section-heading > .ant-btn { width: 100%; }
  .wa-vnext__state--compact { padding: 18px; }
  .wa-vnext__schedule-toolbar, .wa-vnext__schedule-row { align-items: stretch; flex-direction: column; }
  .wa-vnext__schedule-toolbar .ant-space, .wa-vnext__schedule-row .ant-space { width: 100%; }
  .wa-vnext__schedule-toolbar .ant-btn, .wa-vnext__schedule-row .ant-btn { flex: 1 1 auto; }
  .wa-vnext__template-toolbar { align-items: stretch; flex-direction: column; }
  .wa-vnext__template-search { flex-basis: auto; max-width: none; width: 100%; }
  .wa-vnext__template-sort { align-items: stretch; display: grid; gap: 6px; width: 100%; }
  .wa-vnext__template-sort .ant-select { min-width: 0; width: 100%; }
  .wa-vnext__publish-review-item { gap: 6px; padding-block: 10px; }
}
@media (max-width: 767px) {
  .wa-vnext-schedule-modal .ant-modal-content { padding: 18px; }
  .wa-vnext__schedule-repeat-grid, .wa-vnext__schedule-cron-grid, .wa-vnext__schedule-review-details, .wa-vnext__schedule-detail-facts { grid-template-columns: 1fr; }
  .wa-vnext__schedule-context { align-items: flex-start; flex-direction: column; }
  .wa-vnext__schedule-context strong { text-align: left; }
  .wa-vnext__schedule-history-header { flex-direction: column; }
  .wa-vnext__schedule-history-header a { align-self: flex-start; }
  .wa-vnext__schedule-footer { align-items: stretch; flex-direction: column-reverse; }
  .wa-vnext__schedule-footer .ant-btn { width: 100%; }
}
@media (max-width: 360px) {
  .wa-vnext__settings-actions.ant-space { grid-template-columns: 1fr; }
  .wa-vnext__state--compact .ant-alert { align-items: flex-start; }
  .wa-vnext__state--compact .ant-alert-action { margin-inline-start: 0; margin-top: 8px; }
}
@media (prefers-reduced-motion: reduce) { .wa-vnext *, .wa-vnext *::before, .wa-vnext *::after { animation: none !important; scroll-behavior: auto !important; transition: none !important; } }
`;
