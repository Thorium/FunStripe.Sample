# FunStripe Integration Guide

[**FunStripe**](https://github.com/simontreanor/FunStripe) is an F# library that provides a functional wrapper around the [Stripe](https://stripe.com/) API for payment processing. This guide demonstrates the essential patterns for integrating Stripe payments in your application.

This repository uses the [FunStripe.Core](https://www.nuget.org/packages/FunStripe.Core/) NuGet package, version 3.0, which targets Stripe API version `2026-09-30.endive`. Since FunStripe 2.0 this is the only package: the code generators stay in the FunStripe repository, so `FunStripe.Core` is what `FunStripeLite` used to be (the library without the generators and their dependencies). This repository is not using the official Stripe.net .NET integration, as avoiding that is one of the key aims of the alternative, FunStripe.

> **Note:** The F# code in `src/` and `webhooks/` calls FunStripe for real. With a Stripe test key configured, `dotnet run` creates customers, setup intents and payment intents in your Stripe test account. Without one it skips those calls and still runs the webhook demo, which needs no Stripe account.

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
- FunStripe.Core NuGet package

## Quick Start

### 1. Configuration

Set your Stripe test secret key in the `STRIPE_TEST_API_KEY` environment variable (the same variable FunStripe itself reads), so the key never has to be written to a file:

```bash
export STRIPE_TEST_API_KEY=sk_test_your_key_here
```

Or configure the keys in `src/appsettings.json`:

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
let config = loadStripeConfig ()
// config.PublishableKey, config.SecretKey, config.WebhookEndpointSecret

let settings = createSettings config
// FunStripe's RestApi.StripeApiSettings, passed to every Stripe call
```

The test keys are used unless `"Environment"` is explicitly set to `"Live"` (or `"Production"`) — a missing or misspelled value falls back to the test keys. In the test environment `STRIPE_TEST_API_KEY` wins over `TestSecretKey`.

Also update the frontend publishable key in `frontend/stripe-integration.js`:

```javascript
const STRIPE_PUBLISHABLE_KEY = 'pk_test_your_actual_key_here';
```

### 2. Run the Sample

```bash
cd src
dotnet run
```

With a test key configured this demonstrates, against your Stripe test account:
- Customer creation
- Setup intents (for saving payment methods)
- Payment intents (for processing payments)
- Complete payment flows

It always demonstrates webhook handling: a sample `payment_intent.succeeded` delivery is signed locally and run through the webhook handler, followed by a tampered copy that the signature check rejects.

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

FunStripe types are organised into per-domain namespaces: response models live under `Stripe.{Domain}` and request options under `StripeRequest.{Domain}`. The general shape of a request is `<module>.<Method> settings options`, where `settings` carries your API key. Options records are built with the static `New` method (optional parameters are camelCase):

```fsharp
open FunStripe
open FunStripe.IsoTypes          // IsoCurrencyCode
open Stripe.PaymentMethod        // Customer, SetupIntent, PaymentIntent, ...
open StripeRequest.Customers     // Customers
open StripeRequest.Setup         // SetupIntents
open StripeRequest.Payment       // PaymentIntents

let settings =
    RestApi.StripeApiSettings.New(
        apiKey = config.SecretKey,
        stripeVersion = Config.DefaultStripeApiVersion
    )
```

`stripeVersion` sends the `Stripe-Version` header. Without it Stripe uses the default API version of your account, which can differ from the version the FunStripe models were generated from (`Config.DefaultStripeApiVersion`, here `2026-09-30.endive`). Pinning it keeps the responses in line with the types.

### Creating Customers

```fsharp
let createCustomer (firstName: string) (lastName: string) (email: string) =
    Customers.CreateOptions.New(
        email = email,
        name = $"{firstName} {lastName}",
        description = "Sample customer"
    )
    |> Customers.Create settings
    // Async<Result<Customer, StripeError.ErrorResponse>>
```

### Setup Intents (for saving payment methods)

```fsharp
let createSetupIntent (customerId: string) =
    SetupIntents.CreateOptions.New(
        customer = customerId,
        allowedPaymentMethodTypes = [SetupIntents.Create'AllowedPaymentMethodTypes.Card],
        usage = SetupIntents.Create'Usage.OffSession
    )
    |> SetupIntents.Create settings
```

### Payment Intents (for one-time payments)

```fsharp
// Note: the amount is an int in the smallest currency unit (e.g. cents)
let createPaymentIntent (amount: int) (currency: IsoCurrencyCode) (customerId: string) =
    PaymentIntents.CreateOptions.New(
        amount = amount,
        currency = currency,
        customer = customerId,
        allowedPaymentMethodTypes = [PaymentIntents.Create'AllowedPaymentMethodTypes.Card]
    )
    |> PaymentIntents.Create settings

// createPaymentIntent 2000 IsoCurrencyCode.USD "cus_..."
```

Stripe removed the `payment_method_types` request parameter in the `2026-09-30.endive` API, so FunStripe 3.0 no longer has `paymentMethodTypes` on these requests. Its replacement, `allowedPaymentMethodTypes`, is a typed list that filters the payment methods Stripe computes dynamically for the payment: a type you list is offered only if it is also eligible. Leave it out to offer everything enabled in your Dashboard.

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

See the `webhooks/` directory for complete examples. Verify the `Stripe-Signature` header against the raw request body first, then parse the payload into FunStripe's `Event`:

```fsharp
open FunStripe
open Stripe.Event
open Stripe.PaymentMethod

let handleWebhook (endpointSecret: string) (signatureHeader: string) (rawBody: string) =
    async {
        match WebhookSigning.verifyWithDefaultTolerance endpointSecret rawBody signatureHeader with
        | Error reason -> return Error $"Invalid signature: {reason}"
        | Ok () ->
            let stripeEvent = Util.deserialise<Event> rawBody
            match stripeEvent.Type with
            | EventType.PaymentIntentSucceeded ->
                let paymentIntent = stripeEvent.Data.Object |> Util.deserialiseRaw<PaymentIntent>
                let! result = handlePaymentSuccess paymentIntent
                return Ok result
            | EventType.SetupIntentSucceeded ->
                let setupIntent = stripeEvent.Data.Object |> Util.deserialiseRaw<SetupIntent>
                let! result = handleSetupSuccess setupIntent
                return Ok result
            // ... handle other events
            | _ -> return Ok (Ignored "Event type not handled")
    }
```

- `WebhookSigning` checks the HMAC-SHA256 in constant time, enforces the timestamp tolerance (5 minutes by default) and accepts any of the `v1` entries Stripe sends while an endpoint secret is being rolled. Pass it the body exactly as received; a re-serialised copy does not match the signature.
- `Event.Data.Object` is a `RawJson` value holding the event's object as Stripe sent it. `Util.deserialiseRaw` turns it into the model that belongs to the event type.
- Event types Stripe adds after this FunStripe version was generated deserialise to `EventType.UnknownEnumValue "the.event.type"`, so a match on `EventType` needs a wildcard (or that case).
- Stripe renders `data.object` with the API version of the *webhook endpoint*. Create the endpoint with the version the FunStripe models target (`Config.DefaultStripeApiVersion`), otherwise an object may not deserialise.
- Expandable fields are typed. `paymentIntent.Customer` is a `PaymentIntentCustomer'AnyOf` (a bare ID, or the full customer when expanded), and references such as `setupIntent.PaymentMethod` are a `StripeId<Markers.PaymentMethod>`; match `StripeId id` to get the string. See `Events.fs`.

## Architecture Patterns

### Error Handling

FunStripe uses F# Result types for comprehensive error handling. The error type is `StripeError.ErrorResponse`, whose `StripeError` field holds Stripe's error object (with optional `Message`, `Code`, `DeclineCode`, etc.):

```fsharp
let handlePaymentResult result =
    match result with
    | Ok (paymentIntent: PaymentIntent) ->
        // Success - process the payment intent
        printfn $"Payment created: {paymentIntent.Id}"
    | Error (e: StripeError.ErrorResponse) ->
        // Handle the error appropriately
        let message = e.StripeError.Message |> Option.defaultValue "Unknown error"
        printfn $"Error: {message}"
```

### Async Operations

All Stripe operations are asynchronous and return `Async<Result<'T, StripeError.ErrorResponse>>`. FunStripe's `asyncResult` computation expression chains them and stops at the first error:

```fsharp
open FunStripe.AsyncResultCE

let processPayment () =
    asyncResult {
        let! customer = createCustomer "John" "Doe" "john@example.com"
        let! paymentIntent = createPaymentIntent 2000 IsoCurrencyCode.USD customer.Id
        return paymentIntent
    }
```

### Idempotency

Pass an idempotency key with a mutating request so that a retry cannot run it twice. Stripe keeps the response to the first request with a key for 24 hours and answers a repeat with it:

```fsharp
let createCustomerOnce (idempotencyKey: string) (email: string) =
    Customers.CreateOptions.New(email = email)
    |> Customers.Create (settings.WithIdempotencyKey idempotencyKey)
```

The sample passes a key with every create call. `StripeService.fs` has two small types for it, so that a key cannot be blank or be mixed up with the other strings a call takes:

```fsharp
// OperationId: one operation of your application. None when blank or too long.
match OperationId.tryCreate request.IdempotencyKey with
| None -> // reject the request
| Some operationId ->
    // IdempotencyKey: one Stripe call of that operation
    createCustomer settings (idempotencyKeyFor operationId "customer") firstName lastName email
    // ...
    createPaymentIntent settings (idempotencyKeyFor operationId "payment-intent") amount currency (Some customerId)
```

- The operation ID must be the same for every retry and different for any other operation. Take it from something your application stores: an order ID, or the `Idempotency-Key` header your own client sent. The endpoints in `IntegrationExample.fs` read it from the request (`IdempotencyKey`) and reject a request without one. `Program.fs` uses a fresh GUID only because every demo run is a new operation.
- Stripe ties a key to the endpoint and parameters of its first use, hence one key per step of an operation.
- A retried `createPaymentEndpoint` request gets the same customer and payment intent back. A returning user is a different operation, though: to avoid a second Stripe customer for the same person, store the customer ID with your user and look it up before creating one.

## Project Structure

```
FunStripe.Sample/
├── README.md                 # This file
├── src/
│   ├── Program.fs              # Main sample application
│   ├── StripeService.fs        # Core Stripe operations
│   ├── IntegrationExample.fs   # Web API integration patterns
│   ├── appsettings.json        # Configuration (Stripe keys)
│   └── FunStripe.Sample.fsproj
├── frontend/
│   ├── index.html              # Sample payment form
│   ├── stripe-integration.js   # Stripe Elements integration
│   └── styles.css              # Basic styling
├── webhooks/
│   ├── WebhookHandler.fs       # Signature verification and event dispatch
│   ├── Events.fs               # Business logic per event
│   └── WebhookSamples.fs       # Sample payloads and signatures for local runs
└── tests/
    ├── FakeStripeServer.fs     # Local stand-in for api.stripe.com
    ├── EventsTests.fs          # Event handler tests
    ├── WebhookHandlerTests.fs  # Signature verification and processing tests
    ├── StripeServiceTests.fs   # Service, request encoding and config tests
    ├── IntegrationExampleTests.fs # Endpoint tests
    ├── LiveTests.fs            # Calls Stripe test mode (needs STRIPE_TEST_API_KEY)
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
- Keep the endpoint secret configured and private: the handler rejects every delivery while it is blank or still the `whsec_...` placeholder, because anyone could sign a payload with a known secret
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
   - `StripeService` -- Wraps FunStripe operations
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
    | StripeApiError of StripeError.ErrorResponse
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

The repository ships an xUnit test suite in `tests/` covering signature verification, event handling, and the service/endpoint patterns:

```bash
cd tests
dotnet test
```

The tests need no Stripe account. `StripeApiSettings` takes a base URL, so the service code runs unchanged against a local fake server (`FakeStripeServer.fs`) that records the requests and answers with Stripe-shaped JSON. That exercises FunStripe's real request encoding and response deserialisation:

```fsharp
[<Fact>]
let ``createCustomer posts name and email and returns the customer`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        // `key` is a test helper: the idempotency key of a step of the operation "op-1"
        match! createCustomer stripe.Settings (key "customer") "Alice" "Smith" "alice@example.com" with
        | Ok customer -> Assert.Equal("cus_FakeCustomer", customer.Id)
        | Error err -> Assert.Fail($"Expected Ok, got Error: {describeStripeError err}")

        let request = Assert.Single stripe.Requests
        Assert.Equal("/v1/customers", request.Path)
        Assert.Equal("Alice Smith", request.Form.["name"])
    } |> Async.StartImmediateAsTask :> Task
```

`LiveTests.fs` runs the same service functions against Stripe's test mode. It is skipped unless `STRIPE_TEST_API_KEY` is set.

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
open StripeRequest.Subscriptions

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
open StripeRequest.Refunds

let processRefund paymentIntentId amount =
    Refunds.CreateOptions.New(
        paymentIntent = paymentIntentId,
        amount = amount
    )
    |> Refunds.Create settings
```

## Upgrading from FunStripeLite 1.x

- Reference `FunStripe.Core` instead of `FunStripeLite` (or `FunStripe`).
- Replace `open FunStripe.StripeRequest` and `StripeModel.X` with the per-domain namespaces: `StripeRequest.{Domain}` for requests, `Stripe.{Domain}` for models.
- Replace `paymentMethodTypes = ["card"]` with `allowedPaymentMethodTypes = [...Create'AllowedPaymentMethodTypes.Card]` on PaymentIntent, SetupIntent and Checkout Session requests.
- Currencies are `IsoTypes.IsoCurrencyCode` values instead of strings (`parseCurrency` in `StripeService.fs` converts user input).
- Webhooks: `Event.Data.Object` is `RawJson` (use `Util.deserialiseRaw`), `EventType` has an `UnknownEnumValue` case, and `WebhookSigning` replaces hand-written signature checks.
- Many ID fields are `StripeId<_>` or `...'AnyOf` unions instead of strings.

See FunStripe's [CHANGELOG](https://github.com/simontreanor/FunStripe/blob/main/CHANGELOG.md) and [v1 to v2 migration guide](https://github.com/simontreanor/FunStripe/blob/main/MIGRATION-v1-to-v2.md) for the full list.

## Next Steps

For production applications, consider implementing:
- Customer portal for managing payment methods
- Subscription billing (if applicable)
- Advanced webhook handling (retries, idempotency)
- Multi-party payments and marketplace features
- Dispute handling workflows

## Resources

- [Stripe API Documentation](https://stripe.com/docs/api)
- [FunStripe.Core on NuGet](https://www.nuget.org/packages/FunStripe.Core/)
- [FunStripe on GitHub](https://github.com/simontreanor/FunStripe)
- [Stripe Elements Documentation](https://stripe.com/docs/stripe-js)
- [Webhook Best Practices](https://stripe.com/docs/webhooks/best-practices)
- [Stripe Test Cards](https://docs.stripe.com/testing)
