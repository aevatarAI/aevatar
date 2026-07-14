local parsers_issue = require("devloop.parsers.issue")
local gh = require("forge.github")
local issue_adapter = require("forge.github.issue")
local core = require("core")

local function argv_equal(left, right)
  if type(left) ~= "table" or #left ~= #right then
    return false
  end
  for index, value in ipairs(right) do
    if left[index] ~= value then
      return false
    end
  end
  return true
end

local function command_count(commands, expected)
  local count = 0
  for _, command in ipairs(commands) do
    if argv_equal(command, expected) then
      count = count + 1
    end
  end
  return count
end

local function assert_comment_equal(left, right)
  assert(left.body == right.body)
  assert(left.author_login == right.author_login)
  assert(left.created_at == right.created_at)
end

return {
  test_read_issue_builds_exact_generic_command_and_parses_full_issue = function()
    local seen
    local handle = gh.new(function(opts)
      seen = opts.argv
      return {
        stdout = '{"number":42,"state":"OPEN","title":"t","body":"issue body","url":"https://github.com/owner/repo/issues/42","updatedAt":"2026-06-15T00:00:00Z","labels":[{"name":"fkst-dev:enabled"}],"comments":[{"id":1,"body":"b","author":{"login":"bot"},"createdAt":"2026-06-14T00:00:00Z"}],"assignees":[{"login":"dev"}],"author":{"login":"author"}}',
        stderr = "",
        exit_code = 0,
      }
    end)

    local issue = handle.read_issue({ kind = "external", ref = "owner/repo#issue/42" })

    assert(argv_equal(seen, { "gh", "issue", "view", "42", "--repo", "owner/repo", "--json", "number,title,body,url,updatedAt,state,labels,comments,assignees,author" }))
    assert(issue.number == 42)
    assert(issue.source_ref.kind == "external")
    assert(issue.source_ref.ref == "owner/repo#issue/42")
    assert(issue.title == "t")
    assert(issue.body == "issue body")
    assert(issue.url == "https://github.com/owner/repo/issues/42")
    assert(issue.updated_at == "2026-06-15T00:00:00Z")
    assert(issue.state == "OPEN")
    assert(issue.labels[1] == "fkst-dev:enabled")
    assert(issue.comments[1].id == 1)
    assert(issue.comments[1].body == "b")
    assert(issue.comments[1].author_login == "bot")
    assert(issue.comments[1].created_at == "2026-06-14T00:00:00Z")
    assert(issue.assignees[1] == "dev")
    assert(issue.author_login == "author")
  end,

  test_normalize_issue_preserves_loop_used_fields_from_old_loop_stdout = function()
    local stdout = '{"state":"OPEN","title":"t","updatedAt":"2026-06-15T00:00:00Z","labels":[{"name":"fkst-dev:enabled"},{"name":"bug"}],"comments":[{"id":1,"body":"b","author":{"login":"bot"},"createdAt":"2026-06-14T00:00:00Z"}],"assignees":[{"login":"dev"}],"author":{"login":"author"}}'
    local ref = { kind = "external", ref = "owner/repo#issue/42" }
    local normalized = issue_adapter.normalize_issue(stdout, ref)
    local old = parsers_issue.parse_issue_view_loop(core, stdout)

    assert(normalized.title == old.title)
    assert(normalized.updated_at == old.updated_at)
    assert(normalized.state == old.state)
    assert(#normalized.labels == #old.labels)
    for index, label in ipairs(old.labels) do
      assert(normalized.labels[index] == label)
    end
    assert(#normalized.comments == #old.comments)
    for index, comment in ipairs(old.comments) do
      assert_comment_equal(normalized.comments[index], comment)
    end
    assert(#normalized.assignees == #old.assignees)
    for index, assignee in ipairs(old.assignees) do
      assert(normalized.assignees[index] == assignee)
    end
    assert(normalized.author_login == old.author_login)
  end,

  test_read_issue_uses_validator_gated_cache_for_repeat_observe_read = function()
    local ref = { kind = "external", ref = "owner/cache-adapter#issue/42" }
    local key = issue_adapter.issue_view_cache_key("owner/cache-adapter", 42)
    cache_set(key, "")
    local commands = {}
    local handle = gh.new(function(opts)
      table.insert(commands, opts.argv)
      return {
        stdout = '{"number":42,"state":"OPEN","title":"cached adapter","body":"issue body","url":"https://github.com/owner/cache-adapter/issues/42","updatedAt":"2026-06-15T00:00:00Z","labels":[],"comments":[],"assignees":[],"author":{"login":"author"}}',
        stderr = "",
        exit_code = 0,
      }
    end)

    local first = handle.read_issue(ref)
    local second = handle.read_issue(ref, {
      updated_at = first.updated_at,
      consumer = "observe-contract",
    })

    assert(first.title == "cached adapter")
    assert(second.title == "cached adapter")
    assert(command_count(commands, { "gh", "issue", "view", "42", "--repo", "owner/cache-adapter", "--json", "number,title,body,url,updatedAt,state,labels,comments,assignees,author" }) == 1)
    assert(#commands == 1)
  end,

  test_read_issue_force_fresh_bypasses_cache_and_recaches = function()
    local ref = { kind = "external", ref = "owner/force-adapter#issue/43" }
    local key = issue_adapter.issue_view_cache_key("owner/force-adapter", 43)
    local comments_query = table.concat({ "per", "page=100" }, "_")
    local comments_path = "repos/owner/force-adapter/issues/43/comments?" .. comments_query
    cache_set(key, "")
    local commands = {}
    local handle = gh.new(function(opts)
      table.insert(commands, opts.argv)
      if argv_equal(opts.argv, { "gh", "api", "repos/owner/force-adapter/issues/43" }) then
        return {
          stdout = '{"number":43,"state":"open","title":"fresh adapter","body":"fresh body","html_url":"https://github.com/owner/force-adapter/issues/43","updated_at":"2026-06-15T00:00:01Z","labels":[{"name":"fresh"}],"assignees":[{"login":"dev"}],"user":{"login":"author"}}',
          stderr = "",
          exit_code = 0,
        }
      end
      if argv_equal(opts.argv, { "gh", "api", "--paginate", "--slurp", comments_path }) then
        return {
          stdout = '[{"id":7,"body":"fresh comment","user":{"login":"bot"},"created_at":"2026-06-15T00:00:01Z"}]',
          stderr = "",
          exit_code = 0,
        }
      end
      return {
        stdout = '{"number":43,"state":"OPEN","title":"stale adapter","body":"stale body","url":"https://github.com/owner/force-adapter/issues/43","updatedAt":"2026-06-15T00:00:00Z","labels":[],"comments":[],"assignees":[],"author":{"login":"author"}}',
        stderr = "",
        exit_code = 0,
      }
    end)

    local stale = handle.read_issue(ref)
    local fresh = handle.read_issue(ref, {
      force_fresh = true,
      consumer = "authority-contract",
    })
    local cached = handle.read_issue(ref, {
      updated_at = fresh.updated_at,
      consumer = "observe-contract",
    })

    assert(stale.title == "stale adapter")
    assert(fresh.title == "fresh adapter")
    assert(fresh.updated_at == "2026-06-15T00:00:01Z")
    assert(fresh.labels[1] == "fresh")
    assert(fresh.assignees[1] == "dev")
    assert(fresh.comments[1].body == "fresh comment")
    assert(fresh.comments[1].author_login == "bot")
    assert(cached.title == "fresh adapter")
    assert(command_count(commands, { "gh", "issue", "view", "43", "--repo", "owner/force-adapter", "--json", "number,title,body,url,updatedAt,state,labels,comments,assignees,author" }) == 1)
    assert(command_count(commands, { "gh", "api", "repos/owner/force-adapter/issues/43" }) == 1)
    assert(command_count(commands, { "gh", "api", "--paginate", "--slurp", comments_path }) == 1)
    assert(#commands == 3)
  end,
}
