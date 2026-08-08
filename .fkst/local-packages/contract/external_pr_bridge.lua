-- contract.external_pr_bridge: shared protocol for external PR bridge markers.
local B = {}

local marker_prefix = "fkst:github-external-pr-intake:external-pr-bridge:v1"
-- Single grammar source for the marker: a complete HTML comment carrying the prefix.
-- Presence is decided on this COMPLETE-comment grammar, never on a bare-prefix substring,
-- so a normal issue body that merely mentions the prefix in prose is not a bridge marker.
local marker_comment_pattern = "<!%-%-%s*" .. marker_prefix:gsub("%-", "%%-") .. ".-%-%->"

local function positive_number(value, context)
  local number = tonumber(value)
  if number == nil or number < 1 or number % 1 ~= 0 then
    error("contract.external_pr_bridge: invalid-number: " .. tostring(context))
  end
  return number
end

local function source_ref_text(repo, pr_number)
  return tostring(repo) .. "#pr/" .. tostring(positive_number(pr_number, "pr"))
end

local function marker_source_ref(repo, pr_number)
  return "external:" .. source_ref_text(repo, pr_number)
end

local function marker_attr(marker, name)
  return tostring(marker or ""):match(tostring(name) .. '="([^"]*)"')
end

function B.source_ref(repo, pr_number)
  return {
    kind = "external",
    ref = source_ref_text(repo, pr_number),
  }
end

function B.parse_source_ref(source_ref)
  if type(source_ref) ~= "table" or source_ref.kind ~= "external" then
    error("contract.external_pr_bridge: source-ref-required: external PR source_ref is required")
  end
  local repo, number = tostring(source_ref.ref or ""):match("^([^#]+)#pr/(%d+)$")
  if repo == nil then
    error("contract.external_pr_bridge: invalid-source-ref: external PR source_ref is required")
  end
  return repo, positive_number(number, "source_ref pr")
end

function B.marker(repo, pr_number, issue_number)
  local number = positive_number(pr_number, "marker pr")
  local marker = '<!-- ' .. marker_prefix .. ' repo="'
    .. tostring(repo)
    .. '" pr="'
    .. tostring(number)
    .. '" source_ref="'
    .. marker_source_ref(repo, number)
    .. '"'
  if issue_number ~= nil then
    marker = marker .. ' issue="' .. tostring(positive_number(issue_number, "marker issue")) .. '"'
  end
  return marker .. " -->"
end

function B.search_query(repo, pr_number)
  return marker_prefix .. ' repo="'
    .. tostring(repo)
    .. '" pr="'
    .. tostring(positive_number(pr_number, "search pr"))
    .. '"'
end

function B.parse_marker_text(marker)
  if tostring(marker or ""):find(marker_prefix, 1, true) == nil then
    return nil
  end
  local repo = marker_attr(marker, "repo")
  local pr = marker_attr(marker, "pr")
  local source_ref = marker_attr(marker, "source_ref")
  if repo == nil or pr == nil or source_ref == nil then
    error("contract.external_pr_bridge: invalid-marker: external PR bridge marker is incomplete")
  end
  local pr_number = positive_number(pr, "marker pr")
  local expected_source_ref = marker_source_ref(repo, pr_number)
  if source_ref ~= expected_source_ref then
    error("contract.external_pr_bridge: source-ref-mismatch: external PR bridge marker source_ref mismatch")
  end
  local issue = marker_attr(marker, "issue")
  return {
    repo = repo,
    pr_number = pr_number,
    source_ref = B.source_ref(repo, pr_number),
    source_ref_text = source_ref,
    issue_number = issue ~= nil and positive_number(issue, "marker issue") or nil,
    marker = marker,
  }
end

function B.find_marker_comment(body)
  return tostring(body or ""):match(marker_comment_pattern)
end

function B.find_marker(body)
  local marker = B.find_marker_comment(body)
  if marker == nil then
    return nil
  end
  return B.parse_marker_text(marker)
end

function B.has_marker(body)
  return B.find_marker_comment(body) ~= nil
end

return B
