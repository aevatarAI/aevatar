local h = require("tests.devloop_core_helpers")
local payloads_builders = require("devloop.payloads.builders")
local core = h.core
local t = h.t
local decompose_lib = require("devloop.decompose")

local function assert_language_preamble(prompt)
  t.is_true(prompt:find("Write all output in English; quote code identifiers and cited originals verbatim.", 1, true) ~= nil)
end

local function assert_judge_preamble_slots(prompt)
  assert_language_preamble(prompt)
  t.is_true(prompt:find("Before judging, identify the established theory or industry best practice governing this problem class", 1, true) ~= nil)
  t.is_true(prompt:find("grounds for rejection or narrowing", 1, true) ~= nil)
  t.is_nil(prompt:find("Before acting, identify the established theory or industry best practice governing this change", 1, true))
end

local function assert_github_entity_history(prompt)
  t.is_true(prompt:find("Before judging, read the local context files named below.", 1, true) ~= nil)
  t.is_nil(prompt:find("gh issue view --comments / gh pr view --comments", 1, true))
end

return {
  test_decompose_package_installs_only_decompose_prompt_role = function()
    t.eq(type(core.build_decompose_prompt), "function")
    t.is_nil(core.build_implement_prompt)
    t.is_nil(core.build_fix_prompt)
    t.is_nil(core.build_intake_prompt)
    t.is_nil(core.build_sync_conflict_prompt)
    t.is_nil(core.build_review_meta_prompt)
    t.is_nil(core.parse_intake_action)
    t.is_nil(core.parse_review_meta_action)
  end,

  test_decompose_prompt_includes_scoped_github_history = function()
    local prompt = core.build_decompose_prompt({
      proposal_id = "github-devloop/issue/owner/repo/42",
      source_ref = { kind = "external", ref = "owner/repo#pr/7" },
      round = 4,
    }, {
      title = "Implement decision recorder",
      body = "Issue body",
      comments = {
        { body = "Previous note", author_login = "fkst-test-bot" },
      },
    }, "Read these local files for your complete context.\nIssue JSON: /tmp/ctx/issue.json\nBoard digest: /tmp/ctx/board.txt\nPR diff patch: /tmp/ctx/diff.patch")

    assert_judge_preamble_slots(prompt)
    assert_github_entity_history(prompt)
    t.is_true(prompt:find("/tmp/ctx/issue.json", 1, true) ~= nil)
    t.is_nil(prompt:find("gh issue", 1, true))
    t.is_nil(prompt:find("gh pr", 1, true))
    t.is_nil(prompt:find("gh api", 1, true))
    t.is_true(prompt:find("You are running in an empty runtime scratch directory", 1, true) ~= nil)
    t.is_true(prompt:find("Read GitHub context only from the local files named below", 1, true) ~= nil)
    t.is_nil(prompt:find("{{", 1, true))
  end,

  test_decompose_child_fact_indexes_keep_proxy_marker_legacy_but_completion_uses_live_open_children = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "2026-06-03T01-02-03Z"
    local decompose = payloads_builders.build_devloop_decompose_payload(core, {
      proposal_id = proposal_id,
      pr_number = 7,
      issue_version = version,
      review_proposal_id = "github-devloop/pr-review/owner-repo/7/version/def456",
      review_dedup_key = "consensus:github-devloop/pr-review/owner-repo/7/version/def456/review",
      head_sha = "def456",
      round = 0,
      source_ref = { kind = "external", ref = "owner/repo#pr/7" },
    })
    decompose.current_issue_body = "Parent body"
    local dedup_by_index = {
      core.build_issue_create_request("owner/repo", decompose, { title = "One", body = "Body one" }, 1).dedup_key,
      core.build_issue_create_request("owner/repo", decompose, { title = "Two", body = "Body two" }, 2).dedup_key,
    }
    local completed = decompose_lib.decompose_child_fact_indexes(core, {
      {
        body = '<!-- fkst:github-proxy:issue-created:v1 dedup="' .. dedup_by_index[1] .. '" issue="101" -->',
        author_login = "fkst-test-bot",
      },
      {
        body = '<!-- fkst:github-proxy:issue-created:v1 dedup="' .. dedup_by_index[2] .. '" issue="102" -->',
        author_login = "someone-else",
      },
    }, {
      {
        body = decompose_lib.decompose_child_marker(core, proposal_id, version, 7, 3),
        author_login = "fkst-test-bot",
        state = "OPEN",
      },
      {
        body = decompose_lib.decompose_child_marker(core, proposal_id, version, 7, 2),
        author_login = "someone-else",
        state = "OPEN",
      },
    }, proposal_id, version, 7, dedup_by_index)
    local live_completed = decompose_lib.decompose_child_issue_fact_indexes(core, {
      {
        body = decompose_lib.decompose_child_marker(core, proposal_id, version, 7, 1),
        author_login = "fkst-test-bot",
        state = "CLOSED",
      },
      {
        body = decompose_lib.decompose_child_marker(core, proposal_id, version, 7, 2),
        author_login = "fkst-test-bot",
        state = "OPEN",
      },
    }, proposal_id, version, 7)
    t.eq(completed[1], true)
    t.eq(completed[2], nil)
    t.eq(completed[3], true)
    t.eq(live_completed[1], nil)
    t.eq(live_completed[2], true)
  end,
}
