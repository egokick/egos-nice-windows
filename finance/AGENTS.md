# Finance refresh

## Launcher-scoped override

When the Finances app launches Codex with an **Assigned account (authoritative)** block, that session has exactly one assigned account. The launcher prompt is the complete workflow contract for that session. Treat every reference below to every configured account, all accounts, a multi-account checklist, or moving between accounts as legacy general reference that does not apply to a launcher-assigned session. Never browse, collect, update, or verify another account.

For launcher-assigned sessions, the appended **Secure credential lease (authoritative)** block is the only credential source. The Finances app creates the short-lived lease from Windows Credential Manager just before that one account starts, and independently rejects requests for another account ID. Retain the exact new assigned-tab binding and use locators created from that binding, never the active tab. Immediately before filling, verify the retained tab's legitimate institution URL, the locator's visible/enabled state, and `type="password"` for password fields. Redeem one field only inside the same short block-scoped browser-control call and pass its response directly to `locator.fill(...)`. Never persist, print, return, or log a credential. A different foreground tab is irrelevant to targeted locator filling and is not a user-only blocker. These instructions supersede every credential-handling instruction below.

When asked to refresh account values:

Complete the entire finance refresh. Partial completion is failure.

## Browser requirements

1. Before inspecting browser connections, check whether Microsoft Edge is open. If Edge is not open, launch Microsoft Edge first, then retry browser discovery.
2. Use the installed ChatGPT browser extension **directly in Microsoft Edge**. Because Edge is Chromium-based, the browser connection may identify itself as Chrome; this is expected and does not mean Google Chrome should be used. Before doing any finance navigation, inspect the available browser connections and confirm that the selected connection directly exposes Edge and its tabs.
3. An Edge tab displaying `SessionDesktop`, Windows App, AVD, Remote Desktop, or another remote-desktop canvas is not a usable finance-browser connection. Never try to control another browser indirectly through such a tab, canvas, streamed desktop, taskbar, or nested window.
4. Create a brand-new Edge tab with the browser extension's tab API (for example, `browser.tabs.new()`) and use that new tab for the finance refresh. Do not claim, navigate, type into, or repurpose any pre-existing Edge tab.
5. Verify that the newly created tab is a directly controllable Edge tab before entering a finance URL. Browser navigation, DOM inspection, clicks, and typing must target that tab through the browser extension, not through screen-coordinate input sent to a remote desktop.
6. If no direct Edge browser-extension connection is available after opening Edge, stop before interacting with any browser page. Ask the user to enable or reconnect the ChatGPT browser extension in Microsoft Edge; do not substitute Google Chrome, Codex's in-app browser, ChatGPT Computer Use, or indirect remote-desktop control.
7. Reuse the new finance tab throughout the run. Additional new tabs may be created through the same direct Edge connection when sites require separate sessions, but existing user tabs must remain untouched.

## Completion contract

1. GET `http://127.0.0.1:5137/api/finance/state` and maintain a checklist containing every configured account ID.
2. Complete account balances first, UFCU income second, and final verification last.
3. Do not finalize the browser or send a final response while checklist items remain. Work taking time or an individual tool call timing out is not a completion condition; continue from saved progress.
4. CAPTCHA, MFA, or truly user-only input are the only allowed blockers. If one occurs, leave that exact account tab open on the visible actionable blocker, complete every other unblocked account, and then state exactly what the user must do. Keep a handle to every blocked tab. Before browser cleanup or handoff, explicitly include every blocked tab in the browser keep/handoff list. Do not close it, navigate it away, replace it with a generic handoff tab, or end the Edge session in a way that closes it.

## Phase 1: Account balances

1. Read account IDs, login URLs, and other non-secret metadata from `/api/finance/state`. In a launcher-assigned session, use only its appended secure credential lease and exact assigned account ID. Account files and API state are metadata only. Never print, echo, enumerate, serialize, copy, log, return, persist, or include any credential value in tool-call source, arguments, output, notes, or summaries.
2. Sign in and continue until all configured accounts are refreshed today. Wait and recheck slow or blank pages, especially UFCU. Clear and fill the exact username and password fields separately, verify the username and that the password field is populated, and then submit.
3. Record:
   - UFCU checking available balance; ignore savings.
   - Amazon Store Card current balance and available spend.
   - Amazon Visa 7321 at Chase current balance and available credit.
   - Best Buy Visa 0112 at Citi current balance, available credit, and limit.
   - RBC USA checking available balance and card 7651 current balance and available credit.
   - Use **Go to Canadian Accounts** for RBC CAD checking, Mastercard 4484, and credit line values and limits. Keep displayed CAD amounts unconverted.
   - For every credit card and loan, also collect the exact current **minimum payment** in dollars, **payment due date** in `YYYY-MM-DD` format, and whether the current minimum payment has already been met. Inspect the account summary, payment page, and current statement as needed; do not infer these values from the balance, prior month, or scheduled payments alone. If the issuer explicitly says no payment is due, save `minimumPayment` as `0` and `minimumPaymentMet` as `true`, and use the displayed due date when one is provided.
4. Immediately POST each account result to `http://127.0.0.1:5137/api/finance/accounts/{id}/values` and mark that account complete only after the POST succeeds. Every credit-card or loan POST must include `minimumPayment` as a nonnegative JSON number, `paymentDueDate` as `YYYY-MM-DD`, and `minimumPaymentMet` as a JSON boolean. It must also include `collectorNotes`: preserve useful existing notes and add a concise `Payment details:` note naming the exact page, menu, statement, or field where the minimum payment, due date, and paid/outstanding state were found. If a value is genuinely unavailable, explain what was inspected and why it could not be obtained in `collectorNotes` rather than guessing.
5. Treat apparent page, input, navigation, and tool failures as transient. Retry them. After two consecutive failures for one account, move it to the end of the queue, continue the remaining accounts, and revisit it in another pass. Do not abandon the remaining accounts because one site is difficult.

## Phase 2: UFCU income

Begin this phase only after completing the balance pass for every account that is not waiting on user-only input.

1. Read the stored UFCU income records before opening transaction history. If UFCU already has stored income, treat the latest stored `postedOn` date as a hard historical cutoff: collect only transactions posted after that date. Do not reopen, recount, or re-submit older stored deposits. Use `income.tracking.lookbackStartOn` only when UFCU has no stored income and an initial history import is required.
2. UFCU can take more than a minute to render an authenticated page. After each major UFCU navigation, inspect the page immediately and proceed as soon as it shows a stable actionable state, including a login button or form, an MFA or CAPTCHA prompt, an authenticated account page, or an explicit error. Do not use a fixed 60-second sleep when one of those states is already visible. Only while the page is blank, incomplete, or visibly loading, poll and reinspect it for up to 60 seconds before treating the navigation as a failure. If it is still actively loading at that point, allow another 60 seconds and inspect it again. Do not close or replace a working authenticated tab merely because a shorter tool call timed out.
3. On the UFCU checking transaction page, use **Filter** rather than relying on search or the initially rendered rows. For an incremental refresh, set Start Date to the day after the latest stored UFCU `postedOn` date. For an initial import with no stored UFCU income, set Start Date to `income.tracking.lookbackStartOn`. Set End Date to today and Incoming or Outgoing to **Incoming**, then apply the filter. If the start date is after today, there is no UFCU income work to collect.
4. Repeatedly select **Load More** until it no longer appears. Confirm the displayed result count agrees with the number of loaded incoming transactions before deciding the history is complete. Search only filters already loaded rows and is not proof that the full range was reviewed.
5. Inspect the complete new-only range. Collect income from **UFCU only**; do not search other accounts for income. Liberty Mutual ACH transactions whose details identify `TYPE: PAYROLL` and Entry Class Code `PPD` are salary deposits.
6. Open every possible income transaction in the new-only range and treat the transaction detail panel as authoritative for its posted date, amount, description, and source transaction ID. Never infer a transaction date or amount from a biweekly schedule, neighboring rows, or an expected count.
7. The biweekly count check applies only to an initial historical import: about 26 or 27 deposits per full 12 months and about 52 or 53 per 24 months. For an incremental refresh, a small or zero count is normal; reconcile only the dates after the latest stored deposit.
8. Before posting a new UFCU deposit, compare it with stored records by source transaction ID when available; otherwise compare the exact account, posted date, amount, and normalized description. Do not POST any previously stored deposit again. Update an older record only when the site shows that exact transaction changed and a supported stable identifier identifies it; otherwise report the discrepancy.
9. Save each genuine positive UFCU income deposit with POST `http://127.0.0.1:5137/api/finance/income` as JSON: `accountId`, `postedOn` (`YYYY-MM-DD`), positive `amount`, `currency` (`USD`), `kind` (`salary` for regular payroll, `bonus` for bonus pay, otherwise `other`), `description`, and `sourceTransactionId` when the site exposes one.
10. Do not record transfers between the user's accounts, cash deposits, credit-card refunds, reversals, merchant adjustments, reimbursements, or non-income credits. Re-submit an existing transaction only when it changed and a stable transaction ID makes the operation an update rather than a duplicate.

## Phase 3: Verification

1. GET `http://127.0.0.1:5137/api/finance/state`.
2. Verify every configured account has today's values and that every account POST succeeded during this run. For each credit card and loan, also verify the stored minimum payment, due date, paid/outstanding state, and `Payment details:` collector note against the authoritative page or statement inspected during this run.
3. Verify that every qualifying deposit found after the latest pre-run UFCU income date was inserted or updated exactly once, then verify the resulting latest payment and applicable 12-month total. Do not re-verify the entire historical ledger during an incremental refresh. Report the incremental range, how many new transactions were inspected, and how many records were inserted, updated, skipped as duplicates, and excluded as non-income.
4. If anything is incomplete and is not waiting on CAPTCHA, MFA, or truly user-only input, return to the appropriate phase and continue. Report completion only after this verification passes.

## Transaction refresh summary

For every zero-result transaction month or subrange, reconcile each previously stored date with a complete empty snapshot and POST exactly one explicit `{"complete":true,"transactions":[]}` sentinel on the range's final in-scope date, even when it has no prior rows. If that final date was already reconciled, the same POST is the sentinel; never send it twice or send one for every empty day. Verify the sentinel succeeded with `observedCount: 0` before marking the range complete or saving its coverage checkpoint.

Use Microsoft Edge through the installed ChatGPT browser extension, following the Browser requirements above. The connection may appear as Chrome because Edge is Chromium-based. Do not use Google Chrome, ChatGPT Computer Use, or Codex's in-app browser.
