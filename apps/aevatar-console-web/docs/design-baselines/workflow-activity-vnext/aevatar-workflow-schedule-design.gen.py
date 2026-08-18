#!/usr/bin/env python3
"""Generate the schedule-only reference board for published Workflows.

The board follows the schedule path in the supplied wireframe while keeping
the Aevatar product boundary explicit: a published Team member owns a
recurring automation, and Activity records the resulting runs.
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


def schedule_summary(x: float, y: float, width: float) -> None:
    rect(x, y, width, 74, bg=BLUE_BG, stroke="#84adff")
    text(x + 16, y + 14, "TEAM MEMBER AUTOMATION", FS_SMALL, BLUE,
         font=FONT_MONO, width=width - 32)
    text(x + 16, y + 38,
         "Weekly Feedback Report · member m-feedback · published v7",
         FS_BODY, INK, width=width - 32)


def schedule_fields(x: float, y: float, width: float, *, show_preview: bool = False) -> None:
    text(x, y, "HOW OFTEN", FS_SMALL, MUTED, font=FONT_MONO)
    text(x, y + 40, "Repeat", FS_SMALL, MUTED, font=FONT_MONO, width=72)
    repeat_x = x + 84
    compact = width < 600
    repeat_w = width - 84 if compact else width * 0.46
    rect(repeat_x, y + 26, repeat_w, 46, bg=SURFACE, stroke=LINE)
    text(repeat_x + 12, y + 40, "Mon · Tue · Wed · Thu · Fri", FS_SMALL, INK,
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
    text(time_x + 12, time_y + 14, "09:00", FS_SMALL, INK, width=time_w - 24)
    zone_w = x + width - zone_x
    rect(zone_x, time_y, zone_w, 46, bg=SURFACE, stroke=LINE)
    text(zone_x + 12, time_y + 14, "Asia/Singapore", FS_SMALL, INK, width=zone_w - 30)
    text(zone_x + zone_w - 20, time_y + 14, "▾", FS_SMALL, MUTED, width=12)

    preview_y = y + (146 if compact else 90)
    preview_h = 112 if compact else 92
    rect(x, preview_y, width, preview_h, bg=GREEN_BG, stroke=GREEN)
    text(x + 14, preview_y + 14, "Every weekday at 09:00", FS_BODY, GREEN,
         width=width * 0.5)
    text(x + width - 210, preview_y + 16,
         "SERVER RESPONSE" if show_preview else "SERVER PREVIEW REQUIRED",
         FS_SMALL, MUTED, font=FONT_MONO, width=196, align="right")
    text(x + 14, preview_y + 50,
         "next  Mon 24, Tue 25, Wed 26 Aug · see all" if show_preview else
         "Next five fires appear after review.",
         FS_SMALL, INK if show_preview else MUTED, width=width * 0.56)
    cron_y = preview_y + (78 if compact else 50)
    text(x + (14 if compact else width * 0.58), cron_y,
         "write it as cron instead", FS_SMALL, BLUE, width=178)
    text(x + width - 110, cron_y, "0 9 * * 1-5", FS_SMALL, MUTED,
         font=FONT_MONO, width=100, align="right")

    needs_y = preview_y + preview_h + 28
    text(x, needs_y, "WHAT IT NEEDS", FS_SMALL, MUTED, font=FONT_MONO)
    text(x + 142, needs_y, "filled fresh at every fire", FS_SMALL, MUTED,
         width=220)
    field(x, needs_y + 28, width, "Starting prompt (optional)", "No prompt",
          disabled=True, h=54)

    outcome_y = needs_y + 120
    text(x, outcome_y, "WHAT WILL HAPPEN", FS_SMALL, MUTED, font=FONT_MONO)
    rect(x, outcome_y + 22, width, 78, bg=SUBTLE, stroke=LINE)
    text(x + 14, outcome_y + 35,
         "Runs every weekday at 09:00, as you, until somebody pauses it.\n"
         "The published revision is pinned from the Workflow context above.",
         FS_SMALL, INK, width=width - 28)


def frame_workflows_list(index: int) -> None:
    fx, fy = begin_frame(index, "01 · Workflows — quick schedule modal")
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
        button(cx + 1140, y + 28, 80, "Schedule",
               color=BLUE if state == "Published" else MUTED,
               bg=BLUE_BG if state == "Published" else SUBTLE)
        button(cx + 1228, y + 28, 62, "Open")
    modal_x, modal_y, modal_w, modal_h = cx + 520, cy + 18, 760, 890
    rect(cx + 500, cy + 10, cw - 512, 910, bg="#eef2f6", stroke="#eef2f6", radius=False)
    rect(modal_x, modal_y, modal_w, modal_h, bg=SURFACE, stroke=INK, sw=2)
    text(modal_x + 28, modal_y + 26, "New schedule", FS_HEAD, INK, width=300)
    text(modal_x + 28, modal_y + 58, "Configure recurring work without leaving Workflows.", FS_SMALL, MUTED, width=520)
    button(modal_x + modal_w - 58, modal_y + 20, 32, "×", color=MUTED)
    schedule_summary(modal_x + 28, modal_y + 92, modal_w - 56)
    schedule_fields(modal_x + 28, modal_y + 186, modal_w - 56)
    button(modal_x + modal_w - 230, modal_y + 824, 202, "Review authorization", primary=True, color=BLUE)
    button(modal_x + modal_w - 324, modal_y + 824, 84, "Cancel", color=MUTED)
    annotation(fx + FRAME_W + 42, fy + 160, "1", "Quick create, no navigation",
                "The published Workflow row opens this modal directly. POST /api/schedules/preview provides the fires; open the editor later for management.")
    end_frame()


def frame_schedule_setup(index: int) -> None:
    fx, fy = begin_frame(index, "02 · Schedule — configure recurring work")
    cx, cy, cw = app_shell(fx, fy, "Workflows", title="Weekly Feedback Report",
                           subtitle="Published · v7 · finance workspace")
    button(cx + cw - 528, fy + 18, 70, "Run", color=INK)
    button(cx + cw - 446, fy + 18, 94, "Schedule", primary=True, color=BLUE)
    button(cx + cw - 344, fy + 18, 94, "Add node", color=INK)
    button(cx + cw - 242, fy + 18, 88, "Save", color=INK)
    button(cx + cw - 146, fy + 18, 116, "Publish", color=INK)
    rect(cx + 20, cy + 18, 700, 898, bg="#f7f9fc", stroke=LINE)
    text(cx + 48, cy + 52, "Workflow canvas remains visible", FS_HEAD, INK, width=420)
    text(cx + 48, cy + 92,
         "The schedule is an owner-aware automation beside the canvas, not a graph node.",
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
    button(cx + 776, cy + 836, 178, "Review authorization", primary=True, color=BLUE)
    button(cx + 966, cy + 836, 92, "Cancel", color=MUTED)
    annotation(fx + FRAME_W + 42, fy + 220, "2", "Only recurring work",
                "The public contract accepts a five-field cron and IANA timezone. POST /api/schedules/preview returns the next fires.")
    end_frame()


def frame_authorization(index: int) -> None:
    fx, fy = begin_frame(index, "03 · Schedule — review authorization")
    cx, cy, cw = app_shell(fx, fy, "Workflows", title="Review before it is created",
                           subtitle="The schedule stores durable authority for each future fire.")
    rect(cx + 150, cy + 34, 1060, 780, bg=SURFACE, stroke=INK, sw=2)
    text(cx + 184, cy + 66, "Weekly Feedback Report will act on your behalf", FS_HEAD, INK, width=680)
    text(cx + 184, cy + 102, "every weekday at 09:00 · Asia/Singapore · published v7", FS_SMALL, MUTED, width=680)
    line(cx + 184, cy + 142, 990, 0, color=LINE)
    text(cx + 184, cy + 174, "EXACT SERVER-RETURNED AUTHORIZATION PLAN", FS_SMALL, MUTED, font=FONT_MONO, width=520)
    permissions = [
        ("Lark · Acme", "service nyx-service-lark-acme", "exact service", "ok"),
        ("Node IDs", "lark.messages.read · lark.messages.write", "exact nodes", "ok"),
        ("Owner LLM", "NyxID Gateway · gpt-4o-mini", "reviewed", "ok"),
        ("Scopes", "read · proxy", "restricted", "ok"),
    ]
    for idx, (name, detail, state, kind) in enumerate(permissions):
        y = cy + 226 + idx * 62
        dot(cx + 190, y + 6, 12, BLUE if kind == "ok" else AMBER if kind == "wait" else RED)
        text(cx + 220, y, name, FS_BODY, INK, width=190)
        text(cx + 408, y, detail, FS_SMALL, MUTED, width=360)
        badge(cx + 818, y - 4, state, kind)
    text(cx + 184, cy + 494, "HOW IT WILL SIGN IN AS YOU", FS_SMALL, MUTED, font=FONT_MONO, width=420)
    text(cx + 184, cy + 522, "DEDICATED AGENT KEY", FS_SMALL, BLUE, font=FONT_MONO, width=360)
    text(cx + 184, cy + 552,
         "A dedicated schedule credential is stored in Aevatar's vault and reused for each fire.",
         FS_SMALL, INK, width=860)
    text(cx + 184, cy + 580,
         "The browser never receives the raw key. Delete revokes it; pause keeps it available.",
         FS_SMALL, MUTED, width=860)
    badge(cx + 184, cy + 628, "Credential plan ready", "ok")
    badge(cx + 354, cy + 628, "Policy team-automation-v3", "info")
    text(cx + 184, cy + 660, "DIGEST sha256:feedback-v7-permissions", FS_SMALL, MUTED, font=FONT_MONO, width=560)
    text(cx + 184, cy + 698, "If the workflow, revision, or grant changes, review is required again.",
         FS_SMALL, MUTED, width=760)
    button(cx + 850, cy + 734, 190, "Confirm and create", primary=True, color=BLUE)
    button(cx + 1052, cy + 734, 88, "Back", color=MUTED)
    annotation(fx + FRAME_W + 42, fy + 280, "3", "Authorization is server-owned",
                "Confirm binds permissionDigest + policyVersion; create stays pending until owner state is observed.")
    end_frame()


def frame_scheduled_runs(index: int) -> None:
    fx, fy = begin_frame(index, "04 · Activity — scheduled runs")
    cx, cy, cw = app_shell(fx, fy, "Activities", title="Activity",
                           subtitle="Every row is one immutable Run. Schedule is only a Run source.")
    chip(cx + 28, cy + 22, "Runs 847")
    chip(cx + 126, cy + 22, "Scheduled 7", selected=True)
    button(cx + cw - 170, fy + 18, 142, "Start a run", color=INK)
    text(cx + 28, cy + 92, "SOURCE: SCHEDULE", FS_SMALL, PURPLE, font=FONT_MONO, width=320)
    text(cx + 28, cy + 120, "Activity records dispatched Runs; recurring configuration stays in Workflow > Schedule.", FS_SMALL, MUTED, width=850)
    table_x = cx + 28
    table_y = cy + 164
    widths = [150, 260, 220, 170, 330, 150]
    rect(table_x, table_y, sum(widths), 42, bg=SUBTLE, stroke=LINE, radius=False)
    cursor = table_x
    for width, label in zip(widths, ("RUN ID", "WORKFLOW", "SOURCE", "STARTED", "RESULT", "STATUS")):
        text(cursor + 14, table_y + 13, label, FS_SMALL, MUTED, font=FONT_MONO, width=width - 28)
        cursor += width
    rows = [
        ("R-1041", "Invoice follow-up", "Schedule · weekday", "42m ago", "Approve 4 messages", "Needs you", "wait"),
        ("R-1040", "Weekly Feedback Report", "Schedule · weekly", "2h ago", "Posted 1 summary", "Succeeded", "ok"),
        ("R-1038", "Nightly order sync", "Schedule · nightly", "3:04 AM", "Warehouse unavailable", "Failed", "fail"),
        ("R-1028", "Invoice follow-up", "Schedule · weekday", "2d ago", "Sent 6 reminders", "Succeeded", "ok"),
    ]
    for idx, (run_id, workflow, source, started, result, state, kind) in enumerate(rows):
        y = table_y + 42 + idx * 104
        rect(table_x, y, sum(widths), 104, bg=SURFACE, stroke=LINE, radius=False)
        text(table_x + 16, y + 30, run_id, FS_SMALL, INK, font=FONT_MONO, width=120)
        text(table_x + 166, y + 30, workflow, FS_SMALL, INK, width=230)
        text(table_x + 426, y + 30, source, FS_SMALL, MUTED, width=190)
        text(table_x + 646, y + 30, started, FS_SMALL, MUTED, width=140)
        text(table_x + 816, y + 30, result, FS_SMALL, RED if kind == "fail" else INK, width=290)
        badge(table_x + 1160, y + 24, state, kind)
        text(table_x + 1230, y + 30, "›", FS_HEAD, MUTED, width=28, align="center")
    text(cx + 28, cy + 748, "Open Workflow > Schedule to manage recurring work. Activity never owns Schedule definitions.", FS_SMALL, MUTED, width=1140)
    annotation(fx + FRAME_W + 42, fy + 190, "4", "Run evidence only",
                "Activity owns immutable Runs; Schedule is one source filter and never becomes a second configuration surface.")
    end_frame()


def workflow_canvas(cx: float, cy: float) -> None:
    rect(cx + 20, cy + 18, 700, 898, bg="#f7f9fc", stroke=LINE)
    text(cx + 48, cy + 52, "Workflow canvas remains visible", FS_HEAD, INK, width=420)
    text(cx + 48, cy + 92, "Schedule belongs to this published Workflow, not to Activity.", FS_SMALL, MUTED, width=590)
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
    fx, fy = begin_frame(index, "05 · Workflow — schedule detail")
    cx, cy, cw = app_shell(fx, fy, "Workflows", title="Weekly Feedback Report",
                           subtitle="Published · v7 · Schedule details")
    button(cx + cw - 528, fy + 18, 70, "Run", color=INK)
    button(cx + cw - 446, fy + 18, 94, "Schedule", primary=True, color=BLUE)
    button(cx + cw - 344, fy + 18, 94, "Add node", color=INK)
    button(cx + cw - 242, fy + 18, 88, "Save", color=INK)
    button(cx + cw - 146, fy + 18, 116, "Publish", color=INK)
    workflow_canvas(cx, cy)
    rect(cx + 748, cy + 18, 548, 898, bg=SURFACE, stroke=LINE)
    text(cx + 776, cy + 46, "Schedule details", FS_HEAD, INK, width=330)
    text(cx + 776, cy + 80, "Managed from this Workflow", FS_SMALL, BLUE, font=FONT_MONO, width=420)
    schedule_summary(cx + 776, cy + 112, 492)
    rect(cx + 776, cy + 204, 492, 136, bg=RED_BG, stroke="#fda29b")
    text(cx + 796, cy + 224, "NEEDS ATTENTION", FS_SMALL, RED, font=FONT_MONO, width=280)
    text(cx + 796, cy + 252, "Credential expired · owner review required", FS_SMALL, INK, width=440)
    text(cx + 796, cy + 278, "Future fires will not start until authorization is reviewed.", FS_SMALL, MUTED, width=440)
    button(cx + 796, cy + 300, 180, "Review and reauthorize", primary=True, color=BLUE)
    field(cx + 776, cy + 368, 240, "Cadence", "Every weekday 09:00")
    field(cx + 1032, cy + 368, 236, "Time zone", "Asia/Shanghai")
    field(cx + 776, cy + 446, 240, "Next fire", "Will not run", disabled=True)
    field(cx + 1032, cy + 446, 236, "Last run", "Failed yesterday")
    field(cx + 776, cy + 524, 492, "Pinned revision", "Published · v7", disabled=True)
    text(cx + 776, cy + 616, "Activity contains immutable Runs only.", FS_SMALL, MUTED, width=492)
    button(cx + 776, cy + 656, 160, "View scheduled runs", color=BLUE, bg=BLUE_BG)
    button(cx + 776, cy + 824, 100, "Run now", color=INK)
    button(cx + 884, cy + 824, 98, "Change", color=BLUE, bg=BLUE_BG)
    button(cx + 990, cy + 824, 84, "Pause", color=INK)
    button(cx + 1082, cy + 824, 78, "Delete", color=RED)
    annotation(fx + FRAME_W + 42, fy + 320, "5", "Workflow owns the schedule",
                "Schedule detail and lifecycle actions stay beside the Workflow; Activity is reached only for Run evidence.")
    end_frame()


def frame_schedule_edit(index: int) -> None:
    fx, fy = begin_frame(index, "06 · Workflow — change schedule")
    cx, cy, cw = app_shell(fx, fy, "Workflows", title="Weekly Feedback Report",
                           subtitle="Published · v7 · Change schedule")
    button(cx + cw - 528, fy + 18, 70, "Run", color=INK)
    button(cx + cw - 446, fy + 18, 94, "Schedule", primary=True, color=BLUE)
    button(cx + cw - 344, fy + 18, 94, "Add node", color=INK)
    button(cx + cw - 242, fy + 18, 88, "Save", color=INK)
    button(cx + cw - 146, fy + 18, 116, "Publish", color=INK)
    workflow_canvas(cx, cy)
    rect(cx + 748, cy + 18, 548, 898, bg=SURFACE, stroke=LINE)
    text(cx + 776, cy + 46, "Change schedule", FS_HEAD, INK, width=330)
    text(cx + 776, cy + 80, "Managed from this Workflow", FS_SMALL, BLUE, font=FONT_MONO, width=420)
    schedule_summary(cx + 776, cy + 112, 492)
    schedule_fields(cx + 776, cy + 204, 492, show_preview=True)
    button(cx + 776, cy + 824, 136, "Save changes", primary=True, color=BLUE)
    button(cx + 924, cy + 824, 92, "Cancel", color=MUTED)
    annotation(fx + FRAME_W + 42, fy + 260, "6", "Edit in Workflow",
                "Cadence and prompt are editable here. Target identity and pinned revision remain read-only facts.")
    end_frame()


def frame_cadence_spec(index: int) -> None:
    fx, fy = begin_frame(index, "SPEC · cadence control")
    text(fx + 58, fy + 38, "Tick the days. The sentence writes itself.", FS_HEAD, INK, width=640)
    text(fx + 58, fy + 84, "The compact builder is a safe way into cron; it never rounds an expression it cannot represent.", FS_SMALL, MUTED, width=900)
    rect(fx + 58, fy + 142, 650, 84, bg=SURFACE, stroke=LINE)
    text(fx + 80, fy + 160, "REPEAT", FS_SMALL, MUTED, font=FONT_MONO, width=120)
    text(fx + 188, fy + 158, "Mon  Tue  Wed  Thu  Fri", FS_SMALL, INK, width=260)
    text(fx + 462, fy + 158, "09:00", FS_SMALL, INK, font=FONT_MONO, width=80)
    text(fx + 562, fy + 158, "Asia/Singapore", FS_SMALL, INK, width=130)
    text(fx + 80, fy + 190, "Every weekday at 09:00", FS_BODY, BLUE, width=300)
    text(fx + 58, fy + 272, "WHAT IS TICKED", FS_SMALL, MUTED, font=FONT_MONO, width=260)
    text(fx + 530, fy + 272, "WHAT IT READS BACK", FS_SMALL, MUTED, font=FONT_MONO, width=260)
    rows = [("Monday to Friday", "Every weekday"), ("Saturday and Sunday", "Every weekend"), ("one day", "Every Monday"), ("anything else", "Every Mon, Wed and Fri"), ("nothing", "Pick at least one day")]
    for idx, (left, right) in enumerate(rows):
        y = fy + 318 + idx * 50
        text(fx + 58, y, left, FS_BODY, INK, width=360)
        text(fx + 530, y, right, FS_BODY, BLUE if idx < 4 else RED, width=460)
        line(fx + 58, y + 34, 980, 0, color="#edf0f3")
    text(fx + 58, fy + 602, "Every hour", FS_BODY, INK, width=180)
    text(fx + 282, fy + 602, "Every month", FS_BODY, INK, width=180)
    text(fx + 506, fy + 602, "Every year", FS_BODY, INK, width=180)
    text(fx + 730, fy + 602, "Something else…", FS_BODY, BLUE, width=220)
    text(fx + 58, fy + 660, "A cron the builder cannot say stays as cron. Reopening */15 9-17 * * 1-5 keeps the exact expression and its plain-English line.", FS_SMALL, MUTED, width=1080)
    annotation(fx + FRAME_W + 42, fy + 214, "7", "No lossy conversion",
                "The builder is a convenience; the stored five-field expression remains authoritative.")
    end_frame()


def frame_row_states(index: int) -> None:
    fx, fy = begin_frame(index, "SPEC · Workflow Schedule row states")
    text(fx + 58, fy + 38, "Seven states. Every Workflow Schedule row names the next action.", FS_HEAD, INK, width=900)
    text(fx + 58, fy + 84, "A schedule that cannot fire is still visible in Workflow, with the observed reason and lifecycle action.", FS_SMALL, MUTED, width=980)
    rows = [("creating", "every day 03:00", "—", "it cannot fire until authorization finishes", "", "wait"),
            ("active", "every day 03:00", "✓ 1h ago", "credential active · next fire observed", "Pause", "ok"),
            ("paused", "every day 03:00", "✓ 3d ago", "paused by Ana · 2d ago", "Resume", "muted"),
            ("needs attention", "every Friday 17:00", "✕ 7d ago", "authorization expired · owner review required", "Open", "fail"),
            ("dispatched late", "every day 03:00", "◷ 03:00 → 07:12", "the fire was accepted late; it was not skipped", "Open", "run"),
            ("never fired", "every Monday 09:00", "never", "created 8 days ago · check the cron", "Open", "muted"),
            ("delete pending", "every Monday 09:00", "—", "credential revocation is still processing", "Open", "wait")]
    header_y = fy + 144
    widths = [250, 270, 250, 580, 120]
    rect(fx + 58, header_y, sum(widths), 42, bg=SUBTLE, stroke=LINE, radius=False)
    cursor = fx + 58
    for width, label in zip(widths, ("STATE", "CADENCE", "LAST", "REASON", "ACTION")):
        text(cursor + 12, header_y + 13, label, FS_SMALL, MUTED, font=FONT_MONO, width=width - 24)
        cursor += width
    for idx, (state, cadence, last, reason, action, kind) in enumerate(rows):
        y = header_y + 42 + idx * 78
        rect(fx + 58, y, sum(widths), 78, bg=SURFACE, stroke=LINE, radius=False)
        badge(fx + 72, y + 18, state, kind)
        text(fx + 322, y + 27, cadence, FS_SMALL, INK, width=240)
        text(fx + 592, y + 27, last, FS_SMALL, MUTED, font=FONT_MONO, width=220)
        text(fx + 842, y + 16, reason, FS_SMALL, RED if kind == "fail" else MUTED, width=540)
        if action:
            button(fx + 1420, y + 20, 82, action, color=BLUE if action != "Open" else INK, bg=BLUE_BG if action == "Open" else SURFACE, h=34)
    annotation(fx + FRAME_W + 42, fy + 184, "8", "State is observed",
                "Accepted commands remain pending until the owner read model reports the new lifecycle state.")
    end_frame()


def frame_runtime_reference(index: int) -> None:
    fx, fy = begin_frame(index, "REF · schedule lifecycle")
    text(fx + 58, fy + 38, "Read from the code. This is why the screens look this way.", FS_HEAD, INK, width=930)
    text(fx + 58, fy + 84, "The schedule surface composes existing Team Automation and ScheduledDispatch facts; it does not invent a second scheduler.", FS_SMALL, MUTED, width=1050)
    text(fx + 58, fy + 148, "ALREADY WORKS", FS_SMALL, GREEN, font=FONT_MONO, width=280)
    works = [("schedules", "create · update · pause · resume · run-now · delete"),
             ("permission preflight", "exact reachable services and grants before commit"),
             ("next-fire preview", "server-side calculation from the same scheduler"),
             ("authorization state", "credential lifecycle with an observed reason"),
             ("named run inputs", "the engine seeds typed inputs at each fire")]
    for idx, (name, detail) in enumerate(works):
        y = fy + 194 + idx * 48
        badge(fx + 58, y - 4, name, "ok")
        text(fx + 284, y, detail, FS_SMALL, INK, width=720)
    text(fx + 58, fy + 468, "PUBLIC BOUNDARY", FS_SMALL, BLUE, font=FONT_MONO, width=280)
    text(fx + 58, fy + 508, "Team member owner", FS_BODY, INK, width=250)
    text(fx + 284, fy + 508, "scopeId + teamId + memberId", FS_SMALL, INK, font=FONT_MONO, width=480)
    text(fx + 58, fy + 548, "Published target", FS_BODY, INK, width=250)
    text(fx + 284, fy + 548, "publishedServiceId + activeRevisionId", FS_SMALL, INK, font=FONT_MONO, width=520)
    text(fx + 58, fy + 588, "Mutation receipt", FS_BODY, INK, width=250)
    text(fx + 284, fy + 588, "202 Accepted · reread owner automation state", FS_SMALL, INK, width=620)
    text(fx + 58, fy + 628, "UI OWNERSHIP", FS_SMALL, BLUE, font=FONT_MONO, width=280)
    text(fx + 58, fy + 668, "Workflow", FS_BODY, INK, width=250)
    text(fx + 284, fy + 668, "owns Schedule list, detail, cadence, authorization, pause, and delete", FS_SMALL, INK, width=760)
    text(fx + 58, fy + 708, "Activity", FS_BODY, INK, width=250)
    text(fx + 284, fy + 708, "owns immutable Runs; Schedule is only a source filter", FS_SMALL, INK, width=760)
    text(fx + 58, fy + 768, "DOES NOT EXIST IN THIS RELEASE", FS_SMALL, RED, font=FONT_MONO, width=420)
    text(fx + 58, fy + 810, "one global schedule collection", FS_BODY, INK, width=360)
    text(fx + 360, fy + 810, "browser-side filtering would cross owner boundaries", FS_SMALL, MUTED, width=620)
    text(fx + 58, fy + 850, "a schedule graph node", FS_BODY, INK, width=360)
    text(fx + 360, fy + 850, "the Workflow starts at its first processing node", FS_SMALL, MUTED, width=620)
    annotation(fx + FRAME_W + 42, fy + 220, "9", "One schedule model",
                "The board keeps owner, target, authorization, dispatch, and Activity evidence on one understandable path.")
    end_frame()


text(ORIGIN_X, 70, "Aevatar — Published workflow schedules", FS_TITLE, INK, width=1040)
text(ORIGIN_X, 126, "One owner, one recurring automation, one observable run history.", FS_HEAD, BLUE, width=1060)
text(ORIGIN_X, 178,
     "Schedule is configured and managed inside a published Workflow; Activity only records its Runs.",
     FS_BODY, MUTED, width=1180)
text(ORIGIN_X, 232,
     "9 frames · configure → authorize → observe → recover. Notes stay outside the screen frames.",
     FS_SMALL, MUTED, width=900)
line(ORIGIN_X, 274, 1150, 0, color=LINE)

frame_workflows_list(0)
frame_schedule_setup(1)
frame_authorization(2)
frame_scheduled_runs(3)
frame_schedule_detail(4)
frame_schedule_edit(5)
frame_cadence_spec(6)
frame_row_states(7)
frame_runtime_reference(8)

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
