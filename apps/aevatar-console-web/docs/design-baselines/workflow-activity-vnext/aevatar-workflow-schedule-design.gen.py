#!/usr/bin/env python3
"""Generate standalone Schedule review scenes for published Workflows.

The scenes follow the Schedule path in the supplied wireframe while keeping
the Aevatar product boundary explicit: a Schedule is owned and addressed by
its published Workflow, independent from Team member automation.
"""

from __future__ import annotations

import json
import random
from pathlib import Path


random.seed(20260817)

OUT = Path(__file__).with_name("aevatar-workflow-schedule-design.excalidraw")
INK = "#1e1e1e"
MUTED = "#667085"
FAINT = "#98a2b3"
LINE = "#ced4da"
SURFACE = "#ffffff"
SUBTLE = "#f8f9fa"
SIDEBAR = "#f8f9fa"
BLUE = "#175cd3"
BLUE_BG = "#eff8ff"
GREEN = "#067647"
GREEN_BG = "#ecfdf3"
AMBER = "#b54708"
AMBER_BG = "#fffaeb"
RED = "#b42318"
RED_BG = "#fef3f2"
PURPLE = "#7048e8"
PURPLE_BG = "#f4f3ff"

FONT_SANS = 1
FONT_MONO = 3
FS_SMALL = 14
FS_BODY = 16
FS_HEAD = 22
FS_TITLE = 32

FRAME_W = 1560
FRAME_H = 1000
GAP_X = 320
GAP_Y = 260
COLS = 3
ORIGIN_X = 140
ORIGIN_Y = 480

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
         stroke: str = LINE, radius: bool = True, style: str = "solid",
         sw: int = 1) -> dict:
    item = base("rectangle", x, y, w, h, bg=bg, stroke=stroke, style=style,
                sw=sw)
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
    item = base("text", x, y, width, len(lines) * size * line_height,
                stroke=color)
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
         arrowhead: str | None = None, sw: int = 1,
         style: str = "solid") -> dict:
    kind = "arrow" if arrowhead else "line"
    item = base(kind, x, y, abs(w), abs(h), stroke=color, sw=sw, style=style)
    item.update({
        "points": [[0, 0], [float(w), float(h)]],
        "lastCommittedPoint": None,
        "startBinding": None,
        "endBinding": None,
        "startArrowhead": None,
        "endArrowhead": arrowhead,
    })
    elements.append(item)
    return item


def dot(x: float, y: float, diameter: float, color: str, *, bg: str | None = None) -> dict:
    item = base("ellipse", x, y, diameter, diameter, stroke=color,
                bg=bg or color)
    elements.append(item)
    return item


def button(x: float, y: float, w: float, label: str, *, primary: bool = False,
           color: str = INK, bg: str = SURFACE, h: float = 38) -> None:
    rect(x, y, w, h, bg=color if primary else bg, stroke=color)
    text(x + 8, y + 9, label, FS_SMALL, SURFACE if primary else color,
         width=w - 16, align="center")


def badge(x: float, y: float, label: str, kind: str = "muted") -> float:
    colors = {
        "ok": (GREEN, GREEN_BG),
        "wait": (AMBER, AMBER_BG),
        "fail": (RED, RED_BG),
        "info": (PURPLE, PURPLE_BG),
        "run": (BLUE, BLUE_BG),
        "muted": (MUTED, SUBTLE),
    }
    color, bg = colors[kind]
    width = max(74, len(label) * 7.5 + 24)
    rect(x, y, width, 28, bg=bg, stroke=color)
    text(x + 10, y + 6, label, FS_SMALL, color, width=width - 20)
    return x + width + 8


def chip(x: float, y: float, label: str, *, selected: bool = False,
         width: float | None = None) -> float:
    width = width or max(86, len(label) * 8 + 24)
    rect(x, y, width, 34, bg=BLUE_BG if selected else SURFACE,
         stroke=BLUE if selected else LINE)
    text(x + 10, y + 8, label, FS_SMALL, BLUE if selected else MUTED,
         width=width - 20, align="center")
    return x + width + 8


def field(x: float, y: float, width: float, label: str, value: str, *,
          mono: bool = False, disabled: bool = False, h: float = 44) -> None:
    text(x, y, label.upper(), FS_SMALL, MUTED, font=FONT_MONO)
    rect(x, y + 22, width, h, bg=SUBTLE if disabled else SURFACE,
         stroke=LINE)
    text(x + 12, y + 34, value, FS_SMALL, FAINT if disabled else INK,
         font=FONT_MONO if mono else FONT_SANS, width=width - 24)


def begin_frame(index: int, name: str) -> tuple[int, int]:
    global current_frame, current_group
    x = ORIGIN_X + (index % COLS) * (FRAME_W + GAP_X)
    y = ORIGIN_Y + (index // COLS) * (FRAME_H + GAP_Y)
    fid = eid("frame")
    frame = base("frame", x, y, FRAME_W, FRAME_H, id=fid,
                 stroke="#bbb", frameId=None)
    frame["name"] = name
    elements.append(frame)
    current_frame = fid
    current_group = f"group-{index:02d}"
    rect(x, y, FRAME_W, FRAME_H, bg=SURFACE, stroke="#ced4da",
         radius=False, sw=2)
    return x, y


def end_frame() -> None:
    global current_frame, current_group
    current_frame = None
    current_group = None


def app_shell(fx: float, fy: float, active: str, *, title: str,
              subtitle: str = "") -> tuple[float, float, float]:
    sidebar_w = 210
    rect(fx, fy, sidebar_w, FRAME_H, bg=SIDEBAR, stroke=SIDEBAR,
         radius=False)
    text(fx + 22, fy + 22, "aevatar", FS_HEAD, INK, width=150)
    text(fx + 22, fy + 54, "AUTOMATION", FS_SMALL, MUTED,
         font=FONT_MONO, width=160)
    for idx, item in enumerate(("Chat", "Workflows", "Activities", "Agents", "Artifacts")):
        row_y = fy + 112 + idx * 42
        selected = item == active
        if selected:
            rect(fx + 10, row_y - 8, sidebar_w - 20, 34, bg="#e9ecef",
                 stroke="#e9ecef")
        text(fx + 24, row_y, item, FS_SMALL, INK if selected else MUTED,
             width=140)
    line(fx + 10, fy + FRAME_H - 82, sidebar_w - 20, 0, color=LINE)
    text(fx + 22, fy + FRAME_H - 60, "Ana · Acme", FS_SMALL, MUTED,
         width=150)
    content_x = fx + sidebar_w
    content_w = FRAME_W - sidebar_w
    rect(content_x, fy, content_w, 74, bg=SURFACE, stroke=LINE, radius=False)
    text(content_x + 28, fy + 16, title, FS_HEAD, INK, width=760)
    if subtitle:
        text(content_x + 28, fy + 46, subtitle, FS_SMALL, MUTED, width=900)
    return content_x, fy + 74, content_w


def annotation(x: float, y: float, number: str, title: str, body: str,
               *, color: str = PURPLE) -> None:
    dot(x, y, 28, color, bg=color)
    text(x, y + 5, number, FS_SMALL, SURFACE, width=28, align="center")
    text(x + 40, y, title.upper(), FS_SMALL, color, font=FONT_MONO,
         width=420)
    text(x + 40, y + 24, body, FS_SMALL, MUTED, width=430)


def schedule_summary(x: float, y: float, width: float, *, state: str | None = None) -> None:
    rect(x, y, width, 74, bg=BLUE_BG, stroke="#84adff")
    text(x + 16, y + 24,
         "Weekly Feedback Report · Published",
         FS_BODY, INK, width=width - 32)
    if state:
        badge(x + width - 92, y + 22, state, "ok" if state == "Enabled" else "muted")


def schedule_fields(
    x: float,
    y: float,
    width: float,
    *,
    show_preview: bool = False,
    name: str = "Weekly Feedback Report recurring work",
    repeat_label: str = "Mon · Tue · Wed · Thu · Fri",
    repeat_time: str = "09:00",
    time_zone: str = "Asia/Shanghai",
    human_summary: str = "Every weekday at 09:00",
    cron_expression: str = "0 9 * * 1-5",
    preview_copy: str = "next  Mon 24, Tue 25, Wed 26 Aug · see all",
) -> None:
    field(x, y, width, "Schedule name", name, h=54)
    y += 76
    text(x, y, "HOW OFTEN", FS_SMALL, MUTED, font=FONT_MONO)
    text(x, y + 40, "Repeat", FS_SMALL, MUTED, font=FONT_MONO, width=72)
    repeat_x = x + 84
    compact = width < 600
    repeat_w = width - 84 if compact else width * 0.46
    rect(repeat_x, y + 26, repeat_w, 46, bg=SURFACE, stroke=LINE)
    text(repeat_x + 12, y + 40, repeat_label, FS_SMALL, INK,
         width=repeat_w - 36)
    text(repeat_x + repeat_w - 20, y + 40, "▾", FS_SMALL, MUTED, width=12)
    if compact:
        time_x = x + 28
        time_y = y + 82
        text(x, y + 96, "at", FS_SMALL, MUTED, width=20)
        time_w = 104
        zone_x = x + 176
        text(x + 148, y + 96, "in", FS_SMALL, MUTED, width=20)
    else:
        time_x = repeat_x + repeat_w + 34
        time_y = y + 26
        text(time_x - 24, y + 40, "at", FS_SMALL, MUTED, width=20)
        time_w = max(88, width * 0.16)
        zone_x = time_x + time_w + 34
        text(zone_x - 22, y + 40, "in", FS_SMALL, MUTED, width=20)
    rect(time_x, time_y, time_w, 46, bg=SURFACE, stroke=LINE)
    text(time_x + 12, time_y + 14, repeat_time, FS_SMALL, INK, width=time_w - 24)
    zone_w = x + width - zone_x
    rect(zone_x, time_y, zone_w, 46, bg=SURFACE, stroke=LINE)
    text(zone_x + 12, time_y + 14, time_zone, FS_SMALL, INK, width=zone_w - 30)
    text(zone_x + zone_w - 20, time_y + 14, "▾", FS_SMALL, MUTED, width=12)

    if show_preview:
        preview_y = y + (146 if compact else 90)
        preview_h = 112 if compact else 92
        rect(x, preview_y, width, preview_h, bg=GREEN_BG, stroke=GREEN)
        text(x + 14, preview_y + 14, human_summary, FS_BODY, GREEN,
             width=width * 0.5)
        text(x + 14, preview_y + 50, preview_copy, FS_SMALL, INK,
             width=width * 0.8)
        cron_y = preview_y + (78 if compact else 50)
    else:
        text(x, y + 88, human_summary, FS_BODY, GREEN, width=width * 0.5)
        cron_y = y + (126 if compact else 122)
    text(x + (14 if compact else width * 0.58), cron_y,
         "write it as cron instead", FS_SMALL, BLUE, width=178)
    text(x + width - 110, cron_y, cron_expression, FS_SMALL, MUTED,
         font=FONT_MONO, width=100, align="right")

    input_y = preview_y + preview_h + 28 if show_preview else cron_y + 42
    field(x, input_y, width, "Run input (optional)", "No prompt",
          disabled=True, h=54)


def frame_workflows_list(index: int) -> None:
    fx, fy = begin_frame(index, "01 · Workflows — schedule management modal")
    cx, cy, cw = app_shell(fx, fy, "Workflows", title="Workflows",
                           subtitle="Published capabilities can run manually or on a schedule.")
    button(cx + cw - 170, fy + 18, 142, "Start a run", color=INK)
    field(cx + 28, cy + 24, 360, "Search", "Search workflows")
    px = cx + 412
    for label, selected in (("All  6", True), ("Published  4", False),
                            ("Runs on its own  3", False), ("Drafts  1", False)):
        px = chip(px, cy + 46, label, selected=selected)
    header_y = cy + 112
    widths = [460, 190, 270, 180, 180]
    rect(cx + 28, header_y, sum(widths), 42, bg=SUBTLE, stroke=LINE,
         radius=False)
    cursor = cx + 28
    for width, label in zip(widths, ("NAME", "LAST RUN", "STARTS ON ITS OWN", "STATE", "ACTIONS")):
        text(cursor + 14, header_y + 13, label, FS_SMALL, MUTED,
             font=FONT_MONO, width=width - 28)
        cursor += width
    rows = [
        ("Weekly Feedback Report", "reads #feedback, groups themes, posts a summary", "2h ago", "3 · 1 paused", "Published"),
        ("Customer escalation triage", "classifies urgent conversations and prepares replies", "4m ago", "2", "Draft"),
        ("Invoice chase", "finds overdue invoices and waits for approval", "42m ago", "1", "Published"),
        ("Nightly data sync", "pulls yesterday's orders into the warehouse", "3:04 AM", "1", "Published"),
    ]
    for row_index, (name, description, last, starts, state) in enumerate(rows):
        y = header_y + 42 + row_index * 116
        rect(cx + 28, y, sum(widths), 116, bg=SURFACE, stroke=LINE,
             radius=False)
        text(cx + 44, y + 16, name, FS_BODY, INK, width=420)
        text(cx + 44, y + 46, description, FS_SMALL, MUTED, width=420)
        text(cx + 488, y + 34, last, FS_SMALL, MUTED, width=150)
        text(cx + 678, y + 34, starts, FS_SMALL, INK, font=FONT_MONO, width=220)
        badge(cx + 948, y + 31, state, "ok" if state == "Published" else "muted")
        button(cx + 1140, y + 28, 90, "Schedules",
               color=BLUE if state == "Published" else MUTED,
               bg=BLUE_BG if state == "Published" else SUBTLE)
        button(cx + 1228, y + 28, 62, "Open")
    modal_x, modal_y, modal_w, modal_h = cx + 520, cy + 18, 760, 890
    rect(cx + 500, cy + 10, cw - 512, 910, bg="#eef2f6", stroke="#eef2f6", radius=False)
    rect(modal_x, modal_y, modal_w, modal_h, bg=SURFACE, stroke=INK, sw=2)
    text(modal_x + 28, modal_y + 26, "Schedules", FS_HEAD, INK, width=300)
    text(modal_x + 28, modal_y + 58,
         "Recurring runs owned by Weekly Feedback Report.", FS_SMALL, MUTED,
         width=520)
    button(modal_x + modal_w - 58, modal_y + 20, 32, "×", color=MUTED)
    rect(modal_x + 28, modal_y + 94, modal_w - 56, 72, bg=BLUE_BG, stroke="#84adff")
    text(modal_x + 48, modal_y + 112, "Weekly Feedback Report", FS_BODY, INK,
         width=360)
    text(modal_x + 48, modal_y + 142, "Published Workflow · 2 schedules", FS_SMALL,
         MUTED, width=360)
    button(modal_x + modal_w - 176, modal_y + 112, 120, "New schedule",
           primary=True, color=BLUE)
    text(modal_x + 28, modal_y + 194, "SCHEDULES", FS_SMALL, MUTED,
         font=FONT_MONO, width=200)
    schedule_rows = [
        ("Weekly feedback report", "Weekdays · 09:00 · Asia/Shanghai", "Enabled", "ok"),
        ("Monthly billing review", "Monthly · day 1 · Asia/Shanghai", "Paused", "muted"),
    ]
    for row_index, (name, summary, state, kind) in enumerate(schedule_rows):
        row_y = modal_y + 224 + row_index * 154
        rect(modal_x + 28, row_y, modal_w - 56, 132, bg=SURFACE, stroke=LINE)
        text(modal_x + 48, row_y + 18, name, FS_BODY, INK, width=330)
        badge(modal_x + modal_w - 132, row_y + 16, state, kind)
        text(modal_x + 48, row_y + 52, summary, FS_SMALL, MUTED, width=420)
        text(modal_x + 48, row_y + 90,
             "Next · Mon 24 Aug at 09:00" if state == "Enabled" else "No upcoming attempt",
             FS_SMALL, INK if state == "Enabled" else MUTED, width=360)
        text(modal_x + modal_w - 110, row_y + 88, "Open  →", FS_SMALL, BLUE,
             width=74, align="right")
    annotation(fx + FRAME_W + 42, fy + 160, "1", "Manage schedules where they are created",
                "The published Workflow row opens its Schedule management modal. Existing entries are readable and editable; New schedule starts the shared configure/review flow.")
    end_frame()


def frame_schedule_setup(index: int) -> None:
    fx, fy = begin_frame(index, "02 · Workflow — schedule setup panel")
    cx, cy, cw = app_shell(fx, fy, "Workflows", title="Weekly Feedback Report",
                           subtitle="Published · v7 · finance workspace")
    button(cx + cw - 528, fy + 18, 70, "Run", color=INK)
    button(cx + cw - 446, fy + 18, 104, "Schedules", primary=True, color=BLUE)
    button(cx + cw - 344, fy + 18, 94, "Add node", color=INK)
    button(cx + cw - 242, fy + 18, 88, "Save", color=INK)
    button(cx + cw - 146, fy + 18, 116, "Publish", color=INK)
    rect(cx + 20, cy + 18, 700, 898, bg="#f7f9fc", stroke=LINE)
    text(cx + 48, cy + 52, "Workflow canvas remains visible", FS_HEAD, INK, width=420)
    text(cx + 48, cy + 92,
         "The Schedule belongs to this Workflow and stays beside the canvas, not inside the graph.",
         FS_SMALL, MUTED, width=590)
    for n, (x, label, detail, color) in enumerate(((cx + 64, "Collect feedback", "Lark messages", GREEN),
                                                     (cx + 282, "Group themes", "AI task", BLUE),
                                                     (cx + 500, "Post summary", "Lark message", GREEN))):
        if n:
            line(x - 34, cy + 260, 30, 0, color="#94a3b8", arrowhead="arrow", sw=2)
        rect(x, cy + 210, 178, 112, bg=SURFACE, stroke=color, sw=2)
        rect(x, cy + 210, 4, 112, bg=color, stroke=color, radius=False)
        text(x + 18, cy + 232, label, FS_SMALL, INK, width=144)
        text(x + 18, cy + 262, detail, FS_SMALL, MUTED, width=144)
        badge(x + 18, cy + 288, "Ready", "ok")
    rect(cx + 748, cy + 18, 548, 898, bg=SURFACE, stroke=LINE)
    text(cx + 776, cy + 46, "Schedule", FS_HEAD, INK, width=300)
    schedule_summary(cx + 776, cy + 88, 492)
    schedule_fields(cx + 776, cy + 182, 492)
    button(cx + 776, cy + 836, 178, "Review schedule", primary=True, color=BLUE)
    button(cx + 966, cy + 836, 92, "Cancel", color=MUTED)
    annotation(fx + FRAME_W + 42, fy + 220, "2", "Only recurring work",
                "The public contract accepts a five-field cron and IANA timezone. POST /api/scopes/{scopeId}/workflows/{workflowId}/schedules/preview returns the next fires.")
    end_frame()


def frame_creation_review(index: int) -> None:
    fx, fy = begin_frame(index, "03 · Schedule — review before creation")
    cx, cy, cw = app_shell(fx, fy, "Workflows", title="Review schedule",
                           subtitle="Confirm the recurring setup before creating it.")
    rect(cx + 150, cy + 34, 1060, 806, bg=SURFACE, stroke=INK, sw=2)
    text(cx + 184, cy + 66, "Weekly Feedback Report recurring work", FS_HEAD, INK, width=680)
    badge(cx + 978, cy + 64, "Enabled after creation", "ok")
    line(cx + 184, cy + 112, 990, 0, color=LINE)
    field(cx + 184, cy + 144, 476, "Workflow", "Weekly Feedback Report", disabled=True)
    field(cx + 680, cy + 144, 494, "Schedule name", "Weekly Feedback Report recurring work")
    field(cx + 184, cy + 224, 476, "Repeat", "Every weekday at 09:00")
    field(cx + 680, cy + 224, 494, "Time zone", "Asia/Shanghai")
    field(cx + 184, cy + 304, 990, "Run input (optional)", "No prompt", disabled=True)
    text(cx + 184, cy + 392, "NEXT FIVE FIRE TIMES", FS_SMALL, MUTED,
         font=FONT_MONO, width=520)
    rect(cx + 184, cy + 424, 990, 176, bg=GREEN_BG, stroke=GREEN)
    fires = [
        ("1", "Mon 24 Aug · 09:00"),
        ("2", "Tue 25 Aug · 09:00"),
        ("3", "Wed 26 Aug · 09:00"),
        ("4", "Thu 27 Aug · 09:00"),
        ("5", "Fri 28 Aug · 09:00"),
    ]
    for fire_index, (number, label) in enumerate(fires):
        column = fire_index % 3
        row = fire_index // 3
        fire_x = cx + 210 + column * 312
        fire_y = cy + 452 + row * 62
        dot(fire_x, fire_y, 24, GREEN, bg=GREEN_BG)
        text(fire_x, fire_y + 4, number, FS_SMALL, GREEN, width=24, align="center")
        text(fire_x + 38, fire_y + 4, label, FS_SMALL, INK, width=230)
    text(cx + 184, cy + 674,
         "Create schedule sends the reviewed name, cron expression, time zone, enabled state, and optional run input.",
         FS_SMALL, INK, width=860)
    button(cx + 886, cy + 742, 170, "Create schedule", primary=True, color=BLUE)
    button(cx + 1068, cy + 742, 106, "Back", color=MUTED)
    annotation(fx + FRAME_W + 42, fy + 280, "3", "One Workflow-scoped create",
               "Create calls POST /api/scopes/{scopeId}/workflows/{workflowId}/schedules; service and owner resolution stay behind the API.")
    end_frame()


def frame_creation_pending(index: int) -> None:
    fx, fy = begin_frame(index, "04 · Schedule — creation pending")
    cx, cy, cw = app_shell(fx, fy, "Workflows", title="Workflows",
                           subtitle="Published capabilities can run manually or on a schedule.")
    field(cx + 28, cy + 24, 360, "Search", "Search workflows")
    header_y = cy + 112
    widths = [460, 190, 270, 180, 180]
    rect(cx + 28, header_y, sum(widths), 42, bg=SUBTLE, stroke=LINE, radius=False)
    cursor = cx + 28
    for width, label in zip(widths, ("NAME", "LAST RUN", "STARTS ON ITS OWN", "STATE", "ACTIONS")):
        text(cursor + 14, header_y + 13, label, FS_SMALL, MUTED,
             font=FONT_MONO, width=width - 28)
        cursor += width
    rect(cx + 28, header_y + 42, sum(widths), 116, bg=SURFACE, stroke=LINE, radius=False)
    text(cx + 44, header_y + 60, "Weekly Feedback Report", FS_BODY, INK, width=420)
    text(cx + 44, header_y + 90, "reads #feedback, groups themes, posts a summary", FS_SMALL, MUTED, width=420)
    text(cx + 488, header_y + 76, "2h ago", FS_SMALL, MUTED, width=150)
    text(cx + 678, header_y + 76, "3 · 1 paused", FS_SMALL, INK, font=FONT_MONO, width=220)
    badge(cx + 948, header_y + 72, "Published", "ok")
    button(cx + 1140, header_y + 70, 80, "Schedule", color=BLUE, bg=BLUE_BG)
    button(cx + 1228, header_y + 70, 62, "Open")

    toast_x, toast_y, toast_w, toast_h = cx + 680, cy + 28, 560, 118
    rect(toast_x, toast_y, toast_w, toast_h, bg=GREEN_BG, stroke=GREEN)
    text(toast_x + 24, toast_y + 22, "Schedule request accepted", FS_HEAD, GREEN,
         width=toast_w - 48)
    text(toast_x + 24, toast_y + 62,
         "It will appear in this Workflow's schedule list shortly.",
         FS_SMALL, INK, width=toast_w - 48)
    annotation(fx + FRAME_W + 42, fy + 240, "4", "Toast, then keep working",
                "202 Accepted is acknowledged without a second modal; the Workflow-scoped list continues observing the new Schedule.")
    end_frame()


def workflow_canvas(cx: float, cy: float) -> None:
    rect(cx + 20, cy + 18, 700, 898, bg="#f7f9fc", stroke=LINE)
    text(cx + 48, cy + 52, "Workflow canvas remains visible", FS_HEAD, INK, width=420)
    text(cx + 48, cy + 92, "Schedule belongs to this published Workflow.", FS_SMALL, MUTED, width=590)
    for n, (x, label, detail, node_color) in enumerate(((cx + 64, "Collect feedback", "Lark messages", GREEN),
                                                         (cx + 282, "Group themes", "AI task", BLUE),
                                                         (cx + 500, "Post summary", "Lark message", GREEN))):
        if n:
            line(x - 34, cy + 260, 30, 0, color="#94a3b8", arrowhead="arrow", sw=2)
        rect(x, cy + 210, 178, 112, bg=SURFACE, stroke=node_color, sw=2)
        rect(x, cy + 210, 4, 112, bg=node_color, stroke=node_color, radius=False)
        text(x + 18, cy + 232, label, FS_SMALL, INK, width=144)
        text(x + 18, cy + 262, detail, FS_SMALL, MUTED, width=144)
        badge(x + 18, cy + 288, "Ready", "ok")


def frame_schedule_detail(index: int) -> None:
    fx, fy = begin_frame(index, "05 · Workflow — schedule overview")
    cx, cy, cw = app_shell(fx, fy, "Workflows", title="Weekly Feedback Report",
                           subtitle="Published · v7 · Schedule overview")
    button(cx + cw - 528, fy + 18, 70, "Run", color=INK)
    button(cx + cw - 446, fy + 18, 94, "Schedule", primary=True, color=BLUE)
    button(cx + cw - 344, fy + 18, 94, "Add node", color=INK)
    button(cx + cw - 242, fy + 18, 88, "Save", color=INK)
    button(cx + cw - 146, fy + 18, 116, "Publish", color=INK)
    workflow_canvas(cx, cy)
    rect(cx + 748, cy + 18, 548, 898, bg=SURFACE, stroke=LINE)
    text(cx + 768, cy + 44, "←", FS_HEAD, MUTED, width=24)
    text(cx + 806, cy + 42, "Morning digest", FS_HEAD, INK, width=310)
    text(cx + 806, cy + 74, "Weekly Feedback Report", FS_SMALL, MUTED, width=310)
    text(cx + 1242, cy + 44, "×", FS_HEAD, MUTED, width=24, align="right")
    line(cx + 748, cy + 108, 548, 0, color=LINE)
    text(cx + 776, cy + 128, "Overview", FS_SMALL, BLUE, width=84, align="center")
    text(cx + 874, cy + 128, "History", FS_SMALL, MUTED, width=84, align="center")
    line(cx + 776, cy + 158, 84, 0, color=BLUE, sw=2)

    badge(cx + 776, cy + 188, "Active", "ok")
    text(cx + 776, cy + 230, "Every weekday at 09:00", FS_HEAD, INK, width=360)
    text(cx + 776, cy + 264, "Asia/Shanghai", FS_SMALL, MUTED, width=260)
    button(cx + 776, cy + 304, 108, "Run now", primary=True, color=BLUE)
    button(cx + 894, cy + 304, 134, "Edit schedule", color=INK)
    button(cx + 1038, cy + 304, 84, "More  ▾", color=MUTED)

    line(cx + 776, cy + 372, 492, 0, color=LINE)
    text(cx + 776, cy + 396, "NEXT SCHEDULED", FS_SMALL, MUTED, font=FONT_MONO, width=212)
    text(cx + 1032, cy + 396, "LAST ATTEMPT", FS_SMALL, MUTED, font=FONT_MONO, width=212)
    text(cx + 776, cy + 426, "Tue 25 Aug · 09:00", FS_BODY, INK, width=220)
    text(cx + 1032, cy + 426, "Mon 24 Aug · Succeeded", FS_BODY, INK, width=226)
    line(cx + 776, cy + 468, 492, 0, color=LINE)
    text(cx + 776, cy + 492, "TOTAL ATTEMPTS", FS_SMALL, MUTED, font=FONT_MONO, width=212)
    text(cx + 1032, cy + 492, "FAILED ATTEMPTS", FS_SMALL, MUTED, font=FONT_MONO, width=212)
    text(cx + 776, cy + 522, "18", FS_BODY, INK, width=220)
    text(cx + 1032, cy + 522, "1", FS_BODY, RED, width=226)
    line(cx + 776, cy + 564, 492, 0, color=LINE)
    text(cx + 776, cy + 590, "RUN INPUT", FS_SMALL, MUTED, font=FONT_MONO, width=180)
    text(cx + 776, cy + 620, "Summarize new feedback.", FS_BODY, INK, width=492)
    line(cx + 776, cy + 664, 492, 0, color=LINE)
    text(cx + 776, cy + 694, "Advanced details", FS_BODY, INK, width=220)
    text(cx + 1240, cy + 694, "›", FS_BODY, MUTED, width=20, align="right")
    annotation(fx + FRAME_W + 42, fy + 300, "5", "Overview answers what is configured",
                "The default tab keeps recurrence and observed state readable. Run and Edit stay direct; Pause and Delete move under More.")
    end_frame()


def frame_schedule_history(index: int) -> None:
    fx, fy = begin_frame(index, "06 · Workflow — schedule history")
    cx, cy, cw = app_shell(fx, fy, "Workflows", title="Weekly Feedback Report",
                           subtitle="Published · v7 · Schedule history")
    button(cx + cw - 528, fy + 18, 70, "Run", color=INK)
    button(cx + cw - 446, fy + 18, 94, "Schedule", primary=True, color=BLUE)
    button(cx + cw - 344, fy + 18, 94, "Add node", color=INK)
    button(cx + cw - 242, fy + 18, 88, "Save", color=INK)
    button(cx + cw - 146, fy + 18, 116, "Publish", color=INK)
    workflow_canvas(cx, cy)
    rect(cx + 748, cy + 18, 548, 898, bg=SURFACE, stroke=LINE)
    text(cx + 768, cy + 44, "←", FS_HEAD, MUTED, width=24)
    text(cx + 806, cy + 42, "Morning digest", FS_HEAD, INK, width=310)
    text(cx + 806, cy + 74, "Weekly Feedback Report", FS_SMALL, MUTED, width=310)
    text(cx + 1242, cy + 44, "×", FS_HEAD, MUTED, width=24, align="right")
    line(cx + 748, cy + 108, 548, 0, color=LINE)
    text(cx + 776, cy + 128, "Overview", FS_SMALL, MUTED, width=84, align="center")
    text(cx + 874, cy + 128, "History", FS_SMALL, BLUE, width=84, align="center")
    line(cx + 874, cy + 158, 84, 0, color=BLUE, sw=2)

    text(cx + 776, cy + 194, "Recent attempts", FS_HEAD, INK, width=260)
    text(cx + 1054, cy + 200, "View related runs in Activity  →", FS_SMALL, BLUE,
         width=214, align="right")
    text(cx + 776, cy + 230,
         "Schedule attempts can fail before a Workflow Run exists.", FS_SMALL,
         MUTED, width=470)

    table_y = cy + 278
    widths = [133, 69, 93, 143, 54]
    rect(cx + 776, table_y, sum(widths), 42, bg=SUBTLE, stroke=LINE, radius=False)
    cursor = cx + 776
    for width, label in zip(widths, ("SCHEDULED", "SOURCE", "RESULT", "COMPLETED", "ACTION")):
        padding = 4 if label == "ACTION" else 8
        size = 12 if label == "ACTION" else FS_SMALL
        text(cursor + padding, table_y + 13, label, size, MUTED,
             font=FONT_MONO, width=width - 2 * padding)
        cursor += width

    rows = [
        ("24 Aug · 09:00", "Scheduled", "Failed", "09:00", False),
        ("21 Aug · 09:00", "Scheduled", "Run started", "09:01", True),
        ("20 Aug · 15:42", "Manual", "Run started", "15:43", True),
    ]
    row_offsets = [42, 194, 270]
    for row_index, (scheduled, source, result, completed, has_action) in enumerate(rows):
        row_y = table_y + row_offsets[row_index]
        row_h = 152 if row_index == 0 else 76
        rect(cx + 776, row_y, sum(widths), row_h, bg=SURFACE, stroke=LINE,
             radius=False)
        cursor = cx + 776
        for width, value in zip(widths[:4], (scheduled, source, result, completed)):
            color = RED if value == "Failed" else GREEN if value == "Run started" else INK
            text(cursor + 4, row_y + 18, value, 12, color,
                 width=width - 8)
            cursor += width
        if has_action:
            text(cursor, row_y + 16, "→", FS_BODY, BLUE,
                 width=widths[-1], align="center")
        if row_index == 0:
            text(cx + 784, row_y + 54,
                 "The scheduled attempt could not start the Workflow.", FS_SMALL,
                 INK, width=462)
            text(cx + 784, row_y + 88, "▾  Technical details", FS_SMALL, BLUE,
                 width=190)
            rect(cx + 784, row_y + 114, 476, 28, bg=SUBTLE, stroke=LINE)
            text(cx + 794, row_y + 121,
                 "Capability admission rejected the scheduled request.", FS_SMALL,
                 MUTED, font=FONT_MONO, width=452)
    annotation(fx + FRAME_W + 42, fy + 300, "6", "History is attempts, Activity is Runs",
                "recentFires renders as a compact bounded list. Technical errors stay disclosed; Activity receives exact Workflow and Schedule filters.")
    end_frame()


def frame_schedule_edit(index: int) -> None:
    fx, fy = begin_frame(index, "07 · Workflow — change schedule")
    cx, cy, cw = app_shell(fx, fy, "Workflows", title="Weekly Feedback Report",
                           subtitle="Published · v7 · Change schedule")
    button(cx + cw - 528, fy + 18, 70, "Run", color=INK)
    button(cx + cw - 446, fy + 18, 94, "Schedule", primary=True, color=BLUE)
    button(cx + cw - 344, fy + 18, 94, "Add node", color=INK)
    button(cx + cw - 242, fy + 18, 88, "Save", color=INK)
    button(cx + cw - 146, fy + 18, 116, "Publish", color=INK)
    workflow_canvas(cx, cy)
    rect(cx + 748, cy + 18, 548, 898, bg=SURFACE, stroke=LINE)
    text(cx + 768, cy + 44, "←", FS_HEAD, MUTED, width=24)
    text(cx + 806, cy + 42, "Weekly review", FS_HEAD, INK, width=310)
    text(cx + 806, cy + 74, "Weekly Feedback Report", FS_SMALL, MUTED, width=310)
    text(cx + 1242, cy + 44, "×", FS_HEAD, MUTED, width=24, align="right")
    line(cx + 748, cy + 108, 548, 0, color=LINE)
    text(cx + 776, cy + 132, "Edit schedule", FS_HEAD, INK, width=330)
    schedule_fields(
        cx + 776,
        cy + 184,
        492,
        show_preview=True,
        name="Weekly review",
        repeat_label="Mon",
        repeat_time="10:00",
        time_zone="Asia/Shanghai",
        human_summary="Every Monday at 10:00",
        cron_expression="0 10 * * 1",
        preview_copy="next  Mon 24 Aug, Mon 31 Aug, Mon 7 Sep · see all",
    )
    button(cx + 776, cy + 824, 136, "Save changes", primary=True, color=BLUE)
    button(cx + 924, cy + 824, 92, "Cancel", color=MUTED)
    annotation(fx + FRAME_W + 42, fy + 260, "7", "Edit is a temporary mode",
                "Cancel returns to Overview. PUT sends the edited fields and preserves the observed enabled state; it never enables a paused Schedule by accident.")
    end_frame()


text(ORIGIN_X, 70, "Aevatar — Published workflow schedules", FS_TITLE, INK, width=1040)
text(ORIGIN_X, 126, "One Workflow, one recurring resource model, seven reviewable screens.", FS_HEAD, BLUE, width=1060)
text(ORIGIN_X, 178,
     "Schedule is configured, previewed, created, observed, and managed through the Workflow-scoped API.",
     FS_BODY, MUTED, width=1180)
text(ORIGIN_X, 232,
     "Each frame renders to its own 1440 × 900 PNG. Notes stay outside the screen frames.",
     FS_SMALL, MUTED, width=900)
line(ORIGIN_X, 274, 1150, 0, color=LINE)

frame_workflows_list(0)
frame_schedule_setup(1)
frame_creation_review(2)
frame_creation_pending(3)
frame_schedule_detail(4)
frame_schedule_history(5)
frame_schedule_edit(6)

document = {
    "type": "excalidraw",
    "version": 2,
    "source": "https://excalidraw.com",
    "elements": elements,
    "appState": {
        "gridSize": None,
        "viewBackgroundColor": "#ffffff",
        "currentItemStrokeColor": INK,
        "currentItemBackgroundColor": "transparent",
    },
    "files": {},
}
OUT.write_text(json.dumps(document, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
print(f"wrote {OUT} ({len(elements)} elements)")
