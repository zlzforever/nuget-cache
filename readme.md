# NuGet Cache

高性能 NuGet 包缓存代理服务，基于 ASP.NET Core Minimal API 和 AOT 编译。

## 功能特性

- **包下载代理**: 代理 `/v3/index.json` 和 `/v3-flatcontainer/` 请求
- **磁盘缓存**: `.nupkg` 文件缓存到本地磁盘，永久保存
- **内存缓存**: `index.json` 缓存 60 分钟
- **自动替换**: 自动将上游响应中的 `v3-flatcontainer` URL 替换为代理域名
- **高并发**: 支持最大 2000 并发连接
- **详细日志**: 记录缓存命中、下载耗时等信息
- **AOT 编译**: 使用 .NET 10 AOT 原生编译，启动快、体积小

## 实现逻辑

```
┌─────────────┐     ┌─────────────────┐     ┌─────────────┐
│   NuGet     │────▶│   nuget-cache   │────▶│  nuget.org  │
│   Client    │     │   (代理服务)     │     │   (上游)    │
└─────────────┘     └─────────────────┘     └─────────────┘
                            │
                    ┌───────┴───────┐
                    ▼               ▼
              ┌──────────┐   ┌──────────┐
              │ 内存缓存  │   │ 磁盘缓存  │
              │(60分钟)   │   │ (永久)    │
              └──────────┘   └──────────┘
```

1. **`/v3/index.json`** - 从上游获取并替换所有 `v3-flatcontainer` URL，内存缓存 60 分钟
2. **`/v3-flatcontainer/{id}/index.json`** - 包版本索引，内存缓存 60 分钟
3. **`/v3-flatcontainer/{id}/{version}/{file}`** - 包文件下载，磁盘永久缓存

## 环境变量

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `PROXY_DOMAIN` | (必填) | 代理服务的外部访问域名，如 `https://nuget.example.com/` |
| `CACHE_PATH` | `nuget-cache` | 磁盘缓存目录 |

## 构建与运行

### Docker 构建

```bash
docker build -t zlzforever/nuget-cache:latest .
```

### Docker 运行

```bash
docker run -d \
  --name nuget-cache \
  --restart always \
  -p 18680:8080 \
  -v /data/nuget-cache:/app/nuget-cache \
  -e PROXY_DOMAIN=https://nuget-cdn.example.com \
  zlzforever/nuget-cache:latest
```

### Docker Compose

```yaml
version: '3.8'
services:
  nuget-cache:
    image: nuget-cache:latest
    restart: always
    ports:
      - "18680:8080"
    volumes:
      - /data/nuget-cache:/app/nuget-cache
    environment:
      - PROXY_DOMAIN=https://nuget-cdn.example.com
```

## 配置 NuGet 客户端

### 添加包源

```bash
dotnet nuget add source https://nuget-cdn.example.com/v3/index.json \
  --name nuget-cache
```

### 或修改 `nuget.config`

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget-cache" value="https://nuget-cdn.example.com/v3/index.json" />
  </packageSources>
</configuration>
```

## 缓存目录结构

```
nuget-cache/
├── newtonsoft.json/
│   └── 13.0.3/
│       ├── newtonsoft.json.13.0.3.nupkg
│       └── newtonsoft.json.nuspec
└── microsoft.extensions.logging/
    └── 8.0.0/
        └── microsoft.extensions.logging.8.0.0.nupkg
```

## 性能配置

### Kestrel 服务器

| 配置 | 值 | 说明 |
|------|-----|------|
| `MaxConcurrentConnections` | 2000 | 最大并发 TCP 连接 |
| `MaxConcurrentUpgradedConnections` | 500 | 最大升级连接 (WebSocket) |
| `KeepAliveTimeout` | 2 分钟 | 长连接保活超时 |
| `RequestHeadersTimeout` | 30 秒 | 请求头超时 |

### HttpClient 连接池

| 配置 | 值 | 说明 |
|------|-----|------|
| `Timeout` | 110 秒 | 请求总超时 |
| `ConnectTimeout` | 30 秒 | 连接建立超时 |
| `MaxConnectionsPerServer` | 100 | 每服务器最大连接数 |
| `PooledConnectionLifetime` | 5 分钟 | 连接池存活时间 |
| `PooledConnectionIdleTimeout` | 1 分钟 | 空闲连接超时 |

## 日志示例

```
info: GET /v3/index.json
info: Package cache hit: newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg, Size: 654321 bytes
info: Download success (2345ms): /app/nuget-cache/serilog/2.10.0/serilog.2.10.0.nupkg, Size: 78901 bytes
warn: Download failed (30000ms): 503 - https://api.nuget.org/v3-flatcontainer/...
```

## 技术栈

- .NET 10.0
- ASP.NET Core Minimal API
- AOT (Ahead-of-Time) 原生编译
- SocketsHttpHandler 连接池
- IMemoryCache 内存缓存

## License

MIT
