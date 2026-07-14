local S = {}

function S.install(M)
local function assignee_login(assignee)
  if type(assignee) == "table" then
    if assignee.login ~= nil then
      return tostring(assignee.login)
    end
    if assignee.name ~= nil then
      return tostring(assignee.name)
    end
  elseif assignee ~= nil then
    return tostring(assignee)
  end
  return nil
end

function M.assignee_logins(value)
  local logins = {}
  if type(value) ~= "table" then
    return logins
  end
  for _, assignee in ipairs(value) do
    local login = assignee_login(assignee)
    if login ~= nil and login ~= "" then
      table.insert(logins, login)
    end
  end
  return logins
end

function M.gh_issue_view_assignees_cmd(repo, issue_number)
  return M.gh_issue_rest_view_cmd(repo, issue_number)
end

function M.github_issue_assign(repo, issue_number, login, timeout)
  return M.github().issue_assign(repo, issue_number, login, timeout or 30)
end

function M.github_issue_unassign(repo, issue_number, login, timeout)
  return M.github().issue_unassign(repo, issue_number, login, timeout or 30)
end

function M.parse_issue_assignees(stdout)
  local decoded = json.decode(stdout or "{}")
  return M.assignee_logins(decoded.assignees)
end

function M.issue_claim_held_by_self(repo, issue_number, login)
  local view = M.gh_exec(M.gh_issue_view_assignees_cmd(repo, issue_number), 30, "GitHub issue REST assignees")
  local logins = M.parse_issue_assignees(view.stdout)
  return #logins == 1 and logins[1] == tostring(login or "")
end

local function claim_source_ref_matches(payload, repo, issue_number)
  local claim = payload and payload.claim
  local source_ref = claim and claim.source_ref
  if type(source_ref) ~= "table" or source_ref.kind ~= "external" then
    return false
  end
  return tostring(source_ref.ref or "") == tostring(repo) .. "#issue/" .. tostring(issue_number)
end

local function verify_claim_log(dept, reason, repo, issue_number, owner)
  local fields = {
    "outcome=lost",
    "reason=" .. tostring(reason),
    "repo=" .. tostring(repo),
    "issue=" .. tostring(issue_number),
  }
  if owner ~= nil and tostring(owner) ~= "" then
    table.insert(fields, "owner=" .. tostring(owner))
  end
  M.log_line("info", dept, "CLAIM", fields)
end

function M.verify_issue_claim_before_write(payload, repo, issue_number, dept)
  local claim = payload and payload.claim
  if type(claim) ~= "table" or claim.owner == nil or tostring(claim.owner) == "" then
    return true
  end
  if not claim_source_ref_matches(payload, repo, issue_number) then
    verify_claim_log(dept, "source-ref-mismatch", repo, issue_number, claim.owner)
    return false
  end
  local owner = tostring(claim.owner)
  if M.issue_claim_held_by_self(repo, issue_number, owner) then
    return true
  end
  verify_claim_log(dept, "assignee-claim-lost", repo, issue_number, owner)
  return false
end

function M.verify_issue_claim_in_issue(issue, payload, repo, issue_number, dept)
  local claim = payload and payload.claim
  if type(claim) ~= "table" or claim.owner == nil or tostring(claim.owner) == "" then
    return true
  end
  if not claim_source_ref_matches(payload, repo, issue_number) then
    verify_claim_log(dept, "source-ref-mismatch", repo, issue_number, claim.owner)
    return false
  end
  local logins = M.assignee_logins(issue and issue.assignees)
  if #logins == 1 and logins[1] == tostring(claim.owner) then
    return true
  end
  verify_claim_log(dept, "assignee-claim-lost", repo, issue_number, claim.owner)
  return false
end

end

return S
