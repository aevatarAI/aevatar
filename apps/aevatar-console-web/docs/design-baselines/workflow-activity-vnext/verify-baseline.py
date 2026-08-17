#!/usr/bin/env python3
"""Verify the immutable Workflow Activity vNext design baseline."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
import zlib
from pathlib import Path


BASELINE_DIR = Path(__file__).resolve().parent
BOARD_NAME = "aevatar-workflow-activity-vnext.excalidraw"
SCHEDULE_BOARD_NAME = "aevatar-workflow-schedule-design.excalidraw"
GENERATOR_NAME = "aevatar-workflow-activity-vnext.gen.py"
SCHEDULE_GENERATOR_NAME = "aevatar-workflow-schedule-design.gen.py"
SCHEDULE_RENDERER_NAME = "render-schedule-png.py"
PROTOTYPE_NAME = "prototype.html"
SCHEDULE_PROTOTYPE_NAME = "prototype-schedule.html"
SCHEDULE_PAGE_PNG_NAME = "prototype-schedule.png"
SCHEDULE_BOARD_PNG_NAME = "aevatar-workflow-schedule-design.png"
EXPECTED_SHA256 = "30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de"
EXPECTED_SCHEDULE_SHA256 = "d1838c0e292b77af646a386cffe7da4590f9ac33da3510526f19fe38b4395084"
EXPECTED_SCHEDULE_PAGE_PNG_SHA256 = "3fc54bb81f4065147013e00279c329f147b9a0c69e7d97ae0f986651a8bddc11"
EXPECTED_SCHEDULE_BOARD_PNG_SHA256 = "ff7b4cbfffa0c592e58ee07f54e8c9126cb8c50820f44c9ecd99d8ba3279c781"
EXPECTED_FRAMES = (
    "01 Workflows - catalogue",
    "02 New workflow - direct creation",
    "03 Describe - generated Workflow draft",
    "04 Start blank - empty Workflow draft",
    "05 Import YAML - imported Workflow draft",
    "06 Template - populated Workflow draft",
    "07 Run - unified execution dialog",
    "08 Running draft - Studio canvas and Run console",
    "09 Activity - filtered by Workflow",
    "10 Activity - all retained Runs",
    "11 Run detail - immutable record",
    "12 Failed Run - recovery creates a new record",
    "13 Workflows and Activity - states",
    "14 Settings - AI defaults",
    "15 Settings - save and recovery states",
    "16 Settings - Account",
    "17 Settings - Advanced and responsive",
)
EXPECTED_SCHEDULE_FRAMES = (
    "01 · Workflows — schedule entry",
    "02 · Schedule — configure recurring work",
    "03 · Schedule — review authorization",
    "04 · Activities — schedules that run without you",
    "05 · Schedule — opened",
    "06 · Schedule — change cadence",
    "SPEC · cadence control",
    "SPEC · schedule row states",
    "REF · schedule lifecycle",
)


def png_chunks(data: bytes) -> list[tuple[bytes, bytes]]:
    chunks: list[tuple[bytes, bytes]] = []
    offset = 8
    while offset + 12 <= len(data):
        length = int.from_bytes(data[offset:offset + 4], "big")
        chunk_type = data[offset + 4:offset + 8]
        chunk_data = data[offset + 8:offset + 8 + length]
        chunks.append((chunk_type, chunk_data))
        offset += length + 12
        if chunk_type == b"IEND":
            break
    return chunks


def paeth(left: int, above: int, upper_left: int) -> int:
    prediction = left + above - upper_left
    left_distance = abs(prediction - left)
    above_distance = abs(prediction - above)
    upper_left_distance = abs(prediction - upper_left)
    if left_distance <= above_distance and left_distance <= upper_left_distance:
        return left
    if above_distance <= upper_left_distance:
        return above
    return upper_left


def sampled_png_colors(chunks: list[tuple[bytes, bytes]]) -> set[tuple[int, int, int]]:
    ihdr = next(chunk for chunk_type, chunk in chunks if chunk_type == b"IHDR")
    width = int.from_bytes(ihdr[0:4], "big")
    height = int.from_bytes(ihdr[4:8], "big")
    if ihdr[8:13] != bytes((8, 2, 0, 0, 0)):
        raise SystemExit("Schedule PNG must be non-interlaced 8-bit RGB")
    encoded = b"".join(chunk for chunk_type, chunk in chunks if chunk_type == b"IDAT")
    decoded = zlib.decompress(encoded)
    bytes_per_pixel = 3
    stride = width * bytes_per_pixel
    previous = bytearray(stride)
    offset = 0
    colors: set[tuple[int, int, int]] = set()
    sample_x = max(1, width // 240)
    sample_y = max(1, height // 160)

    for row_index in range(height):
        filter_type = decoded[offset]
        offset += 1
        row = bytearray(decoded[offset:offset + stride])
        offset += stride
        for byte_index in range(stride):
            left = row[byte_index - bytes_per_pixel] if byte_index >= bytes_per_pixel else 0
            above = previous[byte_index]
            upper_left = previous[byte_index - bytes_per_pixel] if byte_index >= bytes_per_pixel else 0
            if filter_type == 1:
                row[byte_index] = (row[byte_index] + left) & 0xFF
            elif filter_type == 2:
                row[byte_index] = (row[byte_index] + above) & 0xFF
            elif filter_type == 3:
                row[byte_index] = (row[byte_index] + ((left + above) // 2)) & 0xFF
            elif filter_type == 4:
                row[byte_index] = (row[byte_index] + paeth(left, above, upper_left)) & 0xFF
            elif filter_type != 0:
                raise SystemExit(f"unsupported PNG filter: {filter_type}")
        if row_index % sample_y == 0:
            for column in range(0, width, sample_x):
                start = column * bytes_per_pixel
                colors.add(tuple(row[start:start + bytes_per_pixel]))
        previous = row

    return colors


def verify_png(
    path: Path,
    expected_size: tuple[int, int],
    expected_sha256: str,
    expected_source_sha256: str,
    expected_renderer_sha256: str,
) -> None:
    data = path.read_bytes()
    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
        raise SystemExit(f"not a PNG file: {path.name}")
    actual_size = (
        int.from_bytes(data[16:20], "big"),
        int.from_bytes(data[20:24], "big"),
    )
    if actual_size != expected_size:
        raise SystemExit(
            f"{path.name} size mismatch: expected {expected_size}, got {actual_size}"
        )
    actual_sha256 = hashlib.sha256(data).hexdigest()
    if actual_sha256 != expected_sha256:
        raise SystemExit(
            f"{path.name} SHA-256 mismatch: expected {expected_sha256}, got {actual_sha256}"
        )
    chunks = png_chunks(data)
    metadata = {
        value.split(b"\0", 1)[0].decode("latin-1"): value.split(b"\0", 1)[1].decode("latin-1")
        for chunk_type, value in chunks
        if chunk_type == b"tEXt" and b"\0" in value
    }
    if metadata.get("Source-SHA256") != expected_source_sha256:
        raise SystemExit(f"{path.name} is not linked to the current Schedule Excalidraw")
    if metadata.get("Renderer-SHA256") != expected_renderer_sha256:
        raise SystemExit(f"{path.name} is not linked to the current PNG renderer")
    colors = sampled_png_colors(chunks)
    if len(colors) < 16:
        raise SystemExit(f"{path.name} does not contain a non-blank rendered design")


def main() -> None:
    board_path = BASELINE_DIR / BOARD_NAME
    schedule_board_path = BASELINE_DIR / SCHEDULE_BOARD_NAME
    generator_path = BASELINE_DIR / GENERATOR_NAME
    schedule_generator_path = BASELINE_DIR / SCHEDULE_GENERATOR_NAME
    schedule_renderer_path = BASELINE_DIR / SCHEDULE_RENDERER_NAME
    prototype_path = BASELINE_DIR / PROTOTYPE_NAME
    schedule_prototype_path = BASELINE_DIR / SCHEDULE_PROTOTYPE_NAME
    board_bytes = board_path.read_bytes()
    schedule_board_bytes = schedule_board_path.read_bytes()

    actual_sha256 = hashlib.sha256(board_bytes).hexdigest()
    if actual_sha256 != EXPECTED_SHA256:
        raise SystemExit(
            f"design SHA-256 mismatch: expected {EXPECTED_SHA256}, got {actual_sha256}"
        )

    actual_schedule_sha256 = hashlib.sha256(schedule_board_bytes).hexdigest()
    if actual_schedule_sha256 != EXPECTED_SCHEDULE_SHA256:
        raise SystemExit(
            "schedule design SHA-256 mismatch: "
            f"expected {EXPECTED_SCHEDULE_SHA256}, got {actual_schedule_sha256}"
        )
    actual_renderer_sha256 = hashlib.sha256(schedule_renderer_path.read_bytes()).hexdigest()

    document = json.loads(board_bytes)
    schedule_document = json.loads(schedule_board_bytes)
    schedule_visible_text = "\n".join(
        element["text"]
        for element in schedule_document["elements"]
        if element.get("type") == "text"
    )
    schedule_visible_text_casefolded = schedule_visible_text.casefold()
    if "Schedule" not in schedule_visible_text:
        raise SystemExit("schedule entry point is missing from the schedule design board")
    for required in ("Team member automation", "Published target", "Activities",
                     "Dedicated Agent Key", "Cron expression", "Time zone",
                     "NEXT FIVE FIRES", "Review and create", "Pause", "Delete"):
        if required.casefold() not in schedule_visible_text_casefolded:
            raise SystemExit(f"schedule frame is missing required copy: {required}")
    if "prompt (optional)" not in schedule_visible_text_casefolded:
        raise SystemExit("schedule frame incorrectly hides optional recurring prompt semantics")
    if "Credential ready" not in schedule_visible_text:
        raise SystemExit("schedule frame is missing observed credential state")
    if "a schedule graph node" not in schedule_visible_text_casefolded:
        raise SystemExit("schedule board does not state that graph nodes are out of scope")
    if "trigger" in schedule_visible_text_casefolded:
        raise SystemExit("schedule board still contains unsupported trigger terminology")
    schedule_frame_names = tuple(
        element["name"]
        for element in schedule_document["elements"]
        if element.get("type") == "frame"
    )
    if schedule_frame_names != EXPECTED_SCHEDULE_FRAMES:
        raise SystemExit(
            "schedule frame inventory mismatch:\n"
            f"expected {EXPECTED_SCHEDULE_FRAMES!r}\n"
            f"got      {schedule_frame_names!r}"
        )
    if len(schedule_frame_names) != len(EXPECTED_SCHEDULE_FRAMES):
        raise SystemExit("schedule board must contain the complete configure-to-observe flow")

    prototype_text = prototype_path.read_text(encoding="utf-8")
    if 'id="editor-schedule"' not in prototype_text:
        raise SystemExit("prototype schedule action is missing from the editor")
    if 'data-schedule-workflow="${item.id}"' not in prototype_text:
        raise SystemExit("prototype does not expose Schedule from published Workflow rows")
    if 'window.location.hash === "#schedule"' not in prototype_text:
        raise SystemExit("prototype does not expose a directly addressable Schedule state")
    if 'class="studio-schedule-panel"' not in prototype_text:
        raise SystemExit("prototype schedule panel is missing from the editor")
    if "Publish this workflow before scheduling it." not in prototype_text:
        raise SystemExit("prototype schedule draft-state copy is missing")
    if "schedulePanelMode" not in prototype_text or "data-schedule-id" not in prototype_text:
        raise SystemExit("prototype does not model Schedule list and detail states")
    if "const schedule = schedules[0]" in prototype_text:
        raise SystemExit("prototype only renders the first Schedule")
    if "graphLayout(workflowSteps, schedulePanelOpen)" not in prototype_text:
        raise SystemExit("prototype does not reserve canvas layout for the Schedule panel")
    if "flex: 0 0 400px;" not in prototype_text:
        raise SystemExit("prototype Schedule panel does not reserve desktop width")
    if 'data-activity-filter="Scheduled"' not in prototype_text:
        raise SystemExit("prototype is missing the generic Scheduled runs Activity filter")
    if "scheduleMutation" not in prototype_text:
        raise SystemExit("prototype does not model accepted Schedule mutations")
    if 'data-step-type="schedule"' in prototype_text:
        raise SystemExit("prototype still exposes Schedule as a workflow graph node")
    if "schedule.enabled = event.target.checked" in prototype_text:
        raise SystemExit("prototype treats an accepted Schedule update as authoritative")
    if "prototypeSchedules[currentWorkflow.id] = []" in prototype_text:
        raise SystemExit("prototype treats an accepted Schedule delete as authoritative")
    if (
        "currentWorkflow.scopeId" not in prototype_text
        or "currentWorkflow.teamId" not in prototype_text
        or "currentWorkflow.memberId" not in prototype_text
        or "currentWorkflow.publishedServiceId" not in prototype_text
    ):
        raise SystemExit("prototype does not model the Team member automation owner")
    if "currentWorkflow.activeRevisionId" not in prototype_text:
        raise SystemExit("prototype does not model the active published revision identity")
    if "prototypeSchedules[currentWorkflow.id]" in prototype_text:
        raise SystemExit("prototype indexes Schedules by workflow identity instead of published service identity")
    if "prototypeSchedules" in prototype_text:
        raise SystemExit("prototype still uses standalone Schedule state instead of Team automations")
    if "prototypeTeamAutomations[currentWorkflow.scopeId]?.[currentWorkflow.teamId]?.[currentWorkflow.memberId]" not in prototype_text:
        raise SystemExit("prototype does not scope automations to the Team member owner")
    if "/api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations" not in prototype_text:
        raise SystemExit("prototype does not point production reads at Team Automation endpoints")
    if "Prompt (optional)" not in prototype_text:
        raise SystemExit("prototype does not expose prompt as optional")
    if "Dedicated Agent Key" not in prototype_text:
        raise SystemExit("prototype is missing Team Automation authorization review")
    if "Schedule update accepted." in prototype_text or "Schedule deletion accepted." in prototype_text:
        raise SystemExit("prototype still names accepted mutations as standalone Schedule changes")
    if "Automation update accepted." not in prototype_text or "Automation deletion accepted." not in prototype_text:
        raise SystemExit("prototype does not model accepted Team Automation mutations")
    if "revision: currentWorkflow.revision" in prototype_text:
        raise SystemExit("prototype uses a revision display label as the Schedule pin identity")
    if "revisionId: currentWorkflow.activeRevisionId" not in prototype_text:
        raise SystemExit("prototype does not pin new Schedules to the active revision identity")
    if "function schedulePreview(schedule)" not in prototype_text:
        raise SystemExit("prototype does not distinguish paused Schedule previews")
    if 'schedule.enabled ? schedule.preview : ["No upcoming runs"]' not in prototype_text:
        raise SystemExit("prototype promises upcoming runs for a paused Schedule")

    schedule_prototype_text = schedule_prototype_path.read_text(encoding="utf-8")
    if "prototype.html#schedule" not in schedule_prototype_text:
        raise SystemExit("dedicated Schedule prototype does not open the visible Schedule state")
    verify_png(
        BASELINE_DIR / SCHEDULE_PAGE_PNG_NAME,
        (1440, 900),
        EXPECTED_SCHEDULE_PAGE_PNG_SHA256,
        actual_schedule_sha256,
        actual_renderer_sha256,
    )
    verify_png(
        BASELINE_DIR / SCHEDULE_BOARD_PNG_NAME,
        (4800, 3200),
        EXPECTED_SCHEDULE_BOARD_PNG_SHA256,
        actual_schedule_sha256,
        actual_renderer_sha256,
    )

    frame_names = tuple(
        element["name"]
        for element in document["elements"]
        if element.get("type") == "frame"
    )
    if frame_names != EXPECTED_FRAMES:
        raise SystemExit(
            "frame inventory mismatch:\n"
            f"expected {EXPECTED_FRAMES!r}\n"
            f"got      {frame_names!r}"
        )

    with tempfile.TemporaryDirectory(prefix="aevatar-vnext-baseline-") as temp_dir:
        generated_dir = Path(temp_dir)
        generated_script = generated_dir / GENERATOR_NAME
        shutil.copyfile(generator_path, generated_script)
        subprocess.run(["python3", str(generated_script)], check=True, capture_output=True)
        generated_bytes = (generated_dir / BOARD_NAME).read_bytes()
        generated_schedule_script = generated_dir / SCHEDULE_GENERATOR_NAME
        shutil.copyfile(schedule_generator_path, generated_schedule_script)
        subprocess.run(["python3", str(generated_schedule_script)], check=True, capture_output=True)
        generated_schedule_bytes = (generated_dir / SCHEDULE_BOARD_NAME).read_bytes()

        if importlib.util.find_spec("PIL") is not None:
            subprocess.run(
                [sys.executable, str(schedule_renderer_path), "--output-dir", str(generated_dir)],
                check=True,
                capture_output=True,
            )
            for png_name in (SCHEDULE_PAGE_PNG_NAME, SCHEDULE_BOARD_PNG_NAME):
                if (generated_dir / png_name).read_bytes() != (BASELINE_DIR / png_name).read_bytes():
                    raise SystemExit(f"schedule renderer output does not match {png_name}")

    if generated_bytes != board_bytes:
        raise SystemExit("generator output does not match the committed Excalidraw")
    if generated_schedule_bytes != schedule_board_bytes:
        raise SystemExit("schedule generator output does not match the committed Excalidraw")

    print(f"design SHA-256: {actual_sha256}")
    print(f"frames: {len(frame_names)}/{len(EXPECTED_FRAMES)}")
    print(f"schedule design SHA-256: {actual_schedule_sha256}")
    print(f"schedule frames: {len(schedule_frame_names)}/{len(EXPECTED_SCHEDULE_FRAMES)}")
    print("schedule PNGs: 1440x900 page + 4800x3200 overview")
    print("schedule PNG pixels: non-blank and source-linked")
    print("schedule HTML: direct entry available")
    print("generators output: byte-identical")
    print("workflow activity vNext baseline: PASS")


if __name__ == "__main__":
    main()
