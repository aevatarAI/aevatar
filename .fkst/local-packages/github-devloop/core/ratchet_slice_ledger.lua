local S = {}

function S.install(M)

function M.ratchet_slice_ledger_ref(entry_key)
  return "refs/fkst/migration-slices/" .. tostring(entry_key)
end

function M.parse_ratchet_slice_ledger_ref_sha(stdout)
  local sha = tostring(stdout or ""):match("^(%x+)%s+refs/")
  if sha ~= nil and #sha == 40 then
    return sha
  end
  return nil
end

function M.ratchet_slice_ledger_message(stdout)
  local text = tostring(stdout or "")
  local _, finish = text:find("\n\n", 1, true)
  if finish == nil then
    return text
  end
  return text:sub(finish + 1)
end

function M.decode_ratchet_slice_ledger(stdout)
  local ok, decoded = pcall(json.decode, M.ratchet_slice_ledger_message(stdout))
  if not ok or type(decoded) ~= "table" then
    return nil
  end
  return decoded
end

end

return S
