# FunStripe / FunStripeLite Integration Guide

[**FunStripe**](https://github.com/simontreanor/FunStripe) is an F# library that provides a functional wrapper around the [Stripe](https://stripe.com/) API for payment processing. This guide demonstrates the essential patterns for integrating Stripe payments in your application.

This repository uses FunStripeLite NuGet-package, but usage is identical to full FunStripe, just with a few dependencies removed. This repository is not using the official Stripe.net .NET integration, as avoiding that is exactly one key aims of the alternative, FunStripe.

## Overview

This sample covers the most important use cases for a minimum viable product (MVP):

- Creating Stripe customers
- Setting up payment methods with Setup Intents
- Processing one-time payments with Payment Intents
- Handling webhooks for payment events
- Frontend integration with Stripe Elements

## Prerequisites

- .NET 8.0 or later
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

Also update the frontend publishable key in `frontend/stripe-integration.js`:

```javascript
const STRIPE_PUBLISHABLE_KEY = 'pk_test_your_actual_key_here';
```

### 2. Run the Sample

```bash
cd src
dotnet run
```

This will demonstrate:
- Customer creation
- Setup intents (for saving payment methods)
- Payment intents (for processing payments)
- Complete payment flows

### 3. Test the Frontend

Open `frontend/index.html` in a browser to see:
- Stripe Elements integration
- Card setup forms
- Payment processing flows
- Error handling examples

## Core Patterns

### Creating Customers

```fsharp
let createCustomer (firstName: string) (lastName: string) (email: string) =
    async {
        let customerRequest = {
            CustomerCreateRequest.Default with
                Email = Some email
                Name = Some $"{firstName} {lastName}"
                Description = Some "Sample customer"
        }
        
        let! result = StripeRequest.Customer.create customerRequest
        return result
    }
```

### Setup Intents (for saving payment methods)

```fsharp
let createSetupIntent (customerId: string) =
    async {
        let setupRequest = {
            SetupIntentCreateRequest.Default with
                Customer = Some customerId
                PaymentMethodTypes = ["card"]
                Usage = Some SetupIntentUsage.OffSession
        }
        
        let! result = StripeRequest.SetupIntent.create setupRequest
        return result
    }
```

### Payment Intents (for one-time payments)

```fsharp
let createPaymentIntent (amount: int64) (currency: string) (customerId: string) =
    async {
        let paymentRequest = {
            PaymentIntentCreateRequest.Default with
                Amount = amount
                Currency = currency
                Customer = Some (PaymentIntentCustomer'AnyOf.String customerId)
                PaymentMethodTypes = ["card"]
                ConfirmationMethod = PaymentIntentConfirmationMethod.Automatic
        }
        
        let! result = StripeRequest.PaymentIntent.create paymentRequest
        return result
    }
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
let processWebhookEvent eventType eventData =
    async {
        match eventType with
        | PaymentIntentSucceeded ->
            let! result = handlePaymentSuccess paymentIntent
            return result
        | SetupIntentSucceeded ->
            let! result = handleSetupSuccess setupIntent
            return result
        // ... handle other events
    }
```

## Architecture Patterns

### Error Handling

FunStripeLite uses F# Result types for comprehensive error handling:

```fsharp
let handlePaymentResult result =
    match result with
    | Ok paymentIntent ->
        // Success - process the payment intent
        printfn $"Payment created: {paymentIntent.Id}"
    | Error stripeError ->
        // Handle the error appropriately
        printfn $"Error: {stripeError.StripeError.Message}"
```

### Async Operations

All Stripe operations are asynchronous and return `Async<Result<'T, StripeError>>`:

```fsharp
let processPayment() =
    async {
        let! customerResult = createCustomer "John" "Doe" "john@example.com"
        match customerResult with
        | Ok customer ->
            let! paymentResult = createPaymentIntent 2000L "usd" customer.Id
            return paymentResult
        | Error error ->
            return Error error
    }
```

## Project Structure

```
FunStripeLite.Sample/
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
└── webhooks/
    ├── WebhookHandler.fs       # Webhook processing
    └── Events.fs               # Event type definitions
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

### Automated Testing Example

```fsharp
[<Test>]
let ``should create customer successfully`` () =
    async {
        let! result = createCustomer "John" "Doe" "john@test.com"
        match result with
        | Ok customer -> Assert.IsNotEmpty(customer.Id)
        | Error error -> Assert.Fail($"Unexpected error: {error}")
    }
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
    async {
        let subscriptionRequest = {
            Customer = customerId
            Items = [{ Price = priceId }]
            DefaultPaymentMethod = paymentMethodId
        }
        let! result = Stripe.createSubscription subscriptionRequest
        return result
    }
```

### Refund Processing

```fsharp
let processRefund paymentIntentId amount =
    async {
        let! result = Stripe.createRefund paymentIntentId amount
        return result
    }
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
