---
name: nyxid-service-connect
description: Determine whether a NyxID service connection needs an authorization handoff.
metadata:
  category: tool-based
  tool-list:
    - nyxid_service_inventory
    - nyxid_catalog
    - nyxid_service_handoff
version: "1.2"
---

Inspect the inventory, then the catalog entry for the requested capability. When authorization is required, use the typed service handoff. Do not accept credentials, arbitrary headers, service IDs guessed from labels, or inline connection secrets.
