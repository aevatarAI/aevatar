local entity_lib = require("devloop.entity")
local base = require("tests.devloop_base_helpers")
local pr = require("tests.devloop_pr_helpers")
local worktree = require("tests.devloop_worktree_helpers")
local entity_read_mocks = require("tests.entity_read_mock_helpers")

local helpers = {}
for key, value in pairs(base) do
  helpers[key] = value
end
for key, value in pairs(pr) do
  helpers[key] = value
end
for key, value in pairs(worktree) do
  helpers[key] = value
end

local base_run_decompose = helpers.run_decompose

local function issue_identity_from_payload(payload)
  local entity = entity_lib.parse_entity_proposal_id(payload and payload.proposal_id)
  local source_ref = payload and payload.source_ref and payload.source_ref.ref
  local source_repo, source_issue = tostring(source_ref or ""):match("^(.+)#issue/(%d+)$")
  return source_repo or (entity and entity.repo) or "owner/repo",
    tonumber(source_issue) or (entity and entity.issue_number) or 42
end

local function mock_default_issue_claim(repo, number)
  local selected_repo = repo or "owner/repo"
  local selected_number = number or 42
  entity_read_mocks.mock_issue_read_forms(helpers.t, {
    repo = selected_repo,
    number = selected_number,
    assignees = { "fkst-test-bot" },
    author_login = "fkst-test-bot",
  })
  entity_read_mocks.mock_issue_view_selector(helpers.t, {
    repo = selected_repo,
    number = selected_number,
    assignees = { "fkst-test-bot" },
    author_login = "fkst-test-bot",
  }, "assignees,author", 30)
end

helpers.run_decompose = function(payload, run_opts)
  local repo, issue_number = issue_identity_from_payload(payload)
  mock_default_issue_claim(repo, issue_number)
  return base_run_decompose(payload, run_opts)
end

helpers.mock_default_issue_claim = mock_default_issue_claim
helpers.issue_identity_from_payload = issue_identity_from_payload
return helpers
