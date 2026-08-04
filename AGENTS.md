# AGENTS.md - Orbitra Project

Guidelines for AI coding agents working in this repository.

## Project Overview

A high-performance multi-repo package caching proxy service (NuGet + Maven + npm + docker registry) built with:
- **Framework**: .NET 10.0 ASP.NET Core
- **API Style**: Minimal APIs (not MVC/controllers)
- **Compilation**: AOT (Ahead-of-Time) native compilation enabled
- **Architecture**: Composition root (`Program.cs`) + layered `Configuration/` `Services/` `Handlers/`

---

## Build Commands

```bash
dotnet restore                    # Restore dependencies
dotnet build                      # Build (Debug)
dotnet build -c Release           # Build (Release)
dotnet publish -c Release         # Publish AOT native binary
docker build -t orbitra:latest .  # Docker build

# Run locally (requires NUGET_PROXY_DOMAIN env var; PROXY_DOMAIN still works as deprecated fallback)
NUGET_PROXY_DOMAIN=https://your-domain.com/ dotnet run --project src/Orbitra/Orbitra.csproj
```

## Test Commands

**Note: This project currently has no test suite.** When adding tests:

```bash
dotnet test                                    # Run all tests
dotnet test --filter "FullyQualifiedName~ClassName"        # Run specific class
dotnet test --filter "FullyQualifiedName~ClassName.Method" # Run specific method
dotnet test -v detailed                        # Verbose output
```

## Lint/Format Commands

```bash
dotnet format                   # Format code
dotnet format --verify-no-changes  # CI verification
dotnet build -warnaserror       # Warnings as errors
```

---

## Code Style Guidelines

### Imports

```csharp
// System imports first
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

// Then Microsoft/Framework namespaces
using Microsoft.Extensions.Caching.Memory;
```

### Formatting

- **Indentation**: 4 spaces (no tabs)
- **Braces**: Allman for types, same-line for control flow
- **Line length**: Keep under 120 characters

### Types & Nullability

- **Nullable reference types**: ENABLED - respect nullability annotations
- **Implicit usings**: ENABLED
- Prefer explicit types over `var` when type isn't obvious
- Never suppress null warnings with `!` without justification

### Naming Conventions

| Element | Convention | Example |
|---------|------------|---------|
| Parameters | camelCase | `id`, `cacheKey` |
| Local variables | camelCase | `idLower`, `targetUrl` |
| Constants | PascalCase | `MaxConnections` |
| Private fields | `_camelCase` | `_logger` |

### Async/Await

- Always use `async`/`await` for I/O operations
- Never use `.Result` or `.Wait()` - causes deadlocks

### Error Handling

- Use `Results.StatusCode()` for HTTP error responses
- Log errors before returning

```csharp
if (!response.IsSuccessStatusCode)
{
    logger.LogWarning("Request failed: {StatusCode} - {Url}", (int)response.StatusCode, url);
    return Results.StatusCode((int)response.StatusCode);
}
```

### Logging

Use structured logging with template placeholders:

```csharp
// Good
logger.LogInformation("Cache hit: {Id}/{Version}", id, version);

// Bad
logger.LogInformation($"Cache hit: {id}/{version}");
```

### HTTP Client Usage

Always use `IHttpClientFactory` - never create `HttpClient` directly:

```csharp
var httpClient = http.CreateClient("NuGet");
using var response = await httpClient.GetAsync(url);
```

---

## Project-Specific Notes

### AOT Compatibility

- Avoid reflection-heavy patterns
- Avoid dynamic code generation

### Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `NUGET_PROXY_DOMAIN` | Yes | External URL (e.g., `https://nuget.example.com/`). Deprecated fallback: `PROXY_DOMAIN` (with warning log) |
| `CACHE_PATH` | No | Disk cache root directory shared by NuGet/Maven/npm/docker (default: `cache`; repos isolated under `nuget/` `maven/` `npm/` `docker/`) |
| `MAVEN_UPSTREAM_URL` | No | Maven upstream base URL (default: Maven Central) |
| `NPM_UPSTREAM_URL` | No | npm upstream base URL (default: `https://registry.npmjs.org`) |
| `NPM_METADATA_TTL` | No | npm package metadata in-memory TTL in seconds (default: `60`; abbreviated/full variants cached separately) |
| `DOCKER_UPSTREAM_URL` | No | Docker registry upstream URL(s). Comma-separated multi-value supported (default: `https://registry-1.docker.io`); order is the failover order (network error / non-2xx falls through to next upstream); URL must not contain a comma |
| `DOCKER_TAG_TTL` | No | docker tag manifest / tags-list in-memory TTL in seconds (default: `60`) |
| `DOCKER_MANIFEST_TTL` | No | docker digest-manifest in-memory TTL in seconds after disk hit (default: `3600`; disk file itself permanent) |
| `DOCKER_BLOB_VERIFY` | No | Verify blob sha256 against request digest while streaming to disk (default: `true`); on mismatch delete and fall through to next upstream |
| `DOCKER_ENABLE_PUSH` | No | Enable docker push support (default: `false`; v1 does not implement the upload chain) |

### Caching Strategy

- **Memory cache**: NuGet `index.json` 60-minute TTL; Maven `maven-metadata.xml` (snapshot 5 min / release 60 min); npm package metadata short TTL (`NPM_METADATA_TTL`, default 60s); docker tag manifest / tags-list short TTL (`DOCKER_TAG_TTL`, default 60s), docker digest manifest in-memory TTL (`DOCKER_MANIFEST_TTL`, default 3600s)
- **Disk cache**: Permanent for `.nupkg` files, Maven artifacts, npm tarballs, docker blobs and by-digest manifests (`{CACHE_PATH}/nuget/`, `{CACHE_PATH}/maven/`, `{CACHE_PATH}/npm/`, `{CACHE_PATH}/docker/`)

### API Endpoints (all support GET + HEAD)

1. `GET|HEAD /nuget/v3/index.json` - NuGet service index (proxied + URL rewrite to `{domain}/nuget/v3-flatcontainer/`)
2. `GET|HEAD /nuget/v3-flatcontainer/{id}/index.json` - Package version list
3. `GET|HEAD /nuget/v3-flatcontainer/{id}/{version}/{file}` - Package file download (disk cache + legacy-path lazy migration)
4. `GET|HEAD /maven/{**path}` - Maven proxy (artifacts disk cache, metadata memory cache)
5. `GET|HEAD /npm/{**path}` - npm proxy (tarball disk cache, metadata memory cache, tarball URL rewrite)
6. `GET|HEAD /v2/` - Docker Registry version probe (passthrough upstream `/v2/`, returns `{}`; not cached)
7. `GET|HEAD /v2/{name}/manifests/{reference}` - Docker manifest: by-digest → disk permanent + memory TTL; by-tag → memory TTL only (`Docker-Content-Digest` backfilled)
8. `GET|HEAD /v2/{name}/blobs/{digest}` - Docker blob, disk permanent cache (streaming sha256 verify if `DOCKER_BLOB_VERIFY`)
9. `GET|HEAD /v2/{name}/tags/list` - Docker tag list, memory short TTL
10. `GET|HEAD /` - Health check
11. `GET|HEAD /*` (fallback) - Returns 404 with logging

### Docker 代理注意事项

- **Content-Type 逐字节透传**: manifest 的 media type（OCI 与 Docker 两种 schema）必须按上游原值透传/回放，禁止统一归一化；磁盘命中的 digest manifest 从 `.meta` sidecar 读回精确 Content-Type，否则 docker 客户端按 Accept 校验会拒绝
- **digest 严格正则校验**: manifest/blob 的 digest 引用用专用正则校验（如 `^sha256:[0-9a-f]{64}$`），剥离 `:` 后再进文件名路径；通用路径校验会拒绝含 `:` 的 digest，不能混用
- **token 交换与单飞**: 上游 `401 + WWW-Authenticate: Bearer realm/service/scope` → 内部按 scope 换 token（未配凭据则匿名换取，Docker Hub 公共镜像走此流程），token 按 scope 内存缓存（TTL = `expires_in` - 60s），并发请求对同一 scope 做单飞去重；上游 `WWW-Authenticate` 不透传客户端（无凭证且需鉴权 → `401 + WWW-Authenticate: Basic realm="Orbitra"`）；**Authorization 头与 token 绝不落日志**
- **Docker HttpClient Timeout 放宽**: `"Docker"` 命名 HttpClient 连接池参数与现有保持一致，但 `Timeout` 放宽到 30 分钟（大 blob 下载），避免 GB 级镜像中途被超时中断
- **AOT 约束**: manifest 解析用 `System.Text.Json` / `JsonDocument`（AOT 安全）；blob 流式校验用 `IncrementalHash`（不整包入内存）；token 请求用固定 URL 模板 + 现有 `SocketsHttpHandler`；避免反射 / 动态代码生成

---

## Architecture Decisions

### Minimal APIs Over Controllers

- No controller classes
- Route handlers are lambda expressions
- Dependency injection via method parameters

### Kestrel Configuration

- Max concurrent connections: 2000
- Synchronous I/O: Disabled (enforces async)
- HTTP/2: Enabled with multiple connections

### HttpClient Pooling

- Connection lifetime: 5 minutes
- Max connections per server: 100
- Request timeout: 110 seconds

---

## Comments & Documentation

- Comments are written in **Chinese** (中文) - match existing style
- Use XML documentation for public APIs

```csharp
// 配置 Kestrel 并发连接与超时
// 下载失败时记录日志并返回状态码
```

---

## Common Tasks

### Adding a New Endpoint

```csharp
app.MapGet("/new-path", async (ParamType param, IHttpClientFactory http) =>
{
    // Implementation
    return Results.Ok(data);
});
```

### Adding Configuration

1. Add to `appsettings.json` for static config
2. Use environment variables for deployment-specific values

### Debugging

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Orbitra/Orbitra.csproj
```
