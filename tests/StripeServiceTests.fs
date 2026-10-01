module StripeServiceTests

open Xunit
open FunStripe
open FunStripe.IsoTypes
open StripeService
open FakeStripeServer
open System
open System.Threading.Tasks

/// The idempotency key of one step of the operation "op-1"
let key (step: string) =
    idempotencyKeyFor (OperationId.tryCreate "op-1" |> Option.get) step

// =============================================================================
// formatAmount tests
// =============================================================================

[<Fact>]
let ``formatAmount converts whole dollars`` () =
    Assert.Equal(2000, formatAmount 20.00m)

[<Fact>]
let ``formatAmount converts dollars with cents`` () =
    Assert.Equal(2550, formatAmount 25.50m)

[<Fact>]
let ``formatAmount converts zero`` () =
    Assert.Equal(0, formatAmount 0m)

[<Fact>]
let ``formatAmount converts one cent`` () =
    Assert.Equal(1, formatAmount 0.01m)

[<Fact>]
let ``formatAmount converts large amount`` () =
    Assert.Equal(999999, formatAmount 9999.99m)

[<Fact>]
let ``formatAmount rounds fractional cents instead of truncating`` () =
    Assert.Equal(2000, formatAmount 19.999m)

// =============================================================================
// parseCurrency tests
// =============================================================================

[<Fact>]
let ``parseCurrency accepts lower and upper case codes`` () =
    Assert.Equal(Some IsoCurrencyCode.USD, parseCurrency "usd")
    Assert.Equal(Some IsoCurrencyCode.EUR, parseCurrency "EUR")

[<Fact>]
let ``parseCurrency rejects unknown, empty and null codes`` () =
    Assert.Equal(None, parseCurrency "dollars")
    Assert.Equal(None, parseCurrency "")
    Assert.Equal(None, parseCurrency null)

// =============================================================================
// loadStripeConfig tests (integration -- reads appsettings.json)
// =============================================================================

[<Fact>]
let ``loadStripeConfig reads config without throwing`` () =
    // appsettings.json is copied to output directory via the fsproj
    let config = loadStripeConfig ()
    Assert.NotNull(config.PublishableKey)
    Assert.NotNull(config.SecretKey)
    Assert.NotNull(config.WebhookEndpointSecret)

[<Fact>]
let ``loadStripeConfig returns placeholder values from sample config`` () =
    let config = loadStripeConfig ()
    // The sample appsettings.json ships with placeholder values
    Assert.StartsWith("pk_test_", config.PublishableKey)
    Assert.StartsWith("whsec_", config.WebhookEndpointSecret)

[<Fact>]
let ``keyMode tells placeholder, test and live keys apart`` () =
    let config = { PublishableKey = "pk_test_..."; SecretKey = "sk_test_..."; WebhookEndpointSecret = "whsec_..." }
    Assert.Equal(NotConfigured, keyMode config)
    Assert.Equal(NotConfigured, keyMode { config with SecretKey = null })
    Assert.Equal(NotConfigured, keyMode { config with SecretKey = "sk_live_..." })
    Assert.Equal(TestMode, keyMode { config with SecretKey = "sk_test_51Abc" })
    Assert.Equal(TestMode, keyMode { config with SecretKey = "rk_test_51Abc" })
    Assert.Equal(LiveMode, keyMode { config with SecretKey = "sk_live_51Abc" })

[<Fact>]
let ``createSettings pins the Stripe API version of the FunStripe models`` () =
    let settings = createSettings { PublishableKey = "pk_test_x"; SecretKey = "sk_test_x"; WebhookEndpointSecret = "whsec_x" }
    Assert.Equal("sk_test_x", settings.ApiKey)
    Assert.Equal(Some Config.DefaultStripeApiVersion, settings.StripeVersion)

// =============================================================================
// Request encoding tests (what FunStripe puts in the form body)
// =============================================================================

[<Fact>]
let ``paymentIntentOptions encodes amount, currency, customer and allowed card payments`` () =
    let form = paymentIntentOptions 5000 IsoCurrencyCode.USD (Some "cus_test_002") |> Util.serialiseForm |> Map.ofSeq
    Assert.Equal("5000", form.["amount"])
    Assert.Equal("usd", form.["currency"])
    Assert.Equal("cus_test_002", form.["customer"])
    Assert.Equal("card", form.["allowed_payment_method_types[0]"])
    // Removed by Stripe in the 2026-09-30.endive API
    Assert.DoesNotContain(form.Keys, fun key -> key.StartsWith "payment_method_types")

[<Fact>]
let ``paymentIntentOptions leaves the customer out when there is none`` () =
    let form = paymentIntentOptions 1000 IsoCurrencyCode.EUR None |> Util.serialiseForm |> Map.ofSeq
    Assert.Equal("eur", form.["currency"])
    Assert.False(form.ContainsKey "customer")

[<Fact>]
let ``setupIntentOptions encodes customer, off-session usage and allowed card payments`` () =
    let form = setupIntentOptions "cus_test_001" |> Util.serialiseForm |> Map.ofSeq
    Assert.Equal("cus_test_001", form.["customer"])
    Assert.Equal("off_session", form.["usage"])
    Assert.Equal("card", form.["allowed_payment_method_types[0]"])

// =============================================================================
// createCustomer tests
// =============================================================================

[<Fact>]
let ``createCustomer posts name and email and returns the customer`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        match! createCustomer stripe.Settings (key "customer") "Alice" "Smith" "alice@example.com" with
        | Ok customer ->
            Assert.Equal("cus_FakeCustomer", customer.Id)
            Assert.Equal(Some "alice@example.com", customer.Email)
            Assert.Equal(Some "Alice Smith", customer.Name)
        | Error err -> Assert.Fail($"Expected Ok, got Error: {describeStripeError err}")

        let request = Assert.Single stripe.Requests
        Assert.Equal("POST", request.Method)
        Assert.Equal("/v1/customers", request.Path)
        Assert.Equal("alice@example.com", request.Form.["email"])
        Assert.Equal("Alice Smith", request.Form.["name"])
        Assert.Equal(Some Config.DefaultStripeApiVersion, request.StripeVersion)
        Assert.Equal(Some "op-1:customer", request.IdempotencyKey)
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``createCustomer returns the Stripe error`` () =
    async {
        use stripe = new FakeStripe(respondError)
        match! createCustomer stripe.Settings (key "customer") "A" "B" "not-an-email" with
        | Ok customer -> Assert.Fail($"Expected Error, got customer {customer.Id}")
        | Error err ->
            Assert.Equal(Some "email_invalid", err.StripeError.Code)
            Assert.Equal(Some StripeError.ErrorType.InvalidRequestError, err.StripeError.Type)
            Assert.Equal("Invalid email address: not-an-email (code: email_invalid)", describeStripeError err)
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// createSetupIntent tests
// =============================================================================

[<Fact>]
let ``createSetupIntent returns the setup intent with its client secret`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        match! createSetupIntent stripe.Settings (key "setup-intent") "cus_test_001" with
        | Ok setupIntent ->
            Assert.Equal("seti_FakeSetup", setupIntent.Id)
            Assert.Equal(Some "seti_FakeSetup_secret_fake", setupIntent.ClientSecret)
        | Error err -> Assert.Fail($"Expected Ok, got Error: {describeStripeError err}")

        let request = Assert.Single stripe.Requests
        Assert.Equal("/v1/setup_intents", request.Path)
        Assert.Equal("cus_test_001", request.Form.["customer"])
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// createPaymentIntent tests
// =============================================================================

[<Fact>]
let ``createPaymentIntent returns the payment intent with amount and currency`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        match! createPaymentIntent stripe.Settings (key "payment-intent") 5000 IsoCurrencyCode.USD (Some "cus_test_002") with
        | Ok paymentIntent ->
            Assert.Equal("pi_FakePayment", paymentIntent.Id)
            Assert.Equal(Some "pi_FakePayment_secret_fake", paymentIntent.ClientSecret)
            Assert.Equal(5000, paymentIntent.Amount)
            Assert.Equal(IsoCurrencyCode.USD, paymentIntent.Currency)
            Assert.Equal(Stripe.PaymentMethod.PaymentIntentStatus.RequiresPaymentMethod, paymentIntent.Status)
        | Error err -> Assert.Fail($"Expected Ok, got Error: {describeStripeError err}")

        let request = Assert.Single stripe.Requests
        Assert.Equal("/v1/payment_intents", request.Path)
        Assert.Equal("5000", request.Form.["amount"])
        Assert.Equal("usd", request.Form.["currency"])
        Assert.Equal("card", request.Form.["allowed_payment_method_types[0]"])
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``tryFormatAmount accepts amounts Stripe can charge`` () =
    Assert.Equal(Some 50, tryFormatAmount 0.50m)
    Assert.Equal(Some 99999999, tryFormatAmount 999999.99m)
    Assert.Equal(None, tryFormatAmount 0m)
    Assert.Equal(None, tryFormatAmount -1m)
    Assert.Equal(None, tryFormatAmount 0.004m)
    Assert.Equal(None, tryFormatAmount 1000000m)

// =============================================================================
// Idempotency tests
// =============================================================================

[<Fact>]
let ``OperationId.tryCreate rejects blank and overlong IDs`` () =
    Assert.True((OperationId.tryCreate "order-1042").IsSome)
    Assert.Equal(None, OperationId.tryCreate null)
    Assert.Equal(None, OperationId.tryCreate "  ")
    Assert.Equal(None, OperationId.tryCreate (String('x', 201)))

[<Fact>]
let ``idempotencyKeyFor gives each step of an operation its own stable key`` () =
    let order1042 = OperationId.tryCreate "order-1042" |> Option.get
    let order1043 = OperationId.tryCreate "order-1043" |> Option.get
    Assert.Equal(idempotencyKeyFor order1042 "customer", idempotencyKeyFor order1042 "customer")
    Assert.NotEqual(idempotencyKeyFor order1042 "customer", idempotencyKeyFor order1042 "payment-intent")
    Assert.NotEqual(idempotencyKeyFor order1042 "customer", idempotencyKeyFor order1043 "customer")

[<Fact>]
let ``every create call sends its idempotency key`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        let! _ = createCustomer stripe.Settings (key "customer") "Alice" "Smith" "alice@example.com"
        let! _ = createSetupIntent stripe.Settings (key "setup-intent") "cus_FakeCustomer"
        let! _ = createPaymentIntent stripe.Settings (key "payment-intent") 5000 IsoCurrencyCode.USD None
        let keys = stripe.Requests |> List.map (fun request -> request.IdempotencyKey)
        Assert.Equal<string option list>([ Some "op-1:customer"; Some "op-1:setup-intent"; Some "op-1:payment-intent" ], keys)
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``a create call repeated with its key is answered with the first response`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        let! first = createCustomer stripe.Settings (key "customer") "Alice" "Smith" "alice@example.com"
        let! retry = createCustomer stripe.Settings (key "customer") "Alice" "Smith" "alice@example.com"
        Assert.Equal(first, retry)
        Assert.Equal<bool list>([ false; true ], stripe.Requests |> List.map (fun request -> request.Replayed))
    } |> Async.StartImmediateAsTask :> Task
