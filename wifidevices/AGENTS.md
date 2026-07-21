# Finance refresh

When asked to refresh account values:

1. Read every account, login URL, ID, and credential from `data/finance/accounts.json`; never print credentials. Sign in and continue until all configured accounts are refreshed today.
2. Wait and recheck slow/blank pages (especially UFCU). Clear and fill the exact username and password fields separately, verify the username and that the password field is populated, then submit. Retry apparent page/input failures.
3. Record: UFCU checking available balance (ignore savings); Amazon Store Card current balance/available spend; Amazon Visa 7321 at Chase current balance/available credit; Best Buy Visa 0112 at Citi current balance/available credit/limit; RBC USA checking available balance and card 7651 current balance/available credit; then use **Go to Canadian Accounts** for RBC CAD checking, Mastercard 4484, and credit line values/limits. Keep displayed CAD amounts unconverted.
4. Immediately POST each result to `http://127.0.0.1:5136/api/finance/accounts/{id}/values`, then GET `/api/finance/state` and verify all accounts have today's values.
5. Do not stop after partial success. Stop only for CAPTCHA, MFA, or truly user-only input; leave that exact tab open and state what the user must do.

If there are transient failures then simply retry until succesful. the only things you should pause trying for is MFA.

Do not use Chat GPT computer use for this task. You must use the default system microsoft edge browser through the chatgpt browser plugin is installed there. Do not use codex's in app browser.