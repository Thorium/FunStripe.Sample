module WebhookEvents

open FunStripe
open Stripe.PaymentMethod

// The handlers below take the FunStripe models (PaymentIntent, SetupIntent, Customer)
// that the webhook handler extracts from an event's `data.object`.

// We leave disputes out for now, but they are important if you have a service where
// your customers want to cancel their payments afterwards.

/// Event processing result
type EventProcessingResult =
    | Success of string
    | HandlerError of string
    | Ignored of string

/// The customer ID of a payment intent. `customer` is an expandable field: a bare ID
/// unless the request asked Stripe to expand it into the full object.
let paymentIntentCustomerId (paymentIntent: PaymentIntent) =
    paymentIntent.Customer
    |> Option.map (function
        | PaymentIntentCustomer'AnyOf.String id -> id
        | PaymentIntentCustomer'AnyOf.Customer customer -> customer.Id
        | PaymentIntentCustomer'AnyOf.DeletedCustomer customer -> customer.Id)

/// The customer ID of a setup intent (see `paymentIntentCustomerId`)
let setupIntentCustomerId (setupIntent: SetupIntent) =
    setupIntent.Customer
    |> Option.map (function
        | SetupIntentCustomer'AnyOf.String id -> id
        | SetupIntentCustomer'AnyOf.Customer customer -> customer.Id
        | SetupIntentCustomer'AnyOf.DeletedCustomer customer -> customer.Id)

/// Business logic for handling payment completion
let handlePaymentSuccess (paymentIntent: PaymentIntent) =
    async {
        // In a real application, you would:
        // 1. Update your database to mark the order as paid
        // 2. Send confirmation emails
        // 3. Trigger fulfillment processes
        // 4. Update customer account status

        printfn $"Processing successful payment: {paymentIntent.Id}"
        printfn $"Amount: {paymentIntent.Amount} {paymentIntent.Currency}"

        match paymentIntentCustomerId paymentIntent with
        | Some customerId ->
            printfn $"Customer: {customerId}"
            // Update customer's order history
            // Send confirmation email
            return Success $"Payment {paymentIntent.Id} processed successfully for customer {customerId}"
        | None ->
            return Success $"Payment {paymentIntent.Id} processed successfully (no customer)"
    }

/// Business logic for handling payment failures
let handlePaymentFailure (paymentIntent: PaymentIntent) =
    async {
        printfn $"Processing failed payment: {paymentIntent.Id}"

        match paymentIntent.LastPaymentError with
        | Some error ->
            let reason = error.Message |> Option.defaultValue "no message"
            let declineCode = error.DeclineCode |> Option.defaultValue "n/a"
            printfn $"Failure reason: {reason} (decline code: {declineCode})"
        | None ->
            printfn "No failure details on the payment intent"
        // Possibly notify customer service
        // Update order status to failed

        return Success $"Payment failure {paymentIntent.Id} processed"
    }

/// Business logic for handling successful setup intents
let handleSetupSuccess (setupIntent: SetupIntent) =
    async {
        printfn $"Processing successful setup intent: {setupIntent.Id}"

        match setupIntentCustomerId setupIntent with
        | Some customerId ->
            match setupIntent.PaymentMethod with
            | Some (StripeId paymentMethodId) ->
                printfn $"Payment method {paymentMethodId} saved for customer {customerId}"
                // Store the payment method reference in your database
                // Enable subscription features for the customer
                // Send confirmation that payment method was saved
                return Success $"Setup intent {setupIntent.Id} processed - payment method saved"
            | None ->
                return Success $"Setup intent {setupIntent.Id} processed"
        | None ->
            return HandlerError $"Setup intent {setupIntent.Id} succeeded but no customer"
    }

/// Business logic for handling customer creation
let handleCustomerCreated (customer: Customer) =
    async {
        printfn $"Processing new customer: {customer.Id}"
        let email = customer.Email |> Option.defaultValue "N/A"
        printfn $"Email: {email}"

        // In a real application:
        // 1. Update your user management system
        // 2. Send welcome emails
        // 3. Set up customer portal access
        // 4. Initialize customer preferences

        return Success $"Customer {customer.Id} creation processed"
    }
