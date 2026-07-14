-- std module behavior tests are hosted in github-proxy (a flat package, the
-- strictest single-root conformance gate) because the engine test runner only
-- scans <root>/tests and <root>/departments/* (no recursion into std/tests).
local github_view = require("forge.github_view")
local t = fkst.test

return {
  test_updated_at_parsers_accept_view_json_and_trim_stdout = function()
    t.eq(github_view.parse_view_updated_at('{"updatedAt":"2026-06-03T01:02:03Z"}'), "2026-06-03T01:02:03Z")
    t.eq(github_view.parse_view_updated_at('{"updated_at":"2026-06-03T01:02:04Z"}'), "2026-06-03T01:02:04Z")
    t.is_nil(github_view.parse_view_updated_at('{"title":"missing"}'))
    t.is_nil(github_view.parse_view_updated_at("not json"))
    t.eq(github_view.parse_updated_at_stdout(" 2026-06-03T01:02:05Z\n"), "2026-06-03T01:02:05Z")
    t.is_nil(github_view.parse_updated_at_stdout(" \n\t "))
  end,

  test_json_value_escapes_rest_view_control_bytes = function()
    local rendered = github_view.json_value("quote\" slash\\ newline\n control" .. string.char(1))
    t.eq(json.decode(rendered), "quote\" slash\\ newline\n control" .. string.char(1))
    t.eq(github_view.json_value(nil), "null")
    t.eq(github_view.json_value(true), "true")
    t.eq(github_view.json_value(false), "false")
    t.eq(github_view.json_value(42), "42")
  end,

  test_issue_rest_json_fields_render_to_view_shape = function()
    local issue = json.decode('{"state":"open","updated_at":"2026-06-03T01:02:03Z","labels":[{"name":"bug"},"triage"],"assignees":[{"login":"fkst-test-bot"},"reviewer"]}')
    local view_json = '{"state":' .. github_view.json_value(github_view.rest_state(issue.state))
      .. ',"updatedAt":' .. github_view.json_value(issue.updated_at)
      .. ',"labels":' .. github_view.labels_json(issue.labels)
      .. ',"assignees":' .. github_view.assignees_json(issue.assignees)
      .. "}"
    local view = json.decode(view_json)

    t.eq(view.state, "OPEN")
    t.eq(view.updatedAt, "2026-06-03T01:02:03Z")
    t.eq(view.labels[1].name, "bug")
    t.eq(view.labels[2].name, "triage")
    t.eq(view.assignees[1].login, "fkst-test-bot")
    t.eq(view.assignees[2].login, "reviewer")
  end,

  test_label_names_extracts_supported_issue_label_shapes = function()
    local labels = github_view.label_names({
      { name = "bug" },
      "triage",
      { name = 42 },
      { color = "ededed" },
      false,
      { name = false },
    })

    t.eq(#labels, 4)
    t.eq(labels[1], "bug")
    t.eq(labels[2], "triage")
    t.eq(labels[3], "42")
    t.eq(labels[4], "false")
    t.eq(#github_view.label_names(nil), 0)
    t.eq(#github_view.label_names({}), 0)
  end,

  test_pr_rest_json_fields_render_to_view_shape = function()
    local pr = json.decode('{"state":"closed","merged_at":"2026-06-03T01:02:03Z","head":{"repo":{"owner":{"login":"fork"},"name":"repo"}},"base":{"repo":{"full_name":"owner/repo"}}}')
    local view_json = '{"state":' .. github_view.json_value(github_view.rest_pr_state(pr))
      .. ',"headRepository":{"nameWithOwner":' .. github_view.json_value(github_view.repo_name_with_owner(pr.head.repo))
      .. ',"owner":{"login":' .. github_view.json_value(github_view.repo_owner_login(pr.head.repo)) .. "}}"
      .. ',"baseRepository":{"nameWithOwner":' .. github_view.json_value(github_view.repo_name_with_owner(pr.base.repo)) .. "}"
      .. "}"
    local view = json.decode(view_json)

    t.eq(view.state, "MERGED")
    t.eq(view.headRepository.nameWithOwner, "fork/repo")
    t.eq(view.headRepository.owner.login, "fork")
    t.eq(view.baseRepository.nameWithOwner, "owner/repo")
  end,

  test_comment_decoding_and_flattening_preserve_rest_comment_objects = function()
    local decoded = github_view.decode_comments_json('[[{"id":1,"body":"first","user":{"login":"a"}}],{"comments":[{"id":2,"body":"second","author":{"login":"b"}}]}]')
    local comments = {}
    github_view.append_comments(comments, decoded)

    t.eq(#comments, 2)
    t.eq(comments[1].id, 1)
    t.eq(comments[1].body, "first")
    t.eq(comments[1].user.login, "a")
    t.eq(comments[2].id, 2)
    t.eq(comments[2].body, "second")
    t.eq(comments[2].author.login, "b")

    local empty = {}
    github_view.append_comments(empty, github_view.decode_comments_json(""))
    t.eq(#empty, 0)
  end,
}
