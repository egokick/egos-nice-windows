# Finance refresh

When asked to refresh account values:

Complete the entire finance refresh. Partial completion is failure.

## Completion contract

1. GET `http://127.0.0.1:5137/api/finance/state` and maintain a checklist containing every configured account ID.
2. Complete account balances first, UFCU income second, and final verification last.
3. Do not finalize the browser or send a final response while checklist items remain. Work taking time or an individual tool call timing out is not a completion condition; continue from saved progress.
4. CAPTCHA, MFA, or truly user-only input are the only allowed blockers. If one occurs, leave that exact tab open, complete every other unblocked account, and then state exactly what the user must do.

## Phase 1: Account balances

1. Read every account, login URL, ID, and credential from `wifidevices/data/finance/accounts.json`. Never expose credentials in tool-call source, arguments, output, or summaries. Load credentials from the local file inside the execution environment and use variables when filling login fields.
2. Sign in and continue until all configured accounts are refreshed today. Wait and recheck slow or blank pages, especially UFCU. Clear and fill the exact username and password fields separately, verify the username and that the password field is populated, and then submit.
3. Record:
   - UFCU checking available balance; ignore savings.
   - Amazon Store Card current balance and available spend.
   - Amazon Visa 7321 at Chase current balance and available credit.
   - Best Buy Visa 0112 at Citi current balance, available credit, and limit.
   - RBC USA checking available balance and card 7651 current balance and available credit.
   - Use **Go to Canadian Accounts** for RBC CAD checking, Mastercard 4484, and credit line values and limits. Keep displayed CAD amounts unconverted.
4. Immediately POST each account result to `http://127.0.0.1:5137/api/finance/accounts/{id}/values` and mark that account complete only after the POST succeeds.
5. Treat apparent page, input, navigation, and tool failures as transient. Retry them. After two consecutive failures for one account, move it to the end of the queue, continue the remaining accounts, and revisit it in another pass. Do not abandon the remaining accounts because one site is difficult.

## Phase 2: UFCU income

Begin this phase only after completing the balance pass for every account that is not waiting on user-only input.

1. From the finance state, find the entry in `income.tracking` whose `accountId` is the UFCU account ID and use its `lookbackStartOn` through today as the transaction range. With no stored UFCU income this start date is 24 months ago; after at least one UFCU income record is stored it is 30 days ago.
2. UFCU can take more than a minute to render an authenticated page. After each major UFCU navigation, wait at least 60 seconds before treating a blank or incomplete page as a failure. If it is still loading, wait another 60 seconds and inspect it again. Do not close or replace a working authenticated tab merely because a shorter tool call timed out.
3. On the UFCU checking transaction page, use **Filter** rather than relying on search or the initially rendered rows. Set Start Date to `lookbackStartOn`, End Date to today, and Incoming or Outgoing to **Incoming**, then apply the filter.
4. Repeatedly select **Load More** until it no longer appears. Confirm the displayed result count agrees with the number of loaded incoming transactions before deciding the history is complete. Search only filters already loaded rows and is not proof that the full range was reviewed.
5. Inspect the complete range, including changed transactions. Collect income from **UFCU only**; do not search other accounts for income. Liberty Mutual ACH transactions whose details identify `TYPE: PAYROLL` and Entry Class Code `PPD` are salary deposits.
6. Open every possible income transaction and treat the transaction detail panel as authoritative for its posted date, amount, description, and source transaction ID. Never infer a transaction date or amount from a biweekly schedule, neighboring rows, or an expected count.
7. As a completeness check, biweekly payroll normally produces about 26 or 27 deposits in a full 12-month period and about 52 or 53 in 24 months. A substantially smaller result means loading, filtering, pagination, or date coverage must be checked again; it is not permission to manufacture transactions.
8. Read the existing UFCU income records before posting. Match by source transaction ID when available; otherwise match the exact account, posted date, amount, and normalized description. Do not add a duplicate to make the count look correct. If an existing record conflicts with UFCU, use the supported update path or report the unresolved discrepancy instead of adding another version.
9. Save each genuine positive UFCU income deposit with POST `http://127.0.0.1:5137/api/finance/income` as JSON: `accountId`, `postedOn` (`YYYY-MM-DD`), positive `amount`, `currency` (`USD`), `kind` (`salary` for regular payroll, `bonus` for bonus pay, otherwise `other`), `description`, and `sourceTransactionId` when the site exposes one.
10. Do not record transfers between the user's accounts, cash deposits, credit-card refunds, reversals, merchant adjustments, reimbursements, or non-income credits. Re-submit an existing transaction only when it changed and a stable transaction ID makes the operation an update rather than a duplicate.

## Phase 3: Verification

1. GET `http://127.0.0.1:5137/api/finance/state`.
2. Verify every configured account has today's values and that every account POST succeeded during this run.
3. Verify the UFCU income records and salary summary exactly reconcile with the qualifying deposits found: compare the count, posted dates, amounts, latest payment, and applicable 12-month total. Also confirm the complete required range was reviewed. Report how many income records were inserted, updated, skipped as duplicates, and excluded as non-income.
4. If anything is incomplete and is not waiting on CAPTCHA, MFA, or truly user-only input, return to the appropriate phase and continue. Report completion only after this verification passes.

Use the default system Microsoft Edge browser through the installed ChatGPT browser plugin. Do not use ChatGPT Computer Use or Codex's in-app browser.
