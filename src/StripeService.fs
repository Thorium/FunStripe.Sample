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
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional = false, reloadOnChange = false)
            .Build()

    let environment = config.["Environment"]
    let stripe = config.GetSection("Stripe")

    let publishableKey, secretKey =
        match environment with
        | "Test" | "test" ->
            stripe.["TestPublishableKey"], stripe.["TestSecretKey"]
        | _ ->
            stripe.["LivePublishableKey"], stripe.["LiveSecretKey"]

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
                ClientSecret = $"seti_mock_{Guid.NewGuid().ToString()}_secret"
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
                ClientSecret = $"pi_mock_{Guid.NewGuid().ToString()}_secret"
                Amount = amount
                Currency = currency
                CustomerId = customerId
            |}

            return Ok mockPaymentIntent
        with
        | ex ->
            return Error $"Mock error: {ex.Message}"
    }

/// Helper function to format amounts (Stripe uses smallest currency unit)
let formatAmount (dollars: decimal) = int64 (dollars * 100m)

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
///   open FunStripe.StripeModel
///   open FunStripe.StripeRequest
///
/// Key functions in a real implementation could be something like:
/// - Stripe.createCustomer : Account -> Guid -> string -> string -> string -> Async<Result<Customer, StripeError>>
/// - Stripe.createSetupIntent : Account -> string -> Async<Result<SetupIntent, StripeError>>
/// - Stripe.createPaymentIntent : Account -> int64 -> string -> CustomerId option -> Async<Result<PaymentIntent, StripeError>>
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
