#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
GUARD="${REPO_ROOT}/tools/ci/guards/catch_exception_observability_guard.py"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

assert_fails_with() {
  local expected="$1"
  shift
  local output="${TMP_DIR}/failure.out"

  set +e
  "$@" > "${output}" 2>&1
  local status=$?
  set -e

  if [[ ${status} -eq 0 ]]; then
    echo "Expected command to fail: $*"
    cat "${output}"
    exit 1
  fi

  if ! rg -q "${expected}" "${output}"; then
    echo "Expected failure output to contain: ${expected}"
    cat "${output}"
    exit 1
  fi
}

write_fixture() {
  local name="$1"
  local body="$2"
  local dir="${TMP_DIR}/${name}"
  mkdir -p "${dir}"
  printf '%s\n' "${body}" > "${dir}/Fixture.cs"
  printf '%s\n' "${dir}/Fixture.cs"
}

debug_only="$(write_fixture debug-only '
using System;
public sealed class Fixture {
  private readonly dynamic _logger;
  public void Run() {
    try { Work(); }
    catch (Exception ex) { _logger.LogDebug(ex, "hidden"); }
  }
  private void Work() {}
}
')"

empty="$(write_fixture empty '
using System;
public sealed class Fixture {
  public void Run() {
    try { Work(); }
    catch (Exception) { }
  }
  private void Work() {}
}
')"

return_null="$(write_fixture return-null '
using System;
public sealed class Fixture {
  public string? Run() {
    try { return "ok"; }
    catch (Exception) { return null; }
  }
}
')"

warning="$(write_fixture warning '
using System;
public sealed class Fixture {
  private readonly dynamic _logger;
  public void Run() {
    try { Work(); }
    catch (Exception ex) { _logger.LogWarning(ex, "visible"); }
  }
  private void Work() {}
}
')"

when_pattern_warning="$(write_fixture when-pattern-warning '
using System;
public sealed class Fixture {
  private readonly dynamic _logger;
  public void Run() {
    try { Work(); }
    catch (Exception ex) when (TryBuildFallback(ex) is { } fallback) {
      _logger.LogWarning(ex, "visible");
      Use(fallback);
    }
  }
  private void Work() {}
  private object? TryBuildFallback(Exception ex) => new object();
  private void Use(object fallback) {}
}
')"

rethrow="$(write_fixture rethrow '
using System;
public sealed class Fixture {
  public void Run() {
    try { Work(); }
    catch (Exception) { throw; }
  }
  private void Work() {}
}
')"

committed_failure="$(write_fixture committed-failure '
using System;
public sealed class Fixture {
  public void Run() {
    try { Work(); }
    catch (Exception) { PublishAsync(new CommandRejectedEvent()); }
  }
  private void Work() {}
  private void PublishAsync(object evt) {}
}
public sealed class CommandRejectedEvent {}
')"

typed_debug="$(write_fixture typed-debug '
using System;
using System.Text.Json;
public sealed class Fixture {
  private readonly dynamic _logger;
  public void Run() {
    try { Work(); }
    catch (JsonException ex) { _logger.LogDebug(ex, "expected parse fallback"); }
  }
  private void Work() {}
}
')"

baseline_file="${TMP_DIR}/baseline.tsv"
{
  printf 'path\tline\tmessage\n'
  printf '%s\t7\t%s\n' "${debug_only#${TMP_DIR}/}" "broad catch logs only at Debug"
} > "${baseline_file}"

assert_fails_with "Debug" python3 "${GUARD}" --root "${TMP_DIR}" "${debug_only}"
assert_fails_with "empty broad catch" python3 "${GUARD}" --root "${TMP_DIR}" "${empty}"
assert_fails_with "returns null" python3 "${GUARD}" --root "${TMP_DIR}" "${return_null}"
python3 "${GUARD}" --root "${TMP_DIR}" --baseline "${baseline_file}" "${debug_only}"

python3 "${GUARD}" --root "${TMP_DIR}" "${warning}"
python3 "${GUARD}" --root "${TMP_DIR}" "${when_pattern_warning}"
python3 "${GUARD}" --root "${TMP_DIR}" "${rethrow}"
python3 "${GUARD}" --root "${TMP_DIR}" "${committed_failure}"
python3 "${GUARD}" --root "${TMP_DIR}" "${typed_debug}"

echo "catch exception observability guard tests passed"
