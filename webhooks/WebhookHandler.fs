module WebhookHandler

open System
open FunStripe
open Stripe.Event
open Stripe.PaymentMethod
open WebhookEvents

/// Configuration for webhook handling
type WebhookConfig = {
    EndpointSecret: string  // Your webhook endpoint secret from Stripe
}

/// Webhook processing result
type WebhookResult =
    | Processed of string
    | Failed of string
    | InvalidSignature
    | UnsupportedEvent of string

/// The event type as Stripe spells it, e.g. "payment_intent.succeeded"
let eventTypeName (eventType: EventType) =
    match eventType with
    | EventType.UnknownEnumValue name -> name
    | known -> (Util.serialise known).Trim '"'

let private toWebhookResult result =
    match result with
    | Success msg -> Processed msg
    | HandlerError msg -> Failed msg
    | Ignored msg -> UnsupportedEvent msg

/// `data.object` is rendered with the API version of the webhook endpoint, not the version
/// of the request that caused the event. Create the endpoint with the API version of the
/// FunStripe models (`Config.DefaultStripeApiVersion`), or the object may not deserialise.
let private warnOnApiVersionMismatch (stripeEvent: Event) =
    match stripeEvent.ApiVersion with
    | Some version when version <> Config.DefaultStripeApiVersion ->
        printfn $"[WARN] Event {stripeEvent.Id} uses API version {version}, FunStripe models target {Config.DefaultStripeApiVersion}"
    | _ -> ()

/// Process a Stripe webhook event
let processWebhookEvent (stripeEvent: Event) =
    async {
        try
            printfn $"Processing webhook event: {eventTypeName stripeEvent.Type} (ID: {stripeEvent.Id})"
            warnOnApiVersionMismatch stripeEvent

            // Event.Data.Object keeps the JSON of the event's object as Stripe sent it;
            // Util.deserialiseRaw turns it into the model that belongs to the event type.
            match stripeEvent.Type with
            | EventType.PaymentIntentSucceeded ->
                let paymentIntent = stripeEvent.Data.Object |> Util.deserialiseRaw<PaymentIntent>
                let! result = handlePaymentSuccess paymentIntent
                return toWebhookResult result

            | EventType.PaymentIntentPaymentFailed ->
                let paymentIntent = stripeEvent.Data.Object |> Util.deserialiseRaw<PaymentIntent>
                let! result = handlePaymentFailure paymentIntent
                return toWebhookResult result

            | EventType.SetupIntentSucceeded ->
                let setupIntent = stripeEvent.Data.Object |> Util.deserialiseRaw<SetupIntent>
                let! result = handleSetupSuccess setupIntent
                return toWebhookResult result

            | EventType.CustomerCreated ->
                let customer = stripeEvent.Data.Object |> Util.deserialiseRaw<Customer>
                let! result = handleCustomerCreated customer
                return toWebhookResult result

            | EventType.ChargeSucceeded ->
                // For basic MVP, we might just log this and rely on payment_intent.succeeded
                printfn $"Charge succeeded: {stripeEvent.Id}"
                return Processed "Charge success logged"

            | EventType.InvoicePaymentSucceeded ->
                // Handle subscription payments if you implement subscriptions later
                printfn $"Invoice payment succeeded: {stripeEvent.Id}"
                return Processed "Invoice payment logged"

            | other ->
                // Includes EventType.UnknownEnumValue: event types Stripe added after this
                // FunStripe version was generated deserialise to it instead of failing.
                let eventTypeName = eventTypeName other
                printfn $"Unhandled event type: {eventTypeName}"
                return UnsupportedEvent eventTypeName

        with
        | ex ->
            printfn $"Error processing webhook event: {ex.Message}"
            printfn $"Stack trace: {ex.StackTrace}"
            return Failed $"Exception: {ex.Message}"
    }

/// A signature only proves something when the secret is one nobody else knows. With a blank
/// secret, or the "whsec_..." placeholder of appsettings.json, anyone could sign a payload.
let private isSecretConfigured (secret: string) =
    not (String.IsNullOrWhiteSpace secret || secret.EndsWith("...", StringComparison.Ordinal))

/// Main webhook endpoint handler
/// This would typically be called from your web framework (ASP.NET Core, Giraffe, etc.)
/// with the value of the Stripe-Signature header and the raw request body. The body must
/// be the exact bytes Stripe sent: a re-serialised copy does not match the signature.
let handleWebhookRequest (config: WebhookConfig) (signature: string) (payload: string) =
    async {
        try
            if not (isSecretConfigured config.EndpointSecret) then
                printfn "[FAIL] Webhook endpoint secret is not configured, rejecting the delivery"
                return Failed "Webhook endpoint secret is not configured", 500
            else

            // 1. Verify the signature first (critical for security). FunStripe checks the
            //    HMAC-SHA256 in constant time, accepts any of the v1 entries Stripe sends
            //    while an endpoint secret is being rolled, and rejects timestamps more than
            //    5 minutes away from now (replay protection).
            let verification =
                if String.IsNullOrEmpty signature then
                    Error (WebhookSigning.InvalidHeader "Missing Stripe-Signature header")
                else
                    WebhookSigning.verifyWithDefaultTolerance config.EndpointSecret payload signature

            match verification with
            | Error (WebhookSigning.InvalidHeader reason)
            | Error (WebhookSigning.TimestampOutOfTolerance reason)
            | Error (WebhookSigning.SignatureMismatch reason) ->
                printfn $"[WARN] Invalid webhook signature: {reason}"
                return InvalidSignature, 400
            | Ok () ->
                // 2. Parse the verified payload into FunStripe's Event model
                let stripeEvent = Util.deserialise<Event> payload

                // 3. Process the event
                let! result = processWebhookEvent stripeEvent

                match result with
                | Processed msg ->
                    printfn $"[OK] Webhook processed successfully: {msg}"
                    return result, 200
                | Failed msg ->
                    printfn $"[FAIL] Webhook processing failed: {msg}"
                    return result, 500
                | InvalidSignature ->
                    printfn "[FAIL] Invalid webhook signature"
                    return result, 400
                | UnsupportedEvent unsupported ->
                    printfn $"[INFO] Unsupported event type: {unsupported}"
                    return result, 200  // Still return 200 to acknowledge receipt

        with
        | ex ->
            printfn $"[FAIL] Exception handling webhook: {ex.Message}"
            return Failed ex.Message, 500
    }

/// Helper to log webhook events for debugging
let logWebhookEvent (stripeEvent: Event) =
    printfn "=== Webhook Event ==="
    printfn $"ID: {stripeEvent.Id}"
    printfn $"Type: {eventTypeName stripeEvent.Type}"
    printfn $"Created: {DateTimeOffset stripeEvent.Created}"
    printfn $"Livemode: {stripeEvent.Livemode}"
    printfn "==================="

/// Before taking this to production:
///
/// 1. Store the IDs of processed events and skip the ones already seen (Stripe retries
///    and can deliver an event more than once)
/// 2. Return quickly and do slow work on a queue; Stripe times a delivery out
/// 3. Use database transactions for business logic
/// 4. During development, forward events to your machine with the Stripe CLI:
///    `stripe listen --forward-to localhost:5000/webhooks/stripe`
