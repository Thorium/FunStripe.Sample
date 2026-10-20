module MembershipsTests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FunStripe
open FunStripe.IsoTypes
open Stripe.PaymentMethod
open StripeService
open Memberships
open FakeStripeServer
open WebhookHandler
open WebhookSamples

let formOf options = options |> Util.serialiseForm |> Map.ofSeq

let plan = { Currency = IsoCurrencyCode.EUR; MonthlyFee = 4990; JoiningFee = 2900 }
let catalog = { MonthlyPriceId = "price_1Monthly"; JoiningFeePriceId = "price_1Joining" }

/// A Stripe price object (the one in the line items fixture)
let priceJson (id: string) =
    use document = JsonDocument.Parse(fixture "checkout-line-items")
    let price = document.RootElement.GetProperty("data").[0].GetProperty("price").GetRawText()
    price.Replace("\"price_1Mug\"", $"\"{id}\"")

// =============================================================================
// Catalog
// =============================================================================

[<Fact>]
let ``the monthly price recurs every month under its lookup key`` () =
    let form = monthlyPriceOptions plan |> formOf
    Assert.Equal("4990", form.["unit_amount"])
    Assert.Equal("eur", form.["currency"])
    Assert.Equal("month", form.["recurring[interval]"])
    Assert.Equal("gym-membership-monthly", form.["lookup_key"])
    Assert.Equal("true", form.["transfer_lookup_key"])
    Assert.Equal("Gym membership", form.["product_data[name]"])

[<Fact>]
let ``the joining fee is a one-time price`` () =
    let form = joiningFeePriceOptions plan |> formOf
    Assert.Equal("2900", form.["unit_amount"])
    Assert.False(form.ContainsKey "recurring[interval]")
    Assert.Equal("gym-membership-joining-fee", form.["lookup_key"])

[<Fact>]
let ``ensureCatalog finds existing prices and creates the missing ones`` () =
    async {
        let respond (request: RecordedRequest) =
            match request.Method, request.Path with
            | "GET", "/v1/prices" when request.Query.["lookup_keys[]"] = monthlyLookupKey ->
                200, $$"""{ "object": "list", "url": "/v1/prices", "has_more": false, "data": [ {{priceJson "price_1Existing"}} ] }"""
            | "GET", "/v1/prices" -> 200, """{ "object": "list", "url": "/v1/prices", "has_more": false, "data": [] }"""
            | "POST", "/v1/prices" -> 200, priceJson "price_1Created"
            | _ -> 404, errorJson
        use stripe = new FakeStripe(respond)
        let! result = ensureCatalog stripe.Settings (OperationId.tryCreate "catalog" |> Option.get) plan
        match result with
        | Ok catalog ->
            Assert.Equal("price_1Existing", catalog.MonthlyPriceId)
            Assert.Equal("price_1Created", catalog.JoiningFeePriceId)
        | Error error -> Assert.Fail(describeStripeError error)
        let created = stripe.Requests |> List.filter (fun request -> request.Method = "POST")
        Assert.Equal("gym-membership-joining-fee", (Assert.Single created).Form.["lookup_key"])
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// Sign-up
// =============================================================================

[<Fact>]
let ``the sign-up page subscribes the member with the joining fee, by direct debit or card`` () =
    let form = signupSessionOptions catalog "member-7" "jenny@example.com" "https://gym.example.com/welcome" "https://gym.example.com/join" |> formOf
    Assert.Equal("subscription", form.["mode"])
    Assert.Equal("price_1Monthly", form.["line_items[0][price]"])
    Assert.Equal("price_1Joining", form.["line_items[1][price]"])
    Assert.Equal("sepa_debit", form.["allowed_payment_method_types[0]"])
    Assert.Equal("card", form.["allowed_payment_method_types[1]"])
    Assert.Equal("member-7", form.["subscription_data[metadata][member_id]"])
    Assert.Equal("member-7", form.["client_reference_id"])
    Assert.Equal("flexible", form.["subscription_data[billing_mode][type]"])

[<Fact>]
let ``startMembership bills the saved payment method off-session`` () =
    let form = membershipOptions catalog "member-7" "cus_FakeCustomer" "pm_1TestSepa" |> formOf
    Assert.Equal("cus_FakeCustomer", form.["customer"])
    Assert.Equal("price_1Monthly", form.["items[0][price]"])
    Assert.Equal("price_1Joining", form.["add_invoice_items[0][price]"])
    Assert.Equal("pm_1TestSepa", form.["default_payment_method"])
    Assert.Equal("true", form.["off_session"])
    Assert.Equal("member-7", form.["metadata[member_id]"])
    Assert.Equal("flexible", form.["billing_mode[type]"])

// =============================================================================
// Changes
// =============================================================================

let respondWithSubscription =
    let subscription = fixture "subscription-active"
    respondWithRoutes [
        "/v1/subscriptions/sub_1Gym", subscription
        "/v1/subscriptions/sub_1Gym/pause", subscription
        "/v1/subscriptions/sub_1Gym/resume", subscription
    ]

[<Fact>]
let ``a freeze pauses the subscription and the unfreeze starts a new period today`` () =
    async {
        use stripe = new FakeStripe(respondWithSubscription)
        let! frozen = freezeMembership stripe.Settings "sub_1Gym"
        let! unfrozen = unfreezeMembership stripe.Settings "sub_1Gym"
        Assert.True(Result.isOk frozen)
        Assert.True(Result.isOk unfrozen)
        match stripe.Requests with
        | [ pause; resume ] ->
            Assert.Equal("/v1/subscriptions/sub_1Gym/pause", pause.Path)
            Assert.Equal("subscription", pause.Form.["type"])
            Assert.Equal("/v1/subscriptions/sub_1Gym/resume", resume.Path)
            Assert.Equal("now", resume.Form.["billing_cycle_anchor[type]"])
            Assert.Equal("resume_on_payment_success", resume.Form.["payment_behavior"])
            Assert.Equal("none", resume.Form.["proration_behavior"])
        | requests -> Assert.Fail($"Expected 2 requests, got {requests.Length}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``cancelAtPeriodEnd keeps the paid month`` () =
    async {
        use stripe = new FakeStripe(respondWithSubscription)
        let! _ = cancelAtPeriodEnd stripe.Settings "sub_1Gym" true
        Assert.Equal("true", (Assert.Single stripe.Requests).Form.["cancel_at_period_end"])
    } |> Async.StartImmediateAsTask :> Task

let subscriptionWith (status: SubscriptionStatus) =
    { (fixture "subscription-active" |> Util.deserialise<Subscription>) with Status = status }

[<Fact>]
let ``accessOf lets paying members in and gives a grace period while a debit is retried`` () =
    Assert.Equal(Full, accessOf true (subscriptionWith SubscriptionStatus.Active))
    Assert.Equal(Full, accessOf false (subscriptionWith SubscriptionStatus.Trialing))
    Assert.Equal(Grace, accessOf true (subscriptionWith SubscriptionStatus.PastDue))
    Assert.Equal(NoAccess, accessOf true (subscriptionWith SubscriptionStatus.Paused))
    Assert.Equal(NoAccess, accessOf true (subscriptionWith SubscriptionStatus.Unpaid))
    Assert.Equal(NoAccess, accessOf true (subscriptionWith SubscriptionStatus.Canceled))

[<Fact>]
let ``a new member is let in provisionally, but not through the retries of a debit that never cleared`` () =
    Assert.Equal(Provisional, accessOf false (subscriptionWith SubscriptionStatus.Active))
    Assert.Equal(NoAccess, accessOf false (subscriptionWith SubscriptionStatus.PastDue))

[<Fact>]
let ``a freeze that is ending gives grace until its debit succeeds`` () =
    let resuming =
        (fixture "subscription-active")
            .Replace("\"status\": \"active\"", "\"status\": \"paused\"")
            .Replace("\"pending_update\": null", "\"pending_update\": { \"billing_cycle_anchor\": null, \"cancel_at_period_end\": null, \"discount\": null, \"discounts\": null, \"expires_at\": 1790086400, \"metadata\": null, \"subscription_items\": null, \"trial_end\": null, \"trial_from_plan\": null }")
        |> Util.deserialise<Subscription>
    Assert.True(resuming.PendingUpdate.IsSome)
    Assert.Equal(Grace, accessOf false resuming)

[<Fact>]
let ``membershipOfInvoice finds the subscription of an invoice`` () =
    let invoice = fixture "invoice-payment-failed" |> Util.deserialise<Invoice>
    Assert.Equal(Some "sub_1Gym", membershipOfInvoice invoice)

// =============================================================================
// Webhooks
// =============================================================================

let secret = "whsec_test_gym"

let deliver (eventType: string) (objectJson: string) =
    let payload = eventPayload "evt_test_gym" eventType objectJson
    let signature = signatureHeader secret payload (DateTimeOffset.UtcNow.ToUnixTimeSeconds())
    Memberships.handleWebhookRequest { EndpointSecret = secret } signature payload

let assertProcessed (expected: string) (result: WebhookResult, statusCode: int) =
    Assert.Equal(200, statusCode)
    match result with
    | Processed message -> Assert.Contains(expected, message)
    | other -> Assert.Fail($"Expected Processed, got {other}")

[<Fact>]
let ``invoice.payment_failed reports the retry`` () =
    async {
        let! response = deliver "invoice.payment_failed" (fixture "invoice-payment-failed")
        assertProcessed "Debit 1 for membership sub_1Gym failed, Stripe retries" response
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``customer.subscription.updated gives the member's access`` () =
    async {
        let! response = deliver "customer.subscription.updated" (fixture "subscription-active")
        assertProcessed "Member member-7: Active" response
        let pastDue = (fixture "subscription-active").Replace("\"status\": \"active\"", "\"status\": \"past_due\"")
        let! response = deliver "customer.subscription.updated" pastDue
        assertProcessed "Member member-7: PastDue" response
    } |> Async.StartImmediateAsTask :> Task
