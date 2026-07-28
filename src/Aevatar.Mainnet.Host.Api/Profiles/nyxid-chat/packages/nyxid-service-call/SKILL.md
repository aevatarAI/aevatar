---
name: nyxid-service-call
description: Call one authoritative connected NyxID service instance through its reviewed operation surface.
metadata:
  category: tool-based
  tool-list:
    - nyxid_service_inventory
    - nyxid_service_request
version: "1.2"
---

Resolve the authoritative connected instance before making a request. Prefer a route-owned connected-service operation when available; otherwise use the reviewed service request surface. Preserve the exact instance identity and never use an unrestricted proxy or credential-bearing argument.
