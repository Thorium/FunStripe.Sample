module StripeService

open System
open Microsoft.Extensions.Configuration
open Microsoft.FSharp.Reflection
open FunStripe
open FunStripe.IsoTypes
open Stripe.PaymentMethod
open StripeRequest.Customers
open StripeRequest.Setup
open StripeRequest.Payment

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
            // Same convention as FunStripe itself: the STRIPE_TEST_API_KEY environment
            // variable wins, so a test key never has to be written to a file.
            let testSecretKey =
                if String.IsNullOrWhiteSpace Config.StripeTestApiKey then stripe.["TestSecretKey"]
                else Config.StripeTestApiKey
            stripe.["TestPublishableKey"], testSecretKey

    {
        PublishableKey = publishableKey
        SecretKey = secretKey
        WebhookEndpointSecret = stripe.["WebhookEndpointSecret"]
    }

/// What the configured secret key gives access to
type KeyMode =
    /// No key, or one of the "sk_test_..." placeholders of the repository's appsettings.json
    | NotConfigured
    /// A test-mode key (sk_test_..., rk_test_...), which cannot touch live data
    | TestMode
    | LiveMode

let keyMode (config: StripeConfig) =
    match config.SecretKey with
    | key when String.IsNullOrWhiteSpace key || key.EndsWith("...", StringComparison.Ordinal) -> NotConfigured
    | key when key.Contains("_test_", StringComparison.Ordinal) -> TestMode
    | _ -> LiveMode

/// FunStripe settings for the configured account.
/// The Stripe-Version header is pinned to the API version the FunStripe models were
/// generated from, so responses match the types whatever the account's default version is.
let createSettings (config: StripeConfig) =
    RestApi.StripeApiSettings.New(apiKey = config.SecretKey, stripeVersion = Config.DefaultStripeApiVersion)

/// The ISO 4217 codes FunStripe knows, by their upper-case name
let private currencies =
    FSharpType.GetUnionCases typeof<IsoCurrencyCode>
    |> Array.map (fun case -> case.Name, FSharpValue.MakeUnion(case, [||]) :?> IsoCurrencyCode)
    |> Map.ofArray

/// Parses an ISO 4217 code such as "usd" or "EUR" into FunStripe's typed currency
let parseCurrency (code: string) =
    code
    |> Option.ofObj
    |> Option.bind (fun code -> currencies |> Map.tryFind (code.Trim().ToUpperInvariant()))

/// Helper function to format amounts (Stripe uses the smallest currency unit).
/// Only valid for two-decimal currencies (USD, EUR, GBP, ...); zero-decimal
/// currencies such as JPY must not be multiplied by 100.
let formatAmount (dollars: decimal) = int (Math.Round(dollars * 100m))

/// `formatAmount` for untrusted input: None unless the amount is positive, also after
/// rounding, and within the 8 digits Stripe accepts (999,999.99 in a two-decimal currency)
let tryFormatAmount (dollars: decimal) =
    if dollars > 0m && dollars <= 999999.99m then
        match formatAmount dollars with
        | 0 -> None
        | amount -> Some amount
    else
        None

// Idempotency
// ===========
// Every create call below takes an idempotency key. Stripe keeps the response to the first
// request with a key for 24 hours and answers a repeat with it, so a call that is retried
// after a timeout or a crash cannot create a second customer or payment.
//
// A key has to be the same for every retry of an operation and different for any other
// operation. Derive it from an ID your application stores for the operation (an order ID,
// or the Idempotency-Key header your own client sent), never from a fresh GUID per attempt.

/// Identifies one operation of the application, such as placing an order
type OperationId = private OperationId of string

module OperationId =

    /// None for a blank ID and for one over 200 characters: Stripe accepts keys of up to
    /// 255 characters, and `idempotencyKeyFor` appends the step to the ID.
    let tryCreate (value: string) =
        if String.IsNullOrWhiteSpace value || value.Length > 200 then None
        else Some (OperationId value)

/// The idempotency key of one Stripe call. Being a type of its own, it cannot be blank and
/// cannot be mixed up with the other strings a call takes.
type IdempotencyKey = private IdempotencyKey of string

/// The idempotency key of one Stripe call within an operation. Stripe ties a key to the
/// endpoint and the parameters of its first use, so each call of an operation gets its own
/// step: "customer", "payment-intent", ...
let idempotencyKeyFor (OperationId operationId) (step: string) =
    IdempotencyKey $"{operationId}:{step}"

/// The settings for the one request that carries the key
let private withKey (IdempotencyKey key) (settings: RestApi.StripeApiSettings) =
    settings.WithIdempotencyKey key

/// Request options for a new customer
let customerOptions (firstName: string) (lastName: string) (email: string) =
    Customers.CreateOptions.New(
        email = email,
        name = $"{firstName} {lastName}".Trim(),
        description = "FunStripe sample customer"
    )

/// Creates a new Stripe customer
let createCustomer (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (firstName: string) (lastName: string) (email: string) =
    customerOptions firstName lastName email
    |> Customers.Create (settings |> withKey idempotencyKey)

/// Request options for a setup intent that saves a card for later off-session use.
/// `allowedPaymentMethodTypes` replaces the `payment_method_types` parameter, which
/// Stripe removed in the 2026-09-30.endive API. It filters the payment methods Stripe
/// computes dynamically, so the card is offered only if it is also eligible.
let setupIntentOptions (customerId: string) =
    SetupIntents.CreateOptions.New(
        customer = customerId,
        allowedPaymentMethodTypes = [SetupIntents.Create'AllowedPaymentMethodTypes.Card],
        usage = SetupIntents.Create'Usage.OffSession
    )

/// Creates a setup intent for saving a payment method
let createSetupIntent (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (customerId: string) =
    setupIntentOptions customerId
    |> SetupIntents.Create (settings |> withKey idempotencyKey)

/// Request options for a one-time card payment. The amount is in the smallest
/// currency unit (see `formatAmount`).
let paymentIntentOptions (amount: int) (currency: IsoCurrencyCode) (customerId: string option) =
    PaymentIntents.CreateOptions.New(
        amount = amount,
        currency = currency,
        ?customer = customerId,
        allowedPaymentMethodTypes = [PaymentIntents.Create'AllowedPaymentMethodTypes.Card]
    )

/// Creates a payment intent for processing a one-time payment
let createPaymentIntent (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (amount: int) (currency: IsoCurrencyCode) (customerId: string option) =
    paymentIntentOptions amount currency customerId
    |> PaymentIntents.Create (settings |> withKey idempotencyKey)

/// The message Stripe returned, with its error code when there is one
let describeStripeError (error: StripeError.ErrorResponse) =
    let message = error.StripeError.Message |> Option.defaultValue "Unknown error"
    match error.StripeError.Code with
    | Some code -> $"{message} (code: {code})"
    | None -> message

/// Helper function to handle Stripe errors (simplified)
let handleStripeError (error: StripeError.ErrorResponse) =
    printfn $"Stripe Error: {describeStripeError error}"

/// To turn this into a production service:
/// 1. Add logging and retries (the idempotency keys make it safe to retry a create)
/// 2. Add database integration for customer/payment tracking, including the operation IDs
///    the idempotency keys are derived from
/// 3. Add business logic for order processing
///
/// See the webhooks/ directory for handling Stripe events
/// See the frontend/ directory for Stripe Elements integration
