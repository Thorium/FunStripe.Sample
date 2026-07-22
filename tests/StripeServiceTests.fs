module StripeServiceTests

open Xunit
open StripeService

// =============================================================================
// formatAmount tests
// =============================================================================

[<Fact>]
let ``formatAmount converts whole dollars`` () =
    Assert.Equal(2000L, formatAmount 20.00m)

[<Fact>]
let ``formatAmount converts dollars with cents`` () =
    Assert.Equal(2550L, formatAmount 25.50m)

[<Fact>]
let ``formatAmount converts zero`` () =
    Assert.Equal(0L, formatAmount 0m)

[<Fact>]
let ``formatAmount converts one cent`` () =
    Assert.Equal(1L, formatAmount 0.01m)

[<Fact>]
let ``formatAmount converts large amount`` () =
    Assert.Equal(999999L, formatAmount 9999.99m)

[<Fact>]
let ``formatAmount rounds fractional cents instead of truncating`` () =
    Assert.Equal(2000L, formatAmount 19.999m)

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
    Assert.StartsWith("sk_test_", config.SecretKey)
    Assert.StartsWith("whsec_", config.WebhookEndpointSecret)

// =============================================================================
// createCustomer tests
// =============================================================================

[<Fact>]
let ``createCustomer returns Ok with correct fields`` () =
    async {
        let config = loadStripeConfig ()
        let! result = createCustomer config "Alice" "Smith" "alice@example.com"
        match result with
        | Ok customer ->
            Assert.StartsWith("cus_mock_", customer.Id)
            Assert.Equal("alice@example.com", customer.Email)
            Assert.Equal("Alice Smith", customer.Name)
        | Error err -> Assert.Fail($"Expected Ok, got Error: {err}")
    } |> Async.RunSynchronously

[<Fact>]
let ``createCustomer generates unique IDs`` () =
    async {
        let config = loadStripeConfig ()
        let! result1 = createCustomer config "A" "B" "a@b.com"
        let! result2 = createCustomer config "C" "D" "c@d.com"
        match result1, result2 with
        | Ok c1, Ok c2 -> Assert.NotEqual<string>(c1.Id, c2.Id)
        | _ -> Assert.Fail("Expected both Ok")
    } |> Async.RunSynchronously

// =============================================================================
// createSetupIntent tests
// =============================================================================

[<Fact>]
let ``createSetupIntent returns Ok with correct fields`` () =
    async {
        let config = loadStripeConfig ()
        let! result = createSetupIntent config "cus_test_001"
        match result with
        | Ok setupIntent ->
            Assert.StartsWith("seti_mock_", setupIntent.Id)
            Assert.Contains("_secret", setupIntent.ClientSecret)
            Assert.Equal("cus_test_001", setupIntent.CustomerId)
        | Error err -> Assert.Fail($"Expected Ok, got Error: {err}")
    } |> Async.RunSynchronously

// =============================================================================
// createPaymentIntent tests
// =============================================================================

[<Fact>]
let ``createPaymentIntent returns Ok with correct amount and currency`` () =
    async {
        let config = loadStripeConfig ()
        let! result = createPaymentIntent config 5000L "usd" (Some "cus_test_002")
        match result with
        | Ok paymentIntent ->
            Assert.StartsWith("pi_mock_", paymentIntent.Id)
            Assert.Contains("_secret", paymentIntent.ClientSecret)
            Assert.Equal(5000L, paymentIntent.Amount)
            Assert.Equal("usd", paymentIntent.Currency)
            Assert.Equal(Some "cus_test_002", paymentIntent.CustomerId)
        | Error err -> Assert.Fail($"Expected Ok, got Error: {err}")
    } |> Async.RunSynchronously

[<Fact>]
let ``createPaymentIntent works without customer`` () =
    async {
        let config = loadStripeConfig ()
        let! result = createPaymentIntent config 1000L "eur" None
        match result with
        | Ok paymentIntent ->
            Assert.Equal(None, paymentIntent.CustomerId)
            Assert.Equal("eur", paymentIntent.Currency)
        | Error err -> Assert.Fail($"Expected Ok, got Error: {err}")
    } |> Async.RunSynchronously
