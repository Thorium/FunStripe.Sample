// Linking bank accounts with Financial Connections
//
// collectFinancialConnectionsAccounts opens Stripe's dialog, where the customer picks their
// bank, logs in and chooses the accounts to share. It resolves with the session and the
// accounts linked in it; the account data itself is read by your backend.

const result = document.getElementById('result');

document.getElementById('link-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    const submitButton = document.getElementById('link-submit');
    submitButton.disabled = true;

    try {
        const { publishableKey, clientSecret } = readCredentials('fcsess_');
        const stripe = Stripe(publishableKey);
        const { financialConnectionsSession, error } = await stripe.collectFinancialConnectionsAccounts({ clientSecret });

        if (error) {
            showResult(result, `Linking failed: ${error.message}`, false);
        } else if (financialConnectionsSession.accounts.length === 0) {
            // The customer closed the dialog without linking an account
            showResult(result, 'No accounts linked.', false);
        } else {
            const accounts = financialConnectionsSession.accounts
                .map((account) => `${account.institution_name} ****${account.last4 ?? '????'} (${account.id})`)
                .join(', ');
            showResult(result, `Linked: ${accounts}. Now run: dotnet fsi examples/FinancialConnections.fsx -- ${financialConnectionsSession.id}`, true);
        }
    } catch (err) {
        showResult(result, `Error: ${err.message}`, false);
    } finally {
        submitButton.disabled = false;
    }
});
