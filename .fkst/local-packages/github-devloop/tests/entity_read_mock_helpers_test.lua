local h = require("tests.devloop_core_helpers")
local core = h.core
local t = h.t
local seam = require("tests.entity_read_mock_helpers")

local function decode(text)
  local ok, value = pcall(json.decode, text or "")
  t.eq(ok, true)
  return value
end

local function github()
  return require("forge.github").new(exec_argv)
end

local function first(value)
  return (value or {})[1] or {}
end

return {
  test_issue_read_seam_registers_equivalent_view_rest_probe_and_comments = function()
    seam.mock_issue_read_forms(t, {
      repo = "owner/repo",
      number = 42,
      title = "Single source issue",
      body = "Issue body",
      state = "CLOSED",
      updated_at = "2026-06-14T10:11:12Z",
      labels = { "fkst-dev:ready", "priority:high" },
      comments = {
        { id = "IC_1", body = "marker one", author_login = "fkst-test-bot", created_at = "2026-06-14T10:12:00Z" },
      },
      assignees = { "fkst-test-bot" },
      author_login = "fkst-test-bot",
      register_all_views = true,
    })

    local view = decode(require("devloop.github_proxy_entity_view").fetch_issue_view(core, "owner/repo", 42, "2026-06-14T10:11:12Z", { consumer = "view" }).stdout)
    local rest = decode(github().issue_rest_view("owner/repo", 42, 30).stdout)
    local probe = github().issue_updated_at("owner/repo", 42, 30)
    local comments = decode(github().issue_comments("owner/repo", 42, 30).stdout)

    t.eq(view.number, rest.number)
    t.eq(view.title, rest.title)
    t.eq(view.body, rest.body)
    t.eq(view.state, "CLOSED")
    t.eq(rest.state, "closed")
    t.eq(string.lower(view.state), rest.state)
    t.eq(view.updatedAt, rest.updated_at)
    t.eq(probe.stdout, view.updatedAt .. "\n")
    t.eq(first(view.assignees).login, first(rest.assignees).login)
    t.eq(view.author.login, rest.user.login)
    t.eq(#view.labels, #rest.labels)
    t.eq(view.labels[1].name, rest.labels[1].name)
    t.eq(view.labels[2].name, rest.labels[2].name)
    t.eq(#view.comments, #comments)
    t.eq(first(view.comments).id, first(comments).id)
    t.eq(first(view.comments).body, first(comments).body)
    t.eq(first(view.comments).author.login, first(comments).user.login)
    t.eq(first(view.comments).createdAt, first(comments).created_at)
  end,

  test_pr_read_seam_registers_equivalent_view_rest_probe_and_comments = function()
    seam.mock_pr_read_forms(t, {
      repo = "owner/repo",
      number = 7,
      head = "feature/read-seam",
      head_sha = "feedface",
      base_branch = "dev",
      base_sha = "basefeed",
      state = "MERGED",
      updated_at = "2026-06-14T11:11:12Z",
      merged_at = "2026-06-14T11:20:00Z",
      mergeable = "CONFLICTING",
      merge_state = "DIRTY",
      labels = { "fkst-dev:reviewing", "ci:green" },
      comments = {
        { id = "PRC_1", body = "review marker", author_login = "fkst-test-bot", created_at = "2026-06-14T11:12:00Z" },
      },
      register_all_views = true,
    })

    local view = decode(require("devloop.github_proxy_entity_view").fetch_pr_view("owner/repo", 7, "2026-06-14T11:11:12Z", { consumer = "view" }).stdout)
    local rest = decode(github().pr_rest_view("owner/repo", 7, 30).stdout)
    local probe = github().entity_updated_at("owner/repo", "pr", 7, 30)
    local comments = decode(github().issue_comments("owner/repo", 7, 30).stdout)

    t.eq(view.number, rest.number)
    t.eq(view.headRefName, rest.head.ref)
    t.eq(rest.head.sha, view.headRefOid)
    t.eq(rest.base.ref, view.baseRefName)
    t.eq(rest.base.sha, view.baseRefOid)
    t.eq(view.state, "MERGED")
    t.eq(rest.state, "closed")
    t.eq(view.isDraft, rest.draft)
    t.eq(view.merged, true)
    t.eq(view.mergedAt, rest.merged_at)
    t.eq(view.headRepository.nameWithOwner, rest.head.repo.full_name)
    t.eq(view.headRepository.owner.login, rest.head.repo.owner.login)
    t.eq(view.headRepositoryOwner.login, rest.head.repo.owner.login)
    t.eq(rest.base.repo.owner.login, "owner")
    t.eq(view.updatedAt, rest.updated_at)
    t.eq(rest.mergeable, false)
    t.eq(rest.mergeable_state, "DIRTY")
    t.eq(view.mergeable, "CONFLICTING")
    t.eq(view.mergeStateStatus, "DIRTY")
    t.eq(probe.stdout, view.updatedAt .. "\n")
    t.eq(#view.labels, #rest.labels)
    t.eq(view.labels[1].name, rest.labels[1].name)
    t.eq(view.labels[2].name, rest.labels[2].name)
    t.eq(#view.comments, #comments)
    t.eq(first(view.comments).id, first(comments).id)
    t.eq(first(view.comments).body, first(comments).body)
    t.eq(first(view.comments).author.login, first(comments).user.login)
    t.eq(first(view.comments).createdAt, first(comments).created_at)
  end,

  test_comment_body_fixture_preserves_raw_json_body_value = function()
    local table_nil = seam.view_comment_json({ body = nil })
    local scalar_nil = seam.view_comment_json(nil)
    local numeric = decode(seam.view_comment_json({ body = 123 }))

    t.is_true(table_nil:find('"body":null', 1, true) ~= nil)
    t.is_true(scalar_nil:find('"body":null', 1, true) ~= nil)
    t.eq(numeric.body, 123)
  end,

  test_unregistered_entity_read_fails_closed = function()
    local ok = pcall(function()
      require("devloop.github_proxy_entity_view").fetch_issue_view(core, "owner/repo", 404, "2026-06-14T00:00:00Z", { consumer = "unregistered" })
    end)
    t.eq(ok, false)
  end,
}
