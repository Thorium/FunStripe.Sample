// Shared by direct-debit.html and financial-connections.html.
//
// These pages need no backend: run the matching script in examples/, which creates the
// intent or session with FunStripe and prints its client secret, and paste the secret here.
// A real frontend gets the client secret from your backend instead.

// The publishable key is remembered in this browser only, as a convenience
const KEY_STORAGE = 'funstripe-sample-publishable-key';

function rememberedKey() {
    try {
        return localStorage.getItem(KEY_STORAGE) || '';
    } catch {
        return '';
    }
}

function rememberKey(key) {
    try {
        localStorage.setItem(KEY_STORAGE, key);
    } catch {
        // Storage can be unavailable (private windows); the page works without it
    }
}

// Reads and checks the publishable key and client secret fields
function readCredentials(secretPrefix) {
    const publishableKey = document.getElementById('publishable-key').value.trim();
    const clientSecret = document.getElementById('client-secret').value.trim();
    if (!publishableKey.startsWith('pk_test_')) {
        throw new Error('Enter a test publishable key (pk_test_...) from https://dashboard.stripe.com/test/apikeys');
    }
    if (!clientSecret.startsWith(secretPrefix)) {
        throw new Error(`Enter the client secret (${secretPrefix}...) the example script printed`);
    }
    rememberKey(publishableKey);
    return { publishableKey, clientSecret };
}

function showResult(element, message, isSuccess) {
    element.textContent = message;
    element.className = `result ${isSuccess ? 'success' : 'error'}`;
}

document.getElementById('publishable-key').value = rememberedKey();
