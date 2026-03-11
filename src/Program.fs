// FunStripeLite Sample Application
// This demonstrates common payment processing patterns using FunStripeLite

open System
open StripeService

/// Sample customer data for demonstration
type CustomerData = {
    FirstName: string
    LastName: string
    Email: string
}

/// Demo function to create a customer and setup payment
let demoCustomerAndSetup () =
    async {
        printfn "=== FunStripeLite Sample: Customer Creation and Setup Intent ==="

        let config = loadStripeConfig ()

        // Sample customer data (safe for demo)
        let customer = {
            FirstName = "John"
            LastName = "Doe"
            Email = "john.doe+demo@example.com"
        }

        printfn $"Creating customer: {customer.FirstName} {customer.LastName} ({customer.Email})"

        let! customerResult = createCustomer config customer.FirstName customer.LastName customer.Email

        match customerResult with
        | Ok stripeCustomer ->
            printfn $"[OK] Customer created successfully: {stripeCustomer.Id}"

            printfn "Creating setup intent for saving payment methods..."
            let! setupResult = createSetupIntent config stripeCustomer.Id

            match setupResult with
            | Ok setupIntent ->
                printfn $"[OK] Setup intent created: {setupIntent.Id}"
                let previewLen = min setupIntent.ClientSecret.Length 20
                printfn $"Client secret: {setupIntent.ClientSecret.Substring(0, previewLen)}..."
                printfn "Use this client secret in your frontend to collect payment method"
            | Error error ->
                printfn "[FAIL] Failed to create setup intent"
                handleStripeError error

        | Error error ->
            printfn "[FAIL] Failed to create customer"
            handleStripeError error
    }

/// Demo function to create a payment intent
let demoPaymentIntent () =
    async {
        printfn "\n=== FunStripeLite Sample: Payment Intent Creation ==="

        let config = loadStripeConfig ()

        // Create a payment for $20.00 USD
        let amount = formatAmount 20.00m
        let currency = "usd"

        printfn $"Creating payment intent for ${amount / 100L}.{amount % 100L:D2} {currency.ToUpper()}"

        let! paymentResult = createPaymentIntent config amount currency None

        match paymentResult with
        | Ok paymentIntent ->
            printfn $"[OK] Payment intent created: {paymentIntent.Id}"
            printfn $"Status: Processing"
            printfn $"Client secret: {paymentIntent.ClientSecret.Substring(0, 20)}..."
            printfn "Use this client secret in your frontend to process the payment"
        | Error error ->
            printfn "[FAIL] Failed to create payment intent"
            handleStripeError error
    }

/// Demo function showing complete payment flow
let demoCompleteFlow () =
    async {
        printfn "\n=== FunStripeLite Sample: Complete Payment Flow ==="

        let config = loadStripeConfig ()

        // 1. Create customer
        let customer = {
            FirstName = "Jane"
            LastName = "Smith"
            Email = "jane.smith+demo@example.com"
        }

        let! customerResult = createCustomer config customer.FirstName customer.LastName customer.Email

        match customerResult with
        | Ok stripeCustomer ->
            printfn $"[OK] Step 1: Customer created: {stripeCustomer.Id}"

            // 2. Create payment intent for the customer
            let amount = formatAmount 25.50m
            let! paymentResult = createPaymentIntent config amount "usd" (Some stripeCustomer.Id)

            match paymentResult with
            | Ok paymentIntent ->
                printfn $"[OK] Step 2: Payment intent created: {paymentIntent.Id}"
                printfn $"Amount: ${amount / 100L}.{amount % 100L:D2} USD"

                printfn "\nNext steps for your application:"
                printfn "1. Send client_secret to frontend"
                printfn "2. Use Stripe Elements to collect payment details"
                printfn "3. Confirm payment using stripe.confirmPayment()"
                printfn "4. Handle webhooks for payment completion"

            | Error error ->
                printfn "[FAIL] Step 2 failed: Could not create payment intent"
                handleStripeError error

        | Error error ->
            printfn "[FAIL] Step 1 failed: Could not create customer"
            handleStripeError error
    }

/// Main entry point
[<EntryPoint>]
let main argv =
    printfn "FunStripeLite Sample Application"
    printfn "======================================"
    printfn ""
    printfn "This sample demonstrates key FunStripeLite patterns:"
    printfn "- Customer creation"
    printfn "- Setup intents (for saving payment methods)"
    printfn "- Payment intents (for processing payments)"
    printfn ""
    printfn "IMPORTANT: Make sure to replace the API keys in appsettings.json"
    printfn "with your actual Stripe test keys before running this sample."
    printfn ""

    // Load config from appsettings.json
    let config = loadStripeConfig ()
    if config.SecretKey = "sk_test_..." then
        printfn "[ERROR] Please configure your Stripe test keys in appsettings.json"
        printfn "You can find your test keys at: https://dashboard.stripe.com/test/apikeys"
        1
    else
        try
            // Run demonstrations
            demoCustomerAndSetup () |> Async.RunSynchronously
            demoPaymentIntent () |> Async.RunSynchronously
            demoCompleteFlow () |> Async.RunSynchronously

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
