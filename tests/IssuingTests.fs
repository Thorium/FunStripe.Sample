module IssuingTests

open System
open System.Net.Http
open System.Threading.Tasks
open Xunit
open FunStripe
open FunStripe.IsoTypes
open Stripe.Issuing
open Stripe.PaymentMethod
open StripeService
open Issuing
open FakeStripeServer
open WebhookHandler
open WebhookSamples

let formOf options = options |> Util.serialiseForm |> Map.ofSeq

let helsinki = {
    Line1 = "Mannerheimintie 1"
    Line2 = None
    City = "Helsinki"
    PostalCode = "00100"
    State = None
    Country = IsoCountryCode.FI
}

let terms = { AcceptedAt = DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc); IpAddress = "203.0.113.7"; UserAgent = "Mozilla/5.0 (test)" }

// =============================================================================
// Cardholders and cards
// =============================================================================

[<Fact>]
let ``cardholderOptions creates an active individual with an address and accepted terms`` () =
    let form = cardholderOptions "Jenny" "Rosen" "jenny@example.com" helsinki terms |> formOf
    Assert.Equal("individual", form.["type"])
    Assert.Equal("Jenny Rosen", form.["name"])
    Assert.Equal("Jenny", form.["individual[first_name]"])
    Assert.Equal("Rosen", form.["individual[last_name]"])
    Assert.Equal("FI", form.["billing[address][country]"])
    Assert.Equal("00100", form.["billing[address][postal_code]"])
    Assert.Equal("203.0.113.7", form.["individual[card_issuing][user_terms_acceptance][ip]"])
    Assert.True(form.ContainsKey "individual[card_issuing][user_terms_acceptance][date]")
    Assert.False(form.ContainsKey "billing[address][state]")
    Assert.Equal("active", form.["status"])

[<Fact>]
let ``virtualCardOptions creates an active card with a monthly limit`` () =
    let form = virtualCardOptions "ich_1Test" IsoCurrencyCode.EUR 50000 |> formOf
    Assert.Equal("virtual", form.["type"])
    Assert.Equal("eur", form.["currency"])
    Assert.Equal("ich_1Test", form.["cardholder"])
    Assert.Equal("50000", form.["spending_controls[spending_limits][0][amount]"])
    Assert.Equal("monthly", form.["spending_controls[spending_limits][0][interval]"])
    Assert.Equal("active", form.["status"])

[<Fact>]
let ``physicalCardOptions prints the design and posts the card inactive`` () =
    let form = physicalCardOptions "ich_1Test" IsoCurrencyCode.EUR 50000 "ipcd_1Test" "Jenny Rosen" helsinki |> formOf
    Assert.Equal("physical", form.["type"])
    Assert.Equal("ipcd_1Test", form.["personalization_design"])
    Assert.Equal("Jenny Rosen", form.["shipping[name]"])
    Assert.Equal("Helsinki", form.["shipping[address][city]"])
    Assert.Equal("standard", form.["shipping[service]"])
    Assert.Equal("inactive", form.["status"])

// =============================================================================
// Card design
// =============================================================================

let carrierText = { HeaderTitle = Some "Welcome"; HeaderBody = Some "Your new company card"; FooterTitle = None; FooterBody = None }

[<Fact>]
let ``designOptions sends the bundle, logo, lookup key and carrier text`` () =
    let form = designOptions "ics_1Test" "Employee card" "employee-card" (Some "file_1Logo") (Some carrierText) |> formOf
    Assert.Equal("ics_1Test", form.["physical_bundle"])
    Assert.Equal("Employee card", form.["name"])
    Assert.Equal("employee-card", form.["lookup_key"])
    Assert.Equal("file_1Logo", form.["card_logo"])
    Assert.Equal("Welcome", form.["carrier_text[header_title]"])
    Assert.Equal("Your new company card", form.["carrier_text[header_body]"])
    Assert.False(form.ContainsKey "carrier_text[footer_title]")

[<Fact>]
let ``designProblems checks the design against the bundle's features`` () =
    // The fixture bundle requires a logo and takes carrier text optionally
    let bundle = fixture "issuing-physical-bundle" |> Util.deserialise<IssuingPhysicalBundle>
    Assert.Empty(designProblems bundle (Some "file_1Logo") (Some carrierText))
    Assert.Empty(designProblems bundle (Some "file_1Logo") None)
    Assert.Equal<string list>([ "Bundle Standard white requires a card logo" ], designProblems bundle None None)

[<Fact>]
let ``uploadCardLogo sends a multipart upload to the files host and returns the file ID`` () =
    async {
        // A File with the issuing_logo purpose, which FunStripe's File model cannot read
        let fileJson = """{ "id": "file_1Logo", "object": "file", "purpose": "issuing_logo", "created": 1790000000, "size": 4 }"""
        use stripe = new FakeStripe(respondWithRoutes [ "/v1/files", fileJson ])
        use http = new HttpClient()
        let! result = uploadCardLogo http stripe.Settings stripe.BaseUrl "logo.png" [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy |]
        Assert.Equal(Ok "file_1Logo", result)
        let request = Assert.Single stripe.Requests
        Assert.Equal("POST", request.Method)
        Assert.Equal(Some "Bearer sk_test_fake", request.Authorization)
        Assert.StartsWith("multipart/form-data", request.ContentType |> Option.defaultValue "")
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// Real-time authorization
// =============================================================================

let secret = "whsec_test_issuing"

let signed (eventType: string) (objectJson: string) =
    let payload = eventPayload "evt_test_issuing" eventType objectJson
    signatureHeader secret payload (DateTimeOffset.UtcNow.ToUnixTimeSeconds()), payload

/// The fixture asks to authorize 25.00 EUR
let authorizationRequest = signed "issuing_authorization.request" (fixture "issuing-authorization")

[<Fact>]
let ``approveUpTo approves the pending amount up to the limit`` () =
    let authorization = fixture "issuing-authorization" |> Util.deserialise<IssuingAuthorization>
    Assert.Equal(Approve, approveUpTo 2500 authorization)
    Assert.Equal(Decline "2500 is over the limit of 2499", approveUpTo 2499 authorization)
    Assert.Equal(Decline "No pending request", approveUpTo 2500 { authorization with PendingRequest = None })

[<Fact>]
let ``the authorization webhook answers with the decision and the API version`` () =
    let signature, payload = authorizationRequest
    let approved = handleAuthorizationRequest (approveUpTo 5000) { EndpointSecret = secret } signature payload
    Assert.Equal(200, approved.StatusCode)
    Assert.Equal("""{"approved":true}""", approved.Body)
    Assert.Contains(("Stripe-Version", Config.DefaultStripeApiVersion), approved.Headers)

    let declined = handleAuthorizationRequest (approveUpTo 1000) { EndpointSecret = secret } signature payload
    Assert.Equal("""{"approved":false}""", declined.Body)

[<Fact>]
let ``the authorization webhook rejects a delivery it cannot verify`` () =
    let signature, payload = authorizationRequest
    let config = { EndpointSecret = secret }
    Assert.Equal(400, (handleAuthorizationRequest (approveUpTo 5000) config signature (payload.Replace("2500", "1"))).StatusCode)
    Assert.Equal(400, (handleAuthorizationRequest (approveUpTo 5000) config "" payload).StatusCode)
    Assert.Equal(500, (handleAuthorizationRequest (approveUpTo 5000) { EndpointSecret = "whsec_..." } signature payload).StatusCode)

[<Fact>]
let ``an authorization request that cannot be read is declined, not left to the Dashboard default`` () =
    // A verified request whose authorization has a value this FunStripe version does not know
    let signature, payload = signed "issuing_authorization.request" ((fixture "issuing-authorization").Replace("\"authorization_method\": \"chip\"", "\"authorization_method\": \"brain_waves\""))
    let response = handleAuthorizationRequest (approveUpTo 5000) { EndpointSecret = secret } signature payload
    Assert.Equal(200, response.StatusCode)
    Assert.Equal("""{"approved":false}""", response.Body)
    Assert.Contains(("Stripe-Version", Config.DefaultStripeApiVersion), response.Headers)

[<Fact>]
let ``a decision that throws is a decline`` () =
    let signature, payload = authorizationRequest
    let response = handleAuthorizationRequest (fun _ -> failwith "policy service down") { EndpointSecret = secret } signature payload
    Assert.Equal("""{"approved":false}""", response.Body)

// =============================================================================
// Regular webhook endpoint
// =============================================================================

let deliver (eventType: string) (objectJson: string) =
    let signature, payload = signed eventType objectJson
    Issuing.handleWebhookRequest { EndpointSecret = secret } signature payload

let assertProcessed (expected: string) (result: WebhookResult, statusCode: int) =
    Assert.Equal(200, statusCode)
    match result with
    | Processed message -> Assert.Contains(expected, message)
    | other -> Assert.Fail($"Expected Processed, got {other}")

[<Fact>]
let ``issuing_card.created names the card and cardholder`` () =
    async {
        let! response = deliver "issuing_card.created" (fixture "issuing-card")
        assertProcessed "card ****4242 created for Jenny Rosen" response
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``issuing_authorization.created flags a decision made without the webhook`` () =
    async {
        let timedOut =
            (fixture "issuing-authorization")
                .Replace("\"request_history\": []", """ "request_history": [ { "amount": 2500, "amount_details": null, "approved": true, "authorization_code": null, "created": 1790000000, "currency": "eur", "merchant_amount": 2500, "merchant_currency": "eur", "network_risk_score": null, "reason": "webhook_timeout", "reason_message": null, "requested_at": null } ] """)
                .Replace("\"approved\": false", "\"approved\": true")
        let! response = deliver "issuing_authorization.created" timedOut
        assertProcessed "approved (without your webhook's answer)" response
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``issuing_transaction.created reports the captured amount`` () =
    async {
        let! response = deliver "issuing_transaction.created" (fixture "issuing-transaction")
        assertProcessed "Transaction ipi_1Test: -2500 EUR" response
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``issuing_personalization_design.activated says the design can be used`` () =
    async {
        let! response = deliver "issuing_personalization_design.activated" (fixture "issuing-personalization-design")
        assertProcessed "Design ipcd_1Test approved" response
    } |> Async.StartImmediateAsTask :> Task
