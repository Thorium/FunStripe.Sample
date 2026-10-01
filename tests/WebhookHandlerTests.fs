module WebhookHandlerTests

open System
open System.Threading.Tasks
open Xunit
open FunStripe
open Stripe.Event
open WebhookHandler
open WebhookSamples

let testConfig = {
    EndpointSecret = "whsec_test_secret_123"
}

let now () = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

/// An event as FunStripe deserialises it from a webhook payload
let eventOf (eventType: string) (objectJson: string) : Event =
    eventPayload "evt_test_001" eventType objectJson |> Util.deserialise

let failedPaymentIntentObject =
    paymentIntentObject
        .Replace("\"status\": \"succeeded\"", "\"status\": \"requires_payment_method\"")
        .Replace(
            "\"last_payment_error\": null",
            "\"last_payment_error\": { \"code\": \"card_declined\", \"decline_code\": \"insufficient_funds\", \"message\": \"Your card has insufficient funds.\", \"type\": \"card_error\" }")

// =============================================================================
// Signature verification tests (handleWebhookRequest with FunStripe.WebhookSigning)
// =============================================================================

[<Fact>]
let ``handleWebhookRequest processes valid request`` () =
    async {
        let payload = paymentIntentSucceeded
        let signature = signatureHeader testConfig.EndpointSecret payload (now ())
        let! (result, statusCode) = handleWebhookRequest testConfig signature payload
        Assert.Equal(200, statusCode)
        match result with
        | Processed msg -> Assert.Contains("pi_3SampleSucceeded", msg)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``handleWebhookRequest accepts second v1 signature during secret rotation`` () =
    async {
        let payload = paymentIntentSucceeded
        let timestamp = now ()
        // During endpoint-secret rotation Stripe includes one v1 entry per active secret
        let oldSecretSig = (signatureHeader "whsec_old_secret" payload timestamp).Split(',').[1]
        let validSignature = signatureHeader testConfig.EndpointSecret payload timestamp
        let rotationHeader = $"{validSignature.Split(',').[0]},{oldSecretSig},{validSignature.Split(',').[1]}"
        let! (result, statusCode) = handleWebhookRequest testConfig rotationHeader payload
        Assert.Equal(200, statusCode)
        match result with
        | Processed _ -> ()
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> Task

let assertInvalidSignature (signature: string) (payload: string) =
    async {
        let! (result, statusCode) = handleWebhookRequest testConfig signature payload
        Assert.Equal(400, statusCode)
        match result with
        | InvalidSignature -> ()
        | other -> Assert.Fail($"Expected InvalidSignature, got {other}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``handleWebhookRequest rejects tampered payload`` () =
    let signature = signatureHeader testConfig.EndpointSecret paymentIntentSucceeded (now ())
    let tamperedPayload = paymentIntentSucceeded.Replace("\"amount\": 2000", "\"amount\": 99999")
    assertInvalidSignature signature tamperedPayload

[<Fact>]
let ``handleWebhookRequest rejects wrong secret`` () =
    let signature = signatureHeader "whsec_wrong_secret" paymentIntentSucceeded (now ())
    assertInvalidSignature signature paymentIntentSucceeded

[<Fact>]
let ``handleWebhookRequest rejects expired timestamp`` () =
    // Timestamp from 10 minutes ago (beyond 5 minute tolerance)
    let signature = signatureHeader testConfig.EndpointSecret paymentIntentSucceeded (now () - 600L)
    assertInvalidSignature signature paymentIntentSucceeded

[<Fact>]
let ``handleWebhookRequest rejects malformed signature`` () =
    assertInvalidSignature "not_a_valid_signature" paymentIntentSucceeded

[<Fact>]
let ``handleWebhookRequest rejects empty and missing signature`` () =
    Task.WhenAll(
        assertInvalidSignature "" paymentIntentSucceeded,
        assertInvalidSignature null paymentIntentSucceeded)

[<Fact>]
let ``handleWebhookRequest returns 200 for unsupported event type`` () =
    async {
        let payload = eventPayload "evt_unsup" "plan.created" """{ "id": "plan_123", "object": "plan" }"""
        let signature = signatureHeader testConfig.EndpointSecret payload (now ())
        let! (result, statusCode) = handleWebhookRequest testConfig signature payload
        Assert.Equal(200, statusCode)
        match result with
        | UnsupportedEvent name -> Assert.Equal("plan.created", name)
        | other -> Assert.Fail($"Expected UnsupportedEvent, got {other}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``handleWebhookRequest returns 500 when the event object does not match its type`` () =
    async {
        // A signed payment_intent.succeeded event whose object is not a payment intent
        let payload = eventPayload "evt_bad" "payment_intent.succeeded" """{ "id": "plan_123", "object": "plan" }"""
        let signature = signatureHeader testConfig.EndpointSecret payload (now ())
        let! (result, statusCode) = handleWebhookRequest testConfig signature payload
        Assert.Equal(500, statusCode)
        match result with
        | Failed _ -> ()
        | other -> Assert.Fail($"Expected Failed, got {other}")
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// processWebhookEvent tests
// =============================================================================

[<Fact>]
let ``processWebhookEvent handles PaymentIntentSucceeded`` () =
    async {
        match! processWebhookEvent (eventOf "payment_intent.succeeded" paymentIntentObject) with
        | Processed msg ->
            Assert.Contains("pi_3SampleSucceeded", msg)
            Assert.Contains("cus_SampleCustomer", msg)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``processWebhookEvent handles PaymentIntentPaymentFailed`` () =
    async {
        match! processWebhookEvent (eventOf "payment_intent.payment_failed" failedPaymentIntentObject) with
        | Processed msg -> Assert.Contains("pi_3SampleSucceeded", msg)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``processWebhookEvent handles SetupIntentSucceeded`` () =
    async {
        match! processWebhookEvent (eventOf "setup_intent.succeeded" FakeStripeServer.setupIntentJson) with
        | Processed msg -> Assert.Contains("payment method saved", msg)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``processWebhookEvent handles CustomerCreated`` () =
    async {
        match! processWebhookEvent (eventOf "customer.created" FakeStripeServer.customerJson) with
        | Processed msg -> Assert.Contains("cus_FakeCustomer", msg)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``processWebhookEvent handles ChargeSucceeded`` () =
    async {
        match! processWebhookEvent (eventOf "charge.succeeded" """{ "id": "ch_123", "object": "charge" }""") with
        | Processed msg -> Assert.NotEmpty(msg)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``processWebhookEvent handles InvoicePaymentSucceeded`` () =
    async {
        match! processWebhookEvent (eventOf "invoice.payment_succeeded" """{ "id": "in_123", "object": "invoice" }""") with
        | Processed msg -> Assert.NotEmpty(msg)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``processWebhookEvent returns UnsupportedEvent for event types FunStripe does not know`` () =
    async {
        let stripeEvent = eventOf "unknown.event.type" """{ "id": "obj_123" }"""
        Assert.Equal(EventType.UnknownEnumValue "unknown.event.type", stripeEvent.Type)
        match! processWebhookEvent stripeEvent with
        | UnsupportedEvent name -> Assert.Equal("unknown.event.type", name)
        | other -> Assert.Fail($"Expected UnsupportedEvent, got {other}")
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// eventTypeName tests
// =============================================================================

[<Fact>]
let ``eventTypeName returns the name Stripe uses`` () =
    Assert.Equal("payment_intent.succeeded", eventTypeName EventType.PaymentIntentSucceeded)
    Assert.Equal("customer.created", eventTypeName EventType.CustomerCreated)
    Assert.Equal("some.new_event", eventTypeName (EventType.UnknownEnumValue "some.new_event"))

// =============================================================================
// Endpoint secret tests
// =============================================================================

[<Theory; InlineData(""); InlineData("   "); InlineData("whsec_...")>]
let ``handleWebhookRequest rejects every delivery while the endpoint secret is not configured`` (secret: string) =
    async {
        // Correctly signed with the blank or placeholder secret, which anyone could do
        let signature = signatureHeader secret paymentIntentSucceeded (now ())
        let! (result, statusCode) = handleWebhookRequest { EndpointSecret = secret } signature paymentIntentSucceeded
        Assert.Equal(500, statusCode)
        match result with
        | Failed msg -> Assert.Contains("not configured", msg)
        | other -> Assert.Fail($"Expected Failed, got {other}")
    } |> Async.StartImmediateAsTask :> Task
