#!/usr/bin/env python3
"""Verify the immutable Workflow Activity vNext design baseline."""

from __future__ import annotations

import hashlib
import json
import shutil
import subprocess
import tempfile
from pathlib import Path


BASELINE_DIR = Path(__file__).resolve().parent
BOARD_NAME = "aevatar-workflow-activity-vnext.excalidraw"
GENERATOR_NAME = "aevatar-workflow-activity-vnext.gen.py"
PROTOTYPE_NAME = "prototype.html"
EXPECTED_SHA256 = "50a443a89287ad0bdf86b64cb79ea96e62664a8e82a519762be9b36da87f89ca"
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
    "18 Schedule - published workflow configuration",
)


def main() -> None:
    board_path = BASELINE_DIR / BOARD_NAME
    generator_path = BASELINE_DIR / GENERATOR_NAME
    prototype_path = BASELINE_DIR / PROTOTYPE_NAME
    board_bytes = board_path.read_bytes()

    actual_sha256 = hashlib.sha256(board_bytes).hexdigest()
    if actual_sha256 != EXPECTED_SHA256:
        raise SystemExit(
            f"design SHA-256 mismatch: expected {EXPECTED_SHA256}, got {actual_sha256}"
        )

    document = json.loads(board_bytes)
    visible_text = "\n".join(
        element["text"]
        for element in document["elements"]
        if element.get("type") == "text"
    )
    if "Publish this workflow before scheduling it." not in visible_text:
        raise SystemExit("schedule draft-state copy is missing from the design board")
    if "Source: Schedule" not in visible_text:
        raise SystemExit("scheduled Activity filter is missing from the design board")
    if "View scheduled runs" not in visible_text:
        raise SystemExit("scheduled Activity entry point is missing from the design board")
    if "18 Schedule - published workflow configuration" not in {
        element.get("name")
        for element in document["elements"]
        if element.get("type") == "frame"
    }:
        raise SystemExit("schedule configuration frame is missing from the design board")
    if "Schedule every Monday" in visible_text or "Schedule every weekday" in visible_text:
        raise SystemExit("schedule remains modeled as a workflow graph node")
    if "Created from Published" in visible_text:
        raise SystemExit("published status is incorrectly modeled as a workflow creation source")
    schedule_frame_id = next(
        element["id"]
        for element in document["elements"]
        if element.get("type") == "frame"
        and element.get("name") == "18 Schedule - published workflow configuration"
    )
    schedule_nodes = [
        element
        for element in document["elements"]
        if element.get("frameId") == schedule_frame_id
        and element.get("type") == "rectangle"
        and element.get("width") == 214
        and element.get("height") == 126
    ]
    if len(schedule_nodes) != 4:
        raise SystemExit("schedule configuration frame is missing compact workflow nodes")
    for index, left in enumerate(schedule_nodes):
        for right in schedule_nodes[index + 1:]:
            overlaps = (
                left["x"] < right["x"] + right["width"]
                and left["x"] + left["width"] > right["x"]
                and left["y"] < right["y"] + right["height"]
                and left["y"] + left["height"] > right["y"]
            )
            if overlaps:
                raise SystemExit("schedule configuration frame has overlapping workflow nodes")

    prototype_text = prototype_path.read_text(encoding="utf-8")
    if 'id="editor-schedule"' not in prototype_text:
        raise SystemExit("prototype schedule action is missing from the editor")
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
    if "currentWorkflow.scopeId" not in prototype_text or "currentWorkflow.publishedServiceId" not in prototype_text:
        raise SystemExit("prototype does not model the published service Schedule owner")
    if "currentWorkflow.activeRevisionId" not in prototype_text:
        raise SystemExit("prototype does not model the active published revision identity")
    if "prototypeSchedules[currentWorkflow.id]" in prototype_text:
        raise SystemExit("prototype indexes Schedules by workflow identity instead of published service identity")
    if "prototypeSchedules[currentWorkflow.scopeId]?.[currentWorkflow.publishedServiceId]" not in prototype_text:
        raise SystemExit("prototype does not scope Schedules to the published service owner")
    if "revision: currentWorkflow.revision" in prototype_text:
        raise SystemExit("prototype uses a revision display label as the Schedule pin identity")
    if "revisionId: currentWorkflow.activeRevisionId" not in prototype_text:
        raise SystemExit("prototype does not pin new Schedules to the active revision identity")
    if "function schedulePreview(schedule)" not in prototype_text:
        raise SystemExit("prototype does not distinguish paused Schedule previews")
    if 'schedule.enabled ? schedule.preview : ["No upcoming runs"]' not in prototype_text:
        raise SystemExit("prototype promises upcoming runs for a paused Schedule")

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

    if generated_bytes != board_bytes:
        raise SystemExit("generator output does not match the committed Excalidraw")

    print(f"design SHA-256: {actual_sha256}")
    print(f"frames: {len(frame_names)}/{len(EXPECTED_FRAMES)}")
    print("generator output: byte-identical")
    print("workflow activity vNext baseline: PASS")


if __name__ == "__main__":
    main()
