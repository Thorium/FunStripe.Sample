module StripeService

open System
open System.IO
open Microsoft.Extensions.Configuration

/// Configuration for Stripe accounts
type StripeConfig = {
    PublishableKey: string
    SecretKey: string
    WebhookEndpointSecret: string
}

/// Load Stripe configuration from appsettings.json
let loadStripeConfig () =
    let config =
        ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional = false, reloadOnChange = false)
            .Build()

    let environment = config.["Environment"]
    let stripe = config.GetSection "Stripe"

    // Live keys require an explicit opt-in; anything else (including a missing
    // or misspelled Environment value) falls back to the test keys.
    let publishableKey, secretKey =
        match environment with
        | "Live" | "live" | "Production" | "production" ->
            stripe.["LivePublishableKey"], stripe.["LiveSecretKey"]
        | _ ->
            stripe.["TestPublishableKey"], stripe.["TestSecretKey"]

    {
        PublishableKey = publishableKey
        SecretKey = secretKey
        WebhookEndpointSecret = stripe.["WebhookEndpointSecret"]
    }

/// Creates a new Stripe customer
/// This is a mock implementation showing the expected pattern.
/// In a real application, this would call FunStripe internally.
let createCustomer (config: StripeConfig) (firstName: string) (lastName: string) (email: string) =
    async {
        try
            printfn $"Mock: Creating customer {firstName} {lastName} ({email})"

            let mockCustomer = {|
                Id = $"cus_mock_{Guid.NewGuid().ToString().Substring(0, 8)}"
                Email = email
                Name = $"{firstName} {lastName}"
            |}

            return Ok mockCustomer
        with
        | ex ->
            return Error $"Mock error: {ex.Message}"
    }

/// Creates a setup intent for saving a payment method
let createSetupIntent (config: StripeConfig) (customerId: string) =
    async {
        try
            printfn $"Mock: Creating setup intent for customer {customerId}"

            let mockSetupIntent = {|
                Id = $"seti_mock_{Guid.NewGuid().ToString().Substring(0, 8)}"
                ClientSecret = $"seti_mock_{Guid.NewGuid()}_secret"
                CustomerId = customerId
            |}

            return Ok mockSetupIntent
        with
        | ex ->
            return Error $"Mock error: {ex.Message}"
    }

/// Creates a payment intent for processing a one-time payment
let createPaymentIntent (config: StripeConfig) (amount: int64) (currency: string) (customerId: string option) =
    async {
        try
            printfn $"Mock: Creating payment intent for {amount} {currency}"
            match customerId with
            | Some id -> printfn $"  Customer: {id}"
            | None -> ()

            let mockPaymentIntent = {|
                Id = $"pi_mock_{Guid.NewGuid().ToString().Substring(0, 8)}"
                ClientSecret = $"pi_mock_{Guid.NewGuid()}_secret"
                Amount = amount
                Currency = currency
                CustomerId = customerId
            |}

            return Ok mockPaymentIntent
        with
        | ex ->
            return Error $"Mock error: {ex.Message}"
    }

/// Helper function to format amounts (Stripe uses the smallest currency unit).
/// Only valid for two-decimal currencies (USD, EUR, GBP, ...); zero-decimal
/// currencies such as JPY must not be multiplied by 100.
let formatAmount (dollars: decimal) = int64 (Math.Round(dollars * 100m))

/// Helper function to handle Stripe errors (simplified)
let handleStripeError (error: string) =
    printfn $"Stripe Error: {error}"

/// Important Note about Real Implementation
/// ====================================
///
/// This sample uses mock implementations to demonstrate the patterns and structure.
/// In a real application, you would import and use FunStripe types:
///
///   open FunStripe
///   open FunStripe.StripeRequest
///
/// The real API shape is `<module>.<Method> settings options`, e.g.:
///
///   let settings = RestApi.StripeApiSettings.New(apiKey = config.SecretKey)
///
///   Customers.CreateOptions.New(email = email, name = $"{firstName} {lastName}")
///   |> Customers.Create settings
///   // : Async<Result<StripeModel.Customer, StripeError.ErrorResponse>>
///
/// SetupIntents.Create and PaymentIntents.Create follow the same pattern.
///
/// To create a production version:
/// 1. Use FunStripe directly for Stripe API calls
/// 2. Add proper error handling and logging
/// 3. Add database integration for customer/payment tracking
/// 4. Add webhook signature verification
/// 5. Add business logic for order processing
///
/// See the webhooks/ directory for examples of handling Stripe events
/// See the frontend/ directory for Stripe Elements integration
