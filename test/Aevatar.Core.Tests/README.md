# Aevatar.Foundation.Core.Tests

`Aevatar.Foundation.Core.Tests` 覆盖 Core 与 Runtime 的关键行为，测试风格以 BDD 场景为主。

## 覆盖范围

- 统一事件 Pipeline（静态 handler + 动态 module）
- Hook 生命周期（开始、结束、异常）
- StateGuard 写保护与作用域嵌套
- 父子层级下的 Stream 传播行为

## 测试组织

- `Bdd/`：行为场景测试
- `TestHelper.cs`：通用测试工具与测试 Agent
- `test_messages.proto`：测试消息定义

## 运行

在仓库根目录执行：

```bash
dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj
```
