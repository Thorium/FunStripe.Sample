// Gym membership: a monthly subscription paid by SEPA Direct Debit, with a joining fee, then a
// freeze, the end of the freeze, a notice of cancellation and a link to the member portal.
//
//   dotnet fsi examples/Memberships.fsx
//
// The code is in src/Memberships.fs (and src/DirectDebit.fs for the mandate). Needs SEPA
// Direct Debit turned on in the Dashboard and STRIPE_TEST_API_KEY (see Common.fsx). The
// portal link needs the customer portal saved once under Settings > Billing > Customer portal.
//
// The script also prints the link of an online sign-up page, which collects the IBAN itself.
// To see the months go by without waiting, attach the customer to a test clock (Billing >
// Test clocks in the Dashboard) and advance it.

#load "Common.fsx"
#load "../src/DirectDebit.fs"
#load "../src/Memberships.fs"

open FunStripe.IsoTypes
open Common
open StripeService
open DirectDebit
open Memberships

let operation = newOperation "gym"
let memberId = "member-7"

// 1. The plan: 49.90 EUR a month and a 29.00 EUR joining fee
let catalog =
    ensureCatalog settings operation { Currency = IsoCurrencyCode.EUR; MonthlyFee = formatAmount 49.90m; JoiningFee = formatAmount 29m }
    |> step "Finding (or creating) the membership prices"
printfn $"  Monthly {catalog.MonthlyPriceId}, joining fee {catalog.JoiningFeePriceId}"

// 2a. Online sign-up: Stripe's page collects the IBAN and the mandate (or a card)
let signup =
    createSignupSession settings (idempotencyKeyFor operation "signup") catalog "member-8" "sam.smith+gym@example.com" "https://example.com/welcome" "https://example.com/join"
    |> step "Creating an online sign-up page"
printfn $"  {orNone signup.Url}"

// 2b. At the front desk: the member's bank account and mandate, then the membership
let customer =
    createCustomer settings (idempotencyKeyFor operation "customer") "Jenny" "Rosen" "jenny.rosen+gym@example.com"
    |> step "Creating the member's customer"
let acceptance = { IpAddress = "203.0.113.7"; UserAgent = "Mozilla/5.0 (front desk tablet)" }
let setupIntent =
    setupSepaWithIban settings (idempotencyKeyFor operation "mandate") customer.Id "Jenny Rosen" "jenny.rosen+gym@example.com" TestIbans.succeeds acceptance
    |> step "Saving the bank account and mandate"

match savedBankAccount setupIntent with
| None ->
    eprintfn "The bank account was not saved"
    exit 1
| Some account ->
    let membership =
        startMembership settings (idempotencyKeyFor operation "membership") catalog memberId customer.Id account.PaymentMethodId
        |> step "Starting the membership"
    // Nothing has been paid yet: the first debit is on its way
    printfn $"  Subscription {membership.Id}: {membership.Status}, access {accessOf false membership}"

    // 3. A freeze for a broken ankle, and back
    let frozen = freezeMembership settings membership.Id |> step "Freezing the membership"
    printfn $"  {frozen.Status}, access {accessOf false frozen}"
    let back = unfreezeMembership settings membership.Id |> step "Ending the freeze"
    // Paused until the debit for the new month succeeds
    printfn $"  {back.Status}, access {accessOf false back}"

    // 4. Notice to leave at the end of the paid month
    let leaving = cancelAtPeriodEnd settings membership.Id true |> step "Giving notice"
    printfn $"  Cancels at the end of the period: {leaving.CancelAtPeriodEnd}"

    // 5. Self-service
    match memberPortalUrl settings customer.Id "https://example.com/account" |> Async.RunSynchronously with
    | Ok url -> printfn $"Member portal: {url}"
    | Error error -> printfn $"No member portal link ({describeStripeError error}); save the portal settings in the Dashboard first"
