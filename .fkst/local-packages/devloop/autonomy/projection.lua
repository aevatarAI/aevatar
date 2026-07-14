local projection = {}

local function attempt_matches_autonomy_fact(row, fact)
  if type(row) ~= "table" or type(fact) ~= "table" then
    return false
  end
  if tostring(row.proposal_id or "") ~= tostring(fact.proposal_id or "") then
    return false
  end
  if tostring(row.pr_number or "") ~= tostring(fact.pr_number or "") then
    return false
  end
  if tostring(row.terminal_version or "") ~= tostring(fact.version or "") then
    return false
  end
  if fact.head_sha ~= nil and row.head_sha ~= nil and tostring(row.head_sha) ~= tostring(fact.head_sha) then
    return false
  end
  return true
end

function projection.apply_audited_fact(value, fact)
  if type(value) ~= "table" or type(value.attempts) ~= "table" or type(fact) ~= "table" then
    return value
  end
  local valid_merges = 0
  for _, row in ipairs(value.attempts) do
    if attempt_matches_autonomy_fact(row, fact) then
      row.valid_autonomous_merge = fact.valid_autonomous_merge
      if type(row.autonomy_result) == "table" then
        row.autonomy_result.valid_autonomous_merge = fact.valid_autonomous_merge
        row.autonomy_result.gates = fact.gates
      end
    end
    if row.valid_autonomous_merge == "true" then
      valid_merges = valid_merges + 1
    end
  end
  value.valid_merges = valid_merges
  return value
end

return projection
