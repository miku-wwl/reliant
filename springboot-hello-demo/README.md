# Spring Boot Hello Demo

这是一个独立的最小 Spring Boot 示例，用来先理解 Controller，不包含数据库、消息队列或监控组件。

## 启动

```powershell
mvn spring-boot:run
```

## 调用

```powershell
Invoke-RestMethod http://localhost:8080/hello
```

返回：

```text
Hello from Spring Boot
```
