// Webshop checkout: an order of mugs paid on Stripe's hosted payment page. Two steps, because
// paying needs a browser:
//
//   dotnet fsi examples/WebshopCheckout.fsx
//       creates a Checkout Session for a cart and prints its URL. Open it, pay with the test
//       card 4242 4242 4242 4242 (any future date and CVC) and a Finnish address.
//
//   dotnet fsi examples/WebshopCheckout.fsx -- cs_test_...
//       reads the paid order as the warehouse gets it, and refunds one mug
//
// The code is in src/WebshopCheckout.fs. Needs STRIPE_TEST_API_KEY (see Common.fsx).

#load "Common.fsx"
#load "../src/WebshopCheckout.fs"

open System
open FunStripe.IsoTypes
open StripeRequest.Checkout
open Common
open StripeService
open WebshopCheckout

// Your catalog, normally from your database
let catalog =
    [
        { Sku = "MUG-350"; Name = "Ceramic mug"; Description = Some "350 ml, dishwasher safe"; Images = []; UnitAmount = 1490 }
        { Sku = "MUG-500"; Name = "Large mug"; Description = Some "500 ml"; Images = []; UnitAmount = 1890 }
    ]
    |> List.map (fun product -> product.Sku, product)
    |> Map.ofList

let shop = {
    Currency = IsoCurrencyCode.EUR
    ShipsTo = [ CheckoutSessions.Create'ShippingAddressCollectionAllowedCountries.FI; CheckoutSessions.Create'ShippingAddressCollectionAllowedCountries.SE ]
    ShippingOptions = [
        { Name = "Posti parcel"; Amount = 590; MinBusinessDays = 2; MaxBusinessDays = 4 }
        { Name = "Express"; Amount = 1290; MinBusinessDays = 1; MaxBusinessDays = 1 }
    ]
    // Turn on once Stripe Tax is set up in the Dashboard
    AutomaticTax = false
    SuccessUrl = "https://example.com/thanks?session_id={CHECKOUT_SESSION_ID}"
    CancelUrl = "https://example.com/cart"
}

match scriptArgs with
| [] ->
    let orderId = $"order-{DateTime.UtcNow:yyyyMMddHHmmss}"
    let operation = newOperation orderId
    // What the browser sent: SKUs and quantities only
    let cart = [ { Sku = "MUG-350"; Quantity = 2 }; { Sku = "MUG-500"; Quantity = 1 } ]
    printf "Creating the Checkout Session of %s... " orderId
    match createCheckoutSession settings (idempotencyKeyFor operation $"checkout-{cartFingerprint cart}") shop catalog orderId (Some "jenny.rosen+webshop@example.com") cart |> Async.RunSynchronously with
    | Ok session ->
        printfn "OK"
        printfn ""
        printfn "Pay the order on Stripe's page:"
        printfn $"  {orNone session.Url}"
        printfn ""
        printfn "It returns to example.com, which is fine for a test. Then read the order with:"
        printfn $"  dotnet fsi examples/WebshopCheckout.fsx -- {session.Id}"
    | Error reason ->
        printfn "FAILED"
        eprintfn $"{reason}"
        exit 1

| [ sessionId ] ->
    match orderToFulfil settings sessionId |> step $"Reading the order of {sessionId}" with
    | None ->
        printfn "  Not paid yet: pay on the page first (or wait for a delayed payment method)"
    | Some order ->
        printfn $"  Order {order.OrderId}, {order.AmountTotal} {order.Currency} paid by {orNone order.CustomerEmail}"
        for line in order.Lines do
            printfn $"    {line.Quantity} x {line.Description} ({orNone line.Sku}): {line.AmountTotal}"
        match order.ShipTo with
        | Some address -> printfn $"  Ship to {address.Name}, {address.Line1}, {address.PostalCode} {address.City}, {address.Country}"
        | None -> ()

        // One mug comes back
        match order.PaymentIntentId with
        | Some paymentIntentId ->
            let refund =
                refundOrder settings (idempotencyKeyFor (newOperation order.OrderId) "refund-1") paymentIntentId (Some catalog.["MUG-350"].UnitAmount)
                |> step "Refunding one returned mug"
            printfn $"  Refund {refund.Id}: {refund.Amount} {refund.Currency}, {refund.Status}"
        | None -> ()

| _ ->
    eprintfn "Usage: dotnet fsi examples/WebshopCheckout.fsx [-- <Checkout Session ID>]"
    exit 1
