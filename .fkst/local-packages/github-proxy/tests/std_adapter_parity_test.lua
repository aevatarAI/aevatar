local github = require("forge.github")
local github_fake = require("forge.github_fake")
local git = require("forge.git")
local git_fake = require("forge.git_fake")

local function sorted_public_methods(handle)
  local names = {}
  for key, value in pairs(handle) do
    if type(value) == "function" and tostring(key):sub(1, 1) ~= "_" then
      table.insert(names, tostring(key))
    end
  end
  table.sort(names)
  return names
end

local function assert_list_equal(left, right, path)
  assert(#left == #right, path .. " length mismatch")
  for index, value in ipairs(left) do
    assert(value == right[index], path .. "[" .. tostring(index) .. "] mismatch")
  end
end

local function assert_deep_equal(left, right, path)
  local left_type = type(left)
  local right_type = type(right)
  assert(left_type == right_type, path .. " type mismatch: " .. left_type .. " ~= " .. right_type)
  if left_type ~= "table" then
    assert(left == right, path .. " mismatch")
    return
  end
  for key, value in pairs(left) do
    assert_deep_equal(value, right[key], path .. "." .. tostring(key))
  end
  for key, _value in pairs(right) do
    assert(left[key] ~= nil, path .. "." .. tostring(key) .. " missing on left")
  end
end

local function canonical_issue_stdout()
  return [[{"number":42,"title":"Implement decision recorder","body":"the issue body","url":"https://github.com/owner/repo/issues/42","updatedAt":"2026-06-15T00:00:00Z","state":"OPEN","labels":[{"name":"adapter-thinking"},{"name":"bug"}],"comments":[{"id":101,"body":"state marker","author":{"login":"fkst-test-bot"},"createdAt":"2026-06-14T01:02:03Z"},{"id":102,"body":"human note","user":{"login":"human"},"created_at":"2026-06-14T02:03:04Z"}],"assignees":[{"login":"dev1"},{"login":"dev2"}],"author":{"login":"author"}}]]
end

return {
  test_github_read_issue_real_and_fake_normalize_same_gh_shape = function()
    local ref = { kind = "external", ref = "owner/repo#issue/42" }
    local stdout = canonical_issue_stdout()
    local real = github.new(function(_opts)
      return { stdout = stdout, stderr = "", exit_code = 0 }
    end)
    local fake_model = github_fake.model({
      issues = {
        ["owner/repo#issue/42"] = json.decode(stdout),
      },
    })
    local fake = github_fake.new(fake_model)

    local real_issue = real.read_issue(ref)
    local fake_issue = fake.read_issue(ref)

    assert_deep_equal(real_issue, fake_issue, "issue")
    assert(real_issue.number == 42)
    assert(real_issue.source_ref.kind == "external")
    assert(real_issue.source_ref.ref == "owner/repo#issue/42")
    assert(real_issue.body == "the issue body")
    assert(real_issue.url == "https://github.com/owner/repo/issues/42")
    assert_list_equal(real_issue.labels, { "adapter-thinking", "bug" }, "labels")
    assert(real_issue.comments[1].id == 101)
    assert(real_issue.comments[1].body == "state marker")
    assert(real_issue.comments[1].author_login == "fkst-test-bot")
    assert(real_issue.comments[1].created_at == "2026-06-14T01:02:03Z")
    assert(real_issue.comments[2].author_login == "human")
    assert_list_equal(real_issue.assignees, { "dev1", "dev2" }, "assignees")
    assert(real_issue.author_login == "author")
  end,

  test_public_method_sets_match_between_real_and_fake_adapters = function()
    local noop_exec = function(_opts)
      return { stdout = "{}", stderr = "", exit_code = 0 }
    end
    assert_list_equal(
      sorted_public_methods(github.new(noop_exec)),
      sorted_public_methods(github_fake.new(github_fake.model({}))),
      "forge.github methods"
    )
    assert_list_equal(
      sorted_public_methods(git.new(noop_exec)),
      sorted_public_methods(git_fake.new(git_fake.model({}))),
      "forge.git methods"
    )
  end,
}
