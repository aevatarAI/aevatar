# Aevatar.CQRS.Sagas.Runtime.FileSystem

Saga 状态的文件系统默认实现。

- `FileSystemSagaRepository`：Saga 状态读写。
- `SagaPathResolver`：存储目录规范。
- `AddCqrsSagasFileSystem(...)`：DI 注册入口。

默认目录：`artifacts/cqrs/sagas`。
