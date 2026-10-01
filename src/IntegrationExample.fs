module IntegrationExample

open System
open FunStripe
open FunStripe.AsyncResultCE
open StripeService

/// Sample web API endpoints that demonstrate complete integration
/// This shows how you might structure your F# web application

type PaymentRequest = {
    Amount: decimal
    Currency: string
    CustomerEmail: string
    CustomerName: string
    /// Identifies the operation, e.g. your order ID or the Idempotency-Key header of the
    /// incoming HTTP request. A retry with the same key gets the same customer and payment
    /// intent back instead of creating new ones.
    IdempotencyKey: string
}

type SetupRequest = {
    CustomerEmail: string
    CustomerName: string
    /// Identifies the operation (see `PaymentRequest.IdempotencyKey`)
    IdempotencyKey: string
}

type PaymentResponseData = {
    ClientSecret: string
    PaymentIntentId: string
    CustomerId: string
}

type SetupResponseData = {
    ClientSecret: string
    SetupIntentId: string
    CustomerId: string
}

type ApiResponse<'T> = {
    Success: bool
    Data: 'T option
    Error: string option
}

/// Helper to create API responses
let createSuccessResponse data =
    { Success = true; Data = Some data; Error = None }

let createErrorResponse message =
    { Success = false; Data = None; Error = Some message }

let private toResponse (result: Result<'T, string>) =
    match result with
    | Ok data -> createSuccessResponse data
    | Error message -> createErrorResponse message

/// "Jane Smith" -> "Jane", "Smith". A missing or single-word name has no last name.
let private splitName (fullName: string) =
    match (fullName |> Option.ofObj |> Option.defaultValue "").Split(' ', 2) with
    | [| firstName; lastName |] -> firstName, lastName
    | parts -> parts.[0], ""

/// Logs what Stripe answered and gives the client a message that reveals none of it
let private stripeFailure (publicMessage: string) (error: StripeError.ErrorResponse) =
    printfn $"{publicMessage}: {describeStripeError error}"
    publicMessage

/// The client secret the frontend confirms the intent with
let private requireClientSecret (publicMessage: string) (intentId: string) (clientSecret: string option) =
    match clientSecret with
    | Some secret -> Ok secret
    | None ->
        printfn $"{intentId} has no client secret"
        Error publicMessage

let private requireOperationId (idempotencyKey: string) =
    match OperationId.tryCreate idempotencyKey with
    | Some operationId -> Ok operationId
    | None -> Error "Idempotency key is required"

let private validatePaymentRequest (request: PaymentRequest) =
    match requireOperationId request.IdempotencyKey, parseCurrency request.Currency, tryFormatAmount request.Amount with
    | Error message, _, _ -> Error message
    | _, None, _ -> Error "Unsupported currency"
    | _, _, None -> Error "Invalid amount"
    | Ok operationId, Some currency, Some amount -> Ok (operationId, currency, amount)

/// Example: Create customer and payment intent endpoint
let createPaymentEndpoint (settings: RestApi.StripeApiSettings) (request: PaymentRequest) =
    async {
        try
            // asyncResult stops at the first Error, which becomes the error response
            let! result =
                asyncResult {
                    // Validate before the first Stripe call, so that a bad request does not
                    // leave a customer without a payment behind
                    let! operationId, currency, amount = validatePaymentRequest request |> AsyncResult.ofResult
                    let firstName, lastName = splitName request.CustomerName

                    // 1. Create or get customer
                    //    The key makes a retry of this request reuse its customer. A returning
                    //    user is a new operation: look the Stripe customer ID up in your own
                    //    database first, and create a customer only when there is none.
                    let! customer =
                        createCustomer settings (idempotencyKeyFor operationId "customer") firstName lastName request.CustomerEmail
                        |> AsyncResult.mapError (stripeFailure "Failed to create customer")

                    // 2. Create payment intent
                    let! paymentIntent =
                        createPaymentIntent settings (idempotencyKeyFor operationId "payment-intent") amount currency (Some customer.Id)
                        |> AsyncResult.mapError (stripeFailure "Failed to create payment intent")

                    let! clientSecret =
                        paymentIntent.ClientSecret
                        |> requireClientSecret "Failed to create payment intent" paymentIntent.Id
                        |> AsyncResult.ofResult

                    return {
                        PaymentResponseData.ClientSecret = clientSecret
                        PaymentIntentId = paymentIntent.Id
                        CustomerId = customer.Id
                    }
                }

            return toResponse result
        with
        | ex ->
            printfn $"Exception in createPaymentEndpoint: {ex.Message}"
            return createErrorResponse "Internal server error"
    }

/// Example: Create customer and setup intent endpoint
let createSetupEndpoint (settings: RestApi.StripeApiSettings) (request: SetupRequest) =
    async {
        try
            let! result =
                asyncResult {
                    let! operationId = requireOperationId request.IdempotencyKey |> AsyncResult.ofResult
                    let firstName, lastName = splitName request.CustomerName

                    // 1. Create or get customer (see createPaymentEndpoint)
                    let! customer =
                        createCustomer settings (idempotencyKeyFor operationId "customer") firstName lastName request.CustomerEmail
                        |> AsyncResult.mapError (stripeFailure "Failed to create customer")

                    // 2. Create setup intent
                    let! setupIntent =
                        createSetupIntent settings (idempotencyKeyFor operationId "setup-intent") customer.Id
                        |> AsyncResult.mapError (stripeFailure "Failed to create setup intent")

                    let! clientSecret =
                        setupIntent.ClientSecret
                        |> requireClientSecret "Failed to create setup intent" setupIntent.Id
                        |> AsyncResult.ofResult

                    return {
                        SetupResponseData.ClientSecret = clientSecret
                        SetupIntentId = setupIntent.Id
                        CustomerId = customer.Id
                    }
                }

            return toResponse result
        with
        | ex ->
            printfn $"Exception in createSetupEndpoint: {ex.Message}"
            return createErrorResponse "Internal server error"
    }

/// Example: Complete integration flow demonstration
let demonstrateCompleteIntegration () =
    async {
        printfn "=== Complete FunStripe Integration Demo ==="
        printfn ""

        let config = loadStripeConfig ()

        // Note: `return ()` inside an async block does NOT exit early -- code
        // after the `if` would still run. Use if/else to actually branch.
        if keyMode config = NotConfigured then
            printfn "[INFO] Running with placeholder keys (the Stripe API calls will be rejected)"

        let settings = createSettings config

        printfn "1. Creating payment endpoint simulation..."
        let paymentRequest = {
            Amount = 29.99m
            Currency = "usd"
            CustomerEmail = "integration.test@example.com"
            CustomerName = "Integration Test"
            // A real endpoint takes this from the incoming request (order ID or
            // Idempotency-Key header), so that a retry carries the same value
            IdempotencyKey = $"demo-payment-{Guid.NewGuid()}"
        }

        let! paymentResponse = createPaymentEndpoint settings paymentRequest

        if paymentResponse.Success then
            printfn "[OK] Payment endpoint created successfully"
            match paymentResponse.Data with
            | Some data ->
                printfn $"  Payment Intent ID: {data.PaymentIntentId}"
                printfn $"  Customer ID: {data.CustomerId}"
                printfn "  Frontend would use the client_secret to complete payment"
            | None -> ()
        else
            printfn $"[FAIL] Payment endpoint failed: {paymentResponse.Error}"

        printfn ""
        printfn "2. Creating setup endpoint simulation..."
        let setupRequest = {
            CustomerEmail = "setup.test@example.com"
            CustomerName = "Setup Test"
            IdempotencyKey = $"demo-setup-{Guid.NewGuid()}"
        }

        let! setupResponse = createSetupEndpoint settings setupRequest

        if setupResponse.Success then
            printfn "[OK] Setup endpoint created successfully"
            match setupResponse.Data with
            | Some data ->
                printfn $"  Setup Intent ID: {data.SetupIntentId}"
                printfn $"  Customer ID: {data.CustomerId}"
                printfn "  Frontend would use the client_secret to save payment method"
            | None -> ()
        else
            printfn $"[FAIL] Setup endpoint failed: {setupResponse.Error}"

        printfn ""
        printfn "3. Webhook handling simulation..."
        printfn "   In a real application:"
        printfn "   - Stripe sends webhook events to your endpoint"
        printfn "   - Your webhook handler processes payment completions"
        printfn "   - Business logic updates orders, sends emails, etc."
        printfn "   - See WebhookHandler.fs for complete webhook implementation"

        printfn ""
        printfn "=== Integration Flow Summary ==="
        printfn "Frontend Flow:"
        printfn "1. User enters payment details"
        printfn "2. Frontend calls your API to create payment/setup intent"
        printfn "3. Frontend uses Stripe Elements to collect card details"
        printfn "4. Frontend confirms payment using client_secret"
        printfn "5. Stripe processes payment and sends webhook to your backend"
        printfn ""
        printfn "Backend Flow:"
        printfn "1. API endpoints create customers, payment intents, setup intents"
        printfn "2. Webhook endpoints handle payment completion"
        printfn "3. Business logic processes successful payments"
        printfn "4. Database updates, email notifications, order fulfillment"
    }

/// Production deployment checklist
let printProductionChecklist () =
    printfn "=== Production Deployment Checklist ==="
    printfn ""
    printfn "Before going live with FunStripe:"
    printfn ""
    printfn "API Keys:"
    printfn "  □ Replace test keys with live Stripe keys"
    printfn "  □ Store keys securely (environment variables, key vault)"
    printfn "  □ Never commit keys to source control"
    printfn ""
    printfn "Security:"
    printfn "  □ Implement webhook signature verification"
    printfn "  □ Use HTTPS for all endpoints"
    printfn "  □ Validate and sanitize all inputs"
    printfn "  □ Implement rate limiting"
    printfn "  □ Log security events"
    printfn ""
    printfn "Monitoring:"
    printfn "  □ Set up payment monitoring and alerts"
    printfn "  □ Monitor webhook delivery and processing"
    printfn "  □ Track payment success/failure rates"
    printfn "  □ Set up error logging and notifications"
    printfn ""
    printfn "Testing:"
    printfn "  □ Test all payment flows thoroughly"
    printfn "  □ Test error handling scenarios"
    printfn "  □ Verify webhook processing"
    printfn "  □ Test with different card types and currencies"
    printfn "  □ Test 3D Secure flows"
