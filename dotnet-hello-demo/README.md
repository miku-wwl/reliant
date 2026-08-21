# .NET Hello Demo

这是一个独立的最小 ASP.NET Core Hello 示例，用来先理解一个 HTTP Controller 的基本结构。

这个 demo 只包含业务代码：没有 Docker、Kubernetes、Prometheus、Grafana 或 OpenTelemetry。后续 Prometheus 学习会单独通过 Pull Request 进行，避免把学习内容混在这个最小示例里。

## 启动

```powershell
dotnet run
```

默认监听 `http://localhost:8081`。

如果主项目已经占用 8081，可以临时使用其他端口：

```powershell
dotnet run -- --urls http://localhost:18082
```

## 调用

```powershell
Invoke-RestMethod http://localhost:8081/hello
```

返回：

```text
Hello from .NET
```

## 代码对应关系

`Program.cs` 使用 ASP.NET Core Minimal API，把 Spring Boot 示例里的启动类和 `HelloController` 合并成了一个最小文件：

- `WebApplication.CreateBuilder`：创建应用；
- `builder.Build()`：构建应用；
- `app.MapGet("/hello", ...)`：声明 `GET /hello` 接口；
- `app.Run()`：启动 HTTP 服务。
