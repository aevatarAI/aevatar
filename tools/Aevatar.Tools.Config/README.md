# Aevatar.Tools.Config

本地 Web 界面配置 Aevatar 用户密钥（LLM API Key 等），读写 `~/.aevatar/secrets.json`。与 Aevatar.Hosts.Api 使用同一套配置路径，配置完成后直接启动 Api 即可使用。

## 安装（可选，作为 dotnet tool）

```bash
# 在仓库根目录
dotnet pack tools/Aevatar.Tools.Config/Aevatar.Tools.Config.csproj -c Release
dotnet tool install --global --add-source ./tools/Aevatar.Tools.Config/bin/Release aevatar-config
```

## 使用

```bash
# 启动并自动打开浏览器
aevatar-config

# 不自动打开浏览器
aevatar-config --no-browser

# 指定端口
aevatar-config --port 8080
```

或从源码运行：

```bash
dotnet run --project tools/Aevatar.Tools.Config
```

默认地址：http://localhost:6677。所有 API 仅允许 localhost 访问。

## 功能

- **Providers**：添加/编辑 LLM Provider（OpenAI、DeepSeek、Anthropic 等），填写 Endpoint、Model、API Key，可测试连接、拉取模型列表。
- **Default provider**：设置默认 Provider，Api 会优先使用该 Key。
- **Raw JSON**：直接编辑 `secrets.json` 的嵌套 JSON。
- **config.json / agents/**：编辑 `~/.aevatar/config.json` 与 `~/.aevatar/agents/` 下的 YAML（如需要）。

配置写入 `~/.aevatar/secrets.json`（支持明文或加密格式，与 [Aevatar.Configuration](src/Aevatar.Configuration/README.md) 一致）。环境变量 `AEVATAR_SECRETS_PATH` 可指定密钥文件路径；`AEVATAR_HOME` 可指定 `~/.aevatar` 根目录。

## 安全

- 仅监听 localhost，不对外网暴露。
- API Key 仅在本地展示或掩码显示，不记录到日志。
