module WebshopCheckoutTests

open System
open System.Threading.Tasks
open Xunit
open FunStripe
open FunStripe.IsoTypes
open Stripe.Checkout
open Stripe.PaymentMethod
open StripeRequest.Checkout
open StripeService
open WebshopCheckout
open FakeStripeServer
open WebhookHandler
open WebhookSamples

let formOf options = options |> Util.serialiseForm |> Map.ofSeq

let key (step: string) =
    idempotencyKeyFor (OperationId.tryCreate "order-1042" |> Option.get) step

let catalog =
    [
        { Sku = "MUG-350"; Name = "Ceramic mug"; Description = Some "350 ml, dishwasher safe"; Images = [ "https://example.com/mug.png" ]; UnitAmount = 1490 }
        { Sku = "MUG-500"; Name = "Large mug"; Description = None; Images = []; UnitAmount = 1890 }
    ]
    |> List.map (fun product -> product.Sku, product)
    |> Map.ofList

let shop = {
    Currency = IsoCurrencyCode.EUR
    ShipsTo = [ CheckoutSessions.Create'ShippingAddressCollectionAllowedCountries.FI; CheckoutSessions.Create'ShippingAddressCollectionAllowedCountries.SE ]
    ShippingOptions = [ { Name = "Posti parcel"; Amount = 500; MinBusinessDays = 2; MaxBusinessDays = 4 } ]
    AutomaticTax = false
    SuccessUrl = "https://shop.example.com/thanks?session_id={CHECKOUT_SESSION_ID}"
    CancelUrl = "https://shop.example.com/cart"
}

let sessionForm cart =
    match checkoutSessionOptions shop catalog "order-1042" (Some "jenny@example.com") cart with
    | Ok options -> formOf options
    | Error reason -> failwith reason

// =============================================================================
// Checkout Sessions
// =============================================================================

[<Fact>]
let ``the session sells the cart at the catalog's prices`` () =
    let form = sessionForm [ { Sku = "MUG-350"; Quantity = 2 }; { Sku = "MUG-500"; Quantity = 1 } ]
    Assert.Equal("payment", form.["mode"])
    Assert.Equal("hosted_page", form.["ui_mode"])
    Assert.Equal("2", form.["line_items[0][quantity]"])
    Assert.Equal("1490", form.["line_items[0][price_data][unit_amount]"])
    Assert.Equal("eur", form.["line_items[0][price_data][currency]"])
    Assert.Equal("Ceramic mug", form.["line_items[0][price_data][product_data][name]"])
    Assert.Equal("MUG-350", form.["line_items[0][price_data][product_data][metadata][sku]"])
    Assert.Equal("1890", form.["line_items[1][price_data][unit_amount]"])
    Assert.False(form.ContainsKey "line_items[0][price_data][tax_behavior]")

[<Fact>]
let ``the session collects the shipping address and offers the shipping options`` () =
    let form = sessionForm [ { Sku = "MUG-350"; Quantity = 1 } ]
    Assert.Equal("FI", form.["shipping_address_collection[allowed_countries][0]"])
    Assert.Equal("SE", form.["shipping_address_collection[allowed_countries][1]"])
    Assert.Equal("Posti parcel", form.["shipping_options[0][shipping_rate_data][display_name]"])
    Assert.Equal("500", form.["shipping_options[0][shipping_rate_data][fixed_amount][amount]"])
    Assert.Equal("business_day", form.["shipping_options[0][shipping_rate_data][delivery_estimate][minimum][unit]"])
    Assert.Equal("4", form.["shipping_options[0][shipping_rate_data][delivery_estimate][maximum][value]"])
    Assert.Equal("true", form.["phone_number_collection[enabled]"])

[<Fact>]
let ``the session carries the order ID and leaves the payment methods to the Dashboard`` () =
    let form = sessionForm [ { Sku = "MUG-350"; Quantity = 1 } ]
    Assert.Equal("order-1042", form.["client_reference_id"])
    Assert.Equal("order-1042", form.["metadata[order_id]"])
    Assert.Equal("order-1042", form.["payment_intent_data[metadata][order_id]"])
    Assert.Equal("jenny@example.com", form.["customer_email"])
    Assert.Equal("https://shop.example.com/thanks?session_id={CHECKOUT_SESSION_ID}", form.["success_url"])
    Assert.DoesNotContain(form.Keys, fun key -> key.StartsWith "allowed_payment_method_types")

[<Fact>]
let ``with Stripe Tax the prices include VAT`` () =
    match checkoutSessionOptions { shop with AutomaticTax = true } catalog "order-1042" None [ { Sku = "MUG-350"; Quantity = 1 } ] with
    | Ok options ->
        let form = formOf options
        Assert.Equal("true", form.["automatic_tax[enabled]"])
        Assert.Equal("inclusive", form.["line_items[0][price_data][tax_behavior]"])
        Assert.Equal("inclusive", form.["shipping_options[0][shipping_rate_data][tax_behavior]"])
    | Error reason -> Assert.Fail reason

[<Fact>]
let ``cartFingerprint changes with the cart but not with the order of its lines`` () =
    let cart = [ { Sku = "MUG-350"; Quantity = 2 }; { Sku = "MUG-500"; Quantity = 1 } ]
    Assert.Equal(cartFingerprint cart, cartFingerprint (List.rev cart))
    Assert.NotEqual<string>(cartFingerprint cart, cartFingerprint [ { Sku = "MUG-350"; Quantity = 3 }; { Sku = "MUG-500"; Quantity = 1 } ])
    Assert.Equal(16, (cartFingerprint cart).Length)

[<Fact>]
let ``a cart that cannot be sold is rejected before Stripe is called`` () =
    let reasonFor cart =
        match checkoutSessionOptions shop catalog "order-1" None cart with
        | Error reason -> reason
        | Ok _ -> "accepted"
    Assert.Equal("The cart is empty", reasonFor [])
    Assert.Equal("Unknown products: TEAPOT", reasonFor [ { Sku = "TEAPOT"; Quantity = 1 } ])
    Assert.Equal("Invalid quantity", reasonFor [ { Sku = "MUG-350"; Quantity = 0 } ])

// =============================================================================
// Fulfilment
// =============================================================================

let session () = fixture "checkout-session-paid" |> Util.deserialise<CheckoutSession>
let lineItems () = (fixture "checkout-line-items" |> Util.deserialise<StripeList<Item>>).Data

[<Fact>]
let ``orderOf gives the warehouse the lines and the shipping address of a paid session`` () =
    match orderOf (session ()) (lineItems ()) with
    | Some order ->
        Assert.Equal("order-1042", order.OrderId)
        Assert.Equal(Some "pi_1Test", order.PaymentIntentId)
        Assert.Equal(Some "+358401234567", order.Phone)
        Assert.Equal(3480, order.AmountTotal)
        let line = Assert.Single order.Lines
        Assert.Equal(Some "MUG-350", line.Sku)
        Assert.Equal(2, line.Quantity)
        match order.ShipTo with
        | Some address ->
            Assert.Equal("Jenny Rosen", address.Name)
            Assert.Equal("Helsinki", address.City)
            Assert.Equal(Some IsoCountryCode.FI, address.Country)
        | None -> Assert.Fail "Expected a shipping address"
    | None -> Assert.Fail "Expected an order"

[<Fact>]
let ``an unpaid session is not fulfilled`` () =
    Assert.Equal(None, orderOf { session () with PaymentStatus = CheckoutSessionPaymentStatus.Unpaid } (lineItems ()))

let respond =
    respondWithRoutes [
        "/v1/checkout/sessions/cs_test_1Order", fixture "checkout-session-paid"
        "/v1/checkout/sessions/cs_test_1Order/line_items", fixture "checkout-line-items"
    ]

[<Fact>]
let ``orderToFulfil reads the session and its line items with their products`` () =
    async {
        use stripe = new FakeStripe(respond)
        match! orderToFulfil stripe.Settings "cs_test_1Order" with
        | Ok (Some order) -> Assert.Equal("order-1042", order.OrderId)
        | other -> Assert.Fail($"Expected an order, got {other}")
        match stripe.Requests with
        | [ _; items ] -> Assert.Equal("data.price.product", items.Query.["expand[]"])
        | requests -> Assert.Fail($"Expected 2 requests, got {requests.Length}")
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``refundOrder refunds the payment intent of the order`` () =
    async {
        let refundJson = """{ "id": "re_1Mug", "object": "refund", "amount": 1490, "charge": "ch_1Test", "created": 1790000000, "currency": "eur", "metadata": {}, "payment_intent": "pi_1Test", "reason": "requested_by_customer", "status": "succeeded" }"""
        use stripe = new FakeStripe(respondWithRoutes [ "/v1/refunds", refundJson ])
        let! refund = refundOrder stripe.Settings (key "refund-1") "pi_1Test" (Some 1490)
        Assert.True(Result.isOk refund)
        let request = Assert.Single stripe.Requests
        Assert.Equal("pi_1Test", request.Form.["payment_intent"])
        Assert.Equal("1490", request.Form.["amount"])
        Assert.Equal("requested_by_customer", request.Form.["reason"])
    } |> Async.StartImmediateAsTask :> Task

// =============================================================================
// Webhooks
// =============================================================================

let secret = "whsec_test_webshop"

let deliver (settings: RestApi.StripeApiSettings) (eventType: string) (objectJson: string) =
    let payload = eventPayload "evt_test_webshop" eventType objectJson
    let signature = signatureHeader secret payload (DateTimeOffset.UtcNow.ToUnixTimeSeconds())
    WebshopCheckout.handleWebhookRequest settings { EndpointSecret = secret } signature payload

let assertProcessed (expected: string) (result: WebhookResult, statusCode: int) =
    Assert.Equal(200, statusCode)
    match result with
    | Processed message -> Assert.Contains(expected, message)
    | other -> Assert.Fail($"Expected Processed, got {other}")

[<Fact>]
let ``checkout.session.completed with a paid session ships the order`` () =
    async {
        use stripe = new FakeStripe(respond)
        let! response = deliver stripe.Settings "checkout.session.completed" (fixture "checkout-session-paid")
        assertProcessed "Order order-1042 paid: ship 2 items" response
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``checkout.session.completed with a delayed payment waits for it`` () =
    async {
        use stripe = new FakeStripe(respondError)
        let unpaid = (fixture "checkout-session-paid").Replace("\"payment_status\": \"paid\"", "\"payment_status\": \"unpaid\"")
        let! response = deliver stripe.Settings "checkout.session.completed" unpaid
        assertProcessed "waiting for the payment" response
        Assert.Empty(stripe.Requests)
    } |> Async.StartImmediateAsTask :> Task

[<Fact>]
let ``checkout.session.expired releases the stock`` () =
    async {
        use stripe = new FakeStripe(respondError)
        let! response = deliver stripe.Settings "checkout.session.expired" (fixture "checkout-session-paid")
        assertProcessed "release its stock" response
    } |> Async.StartImmediateAsTask :> Task
