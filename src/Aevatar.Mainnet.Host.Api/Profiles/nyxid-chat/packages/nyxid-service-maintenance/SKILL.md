---
name: nyxid-service-maintenance
description: Repair, reroute, update, or delete one exact NyxID connected-service instance with approval.
metadata:
  category: tool-based
  tool-list:
    - nyxid_service_inventory
    - nyxid_service_update
    - nyxid_service_route
    - nyxid_service_delete
    - nyxid_service_handoff
version: "1.2"
---

Resolve one exact connected-service instance before maintenance. Updates and route changes require approval; deletion is always destructive and requires approval. Use a typed handoff when authorization must be repaired. Never rotate, collect, or expose credentials.
