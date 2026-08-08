local source_refs = require("contract.source_ref")
local strings = require("contract.strings")

local I = {}

I.max_key_len = 200
I.max_dedup_len = 512
I.max_title_len = 240
I.max_repo_key_len = 100
I.max_issue_key_len = 30

function I.truncate_utf8(value, limit)
  if type(truncate_utf8) ~= "function" then
    error("github-devloop: truncate_utf8 SDK primitive is required")
  end
  return truncate_utf8(value, limit)
end

function I.dedup_key(parts)
  local key = strings.sanitize_key(table.concat(parts, "/"), false)
  if #key > I.max_dedup_len then
    local suffix = "-" .. strings.decimal_checksum(key)
    key = I.truncate_utf8(key, I.max_dedup_len - #suffix):gsub("[/%-]+$", "") .. suffix
  end
  if not strings.is_path_safe_key(key, I.max_dedup_len) then
    error("github-devloop: invalid dedup_key")
  end
  return key
end

function I.safe_repo(repo)
  local safe = strings.sanitize_key(repo, I.max_key_len):sub(1, I.max_repo_key_len):gsub("/+$", "")
  if safe == "" then
    return "empty"
  end
  return safe
end

function I.safe_issue(issue_number)
  local safe = strings.sanitize_key(issue_number, I.max_key_len):sub(1, I.max_issue_key_len):gsub("/+$", "")
  if safe == "" then
    return "empty"
  end
  return safe
end

function I.proposal_id(repo, issue_number)
  return "github-devloop/issue/" .. I.safe_repo(repo) .. "/" .. I.safe_issue(issue_number)
end

function I.parse_proposal_id(id)
  if type(id) ~= "string" then
    return nil
  end

  local rest = id:match("^github%-devloop/issue/(.+)$")
  if rest == nil then
    return nil
  end

  local issue_number = rest:match("/([^/]+)$")
  local repo = issue_number and rest:sub(1, #rest - #issue_number - 1) or nil
  if repo == nil or repo == "" or issue_number == nil or issue_number == "" then
    return nil
  end
  return repo, issue_number
end

function I.issue_ref_round_trips(repo, issue_number)
  local repo_text = tostring(repo)
  local issue_text = tostring(issue_number)
  if I.safe_repo(repo) ~= repo_text then
    return false
  end
  if I.safe_issue(issue_number) ~= issue_text then
    return false
  end

  local parsed_repo, parsed_issue = I.parse_proposal_id(I.proposal_id(repo, issue_number))
  return parsed_repo == repo_text and parsed_issue == issue_text
end

function I.issue_source_ref(repo, issue_number)
  return {
    kind = "external",
    ref = tostring(repo) .. "#issue/" .. tostring(issue_number),
  }
end

function I.normalize_source_ref(source_ref)
  if not source_refs.has_bounded_source_ref(source_ref, I.max_key_len) then
    error("github-devloop: invalid source_ref")
  end
  return {
    kind = source_ref.kind,
    ref = source_ref.ref,
  }
end

return I
