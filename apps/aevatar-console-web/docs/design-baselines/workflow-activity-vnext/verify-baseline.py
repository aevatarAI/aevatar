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
SCHEDULE_PNG_SHA256 = {
    "schedule-workflows-list-modal.png": "d505b83ca15afb55e68b244a123cc48771c41b96d29a29a8954b1deef11cb0a1",
    "schedule-workflow-editor-panel.png": "73f22d45077b3849243e7c91185eee461d1ed6c900f1bc9987a8c3392818617c",
    "schedule-review.png": "129beba83372ef775c1a171ec7475251332c3833bb121729c79dec1843ff7433",
    "schedule-creation-pending.png": "5d32507eab30c31db7f91f8511ef51e4ccba5003482c3ba32577ab8001e71b70",
    "schedule-detail.png": "4c4f5a06ca4ca15325e6692209783310800f48f8cc487edc246b5ac0fcde4041",
    "schedule-history.png": "f3d871691e5f34648b2e00023ea8a471c81af9383f024900b988c3d2e982663d",
    "schedule-edit.png": "29db6035c476649e8fc5a5548bcdca4bd9b2c4b82cdc9fd4ea36f6bbceb10796",
}
OBSOLETE_SCHEDULE_PNGS = (
    "prototype-schedule.png",
    "aevatar-workflow-schedule-design.png",
    "schedule-authorization-review.png",
)
EXPECTED_SHA256 = "30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de"
EXPECTED_SCHEDULE_SHA256 = "688d5b14a2e1047739f3cf950a1714e256523430cf6966b1441e4b1e4344b3e0"
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
    "01 · Workflows — schedule management modal",
    "02 · Workflow — schedule setup panel",
    "03 · Schedule — review before creation",
    "04 · Schedule — creation pending",
    "05 · Workflow — schedule overview",
    "06 · Workflow — schedule history",
    "07 · Workflow — change schedule",
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

    for obsolete_png in OBSOLETE_SCHEDULE_PNGS:
        if (BASELINE_DIR / obsolete_png).exists():
            raise SystemExit(f"obsolete combined Schedule PNG must be removed: {obsolete_png}")

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
    for required in ("Workflow Schedule", "write it as cron instead", "Time zone",
                     "NEXT FIVE FIRE TIMES", "Review schedule", "Create schedule",
                     "202 Accepted", "Run now", "Pause", "Delete"):
        if required.casefold() not in schedule_visible_text_casefolded:
            raise SystemExit(f"schedule frame is missing required copy: {required}")
    if "run input (optional)" not in schedule_visible_text_casefolded:
        raise SystemExit("schedule frame incorrectly hides optional recurring prompt semantics")
    for required_state in ("enabled after creation",):
        if required_state not in schedule_visible_text_casefolded:
            raise SystemExit(f"schedule frames are missing honest Schedule state: {required_state}")
    if "trigger" in schedule_visible_text_casefolded:
        raise SystemExit("schedule board still contains unsupported trigger terminology")
    for forbidden in (
        "team member",
        "team automation",
        "teamid",
        "memberid",
        "publishedserviceid",
        "dedicated agent key",
        "node ids",
        "owner llm",
        "permission digest",
        "permissiondigest",
        "policy version",
        "policyversion",
        "review and reauthorize",
        "/api/schedules",
        "/automations/preflight",
    ):
        if forbidden in schedule_visible_text_casefolded:
            raise SystemExit(f"Schedule design leaks the wrong resource model: {forbidden}")
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
        raise SystemExit("schedule board must contain exactly seven standalone review scenes")

    schedule_frames_by_name = {
        element["name"]: element["id"]
        for element in schedule_document["elements"]
        if element.get("type") == "frame"
    }

    def schedule_frame_text(frame_name: str) -> str:
        frame_id = schedule_frames_by_name.get(frame_name)
        return "\n".join(
            element["text"]
            for element in schedule_document["elements"]
            if element.get("type") == "text" and element.get("frameId") == frame_id
        )

    quick_modal_text = schedule_frame_text("01 · Workflows — schedule management modal").casefold()
    for required in ("schedules", "new schedule", "open"):
        if required not in quick_modal_text:
            raise SystemExit(f"Workflow catalogue Schedule management modal is missing: {required}")
    quick_modal_lines = {line.strip() for line in quick_modal_text.splitlines()}
    for forbidden in ("edit", "pause", "run now", "delete"):
        if forbidden in quick_modal_lines:
            raise SystemExit(f"Workflow catalogue repeats selected-Schedule actions in every row: {forbidden}")
    configure_frame_name = "02 · Workflow — schedule setup panel"
    configure_text = schedule_frame_text(configure_frame_name).casefold()
    for required in (
        "schedule name",
        "weekly feedback report recurring work",
        "repeat",
        "every weekday at 09:00",
        "write it as cron instead",
        "post /api/scopes/{scopeid}/workflows/{workflowid}/schedules/preview",
        "no prompt",
    ):
        if required not in configure_text:
            raise SystemExit(f"{configure_frame_name} is missing Schedule configuration content: {required}")
    if "cron expression" in configure_text:
        raise SystemExit(f"{configure_frame_name} exposes raw cron as a default primary field")

    schedule_edit_text = schedule_frame_text("07 · Workflow — change schedule").casefold()
    for required in (
        "schedule name",
        "weekly review",
        "repeat",
        "every monday at 10:00",
        "asia/shanghai",
        "0 10 * * 1",
        "write it as cron instead",
    ):
        if required not in schedule_edit_text:
            raise SystemExit(f"Workflow Schedule edit is missing repeat-builder semantics: {required}")
    if "cron expression" in schedule_edit_text:
        raise SystemExit("Workflow Schedule edit exposes raw cron as a default primary field")

    review_text = schedule_frame_text("03 · Schedule — review before creation").casefold()
    for required in (
        "review schedule",
        "weekly feedback report",
        "weekly feedback report recurring work",
        "every weekday at 09:00",
        "asia/shanghai",
        "no prompt",
        "enabled after creation",
        "next five fire times",
        "create schedule",
        "post /api/scopes/{scopeid}/workflows/{workflowid}/schedules",
    ):
        if required not in review_text:
            raise SystemExit(f"Schedule creation review is missing API-backed fact: {required}")

    pending_text = schedule_frame_text("04 · Schedule — creation pending").casefold()
    for required in (
        "202 accepted",
        "schedule request accepted",
        "appear in this workflow's schedule list shortly",
    ):
        if required not in pending_text:
            raise SystemExit(f"Schedule pending frame is missing honest accepted state: {required}")

    for workflow_frame_name in (
        "05 · Workflow — schedule overview",
        "06 · Workflow — schedule history",
        "07 · Workflow — change schedule",
    ):
        workflow_frame_text = schedule_frame_text(workflow_frame_name).casefold()
        if "workflow canvas remains visible" not in workflow_frame_text:
            raise SystemExit(f"{workflow_frame_name} is not visibly owned by Workflow")
        if "weekly feedback report" not in workflow_frame_text:
            raise SystemExit(f"{workflow_frame_name} does not identify its owning Workflow")

    schedule_overview_text = schedule_frame_text("05 · Workflow — schedule overview").casefold()
    for required in (
        "morning digest",
        "overview",
        "history",
        "active",
        "every weekday at 09:00",
        "asia/shanghai",
        "next scheduled",
        "last attempt",
        "total attempts",
        "failed attempts",
        "summarize new feedback",
        "advanced details",
        "run now",
        "edit schedule",
        "more",
    ):
        if required not in schedule_overview_text:
            raise SystemExit(f"Workflow Schedule Overview is missing: {required}")
    for forbidden in ("cron expression", "recent fires", "observed schedule state only"):
        if forbidden in schedule_overview_text:
            raise SystemExit(f"Workflow Schedule Overview exposes secondary implementation content: {forbidden}")

    schedule_history_text = schedule_frame_text("06 · Workflow — schedule history").casefold()
    for required in (
        "morning digest",
        "recent attempts",
        "scheduled",
        "manual",
        "run started",
        "failed",
        "action",
        "technical details",
        "the scheduled attempt could not start the workflow",
        "view related runs in activity",
    ):
        if required not in schedule_history_text:
            raise SystemExit(f"Workflow Schedule History is missing: {required}")
    for forbidden in ("run id", "actor id", "command id", "correlation id", "idempotency key"):
        if forbidden in schedule_history_text:
            raise SystemExit(f"Workflow Schedule History leaks a runtime identifier: {forbidden}")
    if "what will run" in schedule_visible_text_casefolded:
        raise SystemExit("Schedule board still presents the removed collection model")

    prototype_text = prototype_path.read_text(encoding="utf-8")
    creation_start = prototype_text.index("function resetScheduleCreation")
    creation_end = prototype_text.index("function openSchedulePrototype", creation_start)
    creation_text = prototype_text[creation_start:creation_end]
    configure_start = creation_text.index('if (quickScheduleStep === "configure")')
    configure_end = creation_text.index('if (quickScheduleStep === "previewing")', configure_start)
    configure_creation_text = creation_text[configure_start:configure_end]
    if 'id="editor-schedule"' not in prototype_text:
        raise SystemExit("prototype schedule action is missing from the editor")
    if 'data-schedule-workflow="${item.id}"' not in prototype_text:
        raise SystemExit("prototype does not expose Schedule from published Workflow rows")
    if 'button.onclick = () => openScheduleQuickModal(button.dataset.scheduleWorkflow)' not in prototype_text:
        raise SystemExit("Workflow list Schedule action does not open the management modal")
    for required in (
        'id="schedule-quick-modal"',
        "function openScheduleQuickModal",
        "function renderScheduleModalList",
        "function openSchedulePanelCreation",
        "function renderScheduleCreation",
        "function requestQuickSchedulePreview",
        "function acceptQuickScheduleCreation",
        "Review schedule",
        "Create schedule",
        "Recurring runs owned by",
        'id="quick-schedule-new"',
        "without leaving Workflows",
        "Next five fire times",
        "POST /api/scopes/{scopeId}/workflows/{workflowId}/schedules/preview",
        "POST /api/scopes/{scopeId}/workflows/{workflowId}/schedules",
        "Repeat",
        "Every weekday at 09:00",
        "write it as cron instead",
        'id="quick-schedule-cron-editor" hidden',
        "updateQuickScheduleRepeatPresentation",
        '"Every hour": "0 * * * *"',
        '"Write cron yourself": "15 14 * * 2,4"',
        "prototypeSchedulePreviewByCron",
        "const presetCron = quickScheduleCronByRepeat[repeat]",
        "browserTimeZone()",
        "Schedule request accepted. It will appear in the Workflow schedule list shortly.",
        "exitScheduleCreation();",
        "Save and publish the latest changes before scheduling.",
    ):
        if required not in prototype_text:
            raise SystemExit(f"prototype quick-create Schedule modal is missing: {required}")
    for required in (
        'scheduleCreationSurface === "panel"',
        'schedulePanelMode = "new"',
        "prototypeSchedulePreviewByCron[quickScheduleDraft.cronExpression]",
        'modal.id === "schedule-quick-modal"',
    ):
        if required not in prototype_text:
            raise SystemExit(f"prototype shared Schedule creation flow is missing: {required}")
    for forbidden in (
        "function createQuickSchedule()",
        "Schedule created for",
        "Mon 24 Aug · 09:00",
        "Summarize new feedback and post one concise update.",
        '<strong>Lark</strong><span>Already granted</span>',
        "prototypeSchedulePreflightByService",
        "prototypeSchedulePreviewByRequest",
        "prototypeSchedulePreflightByOwner",
        "requestQuickSchedulePreflight",
        "preflightMatchesWorkflow",
        "confirmedPermissionDigest",
        "confirmedPolicyVersion",
        "Dedicated Agent Key",
        "Review and reauthorize",
        "/automations/preflight",
        'quickScheduleStep === "accepted"',
        "quick-schedule-close-accepted",
    ):
        if forbidden in prototype_text:
            raise SystemExit(f"prototype quick-create Schedule modal still contains optimistic or shared sample state: {forbidden}")
    if 'id="quick-schedule-cron-editor" hidden' not in configure_creation_text:
        raise SystemExit("prototype quick-create Schedule exposes raw cron as a default primary field")
    for required in (
        'id="quick-schedule-name"',
        'name: content.querySelector("#quick-schedule-name").value.trim()',
    ):
        if required not in configure_creation_text and required not in creation_text:
            raise SystemExit(f"prototype Schedule creation does not preserve its editable name: {required}")
    for required in (
        "function scheduleEditModel(schedule)",
        'id="schedule-edit-name"',
        'id="schedule-edit-repeat"',
        'id="schedule-edit-repeat-time"',
        'id="schedule-edit-time-zone"',
        'id="schedule-edit-cron"',
        'id="schedule-edit-prompt"',
        'displayName: root.querySelector("#schedule-edit-name").value.trim()',
        'cronExpression: root.querySelector("#schedule-edit-cron").value.trim()',
        'timezone: root.querySelector("#schedule-edit-time-zone").value.trim()',
        "enabled: schedule.enabled",
        'prompt: root.querySelector("#schedule-edit-prompt").value.trim()',
    ):
        if required not in prototype_text:
            raise SystemExit(f"prototype Schedule edit does not round-trip observed state: {required}")
    for forbidden in ("const grants = workflow.steps.map", "schedules.unshift("):
        if forbidden in creation_text:
            raise SystemExit(f"prototype Schedule creation derives or invents observed state: {forbidden}")
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
    if "What will run" in prototype_text:
        raise SystemExit("prototype still presents Schedule definitions inside Activity")
    for forbidden in (
        "function openScheduledRuns",
        'id="schedule-activity"',
        "View scheduled runs",
        "watch Activity",
        "next fire and Activity",
        "rereads Team Automation and Activity",
        "It will appear in Activity when available.",
    ):
        if forbidden in prototype_text:
            raise SystemExit(f"prototype Schedule surface uses the obsolete Activity handoff: {forbidden}")
    for required in (
        "function renderScheduleModalSelected",
        "function scheduleOverviewMarkup",
        "function scheduleHistoryMarkup",
        "function openRelatedRunsInActivity",
        "View related runs in Activity",
        "Recent attempts",
        "Technical details",
        "The ${source.toLowerCase()} attempt could not start the Workflow.",
        "activityScheduleId",
        "run.scheduleId === activityScheduleId",
        "?workflowId=${workflow.id}&schedule=${schedule.scheduleId}&origin=schedule",
    ):
        if required not in prototype_text:
            raise SystemExit(f"prototype Schedule history flow is missing: {required}")
    if "scheduleMutation" not in prototype_text:
        raise SystemExit("prototype does not model accepted Schedule mutations")
    if 'data-step-type="schedule"' in prototype_text:
        raise SystemExit("prototype still exposes Schedule as a workflow graph node")
    if "schedule.enabled = event.target.checked" in prototype_text:
        raise SystemExit("prototype treats an accepted Schedule update as authoritative")
    if "prototypeSchedules[currentWorkflow.id] = []" in prototype_text:
        raise SystemExit("prototype treats an accepted Schedule delete as authoritative")
    for required in (
        "prototypeWorkflowSchedules[currentWorkflow.scopeId]?.[currentWorkflow.id]",
        "GET /api/scopes/{scopeId}/workflows/{workflowId}/schedules",
        "GET /api/scopes/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}",
        "PUT /api/scopes/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}",
        "POST /api/scopes/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}:enable",
        "POST /api/scopes/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}:disable",
        "POST /api/scopes/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}:run-now",
        "DELETE /api/scopes/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}",
        "displayName: quickScheduleDraft.name",
        "cronExpression: quickScheduleDraft.cronExpression",
        "timezone: quickScheduleDraft.timeZone",
        "enabled: quickScheduleDraft.enabled",
        "prompt: quickScheduleDraft.prompt",
        "schedule.scheduleId",
        "enabled: schedule.enabled",
        "refreshWorkflowSchedules",
    ):
        if required not in prototype_text:
            raise SystemExit(f"prototype is missing workflow-scoped Schedule behavior: {required}")
    schedule_surface_text = prototype_text[
        prototype_text.index("function scheduleAvailability"):
        prototype_text.index("function renderActivity")
    ]
    for forbidden in (
        "schedule.id",
        ".teamId",
        ".memberId",
        "prototypeTeamAutomations",
        "Team member automation",
        "Team Automation",
        "/api/schedules",
    ):
        if forbidden in schedule_surface_text:
            raise SystemExit(f"prototype Schedule flow uses the wrong product owner: {forbidden}")
    if "teamId" in prototype_text or "memberId" in prototype_text:
        raise SystemExit("pure Workflow prototype fixtures still require Team or Member identity")
    if "Run input (optional)" not in prototype_text:
        raise SystemExit("prototype does not expose prompt as optional")
    if "Schedule update accepted." not in prototype_text or "Schedule deletion accepted." not in prototype_text:
        raise SystemExit("prototype does not model accepted Workflow Schedule mutations")
    if "function schedulePreview(schedule)" not in prototype_text:
        raise SystemExit("prototype does not distinguish paused Schedule previews")
    if 'schedule.enabled && schedule.nextFireAt ? [schedule.nextFireAt] : ["No upcoming attempts"]' not in prototype_text:
        raise SystemExit("prototype promises upcoming attempts for a paused Schedule")

    schedule_prototype_text = schedule_prototype_path.read_text(encoding="utf-8")
    if "prototype.html#schedule" not in schedule_prototype_text:
        raise SystemExit("dedicated Schedule prototype does not open the visible Schedule state")
    for png_name, expected_sha256 in SCHEDULE_PNG_SHA256.items():
        verify_png(
            BASELINE_DIR / png_name,
            (1440, 900),
            expected_sha256,
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
            for png_name in SCHEDULE_PNG_SHA256:
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
    print(f"schedule PNGs: {len(SCHEDULE_PNG_SHA256)} standalone 1440x900 scenes")
    print("schedule PNG pixels: non-blank and source-linked")
    print("schedule HTML: direct entry available")
    print("generators output: byte-identical")
    print("workflow activity vNext baseline: PASS")


if __name__ == "__main__":
    main()
