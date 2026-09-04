module EventsTests

open Xunit
open WebhookEvents

// =============================================================================
// parseEventType tests
// =============================================================================

[<Fact>]
let ``parseEventType returns PaymentIntentSucceeded for correct string`` () =
    let result = parseEventType "payment_intent.succeeded"
    Assert.Equal(PaymentIntentSucceeded, result)

[<Fact>]
let ``parseEventType returns PaymentIntentPaymentFailed for correct string`` () =
    let result = parseEventType "payment_intent.payment_failed"
    Assert.Equal(PaymentIntentPaymentFailed, result)

[<Fact>]
let ``parseEventType returns SetupIntentSucceeded for correct string`` () =
    let result = parseEventType "setup_intent.succeeded"
    Assert.Equal(SetupIntentSucceeded, result)

[<Fact>]
let ``parseEventType returns CustomerCreated for correct string`` () =
    let result = parseEventType "customer.created"
    Assert.Equal(CustomerCreated, result)

[<Fact>]
let ``parseEventType returns ChargeSucceeded for correct string`` () =
    let result = parseEventType "charge.succeeded"
    Assert.Equal(ChargeSucceeded, result)

[<Fact>]
let ``parseEventType returns InvoicePaymentSucceeded for correct string`` () =
    let result = parseEventType "invoice.payment_succeeded"
    Assert.Equal(InvoicePaymentSucceeded, result)

[<Fact>]
let ``parseEventType returns Other for unknown event type`` () =
    let result = parseEventType "some.unknown.event"
    Assert.Equal(Other "some.unknown.event", result)

[<Fact>]
let ``parseEventType returns Other for empty string`` () =
    let result = parseEventType ""
    Assert.Equal(Other "", result)

// =============================================================================
// handlePaymentSuccess tests
// =============================================================================

[<Fact>]
let ``handlePaymentSuccess returns Success with customer`` () =
    async {
        let paymentIntent = {
            Id = "pi_test_123"
            Amount = 2000L
            Currency = "usd"
            CustomerId = Some "cus_test_456"
        }
        match! handlePaymentSuccess paymentIntent with
        | Success msg ->
            Assert.Contains("pi_test_123", msg)
            Assert.Contains("cus_test_456", msg)
        | Error _ | Ignored _ -> Assert.Fail("Expected Success result")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

[<Fact>]
let ``handlePaymentSuccess returns Success without customer`` () =
    async {
        let paymentIntent = {
            Id = "pi_test_789"
            Amount = 5000L
            Currency = "eur"
            CustomerId = None
        }
        match! handlePaymentSuccess paymentIntent with
        | Success msg ->
            Assert.Contains("pi_test_789", msg)
            Assert.Contains("no customer", msg)
        | Error _ | Ignored _ -> Assert.Fail("Expected Success result")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

// =============================================================================
// handlePaymentFailure tests
// =============================================================================

[<Fact>]
let ``handlePaymentFailure returns Success`` () =
    async {
        let paymentIntent = {
            Id = "pi_fail_001"
            Amount = 1000L
            Currency = "usd"
            CustomerId = Some "cus_123"
        }
        match! handlePaymentFailure paymentIntent with
        | Success msg -> Assert.Contains("pi_fail_001", msg)
        | Error _ | Ignored _ -> Assert.Fail("Expected Success result")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

// =============================================================================
// handleSetupSuccess tests
// =============================================================================

[<Fact>]
let ``handleSetupSuccess returns Success with customer and payment method`` () =
    async {
        let setupIntent = {
            Id = "seti_test_001"
            CustomerId = Some "cus_test_001"
            PaymentMethodId = Some "pm_test_001"
        }
        match! handleSetupSuccess setupIntent with
        | Success msg ->
            Assert.Contains("seti_test_001", msg)
            Assert.Contains("payment method saved", msg)
        | Error _ | Ignored _ -> Assert.Fail("Expected Success result")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

[<Fact>]
let ``handleSetupSuccess returns Success without payment method`` () =
    async {
        let setupIntent = {
            Id = "seti_test_002"
            CustomerId = Some "cus_test_002"
            PaymentMethodId = None
        }
        match! handleSetupSuccess setupIntent with
        | Success msg -> Assert.Contains("seti_test_002", msg)
        | Error _ | Ignored _ -> Assert.Fail("Expected Success result")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

[<Fact>]
let ``handleSetupSuccess returns Error without customer`` () =
    async {
        let setupIntent = {
            Id = "seti_test_003"
            CustomerId = None
            PaymentMethodId = Some "pm_test_003"
        }
        match! handleSetupSuccess setupIntent with
        | Error msg -> Assert.Contains("seti_test_003", msg)
        | Success _ | Ignored _ -> Assert.Fail("Expected Error result")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

// =============================================================================
// handleCustomerCreated tests
// =============================================================================

[<Fact>]
let ``handleCustomerCreated returns Success`` () =
    async {
        let customer = {
            Id = "cus_new_001"
            Email = Some "test@example.com"
            Name = Some "Test User"
        }
        match! handleCustomerCreated customer with
        | Success msg -> Assert.Contains("cus_new_001", msg)
        | Error _ | Ignored _ -> Assert.Fail("Expected Success result")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task

[<Fact>]
let ``handleCustomerCreated handles missing email`` () =
    async {
        let customer = {
            Id = "cus_new_002"
            Email = None
            Name = None
        }
        match! handleCustomerCreated customer with
        | Success msg -> Assert.Contains("cus_new_002", msg)
        | Error _ | Ignored _ -> Assert.Fail("Expected Success result")
    } |> Async.StartImmediateAsTask :> System.Threading.Tasks.Task
