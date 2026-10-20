// Card issuing: a cardholder, a virtual card and a test purchase with it, then your own card
// design and a physical card printed with it.
//
//   dotnet fsi examples/Issuing.fsx                      without a card logo
//   dotnet fsi examples/Issuing.fsx -- path/to/logo.png  with your logo, for bundles that print one
//
// The code is in src/Issuing.fs. Needs an account with Issuing enabled in test mode
// (Dashboard > Issuing), an Issuing balance in EUR to fund the test purchase, and
// STRIPE_TEST_API_KEY (see Common.fsx). The cardholder here is in Finland: change the address
// and currency for a program in another country.

#load "Common.fsx"
#load "../src/Issuing.fs"

open System
open System.IO
open System.Net.Http
open FunStripe.IsoTypes
open Stripe.Issuing
open Common
open StripeService
open Issuing

let operation = newOperation "issuing"
let currency = IsoCurrencyCode.EUR
let address = {
    Line1 = "Mannerheimintie 1"
    Line2 = None
    City = "Helsinki"
    PostalCode = "00100"
    State = None
    Country = IsoCountryCode.FI
}
// What your server saw when the cardholder accepted the issuer's terms
let terms = { AcceptedAt = DateTime.UtcNow; IpAddress = "203.0.113.7"; UserAgent = "Mozilla/5.0 (FunStripe.Sample example)" }

// 1. Cardholder and virtual card
let cardholder =
    createCardholder settings (idempotencyKeyFor operation "cardholder") "Jenny" "Rosen" "jenny.rosen+issuing@example.com" address terms
    |> step "Creating a cardholder"

let virtualCard =
    createVirtualCard settings (idempotencyKeyFor operation "virtual-card") cardholder.Id currency (formatAmount 500m)
    |> step "Creating a virtual card with a 500 EUR monthly limit"
printfn $"  {virtualCard.Brand} ****{virtualCard.Last4}, expires {virtualCard.ExpMonth}/{virtualCard.ExpYear}"

// 2. A purchase with it, as a merchant would make one. With an authorization webhook set up,
//    Stripe asks it now (see Issuing.handleAuthorizationRequest).
let authorization =
    TestMode.purchase settings virtualCard.Id (formatAmount 25m) currency "FunStripe Coffee"
    |> step "Buying 25 EUR of coffee"
printfn $"  Authorization {authorization.Id}: approved = {authorization.Approved}"
if authorization.Approved then
    let captured = TestMode.capture settings authorization.Id |> step "Capturing it"
    printfn $"  Status {captured.Status}"
else
    printfn "  Declined: check the Issuing balance and the card's spending controls"

// 3. Your card design: a physical bundle, your logo and the carrier letter text
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
            use http = new HttpClient()
            uploadCardLogo http (settings |> withKey (idempotencyKeyFor operation "logo")) stripeFilesBaseUrl (Path.GetFileName path) (File.ReadAllBytes path)
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

    TestMode.deliverCard settings physicalCard.Id |> step "Delivering it (test mode)" |> ignore
    printfn "  Activate it (status active) once the cardholder confirms they received it"
