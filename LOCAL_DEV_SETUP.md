# Aevatar 本地开发环境

这份文档只保留当前仓库里还存在的入口。

## 入口

- Mainnet 后端：`bash src/Aevatar.Mainnet.Host.Api/boot.sh`
- Console Web：`bash apps/aevatar-console-web/boot.sh`
- Workflow Host：`dotnet run --project src/workflow/Aevatar.Workflow.Host.Api`

## 后端模式

`src/Aevatar.Mainnet.Host.Api/boot.sh` 支持三种模式：

- `local`：完全本地，读写态都是临时的
- `persistent-local`：Orleans + Garnet，actor 状态可跨重启保留
- `distributed`：Kafka / Garnet / Elasticsearch 分布式配置；图投影默认关闭，不依赖 Neo4j

常用示例：

```bash
bash src/Aevatar.Mainnet.Host.Api/boot.sh --mode local
bash src/Aevatar.Mainnet.Host.Api/boot.sh --mode persistent-local
```

默认端口是 `5080`。如果要改端口：

```bash
bash src/Aevatar.Mainnet.Host.Api/boot.sh --port 8080
```

## 前端控制台

`apps/aevatar-console-web/boot.sh` 会启动控制台前端，并通过 `AEVATAR_API_TARGET`
把请求代理到后端。

```bash
bash apps/aevatar-console-web/boot.sh
```

如果后端不在默认端口，可以显式指定：

```bash
AEVATAR_API_TARGET=http://127.0.0.1:5080 \
  bash apps/aevatar-console-web/boot.sh
```

## 配置密钥

仓库不再提供旧的 CLI 配置工具。请直接编辑 `~/.aevatar/secrets.json`，
格式说明见 [src/Aevatar.Configuration/README.md](src/Aevatar.Configuration/README.md)。

常见的 LLM API Key 也可以直接通过环境变量注入：

- `DEEPSEEK_API_KEY`
- `OPENAI_API_KEY`
- `ANTHROPIC_API_KEY`

## 工作流示例

仓库根目录保留了一个最小示例工作流：

- `workflows/simple_qa.yaml`

Workflow Host 启动后，可用 `GET /api/workflows` 查看可用工作流。
