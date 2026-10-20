module DisputeHandling

// Disputes (chargebacks): the cardholder asks their bank to reverse a payment. Stripe takes
// the amount and a dispute fee from your balance at once, and you have until the evidence due
// date to show the bank the payment was fine, or to accept the dispute.
//
//   1. `charge.dispute.created`: find your order by the payment intent, stop fulfilment if it
//      has not shipped, flag the customer and tell whoever handles disputes (`processEvent`)
//   2. Respond before `EvidenceDueBy`: `submitEvidence` with what proves the order was
//      delivered (`saveEvidence` keeps a draft), or `acceptDispute` when the customer is right.
//      Stripe sends no reminder you can rely on, so track the due date yourself.
//   3. `charge.dispute.closed`: `won` gives the money back, `lost` keeps it with the customer
//   4. `radar.early_fraud_warning.created`: the card issuer reports the payment as fraud, which
//      usually becomes a dispute. Refunding at once (`refundEarlyFraudWarning`) costs the
//      amount but no dispute fee, and a refunded payment cannot be disputed.
//
// Inquiries (`warning_needs_response`) are a step before a dispute: the bank asks questions and
// no money has moved yet. Answer them like a dispute.
//
// See examples/Disputes.fsx for a run against Stripe test mode.

open System
open FunStripe
open FunStripe.AsyncResultCE
open FunStripe.IsoTypes
open Stripe.Event
open Stripe.PaymentMethod
open Stripe.Radar
open StripeRequest.Disputes
open StripeRequest.Refunds
open StripeService
open WebhookHandler

/// What your order system needs to know about a dispute
type DisputeNotice = {
    DisputeId: string
    ChargeId: string
    /// The payment intent of the disputed payment: store it with your orders to find the order
    PaymentIntentId: string option
    /// In the smallest unit of the currency
    Amount: int
    Currency: IsoCurrencyCode
    Reason: DisputeReason
    /// The deadline for evidence. Missing it loses the dispute.
    EvidenceDueBy: DateTime option
    /// An inquiry: the bank asks questions before deciding to open a dispute
    IsInquiry: bool
}

/// The notice of a dispute
let noticeOf (dispute: Dispute) =
    let (StripeId chargeId) = dispute.Charge
    {
        DisputeId = dispute.Id
        ChargeId = chargeId
        PaymentIntentId = dispute.PaymentIntent |> Option.map (fun (StripeId pid) -> pid)
        Amount = dispute.Amount
        Currency = dispute.Currency
        Reason = dispute.Reason
        EvidenceDueBy = dispute.EvidenceDetails.DueBy
        IsInquiry =
            match dispute.Status with
            | DisputeStatus.WarningNeedsResponse | DisputeStatus.WarningUnderReview | DisputeStatus.WarningClosed -> true
            | _ -> false
    }

/// How a dispute ended
type DisputeOutcome =
    /// The bank sided with you: the amount (and for some networks the fee) comes back
    | Won
    /// The customer keeps the money
    | Lost
    /// The bank closed the inquiry without opening a dispute
    | InquiryClosed
    /// The dispute was prevented (e.g. by Stripe's dispute prevention) before it started
    | Prevented
    /// Not decided yet
    | Open of DisputeStatus

let outcomeOf (dispute: Dispute) =
    match dispute.Status with
    | DisputeStatus.Won -> Won
    | DisputeStatus.Lost -> Lost
    | DisputeStatus.WarningClosed -> InquiryClosed
    | DisputeStatus.Prevented -> Prevented
    | status -> Open status

/// Evidence that a physical order was delivered, the strongest answer to "product not
/// received" and to most fraud disputes. Dates and addresses are free text, as Stripe takes them.
type ShippedOrderEvidence = {
    CustomerName: string
    CustomerEmail: string
    /// What was sold, e.g. "2 x ceramic mug, 350 ml"
    ProductDescription: string
    ShippingAddress: string
    ShippingCarrier: string
    ShippingTrackingNumber: string
    /// e.g. "2026-10-20"
    ShippingDate: string
    /// The file ID of a receipt or invoice PDF, uploaded with `StripeService.uploadFile`
    /// (purpose `dispute_evidence`)
    ReceiptFileId: string option
    /// Anything else the bank should read, e.g. the customer's messages about the delivery
    Explanation: string option
}

/// Request options that attach the evidence to the dispute. With `submit = false` Stripe keeps
/// it as a draft you can still change; with `submit = true` it goes to the bank, once.
let evidenceOptions (disputeId: string) (evidence: ShippedOrderEvidence) (submit: bool) =
    Disputes.UpdateOptions.New(
        dispute = disputeId,
        evidence =
            Disputes.Update'Evidence.New(
                customerName = evidence.CustomerName,
                customerEmailAddress = evidence.CustomerEmail,
                productDescription = evidence.ProductDescription,
                shippingAddress = evidence.ShippingAddress,
                shippingCarrier = evidence.ShippingCarrier,
                shippingTrackingNumber = evidence.ShippingTrackingNumber,
                shippingDate = evidence.ShippingDate,
                ?receipt = evidence.ReceiptFileId,
                ?uncategorizedText = evidence.Explanation
            ),
        submit = submit
    )

/// Saves the evidence as a draft
let saveEvidence (settings: RestApi.StripeApiSettings) (disputeId: string) (evidence: ShippedOrderEvidence) =
    evidenceOptions disputeId evidence false
    |> Disputes.Update settings

/// Sends the evidence to the bank. The dispute becomes `under_review`; the bank decides within
/// weeks to a few months, and Stripe sends `charge.dispute.closed`.
let submitEvidence (settings: RestApi.StripeApiSettings) (disputeId: string) (evidence: ShippedOrderEvidence) =
    evidenceOptions disputeId evidence true
    |> Disputes.Update settings

/// Accepts the dispute: you agree the customer should get the money back, e.g. the order was
/// never shipped. The dispute is lost and cannot be reopened.
let acceptDispute (settings: RestApi.StripeApiSettings) (disputeId: string) =
    DisputesClose.CloseOptions.New(dispute = disputeId)
    |> DisputesClose.Close settings

/// The disputes of a payment, e.g. to show on its order page
let disputesOfPayment (settings: RestApi.StripeApiSettings) (paymentIntentId: string) =
    Disputes.ListOptions.New(paymentIntent = paymentIntentId)
    |> Disputes.List settings
    |> AsyncResult.map (fun page -> page.Data)

// Early fraud warnings
// ====================

/// Request options that refund the charge of an early fraud warning in full
let earlyFraudWarningRefundOptions (warning: RadarEarlyFraudWarning) =
    let (StripeId chargeId) = warning.Charge
    Refunds.CreateOptions.New(charge = chargeId, reason = Refunds.Create'Reason.Fraudulent)

/// Refunds the charge of an actionable early fraud warning; None when the warning is not
/// actionable (the charge is already refunded or disputed). Whether to refund is a business
/// decision: it is cheaper than a dispute for most orders, but an expensive order that has
/// already shipped may be worth defending instead.
let refundEarlyFraudWarning (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (warning: RadarEarlyFraudWarning) =
    async {
        if warning.Actionable then
            let! refund = earlyFraudWarningRefundOptions warning |> Refunds.Create (settings |> withKey idempotencyKey)
            return refund |> Result.map Some
        else
            return Ok None
    }

// Webhooks
// ========
// Subscribe a webhook endpoint to these events and give it `processEvent`:
// charge.dispute.created, charge.dispute.updated, charge.dispute.closed,
// charge.dispute.funds_withdrawn, charge.dispute.funds_reinstated, radar.early_fraud_warning.created

let private describeAmount (notice: DisputeNotice) = $"{notice.Amount} {notice.Currency}"

/// Processes the dispute events
let processEvent (stripeEvent: Event) =
    async {
        let dispute () = stripeEvent.Data.Object |> Util.deserialiseRaw<Dispute>

        match stripeEvent.Type with
        | EventType.ChargeDisputeCreated ->
            let notice = dispute () |> noticeOf
            // In your system: mark the order of notice.PaymentIntentId as disputed, hold its
            // shipment, flag the customer (block further orders, report to your fraud tools)
            // and create a task for the evidence with the due date
            let due = notice.EvidenceDueBy |> Option.map (fun due -> $"{due:u}") |> Option.defaultValue "unknown"
            let kind = if notice.IsInquiry then "Inquiry" else "Dispute"
            return Processed $"{kind} {notice.DisputeId} on {describeAmount notice} ({notice.Reason}), evidence due {due}"

        | EventType.ChargeDisputeUpdated ->
            // E.g. your evidence was submitted; nothing to do but keep the status
            let dispute = dispute ()
            return Processed $"Dispute {dispute.Id} is {dispute.Status}"

        | EventType.ChargeDisputeClosed ->
            let dispute = dispute ()
            match outcomeOf dispute with
            | Won -> return Processed $"Dispute {dispute.Id} won: the payment stands"
            | Lost -> return Processed $"Dispute {dispute.Id} lost: treat the order as refunded"
            | InquiryClosed -> return Processed $"Inquiry {dispute.Id} closed without a dispute"
            | Prevented -> return Processed $"Dispute {dispute.Id} was prevented"
            | Open status -> return Processed $"Dispute {dispute.Id} closed while {status}"

        | EventType.ChargeDisputeFundsWithdrawn
        | EventType.ChargeDisputeFundsReinstated ->
            // For your ledger: the balance transactions on the dispute say what moved
            let dispute = dispute ()
            let direction = if stripeEvent.Type = EventType.ChargeDisputeFundsWithdrawn then "withdrawn" else "reinstated"
            return Processed $"Funds of dispute {dispute.Id} {direction}"

        | EventType.RadarEarlyFraudWarningCreated ->
            let warning = stripeEvent.Data.Object |> Util.deserialiseRaw<RadarEarlyFraudWarning>
            let (StripeId chargeId) = warning.Charge
            if warning.Actionable then
                // Decide here whether to refund (refundEarlyFraudWarning) and cancel the order
                return Processed $"Early fraud warning on {chargeId} ({warning.FraudType}): refund it to avoid a dispute"
            else
                return Processed $"Early fraud warning on {chargeId} is not actionable"

        | other ->
            return UnsupportedEvent (eventTypeName other)
    }

/// The webhook endpoint for the dispute events
let handleWebhookRequest (config: WebhookConfig) (signature: string) (payload: string) =
    handleWebhookRequestWith processEvent config signature payload
