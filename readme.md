# Orbitra

高性能多仓库包缓存代理（当前 NuGet + Maven + npm，规划 docker/pip），基于 ASP.NET Core Minimal API 和 AOT 编译。

## 功能特性

- **NuGet 下载代理**: 代理 `/nuget/v3/index.json` 和 `/nuget/v3-flatcontainer/` 请求
- **Maven 缓存代理**: `/maven/{**path}` 通配路由 1:1 透传 Maven Central（或自配上游），支持**多上游按序回退**
- **npm 代理**: `/npm/{**path}` 通配路由透传 npm registry（或自配上游），tarball 磁盘永久缓存、包元数据内存短 TTL 缓存
- **磁盘缓存**: `.nupkg`/`.jar`/`.pom`/tarball 等产物文件缓存到本地磁盘，永久保存
- **内存缓存**: NuGet `index.json` 缓存 60 分钟；`maven-metadata.xml` 缓存（快照 5 分钟 / 非快照 60 分钟）；npm 包元数据默认缓存 60 秒
- **自动替换**: 自动将上游响应中的 `v3-flatcontainer` URL 与 npm tarball URL 重写为代理域名
- **HEAD 支持**: 全部数据路由支持 `HEAD`（Content-Length 与 GET 一致，无响应体）
- **高并发**: 支持最大 5000 并发连接
- **详细日志**: 记录缓存命中、下载耗时等信息
- **AOT 编译**: 使用 .NET 10 AOT 原生编译，启动快、体积小

## 实现逻辑

```
┌──────────────────┐     ┌─────────────────┐     ┌────────────────────────────┐
│ NuGet/Maven/npm  │────▶│     Orbitra     │────▶│  nuget.org / Maven Central │
│     Client       │     │    (代理服务)     │     │      / npm registry       │
└──────────────────┘     └─────────────────┘     └────────────────────────────┘
                                │
                        ┌───────┴───────┐
                        ▼               ▼
                  ┌──────────┐   ┌──────────┐
                  │ 内存缓存  │   │ 磁盘缓存  │
                  │(短 TTL)   │   │ (永久)    │
                  └──────────┘   └──────────┘
```

1. **`/nuget/v3/index.json`** - 从上游获取并替换所有 `v3-flatcontainer` URL，内存缓存 60 分钟
2. **`/nuget/v3-flatcontainer/{id}/index.json`** - 包版本索引，内存缓存 60 分钟
3. **`/nuget/v3-flatcontainer/{id}/{version}/{file}`** - 包文件下载，磁盘永久缓存
4. **`/maven/{**path}`** - Maven 上游代理，产物磁盘永久缓存，元数据内存缓存
5. **`/npm/{**path}`** - npm 上游代理，tarball 磁盘永久缓存，包元数据内存短 TTL 缓存（按 Accept 变体区分）

## 环境变量

| 变量             | 默认值           | 说明                                         |
|----------------|---------------|--------------------------------------------|
| `NUGET_PROXY_DOMAIN` | (必填)          | 代理服务的外部访问域名，如 `https://nuget.example.com/`。旧名 `PROXY_DOMAIN` 仍支持（已弃用，命中时输出警告日志） |
| `CACHE_PATH`   | `cache` | NuGet/Maven/npm 共用的磁盘缓存根目录（各仓库落在 `{CACHE_PATH}/nuget/`、`{CACHE_PATH}/maven/`、`{CACHE_PATH}/npm/`） |
| `MAVEN_UPSTREAM_URL` | `https://repo.maven.apache.org/maven2` | Maven 上游地址。支持**逗号分隔多值**（如 `https://maven.aliyun.com/repository/central,https://repo.maven.apache.org/maven2`），顺序即失败回退顺序（网络异常或非 2xx 自动换下一个上游）；单值行为与旧版一致。注意：URL 内不得含逗号 |
| `NPM_UPSTREAM_URL` | `https://registry.npmjs.org` | npm 上游地址（可切换国内镜像，如 `https://registry.npmmirror.com`） |
| `NPM_METADATA_TTL` | `60` | npm 包元数据内存缓存 TTL（秒），缩写与全量变体分别缓存 |

## 构建与运行

### Docker 构建

```bash
docker build -t zlzforever/orbitra:latest .
```

### Docker 运行

```bash
docker run -d \
  --name orbitra \
  --restart always \
  -p 18680:8080 \
  -v /data/orbitra-cache:/app/cache \
  -e NUGET_PROXY_DOMAIN=https://nuget-cdn.example.com \
  zlzforever/orbitra:latest
```

### Docker Compose

```yaml
version: '3.8'
services:
  orbitra:
    image: zlzforever/orbitra:latest
    restart: always
    ports:
      - "18680:8080"
    volumes:
      - /data/orbitra-cache:/app/cache
    environment:
      - NUGET_PROXY_DOMAIN=https://nuget-cdn.example.com
```

### 测试

```
# NuGet
http://localhost:5212/nuget/v3-flatcontainer/junittestlogger/1.1.0/junittestlogger.1.1.0.nupkg

# Maven（首次拉取落盘，二次命中磁盘缓存）
http://localhost:5212/maven/org/springframework/spring-core/6.1.0/spring-core-6.1.0.jar

# Maven 元数据（内存缓存 60 分钟）
http://localhost:5212/maven/org/springframework/spring-core/maven-metadata.xml

# npm 元数据（tarball URL 自动重写为 {domain}/npm/）
http://localhost:5212/npm/express

# npm tarball（首次落盘，二次命中磁盘缓存）
http://localhost:5212/npm/express/-/express-4.19.2.tgz

# HEAD（Content-Length 与 GET 一致，无响应体）
curl -sI http://localhost:5212/nuget/v3/index.json
curl -sI http://localhost:5212/npm/express
```

## 配置 NuGet 客户端

### 添加包源

```bash
dotnet nuget add source https://nuget-cdn.example.com/nuget/v3/index.json \
  --name orbitra
```

### 或修改 `nuget.config`

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
    <packageSources>
        <clear/>
        <add key="orbitra" value="https://nuget-cdn.example.com/nuget/v3/index.json"/>
    </packageSources>
</configuration>
```

## 配置 npm 客户端

### `.npmrc`（registry 指向代理）

```ini
registry=https://nuget-cdn.example.com/npm/
```

### scope 包说明

`@scope/name` 形式的 scoped 包同样经 `/npm/` 前缀代理。客户端既可请求 `/npm/@scope/name`
也可请求 `/npm/@scope%2fname`（编码形式），代理会保持编码一致地拼接上游与落盘路径，
且元数据中内嵌的 tarball URL（如 `https://registry.npmjs.org/@scope%2fname/-/name-1.0.0.tgz`）
会原样重写为 `{domain}/npm/@scope%2fname/-/name-1.0.0.tgz`，保证客户端可回源下载。

## 配置 Maven 客户端

### 方式一：`settings.xml` mirror（推荐，全局拦截）

在 `~/.m2/settings.xml` 中配置 mirror，将 Maven Central 的请求全部转发到本代理：

```xml
<?xml version="1.0" encoding="UTF-8"?>
<settings xmlns="http://maven.apache.org/SETTINGS/1.0.0">
    <mirrors>
        <mirror>
            <id>orbitra-maven</id>
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
        <id>orbitra-maven</id>
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
cache/                                  # {CACHE_PATH} 默认根目录
├── nuget/                              # NuGet 独立子目录（id/version 小写）
│   └── newtonsoft.json/
│       └── 13.0.3/
│           ├── newtonsoft.json.13.0.3.nupkg
│           └── newtonsoft.json.nuspec
├── maven/                              # Maven 独立子目录，坐标大小写保留
│   └── org/springframework/spring-core/
│       └── 6.1.0/
│           ├── spring-core-6.1.0.pom
│           ├── spring-core-6.1.0.jar
│           ├── spring-core-6.1.0.jar.sha1
│           └── ...
└── npm/                                # npm 独立子目录
    └── express/
        └── -/
            └── express-4.19.2.tgz
```

> **旧缓存懒迁移**：老版本 NuGet 包文件落在 `{CACHE_PATH}/{id}/{version}/`（无 `nuget/` 子目录）。
> 升级后请求新路径 `{CACHE_PATH}/nuget/{id}/{version}/` 未命中时，会自动回查旧路径并将文件
> 原子搬移到新路径（并发请求下目标已存在则忽略，由先完成的搬移生效）。

## npm 缓存策略

| 文件类型 | 缓存策略 | 说明 |
|---------|---------|------|
| tarball（路径含 `/-/` 或以 `.tgz` 结尾） | 磁盘永久缓存 | 落盘到 `{CACHE_PATH}/npm/{path}`，复用共享磁盘缓存服务 |
| 包元数据（`/{pkg}`、`/{pkg}/{version}`，含 scope 包） | 内存短 TTL 缓存 | 默认 60 秒（`NPM_METADATA_TTL` 可配），key 按 Accept 变体区分（缩写 `install-v1+json` vs 全量） |
| `/-/ping`、`/-/v1/search` 等内部端点 | 不缓存 | 兜底透传上游响应 |

## Maven 缓存策略

| 文件类型 | 缓存策略 | 说明 |
|---------|---------|------|
| `.jar` `.pom` `.aar` `.war` `.zip` 及校验和文件 | 磁盘永久缓存 | 落盘到 `{CACHE_PATH}/maven/{**path}`，二次请求命中磁盘 |
| `maven-metadata.xml`（非快照） | 内存缓存 60 分钟 | 仅成功响应缓存，不写盘，key 为 `maven:metadata:{path}` |
| `maven-metadata.xml`（含 `-SNAPSHOT` 段） | 内存缓存 5 分钟 | 快照版本变化频繁，缩短 TTL |

- Maven 路径**大小写敏感**（坐标区分大小写），原样保留大小写代理与缓存
- 多上游按配置顺序回退：当前上游网络异常或返回非 2xx 时自动尝试下一个；全部失败返回最后一个非 2xx 状态码（全为网络异常返回 502），磁盘写失败返回 503（本地故障，不换源）
- Maven 响应体原样透传，无需 URL 重写（pom/metadata 仅含坐标，不含绝对 URL）

## 性能配置

### Kestrel 服务器

| 配置                                 | 值    | 说明                 |
|------------------------------------|------|--------------------|
| `MaxConcurrentConnections`         | 5000 | 最大并发 TCP 连接        |
| `MaxConcurrentUpgradedConnections` | 500  | 最大升级连接 (WebSocket) |
| `KeepAliveTimeout`                 | 2 分钟 | 长连接保活超时            |
| `RequestHeadersTimeout`            | 30 秒 | 请求头超时              |

### HttpClient 连接池

| 配置                            | 值     | 说明        |
|-------------------------------|-------|-----------|
| `Timeout`                     | 120 秒 | 请求总超时     |
| `ConnectTimeout`              | 30 秒  | 连接建立超时    |
| `MaxConnectionsPerServer`     | 1000  | 每服务器最大连接数 |
| `PooledConnectionLifetime`    | 5 分钟  | 连接池存活时间   |
| `PooledConnectionIdleTimeout` | 1 分钟  | 空闲连接超时    |

## 日志示例

```
GET /nuget/v3/index.json
info: NuGet cache lazy migrated: /app/cache/newtonsoft.json/13.0.3/... -> /app/cache/nuget/newtonsoft.json/13.0.3/...
info: Cache hit: /app/cache/nuget/serilog/2.10.0/serilog.2.10.0.nupkg
info: Download success: /app/cache/npm/express/-/express-4.19.2.tgz, Size: 78901 bytes
warn: Download failed: 503 - https://api.nuget.org/v3-flatcontainer/...
```

## 技术栈

- .NET 10.0
- ASP.NET Core Minimal API
- AOT (Ahead-of-Time) 原生编译
- SocketsHttpHandler 连接池
- IMemoryCache 内存缓存

## License

MIT
