# Finance

Standalone local dashboard for account balances, debt, credit, refresh history, and payoff-interest previews.

## Run

Run `start.bat`, then open `http://127.0.0.1:5137`.

The app deliberately continues to use the existing finance configuration and history under `../wifidevices/data/finance` and `../wifidevices/.env.finance`, so splitting the app does not discard account values or refresh history.

## Income ledger

Income is stored separately in `../wifidevices/data/finance/income.json`. It is a versioned ledger rather than an account field so a deposit remains connected to the account that received it and additional accounts can be added later without a data migration.

Each record contains the receiving `accountId`, bank `postedOn` date, positive `amount`, ISO currency, `kind` (`salary`, `bonus`, or `other`), optional description, and the bank's transaction ID when available. The transaction ID is used to update an existing record during the 30-day refresh window; transactions without one use a normalized account/date/amount/kind/description fingerprint to avoid duplicate imports.

The dashboard shows the latest salary payment and salary deposits over the last 12 months per account and currency. These are deposited-income totals, not a gross annual-salary estimate.

The refresh agent currently collects income from UFCU only. Before any UFCU income is stored it inspects the prior 24 months; thereafter it uses a 30-day window, which also lets it reconcile recent bank-side changes.
