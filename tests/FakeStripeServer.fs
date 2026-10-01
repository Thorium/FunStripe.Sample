module FakeStripeServer

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open FunStripe

// A local stand-in for api.stripe.com. FunStripe's settings take a base URL, so the
// service code runs unchanged against it: the real request encoding and the real
// response deserialisation are exercised without a Stripe account.

/// A request FunStripe sent to the fake server
type RecordedRequest = {
    Method: string
    Path: string
    /// The decoded form parameters of the request body
    Form: Map<string, string>
    StripeVersion: string option
    IdempotencyKey: string option
    /// True when the idempotency key was seen before and the first response was sent again
    Replayed: bool
}

let customerJson = """{
  "id": "cus_FakeCustomer",
  "object": "customer",
  "created": 1790000000,
  "email": "alice@example.com",
  "livemode": false,
  "metadata": {},
  "name": "Alice Smith"
}"""

let setupIntentJson = """{
  "id": "seti_FakeSetup",
  "object": "setup_intent",
  "allowed_payment_method_types": ["card"],
  "client_secret": "seti_FakeSetup_secret_fake",
  "created": 1790000000,
  "customer": "cus_FakeCustomer",
  "livemode": false,
  "metadata": {},
  "payment_method": "pm_FakeCard",
  "payment_method_types": ["card"],
  "status": "requires_payment_method",
  "usage": "off_session"
}"""

let paymentIntentJson = """{
  "id": "pi_FakePayment",
  "object": "payment_intent",
  "allowed_payment_method_types": ["card"],
  "amount": 5000,
  "amount_capturable": 0,
  "amount_received": 0,
  "capture_method": "automatic_async",
  "client_secret": "pi_FakePayment_secret_fake",
  "confirmation_method": "automatic",
  "created": 1790000000,
  "currency": "usd",
  "customer": "cus_FakeCustomer",
  "livemode": false,
  "metadata": {},
  "payment_method_types": ["card"],
  "status": "requires_payment_method"
}"""

let errorJson = """{
  "error": {
    "code": "email_invalid",
    "doc_url": "https://docs.stripe.com/error-codes/email-invalid",
    "message": "Invalid email address: not-an-email",
    "param": "email",
    "type": "invalid_request_error"
  }
}"""

/// Answers like Stripe does for the three endpoints the sample calls
let respondOk (request: RecordedRequest) =
    match request.Path with
    | "/v1/customers" -> 200, customerJson
    | "/v1/setup_intents" -> 200, setupIntentJson
    | "/v1/payment_intents" -> 200, paymentIntentJson
    | _ -> 404, errorJson

/// Rejects every request the way Stripe rejects invalid parameters
let respondError (_: RecordedRequest) = 400, errorJson

let private freePort () =
    use probe = new TcpListener(IPAddress.Loopback, 0)
    probe.Start()
    let port = (probe.LocalEndpoint :?> IPEndPoint).Port
    probe.Stop()
    port

let private parseForm (body: string) =
    body.Split([| '&' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun pair ->
        match pair.Split([| '=' |], 2) with
        | [| key; value |] -> WebUtility.UrlDecode key, WebUtility.UrlDecode value
        | parts -> WebUtility.UrlDecode parts.[0], "")
    |> Map.ofArray

type FakeStripe(respond: RecordedRequest -> int * string) =
    let baseUrl = $"http://localhost:{freePort ()}"
    let listener = new HttpListener()
    let requests = ConcurrentQueue<RecordedRequest>()
    // Like Stripe: the response to the first request with an idempotency key is kept
    // and sent again for every later request with that key
    let responsesByKey = ConcurrentDictionary<string, int * string>()
    let mutable stopping = false

    let serve =
        async {
            while listener.IsListening do
                let! context = listener.GetContextAsync() |> Async.AwaitTask
                use reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)
                let idempotencyKey = context.Request.Headers.["Idempotency-Key"] |> Option.ofObj
                let request = {
                    Method = context.Request.HttpMethod
                    Path = context.Request.Url.AbsolutePath
                    Form = parseForm (reader.ReadToEnd())
                    StripeVersion = context.Request.Headers.["Stripe-Version"] |> Option.ofObj
                    IdempotencyKey = idempotencyKey
                    Replayed = idempotencyKey |> Option.exists responsesByKey.ContainsKey
                }
                requests.Enqueue request
                let statusCode, body =
                    match idempotencyKey with
                    | Some key -> responsesByKey.GetOrAdd(key, fun _ -> respond request)
                    | None -> respond request
                let bytes = Encoding.UTF8.GetBytes body
                context.Response.StatusCode <- statusCode
                context.Response.ContentType <- "application/json"
                context.Response.OutputStream.Write(bytes, 0, bytes.Length)
                context.Response.Close()
        }

    do
        listener.Prefixes.Add $"{baseUrl}/"
        listener.Start()
        // Closing the listener makes the pending GetContextAsync fail, with an exception type
        // that depends on the platform and the timing. That is the expected end of the loop;
        // any exception while the server is still in use is a real one and is not caught.
        Async.Start (async {
            try do! serve
            with _ when stopping -> () })

    /// FunStripe settings that send the requests to this server
    member _.Settings =
        RestApi.StripeApiSettings.New(apiKey = "sk_test_fake", baseUrl = baseUrl, stripeVersion = Config.DefaultStripeApiVersion)

    /// The requests received so far, oldest first
    member _.Requests = requests |> Seq.toList

    interface IDisposable with
        member _.Dispose() =
            stopping <- true
            listener.Close()
