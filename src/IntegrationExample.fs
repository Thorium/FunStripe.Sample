module IntegrationExample

open System
open StripeService

/// Sample web API endpoints that demonstrate complete integration
/// This shows how you might structure your F# web application

type PaymentRequest = {
    Amount: decimal
    Currency: string
    CustomerEmail: string
    CustomerName: string
}

type SetupRequest = {
    CustomerEmail: string
    CustomerName: string
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

/// Example: Create customer and payment intent endpoint
let createPaymentEndpoint (config: StripeConfig) (request: PaymentRequest) =
    async {
        try
            let nameParts =
                if isNull request.CustomerName then [|""|]
                else request.CustomerName.Split(' ', 2)
            let firstName = nameParts.[0]
            let lastName = if nameParts.Length > 1 then nameParts.[1] else ""

            // 1. Create or get customer
            let! customerResult = createCustomer config firstName lastName request.CustomerEmail

            match customerResult with
            | Ok customer ->
                // 2. Create payment intent
                let amount = formatAmount request.Amount
                let! paymentResult = createPaymentIntent config amount request.Currency (Some customer.Id)

                match paymentResult with
                | Ok paymentIntent ->
                    let response : ApiResponse<PaymentResponseData> =
                        createSuccessResponse {
                            ClientSecret = paymentIntent.ClientSecret
                            PaymentIntentId = paymentIntent.Id
                            CustomerId = customer.Id
                        }
                    return response
                | Error error ->
                    printfn $"Failed to create payment intent: {error}"
                    return createErrorResponse "Failed to create payment intent"
            | Error error ->
                printfn $"Failed to create customer: {error}"
                return createErrorResponse "Failed to create customer"
        with
        | ex ->
            printfn $"Exception in createPaymentEndpoint: {ex.Message}"
            return createErrorResponse "Internal server error"
    }

/// Example: Create customer and setup intent endpoint
let createSetupEndpoint (config: StripeConfig) (request: SetupRequest) =
    async {
        try
            let nameParts =
                if isNull request.CustomerName then [|""|]
                else request.CustomerName.Split(' ', 2)
            let firstName = nameParts.[0]
            let lastName = if nameParts.Length > 1 then nameParts.[1] else ""

            // 1. Create or get customer
            let! customerResult = createCustomer config firstName lastName request.CustomerEmail

            match customerResult with
            | Ok customer ->
                // 2. Create setup intent
                let! setupResult = createSetupIntent config customer.Id

                match setupResult with
                | Ok setupIntent ->
                    let response : ApiResponse<SetupResponseData> =
                        createSuccessResponse {
                            ClientSecret = setupIntent.ClientSecret
                            SetupIntentId = setupIntent.Id
                            CustomerId = customer.Id
                        }
                    return response
                | Error error ->
                    printfn $"Failed to create setup intent: {error}"
                    return createErrorResponse "Failed to create setup intent"
            | Error error ->
                printfn $"Failed to create customer: {error}"
                return createErrorResponse "Failed to create customer"
        with
        | ex ->
            printfn $"Exception in createSetupEndpoint: {ex.Message}"
            return createErrorResponse "Internal server error"
    }

/// Example: Complete integration flow demonstration
let demonstrateCompleteIntegration () =
    async {
        printfn "=== Complete FunStripeLite Integration Demo ==="
        printfn ""

        let config = loadStripeConfig ()

        // Check configuration
        if config.SecretKey = "sk_test_..." then
            printfn "[ERROR] Please configure your Stripe keys in appsettings.json first"
            return ()

        printfn "1. Creating payment endpoint simulation..."
        let paymentRequest = {
            Amount = 29.99m
            Currency = "usd"
            CustomerEmail = "integration.test@example.com"
            CustomerName = "Integration Test"
        }

        let! paymentResponse = createPaymentEndpoint config paymentRequest

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
        }

        let! setupResponse = createSetupEndpoint config setupRequest

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
    printfn "Before going live with FunStripeLite:"
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
