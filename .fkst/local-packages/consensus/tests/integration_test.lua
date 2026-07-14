local core = require("core")
local t = fkst.test
require("tests.cache_seed_helpers")
local verdict_label = "⟦FKST:VERDICT⟧"
local reply_label = "⟦FKST:REPLY⟧"
local angle_roles = { minimal = true, structural = true, delete = true }

local function nonce()
  return tostring({}):gsub("[^%w._-]", "_")
end

local function runtime_root(name)
  return "/tmp/fkst-packages-test/consensus/" .. tostring(now()) .. "/" .. nonce() .. "/" .. name
end

local function shell_single_quote(value)
  return "'" .. tostring(value):gsub("'", "'\\''") .. "'"
end

local function opts(name)
  return {
    env = {
      FKST_RUNTIME_ROOT = runtime_root(name),
    },
  }
end

local function proposal(extra)
  local value = {
    schema = "consensus.proposal.v1",
    proposal_id = "proposal-42",
    title = "Adopt consensus package",
    body = "Create a small flat package that asks several angles to judge a proposal.",
    content_fetch = "fetch-source --ref demo/consensus/42 --full",
    context = "The package must stay silent unless all angles agree.",
    angles = { "minimal", "structural", "delete" },
    dedup_key = "proposal-42-v1",
    -- Source-agnostic sample: an opaque {kind, ref} pointer, not tied to any provider.
    source_ref = {
      kind = "proposal",
      ref = "demo/consensus/42",
    },
  }
  for key, field in pairs(extra or {}) do
    value[key] = field
  end
  return value
end

local function run_decide(event_payload, run_opts)
  return t.run_department("departments/decide/main.lua", {
    queue = "proposal",
    payload = event_payload,
  }, run_opts)
end

local function seed_cache(key, value, run_opts)
  return t.run_department("departments/test_cache_seed/main.lua", {
    queue = "cache_seed",
    payload = {
      key = key,
      value = value,
    },
  }, run_opts)
end

local function codex_calls()
  local calls = {}
  for _, call in ipairs(t.command_calls()) do
    if call.rendered:find("codex exec", 1, true) ~= nil then
      table.insert(calls, call)
    end
  end
  return calls
end

local function assert_call_contains(calls, expected)
  for _, call in ipairs(calls) do
    if tostring(call.stdin or ""):find(expected, 1, true) ~= nil then
      return
    end
  end
  error("missing codex stdin fragment: " .. expected)
end

local function count_verdicts(items, verdict)
  local count = 0
  for _, item in ipairs(items or {}) do
    if item.verdict == verdict then
      count = count + 1
    end
  end
  return count
end

local function assert_judgment_worktree(call, role)
  t.is_true(call.rendered:find(" -C ", 1, true) ~= nil)
  t.is_true(call.rendered:find("/judgment-worktrees/consensus-" .. role, 1, true) ~= nil)
  t.is_nil(call.rendered:find("/worktrees/", 1, true))
end

local function judgment_call(role)
  for _, call in ipairs(codex_calls()) do
    if call.rendered:find("/judgment-worktrees/consensus-" .. role, 1, true) ~= nil then
      return call
    end
  end
  return nil
end

local function assert_judgment_dir_created_without_permission_control(count)
  local seen = 0
  for _, call in ipairs(t.command_calls()) do
    if call.rendered:find("mkdir -p", 1, true) ~= nil
      and call.rendered:find("/judgment-worktrees/consensus-", 1, true) ~= nil then
      seen = seen + 1
      t.is_nil(call.rendered:find("chmod", 1, true))
    end
  end
  t.eq(seen, count)
end

local function mock_judgment_runtime()
  t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
    stdout = "/tmp/fkst-packages-test/consensus/runtime",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_judgment_dir()
  t.mock_command("mkdir -p", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
end

local function angle_mock_pattern(angle)
  if angle == nil then
    return "codex exec"
  end
  return "consensus-angle-" .. tostring(angle)
end

local function mock_angle(angle, verdict, reply, exit_code)
  mock_judgment_dir()
  local gap = verdict == "reject" and "\n" .. "⟦FKST:GAP⟧ " .. tostring(reply):sub(1, 80) or ""
  t.mock_command(angle_mock_pattern(angle), {
    stdout = verdict_label .. " " .. verdict .. "\n" .. reply_label .. " " .. reply .. gap .. "\n",
    stderr = "",
    exit_code = exit_code or 0,
  })
end

local function mock_meta(line, exit_code)
  mock_judgment_dir()
  t.mock_command("meta-judge", {
    stdout = tostring(line or "") .. "\n",
    stderr = "",
    exit_code = exit_code or 0,
  })
end

return {
  test_all_angles_approve_raises_consensus_reached = function()
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_angle("structural", "approve", "Structural angle approves.")
    mock_angle("delete", "approve", "Delete angle approves.")

    local result = run_decide(proposal(), opts("all-approve"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "consensus_reached")
    t.eq(result.raises[1].payload.schema, "consensus.consensus_reached.v1")
    t.eq(result.raises[1].payload.proposal_id, "proposal-42")
    t.eq(result.raises[1].payload.decision, "approve")
    t.eq(result.raises[1].payload.dedup_key, "consensus:proposal-42-v1")
    t.eq(result.raises[1].payload.source_ref.kind, "proposal")
    t.eq(result.raises[1].payload.source_ref.ref, "demo/consensus/42")
    t.eq(#result.raises[1].payload.angle_results, 3)
    t.eq(result.raises[1].payload.angle_results[1].angle, "minimal")
    t.eq(result.raises[1].payload.angle_results[2].angle, "structural")
    t.eq(result.raises[1].payload.angle_results[3].angle, "delete")

    local calls = codex_calls()
    t.eq(#calls, 3)
    assert_call_contains(calls, "Angle: minimal")
    assert_call_contains(calls, "Angle: structural")
    assert_call_contains(calls, "Angle: delete")
    assert_call_contains(calls, "source_ref.ref: demo/consensus/42")
    assert_call_contains(calls, "fetch-source --ref demo/consensus/42 --full")
    local minimal_call = judgment_call("angle-minimal")
    local structural_call = judgment_call("angle-structural")
    local delete_call = judgment_call("angle-delete")
    t.is_true(minimal_call ~= nil)
    t.is_true(structural_call ~= nil)
    t.is_true(delete_call ~= nil)
    assert_judgment_worktree(minimal_call, "angle-minimal")
    assert_judgment_worktree(structural_call, "angle-structural")
    assert_judgment_worktree(delete_call, "angle-delete")
    assert_judgment_dir_created_without_permission_control(3)
    t.is_true(minimal_call.stdin:find("Angle: minimal", 1, true) ~= nil)
    t.is_true(minimal_call.stdin:find("source_ref.ref: demo/consensus/42", 1, true) ~= nil)
    t.is_true(minimal_call.stdin:find("fetch-source --ref demo/consensus/42 --full", 1, true) ~= nil)
    t.is_true(minimal_call.stdin:find("Do not clone, checkout, fetch with git", 1, true) ~= nil)
    t.is_true(structural_call.stdin:find("Angle: structural", 1, true) ~= nil)
    t.is_true(delete_call.stdin:find("Angle: delete", 1, true) ~= nil)
  end,

  test_codex_stdin_carries_fetch_instruction_not_full_body = function()
    local full_tail = "FULL_BODY_TAIL_MUST_NOT_REACH_CODEX"
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_angle("structural", "approve", "Structural angle approves.")
    mock_angle("delete", "approve", "Delete angle approves.")

    local result = run_decide(proposal({
      body = "Brief only.",
      content_fetch = "fetch-source --ref demo/consensus/42 --full",
      context = nil,
      full_body = string.rep("x", 16000) .. full_tail,
    }), opts("stdin-fetch-not-full-body"))

    t.eq(result.exit_code, 0)
    local calls = codex_calls()
    t.eq(#calls, 3)
    local minimal_call = judgment_call("angle-minimal")
    t.is_true(minimal_call.stdin:find("Brief only.", 1, true) ~= nil)
    t.is_true(minimal_call.stdin:find("fetch-source --ref demo/consensus/42 --full", 1, true) ~= nil)
    t.is_nil(minimal_call.stdin:find(full_tail, 1, true))
  end,

  test_codex_stdin_resolves_runtime_cache_context_manifest = function()
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_angle("structural", "approve", "Structural angle approves.")
    mock_angle("delete", "approve", "Delete angle approves.")
    local run_opts = opts("stdin-runtime-cache-context")
    local root = run_opts.env.FKST_RUNTIME_ROOT
    os.execute("mkdir -p " .. shell_single_quote(root .. "/ctx"))
    local issue = assert(io.open(root .. "/ctx/issue.json", "w"))
    issue:write("issue")
    issue:close()
    local diff = assert(io.open(root .. "/ctx/diff.patch", "w"))
    diff:write("diff")
    diff:close()
    local notice = assert(io.open(root .. "/ctx/UNTRUSTED-NOTICE.txt", "w"))
    notice:write("notice")
    notice:close()
    seed_cache("consensus-test/context", "Untrusted notice: " .. root .. "/ctx/UNTRUSTED-NOTICE.txt\nIssue JSON: " .. root .. "/ctx/issue.json\nPR diff patch: " .. root .. "/ctx/diff.patch", run_opts)

    local result = run_decide(proposal({
      content_fetch = "runtime-cache:consensus-test/context",
    }), run_opts)

    t.eq(result.exit_code, 0)
    local calls = codex_calls()
    t.eq(#calls, 3)
    local minimal_call = judgment_call("angle-minimal")
    t.is_true(minimal_call.stdin:find(root .. "/ctx/issue.json", 1, true) ~= nil)
    t.is_true(minimal_call.stdin:find(root .. "/ctx/diff.patch", 1, true) ~= nil)
    t.is_nil(minimal_call.stdin:find("runtime-cache:consensus-test/context", 1, true))
  end,

  test_runtime_cache_context_manifest_missing_file_ack_drops_without_judgment = function()
    mock_judgment_runtime()
    local run_opts = opts("stdin-runtime-cache-missing-file")
    seed_cache("consensus-test/missing-context", "Issue JSON: /tmp/fkst-packages-test/consensus/missing-file.json", run_opts)

    local result = run_decide(proposal({
      content_fetch = "runtime-cache:consensus-test/missing-context",
    }), run_opts)

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(#codex_calls(), 0)
  end,

  test_runtime_cache_context_cache_miss_is_terminal_ack_drop = function()
    mock_judgment_runtime()

    local result = run_decide(proposal({
      content_fetch = "runtime-cache:consensus-test/stale-missing-context",
    }), opts("stdin-runtime-cache-stale-miss"))

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(#codex_calls(), 0)
  end,

  test_runtime_cache_context_unreadable_manifest_file_is_terminal_ack_drop = function()
    mock_judgment_runtime()
    local run_opts = opts("stdin-runtime-cache-stale-file")
    local root = run_opts.env.FKST_RUNTIME_ROOT
    os.execute("mkdir -p " .. shell_single_quote(root .. "/ctx"))
    local issue = assert(io.open(root .. "/ctx/issue.json", "w"))
    issue:write("issue")
    issue:close()
    local notice = assert(io.open(root .. "/ctx/UNTRUSTED-NOTICE.txt", "w"))
    notice:write("notice")
    notice:close()
    seed_cache("consensus-test/stale-file", "Untrusted notice: " .. root .. "/ctx/UNTRUSTED-NOTICE.txt\nIssue JSON: " .. root .. "/ctx/issue.json", run_opts)
    os.remove(root .. "/ctx/issue.json")

    local result = run_decide(proposal({
      content_fetch = "runtime-cache:consensus-test/stale-file",
    }), run_opts)

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(#codex_calls(), 0)
  end,

  test_unanimous_abstain_raises_consensus_converge = function()
    mock_judgment_runtime()
    mock_angle("minimal", "abstain", "Minimal angle needs narrower scope.")
    mock_angle("structural", "abstain", "Structural angle needs clearer boundaries.")
    mock_angle("delete", "abstain", "Delete angle needs proof the scope is necessary.")
    mock_meta("converge: What concrete evidence would make the narrowed scope approvable?")

    local result = run_decide(proposal(), opts("all-abstain"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "consensus_converge")
    t.eq(result.raises[1].payload.narrowed_question, "What concrete evidence would make the narrowed scope approvable?")
    t.eq(#codex_calls(), 4)
  end,

  test_split_verdicts_spawn_meta_and_raise_consensus_converge = function()
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_angle("structural", "abstain", "Structural angle needs one blocker resolved.")
    mock_angle("delete", "approve", "Delete angle approves.")
    mock_meta("converge: Should structural concerns block this proposal?")

    local result = run_decide(proposal(), opts("split"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "consensus_converge")
    t.eq(result.raises[1].payload.schema, "consensus.consensus_converge.v1")
    t.eq(result.raises[1].payload.proposal_id, "proposal-42")
    t.eq(result.raises[1].payload.dedup_key, "consensus:proposal-42-v1")
    t.eq(result.raises[1].payload.round, 0)
    t.eq(result.raises[1].payload.narrowed_question, "Should structural concerns block this proposal?")
    t.eq(result.raises[1].payload.source_ref.kind, "proposal")
    t.eq(result.raises[1].payload.source_ref.ref, "demo/consensus/42")
    t.eq(#result.raises[1].payload.angle_digests, 3)
    t.eq(count_verdicts(result.raises[1].payload.angle_digests, "approve"), 2)
    t.eq(count_verdicts(result.raises[1].payload.angle_digests, "abstain"), 1)
    t.is_nil(result.raises[1].payload.body)
    t.is_nil(result.raises[1].payload.angle_results)
    t.is_nil(result.raises[1].payload.decision)
    local calls = codex_calls()
    t.eq(#calls, 4)
    local meta_call = judgment_call("meta-judge")
    assert_judgment_worktree(meta_call, "meta-judge")
    t.is_true(meta_call.stdin:find("Angle outputs:", 1, true) ~= nil)
    t.is_true(meta_call.stdin:find("You are running in an empty runtime scratch directory", 1, true) ~= nil)
  end,

  test_duplicate_converge_delivery_redecides_but_emits_stable_dedup_key = function()
    local run_opts = opts("duplicate-converge-delivery")
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_angle("structural", "abstain", "Structural angle needs one blocker resolved.")
    mock_angle("delete", "approve", "Delete angle approves.")
    mock_meta("converge: Should structural concerns block this proposal?")

    local first = run_decide(proposal(), run_opts)
    t.eq(first.exit_code, 0)
    t.eq(#first.raises, 1)
    t.eq(first.raises[1].queue, "consensus_converge")
    t.eq(first.raises[1].payload.dedup_key, "consensus:proposal-42-v1")
    t.eq(first.raises[1].payload.narrowed_question, "Should structural concerns block this proposal?")

    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves on replay.")
    mock_angle("structural", "abstain", "Structural angle still needs one blocker resolved.")
    mock_angle("delete", "approve", "Delete angle approves on replay.")
    mock_meta("converge: Replay may re-decide, but downstream dedup must see the same key.")

    local second = run_decide(proposal(), run_opts)
    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 1)
    t.eq(second.raises[1].queue, "consensus_converge")
    t.eq(second.raises[1].payload.dedup_key, "consensus:proposal-42-v1")
    t.eq(second.raises[1].payload.round, 0)
    t.eq(second.raises[1].payload.source_ref.kind, "proposal")
    t.eq(second.raises[1].payload.source_ref.ref, "demo/consensus/42")
    t.eq(second.raises[1].payload.narrowed_question, "Replay may re-decide, but downstream dedup must see the same key.")
    t.eq(#codex_calls(), 8)
  end,

  test_meta_plan_flows_into_next_converge_round = function()
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle accepts a small adapter.")
    mock_angle("structural", "abstain", "Structural angle wants the retry boundary explicit.")
    mock_angle("delete", "approve", "Delete angle accepts removing duplicate wiring.")
    mock_meta("⟦FKST:PLAN⟧ Keep the adapter, make retry ownership explicit, and delete duplicate wiring.")

    local result = run_decide(proposal(), opts("split-meta-plan"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "consensus_converge")
    t.eq(result.raises[1].payload.narrowed_question, "Keep the adapter, make retry ownership explicit, and delete duplicate wiring.")
    t.eq(#codex_calls(), 4)
  end,

  test_malformed_plan_falls_back_to_default_converge = function()
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_angle("structural", "abstain", "Structural angle needs framing.")
    mock_angle("delete", "approve", "Delete angle approves.")
    mock_meta("⟦FKST:PLAN⟧")

    local result = run_decide(proposal(), opts("malformed-meta-plan"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "consensus_converge")
    t.is_true(result.raises[1].payload.narrowed_question:find("Resolve the concrete disagreement", 1, true) ~= nil)
    t.eq(#codex_calls(), 4)
  end,

  test_converge_mode_reject_outputs_raise_consensus_converge = function()
    mock_judgment_runtime()
    mock_angle("minimal", "reject", "Minimal angle rejects but converge mode cannot reject.")
    mock_angle("structural", "approve", "Structural angle approves.")
    mock_angle("delete", "approve", "Delete angle approves.")
    mock_meta("converge: What concern prevents approval?")

    local result = run_decide(proposal({ verdict_mode = "converge" }), opts("converge-reject-output"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "consensus_converge")
    t.eq(count_verdicts(result.raises[1].payload.angle_digests, "invalid"), 1)
    t.eq(result.raises[1].payload.narrowed_question, "What concern prevents approval?")
    t.eq(#codex_calls(), 4)
  end,

  test_gate_mode_any_reject_raises_consensus_reached_reject_with_gap = function()
    mock_judgment_runtime()
    mock_angle("minimal", "reject", "Minimal angle rejects the diff.")
    mock_angle("structural", "approve", "Structural angle approves.")
    mock_angle("delete", "comment", "Delete angle has advisory feedback.")

    local result = run_decide(proposal({ verdict_mode = "gate" }), opts("gate-any-reject"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "consensus_reached")
    t.eq(result.raises[1].payload.decision, "reject")
    t.eq(result.raises[1].payload.blocking_gap, "Minimal angle rejects the diff.")
    t.eq(#codex_calls(), 3)
  end,

  test_gate_mode_approve_with_comment_raises_consensus_reached_approve = function()
    mock_judgment_runtime()
    mock_angle("minimal", "comment", "Minimal angle notes naming could improve.")
    mock_angle("structural", "approve", "Structural angle approves.")
    mock_angle("delete", "abstain", "Delete angle cannot judge.")

    local result = run_decide(proposal({ verdict_mode = "gate" }), opts("gate-approve-comment"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "consensus_reached")
    t.eq(result.raises[1].payload.decision, "approve")
    t.is_true(result.raises[1].payload.body:find("Advisory (non-blocking):", 1, true) ~= nil)
    t.eq(#codex_calls(), 3)
  end,

  test_meta_reached_after_split_raises_consensus_reached = function()
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_angle("structural", "abstain", "Structural angle abstains but accepts the narrowed framing.")
    mock_angle("delete", "approve", "Delete angle approves.")
    mock_meta("reached:approve approve the narrowed framing")

    local result = run_decide(proposal(), opts("split-meta-reached"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "consensus_reached")
    t.eq(result.raises[1].payload.schema, "consensus.consensus_reached.v1")
    t.eq(result.raises[1].payload.decision, "approve")
    t.eq(result.raises[1].payload.framing, "approve approve the narrowed framing")
    t.eq(result.raises[1].payload.body:find("Meta-judge framing:", 1, true), nil)
    t.eq(#codex_calls(), 4)
  end,

  test_meta_reached_with_failed_angle_falls_back_to_consensus_converge = function()
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_judgment_dir()
    t.mock_command("consensus-angle-structural", {
      stderr = "forced failure",
      exit_code = 7,
    })
    mock_angle("delete", "abstain", "Delete angle abstains.")
    mock_meta("reached:approve approve the narrowed framing")

    local run_opts = opts("split-meta-reached-degraded")
    local result = run_decide(proposal({
      dedup_key = "proposal-42-v1/split-meta-reached-degraded",
    }), run_opts)

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "consensus_converge")
    t.is_nil(cache_get(core.reached_cache_key("proposal-42-v1/split-meta-reached-degraded")))
    t.eq(#codex_calls(), 4)
  end,

  test_meta_reached_with_failed_angle_falls_back_to_consensus_converge_in_gate_mode = function()
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_judgment_dir()
    t.mock_command("consensus-angle-structural", {
      stderr = "forced failure",
      exit_code = 7,
    })
    mock_angle("delete", "comment", "Delete angle notes a non-blocking concern.")
    mock_meta("reached:approve approve the narrowed framing")

    local run_opts = opts("gate-split-meta-reached-degraded")
    local result = run_decide(proposal({
      verdict_mode = "gate",
      dedup_key = "proposal-42-v1/gate-split-meta-reached-degraded",
    }), run_opts)

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "consensus_converge")
    t.is_nil(cache_get(core.reached_cache_key("proposal-42-v1/gate-split-meta-reached-degraded")))
    t.eq(#codex_calls(), 4)
  end,

  test_abstain_raises_consensus_converge = function()
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_angle("structural", "abstain", "Structural angle abstains.")
    mock_angle("delete", "approve", "Delete angle approves.")
    mock_meta("converge: Ask structural to name the one blocker that prevents approval.")

    local result = run_decide(proposal(), opts("abstain"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "consensus_converge")
    t.eq(#codex_calls(), 4)
  end,

  test_failed_codex_call_raises_consensus_converge = function()
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_judgment_dir()
    t.mock_command("consensus-angle-structural", {
      stderr = "forced failure",
      exit_code = 7,
    })
    mock_angle("delete", "approve", "Delete angle approves.")
    mock_meta("converge: Retry the failed structural angle with a concrete blocker.")

    local result = run_decide(proposal(), opts("codex-fails"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "consensus_converge")
    local invalid = false
    for _, digest in ipairs(result.raises[1].payload.angle_digests) do
      if digest.verdict == "invalid" then
        invalid = true
      end
    end
    t.eq(invalid, true)
    t.eq(#codex_calls(), 4)
  end,

  test_unparseable_output_raises_consensus_converge_with_default_question = function()
    mock_judgment_runtime()
    mock_judgment_dir()
    t.mock_command("consensus-angle-minimal", { stdout = "no verdict here", exit_code = 0 })
    mock_judgment_dir()
    t.mock_command("consensus-angle-structural", { stdout = "still nothing useful", exit_code = 0 })
    mock_judgment_dir()
    t.mock_command("consensus-angle-delete", { stdout = "garbage output", exit_code = 0 })
    mock_meta("malformed")

    local result = run_decide(proposal(), opts("unparseable"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "consensus_converge")
    t.is_true(result.raises[1].payload.narrowed_question:find("Resolve the concrete disagreement", 1, true) ~= nil)
    t.eq(#codex_calls(), 4)
  end,

  test_missing_source_ref_fails_closed_without_codex = function()
    local result = run_decide(proposal({ source_ref = false }), opts("no-source-ref"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    -- fail-closed BEFORE spawning any codex angle
    t.eq(#codex_calls(), 0)
  end,

  test_angles_override_runs_only_named_angles = function()
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_angle("delete", "approve", "Delete angle approves.")

    local result = run_decide(proposal({ angles = { "minimal", "delete" } }), opts("angles-override"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].payload.decision, "approve")
    t.eq(#result.raises[1].payload.angle_results, 2)

    local calls = codex_calls()
    t.eq(#calls, 2)
    assert_call_contains(calls, "Angle: minimal")
    assert_call_contains(calls, "Angle: delete")
    t.is_true(judgment_call("angle-minimal").stdin:find("Angle: minimal", 1, true) ~= nil)
    t.is_true(judgment_call("angle-delete").stdin:find("Angle: delete", 1, true) ~= nil)
  end,

  test_same_dedup_key_skips_second_run = function()
    local run_opts = opts("cache-hit")
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_angle("structural", "approve", "Structural angle approves.")
    mock_angle("delete", "approve", "Delete angle approves.")

    local first = run_decide(proposal(), run_opts)
    t.eq(first.exit_code, 0)
    t.eq(#first.raises, 1)

    -- identical dedup_key -> idempotent skip, no new codex calls
    local second = run_decide(proposal(), run_opts)
    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 0)
    t.eq(#codex_calls(), 3)
  end,

  test_same_decision_dedup_key_skips_updated_effect_version_refire = function()
    local run_opts = opts("effect-version-refire")
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_angle("structural", "approve", "Structural angle approves.")
    mock_angle("delete", "approve", "Delete angle approves.")

    local first = run_decide(proposal({
      dedup_key = "proposal-42/intake/1234567890",
      effect_version = "intake/proposal-42/2026-06-03T01-02-03Z",
    }), run_opts)
    t.eq(first.exit_code, 0)
    t.eq(#first.raises, 1)
    t.eq(first.raises[1].payload.dedup_key, "consensus:proposal-42/intake/1234567890")
    t.eq(first.raises[1].payload.effect_version, "intake/proposal-42/2026-06-03T01-02-03Z")

    local second = run_decide(proposal({
      dedup_key = "proposal-42/intake/1234567890",
      effect_version = "intake/proposal-42/2026-06-03T01-22-03Z",
    }), run_opts)
    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 0)
    t.eq(#codex_calls(), 3)
  end,

  test_new_version_reruns_consensus = function()
    local run_opts = opts("new-version")
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves.")
    mock_angle("structural", "approve", "Structural angle approves.")
    mock_angle("delete", "approve", "Delete angle approves.")

    local first = run_decide(proposal(), run_opts)
    t.eq(first.exit_code, 0)
    t.eq(#first.raises, 1)
    t.eq(first.raises[1].payload.dedup_key, "consensus:proposal-42-v1")

    -- a new version (different dedup_key) re-derives consensus instead of being skipped
    mock_judgment_runtime()
    mock_angle("minimal", "approve", "Minimal angle approves again.")
    mock_angle("structural", "approve", "Structural angle approves again.")
    mock_angle("delete", "approve", "Delete angle approves again.")

    local second = run_decide(proposal({ dedup_key = "proposal-42-v2" }), run_opts)
    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 1)
    t.eq(second.raises[1].payload.dedup_key, "consensus:proposal-42-v2")
    t.eq(#codex_calls(), 6)
  end,
}
