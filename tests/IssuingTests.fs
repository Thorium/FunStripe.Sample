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
    let form = cardholderOptions "Jenny" "Rosen" "jenny@example.com" "+358401234567" helsinki terms |> formOf
    Assert.Equal("+358401234567", form.["phone_number"])
    Assert.Equal("individual", form.["type"])
    Assert.Equal("Jenny Rosen", form.["name"])
    Assert.Equal("Jenny", form.["individual[first_name]"])
    Assert.Equal("Rosen", form.["individual[last_name]"])
    // Stripe's spelling; FunStripe.Core 3.1.0 and older sent line_1, which Stripe refuses
    Assert.Equal("Mannerheimintie 1", form.["billing[address][line1]"])
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
        let fileJson = """{ "id": "file_1Logo", "object": "file", "purpose": "issuing_logo", "created": 1790000000, "size": 4, "filename": "logo.png", "type": "png" }"""
        use stripe = new FakeStripe(respondWithRoutes [ "/v1/files", fileJson ])
        let! result = uploadCardLogo stripe.Settings "logo.png" [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy |]
        Assert.Equal(Ok "file_1Logo", result)
        let request = Assert.Single stripe.Requests
        Assert.Equal("POST", request.Method)
        // FunStripe sends the key as Basic auth (key as the user name), which Stripe accepts too
        Assert.Equal(Some $"""Basic {Convert.ToBase64String(Text.Encoding.ASCII.GetBytes "sk_test_fake:")}""", request.Authorization)
        Assert.StartsWith("multipart/form-data", request.ContentType |> Option.defaultValue "")
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// Funding
// =============================================================================

[<Fact>]
let ``fundingInstructions asks for the bank details of the Issuing balance and reads them`` () =
    async {
        use stripe = new FakeStripe(respondWithRoutes [ "/v1/issuing/funding_instructions", fixture "issuing-funding-instructions" ])
        let! result = fundingInstructions stripe.Settings IsoCurrencyCode.GBP "gb_bank_transfer"
        let instructions = match result with Ok value -> value | Error error -> failwith (describeStripeError error)
        let address = Assert.Single instructions.BankTransfer.FinancialAddresses
        Assert.Equal(Some "281214", address.SortCode |> Option.map _.SortCode)
        Assert.Equal<string list>([ "bacs"; "fps" ], address.SupportedNetworks)
        let request = Assert.Single stripe.Requests
        Assert.Equal("POST", request.Method)
        Assert.Equal("gbp", request.Form.["currency"])
        Assert.Equal("bank_transfer", request.Form.["funding_type"])
        Assert.Equal("gb_bank_transfer", request.Form.["bank_transfer[type]"])
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``fundingInstructions reads an IBAN without a BIC`` () =
    async {
        let iban =
            """{ "object": "funding_instructions", "currency": "eur", "funding_type": "bank_transfer", "livemode": false,
                 "bank_transfer": { "country": "FI", "type": "eu_bank_transfer", "financial_addresses": [ {
                   "iban": { "account_holder_address": null, "account_holder_name": "Stripe Technology Europe Ltd",
                             "bank_address": null, "bic": null, "country": "FI", "iban": "FI2112345600000785" },
                   "supported_networks": [ "sepa" ], "type": "iban" } ] } }"""
        use stripe = new FakeStripe(respondWithRoutes [ "/v1/issuing/funding_instructions", iban ])
        let! result = fundingInstructions stripe.Settings IsoCurrencyCode.EUR "eu_bank_transfer"
        let instructions = match result with Ok value -> value | Error error -> failwith (describeStripeError error)
        let address = Assert.Single instructions.BankTransfer.FinancialAddresses
        let details = Assert.Single(Option.toList address.Iban)
        Assert.Equal("FI2112345600000785", details.Iban)
        Assert.Equal(None, details.Bic)
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``regionOf maps an account's country to its Issuing region, currency and funding`` () =
    Assert.Equal(Some UnitedKingdom, regionOf IsoCountryCode.GB)
    Assert.Equal(Some UnitedStates, regionOf IsoCountryCode.US)
    Assert.Equal(Some EuroArea, regionOf IsoCountryCode.FI)
    Assert.Equal(Some EuroArea, regionOf IsoCountryCode.LT')
    Assert.Equal(None, regionOf IsoCountryCode.SE)
    Assert.Equal<(IsoCurrencyCode * string option) list>(
        [ IsoCurrencyCode.GBP, Some "gb_bank_transfer"; IsoCurrencyCode.EUR, Some "eu_bank_transfer"; IsoCurrencyCode.USD, None ],
        [ for region in [ UnitedKingdom; EuroArea; UnitedStates ] -> currencyOf region, bankTransferTypeOf region ])

[<Fact>]
let ``topUpIssuingBalance pulls money into the Issuing balance, not the payments balance`` () =
    async {
        let topUp = """{ "id": "tu_1Test", "object": "topup", "amount": 10000, "created": 1790000000, "currency": "usd", "livemode": false, "metadata": {}, "status": "pending" }"""
        use stripe = new FakeStripe(respondWithRoutes [ "/v1/topups", topUp ])
        let key = idempotencyKeyFor (OperationId.tryCreate "op-issuing" |> Option.get) "top-up"
        let! result = topUpIssuingBalance stripe.Settings key 10000 IsoCurrencyCode.USD "Sample"
        Assert.Equal(Ok "tu_1Test", result |> Result.map _.Id)
        let request = Assert.Single stripe.Requests
        Assert.Equal("issuing", request.Form.["destination_balance"])
        Assert.Equal("10000", request.Form.["amount"])
        Assert.Equal("usd", request.Form.["currency"])
        Assert.Equal(Some "op-issuing:top-up", request.IdempotencyKey)
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``fundBalance adds money to the Issuing balance in test mode`` () =
    async {
        use stripe = new FakeStripe(respondWithRoutes [ "/v1/test_helpers/issuing/fund_balance", fixture "issuing-funding-instructions" ])
        let! result = TestMode.fundBalance stripe.Settings 10000 IsoCurrencyCode.GBP
        Assert.True(Result.isOk result)
        let request = Assert.Single stripe.Requests
        Assert.Equal("10000", request.Form.["amount"])
        Assert.Equal("gbp", request.Form.["currency"])
    } |> Async.StartImmediateAsTask :> Task

/// Answers each balance request with the next of `issuingEur` as the Issuing balance in EUR,
/// and the last one from then on
let issuingBalances (issuingEur: int list) =
    let answered = ref 0
    fun (request: RecordedRequest) ->
        let amount = issuingEur.[min answered.Value (issuingEur.Length - 1)]
        answered.Value <- answered.Value + 1
        match request.Path with
        | "/v1/balance" ->
            200,
            $$"""{ "object": "balance", "livemode": false, "available": [ { "amount": 0, "currency": "eur" } ], "pending": [],
                   "issuing": { "available": [ { "amount": {{amount}}, "currency": "eur" }, { "amount": 99999, "currency": "gbp" } ] } }"""
        | _ -> 404, errorJson

[<Fact>]
let ``waitForBalance asks again until the top-up is in the Issuing balance`` () =
    async {
        use stripe = new FakeStripe(issuingBalances [ 500; 500; 10500 ])
        let! result = TestMode.waitForBalance stripe.Settings IsoCurrencyCode.EUR 10500 (TimeSpan.FromMilliseconds 10.) (TimeSpan.FromSeconds 10.)
        Assert.Equal(Ok (Some 10500), result)
        Assert.Equal(3, stripe.Requests.Length)
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``waitForBalance gives up after the timeout`` () =
    async {
        use stripe = new FakeStripe(issuingBalances [ 500 ])
        let! result = TestMode.waitForBalance stripe.Settings IsoCurrencyCode.EUR 10500 (TimeSpan.FromMilliseconds 20.) (TimeSpan.FromMilliseconds 200.)
        Assert.Equal(Ok None, result)
        Assert.InRange(stripe.Requests.Length, 2, 11)
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

/// The fixture as Stripe sends it when the merchant raises an approved authorization of `amount`
/// by `increase` (a tip, fuel): `pending_request.amount` is only the increase.
let raised (amount: int) (increase: int) =
    let json = Text.Json.Nodes.JsonNode.Parse(fixture "issuing-authorization")
    json.["approved"] <- Text.Json.Nodes.JsonValue.Create true
    json.["amount"] <- Text.Json.Nodes.JsonValue.Create amount
    json.["pending_request"].["amount"] <- Text.Json.Nodes.JsonValue.Create increase
    json.ToJsonString() |> Util.deserialise<IssuingAuthorization>

[<Fact>]
let ``approveUpTo reads Stripe's real increase request: approved, amount so far, pending the increase`` () =
    // Captured with the Stripe CLI in test mode: 25.00 approved, a merchant asks for 8.00 more
    let increase = fixture "issuing-authorization-increase" |> Util.deserialise<IssuingAuthorization>
    Assert.True(increase.Approved)
    Assert.Equal((2500, Some 800), (increase.Amount, increase.PendingRequest |> Option.map _.Amount))
    Assert.Equal(Approve, approveUpTo 3300 increase)
    Assert.Equal(Decline "3300 is over the limit of 3000", approveUpTo 3000 increase)

[<Fact>]
let ``approveUpTo checks the total of an increase, not only the increase`` () =
    Assert.Equal(Approve, approveUpTo 2500 (raised 2000 500))
    Assert.Equal(Decline "2800 is over the limit of 2500", approveUpTo 2500 (raised 2000 800))

/// The fixture as Stripe test mode sends it after 25.00 authorized and 15.00 captured, with the
/// authorization left open (status pending)
let openAfterCaptureJson () =
    let json = Text.Json.Nodes.JsonNode.Parse(fixture "issuing-authorization")
    json.["amount"] <- Text.Json.Nodes.JsonValue.Create 2500
    let change (amount: int) (kind: string) =
        Text.Json.Nodes.JsonNode.Parse(
            $$"""{ "id": "txn_{{kind}}", "object": "balance_transaction", "amount": {{amount}}, "available_on": 1790000000,
                  "balance_type": "issuing", "created": 1790000000, "currency": "eur", "description": null, "exchange_rate": null,
                  "fee": 0, "fee_details": [], "net": {{amount}}, "reporting_category": "{{kind}}", "source": null,
                  "status": "available", "type": "{{kind}}" }""")
    json.["balance_transactions"] <- Text.Json.Nodes.JsonArray(change -2500 "issuing_authorization_hold", change 1500 "issuing_authorization_release")
    json.ToJsonString()

[<Fact>]
let ``heldAmount is the holds less releases while pending, not the authorized amount`` () =
    let openAfterCapture = openAfterCaptureJson () |> Util.deserialise<IssuingAuthorization>
    Assert.Equal(IssuingAuthorizationStatus.Pending, openAfterCapture.Status)
    Assert.Equal(1000, heldAmount openAfterCapture)
    Assert.Equal(0, heldAmount { openAfterCapture with Status = IssuingAuthorizationStatus.Closed })

[<Fact>]
let ``the authorization webhook answers in the API version of the request`` () =
    let payload = (snd authorizationRequest).Replace(Config.DefaultStripeApiVersion, "2026-09-30.endive")
    let signature = signatureHeader secret payload (DateTimeOffset.UtcNow.ToUnixTimeSeconds())
    let response = handleAuthorizationRequest (approveUpTo 5000) { EndpointSecret = secret } signature payload
    Assert.Contains(("Stripe-Version", "2026-09-30.endive"), response.Headers)

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
let ``issuing_authorization.updated reports what the authorization holds`` () =
    async {
        let! response = deliver "issuing_authorization.updated" (openAfterCaptureJson ())
        assertProcessed "holds 1000" response
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
