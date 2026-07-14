local h = require("tests.proxy_integration_helpers")
local conformance = require("testkit.saga_conformance")
local t = h.t
local core = h.core

local function find_step(saga_def, step_id)
  for _, step in ipairs(saga_def.steps or {}) do
    if step.id == step_id then
      return step
    end
  end
  return nil
end

local function find_post_condition(step, condition_id)
  for _, condition in ipairs(step.post_conditions or {}) do
    if condition.id == condition_id then
      return condition
    end
  end
  return nil
end

return {
  test_fork_and_block_saga_declares_issue_create_and_block_steps = function()
    local saga_def = core.external_effect_saga("fork-and-block")

    conformance.assert_external_effect_saga(saga_def)
    t.eq(saga_def.id, "fork-and-block")
    t.eq(#saga_def.steps, 2)
    t.eq(find_step(saga_def, "create-fork").request_queue, "github_issue_create_request")
    t.eq(find_step(saga_def, "block-original").request_queue, "github_issue_blocked_by_request")
  end,

  test_fork_and_block_create_step_requires_parent_created_marker = function()
    local saga_def = core.external_effect_saga("fork-and-block")
    local create = find_step(saga_def, "create-fork")
    local condition = find_post_condition(create, "fork-created-parent-ledger")

    conformance.assert_external_effect_post_condition(condition, {
      body = core.issue_created_marker("fork-dedup-key", "43"),
      dedup_key = "fork-dedup-key",
      issue_number = "43",
    })
  end,

  test_fork_and_block_block_step_requires_valid_add_blocked_by_mutation = function()
    local saga_def = core.external_effect_saga("fork-and-block")
    local block = find_step(saga_def, "block-original")
    local condition = find_post_condition(block, "peer-blocked-by-edge")

    conformance.assert_external_effect_post_condition(condition, {
      query = core.github_graphql_queries.add_blocked_by,
    })

    local malformed = core.github_graphql_queries.add_blocked_by
      :gsub("issueId:%$b", "blockedIssueId:$b")
    local ok, err = pcall(function()
      conformance.assert_external_effect_post_condition(condition, {
        query = malformed,
      })
    end)

    t.eq(ok, false)
    t.eq(tostring(err):find("requires GraphQL field issueId", 1, true) ~= nil, true)
  end,

  test_fork_and_block_block_step_requires_blocked_by_marker = function()
    local saga_def = core.external_effect_saga("fork-and-block")
    local block = find_step(saga_def, "block-original")
    local condition = find_post_condition(block, "blocked-by-marker-visible")

    conformance.assert_external_effect_post_condition(condition, {
      body = core.blocked_by_marker("fork-dedup-key/blocked-by", 42, 43),
      dedup_key = "fork-dedup-key/blocked-by",
    })
  end,

  test_issue_create_parent_ledger_saga_declares_marker_post_conditions = function()
    local saga_def = core.external_effect_saga("issue-create-parent-ledger")
    local intent = find_step(saga_def, "record-create-intent")
    local created = find_step(saga_def, "record-created-issue")

    conformance.assert_external_effect_saga(saga_def)
    t.eq(intent.request_queue, "github_issue_create_request")
    t.eq(created.request_queue, "github_issue_create_request")
    conformance.assert_external_effect_post_condition(find_post_condition(intent, "parent-intent-marker-visible"), {
      body = core.issue_create_intent_marker("dedup-key"),
      dedup_key = "dedup-key",
    })
    conformance.assert_external_effect_post_condition(find_post_condition(created, "parent-created-marker-visible"), {
      body = core.issue_created_marker("dedup-key", "99"),
      dedup_key = "dedup-key",
      issue_number = "99",
    })
  end,
}
