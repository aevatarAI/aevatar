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
EXPECTED_SCHEDULE_SHA256 = "dae8f2038e6aede704219d4a129be97550d07f393edadf34e0e86007234000b5"
EXPECTED_SCHEDULE_PAGE_PNG_SHA256 = "c46f2e35d4289a75173bfa6f27f720684d2d52f7e28c7aabe8076edb890339f9"
EXPECTED_SCHEDULE_BOARD_PNG_SHA256 = "d2c9c94794881c051bc71b9a8f5212f88caf5ce7f1017f05eabc0fb5dcf6f7cb"
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
    "01 · Workflows — quick schedule modal",
    "02 · Schedule — configure recurring work",
    "03 · Schedule — review authorization",
    "04 · Activity — scheduled runs",
    "05 · Workflow — schedule detail",
    "06 · Workflow — change schedule",
    "SPEC · cadence control",
    "SPEC · Workflow Schedule row states",
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
                     "Dedicated Agent Key", "write it as cron instead", "Time zone",
                     "NEXT FIVE FIRES", "Review authorization",
                     "Pause", "Delete"):
        if required.casefold() not in schedule_visible_text_casefolded:
            raise SystemExit(f"schedule frame is missing required copy: {required}")
    if "prompt (optional)" not in schedule_visible_text_casefolded:
        raise SystemExit("schedule frame incorrectly hides optional recurring prompt semantics")
    if "credential active · next fire observed" not in schedule_visible_text_casefolded:
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

    activity_frame_text = schedule_frame_text("04 · Activity — scheduled runs")
    activity_frame_text_casefolded = activity_frame_text.casefold()
    for required in ("Every row is one immutable Run", "Run ID", "Source: Schedule",
                     "Workflow > Schedule"):
        if required.casefold() not in activity_frame_text_casefolded:
            raise SystemExit(f"scheduled Run evidence frame is missing: {required}")
    for forbidden in ("What will run", "Next fire", "Pause", "Delete", "Change cadence"):
        if forbidden.casefold() in activity_frame_text_casefolded:
            raise SystemExit(f"Activity still owns Schedule management: {forbidden}")

    quick_modal_text = schedule_frame_text("01 · Workflows — quick schedule modal").casefold()
    for required in ("new schedule", "without leaving workflows", "review authorization"):
        if required not in quick_modal_text:
            raise SystemExit(f"Workflow catalogue quick-create modal is missing: {required}")
    if "open the editor" not in quick_modal_text:
        raise SystemExit("Workflow catalogue frame does not preserve the optional editor path")
    for configure_frame_name in (
        "01 · Workflows — quick schedule modal",
        "02 · Schedule — configure recurring work",
    ):
        configure_text = schedule_frame_text(configure_frame_name).casefold()
        for required in (
            "repeat",
            "every weekday at 09:00",
            "write it as cron instead",
            "server preview required",
            "post /api/schedules/preview",
            "no prompt",
        ):
            if required not in configure_text:
                raise SystemExit(f"{configure_frame_name} is missing server-owned preview semantics: {required}")
        if "cron expression" in configure_text:
            raise SystemExit(f"{configure_frame_name} exposes raw cron as a default primary field")

    schedule_edit_text = schedule_frame_text("06 · Workflow — change schedule").casefold()
    for required in ("repeat", "every weekday at 09:00", "write it as cron instead"):
        if required not in schedule_edit_text:
            raise SystemExit(f"Workflow Schedule edit is missing repeat-builder semantics: {required}")
    if "cron expression" in schedule_edit_text:
        raise SystemExit("Workflow Schedule edit exposes raw cron as a default primary field")

    authorization_text = schedule_frame_text("03 · Schedule — review authorization").casefold()
    for required in (
        "exact server-returned authorization plan",
        "lark · acme",
        "node ids",
        "owner llm",
        "read · proxy",
        "permissiondigest + policyversion",
        "sha256:feedback-v7-permissions",
    ):
        if required not in authorization_text:
            raise SystemExit(f"Schedule authorization frame is missing server review fact: {required}")
    for forbidden in ("linear", "salesforce", "warehouse"):
        if forbidden in authorization_text:
            raise SystemExit(f"Schedule authorization frame still contains unrelated sample access: {forbidden}")

    for workflow_frame_name in (
        "05 · Workflow — schedule detail",
        "06 · Workflow — change schedule",
    ):
        workflow_frame_text = schedule_frame_text(workflow_frame_name).casefold()
        if "workflow canvas remains visible" not in workflow_frame_text:
            raise SystemExit(f"{workflow_frame_name} is not visibly owned by Workflow")
        if "managed from this workflow" not in workflow_frame_text:
            raise SystemExit(f"{workflow_frame_name} does not state its Workflow ownership")

    schedule_detail_text = schedule_frame_text("05 · Workflow — schedule detail").casefold()
    if "view scheduled runs" not in schedule_detail_text:
        raise SystemExit("Workflow Schedule detail does not link to its Activity run evidence")
    for required in ("Review and reauthorize", "Run now", "Change", "Pause", "Delete"):
        if required.casefold() not in schedule_detail_text:
            raise SystemExit(f"Workflow Schedule detail is missing lifecycle action: {required}")
    if "every fire" in schedule_detail_text:
        raise SystemExit("Workflow Schedule detail duplicates Activity Run history")
    if "what will run" in schedule_visible_text_casefolded:
        raise SystemExit("Schedule board still presents a Schedule collection inside Activity")

    prototype_text = prototype_path.read_text(encoding="utf-8")
    creation_start = prototype_text.index("function resetScheduleCreation")
    creation_end = prototype_text.index("function openSchedulePrototype", creation_start)
    creation_text = prototype_text[creation_start:creation_end]
    configure_start = creation_text.index('if (quickScheduleStep === "configure")')
    configure_end = creation_text.index('if (quickScheduleStep === "preflight")', configure_start)
    configure_creation_text = creation_text[configure_start:configure_end]
    if 'id="editor-schedule"' not in prototype_text:
        raise SystemExit("prototype schedule action is missing from the editor")
    if 'data-schedule-workflow="${item.id}"' not in prototype_text:
        raise SystemExit("prototype does not expose Schedule from published Workflow rows")
    if 'button.onclick = () => openScheduleQuickModal(button.dataset.scheduleWorkflow)' not in prototype_text:
        raise SystemExit("Workflow list Schedule action does not open the quick-create modal")
    for required in (
        'id="schedule-quick-modal"',
        "function openScheduleQuickModal",
        "function openSchedulePanelCreation",
        "function renderScheduleCreation",
        "function requestQuickSchedulePreflight",
        "function acceptQuickScheduleCreation",
        "Review authorization",
        "Confirm and create",
        "without leaving Workflows",
        "server preview required",
        "never calculates future fires",
        "POST /automations/preflight",
        "prototypeSchedulePreflightByOwner",
        "quickSchedulePreflight.serviceGrants.map",
        "Node IDs",
        "Repeat",
        "Every weekday at 09:00",
        "write it as cron instead",
        'id="quick-schedule-cron-editor" hidden',
        "updateQuickScheduleRepeatPresentation",
        '"Every hour": "0 * * * *"',
        '"Write cron yourself": "15 14 * * 2,4"',
        "prototypeSchedulePreviewByCron",
        "POST /api/schedules/preview",
        "const presetCron = quickScheduleCronByRepeat[repeat]",
        "browserTimeZone()",
        "preflightMatchesWorkflow",
        "confirmedPermissionDigest",
        "confirmedPolicyVersion",
        "Deleting the Schedule revokes its credential.",
        "Pausing and resuming preserve the credential.",
        "202 Accepted",
        "waiting for the server",
        "Not yet Active",
        "Save and publish the latest changes before scheduling.",
        "Wait for the published service to become available.",
        "Wait for the published revision to become available.",
    ):
        if required not in prototype_text:
            raise SystemExit(f"prototype quick-create Schedule modal is missing: {required}")
    for required in (
        'scheduleCreationSurface === "panel"',
        'schedulePanelMode = "new"',
        "prototypeSchedulePreflightByOwner[scheduleOwnerKey(workflow)]",
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
    ):
        if forbidden in prototype_text:
            raise SystemExit(f"prototype quick-create Schedule modal still contains optimistic or shared sample state: {forbidden}")
    if '<span class="field-label">Cron expression</span>' in configure_creation_text:
        raise SystemExit("prototype quick-create Schedule exposes raw cron as a default primary field")
    for forbidden in ("const grants = workflow.steps.map", "schedules.unshift(", "enabled: true"):
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
    if 'data-activity-filter="Scheduled"' not in prototype_text:
        raise SystemExit("prototype is missing the generic Scheduled runs Activity filter")
    if "What will run" in prototype_text:
        raise SystemExit("prototype still presents Schedule definitions inside Activity")
    if 'activityFilter = "Scheduled"' not in prototype_text or "View scheduled runs" not in prototype_text:
        raise SystemExit("prototype does not link Workflow Schedule management to Activity Run evidence")
    if "scheduleMutation" not in prototype_text:
        raise SystemExit("prototype does not model accepted Schedule mutations")
    if 'data-step-type="schedule"' in prototype_text:
        raise SystemExit("prototype still exposes Schedule as a workflow graph node")
    if "schedule.enabled = event.target.checked" in prototype_text:
        raise SystemExit("prototype treats an accepted Schedule update as authoritative")
    if "prototypeSchedules[currentWorkflow.id] = []" in prototype_text:
        raise SystemExit("prototype treats an accepted Schedule delete as authoritative")
    if any(
        required not in prototype_text
        for required in (
            "preflight.ownerScopeId === workflow.scopeId",
            "preflight.ownerTeamId === workflow.teamId",
            "preflight.ownerMemberId === workflow.memberId",
            "preflight.publishedServiceId === workflow.publishedServiceId",
        )
    ):
        raise SystemExit("prototype does not model the Team member automation owner")
    if "preflight.revisionId === workflow.activeRevisionId" not in prototype_text:
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
    if "revisionId: quickScheduleWorkflow.activeRevisionId" not in prototype_text:
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
