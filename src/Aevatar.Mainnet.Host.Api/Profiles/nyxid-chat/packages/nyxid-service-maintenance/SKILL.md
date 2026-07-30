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
    - nyxid_require_service
version: "1.3"
---

Resolve one exact connected-service instance before maintenance. Updates and route changes require approval; deletion is always destructive and requires approval. If the required connection is absent, call `nyxid_require_service` with the exact catalog slug and trust only its typed result. Never rotate, collect, or expose credentials.
