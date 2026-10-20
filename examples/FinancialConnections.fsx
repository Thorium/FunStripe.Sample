// Open banking with Financial Connections: the customer links their bank accounts, and you
// screen the transactions for gambling, a sign that the customer may be vulnerable. Runs in
// two steps, because linking needs a browser:
//
//   dotnet fsi examples/FinancialConnections.fsx
//       creates a customer and a session, and prints the session's client secret. Paste it
//       into frontend/financial-connections.html and link an account of a test bank.
//
//   dotnet fsi examples/FinancialConnections.fsx -- fcsess_...
//       reads the accounts linked in that session, subscribes to their transactions and
//       screens them for gambling
//
// The code is in src/FinancialConnections.fs. Needs STRIPE_TEST_API_KEY (see Common.fsx).

#load "Common.fsx"
#load "../src/FinancialConnections.fs"

open System.Threading
open Common
open StripeService
open FinancialConnections

match scriptArgs with
| [] ->
    let operation = newOperation "financial-connections"
    let customer =
        createCustomer settings (idempotencyKeyFor operation "customer") "Jenny" "Rosen" "jenny.rosen+open-banking@example.com"
        |> step "Creating a customer"
    let session =
        createSession settings (idempotencyKeyFor operation "session") customer.Id [ Transactions ]
        |> step "Creating a Financial Connections session"
    printfn ""
    printfn "Open frontend/financial-connections.html, paste the client secret below and link an"
    printfn "account of one of the test banks:"
    printfn $"  {orNone session.ClientSecret}"
    printfn ""
    printfn "Then read the linked accounts with:"
    printfn $"  dotnet fsi examples/FinancialConnections.fsx -- {session.Id}"

| [ sessionId ] ->
    let accounts = linkedAccounts settings sessionId |> step $"Reading the accounts linked in {sessionId}"
    if accounts.IsEmpty then printfn "  No accounts yet: finish linking in the browser first"

    for account in accounts do
        printfn $"  {describeAccount account}"

        // Stripe fetches the transactions from the bank in the background and sends
        // financial_connections.account.refreshed_transactions. A script can just wait a moment.
        subscribeToTransactions settings account.Id |> step "  Subscribing to transactions" |> ignore
        Thread.Sleep 5000

        match screenAccountForGambling settings defaultThresholds account.Id |> step "  Screening the transactions for gambling" with
        | NoGamblingFound ->
            printfn "    No gambling found"
        | BelowThresholds summary ->
            printfn $"    {summary.Transactions.Length} gambling transactions, below the review thresholds"
        | NeedsReview (summary, reasons) ->
            let because = String.concat "; " reasons
            printfn $"    Needs review: {because}"
            for transaction in summary.Transactions |> List.truncate 10 do
                printfn $"      {transaction.TransactedAt:d} {transaction.Amount,8} {transaction.Description} ({transaction.MatchedTerm})"

| _ ->
    eprintfn "Usage: dotnet fsi examples/FinancialConnections.fsx [-- <session ID>]"
    exit 1
