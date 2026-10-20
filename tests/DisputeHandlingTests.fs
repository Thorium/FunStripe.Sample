module DisputeHandlingTests

open System
open System.Threading.Tasks
open Xunit
open FunStripe
open FunStripe.IsoTypes
open Stripe.PaymentMethod
open Stripe.Radar
open StripeService
open DisputeHandling
open FakeStripeServer
open WebhookHandler
open WebhookSamples

let formOf options = options |> Util.serialiseForm |> Map.ofSeq

let dispute () = fixture "dispute-needs-response" |> Util.deserialise<Dispute>

let evidence = {
    CustomerName = "Jenny Rosen"
    CustomerEmail = "jenny@example.com"
    ProductDescription = "2 x ceramic mug, 350 ml"
    ShippingAddress = "Mannerheimintie 1, 00100 Helsinki, FI"
    ShippingCarrier = "Posti"
    ShippingTrackingNumber = "JJFI12345678900000000"
    ShippingDate = "2026-10-02"
    ReceiptFileId = Some "file_1Receipt"
    Explanation = None
}

// =============================================================================
// Notices and outcomes
// =============================================================================

[<Fact>]
let ``noticeOf gives the order system the payment, amount and evidence deadline`` () =
    let notice = noticeOf (dispute ())
    Assert.Equal("dp_1Test", notice.DisputeId)
    Assert.Equal("ch_1Test", notice.ChargeId)
    Assert.Equal(Some "pi_1Test", notice.PaymentIntentId)
    Assert.Equal(2500, notice.Amount)
    Assert.Equal(IsoCurrencyCode.EUR, notice.Currency)
    Assert.Equal(DisputeReason.Fraudulent, notice.Reason)
    Assert.Equal(Some (DateTimeOffset.FromUnixTimeSeconds(1790600000L).UtcDateTime), notice.EvidenceDueBy)
    Assert.False(notice.IsInquiry)

[<Fact>]
let ``an inquiry is told apart from a dispute`` () =
    Assert.True((noticeOf { dispute () with Status = DisputeStatus.WarningNeedsResponse }).IsInquiry)

[<Fact>]
let ``outcomeOf maps the final statuses`` () =
    let withStatus status = outcomeOf { dispute () with Status = status }
    Assert.Equal(Won, withStatus DisputeStatus.Won)
    Assert.Equal(Lost, withStatus DisputeStatus.Lost)
    Assert.Equal(InquiryClosed, withStatus DisputeStatus.WarningClosed)
    Assert.Equal(Open DisputeStatus.UnderReview, withStatus DisputeStatus.UnderReview)

// =============================================================================
// Evidence and responses
// =============================================================================

[<Fact>]
let ``evidenceOptions sends the delivery evidence, as a draft or for submission`` () =
    let draft = evidenceOptions "dp_1Test" evidence false |> formOf
    Assert.Equal("Posti", draft.["evidence[shipping_carrier]"])
    Assert.Equal("JJFI12345678900000000", draft.["evidence[shipping_tracking_number]"])
    Assert.Equal("2026-10-02", draft.["evidence[shipping_date]"])
    Assert.Equal("2 x ceramic mug, 350 ml", draft.["evidence[product_description]"])
    Assert.Equal("file_1Receipt", draft.["evidence[receipt]"])
    Assert.Equal("false", draft.["submit"])
    Assert.False(draft.ContainsKey "evidence[uncategorized_text]")
    Assert.Equal("true", (evidenceOptions "dp_1Test" evidence true |> formOf).["submit"])

[<Fact>]
let ``submitEvidence and acceptDispute post to the dispute`` () =
    async {
        use stripe =
            new FakeStripe(respondWithRoutes [
                "/v1/disputes/dp_1Test", fixture "dispute-needs-response"
                "/v1/disputes/dp_1Test/close", fixture "dispute-needs-response"
            ])
        let! submitted = submitEvidence stripe.Settings "dp_1Test" evidence
        let! accepted = acceptDispute stripe.Settings "dp_1Test"
        Assert.True(Result.isOk submitted)
        Assert.True(Result.isOk accepted)
        match stripe.Requests with
        | [ submit; close ] ->
            Assert.Equal("/v1/disputes/dp_1Test", submit.Path)
            Assert.Equal("true", submit.Form.["submit"])
            Assert.Equal("/v1/disputes/dp_1Test/close", close.Path)
        | requests -> Assert.Fail($"Expected 2 requests, got {requests.Length}")
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// Early fraud warnings
// =============================================================================

let key (step: string) =
    idempotencyKeyFor (OperationId.tryCreate "op-efw" |> Option.get) step

let warning () = fixture "radar-early-fraud-warning" |> Util.deserialise<RadarEarlyFraudWarning>

[<Fact>]
let ``an actionable early fraud warning refunds the charge as fraudulent`` () =
    async {
        let refundJson = """{ "id": "re_1Test", "object": "refund", "amount": 2500, "charge": "ch_1Test", "created": 1790000000, "currency": "eur", "metadata": {}, "payment_intent": "pi_1Test", "reason": "fraudulent", "status": "succeeded" }"""
        use stripe = new FakeStripe(respondWithRoutes [ "/v1/refunds", refundJson ])
        let! result = refundEarlyFraudWarning stripe.Settings (key "refund") (warning ())
        match result with
        | Ok (Some refund) -> Assert.Equal("re_1Test", refund.Id)
        | other -> Assert.Fail($"Expected a refund, got {other}")
        let request = Assert.Single stripe.Requests
        Assert.Equal("ch_1Test", request.Form.["charge"])
        Assert.Equal("fraudulent", request.Form.["reason"])
        Assert.Equal(Some "op-efw:refund", request.IdempotencyKey)
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``a warning that is not actionable is left alone`` () =
    async {
        use stripe = new FakeStripe(respondError)
        let! result = refundEarlyFraudWarning stripe.Settings (key "refund") { warning () with Actionable = false }
        Assert.Equal(Ok None, result)
        Assert.Empty(stripe.Requests)
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// Webhooks
// =============================================================================

let secret = "whsec_test_disputes"

let deliver (eventType: string) (objectJson: string) =
    let payload = eventPayload "evt_test_dispute" eventType objectJson
    let signature = signatureHeader secret payload (DateTimeOffset.UtcNow.ToUnixTimeSeconds())
    DisputeHandling.handleWebhookRequest { EndpointSecret = secret } signature payload

let assertProcessed (expected: string) (result: WebhookResult, statusCode: int) =
    Assert.Equal(200, statusCode)
    match result with
    | Processed message -> Assert.Contains(expected, message)
    | other -> Assert.Fail($"Expected Processed, got {other}")

[<Fact>]
let ``charge.dispute.created reports the amount and the evidence deadline`` () =
    async {
        let! response = deliver "charge.dispute.created" (fixture "dispute-needs-response")
        assertProcessed "Dispute dp_1Test on 2500 EUR (Fraudulent), evidence due 2026-09-28" response
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``charge.dispute.closed reports the outcome`` () =
    async {
        let lost = (fixture "dispute-needs-response").Replace("\"needs_response\"", "\"lost\"")
        let! response = deliver "charge.dispute.closed" lost
        assertProcessed "lost: treat the order as refunded" response
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``radar.early_fraud_warning.created suggests the refund`` () =
    async {
        let! response = deliver "radar.early_fraud_warning.created" (fixture "radar-early-fraud-warning")
        assertProcessed "refund it to avoid a dispute" response
    } |> Async.StartImmediateAsTask :> Task
