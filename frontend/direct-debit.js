// Direct debit setup with the Payment Element
//
// The Payment Element renders the form of the payment method the setup intent allows (IBAN
// for SEPA, sort code for Bacs, the bank login of Financial Connections for ACH, ...) and
// the mandate text the customer has to accept. confirmSetup sends both to Stripe.

let stripe;
let elements;

const result = document.getElementById('result');

document.getElementById('load-form').addEventListener('submit', (event) => {
    event.preventDefault();
    try {
        const { publishableKey, clientSecret } = readCredentials('seti_');
        stripe = Stripe(publishableKey);
        elements = stripe.elements({ clientSecret });
        const paymentElement = elements.create('payment');
        paymentElement.mount('#payment-element');
        document.getElementById('setup-section').classList.remove('hidden');
        result.className = 'result hidden';
    } catch (err) {
        showResult(result, err.message, false);
    }
});

document.getElementById('setup-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    const submitButton = document.getElementById('setup-submit');
    submitButton.disabled = true;

    try {
        const { error, setupIntent } = await stripe.confirmSetup({
            elements,
            confirmParams: {
                // Where Stripe returns the customer after a redirect, if the bank needs one
                return_url: window.location.href,
            },
            // Bank debits rarely redirect; stay on the page when no redirect is needed
            redirect: 'if_required',
        });

        if (error) {
            showResult(result, `Setup failed: ${error.message}`, false);
        } else if (setupIntent.status === 'succeeded') {
            showResult(result, `Bank account saved: ${setupIntent.payment_method}. Your backend gets setup_intent.succeeded.`, true);
        } else {
            // ACH with microdeposits: the customer verifies the amounts later
            showResult(result, `Setup intent ${setupIntent.id} is ${setupIntent.status}.`, true);
        }
    } catch (err) {
        showResult(result, `Error: ${err.message}`, false);
    } finally {
        submitButton.disabled = false;
    }
});
