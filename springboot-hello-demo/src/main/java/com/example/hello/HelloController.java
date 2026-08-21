package com.example.hello;

import io.micrometer.core.instrument.MeterRegistry;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
public class HelloController {

    private final MeterRegistry meterRegistry;

    public HelloController(MeterRegistry meterRegistry) {
        this.meterRegistry = meterRegistry;
    }

    @GetMapping("/hello")
    public String hello() {
        recordBusinessResult("success");
        return "Hello from Spring Boot";
    }

    @GetMapping("/hello/fail")
    public ResponseEntity<String> fail() {
        recordBusinessResult("failure");
        return ResponseEntity.internalServerError()
            .body("Intentional demo failure");
    }

    private void recordBusinessResult(String result) {
        meterRegistry.counter(
                "demo_business_operation",
                "operation", "hello",
                "result", result)
            .increment();
    }
}
