module IntegrationExampleTests

open Xunit
open IntegrationExample
open FakeStripeServer
open System.Threading.Tasks

// =============================================================================
// API response helper tests
// =============================================================================

[<Fact>]
let ``createSuccessResponse sets Success true and Data`` () =
    let response = createSuccessResponse "hello"
    Assert.True(response.Success)
    Assert.Equal(Some "hello", response.Data)
    Assert.Equal(None, response.Error)

[<Fact>]
let ``createErrorResponse sets Success false and Error`` () =
    let response : ApiResponse<string> = createErrorResponse "something broke"
    Assert.False(response.Success)
    Assert.Equal(None, response.Data)
    Assert.Equal(Some "something broke", response.Error)

// =============================================================================
// createPaymentEndpoint tests
// =============================================================================

[<Fact>]
let ``createPaymentEndpoint returns success for valid request`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        let request = {
            Amount = 50.00m
            Currency = "usd"
            CustomerEmail = "test@example.com"
            CustomerName = "Test User"
            IdempotencyKey = "order-1042"
        }
        let! response = createPaymentEndpoint stripe.Settings request
        Assert.True(response.Success)
        match response.Data with
        | Some data ->
            Assert.Equal("pi_FakePayment", data.PaymentIntentId)
            Assert.Equal("cus_FakeCustomer", data.CustomerId)
            Assert.Equal("pi_FakePayment_secret_fake", data.ClientSecret)
        | None -> Assert.Fail("Expected Data to be Some")

        // The payment intent is created for the customer of the first call
        match stripe.Requests with
        | [ customerRequest; paymentRequest ] ->
            Assert.Equal("Test User", customerRequest.Form.["name"])
            Assert.Equal("5000", paymentRequest.Form.["amount"])
            Assert.Equal("cus_FakeCustomer", paymentRequest.Form.["customer"])
        | requests -> Assert.Fail($"Expected two requests, got {requests.Length}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``createPaymentEndpoint handles single-word name`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        let request = {
            Amount = 10.00m
            Currency = "eur"
            CustomerEmail = "mono@example.com"
            CustomerName = "Cher"
            IdempotencyKey = "order-1042"
        }
        let! response = createPaymentEndpoint stripe.Settings request
        Assert.True(response.Success)
        Assert.Equal("Cher", stripe.Requests.Head.Form.["name"])
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``createPaymentEndpoint handles null name`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        let request = {
            Amount = 10.00m
            Currency = "gbp"
            CustomerEmail = "anon@example.com"
            CustomerName = null
            IdempotencyKey = "order-1042"
        }
        let! response = createPaymentEndpoint stripe.Settings request
        Assert.True(response.Success)
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``createPaymentEndpoint rejects unsupported currency without calling Stripe`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        let request = {
            Amount = 10.00m
            Currency = "dollars"
            CustomerEmail = "test@example.com"
            CustomerName = "Test User"
            IdempotencyKey = "order-1042"
        }
        let! response = createPaymentEndpoint stripe.Settings request
        Assert.False(response.Success)
        Assert.Equal(Some "Unsupported currency", response.Error)
        Assert.Empty(stripe.Requests)
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``createPaymentEndpoint reports a Stripe error without leaking its message`` () =
    async {
        use stripe = new FakeStripe(respondError)
        let request = {
            Amount = 10.00m
            Currency = "usd"
            CustomerEmail = "not-an-email"
            CustomerName = "Test User"
            IdempotencyKey = "order-1042"
        }
        let! response = createPaymentEndpoint stripe.Settings request
        Assert.False(response.Success)
        Assert.Equal(Some "Failed to create customer", response.Error)
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// createSetupEndpoint tests
// =============================================================================

[<Fact>]
let ``createSetupEndpoint returns success for valid request`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        let request = {
            CustomerEmail = "setup@example.com"
            CustomerName = "Setup User"
            IdempotencyKey = "signup-77"
        }
        let! response = createSetupEndpoint stripe.Settings request
        Assert.True(response.Success)
        match response.Data with
        | Some data ->
            Assert.Equal("seti_FakeSetup", data.SetupIntentId)
            Assert.Equal("cus_FakeCustomer", data.CustomerId)
            Assert.Equal("seti_FakeSetup_secret_fake", data.ClientSecret)
        | None -> Assert.Fail("Expected Data to be Some")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``createSetupEndpoint handles null name`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        let request = {
            CustomerEmail = "noname@example.com"
            CustomerName = null
            IdempotencyKey = "order-1042"
        }
        let! response = createSetupEndpoint stripe.Settings request
        Assert.True(response.Success)
    } |> Async.StartImmediateAsTask :> Task

[<Theory>]
[<InlineData(0.0)>]
[<InlineData(-5.0)>]
[<InlineData(0.004)>]
[<InlineData(1000000.0)>]
[<InlineData(99999999999.0)>]
let ``createPaymentEndpoint rejects invalid amounts without calling Stripe`` (amount: float) =
    async {
        use stripe = new FakeStripe(respondOk)
        let request = {
            Amount = decimal amount
            Currency = "usd"
            CustomerEmail = "test@example.com"
            CustomerName = "Test User"
            IdempotencyKey = "order-1042"
        }
        let! response = createPaymentEndpoint stripe.Settings request
        Assert.False(response.Success)
        Assert.Equal(Some "Invalid amount", response.Error)
        Assert.Empty(stripe.Requests)
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// Idempotency tests
// =============================================================================

let paymentRequest = {
    Amount = 50.00m
    Currency = "usd"
    CustomerEmail = "test@example.com"
    CustomerName = "Test User"
    IdempotencyKey = "order-1042"
}

[<Fact>]
let ``createPaymentEndpoint retried with the same key creates nothing new`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        let! first = createPaymentEndpoint stripe.Settings paymentRequest
        let! retry = createPaymentEndpoint stripe.Settings paymentRequest
        Assert.True(first.Success)
        Assert.Equal(first, retry)

        let keys = stripe.Requests |> List.map (fun request -> request.IdempotencyKey, request.Replayed)
        Assert.Equal<(string option * bool) list>(
            [ Some "order-1042:customer", false
              Some "order-1042:payment-intent", false
              Some "order-1042:customer", true
              Some "order-1042:payment-intent", true ],
            keys)
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``createPaymentEndpoint with another key is a new operation`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        let! _ = createPaymentEndpoint stripe.Settings paymentRequest
        let! _ = createPaymentEndpoint stripe.Settings { paymentRequest with IdempotencyKey = "order-1043" }
        Assert.Equal(4, stripe.Requests.Length)
        Assert.DoesNotContain(stripe.Requests, fun request -> request.Replayed)
    } |> Async.StartImmediateAsTask :> Task

[<Theory; InlineData(null); InlineData(""); InlineData("   ")>]
let ``endpoints reject a request without idempotency key before calling Stripe`` (key: string) =
    async {
        use stripe = new FakeStripe(respondOk)
        let! payment = createPaymentEndpoint stripe.Settings { paymentRequest with IdempotencyKey = key }
        let! setup =
            createSetupEndpoint stripe.Settings { CustomerEmail = "a@example.com"; CustomerName = "A B"; IdempotencyKey = key }
        Assert.Equal(Some "Idempotency key is required", payment.Error)
        Assert.Equal(Some "Idempotency key is required", setup.Error)
        Assert.Empty(stripe.Requests)
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``createSetupEndpoint sends one key per step`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        let! _ = createSetupEndpoint stripe.Settings { CustomerEmail = "a@example.com"; CustomerName = "A B"; IdempotencyKey = "signup-77" }
        let keys = stripe.Requests |> List.map (fun request -> request.IdempotencyKey)
        Assert.Equal<string option list>([ Some "signup-77:customer"; Some "signup-77:setup-intent" ], keys)
    } |> Async.StartImmediateAsTask :> Task
