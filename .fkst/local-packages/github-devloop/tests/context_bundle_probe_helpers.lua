local core = require("core")
local context_bundle = require("devloop.context_bundle")
local fixtures = require("tests.production_fixture_helpers")

M = {}

local max_bundle_file_len = 10 * 1024 * 1024

M.spec = {
  consumes = { "context_bundle_probe" },
  produces = { "context_bundle_probe_result" },
}

local function shell_single_quote(value)
  return "'" .. tostring(value):gsub("'", "'\\''") .. "'"
end

local function shell_quote_argv(value)
  local text = tostring(value or "")
  if text:find("^[%w_%-%./:=]+$") ~= nil then
    return text
  end
  return "'" .. text:gsub("'", "'\"'\"'") .. "'"
end

local function rendered_command(cmd)
  if type(cmd) ~= "table" then
    return tostring(cmd)
  end
  if cmd.cmd ~= nil then
    return tostring(cmd.cmd)
  end
  local argv = cmd.argv
  if type(argv) ~= "table" then
    return ""
  end
  local parts = {}
  for _, arg in ipairs(argv) do
    table.insert(parts, shell_quote_argv(arg))
  end
  return table.concat(parts, " ")
end

local function exec_with_env(root, fixtures)
  local state = fixtures or {}
  state.calls = state.calls or {}
  state.issue_outputs = state.issue_outputs or {
    '{"title":"Bundle issue","body":"Full issue body","updatedAt":"2026-06-03T01:02:03Z","state":"OPEN","labels":[],"comments":[]}\n',
  }
  state.pr_output = state.pr_output or '{"title":"Bundle PR","body":"PR body","headRefName":"devloop-owner-repo-42","headRefOid":"def456","baseRefName":"dev","state":"OPEN","updatedAt":"2026-06-04T01:02:03Z","comments":[],"labels":[]}\n'
  state.diff_output = state.diff_output or "diff --git a/file.lua b/file.lua\n+return true\n"
  return function(cmd)
    local rendered = rendered_command(cmd)
    table.insert(state.calls, rendered)
    if rendered == core.read_runtime_root_cmd() then
      return { stdout = root, stderr = "", exit_code = 0 }
    end
    if rendered:find("gh issue view", 1, true) ~= nil then
      local output = table.remove(state.issue_outputs, 1) or state.last_issue_output or ""
      state.last_issue_output = output
      return { stdout = output, stderr = "", exit_code = 0 }
    end
    if rendered:find("gh pr view", 1, true) ~= nil then
      return { stdout = state.pr_output, stderr = "", exit_code = 0 }
    end
    if rendered:find("gh pr diff", 1, true) ~= nil and rendered:find("--name-only", 1, true) ~= nil then
      return {
        stdout = state.diff_name_output or state.diff_output,
        stderr = state.diff_name_stderr or state.diff_stderr or "",
        exit_code = state.diff_name_exit_code or state.diff_exit_code or 0,
      }
    end
    if rendered:find("gh pr diff", 1, true) ~= nil then
      return {
        stdout = state.diff_output,
        stderr = state.diff_stderr or "",
        exit_code = state.diff_exit_code or 0,
      }
    end
    if rendered:find("gh issue list", 1, true) ~= nil then
      if rendered:find("--state closed", 1, true) ~= nil then
        return { stdout = state.closed_issue_list_output or "[]\n", stderr = "", exit_code = 0 }
      end
      return { stdout = state.open_issue_list_output or "[]\n", stderr = "", exit_code = 0 }
    end
    if rendered:find("gh pr list", 1, true) ~= nil then
      return { stdout = state.open_pr_list_output or "[]\n", stderr = "", exit_code = 0 }
    end
    local with_env = "FKST_RUNTIME_ROOT=" .. shell_single_quote(root) .. " " .. rendered
    local handle = io.popen(with_env .. " 2>&1")
    local stdout = handle:read("*a")
    local ok, _, status = handle:close()
    return {
      stdout = stdout or "",
      stderr = ok and "" or (stdout or ""),
      exit_code = ok and 0 or (status or 1),
    }
  end
end

local function build_args(root, fixtures, extra)
  local fields = extra or {}
  return {
    repo = "owner/repo",
    issue_number = fields.issue_number or 42,
    pr_number = fields.pr_number,
    proposal_id = fields.proposal_id or "github-devloop/issue/owner/repo/42",
    version = fields.version or "2026-06-03T01-02-03Z",
    tick = fields.tick or "2026-06-10T01:02:03Z",
    exec = exec_with_env(root, fixtures),
  }
end

local function manifest_paths(manifest)
  local paths = {}
  for line in (tostring(manifest or "") .. "\n"):gmatch("([^\n]*)\n") do
    local path = line:match(":%s*(/.+)%s*$")
    if path ~= nil then
      table.insert(paths, path)
    end
  end
  return paths
end

local function has_path_suffix(paths, suffix)
  for _, path in ipairs(paths or {}) do
    if tostring(path):sub(-#suffix) == suffix then
      return true
    end
  end
  return false
end

local function read_file(path)
  local handle = assert(io.open(path, "r"))
  local content = handle:read("*a")
  handle:close()
  return content
end

local function write_file(path, content)
  local handle = assert(io.open(path, "w"))
  handle:write(content)
  handle:close()
end

local function mkdir_p(path)
  local ok = os.execute("mkdir -p " .. shell_single_quote(path))
  if not (ok == true or ok == 0) then
    error("mkdir failed")
  end
end

local function count_calls(calls, needle)
  local count = 0
  for _, rendered in ipairs(calls or {}) do
    if rendered:find(needle, 1, true) ~= nil then
      count = count + 1
    end
  end
  return count
end

local function assert_readable_from_cwd(path, cwd)
  local cmd = "cd " .. shell_single_quote(cwd)
    .. " && test -r " .. shell_single_quote(path)
    .. " && cat " .. shell_single_quote(path) .. " >/dev/null"
  local ok = os.execute(cmd)
  if not (ok == true or ok == 0) then
    error("cross-cwd read failed")
  end
end

local function run_round_trip(root)
  local fixtures = {}
  local bundle = context_bundle.build_context_bundle(core, build_args(root, fixtures, { pr_number = 7 }))
  local paths = manifest_paths(context_bundle.context_bundle_manifest(bundle))
  local scratch = root .. "/isolated-scratch"
  mkdir_p(scratch)
  local contents = {}
  for _, path in ipairs(paths) do
    assert_readable_from_cwd(path, scratch)
    table.insert(contents, read_file(path))
  end
  return {
    paths = paths,
    contents = contents,
    manifest = context_bundle.context_bundle_manifest(bundle),
    issue_content = read_file(bundle.issue_path),
    notice_content = read_file(bundle.notice_path),
  }
end

local function run_deleted_file(root)
  local fixtures = {
    issue_outputs = {
      '{"title":"First issue","body":"first","updatedAt":"2026-06-03T01:02:03Z","state":"OPEN","labels":[],"comments":[]}\n',
      '{"title":"Second issue","body":"second","updatedAt":"2026-06-03T01:02:04Z","state":"OPEN","labels":[],"comments":[]}\n',
    },
  }
  local args = build_args(root, fixtures)
  local first = context_bundle.build_context_bundle(core, args)
  os.remove(first.issue_path)
  local second = context_bundle.build_context_bundle(core, args)
  return {
    first_dir = first.dir,
    second_dir = second.dir,
    issue_content = read_file(second.issue_path),
    issue_fetch_count = count_calls(fixtures.calls, "gh issue view"),
  }
end

local function run_preexisting(root)
  local fixtures = {}
  local dir = root .. "/context/github-devloop-issue-owner-repo-42/2026-06-03T01-02-03Z"
  mkdir_p(dir)
  write_file(dir .. "/UNTRUSTED-NOTICE.txt", "BEGIN UNTRUSTED BUNDLE DATA\npreexisting notice\nEND UNTRUSTED BUNDLE DATA\n")
  write_file(dir .. "/issue.json", "preexisting issue\n")
  write_file(dir .. "/board.txt", "preexisting board\n")
  local bundle = context_bundle.build_context_bundle(core, build_args(root, fixtures))
  return {
    dir = bundle.dir,
    expected_dir = dir,
    issue_content = read_file(bundle.issue_path),
    manifest = context_bundle.context_bundle_manifest(bundle),
    issue_fetch_count = count_calls(fixtures.calls, "gh issue view"),
  }
end

local function run_publish_reuse(root)
  local fixtures = {
    issue_outputs = {
      '{"title":"First publish","body":"first","updatedAt":"2026-06-03T01:02:03Z","state":"OPEN","labels":[],"comments":[]}\n',
      '{"title":"Second publish","body":"second","updatedAt":"2026-06-03T01:02:04Z","state":"OPEN","labels":[],"comments":[]}\n',
    },
  }
  local args = build_args(root, fixtures)
  local first = context_bundle.build_context_bundle(core, args)
  local before_notice = read_file(first.notice_path)
  local before_issue = read_file(first.issue_path)
  local before_board = read_file(first.board_path)
  local fetches_after_first = count_calls(fixtures.calls, "gh issue view")
  local second = context_bundle.build_context_bundle(core, args)
  return {
    first_dir = first.dir,
    second_dir = second.dir,
    fetches_after_first = fetches_after_first,
    fetches_after_second = count_calls(fixtures.calls, "gh issue view"),
    notice_unchanged = before_notice == read_file(first.notice_path),
    issue_unchanged = before_issue == read_file(first.issue_path),
    board_unchanged = before_board == read_file(first.board_path),
  }
end

local function run_publish_unique_on_invalid(root)
  local fixtures = {
    issue_outputs = {
      '{"title":"First publish","body":"first","updatedAt":"2026-06-03T01:02:03Z","state":"OPEN","labels":[],"comments":[]}\n',
      '{"title":"Rebuilt issue","body":"rebuilt","updatedAt":"2026-06-03T01:02:04Z","state":"OPEN","labels":[],"comments":[]}\n',
    },
  }
  local args = build_args(root, fixtures)
  local first = context_bundle.build_context_bundle(core, args)
  os.remove(first.notice_path)
  write_file(first.issue_path, "invalid first issue remains\n")
  local before_issue = read_file(first.issue_path)
  local before_board = read_file(first.board_path)
  local second = context_bundle.build_context_bundle(core, args)
  return {
    dir = second.dir,
    original_dir = first.dir,
    issue_fetch_count = count_calls(fixtures.calls, "gh issue view"),
    original_notice_absent = io.open(first.notice_path, "r") == nil,
    original_issue_unchanged = before_issue == read_file(first.issue_path),
    original_board_unchanged = before_board == read_file(first.board_path),
    rebuilt_issue = read_file(second.issue_path),
    manifest = context_bundle.context_bundle_manifest(second),
    has_notice = has_path_suffix(manifest_paths(context_bundle.context_bundle_manifest(second)), "/UNTRUSTED-NOTICE.txt"),
  }
end

local function run_utf8_truncation(root)
  local fixture_data = {
    issue_outputs = {
      string.rep("a", max_bundle_file_len - 1) .. fixtures.cjk_char() .. "tail",
    },
  }
  local bundle = context_bundle.build_context_bundle(core, build_args(root, fixture_data, { tick = nil }))
  return {
    issue_content = read_file(bundle.issue_path),
    issue_bytes = bundle.issue_bytes,
  }
end

local function run_stale_manifest_files(root)
  local fixtures = {}
  local args = build_args(root, fixtures)
  local ref = context_bundle.context_fetch_ref_from_bundle(core, args)
  local old_bundle = context_bundle.build_context_bundle(core, args)
  os.remove(old_bundle.issue_path)
  local ok, err = pcall(context_bundle.context_bundle_manifest_from_ref, core, ref, args.exec)
  return {
    ok = ok,
    error = tostring(err or ""),
    stale = context_bundle.is_stale_generation_context_error(err),
    class = context_bundle.stale_generation_context_error_class(),
  }
end

local function run_unknown_risk_structured(root)
  local fixtures = {
    diff_output = "diff --git a/file.lua b/file.lua\n+return true\n",
    diff_name_output = "",
    diff_name_exit_code = 1,
    diff_name_stderr = "diff unavailable",
  }
  local safe_suffix = tostring(root):gsub("[^%w._-]", "-")
  local args = build_args(root, fixtures, {
    pr_number = 7,
    proposal_id = "github-devloop/pr-review/owner-repo/1234567890/7/unknown-risk",
    version = "unknown-risk-" .. safe_suffix:sub(-48),
  })
  local ref, high_risk, risk = context_bundle.context_fetch_ref_from_bundle(core, args)
  return {
    ref = ref,
    high_risk = high_risk,
    risk_known = risk and risk.known,
    risk_high = risk and risk.high_risk,
    risk_reason = risk and risk.reason,
    high_risk_path_count = #(risk and risk.high_risk_paths or {}),
    diff_name_fetch_count = count_calls(fixtures.calls, "--name-only"),
  }
end

local function run_stale_manifest_rebuild(root)
  local old_root = root .. "/old"
  local fresh_root = root .. "/fresh"
  local proposal_id = "github-devloop/issue/owner/repo/42"
  local version = "owner/repo#issue#42@2026-06-03T01-02-03Z"
  local old_fixtures = {
    issue_outputs = {
      '{"title":"Old issue","body":"old","updatedAt":"2026-06-03T01:02:03Z","state":"OPEN","labels":[],"comments":[]}\n',
    },
  }
  local fresh_fixtures = {
    issue_outputs = {
      '{"title":"Fresh issue","body":"fresh","updatedAt":"2026-06-03T01:02:03Z","state":"OPEN","labels":[],"comments":[]}\n',
    },
  }
  local old_args = build_args(old_root, old_fixtures, { proposal_id = proposal_id, version = version })
  local old_ref = context_bundle.context_fetch_ref_from_bundle(core, old_args)
  local old_bundle = context_bundle.build_context_bundle(core, old_args)
  os.remove(old_bundle.issue_path)

  local stale_ok, stale_err = pcall(context_bundle.context_bundle_manifest_from_ref, core, old_ref, old_args.exec)
  local fresh_args = build_args(fresh_root, fresh_fixtures, { proposal_id = proposal_id, version = version })
  local fresh_ref = context_bundle.context_fetch_ref_from_bundle(core, fresh_args)
  local fresh_manifest = context_bundle.context_bundle_manifest_from_ref(core, fresh_ref, fresh_args.exec)
  return {
    stale_ok = stale_ok,
    stale = context_bundle.is_stale_generation_context_error(stale_err),
    same_ref = old_ref == fresh_ref,
    fresh_manifest = fresh_manifest,
    fresh_fetch_count = count_calls(fresh_fixtures.calls, "gh issue view"),
  }
end

function M.run(payload)
  local root = payload.root
  if payload.mode == "round_trip" then
    return run_round_trip(root)
  elseif payload.mode == "deleted_file" then
    return run_deleted_file(root)
  elseif payload.mode == "preexisting" then
    return run_preexisting(root)
  elseif payload.mode == "publish_reuse" then
    return run_publish_reuse(root)
  elseif payload.mode == "publish_unique_on_invalid" then
    return run_publish_unique_on_invalid(root)
  elseif payload.mode == "utf8_truncation" then
    return run_utf8_truncation(root)
  elseif payload.mode == "stale_manifest_files" then
    return run_stale_manifest_files(root)
  elseif payload.mode == "unknown_risk_structured" then
    return run_unknown_risk_structured(root)
  elseif payload.mode == "stale_manifest_rebuild" then
    return run_stale_manifest_rebuild(root)
  end
  error("unknown context bundle probe mode")
end

function pipeline(event)
  local payload = event.payload or {}
  local root = payload.root
  if root ~= nil then
    mkdir_p(root)
  end
  raise("context_bundle_probe_result", M.run(payload))
end

M.pipeline = pipeline

return M
