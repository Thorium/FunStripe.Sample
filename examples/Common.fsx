// Shared setup of the example scripts: package references, the sample's shared code and the
// Stripe settings. Each example loads this, then the module of its own use case.
//
// The examples run against Stripe test mode and need a test secret key:
//
//   export STRIPE_TEST_API_KEY=sk_test_...      (bash)
//   $env:STRIPE_TEST_API_KEY = "sk_test_..."    (PowerShell)

#r "nuget: FunStripe.Core, 3.0.0"
#r "nuget: Microsoft.Extensions.Configuration, 10.0.10"
#r "nuget: Microsoft.Extensions.Configuration.Json, 10.0.10"

#load "../src/StripeService.fs"
#load "../webhooks/Events.fs"
#load "../webhooks/WebhookHandler.fs"
#load "../webhooks/WebhookSamples.fs"

open System
open FunStripe
open StripeService

/// The settings of the test key in STRIPE_TEST_API_KEY. The examples create objects in your
/// Stripe account, so they refuse to run with a live key.
let settings =
    let config = { PublishableKey = ""; SecretKey = Config.StripeTestApiKey; WebhookEndpointSecret = "" }
    match keyMode config with
    | TestMode -> createSettings config
    | NotConfigured ->
        eprintfn "Set the STRIPE_TEST_API_KEY environment variable to a test key from https://dashboard.stripe.com/test/apikeys"
        exit 1
    | LiveMode ->
        eprintfn "STRIPE_TEST_API_KEY holds a live key. The examples only run with a test key."
        exit 1

/// A new operation for this run of the script. A real application derives the operation ID
/// from something it stores, such as an order ID (see the README on idempotency).
let newOperation (name: string) =
    OperationId.tryCreate $"example-{name}-{Guid.NewGuid()}" |> Option.get

/// The arguments after `--`, e.g. `dotnet fsi examples/FinancialConnections.fsx -- fcsess_...`
let scriptArgs = fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray

/// Runs one step and stops the script when Stripe answers with an error
let step (description: string) (work: Async<Result<'T, StripeError.ErrorResponse>>) =
    printf $"{description}... "
    match Async.RunSynchronously work with
    | Ok value ->
        printfn "OK"
        value
    | Error error ->
        printfn "FAILED"
        eprintfn $"Stripe: {describeStripeError error}"
        exit 1

/// A value the API may leave out, for printing
let orNone (value: string option) =
    match value with
    | Some value -> value
    | None -> "(none)"
