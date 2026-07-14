local base_ids = require("devloop.base_ids")
local h = require("tests.devloop_helpers")
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local execution_start = require("devloop.execution_start")
local t = h.t
local core = h.core
local opts = h.opts

local function execution_request(extra)
  local payload = execution_start.build_execution_request_payload({
    proposal_id = "github-devloop/issue/owner/repo/42",
    dedup_key = "intake/github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
    source_ref = { kind = "external", ref = "owner/repo#issue/42" },
    origin = {
      package = "github-devloop-intake-default",
      route = "default",
      decision = "enable",
    },
    service_class = "expedite",
  })
  for key, value in pairs(extra or {}) do
    payload[key] = value
  end
  return payload
end

local function current_issue(extra)
  local fields = {
    repo = "owner/repo",
    number = 42,
    title = "Add retry backoff to failed widget sync",
    body = "Implement exponential backoff for widget sync retries.",
    updated_at = "2026-06-03T01:02:03Z",
    state = "OPEN",
    labels = {},
    comments = {},
    assignees = { "fkst-test-bot" },
    author_login = "fkst-test-bot",
  }
  for key, value in pairs(extra or {}) do
    fields[key] = value
  end
  return fields
end

local function mock_execute_start_issue(fields)
  entity_read_mocks.mock_issue_view_selector(t, current_issue(fields), "title,body,createdAt,updatedAt,labels,comments,state,assignees,author", 1)
end

local function run_execute_start(payload, run_opts)
  return t.run_department("departments/execute_start/main.lua", {
    queue = "devloop_execute_request",
    payload = payload,
    ts = "2026-06-03T01:02:04Z",
  }, run_opts or opts("execute-start"))
end

local function find_raise(raises, queue)
  return h.find_raise(raises, queue)
end

local function assert_execution_effects(raises, request)
  t.eq(#raises, 3)
  t.eq(raises[1].queue, "github-proxy.github_issue_comment_request")
  t.eq(raises[2].queue, "github-proxy.github_issue_label_request")
  t.eq(raises[3].queue, "consensus.proposal")

  local comment = raises[1].payload
  t.eq(comment.schema, "github-proxy.v1")
  t.eq(comment.repo, "owner/repo")
  t.eq(tostring(comment.issue_number), "42")
  t.eq(comment.dedup_key, base_ids.dedup_key({
    request.proposal_id,
    "comment",
    "thinking",
    request.dedup_key,
  }))
  t.is_true(comment.body:find(core.state_marker(request.proposal_id, "thinking", request.dedup_key), 1, true) ~= nil)
  t.eq(comment.source_ref.ref, "owner/repo#issue/42")

  local label = raises[2].payload
  t.eq(label.schema, "github-proxy.label.v1")
  t.eq(label.add_labels[1], "fkst-dev:thinking")
  t.eq(label.dedup_key, request.dedup_key .. "/label/thinking")
  t.eq(label.source_ref.ref, "owner/repo#issue/42")

  local proposal = raises[3].payload
  t.eq(proposal.schema, "consensus.proposal.v1")
  t.eq(proposal.verdict_mode, "converge")
  t.eq(proposal.proposal_id, request.proposal_id)
  t.eq(proposal.dedup_key, request.dedup_key)
  t.eq(proposal.effect_version, request.dedup_key)
  t.eq(proposal.source_ref.ref, "owner/repo#issue/42")
  t.eq(proposal.intake_hand_off.kind, "own-intake-decision")
  t.eq(proposal.intake_hand_off.proposal_id, request.proposal_id)
  t.eq(proposal.intake_hand_off.decision, "enable")
  t.eq(proposal.intake_hand_off.dedup_key, request.dedup_key)
  t.eq(proposal.intake_hand_off.source_ref.ref, "owner/repo#issue/42")
  t.is_true(tostring(proposal.content_fetch or ""):find("^runtime%-cache:") ~= nil)
end

return {
  test_execution_start_shared_builder_matches_direct_path_shape = function()
    local request = execution_request()
    local current = current_issue()
    h.mock_context_bundle(request)

    local effects = execution_start.build_execution_start_effects(core, "owner/repo", 42, request, current, "2026-06-03T01:02:04Z", "intake_judge")

    t.is_true(effects ~= nil)
    local raises = {
      { queue = "github-proxy.github_issue_comment_request", payload = effects.thinking_comment_request },
      { queue = "github-proxy.github_issue_label_request", payload = effects.thinking_label_request },
      { queue = "consensus.proposal", payload = effects.proposal },
    }
    assert_execution_effects(raises, request)
  end,

  test_execute_start_raises_proposal_and_thinking_effects_only = function()
    local request = execution_request()
    h.mock_bot_env()
    mock_execute_start_issue()
    h.mock_context_bundle(request)

    local result = run_execute_start(request, opts("execute-start-raises"))

    t.eq(result.exit_code, 0)
    assert_execution_effects(result.raises, request)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
  end,

  test_execute_start_publishes_execution_request_seam = function()
    local module = require("departments.execute_start.main")

    t.eq(module.spec.consumes[1], "devloop_execute_request")
    t.eq(module.spec.published_seam[1], "devloop_execute_request")
    t.eq(find_raise({}, "devloop_ready"), nil)
    for _, queue in ipairs(module.spec.produces or {}) do
      t.is_true(queue ~= "devloop_ready")
    end
  end,
}
