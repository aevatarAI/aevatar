#!/usr/bin/env python3
"""Render review PNGs from the deterministic Schedule Excalidraw source."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
from typing import Any

from PIL import Image, ImageDraw, ImageFont, PngImagePlugin


BASELINE_DIR = Path(__file__).resolve().parent
SOURCE = BASELINE_DIR / "aevatar-workflow-schedule-design.excalidraw"
FRAME_OUTPUTS = (
    ("01 · Workflows — schedule management modal", "schedule-workflows-list-modal.png"),
    ("02 · Workflow — schedule setup panel", "schedule-workflow-editor-panel.png"),
    ("03 · Schedule — review before creation", "schedule-review.png"),
    ("04 · Schedule — creation pending", "schedule-creation-pending.png"),
    ("05 · Workflow — schedule overview", "schedule-detail.png"),
    ("06 · Workflow — schedule history", "schedule-history.png"),
    ("07 · Workflow — change schedule", "schedule-edit.png"),
)

SANS_FONT = Path("/System/Library/Fonts/Supplemental/Arial Unicode.ttf")
MONO_FONT = Path("/System/Library/Fonts/Menlo.ttc")
FALLBACK_FONT = Path("/System/Library/Fonts/Supplemental/Arial.ttf")


def color(value: str | None) -> str | None:
    if not value or value == "transparent":
        return None
    return value


def load_font(font_family: int, size: float) -> ImageFont.FreeTypeFont:
    preferred = MONO_FONT if font_family == 3 else SANS_FONT
    font_path = preferred if preferred.exists() else FALLBACK_FONT
    return ImageFont.truetype(str(font_path), max(7, round(size)))


def dashed_line(
    draw: ImageDraw.ImageDraw,
    start: tuple[float, float],
    end: tuple[float, float],
    fill: str,
    width: int,
    dash: float,
) -> None:
    dx = end[0] - start[0]
    dy = end[1] - start[1]
    length = math.hypot(dx, dy)
    if not length:
        return
    ux, uy = dx / length, dy / length
    cursor = 0.0
    while cursor < length:
        segment_end = min(cursor + dash, length)
        draw.line(
            (
                start[0] + ux * cursor,
                start[1] + uy * cursor,
                start[0] + ux * segment_end,
                start[1] + uy * segment_end,
            ),
            fill=fill,
            width=width,
        )
        cursor += dash * 1.8


def draw_arrowhead(
    draw: ImageDraw.ImageDraw,
    start: tuple[float, float],
    end: tuple[float, float],
    fill: str,
    width: int,
) -> None:
    angle = math.atan2(end[1] - start[1], end[0] - start[0])
    size = max(7, width * 4)
    wing = math.pi / 7
    left = (end[0] - size * math.cos(angle - wing), end[1] - size * math.sin(angle - wing))
    right = (end[0] - size * math.cos(angle + wing), end[1] - size * math.sin(angle + wing))
    draw.line((left, end, right), fill=fill, width=width, joint="curve")


def render_frame(
    document: dict[str, Any],
    frame: dict[str, Any],
    size: tuple[int, int],
    *,
    padding: int = 0,
) -> Image.Image:
    image = Image.new("RGB", size, "#f1f3f5")
    draw = ImageDraw.Draw(image)
    usable_width = size[0] - padding * 2
    usable_height = size[1] - padding * 2
    scale = min(usable_width / frame["width"], usable_height / frame["height"])
    origin_x = padding + (usable_width - frame["width"] * scale) / 2
    origin_y = padding + (usable_height - frame["height"] * scale) / 2

    def point(x: float, y: float) -> tuple[float, float]:
        return (
            origin_x + (x - frame["x"]) * scale,
            origin_y + (y - frame["y"]) * scale,
        )

    elements = [
        element
        for element in document["elements"]
        if not element.get("isDeleted")
        and element.get("type") != "frame"
        and element.get("frameId") == frame["id"]
    ]
    for element in elements:
        kind = element["type"]
        stroke = color(element.get("strokeColor")) or "#1e1e1e"
        fill = color(element.get("backgroundColor"))
        stroke_width = max(1, round(element.get("strokeWidth", 1) * scale))
        x1, y1 = point(element["x"], element["y"])
        x2 = x1 + element.get("width", 0) * scale
        y2 = y1 + element.get("height", 0) * scale

        if kind == "rectangle":
            radius = max(2, round(8 * scale)) if element.get("roundness") else 0
            draw.rounded_rectangle(
                (x1, y1, x2, y2),
                radius=radius,
                fill=fill,
                outline=stroke,
                width=stroke_width,
            )
            continue

        if kind == "ellipse":
            draw.ellipse((x1, y1, x2, y2), fill=fill, outline=stroke, width=stroke_width)
            continue

        if kind in {"line", "arrow"}:
            raw_points = element.get("points") or [[0, 0], [element.get("width", 0), element.get("height", 0)]]
            points = [(x1 + px * scale, y1 + py * scale) for px, py in raw_points]
            for start, end in zip(points, points[1:]):
                if element.get("strokeStyle") == "dashed":
                    dashed_line(draw, start, end, stroke, stroke_width, max(5, 7 * scale))
                else:
                    draw.line((start, end), fill=stroke, width=stroke_width, joint="curve")
            if kind == "arrow" and element.get("endArrowhead") and len(points) > 1:
                draw_arrowhead(draw, points[-2], points[-1], stroke, stroke_width)
            continue

        if kind == "text":
            font_size = element.get("fontSize", 16) * scale
            font = load_font(element.get("fontFamily", 1), font_size)
            text_value = element.get("text", "")
            align = element.get("textAlign", "left")
            spacing = max(0, round(font_size * (element.get("lineHeight", 1.25) - 1)))
            if align == "center":
                draw.multiline_text(
                    (x1 + element.get("width", 0) * scale / 2, y1),
                    text_value,
                    font=font,
                    fill=stroke,
                    anchor="ma",
                    align="center",
                    spacing=spacing,
                )
            elif align == "right":
                draw.multiline_text(
                    (x2, y1),
                    text_value,
                    font=font,
                    fill=stroke,
                    anchor="ra",
                    align="right",
                    spacing=spacing,
                )
            else:
                draw.multiline_text(
                    (x1, y1),
                    text_value,
                    font=font,
                    fill=stroke,
                    spacing=spacing,
                )

    return image


def output_metadata(source_bytes: bytes) -> PngImagePlugin.PngInfo:
    metadata = PngImagePlugin.PngInfo()
    metadata.add_text("Source-SHA256", hashlib.sha256(source_bytes).hexdigest())
    metadata.add_text(
        "Renderer-SHA256",
        hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
    )
    return metadata


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path, default=BASELINE_DIR)
    args = parser.parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)

    source_bytes = SOURCE.read_bytes()
    document = json.loads(source_bytes)
    frames = [element for element in document["elements"] if element.get("type") == "frame"]
    frames_by_name = {frame["name"]: frame for frame in frames}
    expected_names = tuple(frame_name for frame_name, _ in FRAME_OUTPUTS)
    if tuple(frames_by_name) != expected_names:
        raise SystemExit(
            f"Schedule frame inventory mismatch: expected {expected_names!r}, "
            f"found {tuple(frames_by_name)!r}"
        )

    for frame_name, output_name in FRAME_OUTPUTS:
        output_path = args.output_dir / output_name
        render_frame(document, frames_by_name[frame_name], (1440, 900)).save(
            output_path,
            optimize=True,
            pnginfo=output_metadata(source_bytes),
        )
        print(f"rendered {output_path.name}: 1440x900")


if __name__ == "__main__":
    main()
