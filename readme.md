# NuGet Cache

高性能 NuGet + Maven 包缓存代理服务，基于 ASP.NET Core Minimal API 和 AOT 编译。

## 功能特性

- **包下载代理**: 代理 `/v3/index.json` 和 `/v3-flatcontainer/` 请求
- **磁盘缓存**: `.nupkg` 文件缓存到本地磁盘，永久保存
- **内存缓存**: `index.json` 缓存 60 分钟
- **自动替换**: 自动将上游响应中的 `v3-flatcontainer` URL 替换为代理域名
- **Maven 缓存代理**: `/maven/{**path}` 通配路由 1:1 透传 Maven Central（或自配上游）
- **Maven 磁盘永久缓存**: `.jar` `.pom` `.aar` `.war` `.zip` 及校验和文件永久落盘
- **Maven 元数据内存缓存**: `maven-metadata.xml` 内存缓存（快照 5 分钟 / 非快照 60 分钟）
- **高并发**: 支持最大 2000 并发连接
- **详细日志**: 记录缓存命中、下载耗时等信息
- **AOT 编译**: 使用 .NET 10 AOT 原生编译，启动快、体积小

## 实现逻辑

```
┌─────────────┐     ┌─────────────────┐     ┌──────────────────┐
│ NuGet/Maven │────▶│   nuget-cache   │────▶│  nuget.org /     │
│   Client    │     │   (代理服务)     │     │  Maven Central   │
└─────────────┘     └─────────────────┘     └──────────────────┘
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
4. **`/maven/{**path}`** - Maven 上游代理，产物磁盘永久缓存，元数据内存缓存

## 环境变量

| 变量             | 默认值           | 说明                                         |
|----------------|---------------|--------------------------------------------|
| `NUGET_PROXY_DOMAIN` | (必填)          | 代理服务的外部访问域名，如 `https://nuget.example.com/`。旧名 `PROXY_DOMAIN` 仍支持（已弃用，命中时输出警告日志） |
| `CACHE_PATH`   | `nuget-cache` | NuGet/Maven 共用的磁盘缓存根目录（Maven 落在 `{CACHE_PATH}/maven/`） |
| `MAVEN_UPSTREAM_URL` | `https://repo.maven.apache.org/maven2` | Maven 上游地址（可切换国内镜像，如 `https://maven.aliyun.com/repository/central`） |

## 构建与运行

### Docker 构建

```bash
docker build -t gitea.ptkj.cc/public/zlzforever/nuget-cache:202600416.1 .
```

### Docker 运行

```bash
docker run -d \
  --name nuget-cache \
  --restart always \
  -p 18680:8080 \
  -v /data/nuget-cache:/app/nuget-cache \
  -e NUGET_PROXY_DOMAIN=https://nuget-cdn.example.com \
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
      - NUGET_PROXY_DOMAIN=https://nuget-cdn.example.com
```

### 测试

```
# NuGet
http://localhost:5212/v3-flatcontainer/junittestlogger/1.1.0/junittestlogger.1.1.0.nupkg

# Maven（首次拉取落盘，二次命中磁盘缓存）
http://localhost:5212/maven/org/springframework/spring-core/6.1.0/spring-core-6.1.0.jar

# Maven 元数据（内存缓存 60 分钟）
http://localhost:5212/maven/org/springframework/spring-core/maven-metadata.xml
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
        <clear/>
        <add key="nuget-cache" value="https://nuget-cdn.example.com/v3/index.json"/>
    </packageSources>
</configuration>
```

## 配置 Maven 客户端

### 方式一：`settings.xml` mirror（推荐，全局拦截）

在 `~/.m2/settings.xml` 中配置 mirror，将 Maven Central 的请求全部转发到本代理：

```xml
<?xml version="1.0" encoding="UTF-8"?>
<settings xmlns="http://maven.apache.org/SETTINGS/1.0.0">
    <mirrors>
        <mirror>
            <id>nuget-cache-maven</id>
            <mirrorOf>central</mirrorOf>
            <url>https://nuget-cdn.example.com/maven/</url>
        </mirror>
    </mirrors>
</settings>
```

### 方式二：项目 `pom.xml` 配置 repository

```xml
<repositories>
    <repository>
        <id>nuget-cache-maven</id>
        <url>https://nuget-cdn.example.com/maven/</url>
        <releases><enabled>true</enabled></releases>
        <snapshots><enabled>true</enabled></snapshots>
    </repository>
</repositories>
```

### Gradle 示例

在 `build.gradle` 中配置：

```groovy
repositories {
    maven {
        url = uri("https://nuget-cdn.example.com/maven/")
        allowInsecureProtocol = false
    }
}
```

或使用 Gradle 初始化脚本 `~/.gradle/init.gradle` 全局替换：

```groovy
allprojects {
    repositories {
        maven {
            url = uri("https://nuget-cdn.example.com/maven/")
        }
    }
}
```

## 缓存目录结构

```
nuget-cache/
├── newtonsoft.json/
│   └── 13.0.3/
│       ├── newtonsoft.json.13.0.3.nupkg
│       └── newtonsoft.json.nuspec
├── microsoft.extensions.logging/
│   └── 8.0.0/
│       └── microsoft.extensions.logging.8.0.0.nupkg
└── maven/                               # Maven 独立子目录，与 NuGet 天然隔离
    └── org/springframework/spring-core/
        └── 6.1.0/
            ├── spring-core-6.1.0.pom
            ├── spring-core-6.1.0.jar
            ├── spring-core-6.1.0.jar.sha1
            └── ...
```

## Maven 缓存策略

| 文件类型 | 缓存策略 | 说明 |
|---------|---------|------|
| `.jar` `.pom` `.aar` `.war` `.zip` 及校验和文件 | 磁盘永久缓存 | 落盘到 `{CACHE_PATH}/maven/{**path}`，二次请求命中磁盘 |
| `maven-metadata.xml`（非快照） | 内存缓存 60 分钟 | 仅成功响应缓存，不写盘，key 为 `maven:metadata:{path}` |
| `maven-metadata.xml`（含 `-SNAPSHOT` 段） | 内存缓存 5 分钟 | 快照版本变化频繁，缩短 TTL |

- Maven 路径**大小写敏感**（坐标区分大小写），原样保留大小写代理与缓存
- 上游 4xx/5xx 透传状态码，不落盘不缓存
- Maven 响应体原样透传，无需 URL 重写（pom/metadata 仅含坐标，不含绝对 URL）

## 性能配置

### Kestrel 服务器

| 配置                                 | 值    | 说明                 |
|------------------------------------|------|--------------------|
| `MaxConcurrentConnections`         | 2000 | 最大并发 TCP 连接        |
| `MaxConcurrentUpgradedConnections` | 500  | 最大升级连接 (WebSocket) |
| `KeepAliveTimeout`                 | 2 分钟 | 长连接保活超时            |
| `RequestHeadersTimeout`            | 30 秒 | 请求头超时              |

### HttpClient 连接池

| 配置                            | 值     | 说明        |
|-------------------------------|-------|-----------|
| `Timeout`                     | 110 秒 | 请求总超时     |
| `ConnectTimeout`              | 30 秒  | 连接建立超时    |
| `MaxConnectionsPerServer`     | 100   | 每服务器最大连接数 |
| `PooledConnectionLifetime`    | 5 分钟  | 连接池存活时间   |
| `PooledConnectionIdleTimeout` | 1 分钟  | 空闲连接超时    |

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
