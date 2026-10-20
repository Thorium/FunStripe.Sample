module WebshopCheckout

// A webshop for physical goods with Stripe Checkout: your site keeps the catalog and the
// cart, Stripe's hosted payment page collects the shipping address, the shipping option and
// the payment (cards, Apple Pay, Google Pay and the local methods you turn on in the Dashboard).
//
//   1. The customer checks out: price the cart from your own catalog (never from what the
//      browser sends) and create a Checkout Session for the order (`createCheckoutSession`)
//   2. Redirect the browser to the session's `Url` (HTTP 303)
//   3. `checkout.session.completed`: if the payment is `paid`, fulfil the order
//      (`orderToFulfil` gives you the lines and the shipping address). Delayed payment methods
//      such as SEPA Direct Debit complete `unpaid`: wait for
//      `checkout.session.async_payment_succeeded` (or `_failed`)
//   4. `checkout.session.expired`: the customer left; release any stock you held for the order
//   5. Refund a returned order with `refundOrder`
//
// The success page may call `orderToFulfil` as well, to show the order: fulfil it only once,
// whichever of the two gets there first (key it by your order ID).
//
// See examples/WebshopCheckout.fsx for a run against Stripe test mode.

open FunStripe
open FunStripe.AsyncResultCE
open FunStripe.IsoTypes
open Stripe.Checkout
open Stripe.Event
open Stripe.FundingInstructions
open Stripe.PaymentMethod
open Stripe.Price
open StripeRequest.Checkout
open StripeRequest.Refunds
open StripeService
open WebhookHandler

/// A product of your catalog
type Product = {
    Sku: string
    Name: string
    Description: string option
    /// Public image URLs, shown on the payment page
    Images: string list
    /// The price with VAT, in the smallest currency unit
    UnitAmount: int
}

/// A line of the customer's cart. The browser sends only SKUs and quantities.
type CartLine = { Sku: string; Quantity: int }

/// A shipping option the customer picks on the payment page
type ShippingOption = {
    Name: string
    /// In the smallest currency unit
    Amount: int
    MinBusinessDays: int
    MaxBusinessDays: int
}

/// The settings of your shop
type Shop = {
    Currency: IsoCurrencyCode
    /// The countries you deliver to
    ShipsTo: CheckoutSessions.Create'ShippingAddressCollectionAllowedCountries list
    ShippingOptions: ShippingOption list
    /// Let Stripe Tax work out the VAT, from the shipping address. Needs Stripe Tax set up
    /// in the Dashboard (your business address and tax registrations). Without it the
    /// prices are charged as they are.
    AutomaticTax: bool
    /// Where the payment page returns the customer to. Stripe replaces
    /// {CHECKOUT_SESSION_ID} with the session's ID.
    SuccessUrl: string
    CancelUrl: string
}

/// A cart line Checkout can sell: the product it is for, from your catalog
let private priceCart (catalog: Map<string, Product>) (cart: CartLine list) =
    let unknown = cart |> List.filter (fun line -> not (catalog.ContainsKey line.Sku)) |> List.map _.Sku
    if cart.IsEmpty then Error "The cart is empty"
    elif not unknown.IsEmpty then Error $"""Unknown products: {String.concat ", " unknown}"""
    elif cart |> List.exists (fun line -> line.Quantity < 1 || line.Quantity > 99) then Error "Invalid quantity"
    else Ok [ for line in cart -> catalog.[line.Sku], line.Quantity ]

/// Request options for the Checkout Session of an order, or the reason the cart cannot be sold
let checkoutSessionOptions (shop: Shop) (catalog: Map<string, Product>) (orderId: string) (customerEmail: string option) (cart: CartLine list) =
    priceCart catalog cart
    |> Result.map (fun lines ->
        // VAT is included in consumer prices in the EU; tell Stripe Tax so
        let taxBehavior = if shop.AutomaticTax then Some CheckoutSessions.Create'LineItemsPriceDataTaxBehavior.Inclusive else None
        CheckoutSessions.CreateOptions.New(
            mode = CheckoutSessions.Create'Mode.Payment,
            uiMode = CheckoutSessions.Create'UiMode.HostedPage,
            lineItems = [
                for product, quantity in lines ->
                    CheckoutSessions.Create'LineItems.New(
                        quantity = quantity,
                        priceData =
                            CheckoutSessions.Create'LineItemsPriceData.New(
                                currency = shop.Currency,
                                unitAmount = product.UnitAmount,
                                ?taxBehavior = taxBehavior,
                                productData =
                                    CheckoutSessions.Create'LineItemsPriceDataProductData.New(
                                        name = product.Name,
                                        ?description = product.Description,
                                        images = product.Images,
                                        // Which of your products a line is, in the line items you read back
                                        metadata = Map.ofList [ ("sku", product.Sku) ]
                                    )
                            )
                    )
            ],
            shippingAddressCollection = CheckoutSessions.Create'ShippingAddressCollection.New(allowedCountries = shop.ShipsTo),
            shippingOptions = [
                for option in shop.ShippingOptions ->
                    CheckoutSessions.Create'ShippingOptions.New(
                        shippingRateData =
                            CheckoutSessions.Create'ShippingOptionsShippingRateData.New(
                                type' = CheckoutSessions.Create'ShippingOptionsShippingRateDataType.FixedAmount,
                                displayName = option.Name,
                                // Shipping is priced like the products: VAT included
                                ?taxBehavior = (if shop.AutomaticTax then Some CheckoutSessions.Create'ShippingOptionsShippingRateDataTaxBehavior.Inclusive else None),
                                fixedAmount = CheckoutSessions.Create'ShippingOptionsShippingRateDataFixedAmount.New(amount = option.Amount, currency = shop.Currency),
                                deliveryEstimate =
                                    CheckoutSessions.Create'ShippingOptionsShippingRateDataDeliveryEstimate.New(
                                        minimum = CheckoutSessions.Create'ShippingOptionsShippingRateDataDeliveryEstimateMinimum.New(unit = CheckoutSessions.Create'ShippingOptionsShippingRateDataDeliveryEstimateMinimumUnit.BusinessDay, value = option.MinBusinessDays),
                                        maximum = CheckoutSessions.Create'ShippingOptionsShippingRateDataDeliveryEstimateMaximum.New(unit = CheckoutSessions.Create'ShippingOptionsShippingRateDataDeliveryEstimateMaximumUnit.BusinessDay, value = option.MaxBusinessDays)
                                    )
                            )
                    )
            ],
            // The carrier may need it for the delivery
            phoneNumberCollection = CheckoutSessions.Create'PhoneNumberCollection.New(enabled = true),
            automaticTax = CheckoutSessions.Create'AutomaticTax.New(enabled = shop.AutomaticTax),
            // A receipt-like invoice PDF the customer can download, which some customers need
            invoiceCreation = CheckoutSessions.Create'InvoiceCreation.New(enabled = true),
            allowPromotionCodes = true,
            // Your order ID, to find the order again from the session and its events
            clientReferenceId = orderId,
            metadata = Map.ofList [ ("order_id", orderId) ],
            paymentIntentData = CheckoutSessions.Create'PaymentIntentData.New(metadata = Map.ofList [ ("order_id", orderId) ]),
            ?customerEmail = customerEmail,
            successUrl = shop.SuccessUrl,
            cancelUrl = shop.CancelUrl
            // No allowedPaymentMethodTypes: Checkout offers every payment method turned on in
            // the Dashboard that suits the customer, the currency and the amount
        ))

/// A short fingerprint of a cart, the same for the same SKUs and quantities in any order.
/// Stripe rejects an idempotency key used again with other parameters, so the key of a
/// Checkout Session needs it: `idempotencyKeyFor order $"checkout-{cartFingerprint cart}"`.
/// A retry of the same checkout then gets the same session back, and a changed cart a new one.
/// (Change the step too when anything else that goes into the session changes, e.g. the email.)
let cartFingerprint (cart: CartLine list) =
    let text =
        cart
        |> List.sortBy (fun line -> line.Sku, line.Quantity)
        |> List.map (fun line -> $"{line.Sku}:{line.Quantity}")
        |> String.concat ","
    use sha = System.Security.Cryptography.SHA256.Create()
    let hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes text)
    (System.Convert.ToHexString hash).Substring(0, 16).ToLowerInvariant()

/// Creates the Checkout Session of an order. Redirect the customer to its `Url`. Derive the
/// idempotency key from the order and the cart (see `cartFingerprint`).
let createCheckoutSession (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (shop: Shop) (catalog: Map<string, Product>) (orderId: string) (customerEmail: string option) (cart: CartLine list) =
    match checkoutSessionOptions shop catalog orderId customerEmail cart with
    | Error reason -> async { return Error reason }
    | Ok options ->
        options
        |> CheckoutSessions.Create (settings |> withKey idempotencyKey)
        |> AsyncResult.mapError describeStripeError

/// What the warehouse needs to send an order
type OrderToFulfil = {
    OrderId: string
    SessionId: string
    /// For refunds and for finding the payment in the Dashboard
    PaymentIntentId: string option
    CustomerEmail: string option
    Phone: string option
    ShipTo: ShippingAddress option
    Lines: OrderLine list
    /// Including shipping and tax, in the smallest currency unit
    AmountTotal: int
    Currency: IsoCurrencyCode option
}

and ShippingAddress = {
    Name: string
    Line1: string
    Line2: string option
    PostalCode: string
    City: string
    Country: IsoCountryCode option
}

and OrderLine = { Sku: string option; Description: string; Quantity: int; AmountTotal: int }

let private shippingAddressOf (session: CheckoutSession) =
    session.CollectedInformation
    |> Option.bind _.ShippingDetails
    |> Option.map (fun details ->
        let address: Address = details.Address
        {
            Name = details.Name
            Line1 = address.Line1 |> Option.defaultValue ""
            Line2 = address.Line2
            PostalCode = address.PostalCode |> Option.defaultValue ""
            City = address.City |> Option.defaultValue ""
            Country = address.Country
        })

/// The order of a paid session, None while it is unpaid
let orderOf (session: CheckoutSession) (lineItems: Item list) =
    match session.PaymentStatus with
    | CheckoutSessionPaymentStatus.Paid | CheckoutSessionPaymentStatus.NoPaymentRequired ->
        Some {
            OrderId = session.ClientReferenceId |> Option.defaultValue ""
            SessionId = session.Id
            PaymentIntentId = session.PaymentIntent |> Option.map (fun (StripeId pid) -> pid)
            CustomerEmail = session.CustomerDetails |> Option.bind _.Email
            Phone = session.CustomerDetails |> Option.bind _.Phone
            ShipTo = shippingAddressOf session
            Lines = [
                for item in lineItems ->
                    {
                        Sku = item.Price |> Option.bind (fun price -> match price.Product with PriceProduct'AnyOf.Product product -> product.Metadata.TryFind "sku" | PriceProduct'AnyOf.String _ | PriceProduct'AnyOf.DeletedProduct _ -> None)
                        Description = item.Description |> Option.defaultValue ""
                        Quantity = item.Quantity |> Option.defaultValue 1
                        AmountTotal = item.AmountTotal
                    }
            ]
            AmountTotal = session.AmountTotal |> Option.defaultValue 0
            Currency = session.Currency
        }
    | CheckoutSessionPaymentStatus.Unpaid -> None

/// Reads a session and its line items (with their products, for the SKU) and returns the
/// order to fulfil once it is paid
let orderToFulfil (settings: RestApi.StripeApiSettings) (sessionId: string) =
    asyncResult {
        let! session = CheckoutSessions.RetrieveOptions.New(session = sessionId) |> CheckoutSessions.Retrieve settings
        let! lineItems =
            CheckoutSessionsLineItems.ListLineItemsOptions.New(session = sessionId, limit = 100, expand = [ "data.price.product" ])
            |> CheckoutSessionsLineItems.ListLineItems settings
        return orderOf session lineItems.Data
    }

/// Ends an open session at once, e.g. when the cart changes and you create a new one (with a
/// new idempotency key, see `cartFingerprint`)
let expireSession (settings: RestApi.StripeApiSettings) (sessionId: string) =
    CheckoutSessionsExpire.ExpireOptions.New(session = sessionId)
    |> CheckoutSessionsExpire.Expire settings

/// Refunds an order in full, or `amount` of it (e.g. one returned mug). The refund reaches the
/// customer in 5 to 10 business days; Stripe sends `charge.refunded`.
let refundOrder (settings: RestApi.StripeApiSettings) (idempotencyKey: IdempotencyKey) (paymentIntentId: string) (amount: int option) =
    Refunds.CreateOptions.New(paymentIntent = paymentIntentId, ?amount = amount, reason = Refunds.Create'Reason.RequestedByCustomer)
    |> Refunds.Create (settings |> withKey idempotencyKey)

// Webhooks
// ========
// Subscribe a webhook endpoint to these events and give it `processEvent settings`:
// checkout.session.completed, checkout.session.async_payment_succeeded,
// checkout.session.async_payment_failed, checkout.session.expired, charge.refunded

/// Processes the checkout events. It reads the line items of a paid session from Stripe,
/// hence the settings.
let processEvent (settings: RestApi.StripeApiSettings) (stripeEvent: Event) =
    async {
        let session () = stripeEvent.Data.Object |> Util.deserialiseRaw<CheckoutSession>

        match stripeEvent.Type with
        | EventType.CheckoutSessionCompleted
        | EventType.CheckoutSessionAsyncPaymentSucceeded ->
            let session = session ()
            match session.PaymentStatus with
            | CheckoutSessionPaymentStatus.Unpaid ->
                // A delayed payment method: async_payment_succeeded follows when the money is in
                return Processed $"Order {session.ClientReferenceId} placed, waiting for the payment"
            | CheckoutSessionPaymentStatus.NoPaymentRequired | CheckoutSessionPaymentStatus.Paid ->
                match! orderToFulfil settings session.Id with
                | Ok (Some order) ->
                    // Hand the order to the warehouse, once per order ID
                    let items = order.Lines |> List.sumBy _.Quantity
                    return Processed $"Order {order.OrderId} paid: ship {items} items"
                | Ok None -> return Processed $"Order {session.ClientReferenceId} is not paid yet"
                | Error error -> return Failed $"Could not read session {session.Id}: {describeStripeError error}"

        | EventType.CheckoutSessionAsyncPaymentFailed ->
            let session = session ()
            // Tell the customer and cancel the order (or send a new payment link)
            return Processed $"Payment of order {session.ClientReferenceId} failed"

        | EventType.CheckoutSessionExpired ->
            let session = session ()
            return Processed $"Checkout of order {session.ClientReferenceId} expired: release its stock"

        | EventType.ChargeRefunded ->
            // Find the order by the payment intent you stored with it (OrderToFulfil.PaymentIntentId)
            let charge = stripeEvent.Data.Object |> Util.deserialiseRaw<Charge>
            let paymentIntent = charge.PaymentIntent |> Option.map (fun (StripeId pid) -> pid) |> Option.defaultValue "(none)"
            return Processed $"{charge.AmountRefunded} {charge.Currency} refunded of payment {paymentIntent}"

        | other ->
            return UnsupportedEvent (eventTypeName other)
    }

/// The webhook endpoint for the checkout events
let handleWebhookRequest (settings: RestApi.StripeApiSettings) (config: WebhookConfig) (signature: string) (payload: string) =
    handleWebhookRequestWith (processEvent settings) config signature payload
