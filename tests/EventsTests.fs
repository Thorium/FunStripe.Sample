module EventsTests

open Xunit
open FunStripe
open Stripe.PaymentMethod
open WebhookEvents
open System.Threading.Tasks

// The handlers take FunStripe models. Building those field by field is long, so the
// tests deserialise the sample JSON and adjust the fields a test is about.

let paymentIntent : PaymentIntent = Util.deserialise WebhookSamples.paymentIntentObject
let setupIntent : SetupIntent = Util.deserialise FakeStripeServer.setupIntentJson
let customer : Customer = Util.deserialise FakeStripeServer.customerJson

// =============================================================================
// Customer ID tests
// =============================================================================

[<Fact>]
let ``paymentIntentCustomerId reads a bare customer ID`` () =
    Assert.Equal(Some "cus_SampleCustomer", paymentIntentCustomerId paymentIntent)

[<Fact>]
let ``paymentIntentCustomerId reads the ID of an expanded customer`` () =
    let expanded = { paymentIntent with Customer = Some (PaymentIntentCustomer'AnyOf.Customer customer) }
    Assert.Equal(Some "cus_FakeCustomer", paymentIntentCustomerId expanded)

[<Fact>]
let ``setupIntentCustomerId reads a bare customer ID`` () =
    Assert.Equal(Some "cus_FakeCustomer", setupIntentCustomerId setupIntent)

// =============================================================================
// handlePaymentSuccess tests
// =============================================================================

[<Fact>]
let ``handlePaymentSuccess returns Success with customer`` () =
    async {
        match! handlePaymentSuccess paymentIntent with
        | Success msg ->
            Assert.Contains("pi_3SampleSucceeded", msg)
            Assert.Contains("cus_SampleCustomer", msg)
        | HandlerError _ | Ignored _ -> Assert.Fail("Expected Success result")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``handlePaymentSuccess returns Success without customer`` () =
    async {
        match! handlePaymentSuccess { paymentIntent with Id = "pi_test_789"; Customer = None } with
        | Success msg ->
            Assert.Contains("pi_test_789", msg)
            Assert.Contains("no customer", msg)
        | HandlerError _ | Ignored _ -> Assert.Fail("Expected Success result")
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// handlePaymentFailure tests
// =============================================================================

[<Fact>]
let ``handlePaymentFailure returns Success`` () =
    async {
        match! handlePaymentFailure { paymentIntent with Id = "pi_fail_001" } with
        | Success msg -> Assert.Contains("pi_fail_001", msg)
        | HandlerError _ | Ignored _ -> Assert.Fail("Expected Success result")
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// handleSetupSuccess tests
// =============================================================================

[<Fact>]
let ``handleSetupSuccess returns Success with customer and payment method`` () =
    async {
        match! handleSetupSuccess setupIntent with
        | Success msg ->
            Assert.Contains("seti_FakeSetup", msg)
            Assert.Contains("payment method saved", msg)
        | HandlerError _ | Ignored _ -> Assert.Fail("Expected Success result")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``handleSetupSuccess returns Success without payment method`` () =
    async {
        match! handleSetupSuccess { setupIntent with Id = "seti_test_002"; PaymentMethod = None } with
        | Success msg ->
            Assert.Contains("seti_test_002", msg)
            Assert.DoesNotContain("payment method saved", msg)
        | HandlerError _ | Ignored _ -> Assert.Fail("Expected Success result")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``handleSetupSuccess returns HandlerError without customer`` () =
    async {
        match! handleSetupSuccess { setupIntent with Id = "seti_test_003"; Customer = None } with
        | HandlerError msg -> Assert.Contains("seti_test_003", msg)
        | Success _ | Ignored _ -> Assert.Fail("Expected HandlerError result")
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// handleCustomerCreated tests
// =============================================================================

[<Fact>]
let ``handleCustomerCreated returns Success`` () =
    async {
        match! handleCustomerCreated customer with
        | Success msg -> Assert.Contains("cus_FakeCustomer", msg)
        | HandlerError _ | Ignored _ -> Assert.Fail("Expected Success result")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``handleCustomerCreated handles missing email`` () =
    async {
        match! handleCustomerCreated { customer with Id = "cus_new_002"; Email = None; Name = None } with
        | Success msg -> Assert.Contains("cus_new_002", msg)
        | HandlerError _ | Ignored _ -> Assert.Fail("Expected Success result")
    } |> Async.StartImmediateAsTask :> Task
