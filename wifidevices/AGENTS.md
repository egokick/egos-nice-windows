# Finance refresh

When asked to refresh account values:

1. Read every account, login URL, ID, and credential from `wifidevices/data/finance/accounts.json`; never print credentials. Sign in and continue until all configured accounts are refreshed today.
2. Wait and recheck slow/blank pages (especially UFCU). Clear and fill the exact username and password fields separately, verify the username and that the password field is populated, then submit. Retry apparent page/input failures.
3. Record: UFCU checking available balance (ignore savings); Amazon Store Card current balance/available spend; Amazon Visa 7321 at Chase current balance/available credit; Best Buy Visa 0112 at Citi current balance/available credit/limit; RBC USA checking available balance and card 7651 current balance/available credit; then use **Go to Canadian Accounts** for RBC CAD checking, Mastercard 4484, and credit line values/limits. Keep displayed CAD amounts unconverted.
4. Collect income from **UFCU only**. First GET `http://127.0.0.1:5137/api/finance/state`, find the entry in `income.tracking` whose `accountId` is the UFCU account ID, and use its `lookbackStartOn` through today as the transaction range. With no stored UFCU income this start date is 24 months ago; after at least one UFCU income record is stored it is 30 days ago. Inspect the complete range, including any changed transactions, and do not search other accounts for income yet.
5. Save each genuine positive UFCU income deposit with POST `http://127.0.0.1:5137/api/finance/income` as JSON: `accountId`, `postedOn` (`YYYY-MM-DD`), positive `amount`, `currency` (`USD`), `kind` (`salary` for regular payroll, `bonus` for bonus pay, otherwise `other`), `description`, and `sourceTransactionId` when the site exposes one. Do not record transfers between the user's accounts, cash deposits, credit-card refunds, reversals, or non-income credits. Re-submit an existing transaction if it changed; the transaction ID makes this an update rather than a duplicate.
6. Immediately POST each account balance result to `http://127.0.0.1:5137/api/finance/accounts/{id}/values`, then GET `http://127.0.0.1:5137/api/finance/state` and verify all accounts have today's values and the UFCU income records/salary summary reflect the deposits found.
7. Do not stop after partial success. Stop only for CAPTCHA, MFA, or truly user-only input; leave that exact tab open and state what the user must do.

If there are transient failures then simply retry until succesful. the only things you should pause trying for is MFA.

Do not use Chat GPT computer use for this task. You must use the default system microsoft edge browser through the chatgpt browser plugin is installed there. Do not use codex's in app browser.
