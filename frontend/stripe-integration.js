// FunStripeLite Frontend Integration Sample
// This demonstrates how to integrate Stripe Elements with FunStripeLite backend

// Configuration - replace with your actual publishable key
const STRIPE_PUBLISHABLE_KEY = 'pk_test_...'; // Replace with your test publishable key

// Initialize Stripe
const stripe = Stripe(STRIPE_PUBLISHABLE_KEY);

// Common Stripe Elements styling
const elementStyles = {
    base: {
        fontSize: '16px',
        color: '#424770',
        '::placeholder': {
            color: '#aab7c4',
        },
        fontFamily: '"Open Sans", Arial, Helvetica, sans-serif',
    },
    invalid: {
        color: '#9e2146',
    },
};

const elementOptions = {
    style: elementStyles,
    hidePostalCode: false
};

// Create Stripe Elements
const elements = stripe.elements();
const setupCardElement = elements.create('card', elementOptions);
const paymentCardElement = elements.create('card', elementOptions);

// Mount elements
setupCardElement.mount('#setup-card-element');
paymentCardElement.mount('#payment-card-element');

// Handle element state changes
function handleElementChange(element, errorElementId, submitButtonId) {
    element.on('change', ({error}) => {
        const displayError = document.getElementById(errorElementId);
        const submitButton = document.getElementById(submitButtonId);
        
        if (error) {
            displayError.textContent = error.message;
            submitButton.disabled = true;
        } else {
            displayError.textContent = '';
            submitButton.disabled = false;
        }
    });
}

handleElementChange(setupCardElement, 'setup-card-errors', 'setup-submit');
handleElementChange(paymentCardElement, 'payment-card-errors', 'payment-submit');

// Setup Intent Form Handling
document.getElementById('setup-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    
    const submitButton = document.getElementById('setup-submit');
    const resultElement = document.getElementById('setup-result');
    
    submitButton.disabled = true;
    submitButton.textContent = 'Processing...';
    
    try {
        // In a real application, you would:
        // 1. Call your F# backend to create a setup intent
        // 2. Get the client_secret from your backend
        // 3. Use that client_secret here
        
        const clientSecret = await getSetupIntentClientSecret();
        
        if (!clientSecret) {
            throw new Error('Could not get setup intent from backend');
        }
        
        const {error, setupIntent} = await stripe.confirmCardSetup(clientSecret, {
            payment_method: {
                card: setupCardElement,
                billing_details: {
                    name: 'Customer Name'
                }
            }
        });
        
        if (error) {
            showResult(resultElement, `Setup failed: ${error.message}`, false);
        } else {
            showResult(resultElement, 
                `Payment method saved successfully! Setup Intent: ${setupIntent.id}`, 
                true);
            
            // In a real app, you might:
            // - Redirect to a success page
            // - Update the UI to show saved payment methods
            // - Enable subscription options
        }
    } catch (err) {
        showResult(resultElement, `Error: ${err.message}`, false);
    } finally {
        submitButton.disabled = false;
        submitButton.textContent = 'Save Payment Method';
    }
});

// Payment Intent Form Handling
document.getElementById('payment-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    
    const submitButton = document.getElementById('payment-submit');
    const resultElement = document.getElementById('payment-result');
    const amountInput = document.getElementById('amount');
    
    submitButton.disabled = true;
    submitButton.textContent = 'Processing...';
    
    try {
        const amount = parseFloat(amountInput.value);
        if (amount < 0.50) {
            throw new Error('Amount must be at least $0.50');
        }
        
        // In a real application, you would:
        // 1. Call your F# backend to create a payment intent
        // 2. Pass the amount and any other required data
        // 3. Get the client_secret from your backend
        
        const clientSecret = await getPaymentIntentClientSecret(amount);
        
        if (!clientSecret) {
            throw new Error('Could not get payment intent from backend');
        }
        
        const {error, paymentIntent} = await stripe.confirmCardPayment(clientSecret, {
            payment_method: {
                card: paymentCardElement,
                billing_details: {
                    name: 'Customer Name'
                }
            }
        });
        
        if (error) {
            showResult(resultElement, `Payment failed: ${error.message}`, false);
        } else {
            showResult(resultElement, 
                `Payment successful! Payment Intent: ${paymentIntent.id}`, 
                true);
            
            // In a real app, you might:
            // - Redirect to a success page
            // - Show order confirmation
            // - Send confirmation email
        }
    } catch (err) {
        showResult(resultElement, `Error: ${err.message}`, false);
    } finally {
        submitButton.disabled = false;
        submitButton.textContent = 'Pay Now';
    }
});

// Helper function to display results
function showResult(element, message, isSuccess) {
    element.textContent = message;
    element.className = `result ${isSuccess ? 'success' : 'error'}`;
    element.classList.remove('hidden');
}

// Mock backend calls - replace these with actual calls to your F# backend
async function getSetupIntentClientSecret() {
    // This should call your F# backend endpoint that:
    // 1. Creates a customer (if needed)
    // 2. Creates a setup intent using StripeService.createSetupIntent
    // 3. Returns the client_secret
    
    console.log('Mock: Would call backend to create setup intent');
    
    // For demo purposes, return null to show error handling
    // In a real app, this would be something like:
    // const response = await fetch('/api/create-setup-intent', { method: 'POST' });
    // const { client_secret } = await response.json();
    // return client_secret;
    
    return null; // This will trigger the error handling
}

async function getPaymentIntentClientSecret(amount) {
    // This should call your F# backend endpoint that:
    // 1. Creates a payment intent using StripeService.createPaymentIntent
    // 2. Returns the client_secret
    
    console.log(`Mock: Would call backend to create payment intent for $${amount}`);
    
    // For demo purposes, return null to show error handling
    // In a real app, this would be something like:
    // const response = await fetch('/api/create-payment-intent', {
    //     method: 'POST',
    //     headers: { 'Content-Type': 'application/json' },
    //     body: JSON.stringify({ amount: Math.round(amount * 100) })
    // });
    // const { client_secret } = await response.json();
    // return client_secret;
    
    return null; // This will trigger the error handling
}

// Check if publishable key is configured
if (STRIPE_PUBLISHABLE_KEY === 'pk_test_...') {
    document.body.innerHTML = `
        <div class="container">
            <div class="error-message">
                <h2>Configuration Required</h2>
                <p>Please replace the STRIPE_PUBLISHABLE_KEY in stripe-integration.js with your actual Stripe publishable key.</p>
                <p>You can find your keys at: <a href="https://dashboard.stripe.com/test/apikeys" target="_blank">Stripe Dashboard</a></p>
            </div>
        </div>
    `;
}