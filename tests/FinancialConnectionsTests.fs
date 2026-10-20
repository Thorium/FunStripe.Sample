module FinancialConnectionsTests

open System
open System.Threading.Tasks
open Xunit
open FunStripe
open StripeService
open FinancialConnections
open FakeStripeServer
open WebhookHandler
open WebhookSamples

let key (step: string) =
    idempotencyKeyFor (OperationId.tryCreate "op-fc" |> Option.get) step

let respond =
    respondWithRoutes [
        "/v1/financial_connections/sessions", fixture "financial-connections-session"
        "/v1/financial_connections/accounts", fixture "financial-connections-accounts"
        "/v1/financial_connections/accounts/fca_1Test", fixture "financial-connections-account"
        "/v1/financial_connections/accounts/fca_1Test/refresh", fixture "financial-connections-account"
        "/v1/financial_connections/accounts/fca_1Test/subscribe", fixture "financial-connections-account"
        "/v1/financial_connections/transactions", fixture "financial-connections-transactions"
    ]

let expectOk result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"Expected Ok, got Error: {describeStripeError error}"

// =============================================================================
// Sessions
// =============================================================================

[<Fact>]
let ``sessionOptions links the customer's US accounts with the permissions asked for`` () =
    let form = sessionOptions "cus_FakeCustomer" [ Balances; Transactions ] |> Util.serialiseForm |> Map.ofSeq
    Assert.Equal("customer", form.["account_holder[type]"])
    Assert.Equal("cus_FakeCustomer", form.["account_holder[customer]"])
    Assert.Equal("balances", form.["permissions[0]"])
    Assert.Equal("transactions", form.["permissions[1]"])
    Assert.Equal("US", form.["filters[country]"])

[<Fact>]
let ``createSession returns the client secret for the frontend`` () =
    async {
        use stripe = new FakeStripe(respond)
        let! session = createSession stripe.Settings (key "session") "cus_FakeCustomer" [ Ownership ]
        Assert.Equal(Some "fcsess_1Test_secret_fake", (expectOk session).ClientSecret)
        let request = Assert.Single stripe.Requests
        Assert.Equal(Some "op-fc:session", request.IdempotencyKey)
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// Accounts
// =============================================================================

[<Fact>]
let ``parseLinkedAccount reads the integer balances FunStripe 3.0.0 cannot`` () =
    let account = parseLinkedAccount (fixture "financial-connections-account")
    Assert.Equal("fca_1Test", account.Id)
    Assert.Equal(Some "6789", account.Last4)
    Assert.Equal<string list>([ "balances"; "ownership"; "transactions" ], account.Permissions)
    match account.Balance with
    | Some balance ->
        Assert.Equal<Map<string, int64>>(Map.ofList [ "usd", 6000L ], balance.Current)
        Assert.Equal<Map<string, int64>>(Map.ofList [ "usd", 5000L ], balance.Available)
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds 1790000000L, balance.AsOf)
    | None -> Assert.Fail "Expected a balance"

[<Fact>]
let ``the FunStripe model still fails on a balance, so the workaround is still needed`` () =
    // When this test fails, FunStripe reads Financial Connections balances: replace
    // LinkedAccount with Stripe.PaymentMethod.FinancialConnectionsAccount
    Assert.ThrowsAny<exn>(fun () ->
        fixture "financial-connections-account"
        |> Util.deserialise<Stripe.PaymentMethod.FinancialConnectionsAccount>
        |> ignore)
    |> ignore

[<Fact>]
let ``linkedAccounts asks for the accounts of the session`` () =
    async {
        use stripe = new FakeStripe(respond)
        let! accounts = linkedAccounts stripe.Settings "fcsess_1Test"
        let accounts = expectOk accounts
        Assert.Equal<string list>([ "fca_1Test"; "fca_2Test" ], accounts |> List.map _.Id)
        Assert.Equal(None, accounts.[1].Balance)
        let request = Assert.Single stripe.Requests
        Assert.Equal("GET", request.Method)
        Assert.Equal("fcsess_1Test", request.Query.["session"])
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``customerAccounts filters by account holder`` () =
    async {
        use stripe = new FakeStripe(respond)
        let! _ = customerAccounts stripe.Settings "cus_FakeCustomer"
        let request = Assert.Single stripe.Requests
        Assert.Equal("cus_FakeCustomer", request.Query.["account_holder[customer]"])
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``refreshBalance and subscribeToTransactions post the feature`` () =
    async {
        use stripe = new FakeStripe(respond)
        let! refreshed = refreshBalance stripe.Settings "fca_1Test"
        let! subscribed = subscribeToTransactions stripe.Settings "fca_1Test"
        Assert.Equal("fca_1Test", (expectOk refreshed).Id)
        Assert.Equal("fca_1Test", (expectOk subscribed).Id)
        match stripe.Requests with
        | [ refresh; subscribe ] ->
            Assert.Equal("/v1/financial_connections/accounts/fca_1Test/refresh", refresh.Path)
            Assert.Equal("balance", refresh.Form.["features[0]"])
            Assert.Equal("/v1/financial_connections/accounts/fca_1Test/subscribe", subscribe.Path)
            Assert.Equal("transactions", subscribe.Form.["features[0]"])
        | requests -> Assert.Fail($"Expected 2 requests, got {requests.Length}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``transactions lists the account's transactions with FunStripe's model`` () =
    async {
        use stripe = new FakeStripe(respond)
        let! transactions = transactions stripe.Settings "fca_1Test"
        let transaction = Assert.Single(expectOk transactions)
        Assert.Equal(-1500, transaction.Amount)
        Assert.Equal("COFFEE SHOP", transaction.Description)
        Assert.Equal("fca_1Test", (Assert.Single stripe.Requests).Query.["account"])
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``describeAccount falls back to the institution name`` () =
    let account = parseLinkedAccount (fixture "financial-connections-account")
    Assert.Equal("Sample Checking ****6789 (active)", describeAccount account)
    Assert.Equal("StripeBank (active)", describeAccount { account with DisplayName = None; Last4 = None })

// =============================================================================
// Webhooks
// =============================================================================

[<Fact>]
let ``refreshed_balance reports the new balance`` () =
    async {
        let secret = "whsec_test_fc"
        let payload = eventPayload "evt_test_fc" "financial_connections.account.refreshed_balance" (fixture "financial-connections-account")
        let signature = signatureHeader secret payload (DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        let! result, statusCode = FinancialConnections.handleWebhookRequest { EndpointSecret = secret } signature payload
        Assert.Equal(200, statusCode)
        match result with
        | Processed message -> Assert.Contains("6000 usd", message)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// Gambling screening
// =============================================================================

/// A transaction on day `day` of a 30-day period. Money out is negative.
let transaction (id: string) (day: int) (amount: int) (description: string) (status: string) =
    let at = 1790000000L + int64 (day - 1) * 86400L
    $$"""{ "id": "{{id}}", "object": "financial_connections.transaction", "account": "fca_1Test", "amount": {{amount}},
          "currency": "usd", "description": "{{description}}", "livemode": false, "status": "{{status}}",
          "status_transitions": { "posted_at": {{at}}, "void_at": null }, "transacted_at": {{at}},
          "transaction_refresh": "fctxr_1", "updated": {{at}} }"""
    |> Util.deserialise<Stripe.PaymentMethod.FinancialConnectionsTransaction>

let month = [
    transaction "t1" 1 -120000 "RENT PAYMENT" "posted"
    transaction "t2" 2 -4500 "GROCERY MART" "posted"
    transaction "t3" 3 -2000 "DRAFTKINGS*SPORTSBOOK" "posted"
    transaction "t4" 5 -5000 "FanDuel Inc" "posted"
    transaction "t5" 9 3000 "DRAFTKINGS WITHDRAWAL" "posted"
    transaction "t6" 12 -2500 "CASINO ROYALE ONLINE" "pending"
    transaction "t7" 20 -9900 "BET365" "void"
    transaction "t8" 30 -3000 "COFFEE SHOP" "posted"
]

[<Fact>]
let ``tryGamblingTerm finds operators and gambling words, whatever the case`` () =
    Assert.Equal(Some "draftkings", tryGamblingTerm "DRAFTKINGS*SPORTSBOOK 800-123")
    Assert.Equal(Some "william hill", tryGamblingTerm "WILLIAMHILL.COM")
    Assert.Equal(Some "casino", tryGamblingTerm "Online Casino Ltd")
    Assert.Equal(None, tryGamblingTerm "ALPHABET INC")
    Assert.Equal(None, tryGamblingTerm "Grocery Mart")
    Assert.Equal(None, tryGamblingTerm null)

[<Fact>]
let ``summariseGambling adds up stakes, returns and gambling days, without void transactions`` () =
    let summary = summariseGambling month
    Assert.Equal<string list>([ "t3"; "t4"; "t5"; "t6" ], summary.Transactions |> List.map _.TransactionId)
    Assert.Equal(9500L, summary.Staked)
    Assert.Equal(3000L, summary.Returned)
    Assert.Equal(120000L + 4500L + 9500L + 3000L, summary.TotalSpending)
    Assert.Equal(3, summary.GamblingDays)

[<Fact>]
let ``screenForGambling asks for a review when a threshold is crossed`` () =
    // Stakes of 95.00 are 6.9 % of 1370.00 spent; 3 gambling days from day 1 to day 30
    match screenForGambling { defaultThresholds with MaxShareOfSpending = 0.05m; MaxGamblingDaysPer30 = 2 } month with
    | NeedsReview (_, reasons) -> Assert.Equal<string list>([ "Gambling is 6.9% of spending"; "Gambling on 3 days in 29 days" ], reasons)
    | other -> Assert.Fail($"Expected NeedsReview, got {other}")

[<Fact>]
let ``screenForGambling reports gambling below the thresholds, or none at all`` () =
    match screenForGambling { defaultThresholds with MaxShareOfSpending = 0.10m } month with
    | BelowThresholds summary -> Assert.Equal(4, summary.Transactions.Length)
    | other -> Assert.Fail($"Expected BelowThresholds, got {other}")
    Assert.Equal(NoGamblingFound, screenForGambling defaultThresholds [ month.[0]; month.[1] ])

[<Fact>]
let ``a net loss threshold applies when set`` () =
    match screenForGambling { defaultThresholds with MaxNetLoss = Some 5000L } month with
    | NeedsReview (_, reasons) -> Assert.Contains("Net gambling loss of 6500", reasons)
    | other -> Assert.Fail($"Expected NeedsReview, got {other}")

[<Fact>]
let ``screenAccountForGambling reads every page of transactions`` () =
    async {
        let page (hasMore: bool) (transactions: string list) =
            $$"""{ "object": "list", "url": "/v1/financial_connections/transactions", "has_more": {{(if hasMore then "true" else "false")}}, "data": [ {{String.concat ", " transactions}} ] }"""
        let json (pid: string) (amount: int) (description: string) =
            $$"""{ "id": "{{pid}}", "object": "financial_connections.transaction", "account": "fca_1Test", "amount": {{amount}}, "currency": "usd", "description": "{{description}}", "livemode": false, "status": "posted", "status_transitions": { "posted_at": 1790000000, "void_at": null }, "transacted_at": 1790000000, "transaction_refresh": "fctxr_1", "updated": 1790000000 }"""
        let respond (request: RecordedRequest) =
            match request.Query.TryFind "starting_after" with
            | None -> 200, page true [ json "t1" -1000 "BETMGM" ]
            | Some "t1" -> 200, page false [ json "t2" -1000 "SUPERMARKET" ]
            | Some _ -> 404, errorJson
        use stripe = new FakeStripe(respond)
        let! screening = screenAccountForGambling stripe.Settings defaultThresholds "fca_1Test"
        match expectOk screening with
        | NeedsReview (summary, _) ->
            Assert.Equal(1, summary.Transactions.Length)
            Assert.Equal(2000L, summary.TotalSpending)
        | other -> Assert.Fail($"Expected NeedsReview, got {other}")
        Assert.Equal(2, stripe.Requests.Length)
    } |> Async.StartImmediateAsTask :> Task
