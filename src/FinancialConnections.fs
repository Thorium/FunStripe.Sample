module FinancialConnections

// Open banking with Stripe Financial Connections: the customer logs in to their bank in a
// Stripe-hosted modal and grants you access to account data (balances, owners, transactions),
// or to the account and routing numbers for ACH payments. This sample uses the transactions to
// find gambling, a sign that a customer may be vulnerable (see "Gambling transactions" below).
//
//   1. Create a session for the customer (`createSession`), asking only for `Transactions`
//   2. The frontend opens the modal with the session's client secret
//      (frontend/financial-connections.html, `stripe.collectFinancialConnectionsAccounts`)
//   3. Read the linked accounts (`linkedAccounts`)
//   4. Ask for the data: `subscribeToTransactions` (or `refreshBalance`). Both are asynchronous:
//      Stripe fetches the data from the bank and sends `financial_connections.account.refreshed_*`
//   5. Screen the transactions (`screenAccountForGambling`) and route a `NeedsReview` to a person
//
// Financial Connections links US bank accounts. To take ACH payments from a linked account,
// use the direct debit flow with `DirectDebit.UsBankAccount`: its setup intent runs the same
// modal and saves the account as a payment method.
//
// See examples/FinancialConnections.fsx for a run against Stripe test mode.

open System
open System.Text.Json
open FunStripe
open FunStripe.AsyncResultCE
open FunStripe.IsoTypes
open Stripe.Event
open Stripe.PaymentMethod
open StripeRequest.PaymentMethod
open StripeService
open WebhookHandler

/// The data you ask the customer to share. Ask only for what you use: the modal lists each
/// permission, and every extra one makes customers likelier to abandon it.
type Permission =
    | Balances
    | Ownership
    | Transactions

let private sessionPermission permission =
    match permission with
    | Balances -> FinancialConnectionsSessions.Create'Permissions.Balances
    | Ownership -> FinancialConnectionsSessions.Create'Permissions.Ownership
    | Transactions -> FinancialConnectionsSessions.Create'Permissions.Transactions

/// Request options for a session that links the bank accounts of a Stripe customer
let sessionOptions (customerId: string) (permissions: Permission list) =
    FinancialConnectionsSessions.CreateOptions.New(
        accountHolder =
            FinancialConnectionsSessions.Create'AccountHolder.New(
                type' = FinancialConnectionsSessions.Create'AccountHolderType.Customer,
                customer = customerId
            ),
        permissions = (permissions |> List.map sessionPermission),
        filters = FinancialConnectionsSessions.Create'Filters.New(country = IsoCountryCode.US)
    )

/// Creates the session the frontend opens the modal with (its `ClientSecret`)
let createSession (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (customerId: string) (permissions: Permission list) =
    sessionOptions customerId permissions
    |> FinancialConnectionsSessions.Create (settings |> withKey idempotencyKey)

// Linked accounts
// ===============
// FunStripe.Core 3.0.0 types the balance amounts of an account (`balance.current`,
// `balance.cash.available`, ...) as `Map<string, string list>`, but Stripe sends integers, so
// a `FinancialConnectionsAccount` with a balance fails to deserialise. Its `List` also drops
// the `accountHolder` filter from the query string. Until FunStripe fixes both, this sample
// reads accounts as JSON into `LinkedAccount`. Sessions and transactions use FunStripe's
// models as usual.

/// A linked bank account
type LinkedAccount = {
    Id: string
    InstitutionName: string
    DisplayName: string option
    Last4: string option
    /// active, inactive (the bank stopped sharing data) or disconnected
    Status: string
    /// The permissions the customer granted for this account
    Permissions: string list
    /// The latest balance Stripe fetched, if you asked for balances and refreshed them
    Balance: AccountBalance option
}

/// Balances in the smallest currency unit, by lower-case currency code
and AccountBalance = {
    AsOf: DateTimeOffset
    /// What the bank reports as the balance, before pending transactions
    Current: Map<string, int64>
    /// For cash accounts: what can be spent or withdrawn now
    Available: Map<string, int64>
}

let private tryProperty (name: string) (element: JsonElement) =
    match element.TryGetProperty name with
    | true, value when value.ValueKind <> JsonValueKind.Null -> Some value
    | _ -> None

let private tryString name element =
    tryProperty name element |> Option.map (fun value -> value.GetString())

let private amounts (element: JsonElement option) =
    match element with
    | Some amounts when amounts.ValueKind = JsonValueKind.Object ->
        amounts.EnumerateObject()
        |> Seq.map (fun amount -> amount.Name, amount.Value.GetInt64())
        |> Map.ofSeq
    | _ -> Map.empty

let private balanceOf (account: JsonElement) =
    tryProperty "balance" account
    |> Option.map (fun balance ->
        {
            AsOf = DateTimeOffset.FromUnixTimeSeconds((balance.GetProperty "as_of").GetInt64())
            Current = amounts (tryProperty "current" balance)
            Available = amounts (tryProperty "cash" balance |> Option.bind (tryProperty "available"))
        })

/// Reads a Financial Connections account object
let linkedAccountOf (account: JsonElement) =
    {
        Id = (account.GetProperty "id").GetString()
        InstitutionName = tryString "institution_name" account |> Option.defaultValue ""
        DisplayName = tryString "display_name" account
        Last4 = tryString "last4" account
        Status = tryString "status" account |> Option.defaultValue "unknown"
        Permissions =
            match tryProperty "permissions" account with
            | Some permissions -> [ for permission in permissions.EnumerateArray() -> permission.GetString() ]
            | None -> []
        Balance = balanceOf account
    }

/// Reads a Financial Connections account from its JSON, e.g. a webhook event's `data.object`
let parseLinkedAccount (json: string) =
    use document = JsonDocument.Parse json
    linkedAccountOf document.RootElement

let private accountList (page: JsonElement) =
    [ for account in (page.GetProperty "data").EnumerateArray() -> linkedAccountOf account ]

let private accountPath (accountId: string) =
    $"/v1/financial_connections/accounts/{Uri.EscapeDataString accountId}"

/// The accounts the customer linked in a session
let linkedAccounts (settings: RestApi.StripeApiSettings) (sessionId: string) =
    "/v1/financial_connections/accounts"
    |> RestApi.getAsync<JsonElement> settings (Map.ofList [ ("session", box (Some sessionId)) ])
    |> AsyncResult.map accountList

/// All the accounts a customer has linked, in any session (the first 100)
let customerAccounts (settings: RestApi.StripeApiSettings) (customerId: string) =
    "/v1/financial_connections/accounts"
    |> RestApi.getAsync<JsonElement> settings (Map.ofList [ "account_holder[customer]", box (Some customerId); "limit", box (Some 100) ])
    |> AsyncResult.map accountList

/// The account with the data Stripe fetched last
let retrieveAccount (settings: RestApi.StripeApiSettings) (accountId: string) =
    accountPath accountId
    |> RestApi.getAsync<JsonElement> settings Map.empty
    |> AsyncResult.map linkedAccountOf

/// Asks Stripe to fetch the balance from the bank. The answer has `balance_refresh.status`
/// `pending`; read the balance after `financial_connections.account.refreshed_balance`.
let refreshBalance (settings: RestApi.StripeApiSettings) (accountId: string) =
    $"{accountPath accountId}/refresh"
    |> RestApi.postAsync<_, JsonElement> settings Map.empty
        (FinancialConnectionsAccountsRefresh.RefreshOptions.New(
            account = accountId,
            features = [ FinancialConnectionsAccountsRefresh.Refresh'Features.Balance ]))
    |> AsyncResult.map linkedAccountOf

/// Asks Stripe to fetch the transactions now and keep them up to date once a day. Each
/// update sends `financial_connections.account.refreshed_transactions`.
let subscribeToTransactions (settings: RestApi.StripeApiSettings) (accountId: string) =
    $"{accountPath accountId}/subscribe"
    |> RestApi.postAsync<_, JsonElement> settings Map.empty
        (FinancialConnectionsAccountsSubscribe.SubscribeOptions.New(
            account = accountId,
            features = [ FinancialConnectionsAccountsSubscribe.Subscribe'Features.Transactions ]))
    |> AsyncResult.map linkedAccountOf

/// Stops your access to the account, e.g. when the customer closes their account with you
let disconnect (settings: RestApi.StripeApiSettings) (accountId: string) =
    $"{accountPath accountId}/disconnect"
    |> RestApi.postAsync<_, JsonElement> settings Map.empty
        (FinancialConnectionsAccountsDisconnect.DisconnectOptions.New(account = accountId))
    |> AsyncResult.map linkedAccountOf

/// The latest transactions Stripe fetched for the account (up to 100)
let transactions (settings: RestApi.StripeApiSettings) (accountId: string) =
    FinancialConnectionsTransactions.ListOptions.New(account = accountId, limit = 100)
    |> FinancialConnectionsTransactions.List settings
    |> AsyncResult.map (fun page -> page.Data)

/// "Test Bank ****6789 (active)"
let describeAccount (account: LinkedAccount) =
    let name = account.DisplayName |> Option.defaultValue account.InstitutionName
    let last4 = account.Last4 |> Option.map (sprintf " ****%s") |> Option.defaultValue ""
    $"{name}{last4} ({account.Status})"

// Gambling transactions
// =====================
// Regulated firms such as lenders look at gambling in a customer's bank transactions as one
// sign that the customer may be in financial difficulty or otherwise vulnerable. The screening
// below finds those transactions and says when they warrant a closer look. It flags; it does
// not decide. A flagged customer goes to a person trained to handle vulnerable customers, and
// the customer must have agreed to the use of their transaction data for this purpose.
//
// Financial Connections transactions carry no merchant category, only the bank's description,
// so gambling is recognised by the names of gambling operators and words used only for
// gambling. Expect some misses (an operator not in the list, a payment processor's name in the
// description) and some false matches; tune `gamblingTerms` and the thresholds with your data.

/// Gambling operators and words, as they appear in bank descriptions (case does not matter).
/// A starting point for US accounts plus the large European operators: extend it for your market.
let gamblingTerms = [
    // Operators
    "draftkings"; "fanduel"; "betmgm"; "caesars sportsbook"; "betrivers"; "pointsbet"; "bovada"
    "espn bet"; "fanatics sportsbook"; "hard rock bet"; "bet365"; "betfair"; "betway"; "william hill"
    "paddy power"; "sky bet"; "ladbrokes"; "unibet"; "pokerstars"; "betsson"; "leovegas"; "casumo"
    "mr green"; "veikkaus"; "chumba casino"; "stake.us"; "prizepicks"; "underdog fantasy"
    // Words
    "casino"; "sportsbook"; "betting"; "bookmaker"; "poker"; "bingo"; "lottery"; "lotto"; "slots"
]

let private gamblingPattern =
    let alternatives =
        gamblingTerms
        |> List.map (fun term -> Text.RegularExpressions.Regex.Escape(term).Replace(@"\ ", @"\s*"))
        |> String.concat "|"
    Text.RegularExpressions.Regex($@"\b({alternatives})\b", Text.RegularExpressions.RegexOptions.IgnoreCase ||| Text.RegularExpressions.RegexOptions.Compiled)

let private withoutSpaces (s: string) = Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"\s", "")

/// The entry of `gamblingTerms` a transaction description contains, if any
let tryGamblingTerm (description: string) =
    match description with
    | null -> None
    | description ->
        let found = gamblingPattern.Match description
        if found.Success then
            gamblingTerms |> List.tryFind (fun term -> withoutSpaces term = withoutSpaces found.Value)
        else
            None

/// Money out of the account. Stripe's transaction reference does not spell out the sign of
/// `amount`; this sample takes money in as positive and money out as negative, as in the
/// balances. Check it against your test data before relying on the screening.
let isMoneyOut (transaction: FinancialConnectionsTransaction) = transaction.Amount < 0

/// A transaction with a gambling operator
type GamblingTransaction = {
    TransactionId: string
    TransactedAt: DateTime
    /// In the smallest currency unit: negative for a stake, positive for winnings paid out
    Amount: int
    Description: string
    /// The term of `gamblingTerms` the description matched
    MatchedTerm: string
}

/// The gambling found in an account's transactions over a period
type GamblingSummary = {
    /// The period the screened transactions cover
    From: DateTime
    To: DateTime
    Transactions: GamblingTransaction list
    /// Money paid to gambling operators, in the smallest currency unit
    Staked: int64
    /// Money received from them (winnings, withdrawals)
    Returned: int64
    /// All money out of the account in the period
    TotalSpending: int64
    /// `Staked` as a share of `TotalSpending`, from 0 to 1
    ShareOfSpending: decimal
    /// Days in the period with at least one stake
    GamblingDays: int
}

/// Summarises the gambling in the transactions of one account (one currency). Void
/// transactions, which never touched the balance, are left out.
let summariseGambling (transactions: FinancialConnectionsTransaction list) =
    let counted = transactions |> List.filter (fun transaction -> transaction.Status <> FinancialConnectionsTransactionStatus.Void)
    let gambling =
        counted
        |> List.choose (fun transaction ->
            tryGamblingTerm transaction.Description
            |> Option.map (fun term ->
                {
                    TransactionId = transaction.Id
                    TransactedAt = transaction.TransactedAt
                    Amount = transaction.Amount
                    Description = transaction.Description
                    MatchedTerm = term
                }))
        |> List.sortBy _.TransactedAt
    let sumOf amounts = amounts |> List.sumBy (fun (amount: int) -> int64 amount)
    let staked = gambling |> List.filter (fun t -> t.Amount < 0) |> List.map (fun t -> -t.Amount) |> sumOf
    let totalSpending = counted |> List.filter isMoneyOut |> List.map (fun t -> -t.Amount) |> sumOf
    let dates = counted |> List.map _.TransactedAt
    {
        From = if dates.IsEmpty then DateTime.MinValue else List.min dates
        To = if dates.IsEmpty then DateTime.MinValue else List.max dates
        Transactions = gambling
        Staked = staked
        Returned = gambling |> List.filter (fun t -> t.Amount > 0) |> List.map _.Amount |> sumOf
        TotalSpending = totalSpending
        ShareOfSpending = if totalSpending = 0L then 0m else decimal staked / decimal totalSpending
        GamblingDays = gambling |> List.filter (fun t -> t.Amount < 0) |> List.map _.TransactedAt.Date |> List.distinct |> List.length
    }

/// When gambling warrants a review. The values are your policy's, not Stripe's: the defaults
/// below only illustrate the kind of rule.
type ReviewThresholds = {
    /// Review when stakes are more than this share of all spending (0.05 = 5 %)
    MaxShareOfSpending: decimal
    /// Review when the customer gambles on more days than this per 30 days
    MaxGamblingDaysPer30: int
    /// Review when stakes minus returns exceed this, in the smallest currency unit
    MaxNetLoss: int64 option
}

let defaultThresholds = { MaxShareOfSpending = 0.05m; MaxGamblingDaysPer30 = 4; MaxNetLoss = None }

/// The result of screening an account for gambling
type GamblingScreening =
    | NoGamblingFound
    | BelowThresholds of GamblingSummary
    /// Hand the customer to a person who handles vulnerable customers, with the reasons
    | NeedsReview of GamblingSummary * reasons: string list

/// Screens an account's transactions for gambling
let screenForGambling (thresholds: ReviewThresholds) (transactions: FinancialConnectionsTransaction list) =
    let summary = summariseGambling transactions
    if summary.Transactions.IsEmpty then
        NoGamblingFound
    else
        let periodDays = max 1.0 (summary.To - summary.From).TotalDays
        let daysPer30 = float summary.GamblingDays * 30.0 / periodDays
        let netLoss = summary.Staked - summary.Returned
        let reasons = [
            if summary.ShareOfSpending > thresholds.MaxShareOfSpending then
                $"Gambling is {summary.ShareOfSpending:P1} of spending"
            if daysPer30 > float thresholds.MaxGamblingDaysPer30 then
                $"Gambling on {summary.GamblingDays} days in {ceil periodDays} days"
            match thresholds.MaxNetLoss with
            | Some maxNetLoss when netLoss > maxNetLoss -> $"Net gambling loss of {netLoss}"
            | _ -> ()
        ]
        if reasons.IsEmpty then BelowThresholds summary else NeedsReview (summary, reasons)

/// Lists an account's transactions and screens them (see `screenForGambling`)
let screenAccountForGambling (settings: RestApi.StripeApiSettings) (thresholds: ReviewThresholds) (accountId: string) =
    let rec allTransactions (startingAfter: string option) (collected: FinancialConnectionsTransaction list) =
        async {
            let! page =
                FinancialConnectionsTransactions.ListOptions.New(account = accountId, limit = 100, ?startingAfter = startingAfter)
                |> FinancialConnectionsTransactions.List settings
            match page with
            | Error error -> return Error error
            | Ok page ->
                let collected = collected @ page.Data
                match page.HasMore, List.tryLast page.Data with
                | true, Some last -> return! allTransactions (Some last.Id) collected
                | _ -> return Ok collected
        }
    allTransactions None [] |> AsyncResult.map (screenForGambling thresholds)

// Webhooks
// ========
// Subscribe a webhook endpoint to these events and give it `processEvent`:
// financial_connections.account.created, .refreshed_balance, .refreshed_transactions,
// .deactivated, .disconnected

/// Processes the events of linked accounts
let processEvent (stripeEvent: Event) =
    async {
        let account () =
            let (RawJson json) = stripeEvent.Data.Object
            parseLinkedAccount json

        match stripeEvent.Type with
        | EventType.FinancialConnectionsAccountCreated ->
            let account = account ()
            // Store the account ID with your customer, then refresh what you need
            return Processed $"Account linked: {describeAccount account}"

        | EventType.FinancialConnectionsAccountRefreshedBalance ->
            let account = account ()
            match account.Balance with
            | Some balance ->
                let current = balance.Current |> Map.toList |> List.map (fun (currency, amount) -> $"{amount} {currency}") |> String.concat ", "
                return Processed $"Balance of {account.Id} as of {balance.AsOf:u}: {current}"
            | None ->
                // The refresh failed (see balance_refresh.status), e.g. the bank was unavailable
                return Processed $"Balance refresh of {account.Id} finished without a balance"

        | EventType.FinancialConnectionsAccountRefreshedTransactions ->
            // The event carries the account, not the transactions: screen them now
            // (screenAccountForGambling), e.g. on a queue, since that lists every page
            return Processed $"New transactions for {(account ()).Id}"

        | EventType.FinancialConnectionsAccountDeactivated
        | EventType.FinancialConnectionsAccountDisconnected ->
            let account = account ()
            // Stop using the data; ask the customer to link the account again if you need it
            return Processed $"Account {account.Id} is {account.Status}"

        | other ->
            return UnsupportedEvent (eventTypeName other)
    }

/// The webhook endpoint for the Financial Connections events
let handleWebhookRequest (config: WebhookConfig) (signature: string) (payload: string) =
    handleWebhookRequestWith processEvent config signature payload
