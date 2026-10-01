// FunStripe Sample Application
// This demonstrates common payment processing patterns using FunStripe

open System
open FunStripe
open FunStripe.AsyncResultCE
open FunStripe.IsoTypes
open StripeService

/// Sample customer data for demonstration
type CustomerData = {
    FirstName: string
    LastName: string
    Email: string
}

/// The start of a client secret, enough to recognise it in the output
let previewSecret (clientSecret: string option) =
    match clientSecret with
    | Some secret -> secret.Substring(0, min secret.Length 20) + "..."
    | None -> "(none)"

/// The operation ID the idempotency keys of one demo run are derived from. A fresh GUID
/// fits here because every run is a new operation. A real application uses an ID it has
/// stored (an order ID, say), so that a retry of the same operation sends the same keys.
let newOperationId (demo: string) =
    match OperationId.tryCreate $"demo-{demo}-{Guid.NewGuid()}" with
    | Some operationId -> operationId
    | None -> invalidArg (nameof demo) "The demo name is too long for an operation ID"

/// Demo function to create a customer and setup payment
let demoCustomerAndSetup (settings: RestApi.StripeApiSettings) =
    async {
        printfn "=== FunStripe Sample: Customer Creation and Setup Intent ==="

        // Sample customer data (safe for demo)
        let customer = {
            FirstName = "John"
            LastName = "Doe"
            Email = "john.doe+demo@example.com"
        }

        printfn $"Creating customer: {customer.FirstName} {customer.LastName} ({customer.Email})"

        let operationId = newOperationId "setup"

        match! createCustomer settings (idempotencyKeyFor operationId "customer") customer.FirstName customer.LastName customer.Email with
        | Ok stripeCustomer ->
            printfn $"[OK] Customer created successfully: {stripeCustomer.Id}"

            printfn "Creating setup intent for saving payment methods..."

            match! createSetupIntent settings (idempotencyKeyFor operationId "setup-intent") stripeCustomer.Id with
            | Ok setupIntent ->
                printfn $"[OK] Setup intent created: {setupIntent.Id}"
                printfn $"Client secret: {previewSecret setupIntent.ClientSecret}"
                printfn "Use this client secret in your frontend to collect payment method"
            | Error error ->
                printfn "[FAIL] Failed to create setup intent"
                handleStripeError error

        | Error error ->
            printfn "[FAIL] Failed to create customer"
            handleStripeError error
    }

/// Demo function to create a payment intent
let demoPaymentIntent (settings: RestApi.StripeApiSettings) =
    async {
        printfn "\n=== FunStripe Sample: Payment Intent Creation ==="

        // Create a payment for $20.00 USD
        let amount = formatAmount 20.00m
        let currency = IsoCurrencyCode.USD

        printfn $"Creating payment intent for ${amount / 100}.{amount % 100:D2} {currency}"

        let operationId = newOperationId "payment"

        match! createPaymentIntent settings (idempotencyKeyFor operationId "payment-intent") amount currency None with
        | Ok paymentIntent ->
            printfn $"[OK] Payment intent created: {paymentIntent.Id}"
            printfn $"Status: {paymentIntent.Status}"
            printfn $"Client secret: {previewSecret paymentIntent.ClientSecret}"
            printfn "Use this client secret in your frontend to process the payment"
        | Error error ->
            printfn "[FAIL] Failed to create payment intent"
            handleStripeError error
    }

/// Demo function showing complete payment flow
let demoCompleteFlow (settings: RestApi.StripeApiSettings) =
    async {
        printfn "\n=== FunStripe Sample: Complete Payment Flow ==="

        let customer = {
            FirstName = "Jane"
            LastName = "Smith"
            Email = "jane.smith+demo@example.com"
        }

        let amount = formatAmount 25.50m
        let operationId = newOperationId "complete-flow"

        // FunStripe's asyncResult chains the calls and stops at the first Stripe error
        let flow =
            asyncResult {
                // 1. Create customer
                let! stripeCustomer =
                    createCustomer settings (idempotencyKeyFor operationId "customer") customer.FirstName customer.LastName customer.Email
                printfn $"[OK] Step 1: Customer created: {stripeCustomer.Id}"

                // 2. Create payment intent for the customer
                let! paymentIntent =
                    createPaymentIntent settings (idempotencyKeyFor operationId "payment-intent") amount IsoCurrencyCode.USD (Some stripeCustomer.Id)
                printfn $"[OK] Step 2: Payment intent created: {paymentIntent.Id}"
                return paymentIntent
            }

        match! flow with
        | Ok _ ->
            printfn $"Amount: ${amount / 100}.{amount % 100:D2} USD"

            printfn "\nNext steps for your application:"
            printfn "1. Send client_secret to frontend"
            printfn "2. Use Stripe Elements to collect payment details"
            printfn "3. Confirm payment using stripe.confirmPayment()"
            printfn "4. Handle webhooks for payment completion"

        | Error error ->
            printfn "[FAIL] Complete payment flow failed"
            handleStripeError error
    }

/// Demo function running a signed webhook delivery through the webhook handler.
/// Needs no Stripe account: the payload is a sample and the signature is computed here.
let demoWebhook () =
    async {
        printfn "\n=== FunStripe Sample: Webhook Handling ==="

        // A secret for this local run only. A real endpoint uses the signing secret Stripe
        // shows for it (WebhookEndpointSecret in appsettings.json).
        let demoSecret = "whsec_local_demo_only"
        let webhookConfig : WebhookHandler.WebhookConfig = { EndpointSecret = demoSecret }
        let payload = WebhookSamples.paymentIntentSucceeded
        let signature =
            WebhookSamples.signatureHeader demoSecret payload (DateTimeOffset.UtcNow.ToUnixTimeSeconds())

        let! _, statusCode = WebhookHandler.handleWebhookRequest webhookConfig signature payload
        printfn $"Webhook endpoint would answer HTTP {statusCode}"

        let! _, tamperedStatusCode =
            WebhookHandler.handleWebhookRequest webhookConfig signature (payload.Replace("2000", "1"))
        printfn $"The same signature on a tampered payload: HTTP {tamperedStatusCode}"
    }

/// Main entry point
[<EntryPoint>]
let main argv =
    printfn "FunStripe Sample Application"
    printfn "======================================"
    printfn ""
    printfn "This sample demonstrates key FunStripe patterns:"
    printfn "- Customer creation"
    printfn "- Setup intents (for saving payment methods)"
    printfn "- Payment intents (for processing payments)"
    printfn "- Webhook signature verification and event handling"
    printfn ""
    printfn $"FunStripe models target Stripe API version {Config.DefaultStripeApiVersion}"
    printfn ""

    try
        let config = loadStripeConfig ()

        match keyMode config with
        | NotConfigured ->
            printfn "[INFO] No Stripe test key configured, skipping the demos that call the Stripe API."
            printfn "       Set the STRIPE_TEST_API_KEY environment variable, or TestSecretKey in"
            printfn "       appsettings.json, to a test key from https://dashboard.stripe.com/test/apikeys"
        | LiveMode ->
            // The demos create customers and intents, which do not belong in a live account
            printfn "[INFO] A live Stripe key is configured, skipping the demos that call the Stripe API."
            printfn "       They only run with a test key."
        | TestMode ->
            let settings = createSettings config
            demoCustomerAndSetup settings |> Async.RunSynchronously
            demoPaymentIntent settings |> Async.RunSynchronously
            demoCompleteFlow settings |> Async.RunSynchronously

        demoWebhook () |> Async.RunSynchronously

        printfn "\n=== Sample completed successfully! ==="
        printfn "Check the frontend/ directory for Stripe Elements integration examples."
        printfn "Check the webhooks/ directory for webhook handling examples."
        printfn "Check IntegrationExample.fs for web API integration patterns."
        0
    with
    | ex ->
        printfn $"\n[ERROR] Error running sample: {ex.Message}"
        printfn $"Stack trace: {ex.StackTrace}"
        1
