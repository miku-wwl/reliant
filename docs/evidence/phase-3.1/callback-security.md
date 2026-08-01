# Evidence — Callback Security (Gate 5)

## Invariant

Provider callbacks must be authenticated (HMAC signature + timestamp) and
authorized; an invalid/expired/future/non-UTC callback must be rejected with 401
and must not change state.

## Scenario

A real HTTP callback arrives at the API. The handler verifies the signature and
timestamp before applying any state change.

## Failure injection

- Missing signature / timestamp -> 401.
- Invalid signature -> 401, no state change.
- Expired / future (outside clock skew) -> 401.
- Non-UTC or malformed timestamp -> 401.
- Valid signature reaches the handler and applies state once.

## Runtime path

`HandleProviderCallback` API endpoint -> `ProviderCallbackVerifier` (HMAC-SHA256
`FixedTimeEquals`, timestamp parse + 5-minute clock skew, UTC-only) ->
`HandleProviderCallbackCommand` -> locate contribution by reference/key ->
apply state -> DB-unique inbox dedup -> persist orphan callback when not found.

## Exact tests

- `CallbackSecurityHttpTests.ValidSignature_ShouldReturn200`
- `CallbackSecurityHttpTests.MissingSignature_ShouldReturn401`
- `CallbackSecurityHttpTests.MissingTimestamp_ShouldReturn401`
- `CallbackSecurityHttpTests.InvalidSignature_ShouldReturn401_WithoutStateChange`
- `CallbackSecurityHttpTests.InvalidTimestampFormat_ShouldReturn401`
- `CallbackSecurityHttpTests.ExpiredTimestamp_ShouldReturn401`
- `CallbackSecurityHttpTests.FutureTimestampOutsideClockSkew_ShouldReturn401`
- `CallbackSecurityHttpTests.NonUtcTimestamp_ShouldBeRejected`
- `CallbackSecurityHttpTests.ValidSignedPayload_ShouldReachCallbackHandler_AndApplyStateOnce`

## Exact assertions

```text
Valid signed callback  -> HTTP 200, state applied once
Missing/invalid/expired/future/non-UTC -> HTTP 401
Invalid signature -> no state change
```

## Observed result

All PASS (9 HTTP tests). The verifier rejects every invalid timestamp class and
only a correctly signed, in-window, UTC callback is applied.

## Commit SHA

Commit 12 (`2a85f4e`) added the HTTP tests and the verifier rewrite; re-verified
in Commit 19 (`39b492c`).

## CI run

GitHub Actions `CI` (Commit 19, see `ci-run.md`).

## Limitations

Secret is shared via configuration (`Provider:Secret`); a real KMS/secret vault
rotation is out of scope. Signature scheme assumes the provider signs
`timestamp + payload` with the same shared secret.

## Conclusion

**Gate 5 PASS** — callback security is verified over the real HTTP surface.
