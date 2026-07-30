---
name: nyxid-service-connect
description: Determine whether a NyxID service connection needs an authorization handoff.
metadata:
  category: tool-based
  tool-list:
    - nyxid_service_inventory
    - nyxid_catalog
    - nyxid_require_service
version: "1.3"
---

Inspect the inventory, then call `nyxid_require_service` with the exact catalog slug. Only its verified registration-required receipt may request the typed connection journey. Do not accept credentials, arbitrary headers, service IDs guessed from labels, or inline connection secrets.
