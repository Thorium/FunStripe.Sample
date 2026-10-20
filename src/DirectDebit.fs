module DirectDebit

// Direct debit: save a customer's bank account with a mandate, then pull payments from it
// whenever they are due (invoices, instalments, top-ups), without the customer being present.
//
//   1. Create a SetupIntent for the scheme (`createSetupIntent`)
//   2. The customer enters their bank details in the Payment Element and accepts the mandate
//      (frontend/direct-debit.html). Stripe shows the mandate text the scheme requires.
//   3. `setup_intent.succeeded`: store the payment method and the mandate (`processEvent`)
//   4. Charge the saved account off-session (`chargeSavedAccount`). A bank debit is not
//      instant: the payment intent stays `processing` until the bank confirms, which takes
//      days, so fulfil the order on `payment_intent.succeeded`, not on the API response.
//   5. `mandate.updated` with status `inactive`: the customer revoked the mandate at their
//      bank, so stop charging that payment method.
//
// See examples/DirectDebit.fsx for a run against Stripe test mode.

open FunStripe
open FunStripe.IsoTypes
open Stripe.Event
open Stripe.Mandate
open Stripe.PaymentMethod
open StripeRequest.Setup
open StripeRequest.Payment
open StripeService
open WebhookEvents
open WebhookHandler

/// The bank debit schemes of this sample. Each is a payment method of its own, with its own
/// countries and currency; turn on the ones you need in the Stripe Dashboard first.
type Scheme =
    /// SEPA Direct Debit: EUR, from a bank account in the SEPA area (IBAN)
    | SepaDebit
    /// ACH Direct Debit: USD, from a US bank account. The Payment Element verifies the
    /// account instantly with Financial Connections, or with microdeposits as a fallback.
    | UsBankAccount
    /// Bacs Direct Debit: GBP, from a UK bank account (sort code and account number)
    | BacsDebit
    /// BECS Direct Debit: AUD, from an Australian bank account (BSB and account number)
    | AuBecsDebit

/// The currency a scheme debits in
let currencyOf scheme =
    match scheme with
    | SepaDebit -> IsoCurrencyCode.EUR
    | UsBankAccount -> IsoCurrencyCode.USD
    | BacsDebit -> IsoCurrencyCode.GBP
    | AuBecsDebit -> IsoCurrencyCode.AUD

let private setupIntentType scheme =
    match scheme with
    | SepaDebit -> SetupIntents.Create'AllowedPaymentMethodTypes.SepaDebit
    | UsBankAccount -> SetupIntents.Create'AllowedPaymentMethodTypes.UsBankAccount
    | BacsDebit -> SetupIntents.Create'AllowedPaymentMethodTypes.BacsDebit
    | AuBecsDebit -> SetupIntents.Create'AllowedPaymentMethodTypes.AuBecsDebit

let private paymentIntentType scheme =
    match scheme with
    | SepaDebit -> PaymentIntents.Create'AllowedPaymentMethodTypes.SepaDebit
    | UsBankAccount -> PaymentIntents.Create'AllowedPaymentMethodTypes.UsBankAccount
    | BacsDebit -> PaymentIntents.Create'AllowedPaymentMethodTypes.BacsDebit
    | AuBecsDebit -> PaymentIntents.Create'AllowedPaymentMethodTypes.AuBecsDebit

/// ACH: verify the account through Financial Connections (the customer logs in to their
/// bank), with microdeposits for banks it does not cover
let private usBankAccountOptions =
    SetupIntents.Create'PaymentMethodOptions.New(
        usBankAccount =
            SetupIntents.Create'PaymentMethodOptionsUsBankAccount.New(
                financialConnections =
                    SetupIntents.Create'PaymentMethodOptionsUsBankAccountFinancialConnections.New(
                        permissions = [ SetupIntents.Create'PaymentMethodOptionsUsBankAccountFinancialConnectionsPermissions.PaymentMethod ]
                    ),
                verificationMethod = SetupIntents.Create'PaymentMethodOptionsUsBankAccountVerificationMethod.Automatic
            )
    )

/// Request options for a setup intent that saves a bank account for off-session debits.
/// The frontend confirms it with the Payment Element, which collects the bank details and
/// the customer's acceptance of the mandate.
let setupIntentOptions (scheme: Scheme) (customerId: string) =
    SetupIntents.CreateOptions.New(
        customer = customerId,
        allowedPaymentMethodTypes = [ setupIntentType scheme ],
        usage = SetupIntents.Create'Usage.OffSession,
        ?paymentMethodOptions = (match scheme with UsBankAccount -> Some usBankAccountOptions | _ -> None)
    )

/// Creates the setup intent the frontend confirms (see frontend/direct-debit.html)
let createSetupIntent (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (scheme: Scheme) (customerId: string) =
    setupIntentOptions scheme customerId
    |> SetupIntents.Create (settings |> withKey idempotencyKey)

/// The customer's acceptance of the mandate, as seen by your server when they accepted it
type MandateAcceptance = {
    /// The IP address the customer's browser accepted the mandate from
    IpAddress: string
    /// The User-Agent header of that browser
    UserAgent: string
}

/// SEPA test IBANs (docs.stripe.com/testing#sepa-direct-debit). They only work with a test key.
module TestIbans =
    /// The payment intent goes from `processing` to `succeeded`
    [<Literal>]
    let succeeds = "DE89370400440532013000"
    /// The payment intent goes from `processing` to `requires_payment_method`
    [<Literal>]
    let fails = "DE62370400440532013001"

/// Request options that save a SEPA account in one server-side call: the IBAN and the mandate
/// acceptance go with the request and the setup intent is confirmed straight away.
///
/// The sample uses this to run without a browser. A production integration collects the IBAN
/// and the acceptance in the Payment Element instead, which shows the customer the mandate
/// text SEPA requires; sending an IBAN through your server also puts it in your logs.
let sepaSetupWithIbanOptions (customerId: string) (accountHolderName: string) (email: string) (iban: string) (acceptance: MandateAcceptance) =
    SetupIntents.CreateOptions.New(
        customer = customerId,
        allowedPaymentMethodTypes = [ SetupIntents.Create'AllowedPaymentMethodTypes.SepaDebit ],
        usage = SetupIntents.Create'Usage.OffSession,
        confirm = true,
        paymentMethodData =
            SetupIntents.Create'PaymentMethodData.New(
                type' = SetupIntents.Create'PaymentMethodDataType.SepaDebit,
                sepaDebit = SetupIntents.Create'PaymentMethodDataSepaDebit.New(iban = iban),
                // SEPA requires the account holder's name and email
                billingDetails =
                    SetupIntents.Create'PaymentMethodDataBillingDetails.New(
                        name = Choice1Of2 accountHolderName,
                        email = Choice1Of2 email
                    )
            ),
        mandateData =
            Choice1Of2 (
                SetupIntents.Create'MandateDataSecretKey.New(
                    customerAcceptance =
                        SetupIntents.Create'MandateDataSecretKeyCustomerAcceptance.New(
                            type' = SetupIntents.Create'MandateDataSecretKeyCustomerAcceptanceType.Online,
                            online =
                                SetupIntents.Create'MandateDataSecretKeyCustomerAcceptanceOnline.New(
                                    ipAddress = acceptance.IpAddress,
                                    userAgent = acceptance.UserAgent
                                )
                        )
                )
            )
    )

/// Saves a SEPA account server-side (see `sepaSetupWithIbanOptions`)
let setupSepaWithIban (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (customerId: string) (accountHolderName: string) (email: string) (iban: string) (acceptance: MandateAcceptance) =
    sepaSetupWithIbanOptions customerId accountHolderName email iban acceptance
    |> SetupIntents.Create (settings |> withKey idempotencyKey)

/// What a succeeded setup intent leaves you to store with the customer
type SavedBankAccount = {
    CustomerId: string
    PaymentMethodId: string
    /// The mandate that authorises the debits. Stripe uses it automatically when you charge
    /// the payment method; keep it to show the customer and to react to `mandate.updated`.
    MandateId: string option
}

/// The bank account a setup intent saved, once it has succeeded
let savedBankAccount (setupIntent: SetupIntent) =
    match setupIntent.Status, setupIntentCustomerId setupIntent, setupIntent.PaymentMethod with
    | SetupIntentStatus.Succeeded, Some customerId, Some (StripeId paymentMethodId) ->
        Some {
            CustomerId = customerId
            PaymentMethodId = paymentMethodId
            MandateId = setupIntent.Mandate |> Option.map (fun (StripeId mandateId) -> mandateId)
        }
    | _ -> None

/// Request options for debiting a saved bank account while the customer is not present.
/// The amount is in the smallest currency unit of the scheme's currency.
let chargeOptions (scheme: Scheme) (account: SavedBankAccount) (amount: int) =
    PaymentIntents.CreateOptions.New(
        amount = amount,
        currency = currencyOf scheme,
        customer = account.CustomerId,
        paymentMethod = account.PaymentMethodId,
        ?mandate = account.MandateId,
        allowedPaymentMethodTypes = [ paymentIntentType scheme ],
        confirm = true,
        offSession = Choice1Of2 true
    )

/// Debits a saved bank account. A successful call returns a payment intent in `processing`:
/// the money has been requested, not received. Wait for `payment_intent.succeeded`.
let chargeSavedAccount (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (scheme: Scheme) (account: SavedBankAccount) (amount: int) =
    chargeOptions scheme account amount
    |> PaymentIntents.Create (settings |> withKey idempotencyKey)

// Webhooks
// ========
// Subscribe a webhook endpoint to these events and give it `processEvent`:
// setup_intent.succeeded, setup_intent.setup_failed, mandate.updated,
// payment_intent.processing, payment_intent.succeeded, payment_intent.payment_failed

/// Processes the events of the direct debit flow
let processEvent (stripeEvent: Event) =
    async {
        match stripeEvent.Type with
        | EventType.SetupIntentSucceeded ->
            let setupIntent = stripeEvent.Data.Object |> Util.deserialiseRaw<SetupIntent>
            match savedBankAccount setupIntent with
            | Some account ->
                // Store the payment method and mandate with your customer record
                let mandate = account.MandateId |> Option.defaultValue "none"
                return Processed $"Bank account {account.PaymentMethodId} saved for {account.CustomerId} (mandate {mandate})"
            | None ->
                return Failed $"Setup intent {setupIntent.Id} succeeded without a customer or payment method"

        | EventType.SetupIntentSetupFailed ->
            let setupIntent = stripeEvent.Data.Object |> Util.deserialiseRaw<SetupIntent>
            let reason =
                setupIntent.LastSetupError
                |> Option.bind (fun error -> error.Message)
                |> Option.defaultValue "no message"
            // Ask the customer for another account
            return Processed $"Setup intent {setupIntent.Id} failed: {reason}"

        | EventType.MandateUpdated ->
            let mandate = stripeEvent.Data.Object |> Util.deserialiseRaw<Mandate>
            let (StripeId paymentMethodId) = mandate.PaymentMethod
            match mandate.Status with
            | MandateStatus.Inactive ->
                // The customer revoked the mandate (or the bank closed the account):
                // charging this payment method again will fail
                return Processed $"Mandate {mandate.Id} is inactive, stop debiting {paymentMethodId}"
            | status ->
                return Processed $"Mandate {mandate.Id} of {paymentMethodId} is {status}"

        | EventType.PaymentIntentProcessing ->
            let paymentIntent = stripeEvent.Data.Object |> Util.deserialiseRaw<PaymentIntent>
            // Show the order as "payment pending"; the bank confirms in a few business days
            return Processed $"Debit {paymentIntent.Id} submitted to the bank"

        | EventType.PaymentIntentSucceeded ->
            let paymentIntent = stripeEvent.Data.Object |> Util.deserialiseRaw<PaymentIntent>
            let! result = handlePaymentSuccess paymentIntent
            return toWebhookResult result

        | EventType.PaymentIntentPaymentFailed ->
            // For a debit this is e.g. insufficient funds or a closed account, days after
            // the charge. Retry later or ask the customer for another payment method.
            let paymentIntent = stripeEvent.Data.Object |> Util.deserialiseRaw<PaymentIntent>
            let! result = handlePaymentFailure paymentIntent
            return toWebhookResult result

        | other ->
            return UnsupportedEvent (eventTypeName other)
    }

/// The webhook endpoint for the direct debit events
let handleWebhookRequest (config: WebhookConfig) (signature: string) (payload: string) =
    handleWebhookRequestWith processEvent config signature payload
