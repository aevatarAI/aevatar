# Aevatar 本地开发环境设置

## 概述

本文档介绍如何使用本地开发脚本来启动和调试 Aevatar 服务。我们提供了多种启动脚本：

1. `local-dev/boot.sh` - 简单命令行脚本（推荐，git忽略）
2. `local-dev.sh` (根目录) - 交互式菜单脚本
3. `tools/local-dev.sh` - 功能完整的启动脚本

## 快速开始

### 选项 A: 使用简单脚本（推荐）
```bash
# 进入 local-dev 目录
cd local-dev

# 授予执行权限
chmod +x boot.sh

# 启动 Workflow API（默认端口5100）
./boot.sh

# 或者启动 Mainnet API 在自定义端口
./boot.sh --mainnet --port 8080
```

### 选项 B: 使用交互式菜单
```bash
# 授予执行权限
chmod +x local-dev.sh tools/local-dev.sh

# 运行启动脚本
./local-dev.sh

# 这将启动一个交互式菜单，您可以选择不同的启动选项
```

## 启动模式

### 模式 1: Workflow API (内存模式)
- 无需外部服务（Docker）
- 使用内存存储和事件路由
- 适合快速开发和功能验证
- API 端口: 5100（默认，可自定义）

### 模式 2: Mainnet API (内存模式)
- 无需外部服务
- 完整的 Mainnet 功能，包括用户工作流管理
- 包含 GAgentService 功能
- API 端口: 5100（默认，可自定义）

### 模式 3: 分布式模式
- 需要 Docker
- 启动完整的基础设施：
  - Kafka (消息队列)
  - Garnet (Redis协议存储)
  - Elasticsearch (文档存储)
  - Neo4j (图数据库)
- 适合测试分布式功能和持久化

## 功能特性

### 1. 依赖检查
脚本会自动检查：
- .NET SDK 10.0+
- Docker (分布式模式需要)
- LLM API Key 配置

### 2. LLM API Key 配置
支持多种配置方式：
- 环境变量 (`DEEPSEEK_API_KEY`, `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`)
- 配置文件 (`~/.aevatar/secrets.json`)
- 通过配置工具: `dotnet run --project tools/Aevatar.Tools.Cli -- config`

### 3. NyxID spec catalog token
Mainnet API 的 `nyxid_search_capabilities` 与 `nyxid_proxy_execute` 依赖
NyxID OpenAPI spec catalog。若 `Aevatar:NyxId:Authority` 已配置但未提供
`Aevatar:NyxId:SpecFetchToken`，服务仍可启动，但 `/health/ready` 会报告
`nyxid-catalog` unhealthy，generic capability discovery 不可用。

本地需要验证这两类工具或 readiness 时，设置：

```bash
export AEVATAR_Aevatar__NyxId__SpecFetchToken="<real-user-nyxid-api-key>"
```

### 4. 服务管理
- 启动/停止服务
- 后台运行，日志保存到 `logs/` 目录
- PID 文件管理，支持优雅停止

### 5. 日志查看
- 实时查看日志
- 跟踪多个服务日志
- 日志轮转和清理

### 6. 状态监控
- 检查服务运行状态
- 查看基础设施状态
- 验证配置

## 常用命令示例

### 使用简单脚本 (boot.sh)
```bash
# 进入 local-dev 目录
cd local-dev

# 启动 Workflow API（默认端口5100）
./boot.sh

# 启动 Mainnet API 在端口8080
./boot.sh --mainnet --port 8080

# 检查服务状态
./boot.sh --status

# 查看日志
./boot.sh --logs

# 停止所有服务
./boot.sh --stop
```

### 使用交互式菜单
```bash
# 进入项目根目录
cd /Users/zhaoyiqi/Code/aevatar

# 启动脚本并选择选项 1（Workflow API）
./local-dev.sh
```

### 直接启动 Mainnet API
```bash
# 或者使用快捷方式
./tools/local-dev.sh
# 然后在菜单中选择选项 2
```

### 配置 LLM API Key
```bash
# 方法 1: 环境变量
export DEEPSEEK_API_KEY="sk-..."

# 方法 2: 使用配置工具
dotnet run --project tools/Aevatar.Tools.Cli -- config

# 方法 3: 通过脚本菜单
./local-dev.sh
# 选择选项 7 -> 选项 2
```

### 查看服务状态
```bash
./local-dev.sh
# 选择选项 6
```

### 查看日志
```bash
./local-dev.sh
# 选择选项 5
# 然后选择要查看的日志文件
```

## 目录结构

```
aevatar/
├── local-dev/                # 本地开发脚本（git忽略）
│   ├── boot.sh              # 简单命令行启动脚本
│   └── README.md            # 本地开发说明
├── local-dev.sh             # 根目录包装脚本
├── tools/local-dev.sh       # 主启动脚本（交互式）
├── logs/                    # 日志目录（自动创建）
├── .pids/                   # PID 文件目录（自动创建）
├── docker-compose.yml       # 基础设施配置
├── docker-compose.projection-providers.yml
└── docker-compose.mainnet-cluster.yml
```

## 故障排除

### 1. .NET SDK 未找到
```bash
# 检查 .NET 安装
dotnet --version

# 如果未安装，请访问：
# https://dotnet.microsoft.com/download
```

### 2. Docker 未安装
```bash
# 检查 Docker 安装
docker --version

# 如果未安装，请访问：
# https://docs.docker.com/get-docker/
```

### 3. 端口被占用
```bash
# 检查端口使用（默认端口5100）
lsof -i :5100

# 停止占用端口的进程
kill -9 <PID>

# 或者使用不同端口启动
./local-dev/boot.sh --port 8080
```

### 4. LLM API Key 错误
```bash
# 检查配置
cat ~/.aevatar/secrets.json

# 或检查环境变量
echo $DEEPSEEK_API_KEY
```

## 手动启动（不使用脚本）

如果您希望手动启动服务：

### 内存模式
```bash
# Workflow API（默认端口5100，可自定义）
ASPNETCORE_URLS=http://0.0.0.0:5100 dotnet run --project src/workflow/Aevatar.Workflow.Host.Api

# Mainnet API（默认端口5100，可自定义）
ASPNETCORE_URLS=http://0.0.0.0:5100 dotnet run --project src/Aevatar.Mainnet.Host.Api
```

### 分布式模式
```bash
# 启动基础设施
docker compose -f docker-compose.yml up -d
docker compose -f docker-compose.projection-providers.yml up -d

# 启动 Mainnet API（使用分布式配置）
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://0.0.0.0:5100 dotnet run --project src/Aevatar.Mainnet.Host.Api
```

## 注意事项

1. **端口配置**：默认使用仓库允许的开发端口5100
2. **内存模式**：数据不会持久化，服务停止后数据丢失
3. **分布式模式**：需要足够的系统资源（建议 8GB+ RAM）
4. **首次启动**：分布式模式首次启动可能需要较长时间下载 Docker 镜像
5. **网络代理**：如果使用代理，请确保 Docker 和 .NET 能正常访问网络

## 支持的功能

### 简单脚本 (boot.sh)
- [x] 命令行参数启动
- [x] 端口自定义（默认5100）
- [x] Workflow/Mainnet API选择
- [x] 服务状态检查
- [x] 日志查看
- [x] 优雅停止服务

### 交互式脚本 (local-dev.sh)
- [x] Workflow API 启动
- [x] Mainnet API 启动
- [x] 分布式基础设施管理
- [x] LLM API Key 配置
- [x] 日志管理
- [x] 服务状态监控
- [x] 优雅停止服务

## 更新日志

- 2026-03-17: 简单脚本版本
  - 新增 `local-dev/boot.sh` 简单命令行脚本
  - 默认端口改为仓库允许的开发端口5100
  - 所有脚本文档端口统一更新为5100

- 2026-03-17: 初始版本发布
  - 支持内存模式和分布式模式
  - 交互式菜单界面
  - 完整的服务管理功能
