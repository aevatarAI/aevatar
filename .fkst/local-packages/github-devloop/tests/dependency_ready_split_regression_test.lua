local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local h = require("tests.devloop_helpers")
local payloads_builders = require("devloop.payloads.builders")
local conv_attempts = require("devloop.convergence.attempts")
local t = h.t
local core = h.core
local operator_commands = require("devloop.operator_commands")
local replay_fields = require("devloop.replay_fields")
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local m_builders = require("devloop.markers.builders")

local repo = "owner/repo"
local proposal_id = "github-devloop/issue/owner/repo/42"
local version = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"

local function restart_transition_row(state_name)
  return replay_fields.restart_transition_row(core.restart_transition_table(), state_name)
end

local function source_ref()
  return {
    kind = "external",
    ref = "owner/repo#issue/42",
  }
end

local function encode_json_string(value)
  return tostring(value)
    :gsub("\\", "\\\\")
    :gsub('"', '\\"')
    :gsub("\n", "\\n")
end

local function render_comment(comment)
  local body = comment
  local id = ""
  local created_at = "2026-06-03T01:00:00Z"
  if type(comment) == "table" then
    body = comment.body
    id = comment.id or ""
    created_at = comment.created_at or comment.createdAt or created_at
  end
  return string.format(
    '{"id":"%s","body":"%s","author":{"login":"fkst-test-bot"},"createdAt":"%s"}',
    encode_json_string(id),
    encode_json_string(body or ""),
    encode_json_string(created_at)
  )
end

local function trusted_comment(id, body, created_at)
  return {
    id = id,
    body = body,
    author = { login = "fkst-test-bot" },
    created_at = created_at or "2026-06-03T01:00:00Z",
  }
end

local function issue_comments_json(comments)
  local rendered = {}
  for _, comment in ipairs(comments or {}) do
    table.insert(rendered, render_comment(comment))
  end
  return table.concat(rendered, ",")
end

local function issue_view_json(labels, comments, state)
  local rendered_labels = {}
  for _, label in ipairs(labels or {}) do
    table.insert(rendered_labels, string.format('{"name":"%s"}', encode_json_string(label)))
  end
  return string.format(
    '{"title":"Implement dependency split","state":"%s","labels":[%s],"comments":[%s],"assignees":[{"login":"fkst-test-bot"}]}\n',
    encode_json_string(state or "OPEN"),
    table.concat(rendered_labels, ","),
    issue_comments_json(comments)
  )
end

local function blocked_by_json(nodes)
  local rendered = {}
  local input = nodes or {}
  for _, node in ipairs(input) do
    local state_reason = node.state_reason or node.stateReason or ""
    table.insert(rendered, string.format(
      '{"number":%s,"state":"%s","stateReason":"%s","repository":{"nameWithOwner":"%s"}}',
      tostring(node.number),
      encode_json_string(node.state or "OPEN"),
      encode_json_string(state_reason),
      encode_json_string(node.repo or repo)
    ))
  end
  return '{"data":{"repository":{"issue":{"blockedBy":{"totalCount":'
    .. tostring(#input)
    .. ',"pageInfo":{"hasNextPage":false},"nodes":['
    .. table.concat(rendered, ",")
    .. ']}}}}}\n'
end

local function mock_blocked_by(issue_number, nodes)
  t.mock_command(core.gh_blocked_by_cmd(repo, issue_number), {
    stdout = blocked_by_json(nodes),
    stderr = "",
    exit_code = 0,
  })
end

local function mock_blocked_by_failure(issue_number)
  t.mock_command(core.gh_blocked_by_cmd(repo, issue_number), {
    stdout = "",
    stderr = "graphql failed",
    exit_code = 1,
  })
end

local function mock_blocker_issue(issue_number, state_name)
  local comments = {}
  if state_name ~= nil then
    table.insert(comments, core.state_marker(base_ids.proposal_id(repo, issue_number), state_name, "v-" .. tostring(issue_number)))
  end
  t.mock_command(core.gh_issue_view_observe_cmd(repo, issue_number), {
    stdout = '{"state":"OPEN","comments":[' .. issue_comments_json(comments) .. ']}\n',
    stderr = "",
    exit_code = 0,
  })
end

local function mock_observe_issue(labels, comments)
  entity_read_mocks.mock_issue_read_forms(t, {
    repo = repo,
    number = 42,
    labels = labels,
    comments = comments,
    times = 1,
  })
  t.mock_command(core.gh_issue_view_entity_cmd(repo, 42), {
    stdout = issue_view_json(labels, comments),
    stderr = "",
    exit_code = 0,
  })
end

local function mock_implement_issue(labels, comments)
  t.mock_command(core.gh_issue_view_implement_cmd(repo, 42), {
    stdout = issue_view_json(labels, comments),
    stderr = "",
    exit_code = 0,
  })
end

local function reached()
  return {
    schema = "consensus.consensus_reached.v1",
    proposal_id = proposal_id,
    decision = "approve",
    body = "Approved.",
    dedup_key = version,
    source_ref = source_ref(),
  }
end

local function ready_at(inner_version)
  return payloads_builders.build_devloop_ready_payload(core, {
    proposal_id = proposal_id,
    dedup_key = inner_version,
    source_ref = source_ref(),
  })
end

local function run_observe()
  return t.run_department("departments/observe_issue/main.lua", {
    queue = "github-proxy.github_entity_changed",
    payload = h.issue(),
  }, h.opts("ready-split-regression-observe"))
end

local function run_observe_with_issue(event)
  return t.run_department("departments/observe_issue/main.lua", {
    queue = "github-proxy.github_entity_changed",
    payload = event,
  }, h.opts("ready-split-regression-observe-visible"))
end

local function run_implement(payload)
  return t.run_department("departments/implement/main.lua", {
    queue = "devloop_ready",
    payload = payload,
  }, h.opts("ready-split-regression-implement"))
end

local function find_raise(raises, queue, predicate)
  for _, item in ipairs(raises or {}) do
    if item.queue == queue and (predicate == nil or predicate(item.payload)) then
      return item
    end
  end
  return nil
end

local function count_queue(raises, queue)
  local count = 0
  for _, item in ipairs(raises or {}) do
    if item.queue == queue then
      count = count + 1
    end
  end
  return count
end

local function marker_body(raises, needle)
  local raise = find_raise(raises, "github-proxy.github_issue_comment_request", function(payload)
    return type(payload.body) == "string" and payload.body:find(needle, 1, true) ~= nil
  end)
  return raise and raise.payload.body or nil
end

local function ready_handoff_comment_raise(raises)
  return find_raise(raises, "github-proxy.github_issue_comment_request", function(payload)
    return type(payload.handoff) == "table"
      and payload.handoff.kind == "github-devloop.ready"
  end)
end

local function run_comment_handoff_from_request(request, comment_id, name)
  return t.run_department("departments/comment_handoff/main.lua", {
    queue = "github-proxy.github_comment_written",
    payload = {
      schema = "github-proxy.comment-written.v1",
      repo = request.repo,
      target = "issue",
      issue_number = request.issue_number,
      comment_id = comment_id,
      request_dedup_key = request.dedup_key,
      dedup_key = tostring(request.dedup_key) .. "/written/" .. tostring(comment_id),
      source_ref = request.source_ref,
      handoff = request.handoff,
    },
  }, h.opts(name))
end

local function capture_core_raises(fn)
  local raised = {}
  local original_log_raise = core.log_raise
  core.log_raise = function(_, _, queue, payload)
    table.insert(raised, {
      queue = queue,
      payload = payload,
    })
  end
  local ok, err = pcall(fn)
  core.log_raise = original_log_raise
  if not ok then
    error(err)
  end
  return raised
end

local function replay_ready_with_comments(comments)
  return capture_core_raises(function()
    core.replay_ready_state("observe_issue", h.issue(), {
      state = "ready",
      version = version,
      proposal_id = proposal_id,
    }, restart_transition_row("ready"), {
      proposal_id = proposal_id,
      current = {
        labels = { "fkst-dev:enabled", "fkst-dev:ready" },
        comments = comments,
      },
      dependency_gate = {
        ok = true,
        reason = "test",
      },
    })
  end)
end

return {
  test_ready_hand_off_comment_id_requires_trusted_visible_ready_marker = function()
    local marker = core.state_marker(proposal_id, "ready", version, "result-marker,ready-label,devloop-ready")
    t.eq(core.ready_hand_off_comment_id({
      trusted_comment("IC_ready_1", marker),
    }, proposal_id, version), "IC_ready_1")
    t.eq(core.ready_hand_off_comment_id({
      {
        id = "IC_forged",
        body = marker,
        author = { login = "not-the-bot" },
      },
    }, proposal_id, version), nil)
    t.eq(core.ready_hand_off_comment_id({
      trusted_comment("IC_missing_effects", core.state_marker(proposal_id, "ready", version)),
    }, proposal_id, version), nil)
  end,

  test_ready_redrive_with_visible_marker_carries_handoff_and_distinct_generation = function()
    local marker = core.state_marker(proposal_id, "ready", version, "result-marker,ready-label,devloop-ready")
    mock_observe_issue({ "fkst-dev:enabled", "fkst-dev:ready" }, {
      trusted_comment("IC_ready_visible", marker),
    })
    mock_blocked_by(42, {})

    local result = run_observe_with_issue(h.issue())
    t.eq(result.exit_code, 0)
    local ready = find_raise(result.raises, "devloop_ready")
    t.is_true(ready ~= nil)
    t.eq(ready.payload.ready_hand_off.comment_id, "IC_ready_visible")
    t.eq(ready.payload.ready_hand_off.marker_version, version)
    t.eq(ready.payload.ready_hand_off.event_version, ready.payload.dedup_key)
    t.is_true(ready.payload.dedup_key ~= payloads_builders.build_devloop_ready_payload(core, {
      proposal_id = proposal_id,
      dedup_key = version,
      source_ref = source_ref(),
    }).dedup_key)
    t.is_true(ready.payload.dedup_key:find("/redrive/ready/1", 1, true) ~= nil)
  end,

  test_ready_redrive_generation_advances_with_timeout_attempt_markers = function()
    local marker_version = version
    local marker = core.state_marker(proposal_id, "ready", marker_version, "result-marker,ready-label,devloop-ready")
    local attempt_1 = conv_attempts.timeout_attempt_marker(core, proposal_id, marker_version, "ready", 1, source_ref())
    local first_raises = replay_ready_with_comments({
      trusted_comment("IC_ready_visible", marker),
      trusted_comment("IC_timeout_1", attempt_1, "2026-06-03T01:01:00Z"),
    })

    local first_ready = find_raise(first_raises, "devloop_ready")
    t.eq(first_ready ~= nil, true)
    t.eq(first_ready.payload.dedup_key, payloads_builders.build_devloop_ready_payload(core, {
      proposal_id = proposal_id,
      dedup_key = marker_version .. "/redrive/ready/2",
      source_ref = source_ref(),
    }).dedup_key)
    t.eq(first_ready.payload.ready_hand_off.marker_version, marker_version)
    t.eq(first_ready.payload.ready_hand_off.event_version, first_ready.payload.dedup_key)

    local attempt_2 = conv_attempts.timeout_attempt_marker(core, proposal_id, marker_version, "ready", 2, source_ref())
    local second_raises = replay_ready_with_comments({
      trusted_comment("IC_ready_visible", marker),
      trusted_comment("IC_timeout_1", attempt_1, "2026-06-03T01:01:00Z"),
      trusted_comment("IC_timeout_2", attempt_2, "2026-06-03T01:02:00Z"),
    })

    local second_ready = find_raise(second_raises, "devloop_ready")
    t.eq(second_ready ~= nil, true)
    t.eq(second_ready.payload.dedup_key, payloads_builders.build_devloop_ready_payload(core, {
      proposal_id = proposal_id,
      dedup_key = marker_version .. "/redrive/ready/3",
      source_ref = source_ref(),
    }).dedup_key)
    t.eq(second_ready.payload.ready_hand_off.comment_id, "IC_ready_visible")
    t.eq(second_ready.payload.ready_hand_off.marker_version, marker_version)
    t.eq(second_ready.payload.ready_hand_off.event_version, second_ready.payload.dedup_key)
    t.eq(first_ready.payload.dedup_key == second_ready.payload.dedup_key, false)
  end,

  test_ready_redrive_generation_advances_after_accepted_reready_response = function()
    local marker = core.state_marker(proposal_id, "ready", version, "result-marker,ready-label,devloop-ready")
    local command = {
      command = "reready",
      key = "operator-command/IC_reready_ready",
    }
    local accepted = operator_commands.operator_command_marker(core, command, "applied", "ready")
    local raises = replay_ready_with_comments({
      trusted_comment("IC_ready_visible", marker),
      trusted_comment("IC_reready_response", accepted, "2026-06-03T01:01:00Z"),
    })

    local ready = find_raise(raises, "devloop_ready")
    t.eq(ready ~= nil, true)
    t.eq(ready.payload.ready_hand_off.comment_id, "IC_ready_visible")
    t.eq(ready.payload.ready_hand_off.marker_version, version)
    t.eq(ready.payload.dedup_key, payloads_builders.build_devloop_ready_payload(core, {
      proposal_id = proposal_id,
      dedup_key = version .. "/redrive/ready/2",
      source_ref = source_ref(),
    }).dedup_key)
  end,

  test_ready_replay_ignores_prebuilt_payload_without_hand_off = function()
    local marker_version = version
    local marker = core.state_marker(proposal_id, "ready", marker_version, "result-marker,ready-label,devloop-ready")
    local raises = capture_core_raises(function()
      core.replay_ready_state("observe_issue", h.issue(), {
        state = "ready",
        version = marker_version,
        proposal_id = proposal_id,
      }, restart_transition_row("ready"), {
        proposal_id = proposal_id,
        current = {
          labels = { "fkst-dev:enabled", "fkst-dev:ready" },
          comments = {
            trusted_comment("IC_ready_visible", marker),
            trusted_comment(
              "IC_timeout_1",
              conv_attempts.timeout_attempt_marker(core, proposal_id, marker_version, "ready", 1, source_ref()),
              "2026-06-03T01:01:00Z"
            ),
          },
        },
        dependency_gate = {
          ok = true,
          reason = "test",
        },
        ready_payload = payloads_builders.build_devloop_ready_payload(core, {
          proposal_id = proposal_id,
          dedup_key = marker_version .. "/stale-bypass",
          source_ref = source_ref(),
        }),
      })
    end)

    local ready = find_raise(raises, "devloop_ready")
    t.eq(ready ~= nil, true)
    t.eq(ready.payload.ready_hand_off.comment_id, "IC_ready_visible")
    t.eq(ready.payload.ready_hand_off.marker_version, marker_version)
    t.eq(ready.payload.ready_hand_off.event_version, ready.payload.dedup_key)
    t.eq(ready.payload.dedup_key, payloads_builders.build_devloop_ready_payload(core, {
      proposal_id = proposal_id,
      dedup_key = marker_version .. "/redrive/ready/2",
      source_ref = source_ref(),
    }).dedup_key)
  end,

  test_ready_redrive_without_visible_ready_marker_fails_closed = function()
    mock_observe_issue({ "fkst-dev:enabled", "fkst-dev:ready" }, {
      core.state_marker(proposal_id, "ready", version),
    })
    mock_blocked_by(42, {})

    local result = run_observe_with_issue(h.issue())
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
  end,

  test_implement_backstop_split_generation_uses_inner_ready_version = function()
    local split_version = core.ready_split_version(version)
    local ready = ready_at(split_version)
    mock_blocked_by(42, { { number = 55 } })
    mock_blocked_by(55, {})
    mock_implement_issue({ "fkst-dev:ready" }, {
      core.state_marker(proposal_id, "ready", split_version),
    })

    local result = run_implement(ready)
    t.eq(result.exit_code, 0)
    t.eq(count_queue(result.raises, "github-proxy.github_issue_comment_request"), 1)
    t.eq(count_queue(result.raises, "github-proxy.github_issue_label_request"), 1)
    local body = marker_body(result.raises, "ready-split-canonicalized:v1")
    local inner_version = core.ready_payload_inner_version(ready.dedup_key)
    local next_split_version = core.ready_split_version(inner_version)
    t.is_true(body ~= nil)
    t.is_true(body:find('from_version="' .. inner_version .. '"', 1, true) ~= nil)
    t.is_true(body:find('to_version="' .. next_split_version .. '"', 1, true) ~= nil)
    t.is_true(body:find('to_version="ready/', 1, true) == nil)
    t.is_true(body:find('state="dependency_wait"', 1, true) ~= nil)
  end,

  test_legacy_ready_unresolvable_hold_canonicalizes_to_dependency_wait = function()
    mock_observe_issue(
      { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
      {
        core.state_marker(proposal_id, "ready", version),
        "github-devloop dependency hold: unresolvable\n\nReason: gh-failed\n\n"
          .. core.dependency_unresolvable_marker(proposal_id, version, { 42 }),
      }
    )
    mock_blocked_by_failure(42)

    local result = run_observe()
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
    local split_version = core.ready_split_version(version)
    local body = marker_body(result.raises, "ready-split-canonicalized:v1")
    t.is_true(body ~= nil)
    t.is_true(body:find('derived_state="dependency_wait"', 1, true) ~= nil)
    t.is_true(body:find('state="dependency_wait"', 1, true) ~= nil)
    t.is_true(body:find('version="' .. split_version .. '"', 1, true) ~= nil)
    t.is_true(body:find("fkst:github-devloop:dependency-wait:v1", 1, true) ~= nil)
  end,

  test_consensus_result_reraises_partial_dependency_wait_effects = function()
    local current = reached()
    h.mock_issue_result({ "fkst-dev:ready" }, {
      core.state_marker(current.proposal_id, "dependency_wait", current.dedup_key),
      m_builders.result_marker(core, current.proposal_id, current.decision, current.dedup_key),
    })
    mock_blocked_by(42, { { number = 51 } })
    mock_blocked_by(51, {})
    mock_blocker_issue(51, "ready")

    local result = h.run_result(current, h.opts("ready-split-regression-result"))
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
    t.is_true(marker_body(result.raises, "fkst:github-devloop:dependency-wait:v1") ~= nil)
    local label = find_raise(result.raises, "github-proxy.github_issue_label_request", function(payload)
      return h.has_value(payload.add_labels, "fkst-dev:blocked-on-dependency")
    end)
    t.is_true(label ~= nil)
  end,

  test_consensus_result_dependency_wait_comment_has_no_ready_handoff = function()
    local current = reached()
    h.mock_issue_result({ "fkst-dev:thinking" }, {
      core.state_marker(current.proposal_id, "thinking", current.dedup_key),
    })
    mock_blocked_by(42, { { number = 51 } })
    mock_blocked_by(51, {})
    mock_blocker_issue(51, "ready")

    local result = h.run_result(current, h.opts("ready-split-regression-result-hold-handoff"))
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
    local result_comment = find_raise(result.raises, "github-proxy.github_issue_comment_request", function(payload)
      return type(payload.body) == "string"
        and payload.body:find('state="dependency_wait"', 1, true) ~= nil
        and payload.body:find("fkst:github-devloop:result:v1", 1, true) ~= nil
    end)
    t.is_true(result_comment ~= nil)
    t.is_nil(result_comment.payload.handoff)

    local handoff = run_comment_handoff_from_request(
      result_comment.payload,
      "IC_dependency_hold_result",
      "ready-split-regression-result-hold-comment-handoff"
    )
    t.eq(handoff.exit_code, 0)
    t.eq(find_raise(handoff.raises, "devloop_ready"), nil)
  end,

  test_consensus_result_ready_comment_keeps_ready_handoff = function()
    local current = reached()
    h.mock_issue_result({ "fkst-dev:thinking" }, {
      core.state_marker(current.proposal_id, "thinking", current.dedup_key),
    })
    mock_blocked_by(42, {})

    local result = h.run_result(current, h.opts("ready-split-regression-result-ready-handoff"))
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
    local result_comment = ready_handoff_comment_raise(result.raises)
    t.is_true(result_comment ~= nil)
    t.eq(result_comment.payload.handoff.proposal_id, current.proposal_id)
    t.eq(result_comment.payload.handoff.version, current.dedup_key)
    t.eq(result_comment.payload.handoff.marker_version, current.dedup_key)

    local handoff = run_comment_handoff_from_request(
      result_comment.payload,
      "IC_ready_result",
      "ready-split-regression-result-ready-comment-handoff"
    )
    t.eq(handoff.exit_code, 0)
    local ready = find_raise(handoff.raises, "devloop_ready")
    t.is_true(ready ~= nil)
    t.eq(ready.payload.ready_hand_off.comment_id, "IC_ready_result")
    t.eq(ready.payload.ready_hand_off.marker_version, current.dedup_key)
  end,

  test_dependency_release_ready_handoff_accepts_direct_visible_marker = function()
    local split_version = core.ready_split_version(version)
    mock_observe_issue(
      { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
      {
        core.state_marker(proposal_id, "dependency_wait", version),
        "github-devloop dependency hold: waiting\n\nReason: waiting-on-dependency\n\n"
          .. core.dependency_wait_marker(proposal_id, version, { 53 }),
      }
    )
    mock_blocked_by(42, { { number = 53 } })
    mock_blocked_by(53, {})
    mock_blocker_issue(53, "merged")

    local released = run_observe()
    t.eq(released.exit_code, 0)
    t.eq(find_raise(released.raises, "devloop_ready"), nil)
    local release_comment = ready_handoff_comment_raise(released.raises)
    t.is_true(release_comment ~= nil)
    t.eq(release_comment.payload.handoff.marker_version, split_version)
    t.is_true(release_comment.payload.body:find(
      core.state_marker(proposal_id, "ready", split_version, "result-marker,ready-label,devloop-ready"),
      1,
      true
    ) ~= nil)
    t.is_true(release_comment.payload.body:find("fkst:github-devloop:ready-split-canonicalized:v1", 1, true) ~= nil)

    local handoff = run_comment_handoff_from_request(
      release_comment.payload,
      "IC_dependency_release_ready",
      "ready-split-regression-release-comment-handoff"
    )
    t.eq(handoff.exit_code, 0)
    local ready = find_raise(handoff.raises, "devloop_ready")
    t.is_true(ready ~= nil)
    t.eq(ready.payload.ready_hand_off.comment_id, "IC_dependency_release_ready")
    t.eq(ready.payload.ready_hand_off.marker_version, split_version)

    local branch = devloop_base.implement_branch(repo, 42, ready.payload.dedup_key)
    mock_implement_issue({ "fkst-dev:ready" }, {
      core.state_marker(proposal_id, "dependency_wait", version),
    })
    t.mock_command("gh api --method GET 'repos/owner/repo/issues/comments/IC_dependency_release_ready'", {
      stdout = '{"body":"' .. encode_json_string(release_comment.payload.body) .. '","user":{"login":"fkst-test-bot"}}\n',
      stderr = "",
      exit_code = 0,
    })
    h.mock_fresh_implement_worktree({ impl_version = ready.payload.dedup_key })
    h.mock_implement_codex(0, "implemented")
    h.mock_git_status(" M packages/github-devloop/core/ready_split.lua\n")
    h.mock_git_commit("def456", branch)
    mock_implement_issue({ "fkst-dev:ready" }, {
      core.state_marker(proposal_id, "dependency_wait", version),
    })
    mock_implement_issue({ "fkst-dev:ready" }, {
      core.state_marker(proposal_id, "dependency_wait", version),
    })

    local implemented = h.run_implement(ready.payload, h.opts("ready-split-regression-release-implement"))
    t.eq(implemented.exit_code, 0)
    t.eq(count_queue(implemented.raises, "github-proxy.github_issue_label_request"), 1)
    t.eq(find_raise(implemented.raises, "github-proxy.github_issue_label_request").payload.add_labels[1], "fkst-dev:implementing")
    t.eq(h.count_calls("repos/owner/repo/issues/comments/IC_dependency_release_ready"), 1)
    t.eq(h.count_calls("codex exec"), 1)
  end,
}
