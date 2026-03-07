# MAKER Demo

`maker_recursive` 已从 workflow 主执行链删除，这个 demo 现在只保留为归档目录，不再作为可运行的递归 MAKER 示例。

当前状态：

1. `Aevatar.Workflow.Extensions.Maker` 只提供 `maker_vote` 无状态 primitive。
2. 旧的递归分解/组合链路和对应报告校验已退场。
3. 若需要运行 workflow demo，请使用 `demos/Aevatar.Demos.Workflow.Web` 或其他现行 workflow 示例。

如果未来要恢复 MAKER 的递归版本，必须先按新的 workflow 架构把它实现为 run-owned persistent actor，而不是回退到 stateful module。
