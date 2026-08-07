export const missionWallStyles = `
.mission-wall {
  --wall-bg: #09110f;
  --wall-panel: #101916;
  --wall-panel-strong: #14211d;
  --wall-panel-soft: #16231f;
  --wall-line: rgba(201, 213, 206, 0.16);
  --wall-line-strong: rgba(201, 213, 206, 0.28);
  --wall-text: #f8faf8;
  --wall-muted: #aebbb4;
  --wall-faint: #74847c;
  --wall-live: #2dd4bf;
  --wall-blue: #2563eb;
  --wall-blue-soft: #93c5fd;
  --wall-green: #16a34a;
  --wall-green-soft: #86efac;
  --wall-yellow: #d97706;
  --wall-yellow-soft: #fbbf24;
  --wall-red: #dc2626;
  --wall-red-soft: #f87171;
  --wall-canvas: #0d1714;
  --wall-grid: rgba(201, 213, 206, 0.055);
  background:
    linear-gradient(rgba(255, 255, 255, 0.03) 1px, transparent 1px),
    linear-gradient(90deg, rgba(255, 255, 255, 0.024) 1px, transparent 1px),
    linear-gradient(145deg, #09110f 0%, #111916 58%, #090d0b 100%);
  background-size: 48px 48px, 48px 48px, auto;
  color: var(--wall-text);
  display: grid;
  font-family: "SF Pro Display", "Aptos Display", "Segoe UI", sans-serif;
  grid-template-rows: 98px minmax(0, 1fr);
  height: 100vh;
  letter-spacing: 0;
  min-height: 760px;
  overflow: hidden;
  padding: 18px;
  width: 100%;
}

.mission-wall,
.mission-wall * {
  box-sizing: border-box;
}

.mission-wall-top-strip {
  align-items: center;
  background: rgba(16, 25, 22, 0.94);
  border: 1px solid rgba(45, 212, 191, 0.22);
  border-radius: 8px;
  box-shadow: 0 28px 80px rgba(0, 0, 0, 0.32);
  display: grid;
  gap: 18px;
  grid-template-columns:
    minmax(300px, 1.15fr)
    repeat(5, minmax(104px, 0.48fr))
    minmax(210px, auto);
  min-width: 0;
  padding: 16px 18px;
}

.mission-wall-brand {
  min-width: 0;
}

.mission-wall-brand__kicker {
  color: var(--wall-live);
  font-size: 12px;
  font-weight: 820;
  line-height: 1;
  text-transform: uppercase;
}

.mission-wall-brand__title {
  color: var(--wall-text);
  font-size: 30px;
  font-weight: 780;
  line-height: 1.06;
  margin: 6px 0 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mission-wall-metric {
  border-left: 1px solid rgba(201, 213, 206, 0.16);
  min-width: 0;
  padding-left: 16px;
}

.mission-wall-metric__label {
  color: var(--wall-muted);
  display: block;
  font-size: 12px;
  font-weight: 760;
  line-height: 1.2;
  text-transform: uppercase;
}

.mission-wall-metric__value {
  align-items: center;
  color: var(--wall-text);
  display: flex;
  font-size: 30px;
  font-weight: 780;
  gap: 9px;
  line-height: 1.08;
  margin-top: 7px;
  min-width: 0;
  white-space: nowrap;
}

.mission-wall-brand__title,
.mission-wall-run-card__name,
.mission-wall-run-card__stage,
.mission-wall-stage-title,
.mission-wall-stage-subtitle,
.mission-wall-step-node__name,
.mission-wall-step-node__type,
.mission-wall-step-node__meta span {
  pointer-events: none;
  user-select: none;
}

.mission-wall-metric__value--live {
  color: var(--wall-text);
}

.mission-wall-header-actions {
  align-items: center;
  align-self: center;
  display: inline-flex;
  gap: 10px;
  justify-content: flex-end;
  min-width: 0;
}

.mission-wall-header-actions .console-header-actions__language,
.mission-wall-header-actions .console-header-actions__login {
  background: rgba(45, 212, 191, 0.08);
  border: 1px solid rgba(45, 212, 191, 0.18);
  border-radius: 999px;
  color: var(--wall-text) !important;
  font-weight: 720;
  padding-inline: 13px;
  text-shadow: 0 0 18px rgba(45, 212, 191, 0.18);
}

.mission-wall-header-actions .console-header-actions__language .anticon,
.mission-wall-header-actions .console-header-actions__login .anticon {
  color: var(--wall-live);
}

.mission-wall-header-actions .console-header-actions__language:hover,
.mission-wall-header-actions .console-header-actions__language:focus-visible,
.mission-wall-header-actions .console-header-actions__login:hover,
.mission-wall-header-actions .console-header-actions__login:focus-visible {
  background: rgba(45, 212, 191, 0.14) !important;
  border-color: rgba(45, 212, 191, 0.42) !important;
  color: var(--wall-live) !important;
}

.mission-wall-header-actions .console-header-actions__user {
  background: rgba(45, 212, 191, 0.08) !important;
  border-color: rgba(45, 212, 191, 0.22) !important;
  box-shadow: inset 0 0 0 1px rgba(201, 213, 206, 0.06);
  color: var(--wall-text) !important;
  transition:
    background-color 160ms ease,
    border-color 160ms ease,
    color 160ms ease;
}

.mission-wall-header-actions .console-header-actions__user:hover {
  background: rgba(45, 212, 191, 0.14) !important;
  border-color: rgba(45, 212, 191, 0.42) !important;
}

.mission-wall-header-actions .console-header-actions__user .ant-avatar {
  background: rgba(45, 212, 191, 0.14);
  border: 1px solid rgba(45, 212, 191, 0.34);
}

.mission-wall-header-actions .console-header-actions__user-name {
  color: var(--wall-text) !important;
}

.mission-wall-header-actions .console-header-actions__user-caret {
  color: rgba(201, 213, 206, 0.78) !important;
}

.mission-wall-header-menu {
  --wall-text: #f8faf8;
  --wall-muted: #aebbb4;
  --wall-live: #2dd4bf;
}

.mission-wall-header-menu .ant-dropdown-menu {
  background: rgba(13, 22, 19, 0.98);
  border: 1px solid rgba(45, 212, 191, 0.28);
  border-radius: 8px;
  box-shadow: 0 24px 70px rgba(0, 0, 0, 0.42);
  padding: 6px;
}

.mission-wall-header-menu .ant-dropdown-menu-item,
.mission-wall-header-menu .ant-dropdown-menu-submenu-title {
  border-radius: 6px;
  color: var(--wall-text) !important;
  font-weight: 680;
}

.mission-wall-header-menu .ant-dropdown-menu-item .ant-dropdown-menu-title-content,
.mission-wall-header-menu .ant-dropdown-menu-item .anticon {
  color: inherit !important;
}

.mission-wall-header-menu .ant-dropdown-menu-item-disabled,
.mission-wall-header-menu .ant-dropdown-menu-item-disabled .ant-dropdown-menu-title-content,
.mission-wall-header-menu .ant-dropdown-menu-item-disabled .anticon {
  color: rgba(174, 187, 180, 0.5) !important;
}

.mission-wall-header-menu .ant-dropdown-menu-item-selected,
.mission-wall-header-menu .ant-dropdown-menu-item-active,
.mission-wall-header-menu .ant-dropdown-menu-item:hover {
  background: rgba(45, 212, 191, 0.16) !important;
  color: var(--wall-live) !important;
}

.mission-wall-metric__value--red {
  color: var(--wall-red-soft);
}

.mission-wall-metric__value--yellow {
  color: var(--wall-yellow-soft);
}

.mission-wall-live-dot {
  background: var(--wall-live);
  border-radius: 999px;
  box-shadow: 0 0 18px rgba(45, 212, 191, 0.72);
  display: inline-block;
  flex: 0 0 auto;
  height: 12px;
  width: 12px;
}

.mission-wall-screen {
  display: grid;
  gap: 16px;
  grid-template-columns: 404px minmax(0, 1fr);
  min-height: 0;
  padding-top: 16px;
}

.mission-wall-panel {
  background: rgba(16, 25, 22, 0.92);
  border: 1px solid var(--wall-line);
  border-radius: 8px;
  box-shadow: 0 28px 80px rgba(0, 0, 0, 0.32);
  min-height: 0;
  overflow: hidden;
}

.mission-wall-run-window {
  display: flex;
  flex-direction: column;
}

.mission-wall-panel-head {
  align-items: center;
  border-bottom: 1px solid var(--wall-line);
  display: flex;
  gap: 12px;
  justify-content: space-between;
  min-height: 58px;
  padding: 14px 16px;
}

.mission-wall-panel-title {
  color: var(--wall-text);
  font-size: 14px;
  font-weight: 820;
  line-height: 1;
  text-transform: uppercase;
}

.mission-wall-panel-count {
  align-items: center;
  border: 1px solid rgba(201, 213, 206, 0.24);
  border-radius: 999px;
  color: var(--wall-muted);
  display: flex;
  font-size: 12px;
  font-weight: 820;
  height: 26px;
  justify-content: center;
  min-width: 34px;
  padding: 0 9px;
}

.mission-wall-run-window__viewport {
  flex: 1;
  min-height: 0;
  overflow-x: hidden;
  overflow-y: auto;
  position: relative;
  scrollbar-color: rgba(174, 187, 180, 0.44) transparent;
  scrollbar-gutter: stable;
  scrollbar-width: thin;
}

.mission-wall-run-window__viewport::-webkit-scrollbar {
  width: 8px;
}

.mission-wall-run-window__viewport::-webkit-scrollbar-thumb {
  background: rgba(174, 187, 180, 0.36);
  border-radius: 999px;
}

.mission-wall-run-window__viewport::-webkit-scrollbar-track {
  background: transparent;
}

.mission-wall-run-window__viewport::after {
  background: linear-gradient(180deg, transparent, rgba(9, 17, 15, 0.84));
  bottom: 0;
  content: "";
  height: 42px;
  left: 0;
  pointer-events: none;
  position: absolute;
  right: 0;
  z-index: 2;
}

.mission-wall-run-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 14px;
}

.mission-wall-run-card {
  appearance: none;
  background: linear-gradient(180deg, rgba(22, 35, 31, 0.98), rgba(13, 22, 19, 0.98));
  --mission-wall-card-focus-ring: rgba(116, 132, 124, 0.62);
  border: 1px solid var(--wall-line);
  border-left: 4px solid var(--wall-faint);
  border-radius: 8px;
  color: inherit;
  cursor: pointer;
  display: block;
  font: inherit;
  min-height: 126px;
  padding: 14px;
  text-align: left;
  transition: border-color 140ms ease, transform 140ms ease;
  width: 100%;
}

.mission-wall-run-card--focus {
  outline: 2px solid var(--mission-wall-card-focus-ring);
  outline-offset: 3px;
}

.mission-wall-run-card:focus-visible {
  outline: 2px solid var(--mission-wall-card-focus-ring);
  outline-offset: 3px;
}

.mission-wall-run-card:hover {
  transform: translateY(-1px);
}

.mission-wall-tone--blue {
  --mission-wall-card-focus-ring: rgba(147, 197, 253, 0.9);
  border-left-color: var(--wall-blue);
}

.mission-wall-tone--green {
  --mission-wall-card-focus-ring: rgba(134, 239, 172, 0.88);
  border-left-color: var(--wall-green);
}

.mission-wall-tone--grey {
  --mission-wall-card-focus-ring: rgba(174, 187, 180, 0.7);
  border-left-color: var(--wall-faint);
}

.mission-wall-tone--red {
  --mission-wall-card-focus-ring: rgba(248, 113, 113, 0.88);
  border-left-color: var(--wall-red);
}

.mission-wall-tone--teal {
  --mission-wall-card-focus-ring: rgba(45, 212, 191, 0.86);
  border-left-color: var(--wall-live);
}

.mission-wall-tone--yellow {
  --mission-wall-card-focus-ring: rgba(251, 191, 36, 0.88);
  border-left-color: var(--wall-yellow);
}

.mission-wall-row {
  align-items: center;
  display: flex;
  gap: 10px;
  justify-content: space-between;
  min-width: 0;
}

.mission-wall-run-card__name {
  color: var(--wall-text);
  font-size: 20px;
  font-weight: 760;
  line-height: 1.15;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mission-wall-pill {
  border: 1px solid currentColor;
  border-radius: 999px;
  color: var(--wall-muted);
  flex: 0 0 auto;
  font-size: 11px;
  font-weight: 820;
  line-height: 1;
  padding: 6px 9px;
  text-transform: uppercase;
  white-space: nowrap;
}

.mission-wall-pill--blue {
  color: var(--wall-blue-soft);
}

.mission-wall-pill--green {
  color: var(--wall-green-soft);
}

.mission-wall-pill--grey {
  color: var(--wall-muted);
}

.mission-wall-pill--red {
  color: var(--wall-red-soft);
}

.mission-wall-pill--teal {
  color: var(--wall-live);
}

.mission-wall-pill--yellow {
  color: var(--wall-yellow-soft);
}

.mission-wall-run-card__stage {
  color: var(--wall-muted);
  font-size: 14px;
  line-height: 1.35;
  margin-top: 14px;
  min-height: 19px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mission-wall-run-card__progress-row {
  align-items: center;
  display: flex;
  gap: 12px;
  justify-content: space-between;
  margin-top: 14px;
}

.mission-wall-run-card__progress-row--single {
  justify-content: flex-start;
}

.mission-wall-run-card__progress-label,
.mission-wall-run-card__duration {
  color: var(--wall-muted);
  font-size: 14px;
  font-weight: 760;
  line-height: 1;
  white-space: nowrap;
}

.mission-wall-progress {
  background: rgba(201, 213, 206, 0.14);
  border-radius: 999px;
  height: 8px;
  margin-top: 12px;
  overflow: hidden;
  width: 100%;
}

.mission-wall-progress__bar {
  background: var(--wall-live);
  border-radius: inherit;
  height: 100%;
  min-width: 4px;
}

.mission-wall-progress__bar--blue {
  background: linear-gradient(90deg, #2563eb, #2dd4bf);
}

.mission-wall-progress__bar--green {
  background: linear-gradient(90deg, #16a34a, #86efac);
}

.mission-wall-progress__bar--grey {
  background: linear-gradient(90deg, #74847c, #aebbb4);
}

.mission-wall-progress__bar--red {
  background: linear-gradient(90deg, #dc2626, #f87171);
}

.mission-wall-progress__bar--yellow {
  background: linear-gradient(90deg, #d97706, #fbbf24);
}

.mission-wall-stage {
  display: grid;
  grid-template-rows: 78px minmax(0, 1fr);
}

.mission-wall-stage-head {
  align-items: center;
  border-bottom: 1px solid var(--wall-line);
  display: block;
  min-width: 0;
  padding: 12px 18px;
}

.mission-wall-stage-title {
  color: var(--wall-text);
  font-size: 22px;
  font-weight: 780;
  line-height: 1.15;
  margin: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mission-wall-stage-subtitle {
  color: var(--wall-muted);
  font-size: 13px;
  font-weight: 680;
  line-height: 1.25;
  margin-top: 6px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mission-wall-canvas {
  background:
    linear-gradient(var(--wall-grid) 1px, transparent 1px),
    linear-gradient(90deg, var(--wall-grid) 1px, transparent 1px),
    radial-gradient(circle at 50% 44%, rgba(45, 212, 191, 0.08), transparent 36%),
    var(--wall-canvas);
  background-size: 34px 34px, 34px 34px, auto, auto;
  display: grid;
  grid-template-rows: minmax(0, 1fr);
  min-height: 0;
  overflow: hidden;
}

.mission-wall-state-panel {
  align-content: center;
  background:
    linear-gradient(var(--wall-grid) 1px, transparent 1px),
    linear-gradient(90deg, var(--wall-grid) 1px, transparent 1px),
    var(--wall-canvas);
  background-size: 34px 34px, 34px 34px, auto;
  display: grid;
  gap: 12px;
  min-height: 0;
  padding: 56px;
}

.mission-wall-stage-skeleton {
  background:
    linear-gradient(var(--wall-grid) 1px, transparent 1px),
    linear-gradient(90deg, var(--wall-grid) 1px, transparent 1px),
    var(--wall-canvas);
  background-size: 34px 34px, 34px 34px, auto;
  min-height: 0;
  overflow: hidden;
  padding: 18px;
}

.mission-wall-stage-skeleton > div[aria-hidden="true"],
.mission-wall-stage-skeleton .aevatar-content-skeleton-canvas {
  height: 100%;
  min-height: 0;
}

.mission-wall-stage-skeleton .aevatar-content-skeleton-canvas-surface {
  background: var(--wall-panel-soft) !important;
  border-color: var(--wall-line) !important;
  flex: 1;
  min-height: 0 !important;
}

.mission-wall-stage-skeleton .aevatar-content-skeleton-node {
  background: var(--wall-panel-strong) !important;
  border-color: var(--wall-line-strong) !important;
}

.mission-wall-stage-skeleton .aevatar-content-skeleton-connector,
.mission-wall-stage-skeleton .ant-skeleton-button,
.mission-wall-stage-skeleton .ant-skeleton-input {
  background: var(--wall-line-strong) !important;
}

.mission-wall-state-panel__kicker {
  color: var(--wall-live);
  font-size: 12px;
  font-weight: 820;
  letter-spacing: 0;
  line-height: 1;
  text-transform: uppercase;
}

.mission-wall-state-panel__title {
  color: var(--wall-text);
  font-size: 34px;
  font-weight: 780;
  line-height: 1.12;
  max-width: 760px;
}

.mission-wall-graph {
  min-height: 0;
  overflow: hidden;
  position: relative;
}

.mission-wall-react-flow {
  background: transparent;
  height: 100%;
  width: 100%;
}

.mission-wall-react-flow .react-flow__pane {
  cursor: grab;
}

.mission-wall-react-flow .react-flow__pane:active {
  cursor: grabbing;
}

.mission-wall-react-flow .react-flow__edge-path {
  stroke-linecap: round;
  filter: drop-shadow(0 0 8px rgba(45, 212, 191, 0.12));
}

.mission-wall-react-flow .mission-wall-flow-edge--focused .react-flow__edge-path {
  animation: mission-wall-flow-drift 1.8s linear infinite;
  stroke-dasharray: 14 10;
}

@keyframes mission-wall-flow-drift {
  from {
    stroke-dashoffset: 0;
  }

  to {
    stroke-dashoffset: -24;
  }
}

.mission-wall-react-flow .react-flow__controls {
  background: rgba(16, 25, 22, 0.88);
  border: 1px solid rgba(201, 213, 206, 0.18);
  border-radius: 8px;
  box-shadow: none;
  overflow: hidden;
}

.mission-wall-react-flow .react-flow__controls-button {
  background: rgba(16, 25, 22, 0.96);
  border-bottom: 1px solid rgba(201, 213, 206, 0.12);
  color: var(--wall-muted);
}

.mission-wall-react-flow .react-flow__controls-button svg {
  fill: var(--wall-muted);
}

.mission-wall-step-node {
  background: linear-gradient(180deg, rgba(20, 33, 29, 0.98), rgba(15, 25, 22, 0.98));
  border: 1px solid var(--wall-line-strong);
  border-radius: 14px;
  box-shadow: 0 18px 46px rgba(0, 0, 0, 0.34);
  display: grid;
  gap: 10px;
  min-height: 112px;
  padding: 14px;
  width: 260px;
}

.mission-wall-step-node--focused {
  border-color: rgba(45, 212, 191, 0.78);
  box-shadow: 0 0 0 1px rgba(45, 212, 191, 0.36), 0 24px 58px rgba(0, 0, 0, 0.42);
}

.mission-wall-step-node--active {
  border-color: rgba(45, 212, 191, 0.78);
  box-shadow: 0 0 0 1px rgba(45, 212, 191, 0.32), 0 0 26px rgba(45, 212, 191, 0.18), 0 24px 58px rgba(0, 0, 0, 0.42);
}

.mission-wall-step-node--waiting {
  border-color: rgba(217, 119, 6, 0.82);
  box-shadow: 0 0 0 1px rgba(217, 119, 6, 0.28), 0 24px 58px rgba(0, 0, 0, 0.44);
}

.mission-wall-step-node--failed {
  border-color: rgba(220, 38, 38, 0.86);
  box-shadow: 0 0 0 1px rgba(220, 38, 38, 0.34), 0 24px 58px rgba(0, 0, 0, 0.44);
}

.mission-wall-step-node__top {
  align-items: center;
  display: grid;
  gap: 10px;
  grid-template-columns: auto minmax(0, 1fr) auto;
  min-width: 0;
}

.mission-wall-step-node__identity {
  min-width: 0;
}

.mission-wall-step-node__icon {
  align-items: center;
  background: rgba(45, 212, 191, 0.12);
  border-radius: 999px;
  color: var(--wall-live);
  display: flex;
  font-size: 12px;
  font-weight: 820;
  height: 32px;
  justify-content: center;
  width: 32px;
}

.mission-wall-step-node--failed .mission-wall-step-node__icon {
  background: rgba(220, 38, 38, 0.16);
  color: var(--wall-red-soft);
}

.mission-wall-step-node__name {
  color: var(--wall-text);
  font-size: 15px;
  font-weight: 780;
  line-height: 1.2;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mission-wall-step-node__type {
  color: var(--wall-muted);
  font-size: 12px;
  line-height: 1.25;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mission-wall-step-node__meta {
  align-items: center;
  color: var(--wall-muted);
  display: flex;
  font-size: 12px;
  font-weight: 700;
  gap: 10px;
  justify-content: space-between;
  line-height: 1.2;
  min-width: 0;
}

.mission-wall-step-node__meta span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mission-wall-step-node__handle {
  background: var(--wall-live);
  border: 2px solid rgba(9, 17, 15, 0.96);
  height: 12px;
  width: 12px;
}

.mission-wall-step-node__handle--target {
  left: -6px;
}

.mission-wall-step-node__handle--source {
  right: -6px;
}

@media (max-width: 1500px) {
  .mission-wall {
    min-height: 700px;
    padding: 14px;
  }

  .mission-wall-top-strip {
    grid-template-columns:
      minmax(260px, 0.95fr)
      repeat(5, minmax(82px, 0.42fr))
      minmax(190px, auto);
  }

  .mission-wall-screen {
    grid-template-columns: 340px minmax(0, 1fr);
  }

  .mission-wall-brand__title,
  .mission-wall-metric__value {
    font-size: 24px;
  }

  .mission-wall-stage-title {
    font-size: 19px;
  }

  .mission-wall-step-node {
    min-height: 148px;
    width: 310px;
  }
}
`;
