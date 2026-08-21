# Spring Boot Observability Demo

这是一个独立的 Spring Boot 学习 Demo，用来理解：

```text
业务代码埋点 → Prometheus 采集 Metrics → Grafana 查询和展示
业务 Trace → OpenTelemetry OTLP → Collector / Tempo
```

本 Demo 不使用 Docker、不使用 Kubernetes，也不包含数据库和消息队列。

## 启动

```powershell
mvn spring-boot:run
```

应用启动在 `8081` 端口。

## 业务接口

```powershell
Invoke-RestMethod http://localhost:8081/hello
Invoke-RestMethod http://localhost:8081/hello/fail
```

两个接口分别产生：

```text
result="success"
result="failure"
```

## 查看指标

打开：

```text
http://localhost:8081/actuator/prometheus
```

搜索：

```text
demo_business_operation_total
```

## OpenTelemetry Trace

这个增量加入了 OpenTelemetry Trace，但没有在业务代码中直接写 Jaeger
或 Tempo exporter：

```text
Spring Boot HTTP instrumentation
        +
Controller 的 hello.business span
        ↓
Micrometer Tracing → OpenTelemetry bridge
        ↓ OTLP/HTTP
OpenTelemetry Collector: http://localhost:4318/v1/traces
        ↓
Tempo / Jaeger 等 Trace backend
```

代码中的两类 Trace：

- Spring Boot 自动生成 HTTP server span；
- `HelloController` 手动生成 `hello.business` span。

默认采样率是 `1.0`，方便本地学习。Collector 地址可以通过环境变量覆盖：

```powershell
$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT = "http://localhost:4318/v1/traces"
```

如果没有启动 Collector，业务接口仍然应该返回；只是 Trace 无法被后端保存。
这体现了 telemetry fail-open 的原则。

## Prometheus

Prometheus 配置在：

```text
prometheus/prometheus.yml
```

它每 5 秒抓取：

```text
http://localhost:8081/actuator/prometheus
```

## Grafana

Prometheus 数据源配置示例在：

```text
grafana/provisioning/datasources/prometheus.yml
```

Dashboard 文件在：

```text
grafana/dashboards/springboot-observability.json
```

也可以在 Grafana Explore 中执行：

```promql
sum by (operation, result) (
  rate(demo_business_operation_total[1m])
)
```

这个查询会按 `operation` 和 `result` 分组，观察成功与失败的请求速率。

## 三段代码分别在哪里

业务侧：

```text
src/main/java/com/example/hello/HelloController.java
```

这里用 Micrometer 的 `MeterRegistry` 创建 counter。`operation` 和 `result` 是业务代码定义的标签。

Prometheus 侧：

```text
prometheus/prometheus.yml
```

这里定义 Prometheus 每 5 秒访问哪个 metrics endpoint。

Grafana 侧：

```text
grafana/provisioning/datasources/prometheus.yml
grafana/dashboards/springboot-observability.json
```

前一个文件定义 Prometheus 数据源，后一个文件定义 Dashboard 的卡片和 PromQL 查询。Grafana 的 Dashboard 原生保存格式是 JSON；这不是 Docker 或 Kubernetes 配置。

如果本机的 8081 已被其他服务占用，可以使用：

```powershell
java -jar target/springboot-hello-demo-0.0.1-SNAPSHOT.jar --server.port=18081
```
