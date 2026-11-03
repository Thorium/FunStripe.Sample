// Card issuing: a cardholder, a virtual card and a test purchase with it, then your own card
// design and a physical card printed with it.
//
//   dotnet fsi examples/Issuing.fsx                      without a card logo
//   dotnet fsi examples/Issuing.fsx -- path/to/logo.png  with your logo, for bundles that print one
//
// The program follows your Stripe account's country (Issuing.regionOf): GBP cards to a cardholder in
// London for a UK account, EUR cards to one in Helsinki for a euro area account, USD cards to one in
// San Francisco for a US account. The UK program has been run against Stripe test mode; the euro
// area and US ones follow Stripe's documentation but have not.
//
// The code is in src/Issuing.fs. Needs an account with Issuing enabled in test mode
// (Dashboard > Issuing) and STRIPE_TEST_API_KEY (see Common.fsx). The
// script funds the Issuing balance with a test top-up first: with an empty balance Stripe
// declines every purchase as insufficient_funds.

#load "Common.fsx"
#load "../src/Issuing.fs"

open System
open System.IO
open FunStripe.IsoTypes
open Stripe.Issuing
open Common
open StripeService
open Issuing

let operation = newOperation "issuing"
let account =
    StripeRequest.PaymentMethod.Account.Retrieve settings (StripeRequest.PaymentMethod.Account.RetrieveOptions.New())
    |> step "Reading your Stripe account"
let region =
    match account.Country |> Option.bind regionOf with
    | Some region -> region
    | None ->
        printfn $"Issuing here is for the UK, the euro area and the US; this account is in %A{account.Country}"
        exit 1
let currency = currencyOf region
printfn $"  {region} program: {currency} cards"
let address, phoneNumber =
    match region with
    | UnitedKingdom ->
        { Line1 = "1 High Street"; Line2 = None; City = "London"; PostalCode = "SW1A 1AA"; State = None; Country = IsoCountryCode.GB }, "+447700900123"
    | EuroArea ->
        { Line1 = "Mannerheimintie 1"; Line2 = None; City = "Helsinki"; PostalCode = "00100"; State = None; Country = IsoCountryCode.FI }, "+358401234567"
    | UnitedStates ->
        { Line1 = "1 Market Street"; Line2 = None; City = "San Francisco"; PostalCode = "94105"; State = Some "CA"; Country = IsoCountryCode.US }, "+14155550123"
// What your server saw when the cardholder accepted the issuer's terms
let terms = { AcceptedAt = DateTime.UtcNow; IpAddress = "203.0.113.7"; UserAgent = "Mozilla/5.0 (FunStripe.Sample example)" }

// 1. Cardholder and virtual card
let cardholder =
    createCardholder settings (idempotencyKeyFor operation "cardholder") "Jenny" "Rosen" "jenny.rosen+issuing@example.com" phoneNumber address terms
    |> step "Creating a cardholder"

let virtualCard =
    createVirtualCard settings (idempotencyKeyFor operation "virtual-card") cardholder.Id currency (formatAmount 500m)
    |> step $"Creating a virtual card with a 500 {currency} monthly limit"
printfn $"  {virtualCard.Brand} ****{virtualCard.Last4}, expires {virtualCard.ExpMonth}/{virtualCard.ExpYear}"

// 2. Money for it: the bank details that fund the Issuing balance (UK and euro area), then test money
match bankTransferTypeOf region with
| Some transferType ->
    let instructions = fundingInstructions settings currency transferType |> step "Asking for the bank details of the Issuing balance"
    for account in instructions.BankTransfer.FinancialAddresses do
        let networks = String.Join(", ", account.SupportedNetworks)
        account.SortCode |> Option.iter (fun details -> printfn $"  Pay in to {details.AccountHolderName}, {details.SortCode} {details.AccountNumber} ({networks})")
        account.Iban |> Option.iter (fun details -> printfn $"  Pay in to {details.AccountHolderName}, IBAN {details.Iban} ({networks})")
| None ->
    printfn "  US: a top-up pulls the money from your bank account (wires and ACH are set up in the Dashboard)"

let balanceBefore = issuingBalance settings currency |> step "Reading the Issuing balance"
match region with
| UnitedStates ->
    topUpIssuingBalance settings (idempotencyKeyFor operation "top-up") (formatAmount 100m) currency "FunStripe sample"
    |> step $"Topping up 100 {currency} from your bank account (test mode)" |> ignore
| UnitedKingdom
| EuroArea ->
    TestMode.fundBalance settings (formatAmount 100m) currency |> step $"Adding 100 {currency} to the Issuing balance (test mode)" |> ignore
// Stripe credits the top-up after a few seconds
let credited =
    TestMode.waitForBalance settings currency (balanceBefore + formatAmount 100m) (TimeSpan.FromSeconds 1.) (TimeSpan.FromSeconds 30.)
    |> step "Waiting for Stripe to credit it"
match credited with
| Some available -> printfn $"  Issuing balance: {available} (smallest unit of {currency})"
| None -> printfn "  The top-up is not in the Issuing balance after 30 seconds: the purchase below may be declined as insufficient_funds"

// 3. A purchase with it, as a merchant would make one. With an authorization webhook set up,
//    Stripe asks it now (see Issuing.handleAuthorizationRequest).
let authorization =
    TestMode.purchase settings virtualCard.Id (formatAmount 25m) currency "FunStripe Coffee"
    |> step $"Buying 25 {currency} of coffee"
printfn $"  Authorization {authorization.Id}: approved = {authorization.Approved}, holds {heldAmount authorization}"
if authorization.Approved then
    let captured = TestMode.capture settings authorization.Id |> step "Capturing it"
    printfn $"  Status {captured.Status}"
else
    printfn "  Declined: check the Issuing balance and the card's spending controls"

// 4. Your card design: a physical bundle, your logo and the carrier letter text
let bundles = activeBundles settings |> step "Listing the physical bundles"
for bundle in bundles do
    printfn $"  {bundle.Id} {bundle.Name} ({bundle.Type}): logo {bundle.Features.CardLogo}, carrier text {bundle.Features.CarrierText}"

let logoPath = scriptArgs |> List.tryHead
let carrierText = Some { HeaderTitle = Some "Welcome"; HeaderBody = Some "Here is your new company card."; FooterTitle = None; FooterBody = None }

// The first bundle the design fits
let fitting =
    bundles
    |> List.tryFind (fun bundle ->
        let text = if bundle.Features.CarrierText = IssuingPhysicalBundleFeaturesCarrierText.Unsupported then None else carrierText
        designProblems bundle (logoPath |> Option.map (fun _ -> "logo")) text |> List.isEmpty)

match fitting with
| None ->
    printfn "No bundle fits this design: pass a logo for bundles that require one"
| Some bundle ->
    let text = if bundle.Features.CarrierText = IssuingPhysicalBundleFeaturesCarrierText.Unsupported then None else carrierText

    let logoFileId =
        logoPath
        |> Option.map (fun path ->
            uploadCardLogo (settings |> withKey (idempotencyKeyFor operation "logo")) (Path.GetFileName path) (File.ReadAllBytes path)
            |> step $"Uploading {path}")

    let design =
        createDesign settings (idempotencyKeyFor operation "design") bundle.Id "FunStripe sample card" $"funstripe-sample-{Guid.NewGuid():N}" logoFileId text
        |> step $"Creating a design on {bundle.Name}"
    printfn $"  Design {design.Id}: {design.Status}"

    // Stripe reviews a design before cards can use it; in test mode, approve it yourself
    let active = TestMode.activateDesign settings design.Id |> step "Approving the design (test mode)"
    printfn $"  Design {active.Id}: {active.Status}"

    // 4. A physical card printed with the design
    let physicalCard =
        createPhysicalCard settings (idempotencyKeyFor operation "physical-card") cardholder.Id currency (formatAmount 500m) active.Id "Jenny Rosen" address
        |> step "Ordering a physical card"
    printfn $"  {physicalCard.Brand} ****{physicalCard.Last4}: {physicalCard.Status}"

    TestMode.shipCard settings physicalCard.Id |> step "Posting it (test mode)" |> ignore
    TestMode.deliverCard settings physicalCard.Id |> step "Delivering it (test mode)" |> ignore
    printfn "  Activate it (status active) once the cardholder confirms they received it"
