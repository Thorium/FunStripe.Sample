module WebhookSamples

open System
open System.Security.Cryptography
open System.Text
open FunStripe

// Example webhook payloads, and the Stripe-Signature header that goes with them, for
// trying the webhook handler without a Stripe account. Real deliveries come from Stripe,
// or from `stripe listen` / `stripe trigger` of the Stripe CLI during development.

/// A PaymentIntent as it appears in `data.object` of a payment_intent.* event
let paymentIntentObject = """{
    "id": "pi_3SampleSucceeded",
    "object": "payment_intent",
    "allowed_payment_method_types": ["card"],
    "amount": 2000,
    "amount_capturable": 0,
    "amount_received": 2000,
    "application": null,
    "application_fee_amount": null,
    "automatic_payment_methods": null,
    "canceled_at": null,
    "cancellation_reason": null,
    "capture_method": "automatic_async",
    "client_secret": "pi_3SampleSucceeded_secret_sample",
    "confirmation_method": "automatic",
    "created": 1790000000,
    "currency": "usd",
    "customer": "cus_SampleCustomer",
    "description": null,
    "last_payment_error": null,
    "latest_charge": "ch_3SampleSucceeded",
    "livemode": false,
    "metadata": {},
    "next_action": null,
    "on_behalf_of": null,
    "payment_method": "pm_1SampleCard",
    "payment_method_types": ["card"],
    "processing": null,
    "receipt_email": null,
    "review": null,
    "setup_future_usage": null,
    "shipping": null,
    "source": null,
    "statement_descriptor": null,
    "statement_descriptor_suffix": null,
    "status": "succeeded",
    "transfer_data": null,
    "transfer_group": null
  }"""

/// Wraps an object in the event envelope Stripe posts to a webhook endpoint
let eventPayload (eventId: string) (eventType: string) (objectJson: string) =
    $$"""{
  "id": "{{eventId}}",
  "object": "event",
  "api_version": "{{Config.DefaultStripeApiVersion}}",
  "created": 1790000000,
  "data": {
    "object": {{objectJson}}
  },
  "livemode": false,
  "pending_webhooks": 1,
  "request": { "id": null, "idempotency_key": null },
  "type": "{{eventType}}"
}"""

/// A payment_intent.succeeded event for a 20.00 USD card payment
let paymentIntentSucceeded =
    eventPayload "evt_3SampleSucceeded" "payment_intent.succeeded" paymentIntentObject

/// The Stripe-Signature header Stripe sends for a payload: the timestamp and an
/// HMAC-SHA256 of "timestamp.payload" keyed with the endpoint secret.
let signatureHeader (secret: string) (payload: string) (timestamp: int64) =
    use hmac = new HMACSHA256(Encoding.UTF8.GetBytes secret)
    let hash = hmac.ComputeHash(Encoding.UTF8.GetBytes $"{timestamp}.{payload}")
    $"t={timestamp},v1={(Convert.ToHexString hash).ToLower()}"
