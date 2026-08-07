# Orbitra

高性能多仓库包缓存代理（当前 NuGet + Maven + npm + pip + docker registry），基于 ASP.NET Core Minimal API 和 AOT 编译。

## 功能特性

- **NuGet 下载代理**: 代理 `/nuget/v3/index.json` 和 `/nuget/v3-flatcontainer/` 请求
- **Maven 缓存代理**: `/maven/{**path}` 通配路由 1:1 透传 Maven Central（或自配上游），支持**多上游按序回退**
- **npm 代理**: `/npm/{**path}` 通配路由透传 npm registry（或自配上游），tarball 磁盘永久缓存、包元数据内存短 TTL 缓存
- **pip 代理**: `/pip/{**path}` 通配路由透传 PyPI Simple API（或自配镜像，PEP 503/691/658/714 对齐），simple 项目页内存短 TTL 缓存（按 Accept 变体分 key）、文件磁盘永久缓存
- **docker registry 代理（pull-through）**: `/v2/{**path}` 主路由（Docker 客户端可直接对接），manifest 分级缓存（digest 磁盘永久 + tag 内存 TTL）、blob 磁盘永久缓存，支持多上游按序回退，Docker Hub 匿名拉取开箱即用
- **磁盘缓存**: `.nupkg`/`.jar`/`.pom`/tarball/wheel/`sdist`/`blob`/`manifest` 等产物文件缓存到本地磁盘，永久保存
- **内存缓存**: NuGet `index.json` 缓存 60 分钟；`maven-metadata.xml` 缓存（快照 5 分钟 / 非快照 60 分钟）；npm 包元数据默认缓存 60 秒；pip simple 项目页默认缓存 600 秒（HTML/JSON 变体分离）；docker tag manifest 默认 60 秒、digest manifest 内存 TTL 默认 3600 秒
- **自动替换**: 自动将上游响应中的 `v3-flatcontainer` URL、npm tarball URL 与 pip 文件 URL 重写为代理域名
- **HEAD 支持**: 全部数据路由支持 `HEAD`（Content-Length 与 GET 一致，无响应体）
- **高并发**: 支持最大 5000 并发连接
- **详细日志**: 记录缓存命中、下载耗时等信息
- **AOT 编译**: 使用 .NET 10 AOT 原生编译，启动快、体积小

## 实现逻辑

```
┌─────────────────────┐     ┌─────────────────┐     ┌───────────────────────────────┐
│ NuGet/Maven/npm/    │────▶│     Orbitra     │────▶│ nuget.org / Maven Central /   │
│   pip/docker 客户端   │     │    (代理服务)     │     │  npm registry / PyPI / Docker │
└─────────────────────┘     └─────────────────┘     └───────────────────────────────┘
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
6. **`/pip/{**path}`** - PyPI Simple 代理：`/pip/simple/{name}/` 项目页内存短 TTL 缓存（按 Accept 变体区分，内嵌文件 URL 自动重写为 `{domain}/pip/files/`）、`/pip/simple/` 索引根透传、`/pip/files/{**path}` 文件磁盘永久缓存
7. **`/v2`、`/v2/{**path}`** - Docker Registry HTTP API V2 主路由（Docker 客户端可直接对接，registry-mirrors / docker pull 直连均可）；版本探测、manifest 分级缓存、blob 磁盘永久缓存、tags/list 透传

## 环境变量

| 变量             | 默认值           | 说明                                         |
|----------------|---------------|--------------------------------------------|
| `NUGET_PROXY_DOMAIN` | (必填)          | 代理服务的外部访问域名，如 `https://nuget.example.com/`。旧名 `PROXY_DOMAIN` 仍支持（已弃用，命中时输出警告日志） |
| `CACHE_PATH`   | `cache` | NuGet/Maven/npm/pip/docker 共用的磁盘缓存根目录（各仓库落在 `{CACHE_PATH}/nuget/`、`{CACHE_PATH}/maven/`、`{CACHE_PATH}/npm/`、`{CACHE_PATH}/pip/`、`{CACHE_PATH}/docker/`） |
| `MAVEN_UPSTREAM_URL` | `https://repo.maven.apache.org/maven2` | Maven 上游地址。支持**逗号分隔多值**（如 `https://maven.aliyun.com/repository/central,https://repo.maven.apache.org/maven2`），顺序即失败回退顺序（网络异常或非 2xx 自动换下一个上游）；单值行为与旧版一致。注意：URL 内不得含逗号 |
| `NPM_UPSTREAM_URL` | `https://registry.npmjs.org` | npm 上游地址（可切换国内镜像，如 `https://registry.npmmirror.com`） |
| `NPM_METADATA_TTL` | `60` | npm 包元数据内存缓存 TTL（秒），缩写与全量变体分别缓存 |
| `PIP_UPSTREAM_URL` | `https://pypi.org/simple` | pip 上游索引基址（含 `/simple`，可切换国内镜像如 `https://pypi.tuna.tsinghua.edu.cn/simple`）。单上游；**含 userinfo（`user:pass@`）时启动即抛异常**（凭据不进日志，私有源 Basic 鉴权本期不支持） |
| `PIP_SIMPLE_TTL` | `600` | pip simple 项目页内存缓存 TTL（秒），HTML 与 PEP 691 JSON 变体分别缓存 |
| `DOCKER_UPSTREAM_URL` | `https://registry-1.docker.io` | Docker registry 上游地址。支持**逗号分隔多值**，顺序即失败回退顺序（网络异常或非 2xx 自动换下一个上游）；URL 内不得含逗号 |
| `DOCKER_TAG_TTL` | `60` | docker tag manifest / tags-list 内存缓存 TTL（秒） |
| `DOCKER_MANIFEST_TTL` | `3600` | docker digest manifest 磁盘命中后的内存 TTL（秒），磁盘文件本身永久保留 |
| `DOCKER_BLOB_VERIFY` | `true` | 拉取 blob 时是否流式计算 sha256 与请求 digest 比对，不符则删除并回退下一上游 |
| `DOCKER_ENABLE_PUSH` | `false` | 是否启用 docker push 支持（v1 默认关闭，暂不支持上传链路） |

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

# pip simple 项目页（文件 URL 自动重写为 {domain}/pip/files/，内存缓存 600 秒）
http://localhost:5212/pip/simple/requests/

# pip PEP 691 JSON 变体（Accept 头协商）
curl -H "Accept: application/vnd.pypi.simple.v1+json" http://localhost:5212/pip/simple/requests/

# pip wheel 文件（首次落盘，二次命中磁盘缓存）
http://localhost:5212/pip/files/packages/a8/57/requests-2.31.0-py3-none-any.whl

# Docker 版本探测
http://localhost:5212/v2/

# Docker manifest（by-tag，内存 TTL）
http://localhost:5212/v2/library/nginx/manifests/latest

# Docker manifest（by-digest，磁盘永久缓存）
http://localhost:5212/v2/library/nginx/manifests/sha256:...

# Docker blob（磁盘永久缓存）
http://localhost:5212/v2/library/nginx/blobs/sha256:...

# Docker tags/list（内存短 TTL）
http://localhost:5212/v2/library/nginx/tags/list

# HEAD（Content-Length 与 GET 一致，无响应体）
curl -sI http://localhost:5212/nuget/v3/index.json
curl -sI http://localhost:5212/npm/express
curl -sI http://localhost:5212/v2/library/nginx/manifests/latest
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

## 配置 pip / uv 客户端

### pip（`pip.config` 或环境变量）

```bash
# 方式一：全局配置（推荐）
pip config set global.index-url https://nuget-cdn.example.com/pip/simple/

# 方式二：环境变量（CI / 容器）
export PIP_INDEX_URL=https://nuget-cdn.example.com/pip/simple/

# 验证安装（二次安装全部缓存命中，不再请求上游）
pip install requests
```

### uv

```bash
# 单次命令
uv add requests --index-url https://nuget-cdn.example.com/pip/simple/

# 或环境变量
export UV_INDEX_URL=https://nuget-cdn.example.com/pip/simple/
```

> **HTTP 部署注意**：pip 对 http 源默认拒绝，代理走 HTTP（无 TLS）时需加 `--trusted-host`（如 `pip install --trusted-host nuget-cdn.example.com requests`）；生产建议启用 TLS 后使用 https 地址，无需该参数。

### 说明

- 项目名按 **PEP 503** 规范化（`Django` 与 `django` 命中同一缓存），文件 URL 重写仅针对「配置上游主机 + 伴生文件主机（pypi.org → files.pythonhosted.org）」的绝对地址，其余主机 URL 原样保留（客户端直连上游文件主机，功能不破坏）
- simple 项目页仅内存短 TTL 缓存（`PIP_SIMPLE_TTL`，不落盘），发布新版本后 TTL 内即可见；文件（wheel/sdist/`.metadata`）磁盘永久缓存
- 上游响应中的 `#sha256=` 片段原样保留，哈希校验由 pip 客户端自身完成（代理无感知，见「Docker 鉴权说明」同思路的客户端侧职责划分）

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

## 配置 Docker 客户端

### 方式一：daemon.json `registry-mirrors`（推荐，镜像加速）

修改 `/etc/docker/daemon.json`（或 macOS/Linux 对应位置），将 Orbitra 配置为 registry mirror：

```json
{
  "registry-mirrors": ["http://localhost:18680"]
}
```

配置后 `docker pull nginx` 会优先经过 Orbitra 代理拉取；若代理走 HTTP 且未配置 TLS，需在 `daemon.json` 同时加入 `"insecure-registries": ["localhost:18680"]`。

### 方式二：containerd `hosts.toml`

在 `/etc/containerd/certs.d/docker.io/hosts.toml` 中配置：

```toml
[host."http://localhost:18680"]
  capabilities = ["pull", "resolve"]
```

### 方式三：直接作为 registry（docker login + 拉取）

```bash
# 登录（匿名拉取无需登录；登录仅用于私有仓库场景）
docker login localhost:18680

# 直接拉取
docker pull localhost:18680/library/nginx
```

**镜像名说明**：Docker Hub 的标准镜像名（如 `nginx`）会由客户端自动规范为 `library/nginx` 再请求代理，`registry-mirrors` 与 containerd 方式均开箱即用；v1 暂不支持「镜像名带代理域名前缀」（如 `docker pull myproxy.example/nginx`）这种把代理当作独立 registry 使用的形态，请使用上面的 `registry-mirrors` / `hosts.toml` 方式。

## Docker 鉴权说明

代理内部自动完成上游 token 交换：上游返回 `401 + WWW-Authenticate: Bearer realm/service/scope` 时，代理按 `?service&scope` 向 realm 换取 Bearer token（未配置凭据则匿名换取，Docker Hub 公共镜像走此流程），缓存 token 后带 `Authorization: Bearer` 重试。**上游的 `WWW-Authenticate` 质询不会透传给客户端**；客户端无需任何配置即可匿名拉取 Docker Hub 公共镜像。

## Docker 缓存策略

| 对象 | 缓存策略 | 说明 |
|---------|---------|------|
| manifest（by-digest） | 磁盘永久 + 内存 TTL | 落盘 `{CACHE_PATH}/docker/manifests/sha256/{hex[:2]}/{hex}.json` + `.meta` sidecar（记录上游 Content-Type），磁盘命中时回放精确 media type；内存 TTL `DOCKER_MANIFEST_TTL`（默认 3600 秒） |
| manifest（by-tag） | 仅内存 TTL | tag 可变不落盘；TTL `DOCKER_TAG_TTL`（默认 60 秒），返回时回填 `Docker-Content-Digest` |
| tags/list | 内存短 TTL | 同 `DOCKER_TAG_TTL`（默认 60 秒） |
| blob | 磁盘永久 | 内容寻址天然不可变；落盘 `{CACHE_PATH}/docker/blobs/sha256/{hex[:2]}/{hex}`，复用共享磁盘缓存链路（下载→流式落盘→原子 rename），`DOCKER_BLOB_VERIFY=true` 时边写边算 sha256 校验 |

## Docker 已知限制（v1）

- 无 blob 磁盘淘汰策略：镜像磁盘膨胀依赖运维层卷容量管理（与 NuGet/Maven/npm 一致的永久缓存模型）
- 不支持 push：`DOCKER_ENABLE_PUSH` 默认 `false`，上传链路（PUT manifest / POST-PATCH-PUT blob）不在 v1 范围

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
├── npm/                                # npm 独立子目录
│   └── express/
│       └── -/
│           └── express-4.19.2.tgz
├── pip/                                # pip 独立子目录
│   └── files/                          # wheel / sdist / .metadata 文件（路径与文件主机 URL 一致）
│       └── packages/
│           └── a8/
│               └── 57/
│                   └── requests-2.31.0-py3-none-any.whl
└── docker/                             # Docker registry 独立子目录
    ├── blobs/                          # blob 按算法 + digest 前两位分片，避免目录爆炸
    │   └── sha256/
    │       └── 3b/                     # {hex[:2]} 256 个子目录
    │           └── 3b2e...8f2a         # 完整 hex 摘要作为文件名（无扩展名）
    └── manifests/                      # digest manifest
        └── sha256/
            └── 3b/
                ├── 3b2e...8f2a.json    # digest manifest body
                └── 3b2e...8f2a.json.meta  # Content-Type sidecar
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

## pip 缓存策略

| 文件类型 | 缓存策略 | 说明 |
|---------|---------|------|
| simple 项目页（`/simple/{name}/`） | 内存短 TTL 缓存 | 默认 600 秒（`PIP_SIMPLE_TTL` 可配），key 按 Accept 变体区分（HTML / PEP 691 JSON），仅成功响应缓存、不落盘；项目名 PEP 503 规范化后参与 key 与上游请求 |
| 文件（`/files/{**path}`，wheel/sdist/`.metadata`） | 磁盘永久缓存 | 落盘 `{CACHE_PATH}/pip/files/{path}`，复用共享磁盘缓存服务（下载→流式落盘→原子 rename），上游 URL 由文件主机基址拼接（pypi.org → `https://files.pythonhosted.org`，镜像与页面同主机） |
| 索引根（`/simple/`） | 不缓存 | 全量项目列表体量大且客户端不依赖，兜底透传 |

- pip 单上游（v1），`PIP_UPSTREAM_URL` 多镜像回退见 v2 规划；上游含 userinfo 启动即抛异常（凭据不进日志）
- 代理侧不做文件哈希校验：simple 页 `#sha256=` 位于 URL 片段，HTTP 请求不携带，由 pip 客户端按索引页哈希自行校验（不符直接失败）
- `HEAD` 与 `GET` 一致：项目页显式设置 Content-Length；文件磁盘命中走 SendFile 零拷贝

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
