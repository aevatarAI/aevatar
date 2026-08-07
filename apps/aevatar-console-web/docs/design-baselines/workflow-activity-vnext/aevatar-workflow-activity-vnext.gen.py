#!/usr/bin/env python3
"""Generate the Workflow + Activity + Settings vNext Excalidraw board.

The board implements the product decisions from the 2026-08-03 meeting:
- Workflow is the only user-authored resource in scope.
- Run is the only execution action.
- Every Run is retained in Activity, including draft revisions.
- Retry and Run again create linked records instead of rewriting history.
- Settings follows the current Aevatar Console LLM and Account behavior.
- The application navigation contains Workflows, Activity, and Settings.
"""

from __future__ import annotations

import json
import random
import re
from pathlib import Path


random.seed(20260803)

OUT = Path(__file__).with_suffix("").with_suffix(".excalidraw")

INK = "#17202a"
MUTED = "#667085"
FAINT = "#98a2b3"
LINE = "#d0d5dd"
SURFACE = "#ffffff"
SUBTLE = "#f8fafc"
SIDEBAR = "#101828"
BLUE = "#175cd3"
BLUE_BG = "#eff8ff"
GREEN = "#067647"
GREEN_BG = "#ecfdf3"
AMBER = "#b54708"
AMBER_BG = "#fffaeb"
RED = "#b42318"
RED_BG = "#fef3f2"
PURPLE = "#6941c6"
PURPLE_BG = "#f4f3ff"

FONT_SANS = 1
FONT_MONO = 3
FS_SMALL = 14
FS_BODY = 16
FS_HEAD = 22
FS_TITLE = 32

FRAME_W = 1440
FRAME_H = 920
GAP_X = 320
GAP_Y = 260
COLS = 3
ORIGIN_X = 160
ORIGIN_Y = 300

elements: list[dict] = []
current_frame: str | None = None
current_group: str | None = None
sequence = 0


def eid(prefix: str = "e") -> str:
    global sequence
    sequence += 1
    return f"{prefix}-{sequence:05d}-{random.randint(1000, 9999)}"


def base(kind: str, x: float, y: float, w: float, h: float, **kwargs) -> dict:
    item = {
        "id": kwargs.pop("id", eid(kind[:1])),
        "type": kind,
        "x": float(x),
        "y": float(y),
        "width": float(w),
        "height": float(h),
        "angle": 0,
        "strokeColor": kwargs.pop("stroke", INK),
        "backgroundColor": kwargs.pop("bg", "transparent"),
        "fillStyle": "solid",
        "strokeWidth": kwargs.pop("sw", 1),
        "strokeStyle": kwargs.pop("style", "solid"),
        "roughness": 0,
        "opacity": 100,
        "groupIds": [current_group] if current_group else [],
        "frameId": kwargs.pop("frameId", current_frame),
        "roundness": kwargs.pop("roundness", None),
        "seed": random.randint(1, 2**31 - 1),
        "version": 1,
        "versionNonce": random.randint(1, 2**31 - 1),
        "isDeleted": False,
        "boundElements": None,
        "updated": 1,
        "link": None,
        "locked": False,
    }
    item.update(kwargs)
    return item


def rect(x: float, y: float, w: float, h: float, *, bg: str = "transparent",
         stroke: str = LINE, radius: bool = True, style: str = "solid", sw: int = 1) -> dict:
    item = base("rectangle", x, y, w, h, bg=bg, stroke=stroke, style=style, sw=sw)
    if radius:
        item["roundness"] = {"type": 3}
    elements.append(item)
    return item


def text(x: float, y: float, value: str, size: int = FS_BODY, color: str = INK,
         *, font: int = FONT_SANS, width: float | None = None,
         align: str = "left", line_height: float = 1.25) -> dict:
    lines = value.split("\n")
    if width is None:
        width = max(18, max(len(line) for line in lines) * size * 0.57)
    height = len(lines) * size * line_height
    item = base("text", x, y, width, height, stroke=color)
    item.update({
        "text": value,
        "originalText": value,
        "fontSize": size,
        "fontFamily": font,
        "textAlign": align,
        "verticalAlign": "top",
        "containerId": None,
        "autoResize": False,
        "lineHeight": line_height,
    })
    elements.append(item)
    return item


def line(x: float, y: float, w: float, h: float, *, color: str = LINE,
         style: str = "solid", arrowhead: str | None = None, sw: int = 1) -> dict:
    kind = "arrow" if arrowhead else "line"
    item = base(kind, x, y, abs(w), abs(h), stroke=color, style=style, sw=sw)
    item.update({
        "points": [[0, 0], [float(w), float(h)]],
        "lastCommittedPoint": None,
        "startBinding": None,
        "endBinding": None,
        "startArrowhead": None,
        "endArrowhead": arrowhead,
    })
    if arrowhead:
        item["elbowed"] = False
    elements.append(item)
    return item


def dot(x: float, y: float, diameter: float, color: str, *, bg: str | None = None) -> dict:
    item = base("ellipse", x, y, diameter, diameter, stroke=color, bg=bg or color)
    elements.append(item)
    return item


def button(x: float, y: float, w: float, label: str, *, primary: bool = False,
           color: str = INK, bg: str = SURFACE, h: float = 38) -> None:
    stroke = color
    fill = color if primary else bg
    rect(x, y, w, h, bg=fill, stroke=stroke)
    text(x + 8, y + 9, label, FS_SMALL, SURFACE if primary else color,
         width=w - 16, align="center")


def chip(x: float, y: float, label: str, *, color: str = MUTED,
         bg: str = SURFACE, selected: bool = False) -> float:
    w = max(62, len(label) * 8.2 + 24)
    rect(x, y, w, 30, bg=(BLUE_BG if selected else bg),
         stroke=(BLUE if selected else LINE))
    text(x + 10, y + 7, label, FS_SMALL, BLUE if selected else color, width=w - 20)
    return x + w + 8


def badge(x: float, y: float, label: str, status: str) -> float:
    palette = {
        "ok": (GREEN, GREEN_BG),
        "run": (BLUE, BLUE_BG),
        "wait": (AMBER, AMBER_BG),
        "fail": (RED, RED_BG),
        "info": (PURPLE, PURPLE_BG),
        "muted": (MUTED, SUBTLE),
    }
    color, bg = palette[status]
    w = max(68, len(label) * 7.8 + 24)
    rect(x, y, w, 28, bg=bg, stroke=color)
    text(x + 10, y + 6, label, FS_SMALL, color, width=w - 20)
    return x + w + 8


def field(x: float, y: float, w: float, label: str, value: str,
          *, mono: bool = False, h: float = 42) -> None:
    text(x, y, label.upper(), FS_SMALL, MUTED, font=FONT_MONO)
    rect(x, y + 22, w, h, bg=SURFACE, stroke=LINE)
    text(x + 12, y + 33, value, FS_SMALL, INK,
         font=FONT_MONO if mono else FONT_SANS, width=w - 24)


def frame_position(index: int) -> tuple[int, int]:
    return (
        ORIGIN_X + (index % COLS) * (FRAME_W + GAP_X),
        ORIGIN_Y + (index // COLS) * (FRAME_H + GAP_Y),
    )


def begin_frame(index: int, name: str) -> tuple[int, int]:
    global current_frame, current_group
    x, y = frame_position(index)
    fid = eid("frame")
    frame_item = base("frame", x, y, FRAME_W, FRAME_H, id=fid,
                      stroke="#344054", bg="transparent", frameId=None)
    frame_item["name"] = name
    elements.append(frame_item)
    current_frame = fid
    current_group = f"group-{index:02d}"
    rect(x, y, FRAME_W, FRAME_H, bg=SURFACE, stroke="#344054", radius=False, sw=2)
    return x, y


def end_frame() -> None:
    global current_frame, current_group
    current_frame = None
    current_group = None


def app_shell(fx: float, fy: float, active: str, *, title: str,
              subtitle: str = "") -> tuple[float, float, float]:
    sidebar_w = 210
    top_h = 82
    rect(fx, fy, sidebar_w, FRAME_H, bg=SIDEBAR, stroke=SIDEBAR, radius=False)
    text(fx + 24, fy + 24, "AEVATAR", FS_HEAD, SURFACE, width=160)
    text(fx + 24, fy + 53, "AUTOMATION", FS_SMALL, "#98a2b3", font=FONT_MONO, width=160)
    nav_y = fy + 116
    for idx, item in enumerate(("Workflows", "Activity", "Settings")):
        row_y = nav_y + idx * 52
        if item == active:
            rect(fx + 12, row_y - 8, sidebar_w - 24, 42, bg="#344054", stroke="#344054")
        dot(fx + 28, row_y + 4, 10, BLUE if item == active else FAINT)
        text(fx + 48, row_y, item, FS_BODY, SURFACE if item == active else "#d0d5dd", width=130)
    text(fx + 24, fy + FRAME_H - 74, "Workspace", FS_SMALL, "#98a2b3", width=150)
    text(fx + 24, fy + FRAME_H - 48, "Acme Operations", FS_SMALL, SURFACE, width=160)

    content_x = fx + sidebar_w
    content_w = FRAME_W - sidebar_w
    rect(content_x, fy, content_w, top_h, bg=SURFACE, stroke=LINE, radius=False)
    text(content_x + 30, fy + 18, title, FS_HEAD, INK, width=620)
    if subtitle:
        text(content_x + 30, fy + 49, subtitle, FS_SMALL, MUTED, width=700)
    return content_x, fy + top_h, content_w


def table_header(x: float, y: float, widths: list[float], labels: list[str]) -> None:
    rect(x, y, sum(widths), 42, bg=SUBTLE, stroke=LINE, radius=False)
    cursor = x
    for width, label in zip(widths, labels):
        text(cursor + 14, y + 13, label.upper(), FS_SMALL, MUTED,
             font=FONT_MONO, width=width - 28)
        cursor += width


def workflow_row(x: float, y: float, name: str, description: str, revision: str,
                 last_run: str, run_status: str, state: str) -> None:
    widths = [410, 170, 220, 150, 190]
    rect(x, y, sum(widths), 82, bg=SURFACE, stroke=LINE, radius=False)
    text(x + 16, y + 15, name, FS_BODY, INK, width=360)
    text(x + 16, y + 43, description, FS_SMALL, MUTED, width=375)
    cursor = x + widths[0]
    text(cursor + 14, y + 28, revision, FS_SMALL, MUTED, font=FONT_MONO, width=140)
    cursor += widths[1]
    status_label = {"ok": "Succeeded", "run": "Running", "fail": "Failed", "muted": "Never run"}[run_status]
    badge(cursor + 14, y + 26, status_label, run_status)
    text(cursor + 115, y + 31, last_run, FS_SMALL, MUTED, width=95)
    cursor += widths[2]
    badge(cursor + 14, y + 26, state, "ok" if state == "Published" else "muted")
    cursor += widths[3]
    button(cursor + 12, y + 22, 72, "Run")
    button(cursor + 94, y + 22, 76, "Open", color=BLUE, bg=BLUE_BG)


def activity_row(x: float, y: float, status: str, name: str, origin: str,
                 revision: str, when: str, took: str, usage: str, outcome: str) -> None:
    widths = [105, 235, 120, 175, 115, 75, 90, 255]
    rect(x, y, sum(widths), 70, bg=SURFACE, stroke=LINE, radius=False)
    palette_key = {"Succeeded": "ok", "Running": "run", "Needs you": "wait", "Failed": "fail"}[status]
    cursor = x
    badge(cursor + 12, y + 21, status, palette_key)
    cursor += widths[0]
    text(cursor + 12, y + 14, name, FS_SMALL, INK, width=210)
    text(cursor + 12, y + 39, origin, FS_SMALL, MUTED, width=210)
    cursor += widths[1]
    text(cursor + 12, y + 25, origin.split(" · ")[0], FS_SMALL, MUTED, width=96)
    cursor += widths[2]
    text(cursor + 12, y + 15, revision.split(" · ")[0], FS_SMALL, INK, width=150)
    text(cursor + 12, y + 40, " · ".join(revision.split(" · ")[1:]), FS_SMALL, MUTED, font=FONT_MONO, width=150)
    cursor += widths[3]
    text(cursor + 12, y + 25, when, FS_SMALL, MUTED, width=92)
    cursor += widths[4]
    text(cursor + 8, y + 25, took, FS_SMALL, MUTED, font=FONT_MONO, width=60)
    cursor += widths[5]
    text(cursor + 8, y + 25, usage, FS_SMALL, MUTED, font=FONT_MONO, width=72)
    cursor += widths[6]
    text(cursor + 12, y + 15, outcome, FS_SMALL, INK, width=228)
    text(cursor + 12, y + 40, "Open details", FS_SMALL, BLUE, width=228)


def node_appearance(kind: str) -> tuple[str, str]:
    normalized = kind.casefold()
    if "ai" in normalized:
        return BLUE, "AI"
    if "schedule" in normalized:
        return PURPLE, "T"
    if "condition" in normalized:
        return "#9333ea", "C"
    if "approval" in normalized:
        return AMBER, "H"
    if "validation" in normalized:
        return RED, "V"
    if any(token in normalized for token in ("data", "query", "xero", "warehouse")):
        return "#0891b2", "D"
    if any(token in normalized for token in ("message", "lark", "gmail")):
        return "#16a34a", "I"
    return MUTED, "{}"


def studio_node(x: float, y: float, name: str, kind: str, summary: str,
                *, selected: bool = False, status: str | None = None) -> None:
    color, icon = node_appearance(kind)
    node_w = 214
    node_h = 126
    rect(x, y, node_w, node_h, bg=SURFACE,
         stroke=color if selected else "#dbe3ee", sw=2 if selected else 1)
    rect(x, y, 4, node_h, bg=color, stroke=color, radius=False)
    dot(x - 6, y + 54, 12, color, bg=color)
    dot(x + node_w - 6, y + 54, 12, color, bg=color)
    rect(x + 14, y + 14, 34, 34, bg=BLUE_BG if color == BLUE else SUBTLE,
         stroke=color)
    text(x + 14, y + 23, icon, FS_SMALL, color, width=34, align="center")
    text(x + 60, y + 13, name, FS_SMALL, INK, width=138)
    text(x + 60, y + 39, kind, 12, MUTED, width=138)
    line(x + 12, y + 62, node_w - 24, 0, color="#edf2f7")
    text(x + 14, y + 76, summary, 12, MUTED, width=node_w - 28)
    if status:
        status_palette = "ok" if status == "Completed" else "run" if status == "Running" else "muted"
        badge(x + 124, y + 88, status, status_palette)


def studio_canvas(cx: float, cy: float, cw: float, ch: float,
                  steps: list[tuple[str, str]], *, selected: int | None = None,
                  fields: list[tuple[str, str]] | None = None,
                  empty: bool = False, source: str = "",
                  statuses: list[str] | None = None) -> None:
    canvas_x = cx + 10
    canvas_y = cy + 10
    canvas_w = cw - 20
    canvas_h = ch - 20
    rect(canvas_x, canvas_y, canvas_w, canvas_h, bg="#f7f9fc", stroke="#d8e0ea")
    for grid_x in range(80, int(canvas_w), 120):
        line(canvas_x + grid_x, canvas_y, 0, canvas_h, color="#edf2f7")
    for grid_y in range(80, int(canvas_h), 120):
        line(canvas_x, canvas_y + grid_y, canvas_w, 0, color="#edf2f7")
    badge(canvas_x + 16, canvas_y + 16, f"Created from {source}", "info")

    if empty:
        center_x = canvas_x + canvas_w / 2
        center_y = canvas_y + canvas_h / 2
        rect(center_x - 56, center_y - 86, 112, 112,
             bg=SURFACE, stroke=FAINT, style="dashed")
        text(center_x - 56, center_y - 58, "+", FS_TITLE, MUTED,
             width=112, align="center")
        text(center_x - 150, center_y + 44, "Add first step", FS_HEAD, INK,
             width=300, align="center")
        text(center_x - 220, center_y + 82,
             "Start this workflow by adding the first step.",
             FS_SMALL, MUTED, width=440, align="center")
        button(canvas_x + 16, canvas_y + canvas_h - 128, 34, "+", color=MUTED, h=32)
        button(canvas_x + 16, canvas_y + canvas_h - 92, 34, "-", color=MUTED, h=32)
        button(canvas_x + 16, canvas_y + canvas_h - 56, 34, "Fit", color=MUTED, h=32)
        return

    inspector_open = selected is not None and 0 <= selected < len(steps)
    graph_w = canvas_w - (400 if inspector_open else 0)
    node_w = 214
    positions: list[tuple[float, float]] = []
    if len(steps) <= 5 and not inspector_open:
        gap = min(48, (graph_w - 56 - len(steps) * node_w) / max(1, len(steps) - 1))
        total_w = len(steps) * node_w + max(0, len(steps) - 1) * gap
        start_x = canvas_x + (graph_w - total_w) / 2
        positions = [(start_x + idx * (node_w + gap), canvas_y + 260)
                     for idx in range(len(steps))]
    else:
        positions = []
        for idx in range(len(steps)):
            if idx < 3:
                positions.append((canvas_x + 44 + idx * 248, canvas_y + 112))
            else:
                positions.append((canvas_x + 540, canvas_y + 112 + (idx - 2) * 236))

    for idx in range(len(positions) - 1):
        sx, sy = positions[idx]
        tx, ty = positions[idx + 1]
        line(sx + node_w, sy + 60, tx - (sx + node_w), ty - sy,
             color="#94a3b8", arrowhead="arrow", sw=2)

    for idx, ((name, kind), (nx, ny)) in enumerate(zip(steps, positions)):
        summary = fields[0][1] if idx == selected and fields else "Configuration ready"
        studio_node(nx, ny, name, kind, summary, selected=idx == selected,
                    status=statuses[idx] if statuses and idx < len(statuses) else None)

    button(canvas_x + 16, canvas_y + canvas_h - 128, 34, "+", color=MUTED, h=32)
    button(canvas_x + 16, canvas_y + canvas_h - 92, 34, "-", color=MUTED, h=32)
    button(canvas_x + 16, canvas_y + canvas_h - 56, 34, "Fit", color=MUTED, h=32)
    minimap_x = canvas_x + graph_w - 154
    minimap_y = canvas_y + canvas_h - 104
    rect(minimap_x, minimap_y, 138, 86, bg=SURFACE, stroke="#d8e0ea")
    for idx, _ in enumerate(steps[:6]):
        rect(minimap_x + 10 + (idx % 3) * 40, minimap_y + 16 + (idx // 3) * 28,
             28, 14, bg=SURFACE, stroke=BLUE)

    if not inspector_open:
        return
    selected_name, selected_kind = steps[selected]
    panel_x = canvas_x + canvas_w - 390
    panel_w = 390
    rect(panel_x, canvas_y, panel_w, canvas_h, bg=SURFACE, stroke="#e5e7eb")
    text(panel_x + 20, canvas_y + 18, "Node configuration", FS_BODY, INK, width=260)
    text(panel_x + 20, canvas_y + 48, selected_name, FS_SMALL, MUTED, width=300)
    text(panel_x + 344, canvas_y + 17, "x", FS_HEAD, MUTED, width=24, align="center")
    line(panel_x, canvas_y + 76, panel_w, 0, color="#eef2f7")
    field(panel_x + 20, canvas_y + 100, panel_w - 40, "Node name", selected_name)
    field(panel_x + 20, canvas_y + 188, panel_w - 40, "Type", selected_kind)
    for field_index, (label, value) in enumerate(fields or []):
        field(panel_x + 20, canvas_y + 276 + field_index * 88,
              panel_w - 40, label, value, h=58 if len(value) > 64 else 42)
    line(panel_x, canvas_y + canvas_h - 62, panel_w, 0, color="#eef2f7")
    button(panel_x + 194, canvas_y + canvas_h - 48, 78, "Delete", color=RED, h=34)
    button(panel_x + 282, canvas_y + canvas_h - 48, 88, "Apply",
           primary=True, color=BLUE, h=34)


def workflow_editor_frame(index: int, frame_name: str, *, title: str,
                          subtitle: str, source: str,
                          steps: list[tuple[str, str]], selected: int | None = None,
                          fields: list[tuple[str, str]] | None = None,
                          empty: bool = False) -> None:
    fx, fy = begin_frame(index, frame_name)
    cx, cy, cw = app_shell(fx, fy, "Workflows", title=title, subtitle=subtitle)
    button(cx + cw - 510, fy + 24, 72, "Run", color=MUTED if empty else INK, h=32)
    button(cx + cw - 428, fy + 24, 92, "Add node", color=INK, h=32)
    button(cx + cw - 326, fy + 24, 92, "Edit YAML", color=INK, h=32)
    button(cx + cw - 224, fy + 24, 82, "Save", primary=True, color=BLUE, h=32)
    button(cx + cw - 132, fy + 24, 102, "Publish", color=MUTED if empty else INK, h=32)
    studio_canvas(cx, cy, cw, FRAME_H - (cy - fy), steps,
                  selected=selected, fields=fields, empty=empty, source=source)
    end_frame()


def settings_nav(x: float, y: float, active: str, *, mobile: bool = False) -> None:
    labels = ("AI defaults", "Account", "Advanced")
    if mobile:
        tab_w = 108
        for idx, label in enumerate(labels):
            tab_x = x + idx * tab_w
            selected = label == active
            if selected:
                rect(tab_x, y, tab_w, 42, bg=BLUE_BG, stroke=BLUE_BG)
            text(tab_x + 6, y + 12, label, FS_SMALL,
                 BLUE if selected else MUTED, width=tab_w - 12, align="center")
        line(x, y + 48, tab_w * len(labels), 0, color=LINE)
        return

    for idx, label in enumerate(labels):
        row_y = y + idx * 48
        selected = label == active
        if selected:
            rect(x, row_y, 176, 40, bg=BLUE_BG, stroke=BLUE_BG)
        icon = ("AI", "ID", "</>")[idx]
        text(x + 12, row_y + 12, icon, FS_SMALL,
             BLUE if selected else MUTED, font=FONT_MONO, width=34)
        text(x + 48, row_y + 11, label, FS_SMALL,
             BLUE if selected else MUTED, width=118)


def settings_panel_heading(x: float, y: float, w: float, title_value: str,
                           description: str = "") -> None:
    text(x + 18, y + 16, title_value, FS_BODY, INK, width=w - 36)
    if description:
        text(x + 18, y + 42, description, FS_SMALL, MUTED, width=w - 36)
    line(x, y + 72, w, 0, color=LINE)


# Board title and semantic contract
text(ORIGIN_X, 80, "Aevatar Workflow + Activity + Settings vNext", FS_TITLE, INK, width=1040)
text(ORIGIN_X, 132,
     "One authoring resource. One Run action. One immutable Activity history.",
     FS_HEAD, BLUE, width=980)
text(ORIGIN_X, 180,
     "Primary path: Workflows -> direct draft -> Run -> Activity. Settings owns LLM defaults and browser identity.",
     FS_BODY, MUTED, width=1180)


# 01 Workflows catalogue
fx, fy = begin_frame(0, "01 Workflows - catalogue")
cx, cy, cw = app_shell(fx, fy, "Workflows", title="Workflows",
                       subtitle="Create, edit, publish, and run automations.")
button(cx + cw - 174, fy + 22, 144, "New workflow", primary=True, color=BLUE)
field(cx + 30, cy + 26, 370, "Search", "Search workflows")
px = cx + 426
for label, selected in (("All  5", True), ("Published  3", False), ("Drafts  2", False), ("Failing  1", False)):
    px = chip(px, cy + 48, label, selected=selected)
table_x = cx + 30
table_y = cy + 112
table_header(table_x, table_y, [410, 170, 220, 150, 190],
             ["Workflow", "Version", "Last run", "State", "Actions"])
rows = [
    ("Weekly feedback digest", "Group feedback and post a weekly summary", "v7", "12 min ago", "ok", "Published"),
    ("Customer escalation triage", "Classify urgent conversations and prepare replies", "draft r12", "4 min ago", "run", "Draft"),
    ("Invoice follow-up", "Prepare overdue invoice reminders for approval", "v4", "42 min ago", "ok", "Published"),
    ("Nightly order sync", "Move completed orders into the warehouse", "v9", "3:04 AM", "fail", "Published"),
    ("Contract review", "Flag unusual clauses and prepare a review note", "draft r3", "-", "muted", "Draft"),
]
for idx, row in enumerate(rows):
    workflow_row(table_x, table_y + 42 + idx * 82, *row)
text(table_x, fy + FRAME_H - 54,
     "5 workflows  |  Every Run, including a draft revision, is retained in Activity.",
     FS_SMALL, MUTED, width=760)
end_frame()


# 02 Direct Workflow creation
fx, fy = begin_frame(1, "02 New workflow - direct creation")
cx, cy, cw = app_shell(fx, fy, "Workflows", title="New workflow",
                       subtitle="Choose a starting point. Each option creates a workflow draft directly.")
button(cx + cw - 118, fy + 22, 88, "Cancel")
card_y = cy + 42
cards = [
    ("Describe what you need", "Turn a plain-language goal into a reviewable first draft.", BLUE, BLUE_BG, "Describe"),
    ("Start blank", "Open an empty workflow canvas and add the first node.", INK, SURFACE, "Start blank"),
    ("Import YAML", "Create a draft from an existing workflow definition.", PURPLE, PURPLE_BG, "Import"),
]
for idx, (title, body, color, bg, action) in enumerate(cards):
    x = cx + 30 + idx * 390
    rect(x, card_y, 362, 180, bg=bg, stroke=color if idx != 1 else LINE)
    text(x + 22, card_y + 24, title, FS_HEAD, color, width=318)
    text(x + 22, card_y + 64, body, FS_BODY, MUTED, width=310)
    button(x + 22, card_y + 124, 130, action, primary=(idx == 0), color=color)
text(cx + 30, card_y + 226, "TEMPLATES", FS_SMALL, MUTED, font=FONT_MONO, width=180)
templates = [
    ("Weekly feedback digest", "6 steps", "Lark", "Used 240 times"),
    ("Escalation triage", "9 steps", "Lark + Linear", "Used 180 times"),
    ("Invoice follow-up", "11 steps", "Xero + Gmail", "Used 96 times"),
    ("Standup digest", "5 steps", "Lark", "Used 88 times"),
    ("On-call summary", "5 steps", "PagerDuty", "Used 61 times"),
    ("Contract review", "7 steps", "Drive", "Used 44 times"),
]
for idx, item in enumerate(templates):
    col = idx % 3
    row = idx // 3
    x = cx + 30 + col * 390
    y = card_y + 264 + row * 172
    rect(x, y, 362, 148, bg=SURFACE, stroke=LINE)
    text(x + 18, y + 18, item[0], FS_BODY, INK, width=320)
    text(x + 18, y + 48, f"{item[1]}  |  {item[2]}", FS_SMALL, MUTED, width=320)
    text(x + 18, y + 78, item[3], FS_SMALL, MUTED, width=160)
    button(x + 218, y + 92, 126, "Use template", color=BLUE, bg=BLUE_BG)
end_frame()


# 03-06 The four creation paths produce different Workflow documents.
workflow_editor_frame(
    2, "03 Describe - generated Workflow draft",
    title="Monday feedback summary",
    subtitle="Current draft · revision 1 · Saved just now",
    source="Description",
    steps=[
        ("Schedule every Monday", "Schedule"),
        ("Collect recent feedback", "Lark messages"),
        ("Group feedback themes", "AI task"),
        ("Draft concise summary", "AI task"),
        ("Post feedback summary", "Lark message"),
    ],
    selected=0,
    fields=[("Frequency", "Every Monday at 09:00"),
            ("Timezone", "Asia/Shanghai")],
)

workflow_editor_frame(
    3, "04 Start blank - empty Workflow draft",
    title="Untitled workflow",
    subtitle="Current draft · revision 1 · Saved just now",
    source="Blank",
    steps=[],
    empty=True,
)

workflow_editor_frame(
    4, "05 Import YAML - imported Workflow draft",
    title="Daily order exception report",
    subtitle="Current draft · revision 1 · Imported just now",
    source="Import",
    steps=[
        ("Schedule every weekday", "Schedule"),
        ("Find order exceptions", "Data query"),
        ("Summarize exceptions", "AI task"),
        ("Send operations summary", "Lark message"),
    ],
    selected=1,
    fields=[("Source", "Orders"),
            ("Filter", "status = exception")],
)

workflow_editor_frame(
    5, "06 Template - populated Workflow draft",
    title="Standup digest copy",
    subtitle="Current draft · revision 1 · Saved just now",
    source="Template",
    steps=[
        ("Schedule every weekday", "Schedule"),
        ("Collect contributor updates", "Lark messages"),
        ("Draft standup digest", "AI task"),
        ("Post standup digest", "Lark message"),
    ],
    selected=2,
    fields=[("Instruction", "Group updates into completed, planned, and blocked."),
            ("Tone", "Concise")],
)


# 07 Unified Run dialog
fx, fy = begin_frame(6, "07 Run - unified execution dialog")
cx, cy, cw = app_shell(fx, fy, "Workflows", title="Customer escalation triage",
                       subtitle="Draft revision 12  |  Saved just now")
button(cx + cw - 510, fy + 24, 72, "Run", color=INK, h=32)
button(cx + cw - 428, fy + 24, 92, "Add node", color=INK, h=32)
button(cx + cw - 326, fy + 24, 92, "Edit YAML", color=INK, h=32)
button(cx + cw - 224, fy + 24, 82, "Save", primary=True, color=BLUE, h=32)
button(cx + cw - 132, fy + 24, 102, "Publish", color=INK, h=32)
studio_canvas(cx, cy, cw, FRAME_H - (cy - fy), [
    ("Receive conversation", "Lark message"),
    ("Classify severity", "AI task"),
    ("Route escalation", "Condition"),
    ("Prepare reply", "AI task"),
    ("Post response", "Lark message"),
], source="Description")
modal_x = cx + 260
modal_y = cy + 38
modal_w = 710
rect(modal_x, modal_y, modal_w, 720, bg=SURFACE, stroke="#344054", sw=2)
text(modal_x + 28, modal_y + 26, "Run workflow", FS_HEAD, INK, width=450)
text(modal_x + 28, modal_y + 62,
     "Confirm the revision, inputs, and external effects.", FS_SMALL, MUTED, width=520)
badge(modal_x + 28, modal_y + 102, "Current draft", "info")
text(modal_x + 154, modal_y + 108, "revision 12", FS_SMALL, PURPLE,
     font=FONT_MONO, width=130)
line(modal_x + 28, modal_y + 144, modal_w - 56, 0, color=LINE)
field(modal_x + 28, modal_y + 168, modal_w - 56, "Workflow", "Customer escalation triage")
field(modal_x + 28, modal_y + 252, modal_w - 56, "Conversation", "support-thread-4821", mono=True)
field(modal_x + 28, modal_y + 336, (modal_w - 70) / 2, "Environment", "Production")
field(modal_x + 42 + (modal_w - 70) / 2, modal_y + 336,
      (modal_w - 70) / 2, "Connections", "2 ready")
rect(modal_x + 28, modal_y + 430, modal_w - 56, 86, bg=AMBER_BG, stroke=AMBER)
text(modal_x + 46, modal_y + 448, "External effects", FS_BODY, AMBER, width=180)
text(modal_x + 46, modal_y + 477,
     "May post a Lark reply and create a Linear issue.", FS_SMALL, INK, width=560)
rect(modal_x + 28, modal_y + 536, 18, 18, bg=BLUE, stroke=BLUE)
text(modal_x + 32, modal_y + 537, "x", FS_SMALL, SURFACE, width=12)
text(modal_x + 58, modal_y + 536,
     "I understand this workflow may change external systems.", FS_SMALL, INK, width=590)
rect(modal_x + 28, modal_y + 574, modal_w - 56, 50, bg=BLUE_BG, stroke=BLUE)
text(modal_x + 46, modal_y + 590,
     "This Run will be saved in Activity.", FS_SMALL, BLUE, width=500)
button(modal_x + modal_w - 232, modal_y + 652, 88, "Cancel")
button(modal_x + modal_w - 132, modal_y + 652, 104, "Run", primary=True, color=BLUE)
end_frame()


# 08 Running in the same Workflow Studio canvas
fx, fy = begin_frame(7, "08 Running draft - Studio canvas and Run console")
cx, cy, cw = app_shell(fx, fy, "Workflows", title="Customer escalation triage",
                       subtitle="Current draft · revision 12 · Run R-1042")
button(cx + cw - 510, fy + 24, 72, "Run", color=INK, h=32)
button(cx + cw - 428, fy + 24, 92, "Add node", color=INK, h=32)
button(cx + cw - 326, fy + 24, 92, "Edit YAML", color=INK, h=32)
button(cx + cw - 224, fy + 24, 82, "Save", primary=True, color=BLUE, h=32)
button(cx + cw - 132, fy + 24, 102, "Publish", color=INK, h=32)
running_steps = [
    ("Receive conversation", "Lark message"),
    ("Classify severity", "AI task"),
    ("Route escalation", "Condition"),
    ("Prepare reply", "AI task"),
    ("Post response", "Lark message"),
]
studio_canvas(cx, cy, cw, 570, running_steps, source="Description",
              statuses=["Completed", "Completed", "Completed", "Running", "Queued"])
console_y = cy + 570
rect(cx, console_y, cw, FRAME_H - (console_y - fy), bg=SURFACE, stroke="#d8e0ea", radius=False)
rect(cx, console_y - 6, cw, 6, bg="#cbd5e1", stroke="#cbd5e1", radius=False)
text(cx + 22, console_y + 18, "Run console", FS_BODY, INK, width=180)
badge(cx + 142, console_y + 14, "Running", "run")
text(cx + 248, console_y + 21,
     "R-1042 · Current draft · revision 12 · Activity record created",
     FS_SMALL, BLUE, font=FONT_MONO, width=620)
button(cx + cw - 132, console_y + 12, 102, "View Run", color=BLUE, bg=BLUE_BG, h=34)
line(cx, console_y + 58, cw, 0, color="#eef2f7")
log_rows = [
    ("14:32:18.102", "Receive conversation", "completed", GREEN),
    ("14:32:22.364", "Classify severity", "completed", GREEN),
    ("14:32:22.481", "Route escalation", "completed", GREEN),
    ("14:32:23.019", "Prepare reply", "running", BLUE),
]
for idx, (stamp, name, state, color) in enumerate(log_rows):
    row_y = console_y + 78 + idx * 37
    text(cx + 24, row_y, stamp, 12, MUTED, font=FONT_MONO, width=120)
    dot(cx + 152, row_y + 2, 10, color, bg=color)
    text(cx + 176, row_y, name, FS_SMALL, INK, width=260)
    text(cx + 450, row_y, state, FS_SMALL, color, font=FONT_MONO, width=130)
rect(cx + 690, console_y + 74, cw - 720, 134, bg=SIDEBAR, stroke=SIDEBAR)
text(cx + 708, console_y + 90, "OUTPUT", 12, FAINT, font=FONT_MONO, width=100)
text(cx + 708, console_y + 120,
     "Preparing a concise response for\nthe support conversation...",
     FS_SMALL, "#d0d5dd", font=FONT_MONO, width=cw - 760)
end_frame()


# 09 Activity with a Workflow filter
fx, fy = begin_frame(8, "09 Activity - filtered by Workflow")
cx, cy, cw = app_shell(fx, fy, "Activity", title="Activity",
                       subtitle="Every Run, newest first. Filtered to Weekly feedback digest.")
field(cx + 30, cy + 24, 330, "Search", "Search Run or outcome")
px = cx + 386
px = chip(px, cy + 46, "Workflow: Weekly feedback digest", selected=True)
for label, selected in (("All  24", True), ("Needs you  1", False), ("Running  1", False), ("Failed  2", False)):
    px = chip(px, cy + 46, label, selected=selected)
table_x = cx + 30
table_y = cy + 112
table_header(table_x, table_y, [105, 235, 120, 175, 115, 75, 90, 255],
             ["Status", "Workflow", "Origin", "Revision", "When", "Took", "Usage", "Outcome"])
runs = [
    ("Running", "Weekly feedback digest", "Manual · you", "Current draft · revision 12", "18s ago", "00:18", "$0.03", "Preparing summary"),
    ("Succeeded", "Weekly feedback digest", "Schedule · weekly", "Published · v7", "2h ago", "01:42", "$0.18", "Posted 1 summary"),
    ("Needs you", "Weekly feedback digest", "Schedule · weekly", "Published · v7", "1w ago", "00:56", "$0.11", "Approval requested"),
    ("Failed", "Weekly feedback digest", "Manual · Calvin", "Draft · revision 9", "8d ago", "00:12", "$0.02", "Lark connection expired"),
    ("Succeeded", "Weekly feedback digest", "Run again · R-1001", "Published · v6", "15d ago", "01:37", "$0.17", "Posted 1 summary"),
]
for idx, run in enumerate(runs):
    activity_row(table_x, table_y + 42 + idx * 70, *run)
text(table_x, fy + FRAME_H - 52,
     "Activity remains the authoritative Run history. Run again and Retry append linked records.",
     FS_SMALL, BLUE, width=720)
end_frame()


# 10 Global Activity
fx, fy = begin_frame(9, "10 Activity - all retained Runs")
cx, cy, cw = app_shell(fx, fy, "Activity", title="Activity",
                       subtitle="Every Run, newest first. Search, inspect, and recover from one place.")
field(cx + 30, cy + 28, 380, "Search", "Search workflow, Run, or outcome")
px = cx + 438
for label, selected in (("All  128", True), ("Needs you  2", False), ("Running  3", False), ("Failed  7", False), ("Mine  18", False)):
    px = chip(px, cy + 50, label, selected=selected)
table_x = cx + 30
table_y = cy + 116
table_header(table_x, table_y, [105, 235, 120, 175, 115, 75, 90, 255],
             ["Status", "Workflow", "Origin", "Revision", "When", "Took", "Usage", "Outcome"])
runs = [
    ("Running", "Customer escalation triage", "Manual · you", "Current draft · revision 12", "18s ago", "00:18", "$0.03", "Preparing reply"),
    ("Needs you", "Invoice follow-up", "Schedule · weekday", "Published · v4", "42m ago", "02:18", "$0.22", "Approve 4 messages"),
    ("Succeeded", "Weekly feedback digest", "Schedule · weekly", "Published · v7", "2h ago", "01:42", "$0.18", "Posted 1 summary"),
    ("Failed", "Nightly order sync", "Schedule · nightly", "Published · v9", "3:04 AM", "00:21", "$0.04", "Warehouse unavailable"),
    ("Succeeded", "Customer escalation triage", "Retry · R-1031", "Draft · revision 11", "Yesterday", "00:49", "$0.09", "Posted reply"),
    ("Failed", "Customer escalation triage", "Manual · Calvin", "Draft · revision 11", "Yesterday", "00:13", "$0.02", "Linear permission denied"),
    ("Succeeded", "Invoice follow-up", "Manual · you", "Published · v4", "2d ago", "02:04", "$0.20", "Sent 6 reminders"),
]
for idx, run in enumerate(runs):
    activity_row(table_x, table_y + 42 + idx * 70, *run)
text(table_x, fy + FRAME_H - 50,
     "Showing 7 of 128 Runs  |  New records appear here as soon as execution starts.",
     FS_SMALL, MUTED, width=680)
end_frame()


# 11 Run detail
fx, fy = begin_frame(10, "11 Run detail - immutable record")
cx, cy, cw = app_shell(fx, fy, "Activity", title="Run #R-1042",
                       subtitle="Customer escalation triage  |  Started 18 seconds ago")
button(cx + cw - 328, fy + 22, 126, "Open workflow", color=BLUE, bg=BLUE_BG)
button(cx + cw - 190, fy + 22, 160, "Run again", primary=True, color=BLUE)
left_w = 780
rect(cx + 30, cy + 28, left_w, 748, bg=SURFACE, stroke=LINE)
badge(cx + 54, cy + 52, "Running", "run")
text(cx + 54, cy + 100, "Customer escalation triage", FS_HEAD, INK, width=520)
text(cx + 54, cy + 138,
     "Current draft · revision 12", FS_BODY, PURPLE, font=FONT_MONO, width=360)
summary_fields = [
    ("ORIGIN", "Manual · you"),
    ("STARTED", "Aug 3, 2026 at 14:32:18"),
    ("ELAPSED", "00:18"),
    ("USAGE", "$0.03 · 5.8k tokens"),
]
for idx, (label, value) in enumerate(summary_fields):
    sx = cx + 54 + (idx % 2) * 360
    sy = cy + 196 + (idx // 2) * 84
    text(sx, sy, label, FS_SMALL, MUTED, font=FONT_MONO, width=130)
    text(sx, sy + 26, value, FS_SMALL, INK, width=310)
line(cx + 54, cy + 362, left_w - 48, 0, color=LINE)
text(cx + 54, cy + 390, "STEP TIMELINE", FS_SMALL, MUTED, font=FONT_MONO, width=170)
timeline = [
    ("Receive conversation", "0.8s", "ok"),
    ("Classify severity", "3.4s", "ok"),
    ("Route escalation", "0.1s", "ok"),
    ("Prepare reply", "running 13s", "run"),
    ("Post response", "queued", "muted"),
]
for idx, (name, duration, status) in enumerate(timeline):
    ty = cy + 432 + idx * 58
    color = GREEN if status == "ok" else BLUE if status == "run" else FAINT
    dot(cx + 58, ty + 4, 18, color, bg=color if status != "muted" else SURFACE)
    if idx < len(timeline) - 1:
        line(cx + 67, ty + 22, 0, 38, color=LINE)
    text(cx + 94, ty + 2, name, FS_SMALL, INK, width=320)
    text(cx + 460, ty + 2, duration, FS_SMALL, color, font=FONT_MONO, width=160)

side_x = cx + 834
side_w = cw - 864
rect(side_x, cy + 28, side_w, 748, bg=SUBTLE, stroke=LINE)
text(side_x + 24, cy + 52, "RECORD", FS_SMALL, MUTED, font=FONT_MONO, width=100)
field(side_x + 24, cy + 88, side_w - 48, "Run", "R-1042", mono=True)
field(side_x + 24, cy + 172, side_w - 48, "Revision", "draft-r12", mono=True)
field(side_x + 24, cy + 256, side_w - 48, "Environment", "Production")
text(side_x + 24, cy + 346, "LINKED RUNS", FS_SMALL, MUTED, font=FONT_MONO, width=140)
rect(side_x + 24, cy + 376, side_w - 48, 76, bg=SURFACE, stroke=LINE)
text(side_x + 40, cy + 392, "No linked Runs yet", FS_SMALL, INK, width=240)
text(side_x + 40, cy + 419,
     "Run again will create one.", FS_SMALL, MUTED, width=270)
rect(side_x + 24, cy + 480, side_w - 48, 124, bg=BLUE_BG, stroke=BLUE)
text(side_x + 40, cy + 500, "History is immutable", FS_BODY, BLUE, width=250)
text(side_x + 40, cy + 534,
     "A new attempt keeps this record and links back to R-1042.",
     FS_SMALL, INK, width=300)
button(side_x + 24, cy + 634, side_w - 48, "Run again", primary=True, color=BLUE)
end_frame()


# 12 Failure / Needs you recovery
fx, fy = begin_frame(11, "12 Failed Run - recovery creates a new record")
cx, cy, cw = app_shell(fx, fy, "Activity", title="Run #R-1038",
                       subtitle="Nightly order sync  |  Failed at step 4")
button(cx + cw - 328, fy + 22, 126, "Edit workflow", color=BLUE, bg=BLUE_BG)
button(cx + cw - 190, fy + 22, 160, "Retry", primary=True, color=BLUE)
rect(cx + 30, cy + 28, cw - 60, 110, bg=RED_BG, stroke=RED)
badge(cx + 52, cy + 52, "Failed", "fail")
text(cx + 160, cy + 56, "Warehouse connection is unavailable", FS_HEAD, RED, width=560)
text(cx + 160, cy + 90,
     "Step 4 · Write completed orders  |  The connection rejected the request.",
     FS_SMALL, INK, width=700)
text(cx + 52, cy + 168, "WHAT HAPPENED", FS_SMALL, MUTED, font=FONT_MONO, width=180)
rect(cx + 30, cy + 198, 720, 246, bg=SURFACE, stroke=LINE)
field(cx + 54, cy + 222, 672, "Revision", "Published · v9", mono=True)
field(cx + 54, cy + 306, 320, "Failed after", "00:21", mono=True)
field(cx + 390, cy + 306, 336, "Usage", "$0.04 · 8.1k tokens", mono=True)
text(cx + 54, cy + 400,
     "The previous three completed steps remain visible in this Run record.",
     FS_SMALL, MUTED, width=620)
side_x = cx + 780
side_w = cw - 810
text(side_x + 22, cy + 168, "RECOVER", FS_SMALL, MUTED, font=FONT_MONO, width=130)
rect(side_x, cy + 198, side_w, 246, bg=SUBTLE, stroke=LINE)
text(side_x + 24, cy + 222, "1. Repair the connection", FS_BODY, INK, width=330)
button(side_x + 24, cy + 254, side_w - 48, "Open connections", color=BLUE, bg=BLUE_BG)
text(side_x + 24, cy + 318, "2. Retry this Run", FS_BODY, INK, width=330)
button(side_x + 24, cy + 350, side_w - 48, "Retry", primary=True, color=BLUE)
text(cx + 30, cy + 486, "RETRY PREVIEW", FS_SMALL, MUTED, font=FONT_MONO, width=160)
rect(cx + 30, cy + 516, cw - 60, 204, bg=BLUE_BG, stroke=BLUE)
text(cx + 54, cy + 542, "A new Run will be created", FS_HEAD, BLUE, width=420)
text(cx + 54, cy + 584,
     "New record: R-1043  |  Source Run: R-1038  |  Published · v9",
     FS_BODY, INK, font=FONT_MONO, width=720)
text(cx + 54, cy + 624,
     "R-1038 remains Failed. R-1043 starts from step 4 after the connection is repaired.",
     FS_SMALL, MUTED, width=790)
button(cx + cw - 252, cy + 652, 88, "Cancel")
button(cx + cw - 152, cy + 652, 98, "Retry", primary=True, color=BLUE)
end_frame()


# 13 State sheet and mobile behavior
fx, fy = begin_frame(12, "13 Workflows and Activity - states")
cx, cy, cw = app_shell(fx, fy, "Activity", title="Workflows + Activity states",
                       subtitle="Empty, loading, error, and compact layouts for the two in-scope areas.")
state_w = 360
state_y = cy + 34
states = [
    ("Empty Workflows", "No workflows yet", "Create your first workflow directly.", "New workflow", BLUE, BLUE_BG),
    ("Empty Activity", "No Runs match", "Change filters or run a workflow.", "Clear filters", MUTED, SUBTLE),
    ("Loading Activity", "Loading Run history", "Keeping the current filters in place.", "", BLUE, BLUE_BG),
]
for idx, (label, title, body, action, color, bg) in enumerate(states):
    x = cx + 30 + idx * 390
    rect(x, state_y, state_w, 218, bg=bg, stroke=color if idx != 1 else LINE)
    text(x + 20, state_y + 18, label.upper(), FS_SMALL, color, font=FONT_MONO, width=260)
    if idx == 2:
        for bar in range(3):
            rect(x + 24, state_y + 62 + bar * 42, state_w - 48 - bar * 34, 18,
                 bg="#dbeafe", stroke="#dbeafe")
    else:
        text(x + 20, state_y + 62, title, FS_HEAD, INK, width=310)
        text(x + 20, state_y + 100, body, FS_SMALL, MUTED, width=310)
        button(x + 20, state_y + 150, 130, action,
               primary=(idx == 0), color=color, bg=SURFACE)

error_y = state_y + 250
rect(cx + 30, error_y, 750, 174, bg=RED_BG, stroke=RED)
text(cx + 52, error_y + 22, "ACTIVITY UNAVAILABLE", FS_SMALL, RED, font=FONT_MONO, width=240)
text(cx + 52, error_y + 58, "Run history could not be loaded", FS_HEAD, INK, width=520)
text(cx + 52, error_y + 96,
     "Your Runs are still retained. Retry loading without losing search or filters.",
     FS_SMALL, MUTED, width=620)
button(cx + 608, error_y + 108, 142, "Retry loading", color=RED, bg=RED_BG)

mobile_x = cx + 826
mobile_y = error_y
mobile_w = 370
rect(mobile_x, mobile_y, mobile_w, 438, bg=SURFACE, stroke="#344054", sw=2)
rect(mobile_x, mobile_y, mobile_w, 58, bg=SIDEBAR, stroke=SIDEBAR, radius=False)
text(mobile_x + 18, mobile_y + 18, "Activity", FS_BODY, SURFACE, width=140)
button(mobile_x + 264, mobile_y + 12, 88, "Filters", color=SURFACE, bg=SIDEBAR, h=34)
mobile_runs = [
    ("Running", "Customer escalation triage", "Draft · r12", "run"),
    ("Needs you", "Invoice follow-up", "Published · v4", "wait"),
    ("Failed", "Nightly order sync", "Published · v9", "fail"),
]
for idx, (status, name, revision, palette) in enumerate(mobile_runs):
    y = mobile_y + 76 + idx * 108
    rect(mobile_x + 14, y, mobile_w - 28, 92, bg=SURFACE, stroke=LINE)
    badge(mobile_x + 28, y + 14, status, palette)
    text(mobile_x + 28, y + 48, name, FS_SMALL, INK, width=210)
    text(mobile_x + 230, y + 48, revision, FS_SMALL, MUTED,
         font=FONT_MONO, width=100, align="right")
text(cx + 30, fy + FRAME_H - 54,
     "Compact layouts preserve status, workflow, revision, and recovery actions before secondary metrics.",
     FS_SMALL, MUTED, width=820)
end_frame()


# 14 Settings - AI defaults
fx, fy = begin_frame(13, "14 Settings - AI defaults")
cx, cy, cw = app_shell(fx, fy, "Settings", title="Settings",
                       subtitle="Personal defaults and access.")
settings_nav(cx + 26, cy + 34, "AI defaults")
main_x = cx + 236
main_w = cw - 266
text(main_x, cy + 34, "AI defaults", FS_HEAD, INK, width=360)
text(main_x, cy + 70,
     "Choose the service and model used by new Chat, Studio, and global tool sessions without an override.",
     FS_SMALL, MUTED, width=650)
line(main_x, cy + 108, main_w, 0, color=LINE)

rows = [
    ("Preferred service", "Gateway or exact connected service.", "NyxID Gateway"),
    ("Default model", "Leave unset to use the service default.", "gpt-4o-mini"),
]
for idx, (label, body, value) in enumerate(rows):
    y = cy + 146 + idx * 104
    text(main_x, y + 10, label, FS_SMALL, INK, width=220)
    text(main_x, y + 38, body, FS_SMALL, MUTED, width=230)
    rect(main_x + 270, y, main_w - 270, 48, bg=SURFACE, stroke=LINE)
    text(main_x + 286, y + 15, value, FS_SMALL, INK, width=main_w - 310)
    line(main_x, y + 82, main_w, 0, color=LINE)
end_frame()


# 15 Settings - save and recovery states
fx, fy = begin_frame(14, "15 Settings - save and recovery states")
cx, cy, cw = app_shell(fx, fy, "Settings", title="Settings states",
                       subtitle="Dirty, accepted, fallback, and catalog recovery without dashboard noise.")
settings_nav(cx + 26, cy + 34, "AI defaults")
main_x = cx + 236
main_w = cw - 266
text(main_x, cy + 34, "AI defaults", FS_HEAD, INK, width=340)
badge(main_x + main_w - 126, cy + 34, "Unsaved", "wait")
line(main_x, cy + 92, main_w, 0, color=LINE)

state_y = cy + 126
state_gap = 18
state_w = (main_w - state_gap * 2) / 3
state_specs = [
    ("UNSAVED", "Sticky save bar appears", "Discard and Save changes stay close to the edited defaults.", AMBER, AMBER_BG),
    ("ACCEPTED", "Confirming saved values", "Keep the submitted exact service visible until the committed values are observed.", BLUE, BLUE_BG),
    ("FALLBACK", "Effective route changed", "Show fallback only when the selected service is unavailable.", AMBER, AMBER_BG),
]
for idx, (label, title_value, body, color, bg) in enumerate(state_specs):
    x = main_x + idx * (state_w + state_gap)
    rect(x, state_y, state_w, 218, bg=bg, stroke=color)
    text(x + 16, state_y + 16, label, FS_SMALL, color, font=FONT_MONO, width=state_w - 32)
    text(x + 16, state_y + 52, title_value, FS_BODY, INK, width=state_w - 32)
    text(x + 16, state_y + 88, body, FS_SMALL, MUTED,
         width=state_w - 32, line_height=1.45)
    if idx == 0:
        button(x + 16, state_y + 166, 92, "Discard")
        button(x + 118, state_y + 166, 122, "Save changes", primary=True, color=BLUE)
    elif idx == 1:
        badge(x + 16, state_y + 166, "Observation pending", "run")
    else:
        badge(x + 16, state_y + 166, "Effective: Gateway", "wait")

recovery_y = state_y + 252
rect(main_x, recovery_y, (main_w - 18) / 2, 220, bg=SUBTLE, stroke=LINE)
text(main_x + 18, recovery_y + 18, "CATALOG UNAVAILABLE", FS_SMALL, AMBER,
     font=FONT_MONO, width=300)
text(main_x + 18, recovery_y + 56, "Keep stored defaults visible", FS_BODY, INK, width=380)
text(main_x + 18, recovery_y + 92,
     "Disable service and model editing until provider inventory can be loaded.",
     FS_SMALL, MUTED, width=400, line_height=1.45)
button(main_x + 18, recovery_y + 160, 88, "Retry", color=AMBER, bg=AMBER_BG)

error_x = main_x + (main_w - 18) / 2 + 18
rect(error_x, recovery_y, (main_w - 18) / 2, 220, bg=RED_BG, stroke=RED)
text(error_x + 18, recovery_y + 18, "SAVE FAILED", FS_SMALL, RED,
     font=FONT_MONO, width=300)
text(error_x + 18, recovery_y + 56, "Edits remain intact", FS_BODY, INK, width=380)
text(error_x + 18, recovery_y + 92,
     "The exact service and model stay editable. Retry without rebuilding the draft.",
     FS_SMALL, MUTED, width=400, line_height=1.45)
button(error_x + 18, recovery_y + 160, 126, "Save changes", color=RED, bg=RED_BG)

savebar_y = cy + 680
rect(main_x, savebar_y, main_w, 72, bg="#1d2939", stroke="#344054")
text(main_x + 18, savebar_y + 14, "Unsaved changes", FS_SMALL, SURFACE, width=280)
text(main_x + 18, savebar_y + 40, "Your AI defaults have not been saved.", FS_SMALL, "#d0d5dd", width=420)
button(main_x + main_w - 244, savebar_y + 17, 92, "Discard", color=SURFACE, bg="#1d2939")
button(main_x + main_w - 140, savebar_y + 17, 122, "Save changes", primary=True, color=BLUE)
end_frame()


# 16 Settings - Account
fx, fy = begin_frame(15, "16 Settings - Account")
cx, cy, cw = app_shell(fx, fy, "Settings", title="Settings",
                       subtitle="Identity and service access for this browser.")
settings_nav(cx + 26, cy + 34, "Account")
main_x = cx + 236
main_w = cw - 266
text(main_x, cy + 34, "Account", FS_HEAD, INK, width=340)
text(main_x, cy + 70, "Identity and service access for this browser session.",
     FS_SMALL, MUTED, width=560)
line(main_x, cy + 108, main_w, 0, color=LINE)

profile_y = cy + 138
text(main_x, profile_y, "Profile", FS_BODY, INK, width=260)
button(main_x + main_w - 94, profile_y, 94, "Sign out", color=RED, bg=RED_BG)
dot(main_x, profile_y + 48, 50, BLUE)
text(main_x + 68, profile_y + 48, "Calvin Tan", FS_BODY, INK, width=260)
text(main_x + 68, profile_y + 78, "calvin@example.com", FS_SMALL, MUTED, width=300)

detail_y = profile_y + 122
detail_w = main_w / 2
account_fields = [
    ("USER ID", "usr_7c9142...e8a13f"),
    ("ROLES", "admin, operator"),
    ("GROUPS", "platform"),
    ("EMAIL", "Verified"),
]
for idx, (label, value) in enumerate(account_fields):
    col = idx % 2
    row = idx // 2
    x = main_x + col * detail_w
    y = detail_y + row * 76
    rect(x, y, detail_w, 76, bg=SURFACE, stroke=LINE, radius=False)
    text(x + 14, y + 14, label, FS_SMALL, MUTED, font=FONT_MONO, width=detail_w - 28)
    text(x + 14, y + 44, value, FS_SMALL, INK, width=detail_w - 28)

auth_y = profile_y + 300
line(main_x, auth_y - 22, main_w, 0, color=LINE)
text(main_x, auth_y, "Authentication", FS_BODY, INK, width=280)
button(main_x + main_w - 208, auth_y, 208, "Manage service access")
auth_fields = [
    ("SESSION EXPIRES", "2026/8/4 18:30"),
    ("PROVIDER", "NyxID"),
    ("SCOPE", "openid profile email"),
    ("BROWSER TOKEN REFRESH", "Disabled"),
]
for idx, (label, value) in enumerate(auth_fields):
    col = idx % 2
    row = idx // 2
    x = main_x + col * detail_w
    y = auth_y + 56 + row * 76
    rect(x, y, detail_w, 76, bg=SURFACE, stroke=LINE, radius=False)
    text(x + 14, y + 14, label, FS_SMALL, MUTED, font=FONT_MONO, width=detail_w - 28)
    text(x + 14, y + 44, value, FS_SMALL, INK, width=detail_w - 28)

signedout_y = cy + 704
rect(main_x, signedout_y, main_w, 66, bg=SUBTLE, stroke=LINE)
text(main_x + 16, signedout_y + 14, "SIGNED-OUT STATE", FS_SMALL, MUTED,
     font=FONT_MONO, width=180)
text(main_x + 214, signedout_y + 14,
     "Replace identity claims with No active session and a Sign in action.",
     FS_SMALL, INK, width=560)
end_frame()


# 17 Settings - Advanced and responsive behavior
fx, fy = begin_frame(16, "17 Settings - Advanced and responsive")
cx, cy, cw = app_shell(fx, fy, "Settings", title="Settings",
                       subtitle="Advanced diagnostics stay separate from everyday defaults.")
settings_nav(cx + 20, cy + 30, "Advanced")
advanced_x = cx + 218
advanced_w = 610
text(advanced_x, cy + 30, "Advanced", FS_HEAD, INK, width=280)
text(advanced_x, cy + 66, "Read-only runtime and routing details for troubleshooting.",
     FS_SMALL, MUTED, width=480)
line(advanced_x, cy + 104, advanced_w, 0, color=LINE)
text(advanced_x, cy + 132, "Request values", FS_BODY, INK, width=260)
text(advanced_x, cy + 162, "Effective values supplied to new requests.",
     FS_SMALL, MUTED, width=460)
technical_rows = [
    ("x-aevatar-llm-route", "nyxid_gateway"),
    ("x-aevatar-llm-model", "gpt-4o-mini"),
    ("studio.runtime_base_url", "http://127.0.0.1:5080"),
    ("aevatar.runtime_mode", "local"),
]
for idx, (key, value) in enumerate(technical_rows):
    y = cy + 204 + idx * 48
    rect(advanced_x, y, advanced_w, 48, bg=SIDEBAR, stroke="#344054", radius=False)
    text(advanced_x + 14, y + 15, key, FS_SMALL, "#d0d5dd",
         font=FONT_MONO, width=270)
    text(advanced_x + 292, y + 15, value, FS_SMALL, "#98a2b3",
         font=FONT_MONO, width=advanced_w - 306)

mobile_x = cx + 858
mobile_y = cy + 28
mobile_w = 344
rect(mobile_x, mobile_y, mobile_w, 774, bg=SURFACE, stroke="#344054", sw=2)
rect(mobile_x, mobile_y, mobile_w, 58, bg=SIDEBAR, stroke=SIDEBAR, radius=False)
text(mobile_x + 14, mobile_y + 18, "Settings", FS_BODY, SURFACE, width=120)
text(mobile_x + 14, mobile_y + 80, "Settings", FS_HEAD, INK, width=180)
text(mobile_x + 14, mobile_y + 116,
     "Personal defaults and access.",
     FS_SMALL, MUTED, width=310)
settings_nav(mobile_x + 10, mobile_y + 166, "AI defaults", mobile=True)
text(mobile_x + 14, mobile_y + 236, "AI defaults", FS_HEAD, INK, width=220)
text(mobile_x + 14, mobile_y + 274,
     "Service and model for new sessions without an override.",
     FS_SMALL, MUTED, width=310)
line(mobile_x + 14, mobile_y + 322, mobile_w - 28, 0, color=LINE)
text(mobile_x + 14, mobile_y + 348, "Preferred service", FS_SMALL, INK, width=260)
field(mobile_x + 14, mobile_y + 376, mobile_w - 28, "Service", "NyxID Gateway")
text(mobile_x + 14, mobile_y + 472, "Default model", FS_SMALL, INK, width=260)
field(mobile_x + 14, mobile_y + 500, mobile_w - 28, "Model", "gpt-4o-mini")
rect(mobile_x + 14, mobile_y + 670, mobile_w - 28, 70, bg="#1d2939", stroke="#344054")
text(mobile_x + 26, mobile_y + 682, "Unsaved changes", FS_SMALL, SURFACE, width=180)
button(mobile_x + 202, mobile_y + 686, 116, "Save changes", primary=True, color=BLUE, h=34)
end_frame()


document = {
    "type": "excalidraw",
    "version": 2,
    "source": "https://excalidraw.com",
    "elements": elements,
    "appState": {
        "gridSize": 20,
        "gridStep": 5,
        "gridModeEnabled": False,
        "viewBackgroundColor": "#eef2f6",
    },
    "files": {},
}

OUT.write_text(json.dumps(document, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")


# Structural and semantic checks
ids = [item["id"] for item in elements]
assert len(ids) == len(set(ids)), "duplicate Excalidraw element IDs"
frames = {item["id"]: item for item in elements if item["type"] == "frame"}
assert len(frames) == 17, f"expected 17 frames, got {len(frames)}"

escaped = []
for item in elements:
    frame_id = item.get("frameId")
    if not frame_id:
        continue
    owner = frames[frame_id]
    if (item["x"] < owner["x"] - 2 or item["y"] < owner["y"] - 2
            or item["x"] + item["width"] > owner["x"] + owner["width"] + 2
            or item["y"] + item["height"] > owner["y"] + owner["height"] + 2):
        escaped.append((owner["name"], item["type"], item.get("text", "")[:60]))
assert not escaped, f"elements escaped frames: {escaped[:8]}"

visible_text = "\n".join(item["text"] for item in elements if item["type"] == "text")
for forbidden in ("Invoke", "Draft Run", "Test Run", "Create Team", "Create Member", "Open Flows", "Fix flow"):
    assert forbidden.casefold() not in visible_text.casefold(), f"stale term: {forbidden}"

standalone_flow = re.compile(r"(?<!work)\bflows?\b", re.IGNORECASE)
assert not standalone_flow.search(visible_text), "standalone product Flow terminology remains"

assert visible_text.count("Workflows") >= 10
assert visible_text.count("Activity") >= 12
assert visible_text.count("Settings") >= 8
assert "This Run will be saved in Activity." in visible_text
assert "Current draft · revision 12" in visible_text
assert "Run again will create one." in visible_text
assert "Preferred service" in visible_text
assert "Manage service access" in visible_text
assert "AI defaults" in visible_text
assert "studio.runtime_base_url" in visible_text
assert "aevatar.runtime_mode" in visible_text

print(f"wrote {OUT}")
print(f"elements: {len(elements)}")
print(f"frames: {len(frames)}")
print("semantic checks: passed")
print("frame bounds: passed")
