# ADR-0010: API Contract

## Status

Proposed

## Context

Phase 1 需要建立 API 契约。outline 第14.1节要求 OpenAPI 兼容检查。API 契约必须稳定，后续 Phase 依赖它。

## Decision

### 1. API 风格

REST API，使用标准 HTTP 方法和状态码。

### 2. R1 Phase 1 端点

```
POST   /api/organizations                    创建组织
GET    /api/organizations/{orgId}             查询组织

POST   /api/organizations/{orgId}/memberships  邀请成员
GET    /api/organizations/{orgId}/memberships  查询成员列表

POST   /api/organizations/{orgId}/campaigns    创建 Campaign
GET    /api/organizations/{orgId}/campaigns    查询 Campaign 列表
GET    /api/organizations/{orgId}/campaigns/{campaignId}  查询 Campaign 详情

POST   /api/organizations/{orgId}/contributions  提交 Contribution
GET    /api/organizations/{orgId}/contributions   查询 Contribution 列表
GET    /api/organizations/{orgId}/contributions/{contributionId}  查询详情
```

### 3. 请求和响应格式

- 请求：JSON
- 响应：JSON
- 编码：UTF-8
- 内容类型：`application/json`

### 4. HTTP 状态码

| 状态码 | 场景 |
| --- | --- |
| 200 OK | 查询成功 |
| 201 Created | 创建成功，Location Header 指向新资源 |
| 204 No Content | 删除成功 |
| 400 Bad Request | 请求格式错误、缺少必填字段 |
| 401 Unauthorized | 未认证、JWT 无效 |
| 403 Forbidden | 无权限、角色不够 |
| 404 Not Found | 资源不存在或不属于当前租户 |
| 409 Conflict | 幂等冲突、请求体不匹配、并发冲突 |
| 412 Precondition Failed | ETag 不匹配 |
| 429 Too Many Requests | 限流 |
| 500 Internal Server Error | 未预期错误 |

### 5. Problem Details

错误响应用 RFC 9457 Problem Details 格式：

```json
{
  "type": "https://reliant.dev/errors/invalid-state-transition",
  "title": "Invalid state transition",
  "status": 409,
  "detail": "Cannot transition from Created to Succeeded",
  "instance": "/api/organizations/abc/contributions/def",
  "trace-id": "00-abc123-def456"
}
```

### 6. 幂等 Header

- `POST /contributions` 必须带 `Idempotency-Key` Header
- 其他 POST 端点可选

### 7. ETag Header

- GET 响应带 `ETag` Header
- PUT/PATCH 请求带 `If-Match` Header

### 8. 分页

- List 端点使用 cursor 分页：`?cursor=abc&limit=20`
- 响应包含 `nextCursor`（null 表示没有更多）

### 9. 限流

- 每个 Organization 有默认限制：100 请求/分钟
- 超限返回 429 + `Retry-After` Header
- Phase 5 根据负载测试调整限制

### 10. API 版本

- URL 中不含版本号（R1 只有一个版本）
- 如果未来需要 v2，通过 Header `Api-Version` 控制

## Consequences

- API 契约一旦发布，后续 Phase 不能 breaking change
- Problem Details 是标准格式，客户端需要按此解析错误
- 分页用 cursor 而非 offset，适合大数据量
- 限流在 Phase 1 实现基础版，Phase 5 根据负载调整
