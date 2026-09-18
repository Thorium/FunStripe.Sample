module IntegrationExampleTests

open Xunit
open IntegrationExample
open StripeService
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
        let config = loadStripeConfig ()
        let request = {
            Amount = 50.00m
            Currency = "usd"
            CustomerEmail = "test@example.com"
            CustomerName = "Test User"
        }
        let! response = createPaymentEndpoint config request
        Assert.True(response.Success)
        match response.Data with
        | Some data ->
            Assert.StartsWith("pi_mock_", data.PaymentIntentId)
            Assert.StartsWith("cus_mock_", data.CustomerId)
            Assert.NotEmpty(data.ClientSecret)
        | None -> Assert.Fail("Expected Data to be Some")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``createPaymentEndpoint handles single-word name`` () =
    async {
        let config = loadStripeConfig ()
        let request = {
            Amount = 10.00m
            Currency = "eur"
            CustomerEmail = "mono@example.com"
            CustomerName = "Cher"
        }
        let! response = createPaymentEndpoint config request
        Assert.True(response.Success)
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``createPaymentEndpoint handles null name`` () =
    async {
        let config = loadStripeConfig ()
        let request = {
            Amount = 10.00m
            Currency = "gbp"
            CustomerEmail = "anon@example.com"
            CustomerName = null
        }
        let! response = createPaymentEndpoint config request
        Assert.True(response.Success)
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// createSetupEndpoint tests
// =============================================================================

[<Fact>]
let ``createSetupEndpoint returns success for valid request`` () =
    async {
        let config = loadStripeConfig ()
        let request = {
            CustomerEmail = "setup@example.com"
            CustomerName = "Setup User"
        }
        let! response = createSetupEndpoint config request
        Assert.True(response.Success)
        match response.Data with
        | Some data ->
            Assert.StartsWith("seti_mock_", data.SetupIntentId)
            Assert.StartsWith("cus_mock_", data.CustomerId)
            Assert.NotEmpty(data.ClientSecret)
        | None -> Assert.Fail("Expected Data to be Some")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``createSetupEndpoint handles null name`` () =
    async {
        let config = loadStripeConfig ()
        let request = {
            CustomerEmail = "noname@example.com"
            CustomerName = null
        }
        let! response = createSetupEndpoint config request
        Assert.True(response.Success)
    } |> Async.StartImmediateAsTask :> Task
