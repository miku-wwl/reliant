# .NET Hello Prometheus Demo

这是一个独立的 ASP.NET Core 学习 Demo，用来理解：

```text
业务代码埋点 → Prometheus 采集和保存 → Grafana 查询和展示
```

这个版本只通过 Pull Request 学习 Prometheus 和 Grafana，不使用 Docker、Kubernetes、数据库或消息队列。

## 启动

```powershell
dotnet run
```

默认监听 `http://localhost:8081`。

如果主项目已经占用 8081，可以临时使用其他端口：

```powershell
dotnet run -- --urls http://localhost:18082
```

## 业务接口

```powershell
Invoke-RestMethod http://localhost:8081/hello
Invoke-WebRequest http://localhost:8081/hello/fail -UseBasicParsing
```

两个接口分别记录：

```text
result="success"
result="failure"
```

## 查看指标

打开：

```text
http://localhost:8081/metrics
```

搜索：

```text
demo_business_operation_total
```

## Prometheus

Prometheus 配置在：

```text
prometheus/prometheus.yml
```

它每 5 秒抓取：

```text
http://localhost:8081/metrics
```

## Grafana

Prometheus 数据源配置在：

```text
grafana/provisioning/datasources/prometheus.yml
```

Dashboard 文件在：

```text
grafana/dashboards/dotnet-hello-observability.json
```

也可以在 Grafana Explore 中执行：

```promql
sum by (operation, result) (
  rate(demo_business_operation_total[1m])
)
```

## 三段代码分别在哪里

业务侧：

```text
Program.cs
```

这里使用 .NET `Meter` 和 `Counter<long>` 记录业务结果，再由 OpenTelemetry Prometheus Exporter 暴露 `/metrics`。

Prometheus 侧：

```text
prometheus/prometheus.yml
```

这里定义 Prometheus 每 5 秒访问哪个 metrics endpoint。

Grafana 侧：

```text
grafana/provisioning/datasources/prometheus.yml
grafana/dashboards/dotnet-hello-observability.json
```

前一个文件定义 Prometheus 数据源，后一个文件定义 Dashboard 的卡片和 PromQL 查询。Grafana Dashboard 的原生保存格式是 JSON。
