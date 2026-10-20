module DirectDebitTests

open System.Threading.Tasks
open Xunit
open FunStripe
open FunStripe.IsoTypes
open Stripe.PaymentMethod
open StripeService
open DirectDebit
open FakeStripeServer
open WebhookHandler
open WebhookSamples

let key (step: string) =
    idempotencyKeyFor (OperationId.tryCreate "op-dd" |> Option.get) step

let formOf options = options |> Util.serialiseForm |> Map.ofSeq

let acceptance = { IpAddress = "203.0.113.7"; UserAgent = "Mozilla/5.0 (test)" }

let savedSepaAccount = { CustomerId = "cus_FakeCustomer"; PaymentMethodId = "pm_1TestSepa"; MandateId = Some "mandate_1Test" }

// =============================================================================
// Setup intents
// =============================================================================

[<Fact>]
let ``setupIntentOptions allows only the scheme's payment method, for off-session use`` () =
    let form = setupIntentOptions SepaDebit "cus_FakeCustomer" |> formOf
    Assert.Equal("cus_FakeCustomer", form.["customer"])
    Assert.Equal("sepa_debit", form.["allowed_payment_method_types[0]"])
    Assert.Equal("off_session", form.["usage"])
    Assert.DoesNotContain(form.Keys, fun key -> key.StartsWith "payment_method_options")

[<Fact>]
let ``setupIntentOptions names each scheme as Stripe does`` () =
    let allowedType scheme = (setupIntentOptions scheme "cus_1" |> formOf).["allowed_payment_method_types[0]"]
    Assert.Equal("us_bank_account", allowedType UsBankAccount)
    Assert.Equal("bacs_debit", allowedType BacsDebit)
    Assert.Equal("au_becs_debit", allowedType AuBecsDebit)

[<Fact>]
let ``ACH setup verifies the account with Financial Connections`` () =
    let form = setupIntentOptions UsBankAccount "cus_FakeCustomer" |> formOf
    Assert.Equal("payment_method", form.["payment_method_options[us_bank_account][financial_connections][permissions][0]"])
    Assert.Equal("automatic", form.["payment_method_options[us_bank_account][verification_method]"])

[<Fact>]
let ``sepaSetupWithIbanOptions confirms with the IBAN, the account holder and the mandate acceptance`` () =
    let form = sepaSetupWithIbanOptions "cus_FakeCustomer" "Jenny Rosen" "jenny@example.com" TestIbans.succeeds acceptance |> formOf
    Assert.Equal("true", form.["confirm"])
    Assert.Equal("sepa_debit", form.["payment_method_data[type]"])
    Assert.Equal("DE89370400440532013000", form.["payment_method_data[sepa_debit][iban]"])
    Assert.Equal("Jenny Rosen", form.["payment_method_data[billing_details][name]"])
    Assert.Equal("jenny@example.com", form.["payment_method_data[billing_details][email]"])
    Assert.Equal("online", form.["mandate_data[customer_acceptance][type]"])
    Assert.Equal("203.0.113.7", form.["mandate_data[customer_acceptance][online][ip_address]"])
    Assert.Equal("Mozilla/5.0 (test)", form.["mandate_data[customer_acceptance][online][user_agent]"])

[<Fact>]
let ``savedBankAccount takes the payment method and mandate of a succeeded setup intent`` () =
    let setupIntent = fixture "setup-intent-sepa-succeeded" |> Util.deserialise<SetupIntent>
    Assert.Equal(Some savedSepaAccount, savedBankAccount setupIntent)
    Assert.Equal(None, savedBankAccount { setupIntent with Status = SetupIntentStatus.Processing })

[<Fact>]
let ``createSetupIntent posts to setup intents with its idempotency key`` () =
    async {
        use stripe = new FakeStripe(respondOk)
        let! result = createSetupIntent stripe.Settings (key "setup-intent") SepaDebit "cus_FakeCustomer"
        Assert.True(Result.isOk result)
        let request = Assert.Single stripe.Requests
        Assert.Equal("/v1/setup_intents", request.Path)
        Assert.Equal(Some "op-dd:setup-intent", request.IdempotencyKey)
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// Charging a saved account
// =============================================================================

[<Fact>]
let ``chargeOptions debits the saved account off-session in the scheme's currency`` () =
    let form = chargeOptions SepaDebit savedSepaAccount 1999 |> formOf
    Assert.Equal("1999", form.["amount"])
    Assert.Equal("eur", form.["currency"])
    Assert.Equal("cus_FakeCustomer", form.["customer"])
    Assert.Equal("pm_1TestSepa", form.["payment_method"])
    Assert.Equal("mandate_1Test", form.["mandate"])
    Assert.Equal("sepa_debit", form.["allowed_payment_method_types[0]"])
    Assert.Equal("true", form.["confirm"])
    Assert.Equal("true", form.["off_session"])

[<Fact>]
let ``chargeOptions leaves the mandate out when none was stored`` () =
    let form = chargeOptions BacsDebit { savedSepaAccount with MandateId = None } 500 |> formOf
    Assert.Equal("gbp", form.["currency"])
    Assert.False(form.ContainsKey "mandate")

[<Fact>]
let ``currencyOf gives each scheme its currency`` () =
    Assert.Equal(IsoCurrencyCode.EUR, currencyOf SepaDebit)
    Assert.Equal(IsoCurrencyCode.USD, currencyOf UsBankAccount)
    Assert.Equal(IsoCurrencyCode.GBP, currencyOf BacsDebit)
    Assert.Equal(IsoCurrencyCode.AUD, currencyOf AuBecsDebit)

// =============================================================================
// Webhooks
// =============================================================================

let secret = "whsec_test_direct_debit"

let deliver (eventType: string) (objectJson: string) =
    let payload = eventPayload "evt_test_dd" eventType objectJson
    let signature = signatureHeader secret payload (System.DateTimeOffset.UtcNow.ToUnixTimeSeconds())
    DirectDebit.handleWebhookRequest { EndpointSecret = secret } signature payload

[<Fact>]
let ``setup_intent.succeeded reports the saved account and its mandate`` () =
    async {
        let! result, statusCode = deliver "setup_intent.succeeded" (fixture "setup-intent-sepa-succeeded")
        Assert.Equal(200, statusCode)
        match result with
        | Processed message ->
            Assert.Contains("pm_1TestSepa", message)
            Assert.Contains("mandate_1Test", message)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``an inactive mandate tells you to stop debiting the payment method`` () =
    async {
        let! result, _ = deliver "mandate.updated" (fixture "mandate-inactive")
        match result with
        | Processed message -> Assert.Contains("stop debiting pm_1TestSepa", message)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``a processing debit is acknowledged`` () =
    async {
        let processing = paymentIntentObject.Replace("\"status\": \"succeeded\"", "\"status\": \"processing\"")
        let! result, statusCode = deliver "payment_intent.processing" processing
        Assert.Equal(200, statusCode)
        match result with
        | Processed message -> Assert.Contains("submitted to the bank", message)
        | other -> Assert.Fail($"Expected Processed, got {other}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``events of other use cases are acknowledged but not processed`` () =
    async {
        let! result, statusCode = deliver "customer.created" FakeStripeServer.customerJson
        Assert.Equal(200, statusCode)
        Assert.Equal(UnsupportedEvent "customer.created", result)
    } |> Async.StartImmediateAsTask :> Task
