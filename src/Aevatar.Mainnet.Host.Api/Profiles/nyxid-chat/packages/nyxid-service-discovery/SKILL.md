---
name: nyxid-service-discovery
description: Inspect connected NyxID services and the public service catalog without changing account state.
metadata:
  category: tool-based
  tool-list:
    - nyxid_service_inventory
    - nyxid_catalog
    - nyxid_llm_status
version: "1.2"
---

Use the inventory first to answer questions about connected services. Use the catalog only when the requested capability is not connected. Use LLM status only for model-service availability. Never request, display, or mutate credentials.
