local h = require("tests.devloop_helpers")
local m_facts = require("devloop.markers.facts")
local t = h.t
local core = h.core
local git_fake = require("forge.git_fake")
local substrate_pin = require("departments.implement.substrate_pin")
local opts = h.opts
local ready = h.ready
local run_implement = h.run_implement
local mock_issue_implement = h.mock_issue_implement
local deterministic_branch_for = h.deterministic_branch_for
local mock_fresh_implement_worktree = h.mock_fresh_implement_worktree
local mock_implement_codex = h.mock_implement_codex
local mock_git_status = h.mock_git_status
local mock_git_commit = h.mock_git_commit
local count_calls = h.count_calls
local find_raise = h.find_raise

local current_base_pin = "2222222222222222222222222222222222222222"
local stale_queue_pin = "1111111111111111111111111111111111111111"

local function shell_quote(value)
  return "'" .. tostring(value):gsub("'", "'\\''") .. "'"
end

local function ensure_dir(path)
  local ok = os.execute("mkdir -p " .. shell_quote(path))
  if ok ~= true and ok ~= 0 then
    error("github-devloop test: mkdir failed for " .. tostring(path))
  end
end

local function copy(value)
  if type(value) ~= "table" then
    return value
  end
  local result = {}
  for key, field in pairs(value) do
    result[copy(key)] = copy(field)
  end
  return result
end

local function substrate_pin_git(files)
  local model = git_fake.model({})
  local handle = git_fake.new(model)
  handle.show_file = function(ref, path, timeout)
    table.insert(model.writes, {
      kind = "show_file",
      ref = tostring(ref),
      path = tostring(path),
      timeout = timeout,
    })
    local by_ref = files[tostring(ref)]
    local value = type(by_ref) == "table" and by_ref[tostring(path)] or nil
    if value == nil then
      return {
        stdout = "",
        stderr = "fatal: path '" .. tostring(path) .. "' does not exist in '" .. tostring(ref) .. "'\n",
        exit_code = 128,
      }
    end
    if type(value) == "table" then
      return copy(value)
    end
    return { stdout = tostring(value), stderr = "", exit_code = 0 }
  end
  return handle, model
end

local function find_comment_with(raises, text)
  return find_raise(raises, "github-proxy.github_issue_comment_request", function(payload)
    return tostring(payload.body or ""):find(text, 1, true) ~= nil
  end)
end

return {
  test_substrate_pin_refresh_skips_missing_base_pin_with_git_fake = function()
    local git = substrate_pin_git({})
    substrate_pin.refresh("/tmp/fkst-packages-test/github-devloop/no-pin-worktree", "devloop-owner-repo-42-01HY", "abc123", true, { git = git })

    t.eq(#git._model.writes, 1)
    t.eq(git._model.writes[1].kind, "show_file")
    t.eq(git._model.writes[1].ref, "abc123")
    t.eq(git._model.writes[1].path, ".fkst/substrate-ref")
  end,

  test_substrate_pin_refresh_propagates_git_error_with_git_fake = function()
    local git = substrate_pin_git({
      abc123 = { [".fkst/substrate-ref"] = { stdout = "", stderr = "", exit_code = 128 } },
    })

    local ok, err = pcall(function()
      substrate_pin.refresh("/tmp/fkst-packages-test/github-devloop/git-error-pin-worktree", "devloop-owner-repo-42-01HY", "abc123", true, { git = git })
    end)

    t.eq(ok, false)
    t.is_true(tostring(err):find("implement-substrate-pin-read-failed", 1, true) ~= nil)
    t.eq(#git._model.writes, 1)
    t.eq(git._model.writes[1].ref, "abc123")
  end,

  test_substrate_pin_refresh_keeps_pinned_repo_behavior_with_git_fake = function()
    local worktree = "/tmp/fkst-packages-test/github-devloop/pinned-worktree"
    ensure_dir(worktree .. "/.fkst")
    local branch = "devloop-owner-repo-42-01HY"
    local git = substrate_pin_git({
      abc123 = { [".fkst/substrate-ref"] = current_base_pin .. "\n" },
      [branch] = { [".fkst/substrate-ref"] = stale_queue_pin .. "\n" },
    })
    t.mock_command("add -A", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("commit -m 'chore: refresh fkst-substrate pin'", {
      stdout = "[devloop-owner-repo-42-01HY 9999999] chore: refresh fkst-substrate pin\n",
      stderr = "",
      exit_code = 0,
    })

    substrate_pin.refresh(worktree, branch, "abc123", true, { git = git })

    t.eq(file.read(worktree .. "/.fkst/substrate-ref"), current_base_pin .. "\n")
    t.eq(#git._model.writes, 2)
    t.eq(git._model.writes[1].ref, "abc123")
    t.eq(git._model.writes[2].ref, branch)
    t.eq(count_calls("commit -m 'chore: refresh fkst-substrate pin'"), 1)
  end,

  test_substrate_pin_refresh_errors_on_present_malformed_base_pin = function()
    local git = substrate_pin_git({
      abc123 = { [".fkst/substrate-ref"] = "not-a-sha\n" },
    })

    local ok, err = pcall(function()
      substrate_pin.refresh("/tmp/fkst-packages-test/github-devloop/malformed-pin-worktree", "devloop-owner-repo-42-01HY", "abc123", true, { git = git })
    end)

    t.eq(ok, false)
    t.is_true(tostring(err):find("implement-substrate-pin-invalid", 1, true) ~= nil)
    t.eq(#git._model.writes, 1)
  end,

  test_implement_ready_runs_codex_in_worktree_and_marks_implementing = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    mock_issue_implement({ "fkst-dev:ready", "fkst-dev:thinking" })
    mock_fresh_implement_worktree()
    mock_implement_codex(0, "implemented")
    mock_git_status(" M packages/github-devloop/core.lua\n")
    mock_git_commit("def456", branch)
    local result = run_implement(event, opts("implement-success"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 4)
    local attempt_raise = find_comment_with(result.raises, "fkst:github-devloop:implement-attempt:v1")
    t.is_true(attempt_raise.payload.body:find('proposal="' .. event.proposal_id .. '"', 1, true) ~= nil)
    t.is_true(attempt_raise.payload.body:find('dedup="' .. event.dedup_key .. '"', 1, true) ~= nil)
    local label_raise = find_raise(result.raises, "github-proxy.github_issue_label_request")
    local state_raise = find_raise(result.raises, "github-proxy.github_issue_comment_request", function(payload)
      return tostring(payload.body or ""):find("github-devloop implementation worktree ready", 1, true) ~= nil
    end)
    local comment_raise = find_raise(result.raises, "github-proxy.github_issue_comment_request", function(payload)
      return tostring(payload.body or ""):find("github-devloop implementation output published", 1, true) ~= nil
    end)
    t.eq(label_raise.payload.add_labels[1], "fkst-dev:implementing")
    t.eq(#label_raise.payload.remove_labels, 12)
    t.eq(attempt_raise.payload.body, state_raise.payload.body)
    t.is_true(state_raise.payload.body:find(core.state_marker(event.proposal_id, "implementing", event.dedup_key), 1, true) ~= nil)
    t.is_true(state_raise.payload.body:find("fkst:github-devloop:implement-attempt:v1", 1, true) ~= nil)
    t.eq(m_facts.implementing_fact(core, { state_raise.payload.body }, event.proposal_id, event.dedup_key), nil)
    t.is_true(comment_raise.payload.body:find("github-devloop implementation output published", 1, true) ~= nil)
    t.eq(comment_raise.payload.body:find(core.state_marker(event.proposal_id, "implementing", event.dedup_key), 1, true), nil)
    local outcome_attempt_raise = find_comment_with(result.raises, "github-devloop implementation attempt started")
    t.is_true(outcome_attempt_raise ~= nil)
    t.eq(outcome_attempt_raise.payload.body:find(core.state_marker(event.proposal_id, "implementing", event.dedup_key), 1, true), nil)
    local fact = m_facts.implementing_fact(core, { comment_raise.payload.body }, event.proposal_id, event.dedup_key)
    t.eq(fact.branch, branch)
    t.eq(fact.head_sha, "def456")
    local calls = t.command_calls()
    local saw_worktree_prefix = false
    local saw_prompt = false
    for _, call in ipairs(calls) do
      if call.rendered:find("codex exec", 1, true) ~= nil then
        saw_worktree_prefix = call.rendered:find("devloop-owner-repo-42", 1, true) ~= nil
        saw_prompt = call.stdin:find("Do not open a pull request.", 1, true) ~= nil
      end
    end
    t.eq(saw_worktree_prefix, true)
    t.eq(saw_prompt, true)
    t.eq(count_calls("git -C"), 10)
    t.eq(count_calls("git worktree add -b"), 1)
    t.eq(count_calls("codex exec"), 1)
    t.eq(count_calls("status --porcelain"), 1)
    t.eq(count_calls("add -A"), 2)
    t.eq(count_calls("commit -m"), 2)
  end,

  test_implement_missing_substrate_ref_still_spawns_codex = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    mock_issue_implement({ "fkst-dev:ready", "fkst-dev:thinking" })
    mock_fresh_implement_worktree({
      base_pin = {
        stdout = "",
        stderr = "fatal: path '.fkst/substrate-ref' does not exist in 'abc123'\n",
        exit_code = 128,
      },
    })
    mock_implement_codex(0, "implemented without substrate pin")
    mock_git_status(" M packages/github-devloop/core.lua\n")
    mock_git_commit("def456", branch)

    local result = run_implement(event, opts("implement-missing-substrate-pin"))
    t.eq(result.exit_code, 0)
    t.eq(count_calls("codex exec"), 1)
    t.eq(count_calls("commit -m 'chore: refresh fkst-substrate pin'"), 0)
    t.eq(#result.raises, 4)
  end,

  test_implement_refreshes_substrate_ref_to_current_base_before_codex = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    mock_issue_implement({ "fkst-dev:ready", "fkst-dev:thinking" })
    local worktree = mock_fresh_implement_worktree(nil, current_base_pin, stale_queue_pin)
    mock_implement_codex(0, "implemented after current substrate pin")
    mock_git_status(" M packages/github-devloop/core.lua\n")
    mock_git_commit("def456", branch)

    local result = run_implement(event, opts("implement-refreshes-substrate-pin"))
    t.eq(result.exit_code, 0)
    t.eq(file.read(worktree .. "/.fkst/substrate-ref"), current_base_pin .. "\n")

    local base_pin_read_index = nil
    local branch_pin_read_index = nil
    local pin_commit_index = nil
    local codex_index = nil
    for index, call in ipairs(t.command_calls()) do
      if call.rendered:find("git show abc123:.fkst/substrate-ref", 1, true) ~= nil then
        base_pin_read_index = base_pin_read_index or index
      elseif call.rendered:find("git show", 1, true) ~= nil
        and call.rendered:find(":.fkst/substrate-ref", 1, true) ~= nil then
        branch_pin_read_index = branch_pin_read_index or index
      elseif call.rendered:find("commit -m 'chore: refresh fkst-substrate pin'", 1, true) ~= nil then
        pin_commit_index = index
      elseif call.rendered:find("codex exec", 1, true) ~= nil then
        codex_index = index
      end
    end
    t.is_true(base_pin_read_index ~= nil)
    t.is_true(branch_pin_read_index ~= nil)
    t.is_true(pin_commit_index ~= nil)
    t.is_true(codex_index ~= nil)
    t.is_true(base_pin_read_index < codex_index)
    t.is_true(branch_pin_read_index < codex_index)
    t.is_true(pin_commit_index < codex_index)
  end,
}
