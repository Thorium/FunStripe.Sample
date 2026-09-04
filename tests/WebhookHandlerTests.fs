module WebhookHandlerTests

open System
open System.Text
open System.Security.Cryptography
open Xunit
open WebhookHandler
open WebhookEvents

// =============================================================================
// Helper to build valid Stripe-style webhook signatures
// =============================================================================

let buildSignature (secret: string) (payload: string) (timestamp: int64) =
    let signedPayload = $"{timestamp}.{payload}"
    use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret))
    let hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload))
    let hexSig = (Convert.ToHexString hash).ToLower()
    $"t={timestamp},v1={hexSig}"

let testConfig = {
    EndpointSecret = "whsec_test_secret_123"
}

// =============================================================================
// verifyWebhookSignature tests
// =============================================================================

[<Fact>]
let ``verifyWebhookSignature accepts valid signature`` () =
    let payload = """{"id":"evt_1","type":"payment_intent.succeeded"}"""
    let timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    let signature = buildSignature testConfig.EndpointSecret payload timestamp
    let result = verifyWebhookSignature testConfig signature payload
    Assert.True(result, "Valid signature should be accepted")

[<Fact>]
let ``verifyWebhookSignature accepts second v1 signature during secret rotation`` () =
    let payload = """{"id":"evt_1","type":"payment_intent.succeeded"}"""
    let timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    // During endpoint-secret rotation Stripe includes one v1 entry per active secret
    let oldSecretSig = (buildSignature "whsec_old_secret" payload timestamp).Split(',').[1]
    let validSignature = buildSignature testConfig.EndpointSecret payload timestamp
    let rotationHeader = $"{validSignature.Split(',').[0]},{oldSecretSig},{validSignature.Split(',').[1]}"
    let result = verifyWebhookSignature testConfig rotationHeader payload
    Assert.True(result, "Any matching v1 signature should be accepted")

[<Fact>]
let ``verifyWebhookSignature rejects tampered payload`` () =
    let payload = """{"id":"evt_1","type":"payment_intent.succeeded"}"""
    let timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    let signature = buildSignature testConfig.EndpointSecret payload timestamp
    let tamperedPayload = """{"id":"evt_1","type":"payment_intent.succeeded","amount":99999}"""
    let result = verifyWebhookSignature testConfig signature tamperedPayload
    Assert.False(result, "Tampered payload should be rejected")

[<Fact>]
let ``verifyWebhookSignature rejects wrong secret`` () =
    let payload = """{"id":"evt_1","type":"payment_intent.succeeded"}"""
    let timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    let signature = buildSignature "whsec_wrong_secret" payload timestamp
    let result = verifyWebhookSignature testConfig signature payload
    Assert.False(result, "Wrong secret should be rejected")

[<Fact>]
let ``verifyWebhookSignature rejects expired timestamp`` () =
    let payload = """{"id":"evt_1","type":"payment_intent.succeeded"}"""
    // Timestamp from 10 minutes ago (beyond 5 minute tolerance)
    let oldTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600L
    let signature = buildSignature testConfig.EndpointSecret payload oldTimestamp
    let result = verifyWebhookSignature testConfig signature payload
    Assert.False(result, "Expired timestamp should be rejected")

[<Fact>]
let ``verifyWebhookSignature rejects malformed signature`` () =
    let payload = """{"id":"evt_1"}"""
    let result = verifyWebhookSignature testConfig "not_a_valid_signature" payload
    Assert.False(result, "Malformed signature should be rejected")

[<Fact>]
let ``verifyWebhookSignature rejects empty signature`` () =
    let payload = """{"id":"evt_1"}"""
    let result = verifyWebhookSignature testConfig "" payload
    Assert.False(result, "Empty signature should be rejected")

// =============================================================================
// processWebhookEvent tests
// =============================================================================

[<Fact>]
let ``processWebhookEvent handles PaymentIntentSucceeded`` () =
    async {
        let event = {
            Id = "evt_test_001"
            Type = "payment_intent.succeeded"
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            Livemode = false
            Data = None
        }
        match! processWebhookEvent testConfig event with
        | Processed msg -> Assert.NotEmpty(msg)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

[<Fact>]
let ``processWebhookEvent handles PaymentIntentPaymentFailed`` () =
    async {
        let event = {
            Id = "evt_test_002"
            Type = "payment_intent.payment_failed"
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            Livemode = false
            Data = None
        }
        match! processWebhookEvent testConfig event with
        | Processed msg -> Assert.NotEmpty(msg)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

[<Fact>]
let ``processWebhookEvent handles SetupIntentSucceeded`` () =
    async {
        let event = {
            Id = "evt_test_003"
            Type = "setup_intent.succeeded"
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            Livemode = false
            Data = None
        }
        match! processWebhookEvent testConfig event with
        | Processed msg -> Assert.NotEmpty(msg)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

[<Fact>]
let ``processWebhookEvent handles CustomerCreated`` () =
    async {
        let event = {
            Id = "evt_test_004"
            Type = "customer.created"
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            Livemode = false
            Data = None
        }
        match! processWebhookEvent testConfig event with
        | Processed msg -> Assert.NotEmpty(msg)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

[<Fact>]
let ``processWebhookEvent handles ChargeSucceeded`` () =
    async {
        let event = {
            Id = "evt_test_005"
            Type = "charge.succeeded"
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            Livemode = false
            Data = None
        }
        match! processWebhookEvent testConfig event with
        | Processed msg -> Assert.NotEmpty(msg)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

[<Fact>]
let ``processWebhookEvent handles InvoicePaymentSucceeded`` () =
    async {
        let event = {
            Id = "evt_test_006"
            Type = "invoice.payment_succeeded"
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            Livemode = false
            Data = None
        }
        match! processWebhookEvent testConfig event with
        | Processed msg -> Assert.NotEmpty(msg)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

[<Fact>]
let ``processWebhookEvent returns UnsupportedEvent for unknown types`` () =
    async {
        let event = {
            Id = "evt_test_007"
            Type = "unknown.event.type"
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            Livemode = false
            Data = None
        }
        match! processWebhookEvent testConfig event with
        | UnsupportedEvent name -> Assert.Equal("unknown.event.type", name)
        | other -> Assert.Fail($"Expected UnsupportedEvent, got {other}")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

// =============================================================================
// handleWebhookRequest integration tests
// =============================================================================

[<Fact>]
let ``handleWebhookRequest returns InvalidSignature for bad signature`` () =
    async {
        let payload = """{"id":"evt_1","type":"payment_intent.succeeded"}"""
        let! (result, statusCode) = handleWebhookRequest testConfig "bad_sig" payload "payment_intent.succeeded" "evt_1"
        Assert.Equal(400, statusCode)
        match result with
        | InvalidSignature -> ()
        | other -> Assert.Fail($"Expected InvalidSignature, got {other}")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

[<Fact>]
let ``handleWebhookRequest processes valid request`` () =
    async {
        let payload = """{"id":"evt_valid","type":"payment_intent.succeeded"}"""
        let timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        let signature = buildSignature testConfig.EndpointSecret payload timestamp
        let! (result, statusCode) = handleWebhookRequest testConfig signature payload "payment_intent.succeeded" "evt_valid"
        Assert.Equal(200, statusCode)
        match result with
        | Processed _ -> ()
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

[<Fact>]
let ``handleWebhookRequest returns 200 for unsupported event type`` () =
    async {
        let payload = """{"id":"evt_unsup","type":"plan.created"}"""
        let timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        let signature = buildSignature testConfig.EndpointSecret payload timestamp
        let! (result, statusCode) = handleWebhookRequest testConfig signature payload "plan.created" "evt_unsup"
        Assert.Equal(200, statusCode)
        match result with
        | UnsupportedEvent _ -> ()
        | other -> Assert.Fail($"Expected UnsupportedEvent, got {other}")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task
