module Issuing

// Card issuing with Stripe Issuing: create your own payment cards, virtual or physical, for
// your employees or customers, with your own design and spending rules.
//
//   1. Create a cardholder (`createCardholder`): the person whose name is on the card
//   2. Virtual card (`createVirtualCard`): usable online at once
//   3. Physical card with your design:
//      a. pick a physical bundle, the card stock and packaging (`activeBundles`)
//      b. upload your logo (`uploadCardLogo`) and create a personalization design with it
//         and the carrier letter text (`createDesign`). Stripe reviews the design;
//         `issuing_personalization_design.activated` (or `.rejected`) tells you the outcome.
//      c. create the card with the design and a shipping address (`createPhysicalCard`)
//   4. Approve or decline each purchase in real time (`handleAuthorizationRequest`, served on
//      a webhook endpoint of its own), on top of the spending limits set on the card
//   5. Follow authorizations and transactions on your regular webhook endpoint (`processEvent`)
//
// Issuing needs a Stripe account that is approved for it, and an Issuing balance to fund the
// purchases (`fundingInstructions`): with an empty balance Stripe declines every purchase as
// `insufficient_funds`. See examples/Issuing.fsx for a run against Stripe test mode.

open System
open FunStripe
open FunStripe.AsyncResultCE
open FunStripe.IsoTypes
open Stripe.Event
open Stripe.Issuing
open Stripe.IssuingCard
open Stripe.IssuingPersonalizationDesign
open Stripe.PaymentMethod
open StripeRequest.Issuing
open StripeRequest.TestHelpers
open StripeService
open WebhookHandler

/// A postal address, for the cardholder's billing address and for shipping cards
type Address = {
    Line1: string
    Line2: string option
    City: string
    PostalCode: string
    /// Required for US addresses
    State: string option
    Country: IsoCountryCode
}

/// The cardholder's acceptance of the card issuer's terms, which cardholders in the EEA and
/// the UK (and those of some US programs) must give before a card can be issued to them
type TermsAcceptance = {
    AcceptedAt: DateTime
    /// The IP address the cardholder accepted the terms from
    IpAddress: string
    UserAgent: string
}

/// Request options for an individual cardholder. `phoneNumber` (E.164, e.g. +447700900123) gets the
/// one-time passwords of 3D Secure: Stripe refuses to create a card in the UK or the EEA for a
/// cardholder without one.
let cardholderOptions (firstName: string) (lastName: string) (email: string) (phoneNumber: string) (billingAddress: Address) (terms: TermsAcceptance) =
    IssuingCardholders.CreateOptions.New(
        type' = IssuingCardholders.Create'Type.Individual,
        name = $"{firstName} {lastName}",
        email = email,
        phoneNumber = phoneNumber,
        billing =
            IssuingCardholders.Create'Billing.New(
                address =
                    IssuingCardholders.Create'BillingAddress.New(
                        line1 = billingAddress.Line1,
                        ?line2 = billingAddress.Line2,
                        city = billingAddress.City,
                        postalCode = billingAddress.PostalCode,
                        ?state = billingAddress.State,
                        country = billingAddress.Country
                    )
            ),
        individual =
            IssuingCardholders.Create'Individual.New(
                firstName = firstName,
                lastName = lastName,
                cardIssuing =
                    IssuingCardholders.Create'IndividualCardIssuing.New(
                        userTermsAcceptance =
                            IssuingCardholders.Create'IndividualCardIssuingUserTermsAcceptance.New(
                                date = terms.AcceptedAt,
                                ip = terms.IpAddress,
                                userAgent = Choice1Of2 terms.UserAgent
                            )
                    )
            ),
        status = IssuingCardholders.Create'Status.Active
    )

/// Creates a cardholder
let createCardholder (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (firstName: string) (lastName: string) (email: string) (phoneNumber: string) (billingAddress: Address) (terms: TermsAcceptance) =
    cardholderOptions firstName lastName email phoneNumber billingAddress terms
    |> IssuingCardholders.Create (settings |> withKey idempotencyKey)

/// A monthly spending limit in the smallest unit of the card's currency. Stripe declines a
/// purchase that would go over it before your authorization webhook is asked.
let private monthlyLimit (amount: int) =
    IssuingCards.Create'SpendingControls.New(
        spendingLimits = [
            IssuingCards.Create'SpendingControlsSpendingLimits.New(
                amount = amount,
                interval = IssuingCards.Create'SpendingControlsSpendingLimitsInterval.Monthly
            )
        ]
    )

/// Request options for a virtual card, active at once
let virtualCardOptions (cardholderId: string) (currency: IsoCurrencyCode) (monthlySpendingLimit: int) =
    IssuingCards.CreateOptions.New(
        currency = currency,
        type' = IssuingCards.Create'Type.Virtual,
        cardholder = cardholderId,
        spendingControls = monthlyLimit monthlySpendingLimit,
        status = IssuingCards.Create'Status.Active
    )

/// Creates a virtual card. Show its number to the cardholder with Issuing Elements in your
/// frontend, so that it never passes through your server.
let createVirtualCard (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (cardholderId: string) (currency: IsoCurrencyCode) (monthlySpendingLimit: int) =
    virtualCardOptions cardholderId currency monthlySpendingLimit
    |> IssuingCards.Create (settings |> withKey idempotencyKey)

/// Request options for a physical card printed with a personalization design and posted to
/// `shipTo`. It is created inactive: activate it (status `active`) once the cardholder
/// confirms they received it, so that a card lost in the post cannot be used.
let physicalCardOptions (cardholderId: string) (currency: IsoCurrencyCode) (monthlySpendingLimit: int) (personalizationDesignId: string) (recipientName: string) (shipTo: Address) =
    IssuingCards.CreateOptions.New(
        currency = currency,
        type' = IssuingCards.Create'Type.Physical,
        cardholder = cardholderId,
        personalizationDesign = personalizationDesignId,
        shipping =
            IssuingCards.Create'Shipping.New(
                name = recipientName,
                address =
                    IssuingCards.Create'ShippingAddress.New(
                        line1 = shipTo.Line1,
                        ?line2 = shipTo.Line2,
                        city = shipTo.City,
                        postalCode = shipTo.PostalCode,
                        ?state = shipTo.State,
                        country = shipTo.Country
                    ),
                service = IssuingCards.Create'ShippingService.Standard
            ),
        spendingControls = monthlyLimit monthlySpendingLimit,
        status = IssuingCards.Create'Status.Inactive
    )

/// Orders a physical card
let createPhysicalCard (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (cardholderId: string) (currency: IsoCurrencyCode) (monthlySpendingLimit: int) (personalizationDesignId: string) (recipientName: string) (shipTo: Address) =
    physicalCardOptions cardholderId currency monthlySpendingLimit personalizationDesignId recipientName shipTo
    |> IssuingCards.Create (settings |> withKey idempotencyKey)

// Funding
// =======
// Purchases are paid from your Issuing balance, funded over the region's local rail, never SWIFT:
//   - UK: push it by Faster Payments (FPS) to the sort code Stripe gives the balance
//     (`fundingInstructions`); Bacs works but takes days, CHAPS is for large amounts
//   - euro area: push it by SEPA to the IBAN Stripe gives the balance (`fundingInstructions`)
//   - US: pull it by ACH from your bank account with a top-up (`topUpIssuingBalance`); an ACH push
//     (or a costlier wire) is set up in the Dashboard (Balances > Add to balance)
// The money is the account's, shared by all its cards: keep your own record of what each
// cardholder may spend if they fund it themselves.

/// Where an Issuing program runs: what its cards are in and how its balance is funded
type IssuingRegion =
    | UnitedKingdom
    | EuroArea
    | UnitedStates

/// The countries that use the euro, where an account funds its Issuing balance by SEPA
let euroAreaCountries =
    set [
        IsoCountryCode.AT; IsoCountryCode.BE; IsoCountryCode.HR; IsoCountryCode.CY; IsoCountryCode.EE
        IsoCountryCode.FI; IsoCountryCode.FR; IsoCountryCode.DE; IsoCountryCode.GR; IsoCountryCode.IE
        IsoCountryCode.IT; IsoCountryCode.LV; IsoCountryCode.LT'; IsoCountryCode.LU; IsoCountryCode.MT
        IsoCountryCode.NL; IsoCountryCode.PT; IsoCountryCode.SK; IsoCountryCode.SI; IsoCountryCode.ES
    ]

/// The Issuing region of an account in `country`; None where Stripe does not offer Issuing
/// through one of these regions
let regionOf (country: IsoCountryCode) =
    if country = IsoCountryCode.GB then Some UnitedKingdom
    elif country = IsoCountryCode.US then Some UnitedStates
    elif euroAreaCountries.Contains country then Some EuroArea
    else None

/// The currency of the region's cards and Issuing balance
let currencyOf region =
    match region with
    | UnitedKingdom -> IsoCurrencyCode.GBP
    | EuroArea -> IsoCurrencyCode.EUR
    | UnitedStates -> IsoCurrencyCode.USD

/// The bank transfer type of the region's funding instructions; None in the US, which has none
let bankTransferTypeOf region =
    match region with
    | UnitedKingdom -> Some "gb_bank_transfer"
    | EuroArea -> Some "eu_bank_transfer"
    | UnitedStates -> None

/// What the bank details of the Issuing balance are asked for. Stripe's OpenAPI spec leaves this
/// endpoint out, so FunStripe has no request for it: it is posted with `RestApi.postAsync`.
type FundingInstructionsOptions = {
    [<Config.Form>]
    Currency: IsoCurrencyCode
    [<Config.Form>]
    FundingType: string
    [<Config.Form>]
    BankTransfer: FundingBankTransfer
}

and FundingBankTransfer = {
    /// `gb_bank_transfer` for a GBP balance
    [<Config.Form>]
    Type: string
}

/// A UK account: pay in by Faster Payments or Bacs
type FundingSortCode = {
    AccountHolderName: string
    AccountNumber: string
    SortCode: string
}

/// A SEPA account
type FundingIban = {
    AccountHolderName: string
    Iban: string
    /// Stripe can send null: SEPA transfers need only the IBAN
    Bic: string option
}

/// An account that pays into the Issuing balance
type FundingAddress = {
    SortCode: FundingSortCode option
    Iban: FundingIban option
    /// The payment networks it takes, e.g. bacs and fps
    SupportedNetworks: string list
}

type FundingBankDetails = {
    /// e.g. gb_bank_transfer
    Type: string
    FinancialAddresses: FundingAddress list
}

/// The bank details that fund the Issuing balance. FunStripe's `FundingInstructions` model knows
/// only the transfer types of customer balances (eu_bank_transfer, jp_bank_transfer) and fails on
/// gb_bank_transfer, so the answer is read into these.
type IssuingFundingInstructions = {
    /// Lower-case ISO 4217 code
    Currency: string
    BankTransfer: FundingBankDetails
}

/// The bank details that fund the Issuing balance in `currency`. `bankTransferType` is the
/// region's transfer type, e.g. `gb_bank_transfer` for GBP.
let fundingInstructions (settings: RestApi.StripeApiSettings) (currency: IsoCurrencyCode) (bankTransferType: string) =
    "/v1/issuing/funding_instructions"
    |> RestApi.postAsync<_, IssuingFundingInstructions> settings Map.empty
        { Currency = currency; FundingType = "bank_transfer"; BankTransfer = { Type = bankTransferType } }

/// A top-up of the Issuing balance from your bank account (US). Stripe's OpenAPI spec leaves
/// `destination_balance` out of top-ups, so FunStripe's `Topups.Create` cannot send it: it is
/// posted with `RestApi.postAsync`.
type IssuingTopUpOptions = {
    [<Config.Form>]
    Amount: int
    [<Config.Form>]
    Currency: IsoCurrencyCode
    /// Always `issuing`
    [<Config.Form>]
    DestinationBalance: string
    [<Config.Form>]
    Description: string
}

/// Pulls `amount` (in the smallest unit of `currency`, USD) from your bank account by ACH into the
/// Issuing balance. It is `pending` until the money arrives, up to 5 business days in live mode.
let topUpIssuingBalance (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (amount: int) (currency: IsoCurrencyCode) (description: string) =
    "/v1/topups"
    |> RestApi.postAsync<_, Topup> (settings |> withKey idempotencyKey) Map.empty
        { Amount = amount; Currency = currency; DestinationBalance = "issuing"; Description = description }

/// What the Issuing balance has available in `currency`, in the smallest currency unit
let issuingBalance (settings: RestApi.StripeApiSettings) (currency: IsoCurrencyCode) =
    StripeRequest.Balance.Balance.RetrieveOptions.New()
    |> StripeRequest.Balance.Balance.Retrieve settings
    |> AsyncResult.map (fun balance ->
        balance.Issuing
        |> Option.map (fun issuing -> issuing.Available |> List.filter (fun amount -> amount.Currency = currency) |> List.sumBy _.Amount)
        |> Option.defaultValue 0)

// Card design
// ===========

/// The physical bundles (card stock, carrier letter and envelope) available to your account.
/// A bundle's `Features` say whether a design on it takes a logo and carrier text.
let activeBundles (settings: RestApi.StripeApiSettings) =
    IssuingPhysicalBundles.ListOptions.New(status = "active", limit = 100)
    |> IssuingPhysicalBundles.List settings
    |> AsyncResult.map (fun page -> page.Data)

/// Uploads the logo for a personalization design and returns its file ID. The logo is a black
/// and white PNG that follows Stripe's card design guidelines
/// (docs.stripe.com/issuing/cards/physical/issue-cards).
let uploadCardLogo (settings: RestApi.StripeApiSettings) (fileName: string) (png: byte array) =
    uploadFile settings StripeRequest.Files.Files.Create'Purpose.IssuingLogo fileName "image/png" png
    |> AsyncResult.map _.Id

/// The text of the carrier letter the card is attached to. Which fields a bundle prints
/// depends on the bundle; leave out the ones you do not use.
type CarrierText = {
    HeaderTitle: string option
    HeaderBody: string option
    FooterTitle: string option
    FooterBody: string option
}

/// Request options for a personalization design. `lookupKey` names the design in your
/// code ("standard-employee-card"), so that you can find it again without storing its ID.
let designOptions (physicalBundleId: string) (name: string) (lookupKey: string) (cardLogoFileId: string option) (carrierText: CarrierText option) =
    IssuingPersonalizationDesigns.CreateOptions.New(
        physicalBundle = physicalBundleId,
        name = name,
        lookupKey = lookupKey,
        ?cardLogo = cardLogoFileId,
        ?carrierText =
            (carrierText
             |> Option.map (fun text ->
                 IssuingPersonalizationDesigns.Create'CarrierText.New(
                     ?headerTitle = (text.HeaderTitle |> Option.map Choice1Of2),
                     ?headerBody = (text.HeaderBody |> Option.map Choice1Of2),
                     ?footerTitle = (text.FooterTitle |> Option.map Choice1Of2),
                     ?footerBody = (text.FooterBody |> Option.map Choice1Of2)
                 )))
    )

/// What is wrong with a design for a bundle, before Stripe's review rejects it
let designProblems (bundle: IssuingPhysicalBundle) (cardLogoFileId: string option) (carrierText: CarrierText option) =
    [
        match bundle.Features.CardLogo, cardLogoFileId with
        | IssuingPhysicalBundleFeaturesCardLogo.Required, None -> $"Bundle {bundle.Name} requires a card logo"
        | IssuingPhysicalBundleFeaturesCardLogo.Unsupported, Some _ -> $"Bundle {bundle.Name} does not print a card logo"
        | _ -> ()
        match bundle.Features.CarrierText, carrierText with
        | IssuingPhysicalBundleFeaturesCarrierText.Required, None -> $"Bundle {bundle.Name} requires carrier text"
        | IssuingPhysicalBundleFeaturesCarrierText.Unsupported, Some _ -> $"Bundle {bundle.Name} does not print carrier text"
        | _ -> ()
    ]

/// Creates a personalization design. It starts in `review`.
let createDesign (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (physicalBundleId: string) (name: string) (lookupKey: string) (cardLogoFileId: string option) (carrierText: CarrierText option) =
    designOptions physicalBundleId name lookupKey cardLogoFileId carrierText
    |> IssuingPersonalizationDesigns.Create (settings |> withKey idempotencyKey)

// Test mode
// =========
// Stripe's test helpers stand in for what happens outside Stripe: the design review, the card
// being posted, a merchant asking to charge the card. They only work with a test key.

module TestMode =

    /// Approves a design as Stripe's review would
    let activateDesign (settings: RestApi.StripeApiSettings) (designId: string) =
        TestHelpersIssuingPersonalizationDesignsActivate.ActivateOptions.New(personalizationDesign = designId)
        |> TestHelpersIssuingPersonalizationDesignsActivate.Activate settings

    /// Marks a physical card as posted. A card is `pending` until then, and only a posted card can
    /// be delivered.
    let shipCard (settings: RestApi.StripeApiSettings) (cardId: string) =
        TestHelpersIssuingCardsShippingShip.ShipCardOptions.New(card = cardId)
        |> TestHelpersIssuingCardsShippingShip.ShipCard settings

    /// Marks a posted physical card as delivered
    let deliverCard (settings: RestApi.StripeApiSettings) (cardId: string) =
        TestHelpersIssuingCardsShippingDeliver.DeliverCardOptions.New(card = cardId)
        |> TestHelpersIssuingCardsShippingDeliver.DeliverCard settings

    /// A merchant asks to charge the card, as when the cardholder pays with it. Stripe applies
    /// the spending controls and asks your authorization webhook, if there is one.
    let purchase (settings: RestApi.StripeApiSettings) (cardId: string) (amount: int) (currency: IsoCurrencyCode) (merchantName: string) =
        TestHelpersIssuingAuthorizations.CreateOptions.New(
            card = cardId,
            amount = amount,
            currency = currency,
            merchantData = TestHelpersIssuingAuthorizations.Create'MerchantData.New(name = merchantName)
        )
        |> TestHelpersIssuingAuthorizations.Create settings

    /// Money arriving at the Issuing balance, as a bank transfer would bring it
    type FundBalanceOptions = {
        [<Config.Form>]
        Amount: int
        [<Config.Form>]
        Currency: IsoCurrencyCode
    }

    /// Adds `amount` (in the smallest currency unit) to the Issuing balance. Stripe's OpenAPI spec
    /// leaves this test helper out too. The balance shows it after a few seconds (`waitForBalance`).
    let fundBalance (settings: RestApi.StripeApiSettings) (amount: int) (currency: IsoCurrencyCode) =
        "/v1/test_helpers/issuing/fund_balance"
        |> RestApi.postAsync<_, IssuingFundingInstructions> settings Map.empty { Amount = amount; Currency = currency }

    /// Asks for the Issuing balance every `interval` until at least `atLeast` is available in
    /// `currency`. `Ok (Some available)` once it is, `Ok None` when `timeout` passes first.
    let waitForBalance (settings: RestApi.StripeApiSettings) (currency: IsoCurrencyCode) (atLeast: int) (interval: TimeSpan) (timeout: TimeSpan) =
        async {
            let deadline = DateTime.UtcNow + timeout
            let rec poll () =
                async {
                    match! issuingBalance settings currency with
                    | Error error -> return Error error
                    | Ok available when available >= atLeast -> return Ok (Some available)
                    | Ok _ when DateTime.UtcNow + interval > deadline -> return Ok None
                    | Ok _ ->
                        do! Async.Sleep interval
                        return! poll ()
                }
            return! poll ()
        }

    /// The merchant captures an approved authorization, which creates the transaction
    let capture (settings: RestApi.StripeApiSettings) (authorizationId: string) =
        TestHelpersIssuingAuthorizationsCapture.CaptureOptions.New(authorization = authorizationId)
        |> TestHelpersIssuingAuthorizationsCapture.Capture settings

// Real-time authorization
// =======================
// Stripe sends `issuing_authorization.request` to the endpoint set in the Issuing settings
// of the Dashboard and waits up to 2 seconds for the answer in the response body. (The
// approve and decline API endpoints are deprecated.) Without an answer in time Stripe
// approves or declines as those settings say, so keep the decision fast and local.

/// The answer to an authorization request
type AuthorizationDecision =
    | Approve
    | Decline of reason: string

/// An example policy: approve purchases up to `maxAmount`, in the card's currency. A request's
/// `PendingRequest.Amount` is what Stripe would hold more: the whole amount at first, and only
/// the increase when a merchant raises an authorization already approved (a tip, fuel), so the
/// policy checks the total.
let approveUpTo (maxAmount: int) (authorization: IssuingAuthorization) =
    match authorization.PendingRequest with
    | Some request ->
        let total = (if authorization.Approved then authorization.Amount else 0) + request.Amount
        if total <= maxAmount then Approve else Decline $"{total} is over the limit of {maxAmount}"
    | None -> Decline "No pending request"

/// The HTTP response of the authorization webhook. Send its headers as they are: Stripe reads the
/// answer only as `application/json` and otherwise records a `webhook_error` and applies the
/// Dashboard's fallback decision (beware of helpers that write text as `text/plain`).
type AuthorizationResponse = {
    StatusCode: int
    Headers: (string * string) list
    Body: string
}

/// Handles a delivery to the authorization webhook endpoint: verifies it, decides with
/// `decide` and answers Stripe in the response. When the request cannot be read or `decide`
/// fails, it declines: an error response would leave the decision to the timeout setting of
/// the Dashboard, for every purchase until the problem is fixed.
let handleAuthorizationRequest (decide: IssuingAuthorization -> AuthorizationDecision) (config: WebhookConfig) (signature: string) (payload: string) =
    let respond statusCode body = { StatusCode = statusCode; Headers = []; Body = body }
    let answer (apiVersion: string option) approved =
        {
            StatusCode = 200
            // Stripe reads the answer in the API version of the request: the event's
            Headers = [ "Stripe-Version", apiVersion |> Option.defaultValue Config.DefaultStripeApiVersion; "Content-Type", "application/json" ]
            Body = $"""{{"approved":{(if approved then "true" else "false")}}}"""
        }

    if not (isSecretConfigured config.EndpointSecret) then
        respond 500 ""
    elif String.IsNullOrEmpty signature then
        respond 400 ""
    else
        match WebhookSigning.verifyWithDefaultTolerance config.EndpointSecret payload signature with
        | Error _ -> respond 400 ""
        | Ok () ->
            try
                let stripeEvent = Util.deserialise<Event> payload
                match stripeEvent.Type with
                | EventType.IssuingAuthorizationRequest ->
                    let authorization = stripeEvent.Data.Object |> Util.deserialiseRaw<IssuingAuthorization>
                    match decide authorization with
                    | Approve -> answer stripeEvent.ApiVersion true
                    | Decline reason ->
                        printfn $"Declining {authorization.Id}: {reason}"
                        answer stripeEvent.ApiVersion false
                | _ ->
                    // Only authorization requests are sent to this endpoint
                    respond 200 ""
            with ex ->
                // E.g. a value Stripe added after this FunStripe version. Alert on these.
                printfn $"[FAIL] Declining an authorization request that could not be decided: {ex.Message}"
                answer None false

// Webhooks
// ========
// Subscribe your regular webhook endpoint to these events and give it `processEvent`:
// issuing_card.created, issuing_authorization.created, issuing_authorization.updated,
// issuing_transaction.created, issuing_personalization_design.activated,
// issuing_personalization_design.rejected

/// What an authorization holds from the Issuing balance now, in the smallest currency unit: its
/// holds less releases while pending, nothing once closed, expired or reversed. Not `Amount`,
/// which stays as authorized after a capture that leaves the authorization open, and grows with
/// an increase.
let heldAmount (authorization: IssuingAuthorization) =
    if authorization.Status = IssuingAuthorizationStatus.Pending then
        max 0 -(authorization.BalanceTransactions |> List.sumBy _.Amount)
    else
        0

/// Processes the Issuing events of the regular webhook endpoint
let processEvent (stripeEvent: Event) =
    async {
        match stripeEvent.Type with
        | EventType.IssuingCardCreated ->
            let card = stripeEvent |> eventObject<IssuingCard>
            return Processed $"{card.Type} card ****{card.Last4} created for {card.Cardholder.Name}"

        | EventType.IssuingAuthorizationCreated ->
            let authorization = stripeEvent |> eventObject<IssuingAuthorization>
            // Your webhook did not answer in time, or answered something Stripe could not
            // read: Stripe decided on its own. Alert on these.
            let fallback =
                authorization.RequestHistory
                |> List.exists (fun request ->
                    request.Reason = IssuingAuthorizationRequestReason.WebhookError
                    || request.Reason = IssuingAuthorizationRequestReason.WebhookTimeout)
            let outcome = if authorization.Approved then "approved" else "declined"
            let note = if fallback then " (without your webhook's answer)" else ""
            return Processed $"Authorization {authorization.Id} of {authorization.MerchantData.Name} {outcome}{note}"

        | EventType.IssuingAuthorizationUpdated ->
            // A release, an increase, a capture or the end of the authorization
            let authorization = stripeEvent |> eventObject<IssuingAuthorization>
            return Processed $"Authorization {authorization.Id} is {authorization.Status} and holds {heldAmount authorization}"

        | EventType.IssuingTransactionCreated ->
            // The captured amount, which can differ from the authorized one (tips, fuel):
            // negative for a capture, positive for a refund
            let transaction = stripeEvent |> eventObject<IssuingTransaction>
            return Processed $"Transaction {transaction.Id}: {transaction.Amount} {transaction.Currency}"

        | EventType.IssuingPersonalizationDesignActivated ->
            let design = stripeEvent |> eventObject<IssuingPersonalizationDesign>
            return Processed $"Design {design.Id} approved, physical cards can use it now"

        | EventType.IssuingPersonalizationDesignRejected ->
            // RejectionReasons says what to change in the logo or the carrier text
            let design = stripeEvent |> eventObject<IssuingPersonalizationDesign>
            return Processed $"Design {design.Id} rejected: {design.RejectionReasons}"

        | other ->
            return UnsupportedEvent (eventTypeName other)
    }

/// The regular webhook endpoint for the Issuing events
let handleWebhookRequest (config: WebhookConfig) (signature: string) (payload: string) =
    handleWebhookRequestWith processEvent config signature payload
