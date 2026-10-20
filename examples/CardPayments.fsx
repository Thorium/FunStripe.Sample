// Card payments: a customer, a payment intent for a one-time payment, and a setup intent that
// saves a card for later. The frontend confirms them with the client secrets printed here.
//
//   dotnet fsi examples/CardPayments.fsx
//
// The code is in src/StripeService.fs; src/Program.fs runs the same demo as a console app.
// Needs STRIPE_TEST_API_KEY (see Common.fsx).

#load "Common.fsx"

open FunStripe.IsoTypes
open Common
open StripeService

let operation = newOperation "cards"

let customer =
    createCustomer settings (idempotencyKeyFor operation "customer") "Jenny" "Rosen" "jenny.rosen+cards@example.com"
    |> step "Creating a customer"

let payment =
    createPaymentIntent settings (idempotencyKeyFor operation "payment-intent") (formatAmount 20m) IsoCurrencyCode.USD (Some customer.Id)
    |> step "Creating a payment intent for 20.00 USD"
printfn $"  {payment.Id}: {payment.Status}, client secret {orNone payment.ClientSecret}"

let setup =
    createSetupIntent settings (idempotencyKeyFor operation "setup-intent") customer.Id
    |> step "Creating a setup intent to save a card"
printfn $"  {setup.Id}: {setup.Status}, client secret {orNone setup.ClientSecret}"

printfn ""
printfn "Confirm them in frontend/index.html with a test card such as 4242 4242 4242 4242."
printfn "Stripe then sends payment_intent.succeeded and setup_intent.succeeded, which"
printfn "webhooks/WebhookHandler.fs handles."
