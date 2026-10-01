module LiveTests

open System
open System.Threading.Tasks
open Xunit
open FunStripe
open FunStripe.AsyncResultCE
open FunStripe.IsoTypes
open Stripe.PaymentMethod
open StripeRequest.Customers
open StripeService

// These tests call the real Stripe API in test mode. They run only when the
// STRIPE_TEST_API_KEY environment variable holds a test key, and are skipped otherwise.

type LiveFactAttribute() as this =
    inherit FactAttribute()
    do
        if String.IsNullOrWhiteSpace Config.StripeTestApiKey then
            this.Skip <- "STRIPE_TEST_API_KEY is not set"
        elif not (Config.StripeTestApiKey.Contains("_test_", StringComparison.Ordinal)) then
            this.Skip <- "STRIPE_TEST_API_KEY is not a test-mode key"

let settings =
    RestApi.StripeApiSettings.New(apiKey = Config.StripeTestApiKey, stripeVersion = Config.DefaultStripeApiVersion)

[<LiveFact>]
let ``customer, setup intent and payment intent are created in Stripe test mode`` () =
    async {
        // A new operation per test run
        let operationId = OperationId.tryCreate $"live-test-{Guid.NewGuid()}" |> Option.get
        let customerKey = idempotencyKeyFor operationId "customer"

        match! createCustomer settings customerKey "Live" "Test" "live.test+funstripe-sample@example.com" with
        | Error error -> Assert.Fail($"Stripe returned an error: {describeStripeError error}")
        | Ok customer ->
            // The same key again: Stripe answers with the customer it already created
            let! retried = createCustomer settings customerKey "Live" "Test" "live.test+funstripe-sample@example.com"

            let! intents =
                asyncResult {
                    let! setupIntent = createSetupIntent settings (idempotencyKeyFor operationId "setup-intent") customer.Id
                    let! paymentIntent =
                        createPaymentIntent settings (idempotencyKeyFor operationId "payment-intent") 2000 IsoCurrencyCode.USD (Some customer.Id)
                    return setupIntent, paymentIntent
                }

            // Leave nothing behind in the test account, whatever happened above
            let! deleted = Customers.DeleteOptions.New(customer = customer.Id) |> Customers.Delete settings

            Assert.StartsWith("cus_", customer.Id)
            Assert.Equal(Some "Live Test", customer.Name)

            match retried with
            | Ok retriedCustomer -> Assert.Equal(customer.Id, retriedCustomer.Id)
            | Error error -> Assert.Fail($"The retry returned an error: {describeStripeError error}")

            match intents with
            | Ok (setupIntent, paymentIntent) ->
                Assert.StartsWith("seti_", setupIntent.Id)
                Assert.True(setupIntent.ClientSecret.IsSome)
                Assert.Equal(Some (SetupIntentCustomer'AnyOf.String customer.Id), setupIntent.Customer)
                Assert.StartsWith("pi_", paymentIntent.Id)
                Assert.Equal(2000, paymentIntent.Amount)
                Assert.Equal(IsoCurrencyCode.USD, paymentIntent.Currency)
                Assert.Equal(PaymentIntentStatus.RequiresPaymentMethod, paymentIntent.Status)
            | Error error -> Assert.Fail($"Stripe returned an error: {describeStripeError error}")

            match deleted with
            | Ok deletedCustomer -> Assert.True(deletedCustomer.Deleted)
            | Error error -> Assert.Fail($"Could not delete the customer: {describeStripeError error}")
    } |> Async.StartImmediateAsTask :> Task
