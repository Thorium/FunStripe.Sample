module Memberships

// A gym membership with Stripe Billing: a monthly subscription, paid by SEPA Direct Debit (or
// card), with a joining fee on the first invoice. Stripe bills every month, retries failed
// debits, and sends the events that open or close the door for the member.
//
//   1. The plan lives in Stripe's catalog, found by its lookup key (`ensureCatalog`)
//   2. Sign-up, either way:
//      a. online: a Checkout Session in subscription mode (`createSignupSession`), whose page
//         collects the IBAN and the mandate, or a card
//      b. at the front desk: save the bank account with DirectDebit.fs, then
//         `startMembership` with the payment method
//   3. `invoice.paid` every month: the member is paid up. `invoice.payment_failed`: the debit
//      bounced; Stripe retries it on the schedule of your Billing settings and finally marks
//      the subscription `unpaid` or cancels it, as those settings say
//   4. A freeze (injury, travel): `freezeMembership` pauses billing and the membership
//      period, `unfreezeMembership` starts a new period once its first payment succeeds.
//      Pausing needs the subscription in flexible billing mode, which both sign-ups set.
//   5. Leaving: `cancelAtPeriodEnd` keeps the paid month, `cancelNow` ends it at once
//   6. Self-service: `memberPortalUrl` opens Stripe's customer portal, where the member can
//      change the bank account, see the invoices and, if you allow it, cancel
//
// What the door checks is `accessOf hasPaid subscription`: keep the subscription from the
// `customer.subscription.*` events, and whether the member has ever paid from `invoice.paid`,
// in your member records.
//
// See examples/Memberships.fsx for a run against Stripe test mode.

open FunStripe
open FunStripe.AsyncResultCE
open FunStripe.IsoTypes
open Stripe.Event
open Stripe.PaymentMethod
open Stripe.Price
open StripeRequest.BillingPortal
open StripeRequest.Checkout
open StripeRequest.Prices
open StripeRequest.Subscriptions
open StripeService
open WebhookHandler

// Catalog
// =======

/// The prices of the membership, in the smallest currency unit
type Plan = {
    Currency: IsoCurrencyCode
    MonthlyFee: int
    JoiningFee: int
}

/// The Stripe prices of the plan
type Catalog = {
    MonthlyPriceId: string
    JoiningFeePriceId: string
}

/// The lookup keys that name the prices in your code, whatever their Stripe IDs.
/// To change a price, create a new one with the same lookup key (`transferLookupKey`).
[<Literal>]
let monthlyLookupKey = "gym-membership-monthly"
[<Literal>]
let joiningFeeLookupKey = "gym-membership-joining-fee"

let private priceByLookupKey (settings: RestApi.StripeApiSettings) (lookupKey: string) =
    Prices.ListOptions.New(lookupKeys = [ lookupKey ], active = true, limit = 1)
    |> Prices.List settings
    |> AsyncResult.map (fun page -> page.Data |> List.tryHead)

/// Request options for the monthly fee
let monthlyPriceOptions (plan: Plan) =
    Prices.CreateOptions.New(
        currency = plan.Currency,
        unitAmount = plan.MonthlyFee,
        recurring = Prices.Create'Recurring.New(interval = Prices.Create'RecurringInterval.Month),
        productData = Prices.Create'ProductData.New(name = "Gym membership", statementDescriptor = "GYM MEMBERSHIP"),
        lookupKey = monthlyLookupKey,
        transferLookupKey = true
    )

/// Request options for the joining fee, charged once with the first month
let joiningFeePriceOptions (plan: Plan) =
    Prices.CreateOptions.New(
        currency = plan.Currency,
        unitAmount = plan.JoiningFee,
        productData = Prices.Create'ProductData.New(name = "Joining fee"),
        lookupKey = joiningFeeLookupKey,
        transferLookupKey = true
    )

/// Finds the prices of the plan, creating the ones that do not exist yet. Most gyms create
/// them once (here or in the Dashboard) and only look them up afterwards.
let ensureCatalog (settings: RestApi.StripeApiSettings) (operationId: OperationId) (plan: Plan) =
    let ensure lookupKey (options: Prices.CreateOptions) =
        asyncResult {
            match! priceByLookupKey settings lookupKey with
            | Some (price: Price) -> return price.Id
            | None ->
                let! price = options |> Prices.Create (settings |> withKey (idempotencyKeyFor operationId lookupKey))
                return price.Id
        }
    asyncResult {
        let! monthly = ensure monthlyLookupKey (monthlyPriceOptions plan)
        let! joiningFee = ensure joiningFeeLookupKey (joiningFeePriceOptions plan)
        return { MonthlyPriceId = monthly; JoiningFeePriceId = joiningFee }
    }

// Sign-up
// =======

/// Request options for an online sign-up: Stripe's page collects the payment method (the
/// IBAN and SEPA mandate, or a card) and starts the subscription. The joining fee, a one-time
/// price, goes on the first invoice.
let signupSessionOptions (catalog: Catalog) (memberId: string) (email: string) (successUrl: string) (cancelUrl: string) =
    CheckoutSessions.CreateOptions.New(
        mode = CheckoutSessions.Create'Mode.Subscription,
        uiMode = CheckoutSessions.Create'UiMode.HostedPage,
        lineItems = [
            CheckoutSessions.Create'LineItems.New(price = catalog.MonthlyPriceId, quantity = 1)
            CheckoutSessions.Create'LineItems.New(price = catalog.JoiningFeePriceId, quantity = 1)
        ],
        // Direct debit first: no card expiry to chase, lower fees on a recurring charge
        allowedPaymentMethodTypes = [ CheckoutSessions.Create'AllowedPaymentMethodTypes.SepaDebit; CheckoutSessions.Create'AllowedPaymentMethodTypes.Card ],
        customerEmail = email,
        clientReferenceId = memberId,
        subscriptionData =
            CheckoutSessions.Create'SubscriptionData.New(
                metadata = Map.ofList [ ("member_id", memberId) ],
                // Freezing (pausing) a subscription needs flexible billing mode
                billingMode = CheckoutSessions.Create'SubscriptionDataBillingMode.New(type' = CheckoutSessions.Create'SubscriptionDataBillingModeType.Flexible)
            ),
        successUrl = successUrl,
        cancelUrl = cancelUrl
    )

/// Creates the sign-up page; redirect the new member to its `Url`
let createSignupSession (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (catalog: Catalog) (memberId: string) (email: string) (successUrl: string) (cancelUrl: string) =
    signupSessionOptions catalog memberId email successUrl cancelUrl
    |> CheckoutSessions.Create (settings |> withKey idempotencyKey)

/// Request options that start a membership paid from a saved payment method, e.g. a bank
/// account saved with `DirectDebit.setupSepaWithIban` at the front desk
let membershipOptions (catalog: Catalog) (memberId: string) (customerId: string) (paymentMethodId: string) =
    Subscriptions.CreateOptions.New(
        customer = customerId,
        items = [ Subscriptions.Create'Items.New(price = catalog.MonthlyPriceId) ],
        addInvoiceItems = [ Subscriptions.Create'AddInvoiceItems.New(price = catalog.JoiningFeePriceId) ],
        defaultPaymentMethod = paymentMethodId,
        // The member is at the desk, but the debit runs without them
        offSession = true,
        // Freezing (pausing) a subscription needs flexible billing mode
        billingMode = Subscriptions.Create'BillingMode.New(type' = Subscriptions.Create'BillingModeType.Flexible),
        metadata = Map.ofList [ ("member_id", memberId) ]
    )

/// Starts a membership. The first invoice (month plus joining fee) is debited at once; a bank
/// debit takes days to confirm, see `invoice.paid`.
let startMembership (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (catalog: Catalog) (memberId: string) (customerId: string) (paymentMethodId: string) =
    membershipOptions catalog memberId customerId paymentMethodId
    |> Subscriptions.Create (settings |> withKey idempotencyKey)

// Changes
// =======

/// Freezes the membership: no invoices, and the membership period stops where it is
let freezeMembership (settings: RestApi.StripeApiSettings) (subscriptionId: string) =
    SubscriptionsPause.PauseOptions.New(subscription = subscriptionId, type' = SubscriptionsPause.Pause'Type.Subscription)
    |> SubscriptionsPause.Pause settings

/// Ends a freeze: a new monthly period starts today and is debited at once, less the unused
/// days Stripe credited when the membership was frozen. The membership stays `paused`, with a
/// `PendingUpdate`, until that payment succeeds: for a bank debit, a few days (`accessOf` lets
/// the member in meanwhile). Without `resume_on_payment_success` Stripe would not attempt the
/// payment at all, and would void the invoice after 23 hours.
let unfreezeMembership (settings: RestApi.StripeApiSettings) (subscriptionId: string) =
    SubscriptionsResume.ResumeOptions.New(
        subscription = subscriptionId,
        paymentBehavior = SubscriptionsResume.Resume'PaymentBehavior.ResumeOnPaymentSuccess,
        billingCycleAnchor = SubscriptionsResume.Resume'BillingCycleAnchor.New(type' = SubscriptionsResume.Resume'BillingCycleAnchorType.Now),
        prorationBehavior = SubscriptionsResume.Resume'ProrationBehavior.None'
    )
    |> SubscriptionsResume.Resume settings

/// The member leaves at the end of the month they have paid for (or stays: `false`)
let cancelAtPeriodEnd (settings: RestApi.StripeApiSettings) (subscriptionId: string) (cancel: bool) =
    Subscriptions.UpdateOptions.New(subscriptionExposedId = subscriptionId, cancelAtPeriodEnd = cancel)
    |> Subscriptions.Update settings

/// Ends the membership at once, e.g. for a breach of the gym rules
let cancelNow (settings: RestApi.StripeApiSettings) (subscriptionId: string) =
    Subscriptions.CancelOptions.New(subscriptionExposedId = subscriptionId)
    |> Subscriptions.Cancel settings

/// A link to Stripe's customer portal for the member. What the portal allows (cancelling,
/// changing the payment method, ...) is set under Settings > Billing > Customer portal.
let memberPortalUrl (settings: RestApi.StripeApiSettings) (customerId: string) (returnUrl: string) =
    BillingPortalSessions.CreateOptions.New(customer = customerId, returnUrl = returnUrl)
    |> BillingPortalSessions.Create settings
    |> AsyncResult.map _.Url

// Access
// ======

/// What the door lets a member do
type Access =
    | Full
    /// A new member whose first debit has not cleared yet. A SEPA debit takes days, and the
    /// subscription is `active` meanwhile; it may still bounce.
    | Provisional
    /// A debit failed and Stripe is retrying it, or a freeze is ending and its first debit is on
    /// its way: let the member in, and remind them
    | Grace
    | NoAccess

/// The access of a membership. `hasPaid`: an invoice of the member has been paid
/// (`invoice.paid`), so the bank account is known to work. Change the policy here, e.g. no
/// provisional access or no grace period.
let accessOf (hasPaid: bool) (subscription: Subscription) =
    match subscription.Status with
    | SubscriptionStatus.Trialing -> Full
    | SubscriptionStatus.Active -> if hasPaid then Full else Provisional
    // A member who never paid does not get the retry period: the account may not be theirs
    | SubscriptionStatus.PastDue -> if hasPaid then Grace else NoAccess
    | SubscriptionStatus.Paused when subscription.PendingUpdate.IsSome -> Grace
    | SubscriptionStatus.Incomplete
    | SubscriptionStatus.IncompleteExpired
    | SubscriptionStatus.Paused
    | SubscriptionStatus.Unpaid
    | SubscriptionStatus.Canceled -> NoAccess

/// The membership (subscription) an invoice is for
let membershipOfInvoice (invoice: Invoice) =
    invoice.Parent
    |> Option.bind _.SubscriptionDetails
    |> Option.map (fun details -> let (StripeId pid) = details.Subscription in pid)

// Webhooks
// ========
// Subscribe a webhook endpoint to these events and give it `processEvent`:
// invoice.paid, invoice.payment_failed, customer.subscription.created,
// customer.subscription.updated, customer.subscription.paused, customer.subscription.resumed,
// customer.subscription.deleted

/// Processes the membership events
let processEvent (stripeEvent: Event) =
    async {
        let subscription () = stripeEvent.Data.Object |> Util.deserialiseRaw<Subscription>
        let memberOf (subscription: Subscription) = subscription.Metadata.TryFind "member_id" |> Option.defaultValue "(unknown)"

        match stripeEvent.Type with
        | EventType.InvoicePaid ->
            let invoice = stripeEvent.Data.Object |> Util.deserialiseRaw<Invoice>
            let membership = membershipOfInvoice invoice |> Option.defaultValue "(none)"
            // Record that the member has paid (`hasPaid` of accessOf)
            return Processed $"Membership {membership} paid: {invoice.AmountPaid} {invoice.Currency}"

        | EventType.InvoicePaymentFailed ->
            let invoice = stripeEvent.Data.Object |> Util.deserialiseRaw<Invoice>
            let membership = membershipOfInvoice invoice |> Option.defaultValue "(none)"
            // Tell the member (e.g. with invoice.HostedInvoiceUrl to pay by card instead)
            match invoice.NextPaymentAttempt with
            | Some retry -> return Processed $"Debit {invoice.AttemptCount} for membership {membership} failed, Stripe retries {retry:u}"
            | None -> return Processed $"Debit for membership {membership} failed for the last time"

        | EventType.CustomerSubscriptionCreated
        | EventType.CustomerSubscriptionUpdated
        | EventType.CustomerSubscriptionPaused
        | EventType.CustomerSubscriptionResumed
        | EventType.CustomerSubscriptionDeleted ->
            // Store the subscription: the door asks accessOf about it
            let subscription = subscription ()
            let leaving =
                match subscription.CancelAtPeriodEnd, subscription.CancelAt with
                | true, Some cancelAt -> $", leaving {cancelAt:d}"
                | _ -> ""
            let resuming = if subscription.PendingUpdate.IsSome then ", resuming once paid" else ""
            return Processed $"Member {memberOf subscription}: {subscription.Status}{resuming}{leaving}"

        | other ->
            return UnsupportedEvent (eventTypeName other)
    }

/// The webhook endpoint for the membership events
let handleWebhookRequest (config: WebhookConfig) (signature: string) (payload: string) =
    handleWebhookRequestWith processEvent config signature payload
