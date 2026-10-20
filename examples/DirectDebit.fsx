// Direct debit: save a SEPA bank account with a mandate, then debit it off-session.
//
//   dotnet fsi examples/DirectDebit.fsx            the debit succeeds
//   dotnet fsi examples/DirectDebit.fsx -- fails   the debit fails a few minutes later
//
// The code is in src/DirectDebit.fs. Needs SEPA Direct Debit turned on in the Dashboard
// (Settings > Payment methods) and STRIPE_TEST_API_KEY (see Common.fsx).

#load "Common.fsx"
#load "../src/DirectDebit.fs"

open Common
open StripeService
open DirectDebit

let operation = newOperation "direct-debit"
let iban = if scriptArgs = [ "fails" ] then TestIbans.fails else TestIbans.succeeds

let customer =
    createCustomer settings (idempotencyKeyFor operation "customer") "Jenny" "Rosen" "jenny.rosen+direct-debit@example.com"
    |> step "Creating a customer"

// 1. The usual flow: a setup intent the customer confirms in the browser with the Payment
//    Element, which collects the IBAN and shows the mandate text
let browserSetup =
    createSetupIntent settings (idempotencyKeyFor operation "setup-intent") SepaDebit customer.Id
    |> step "Creating a setup intent for the Payment Element"
printfn "  Open frontend/direct-debit.html and paste this client secret to try the browser flow:"
printfn $"  {orNone browserSetup.ClientSecret}"

// 2. The same in one server-side call, so that this script can carry on without a browser.
//    The acceptance is what your server saw when the customer accepted the mandate.
let acceptance = { IpAddress = "203.0.113.7"; UserAgent = "Mozilla/5.0 (FunStripe.Sample example)" }
let setupIntent =
    setupSepaWithIban settings (idempotencyKeyFor operation "setup-with-iban") customer.Id "Jenny Rosen" "jenny.rosen+direct-debit@example.com" iban acceptance
    |> step $"Saving the test IBAN {iban}"
printfn $"  Setup intent {setupIntent.Id}: {setupIntent.Status}"

match savedBankAccount setupIntent with
| None ->
    eprintfn "The setup intent did not save the account"
    exit 1
| Some account ->
    printfn $"  Payment method {account.PaymentMethodId}, mandate {orNone account.MandateId}"

    // 3. Later, e.g. when an invoice is due: debit the account without the customer
    let amount = formatAmount 19.99m
    let payment =
        chargeSavedAccount settings (idempotencyKeyFor operation "charge") SepaDebit account amount
        |> step "Debiting 19.99 EUR"
    printfn $"  Payment intent {payment.Id}: {payment.Status}"

printfn ""
printfn "The debit stays 'processing' until the bank confirms it: in test mode a few minutes,"
printfn "in live mode several business days. Stripe then sends payment_intent.succeeded (or"
printfn "payment_intent.payment_failed). DirectDebit.processEvent shows what to do with each event;"
printfn "to see them arrive, run: stripe listen --events payment_intent.succeeded,payment_intent.payment_failed"
