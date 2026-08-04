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
  min-height: calc(100vh - 56px);
}
.wa-vnext * { box-sizing: border-box; letter-spacing: 0; }
.wa-vnext__rail {
  background: var(--wa-sidebar);
  border-right: 1px solid #344054;
  color: #fff;
  display: flex;
  flex-direction: column;
  min-height: calc(100vh - 56px);
  padding: 22px 12px 18px;
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
.wa-vnext__rail-foot { border-top: 1px solid #344054; margin-top: auto; min-width: 0; padding: 14px 12px 0; }
.wa-vnext__rail-foot span { color: #98a2b3; font-size: 12px; }
.wa-vnext__rail-foot strong { display: block; font: 12px ui-monospace, monospace; margin-top: 5px; overflow-wrap: anywhere; }
.wa-vnext__main { background: var(--wa-surface); min-width: 0; }
.wa-vnext__mobile-nav { display: none; }
.wa-vnext__header { align-items: center; border-bottom: 1px solid var(--wa-line); display: flex; gap: 18px; justify-content: space-between; min-height: 84px; padding: 18px 30px; }
.wa-vnext__header h1 { font-size: 22px; line-height: 1.25; margin: 0; }
.wa-vnext__header p { color: var(--wa-muted); font-size: 13px; margin: 5px 0 0; }
.wa-vnext__header-actions { align-items: center; display: flex; flex-wrap: wrap; gap: 8px; justify-content: flex-end; }
.wa-vnext__content { padding: 24px 30px 56px; }
.wa-vnext__toolbar { align-items: end; display: flex; flex-wrap: wrap; gap: 12px; justify-content: space-between; margin-bottom: 18px; }
.wa-vnext__table-wrap { border: 1px solid var(--wa-line); overflow-x: auto; }
.wa-vnext__table { border-collapse: collapse; min-width: 720px; table-layout: fixed; width: 100%; }
.wa-vnext__table th { background: var(--wa-subtle); border-bottom: 1px solid var(--wa-line); color: var(--wa-muted); font: 11px ui-monospace, monospace; height: 42px; padding: 0 12px; text-align: left; text-transform: uppercase; }
.wa-vnext__table td { border-bottom: 1px solid var(--wa-line); height: 72px; padding: 10px 12px; vertical-align: middle; }
.wa-vnext__table tr:last-child td { border-bottom: 0; }
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
.wa-vnext__notice { border: 1px solid #fdb022; background: var(--wa-amber-bg); color: #7a2e0e; margin-bottom: 14px; padding: 11px 13px; }
.wa-vnext__notice--error { background: var(--wa-red-bg); border-color: #fda29b; color: var(--wa-red); }
.wa-vnext__panel { border: 1px solid var(--wa-line); padding: 20px; }
.wa-vnext__form { display: grid; gap: 18px; max-width: 760px; }
.wa-vnext__form-actions { display: flex; flex-wrap: wrap; gap: 8px; }
.wa-vnext__settings-actions--dirty { background: var(--wa-subtle); border: 1px solid var(--wa-line); bottom: 12px; padding: 10px; position: sticky; z-index: 8; }
.wa-vnext__mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; overflow-wrap: anywhere; }
.wa-vnext__split { display: grid; gap: 20px; grid-template-columns: minmax(0, 1fr) minmax(280px, 34%); }
.wa-vnext button:focus-visible, .wa-vnext a:focus-visible, .wa-vnext input:focus-visible, .wa-vnext textarea:focus-visible, .wa-vnext select:focus-visible { outline: 3px solid rgba(23, 92, 211, .25); outline-offset: 2px; }
@media (max-width: 900px) {
  .wa-vnext { grid-template-columns: 1fr; }
  .wa-vnext__rail { display: none; }
  .wa-vnext__mobile-nav { background: var(--wa-sidebar); display: flex; gap: 4px; overflow-x: auto; padding: 8px 10px; }
  .wa-vnext__mobile-nav .wa-vnext__nav-button { flex: 1 0 auto; justify-content: center; min-height: 38px; }
  .wa-vnext__split { grid-template-columns: 1fr; }
}
@media (max-width: 600px) {
  .wa-vnext__header { align-items: flex-start; flex-direction: column; min-height: 0; padding: 16px; }
  .wa-vnext__header-actions { justify-content: flex-start; width: 100%; }
  .wa-vnext__content { padding: 18px 16px 44px; }
  .wa-vnext__toolbar { align-items: stretch; flex-direction: column; }
  .wa-vnext__toolbar .ant-input-affix-wrapper { width: 100% !important; }
}
@media (prefers-reduced-motion: reduce) { .wa-vnext *, .wa-vnext *::before, .wa-vnext *::after { scroll-behavior: auto !important; transition: none !important; } }
`;
