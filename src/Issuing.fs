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
// purchases. See examples/Issuing.fsx for a run against Stripe test mode.

open System
open System.Net.Http
open System.Net.Http.Headers
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

/// Request options for an individual cardholder
let cardholderOptions (firstName: string) (lastName: string) (email: string) (billingAddress: Address) (terms: TermsAcceptance) =
    IssuingCardholders.CreateOptions.New(
        type' = IssuingCardholders.Create'Type.Individual,
        name = $"{firstName} {lastName}",
        email = email,
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
let createCardholder (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (firstName: string) (lastName: string) (email: string) (billingAddress: Address) (terms: TermsAcceptance) =
    cardholderOptions firstName lastName email billingAddress terms
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
let uploadCardLogo (http: HttpClient) (settings: RestApi.StripeApiSettings) (filesBaseUrl: string) (fileName: string) (png: byte array) =
    uploadFile http settings filesBaseUrl "issuing_logo" fileName "image/png" png

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

    /// Marks a physical card as delivered
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

/// An example policy: approve purchases up to `maxAmount`, in the card's currency
let approveUpTo (maxAmount: int) (authorization: IssuingAuthorization) =
    match authorization.PendingRequest with
    | Some request when request.Amount <= maxAmount -> Approve
    | Some request -> Decline $"{request.Amount} is over the limit of {maxAmount}"
    | None -> Decline "No pending request"

/// The HTTP response of the authorization webhook
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
    let answer approved =
        {
            StatusCode = 200
            // Stripe requires the API version of the answer
            Headers = [ "Stripe-Version", Config.DefaultStripeApiVersion; "Content-Type", "application/json" ]
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
                    | Approve -> answer true
                    | Decline reason ->
                        printfn $"Declining {authorization.Id}: {reason}"
                        answer false
                | _ ->
                    // Only authorization requests are sent to this endpoint
                    respond 200 ""
            with ex ->
                // E.g. a value Stripe added after this FunStripe version. Alert on these.
                printfn $"[FAIL] Declining an authorization request that could not be decided: {ex.Message}"
                answer false

// Webhooks
// ========
// Subscribe your regular webhook endpoint to these events and give it `processEvent`:
// issuing_card.created, issuing_authorization.created, issuing_transaction.created,
// issuing_personalization_design.activated, issuing_personalization_design.rejected

/// Processes the Issuing events of the regular webhook endpoint
let processEvent (stripeEvent: Event) =
    async {
        match stripeEvent.Type with
        | EventType.IssuingCardCreated ->
            let card = stripeEvent.Data.Object |> Util.deserialiseRaw<IssuingCard>
            return Processed $"{card.Type} card ****{card.Last4} created for {card.Cardholder.Name}"

        | EventType.IssuingAuthorizationCreated ->
            let authorization = stripeEvent.Data.Object |> Util.deserialiseRaw<IssuingAuthorization>
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

        | EventType.IssuingTransactionCreated ->
            // The captured amount, which can differ from the authorized one (tips, fuel)
            let transaction = stripeEvent.Data.Object |> Util.deserialiseRaw<IssuingTransaction>
            return Processed $"Transaction {transaction.Id}: {transaction.Amount} {transaction.Currency}"

        | EventType.IssuingPersonalizationDesignActivated ->
            let design = stripeEvent.Data.Object |> Util.deserialiseRaw<IssuingPersonalizationDesign>
            return Processed $"Design {design.Id} approved, physical cards can use it now"

        | EventType.IssuingPersonalizationDesignRejected ->
            // RejectionReasons says what to change in the logo or the carrier text
            let design = stripeEvent.Data.Object |> Util.deserialiseRaw<IssuingPersonalizationDesign>
            return Processed $"Design {design.Id} rejected: {design.RejectionReasons}"

        | other ->
            return UnsupportedEvent (eventTypeName other)
    }

/// The regular webhook endpoint for the Issuing events
let handleWebhookRequest (config: WebhookConfig) (signature: string) (payload: string) =
    handleWebhookRequestWith processEvent config signature payload
