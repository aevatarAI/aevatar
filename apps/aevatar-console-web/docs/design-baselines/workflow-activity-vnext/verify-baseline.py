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
EXPECTED_SHA256 = "30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de"
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


def main() -> None:
    board_path = BASELINE_DIR / BOARD_NAME
    generator_path = BASELINE_DIR / GENERATOR_NAME
    board_bytes = board_path.read_bytes()

    actual_sha256 = hashlib.sha256(board_bytes).hexdigest()
    if actual_sha256 != EXPECTED_SHA256:
        raise SystemExit(
            f"design SHA-256 mismatch: expected {EXPECTED_SHA256}, got {actual_sha256}"
        )

    document = json.loads(board_bytes)
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
