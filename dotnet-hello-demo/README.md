# .NET Hello Observability Demo

这是 Spring Boot Hello Demo 的 .NET 对应版本，用来学习：

```text
业务代码埋点 → Prometheus 采集和保存 → Grafana 查询和展示
```

本 Demo 不使用 Docker、不使用 Kubernetes，也不包含数据库和消息队列。

## 启动

```powershell
dotnet run
```

应用监听：

```text
http://localhost:8081
```

## 业务接口

```powershell
Invoke-RestMethod http://localhost:8081/hello
Invoke-WebRequest http://localhost:8081/hello/fail -UseBasicParsing
```

两个接口分别产生：

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

这里使用 .NET Meter 和 Counter<long> 记录业务结果，再由 OpenTelemetry Prometheus Exporter 暴露 /metrics。

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
