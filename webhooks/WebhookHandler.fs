module WebhookHandler

open System
open System.Text
open System.Security.Cryptography
open WebhookEvents

/// Configuration for webhook handling
type WebhookConfig = {
    EndpointSecret: string  // Your webhook endpoint secret from Stripe
    StripeSecretKey: string // Your Stripe secret key
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

/// Verify webhook signature (important for security)
let verifyWebhookSignature (config: WebhookConfig) (signature: string) (payload: string) (timestamp: int64) =
    try
        // Stripe signature format: t=timestamp,v1=signature
        let signatureParts = signature.Split(',')
        let timestampPart = signatureParts |> Array.find (fun s -> s.StartsWith("t="))
        let signaturePart = signatureParts |> Array.find (fun s -> s.StartsWith("v1="))
        
        let extractedTimestamp = timestampPart.Substring(2) |> int64
        let extractedSignature = signaturePart.Substring(3)
        
        // Check timestamp (prevent replay attacks)
        let currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        if abs(currentTimestamp - extractedTimestamp) > 300L then // 5 minutes tolerance
            false
        else
            // Compute expected signature
            let signedPayload = $"{extractedTimestamp}.{payload}"
            use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(config.EndpointSecret))
            let computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload))
            let computedSignature = BitConverter.ToString(computedHash).Replace("-", "").ToLower()
            
            // Constant-time comparison to prevent timing attacks
            let a = System.Text.Encoding.UTF8.GetBytes(computedSignature)
            let b = System.Text.Encoding.UTF8.GetBytes(extractedSignature)
            CryptographicOperations.FixedTimeEquals(System.ReadOnlySpan(a), System.ReadOnlySpan(b))
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
                let! result = handlePaymentSuccess mockPaymentIntent
                match result with
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
                let! result = handlePaymentFailure mockPaymentIntent
                match result with
                | Success msg -> return Processed msg
                | Error msg -> return Failed msg
                | Ignored msg -> return UnsupportedEvent msg
            
            | SetupIntentSucceeded ->
                let mockSetupIntent = {
                    Id = "seti_mock_123"
                    CustomerId = Some "cus_mock_123"
                    PaymentMethodId = Some "pm_mock_123"
                }
                let! result = handleSetupSuccess mockSetupIntent
                match result with
                | Success msg -> return Processed msg
                | Error msg -> return Failed msg
                | Ignored msg -> return UnsupportedEvent msg
            
            | CustomerCreated ->
                let mockCustomer = {
                    Id = "cus_mock_new"
                    Email = Some "customer@example.com"
                    Name = Some "Test Customer"
                }
                let! result = handleCustomerCreated mockCustomer
                match result with
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
            let timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            if not (verifyWebhookSignature config signature payload timestamp) then
                printfn "[WARN] Invalid webhook signature"
                return (InvalidSignature, 400)
            else
            
            // 2. Construct webhook event from parsed payload data
            let stripeEvent = {
                Id = eventId
                Type = eventType
                Created = timestamp
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
    printfn $"=== Webhook Event ==="
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
/// Example real webhook processing with FunStripe:
/// 
/// ```fsharp
/// let processRealWebhook (payload: string) =
///     async {
///         let stripeEvent = Newtonsoft.Json.JsonConvert.DeserializeObject<FunStripe.StripeModel.Event>(payload)
///         
///         match stripeEvent.Type with
///         | "payment_intent.succeeded" ->
///             match stripeEvent.Data with
///             | Some eventData ->
///                 match eventData.Object with
///                 | FunStripe.StripeModel.EventDataObject.PaymentIntent paymentIntent ->
///                     // Process the actual PaymentIntent object
///                     return! handlePaymentSuccess paymentIntent
///                 | _ -> return Error "Unexpected event data object"
///             | None -> return Error "No event data"
///         // ... handle other event types
///     }
/// ```