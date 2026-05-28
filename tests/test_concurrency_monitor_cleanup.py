import importlib
import os
import sys
import time
from pathlib import Path

import pytest


SCRIPT_DIR = Path(__file__).resolve().parents[1] / ".claude" / "skills" / "codex-refactor-loop" / "scripts"
sys.path.insert(0, str(SCRIPT_DIR))

concurrency_monitor = importlib.import_module("concurrency_monitor")


def write_file(path: Path, content: str = "x", age_seconds: int = 0) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content)
    if age_seconds:
        ts = time.time() - age_seconds
        os.utime(path, (ts, ts))
    return path


def patch_repo_root(monkeypatch: pytest.MonkeyPatch, repo_root: Path) -> None:
    monkeypatch.setattr(concurrency_monitor, "REPO_ROOT", repo_root)


def test_cleanup_logs_deletes_old_logs(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    patch_repo_root(monkeypatch, tmp_path)
    logs_dir = tmp_path / ".refactor-loop" / "logs"
    old_log = write_file(logs_dir / "old.log", age_seconds=4 * 86400)
    new_log = write_file(logs_dir / "new.log")

    concurrency_monitor.cleanup_logs()

    assert not old_log.exists()
    assert new_log.exists()


def test_cleanup_logs_prompts_uses_7d_threshold(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    patch_repo_root(monkeypatch, tmp_path)
    prompts_dir = tmp_path / ".refactor-loop" / "prompts"
    old_prompt = write_file(prompts_dir / "old.md", age_seconds=8 * 86400)
    new_prompt = write_file(prompts_dir / "new.md", age_seconds=6 * 86400)

    concurrency_monitor.cleanup_logs()

    assert not old_prompt.exists()
    assert new_prompt.exists()


def test_cleanup_logs_runs_uses_14d_threshold(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    patch_repo_root(monkeypatch, tmp_path)
    runs_dir = tmp_path / ".refactor-loop" / "runs"
    old_markdown = write_file(runs_dir / "old.md", age_seconds=15 * 86400)
    old_ndjson = write_file(runs_dir / "old.ndjson", age_seconds=15 * 86400)
    new_markdown = write_file(runs_dir / "new.md", age_seconds=13 * 86400)
    ignored_txt = write_file(runs_dir / "old.txt", age_seconds=15 * 86400)

    concurrency_monitor.cleanup_logs()

    assert not old_markdown.exists()
    assert not old_ndjson.exists()
    assert new_markdown.exists()
    assert ignored_txt.exists()


def test_cleanup_logs_deletes_old_tmp_codex_prompts(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    patch_repo_root(monkeypatch, tmp_path)
    fake_tmp = tmp_path / "tmp"
    old_prompt = write_file(fake_tmp / "codex-prompt-old.md", age_seconds=2 * 86400)
    new_prompt = write_file(fake_tmp / "codex-prompt-new.md")
    unrelated = write_file(fake_tmp / "other-prompt.md", age_seconds=2 * 86400)

    real_path = concurrency_monitor.Path

    def fake_path(value: str) -> Path:
        if value == "/tmp":
            return fake_tmp
        return real_path(value)

    monkeypatch.setattr(concurrency_monitor, "Path", fake_path)

    concurrency_monitor.cleanup_logs()

    assert not old_prompt.exists()
    assert new_prompt.exists()
    assert unrelated.exists()


def test_cleanup_logs_keeps_recent_files(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    patch_repo_root(monkeypatch, tmp_path)
    loop_dir = tmp_path / ".refactor-loop"
    files = [
        write_file(loop_dir / "logs" / "recent.log", age_seconds=2 * 86400),
        write_file(loop_dir / "prompts" / "recent.md", age_seconds=6 * 86400),
        write_file(loop_dir / "runs" / "recent.md", age_seconds=13 * 86400),
        write_file(loop_dir / "runs" / "recent.ndjson", age_seconds=13 * 86400),
    ]

    concurrency_monitor.cleanup_logs()

    assert all(path.exists() for path in files)


def test_main_runs_cleanup_every_60_ticks(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("CLEANUP_EVERY_TICKS", "60")
    monkeypatch.setattr(concurrency_monitor, "INTERVAL", 0)
    monkeypatch.setattr(concurrency_monitor, "tick", lambda: None)

    cleanup_calls = []

    def cleanup() -> None:
        cleanup_calls.append(len(cleanup_calls) + 1)

    sleeps = []

    def stop_after_sixty_ticks(_seconds: int) -> None:
        sleeps.append(None)
        if len(sleeps) == 60:
            raise SystemExit

    monkeypatch.setattr(concurrency_monitor, "cleanup_logs", cleanup)
    monkeypatch.setattr(concurrency_monitor.time, "sleep", stop_after_sixty_ticks)

    with pytest.raises(SystemExit):
        concurrency_monitor.main()

    assert len(sleeps) == 60
    assert cleanup_calls == [1]
