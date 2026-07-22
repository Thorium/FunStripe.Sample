# FunStripe / FunStripeLite Integration Guide

[**FunStripe**](https://github.com/simontreanor/FunStripe) is an F# library that provides a functional wrapper around the [Stripe](https://stripe.com/) API for payment processing. This guide demonstrates the essential patterns for integrating Stripe payments in your application.

This repository uses the FunStripeLite NuGet package, but usage is identical to full FunStripe, just with a few dependencies removed. This repository is not using the official Stripe.net .NET integration, as avoiding that is one of the key aims of the alternative, FunStripe.

> **Note:** The F# code in `src/` and `webhooks/` uses *mock* implementations so the sample runs out of the box without real API keys — it demonstrates the structure and patterns (service layer, webhook verification, error handling) rather than making live calls. The code snippets in this README show the **real FunStripeLite API** you would use in production.

## Overview

This sample covers the most important use cases for a minimum viable product (MVP):

- Creating Stripe customers
- Setting up payment methods with Setup Intents
- Processing one-time payments with Payment Intents
- Handling webhooks for payment events
- Frontend integration with Stripe Elements

## Prerequisites

- .NET 10.0 or later
- Stripe account (test keys for development)
- FunStripeLite NuGet package

## Quick Start

### 1. Configuration

Configure your Stripe keys in `src/appsettings.json`:

```json
{
  "Stripe": {
    "TestPublishableKey": "pk_test_your_key_here",
    "TestSecretKey": "sk_test_your_key_here",
    "LivePublishableKey": "pk_live_...",
    "LiveSecretKey": "sk_live_...",
    "WebhookEndpointSecret": "whsec_your_secret_here"
  },
  "Environment": "Test"
}
```

The configuration is loaded via `Microsoft.Extensions.Configuration` in `StripeService.fs`:

```fsharp
open Microsoft.Extensions.Configuration

let config = loadStripeConfig ()
// config.PublishableKey, config.SecretKey, config.WebhookEndpointSecret
```

The test keys are used unless `"Environment"` is explicitly set to `"Live"` (or `"Production"`) — a missing or misspelled value falls back to the test keys.

Also update the frontend publishable key in `frontend/stripe-integration.js`:

```javascript
const STRIPE_PUBLISHABLE_KEY = 'pk_test_your_actual_key_here';
```

### 2. Run the Sample

```bash
cd src
dotnet run
```

This runs out of the box (the Stripe calls are mocked, so no real keys are needed) and demonstrates:
- Customer creation
- Setup intents (for saving payment methods)
- Payment intents (for processing payments)
- Complete payment flows

### 3. Test the Frontend

Serve the `frontend/` directory over HTTP (Stripe.js does not work reliably from `file://` URLs), e.g.:

```bash
cd frontend
python -m http.server 8080
# then open http://localhost:8080
```

The page shows:
- Stripe Elements integration
- Card setup forms
- Payment processing flows
- Error handling examples

## Core Patterns

The general shape of a FunStripeLite request is `<module>.<Method> settings options`, where `settings` carries your API key. Options records are built with the static `New` method (optional parameters are camelCase):

```fsharp
open FunStripe
open FunStripe.StripeRequest

let settings = RestApi.StripeApiSettings.New(apiKey = config.SecretKey)
```

### Creating Customers

```fsharp
let createCustomer (firstName: string) (lastName: string) (email: string) =
    Customers.CreateOptions.New(
        email = email,
        name = $"{firstName} {lastName}",
        description = "Sample customer"
    )
    |> Customers.Create settings
    // Async<Result<StripeModel.Customer, StripeError.ErrorResponse>>
```

### Setup Intents (for saving payment methods)

```fsharp
let createSetupIntent (customerId: string) =
    SetupIntents.CreateOptions.New(
        customer = customerId,
        paymentMethodTypes = ["card"],
        usage = SetupIntents.Create'Usage.OffSession
    )
    |> SetupIntents.Create settings
```

### Payment Intents (for one-time payments)

```fsharp
// Note: the amount is an int in the smallest currency unit (e.g. cents)
let createPaymentIntent (amount: int) (currency: string) (customerId: string) =
    PaymentIntents.CreateOptions.New(
        amount = amount,
        currency = currency,
        customer = customerId,
        paymentMethodTypes = ["card"],
        confirmationMethod = PaymentIntents.Create'ConfirmationMethod.Automatic
    )
    |> PaymentIntents.Create settings
```

### Frontend Integration

See the `frontend/` directory for complete examples. Key patterns:

```javascript
// Initialize Stripe Elements
const stripe = Stripe('pk_test_...');
const elements = stripe.elements();
const cardElement = elements.create('card', { style: elementStyles });
cardElement.mount('#card-element');

// Confirm a payment
const { error, paymentIntent } = await stripe.confirmCardPayment(clientSecret, {
    payment_method: {
        card: cardElement,
        billing_details: { name: 'Customer Name' }
    }
});
```

### Webhook Handling

See the `webhooks/` directory for complete examples. Key patterns:

```fsharp
let processWebhookEvent (payload: string) =
    async {
        let stripeEvent = Util.deserialise<StripeModel.Event> payload
        match stripeEvent.Type with
        | StripeModel.EventType.PaymentIntentSucceeded ->
            let paymentIntent = Util.deserialise<StripeModel.PaymentIntent> stripeEvent.Data.Object
            return! handlePaymentSuccess paymentIntent
        | StripeModel.EventType.SetupIntentSucceeded ->
            let setupIntent = Util.deserialise<StripeModel.SetupIntent> stripeEvent.Data.Object
            return! handleSetupSuccess setupIntent
        // ... handle other events
        | _ -> return Ignored "Event type not handled"
    }
```

Always verify the `Stripe-Signature` header before processing the payload — see `verifyWebhookSignature` in `WebhookHandler.fs` for a complete implementation (HMAC-SHA256, timestamp tolerance, constant-time comparison, multiple `v1` entries during secret rotation).

## Architecture Patterns

### Error Handling

FunStripeLite uses F# Result types for comprehensive error handling. The error type is `StripeError.ErrorResponse`, whose `StripeError` field holds Stripe's error object (with optional `Message`, `Code`, `DeclineCode`, etc.):

```fsharp
let handlePaymentResult result =
    match result with
    | Ok (paymentIntent: StripeModel.PaymentIntent) ->
        // Success - process the payment intent
        printfn $"Payment created: {paymentIntent.Id}"
    | Error (e: StripeError.ErrorResponse) ->
        // Handle the error appropriately
        let message = e.StripeError.Message |> Option.defaultValue "Unknown error"
        printfn $"Error: {message}"
```

### Async Operations

All Stripe operations are asynchronous and return `Async<Result<'T, StripeError.ErrorResponse>>`:

```fsharp
let processPayment() =
    async {
        let! customerResult = createCustomer "John" "Doe" "john@example.com"
        match customerResult with
        | Ok customer ->
            let! paymentResult = createPaymentIntent 2000 "usd" customer.Id
            return paymentResult
        | Error error ->
            return Error error
    }
```

## Project Structure

```
FunStripe.Sample/
├── README.md                 # This file
├── src/
│   ├── Program.fs              # Main sample application
│   ├── StripeService.fs        # Core Stripe operations
│   ├── IntegrationExample.fs   # Web API integration patterns
│   ├── appsettings.json        # Configuration (Stripe keys)
│   └── FunStripeLite.Sample.fsproj
├── frontend/
│   ├── index.html              # Sample payment form
│   ├── stripe-integration.js   # Stripe Elements integration
│   └── styles.css              # Basic styling
├── webhooks/
│   ├── WebhookHandler.fs       # Webhook processing
│   └── Events.fs               # Event type definitions
└── tests/
    ├── EventsTests.fs          # Event parsing and handler tests
    ├── WebhookHandlerTests.fs  # Signature verification and processing tests
    ├── StripeServiceTests.fs   # Service and config tests
    ├── IntegrationExampleTests.fs # Endpoint tests
    └── tests.fsproj
```

## Security Considerations

### API Keys
- Never expose secret keys in frontend code -- only use publishable keys
- Use environment variables or a key vault for production keys
- Rotate keys regularly
- Use different keys for test and production

### Webhook Security
- Always verify webhook signatures (see `WebhookHandler.fs`)
- Use HTTPS endpoints only
- Implement idempotency to handle duplicate events
- Store and replay events if processing fails

### Payment Security
- Never store card details yourself -- use Stripe Elements
- Implement proper error handling to avoid exposing sensitive information
- Log security events for auditing

## Architecture Recommendations

### Production Web API Structure

1. **Endpoints**:
   - `POST /api/customers` -- Create customers
   - `POST /api/payment-intents` -- Create payment intents
   - `POST /api/setup-intents` -- Create setup intents
   - `POST /webhooks/stripe` -- Handle Stripe webhooks

2. **Service Layer**:
   - `StripeService` -- Wraps FunStripeLite operations
   - `PaymentService` -- Business logic for payments
   - `CustomerService` -- Customer management
   - `WebhookService` -- Event processing

3. **Database Integration**:
   - Store customer mappings (your user ID <-> Stripe customer ID)
   - Track payment statuses and order history
   - Log webhook events for idempotency
   - Store payment method references

### Business Logic Error Handling

Define your own error types alongside Stripe's:

```fsharp
type PaymentError =
    | StripeApiError of StripeError
    | InsufficientFunds
    | InvalidCustomer
    | OrderNotFound
```

## Testing

### Test Cards (Stripe Test Mode)

- Successful payment: `4242424242424242`
- Declined payment: `4000000000000002`
- 3D Secure required: `4000002500003155`
- Insufficient funds: `4000000000009995`

### Test Scenarios

1. **Happy path**: Successful payment flows
2. **Error handling**: Failed payments, network errors
3. **Edge cases**: Large amounts, international cards
4. **Security**: Invalid webhooks, tampered requests

### Automated Testing

The repository ships an xUnit test suite in `tests/` covering signature verification, event parsing, and the service/endpoint patterns:

```bash
cd tests
dotnet test
```

Example test (xUnit):

```fsharp
[<Fact>]
let ``should create customer successfully`` () =
    async {
        let! result = createCustomer config "John" "Doe" "john@test.com"
        match result with
        | Ok customer -> Assert.NotEmpty(customer.Id)
        | Error error -> Assert.Fail($"Unexpected error: {error}")
    } |> Async.RunSynchronously
```

## Deployment Checklist

### Before Going Live
- [ ] Replace test keys with live Stripe keys
- [ ] Set up webhook endpoints with HTTPS
- [ ] Configure proper error monitoring
- [ ] Set up payment reconciliation
- [ ] Test all payment flows thoroughly
- [ ] Configure fraud prevention rules
- [ ] Set up customer support processes

### Monitoring and Alerts
- [ ] Payment success/failure rates
- [ ] Webhook delivery status
- [ ] API response times
- [ ] Error rates and patterns
- [ ] Revenue and transaction volume

## Common Integration Patterns

### Subscription Billing

```fsharp
let createSubscription customerId priceId paymentMethodId =
    Subscriptions.CreateOptions.New(
        customer = customerId,
        items = [Subscriptions.Create'Items.New(price = priceId)],
        defaultPaymentMethod = paymentMethodId
    )
    |> Subscriptions.Create settings
```

### Refund Processing

```fsharp
let processRefund paymentIntentId amount =
    Refunds.CreateOptions.New(
        paymentIntent = paymentIntentId,
        amount = amount
    )
    |> Refunds.Create settings
```

## Next Steps

For production applications, consider implementing:
- Customer portal for managing payment methods
- Subscription billing (if applicable)
- Advanced webhook handling (retries, idempotency)
- Multi-party payments and marketplace features
- Dispute handling workflows

## Resources

- [Stripe API Documentation](https://stripe.com/docs/api)
- [FunStripeLite on NuGet](https://www.nuget.org/packages/FunStripeLite/)
- [Stripe Elements Documentation](https://stripe.com/docs/stripe-js)
- [Webhook Best Practices](https://stripe.com/docs/webhooks/best-practices)
- [Stripe Test Cards](https://docs.stripe.com/testing)
