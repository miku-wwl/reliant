package com.example.hello;

import io.micrometer.core.instrument.MeterRegistry;
import io.micrometer.tracing.Span;
import io.micrometer.tracing.Tracer;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
public class HelloController {

    private final MeterRegistry meterRegistry;
    private final Tracer tracer;

    public HelloController(MeterRegistry meterRegistry, Tracer tracer) {
        this.meterRegistry = meterRegistry;
        this.tracer = tracer;
    }

    @GetMapping("/hello")
    public String hello() {
        Span span = tracer.nextSpan().name("hello.business").start();
        try (Tracer.SpanInScope ignored = tracer.withSpan(span)) {
            span.tag("business.operation", "hello");
            span.tag("business.result", "success");
            recordBusinessResult("success");
            return "Hello from Spring Boot";
        } finally {
            span.end();
        }
    }

    @GetMapping("/hello/fail")
    public ResponseEntity<String> fail() {
        Span span = tracer.nextSpan().name("hello.business").start();
        try (Tracer.SpanInScope ignored = tracer.withSpan(span)) {
            span.tag("business.operation", "hello");
            span.tag("business.result", "failure");
            recordBusinessResult("failure");
            return ResponseEntity.internalServerError()
                .body("Intentional demo failure");
        } finally {
            span.end();
        }
    }

    private void recordBusinessResult(String result) {
        meterRegistry.counter(
                "demo_business_operation",
                "operation", "hello",
                "result", result)
            .increment();
    }
}
