module WebhookHandler

open System
open System.Text
open System.Security.Cryptography
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

/// Simplified webhook event representation for the sample
type MockWebhookEvent = {
    Id: string
    Type: string
    Created: int64
    Livemode: bool
    Data: obj option
}

/// Verify webhook signature (important for security).
/// The timestamp is taken from the Stripe-Signature header itself (the `t=` part).
let verifyWebhookSignature (config: WebhookConfig) (signature: string) (payload: string) =
    try
        // Stripe signature format: t=timestamp,v1=signature
        // There can be multiple v1 entries while an endpoint secret is being
        // rolled, so the header is valid if any of them matches.
        let signatureParts = signature.Split ','
        let timestampPart = signatureParts |> Array.find (fun s -> s.StartsWith "t=")
        let signatureCandidates =
            signatureParts
            |> Array.filter (fun s -> s.StartsWith "v1=")
            |> Array.map (fun s -> s.Substring 3)

        if Array.isEmpty signatureCandidates then
            false
        else

        let extractedTimestamp = timestampPart.Substring(2) |> int64

        // Check timestamp (prevent replay attacks)
        let currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        if abs(currentTimestamp - extractedTimestamp) > 300L then // 5 minutes tolerance
            false
        else
            // Compute expected signature
            let signedPayload = $"{extractedTimestamp}.{payload}"
            use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(config.EndpointSecret))
            let computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload))
            let computedSignature = (Convert.ToHexString computedHash).ToLower()

            // Constant-time comparison to prevent timing attacks
            let a = Encoding.UTF8.GetBytes(computedSignature)
            signatureCandidates
            |> Array.exists (fun candidate ->
                let b = Encoding.UTF8.GetBytes(candidate)
                CryptographicOperations.FixedTimeEquals(ReadOnlySpan(a), ReadOnlySpan(b)))
    with
    | ex ->
        printfn $"Error verifying webhook signature: {ex.Message}"
        false

/// Process a Stripe webhook event (simplified for sample)
let processWebhookEvent (config: WebhookConfig) (stripeEvent: MockWebhookEvent) =
    async {
        try
            let eventType = parseEventType stripeEvent.Type
            
            printfn $"Processing webhook event: {stripeEvent.Type} (ID: {stripeEvent.Id})"
            
            match eventType with
            | PaymentIntentSucceeded ->
                // In a real implementation, you would extract the actual PaymentIntent from the event
                let mockPaymentIntent = {
                    Id = "pi_mock_123"
                    Amount = 2000L
                    Currency = "usd"
                    CustomerId = Some "cus_mock_123"
                }
                match! handlePaymentSuccess mockPaymentIntent with
                | Success msg -> return Processed msg
                | Error msg -> return Failed msg
                | Ignored msg -> return UnsupportedEvent msg
            
            | PaymentIntentPaymentFailed ->
                let mockPaymentIntent = {
                    Id = "pi_mock_failed"
                    Amount = 2000L
                    Currency = "usd"
                    CustomerId = Some "cus_mock_123"
                }
                match! handlePaymentFailure mockPaymentIntent with
                | Success msg -> return Processed msg
                | Error msg -> return Failed msg
                | Ignored msg -> return UnsupportedEvent msg
            
            | SetupIntentSucceeded ->
                let mockSetupIntent = {
                    Id = "seti_mock_123"
                    CustomerId = Some "cus_mock_123"
                    PaymentMethodId = Some "pm_mock_123"
                }
                match! handleSetupSuccess mockSetupIntent with
                | Success msg -> return Processed msg
                | Error msg -> return Failed msg
                | Ignored msg -> return UnsupportedEvent msg
            
            | CustomerCreated ->
                let mockCustomer = {
                    Id = "cus_mock_new"
                    Email = Some "customer@example.com"
                    Name = Some "Test Customer"
                }
                match! handleCustomerCreated mockCustomer with
                | Success msg -> return Processed msg
                | Error msg -> return Failed msg
                | Ignored msg -> return UnsupportedEvent msg
            
            | ChargeSucceeded ->
                // For basic MVP, we might just log this and rely on payment_intent.succeeded
                printfn $"Charge succeeded: {stripeEvent.Id}"
                return Processed "Charge success logged"
            
            | InvoicePaymentSucceeded ->
                // Handle subscription payments if you implement subscriptions later
                printfn $"Invoice payment succeeded: {stripeEvent.Id}"
                return Processed "Invoice payment logged"
            
            | Other eventTypeName ->
                printfn $"Unhandled event type: {eventTypeName}"
                return UnsupportedEvent eventTypeName
                
        with
        | ex ->
            printfn $"Error processing webhook event: {ex.Message}"
            printfn $"Stack trace: {ex.StackTrace}"
            return Failed $"Exception: {ex.Message}"
    }

/// Main webhook endpoint handler
/// This would typically be called from your web framework (ASP.NET Core, Giraffe, etc.)
/// In a real implementation, eventType and eventId would be parsed from the JSON payload.
let handleWebhookRequest (config: WebhookConfig) (signature: string) (payload: string) (eventType: string) (eventId: string) =
    async {
        try
            // 1. Verify the signature first (critical for security)
            if not (verifyWebhookSignature config signature payload) then
                printfn "[WARN] Invalid webhook signature"
                return (InvalidSignature, 400)
            else

            // 2. Construct webhook event from parsed payload data
            let stripeEvent = {
                Id = eventId
                Type = eventType
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                Livemode = false
                Data = None
            }
            
            // 3. Process the event
            let! result = processWebhookEvent config stripeEvent
            
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
let logWebhookEvent (stripeEvent: MockWebhookEvent) =
    printfn "=== Webhook Event ==="
    printfn $"ID: {stripeEvent.Id}"
    printfn $"Type: {stripeEvent.Type}"
    printfn $"Created: {DateTimeOffset.FromUnixTimeSeconds(stripeEvent.Created)}"
    printfn $"Livemode: {stripeEvent.Livemode}"
    printfn "==================="

/// Important Note about Real Implementation
/// =======================================
/// 
/// This sample uses simplified mock implementations to demonstrate the patterns.
/// In a real application using FunStripe, you would:
/// 
/// 1. Parse the actual webhook payload JSON into FunStripe.StripeModel.Event
/// 2. Use the actual event data objects (PaymentIntent, SetupIntent, Customer, etc.)
/// 3. Implement proper error handling and retry logic
/// 4. Store webhook events for idempotency
/// 5. Use database transactions for business logic
/// 
/// Example real webhook processing with FunStripeLite (which uses its own
/// FSharp.Data-based JSON handling, not Newtonsoft.Json):
///
/// ```fsharp
/// open FunStripe
///
/// let processRealWebhook (payload: string) =
///     async {
///         // Util.deserialise handles Stripe's snake_case field names
///         let stripeEvent = Util.deserialise<StripeModel.Event> payload
///
///         match stripeEvent.Type with
///         | StripeModel.EventType.PaymentIntentSucceeded ->
///             // Event.Data.Object holds the raw JSON of the event's object
///             let paymentIntent = Util.deserialise<StripeModel.PaymentIntent> stripeEvent.Data.Object
///             return! handlePaymentSuccess paymentIntent
///         | StripeModel.EventType.SetupIntentSucceeded ->
///             let setupIntent = Util.deserialise<StripeModel.SetupIntent> stripeEvent.Data.Object
///             return! handleSetupSuccess setupIntent
///         // ... handle other event types
///         | _ -> return Ignored "Event type not handled"
///     }
/// ```