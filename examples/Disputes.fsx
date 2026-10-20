// Disputes: a card payment the cardholder disputes, and your evidence that wins (or loses) it.
//
//   dotnet fsi examples/Disputes.fsx           the evidence wins the dispute
//   dotnet fsi examples/Disputes.fsx -- lose   the evidence loses it
//
// The code is in src/DisputeHandling.fs. Needs STRIPE_TEST_API_KEY (see Common.fsx). In test
// mode the payment method pm_card_createDispute is disputed right after the payment, and the
// evidence text "winning_evidence" or "losing_evidence" decides the outcome.

#load "Common.fsx"
#load "../src/DisputeHandling.fs"

open System.Threading
open FunStripe
open FunStripe.IsoTypes
open StripeRequest.Payment
open Common
open StripeService
open DisputeHandling

let operation = newOperation "disputes"
let outcomeText = if scriptArgs = [ "lose" ] then "losing_evidence" else "winning_evidence"

// 1. A paid order: 2 mugs for 25.00 EUR
let payment =
    PaymentIntents.CreateOptions.New(
        amount = formatAmount 25m,
        currency = IsoCurrencyCode.EUR,
        paymentMethod = "pm_card_createDispute",
        allowedPaymentMethodTypes = [ PaymentIntents.Create'AllowedPaymentMethodTypes.Card ],
        confirm = true
    )
    |> PaymentIntents.Create (settings |> withKey (idempotencyKeyFor operation "payment"))
    |> step "Paying 25.00 EUR with a card that will be disputed"
printfn $"  Payment intent {payment.Id}: {payment.Status}"

// 2. The cardholder disputes it. Stripe sends charge.dispute.created; a script can just look.
[<TailCall>]
let rec waitForDispute attempts =
    let disputes = disputesOfPayment settings payment.Id |> step "Looking for the dispute"
    match disputes with
    | dispute :: _ -> dispute
    | [] when attempts > 0 ->
        Thread.Sleep 2000
        waitForDispute (attempts - 1)
    | [] ->
        eprintfn "No dispute after 20 seconds"
        exit 1

let dispute = waitForDispute 10
let notice = noticeOf dispute
printfn $"  Dispute {notice.DisputeId}: {notice.Amount} {notice.Currency}, reason {notice.Reason}, evidence due {notice.EvidenceDueBy}"

// 3. Your evidence that the mugs were delivered
let evidence = {
    CustomerName = "Jenny Rosen"
    CustomerEmail = "jenny.rosen+disputes@example.com"
    ProductDescription = "2 x ceramic mug, 350 ml"
    ShippingAddress = "Mannerheimintie 1, 00100 Helsinki, Finland"
    ShippingCarrier = "Posti"
    ShippingTrackingNumber = "JJFI12345678900000000"
    ShippingDate = "2026-10-02"
    ReceiptFileId = None
    // In test mode this text decides the outcome
    Explanation = Some outcomeText
}

saveEvidence settings dispute.Id evidence |> step "Saving the evidence as a draft" |> ignore
let submitted = submitEvidence settings dispute.Id evidence |> step "Submitting the evidence"
printfn $"  Dispute {submitted.Id}: {submitted.Status}"

// 4. The bank decides (in test mode within seconds) and Stripe sends charge.dispute.closed
Thread.Sleep 5000
match disputesOfPayment settings payment.Id |> step "Reading the outcome" with
| decided :: _ -> printfn $"  Outcome: {outcomeOf decided}"
| [] -> printfn "  The dispute is gone"
