local h = require("tests.devloop_core_helpers")
local payloads_predicates = require("devloop.payloads.predicates")
local core = h.core
local t = h.t

local function json_string(value)
  return tostring(value or "")
    :gsub("\\", "\\\\")
    :gsub('"', '\\"')
    :gsub("\n", "\\n")
end

local function mock_comment_get(comment_id, marker)
  t.mock_command("gh api --method GET 'repos/owner/repo/issues/comments/" .. tostring(comment_id) .. "'", {
    stdout = '{"id":"' .. json_string(comment_id) .. '","body":"' .. json_string(marker) .. '","user":{"login":"fkst-test-bot"}}\n',
    stderr = "",
    exit_code = 0,
  })
end

local function verify(hand_off, expected)
  return payloads_predicates.verified_hand_off_state(core, "owner/repo", hand_off, expected)
end

return {
  test_ready_hand_off_verifies_carried_comment_id_when_comment_list_is_stale = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local marker_version = "consensus:github-devloop/issue/owner/repo/42/intake/123"
    local event_version = "ready/consensus-github-devloop/issue/owner/repo/42/intake/123"
    mock_comment_get(
      "IC_ready_by_id",
      core.state_marker(proposal_id, "ready", marker_version, "result-marker,ready-label,devloop-ready")
    )

    local state, reason = verify({
      kind = "own-state-marker",
      proposal_id = proposal_id,
      state = "ready",
      marker_version = marker_version,
      event_version = event_version,
      stage_rank = core.stage_rank("ready"),
      effects = "result-marker,ready-label,devloop-ready",
      comment_id = "IC_ready_by_id",
    }, {
      proposal_id = proposal_id,
      state = "ready",
      marker_version = marker_version,
      event_version = event_version,
    })

    t.eq(reason, "verified")
    t.eq(state.state, "ready")
    t.eq(state.version, event_version)
    t.eq(core.current_state({}, proposal_id).state, nil)
  end,

  test_reviewing_hand_off_verifies_carried_comment_id_when_comment_list_is_stale = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/intake/123"
    mock_comment_get("IC_reviewing_by_id", core.state_marker(proposal_id, "reviewing", version))

    local state, reason = verify({
      kind = "own-state-marker",
      proposal_id = proposal_id,
      state = "reviewing",
      marker_version = version,
      event_version = version,
      stage_rank = core.stage_rank("reviewing"),
      comment_id = "IC_reviewing_by_id",
    }, {
      proposal_id = proposal_id,
      state = "reviewing",
      marker_version = version,
      event_version = version,
    })

    t.eq(reason, "verified")
    t.eq(state.state, "reviewing")
    t.eq(state.version, version)
    t.eq(core.current_state({}, proposal_id).state, nil)
  end,

  test_hand_off_rejects_comment_id_mismatch = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/intake/123"
    t.mock_command("gh api --method GET 'repos/owner/repo/issues/comments/IC_expected'", {
      stdout = '{"id":"IC_other","body":"' .. json_string(core.state_marker(proposal_id, "reviewing", version)) .. '","user":{"login":"fkst-test-bot"}}\n',
      stderr = "",
      exit_code = 0,
    })

    local state, reason = verify({
      kind = "own-state-marker",
      proposal_id = proposal_id,
      state = "reviewing",
      marker_version = version,
      event_version = version,
      stage_rank = core.stage_rank("reviewing"),
      comment_id = "IC_expected",
    }, {
      proposal_id = proposal_id,
      state = "reviewing",
      marker_version = version,
      event_version = version,
    })

    t.eq(state, nil)
    t.eq(reason, "comment-id-mismatch")
  end,
}
