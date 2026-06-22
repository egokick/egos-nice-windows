const financeState = {
  data: null,
  historyRange: "1w"
};

const financeEls = {
  summary: document.querySelector("#financeSummary"),
  alert: document.querySelector("#financeAlert"),
  refresh: document.querySelector("#refreshFinance"),
  netAfterDebt: document.querySelector("#netAfterDebt"),
  totalCash: document.querySelector("#totalCash"),
  totalCredit: document.querySelector("#totalCredit"),
  totalDebt: document.querySelector("#totalDebt"),
  netBand: document.querySelector(".net-band"),
  accountFormTitle: document.querySelector("#accountFormTitle"),
  editAccountSelect: document.querySelector("#editAccountSelect"),
  showAccountForm: document.querySelector("#showAccountForm"),
  accountForm: document.querySelector("#accountForm"),
  cancelAccountForm: document.querySelector("#cancelAccountForm"),
  accountFormStatus: document.querySelector("#accountFormStatus"),
  historyCaption: document.querySelector("#historyCaption"),
  historyRange: document.querySelector("#historyRange"),
  chart: document.querySelector("#financeChart"),
  cardCount: document.querySelector("#cardCount"),
  accountCount: document.querySelector("#accountCount"),
  cardRows: document.querySelector("#cardRows"),
  accountRows: document.querySelector("#accountRows"),
  refreshCaption: document.querySelector("#refreshCaption"),
  refreshLog: document.querySelector("#refreshLog")
};

financeEls.refresh.addEventListener("click", async () => {
  financeEls.refresh.disabled = true;
  try {
    await fetchJson("/api/finance/refresh", { method: "POST" });
    await loadFinance();
  } finally {
    financeEls.refresh.disabled = false;
  }
});

financeEls.showAccountForm.addEventListener("click", () => {
  showAddAccountForm();
});

financeEls.editAccountSelect.addEventListener("change", () => {
  const account = (financeState.data?.current?.accounts || []).find(item => item.id === financeEls.editAccountSelect.value);
  if (account) {
    showEditAccountForm(account);
  }
});

financeEls.cancelAccountForm.addEventListener("click", () => {
  hideAccountForm();
});

financeEls.accountForm.addEventListener("submit", async event => {
  event.preventDefault();
  const formData = new FormData(financeEls.accountForm);
  const payload = Object.fromEntries([...formData.entries()].map(([key, value]) => [key, String(value).trim() || null]));
  const accountId = payload.id;
  delete payload.id;
  financeEls.accountFormStatus.textContent = accountId ? "Saving account..." : "Adding account...";
  await fetchJson(accountId ? `/api/finance/accounts/${encodeURIComponent(accountId)}` : "/api/finance/accounts", {
    method: accountId ? "PUT" : "POST",
    body: JSON.stringify(payload)
  });
  financeEls.accountForm.reset();
  financeEls.accountFormStatus.textContent = accountId
    ? "Account updated. Website accounts are queued for Codex Computer Use assisted refresh."
    : "Account added. Website accounts are queued for Codex Computer Use assisted refresh.";
  hideAccountForm();
  await loadFinance();
});

financeEls.historyRange.addEventListener("change", () => {
  financeState.historyRange = financeEls.historyRange.value;
  if (financeState.data) {
    renderChart(financeState.data);
  }
});

loadFinance();
setInterval(loadFinance, 60000);

function hideAccountForm() {
  financeEls.accountForm.reset();
  financeEls.accountFormTitle.textContent = "Accounts";
  financeEls.accountForm.hidden = true;
  financeEls.showAccountForm.hidden = false;
  financeEls.editAccountSelect.value = "";
}

function showAddAccountForm() {
  financeEls.accountForm.reset();
  document.querySelector("#accountId").value = "";
  financeEls.accountFormTitle.textContent = "Add Account";
  financeEls.accountForm.hidden = false;
  financeEls.showAccountForm.hidden = true;
  financeEls.accountFormStatus.textContent = "Values are stored locally. Website refreshes require a Codex-assisted session.";
  document.querySelector("#accountName").focus();
}

function showEditAccountForm(account) {
  financeEls.accountForm.reset();
  document.querySelector("#accountId").value = account.id;
  document.querySelector("#accountName").value = account.name || "";
  document.querySelector("#accountKind").value = account.kind || "credit_card";
  document.querySelector("#accountInstitution").value = account.institution || "";
  document.querySelector("#accountLoginUrl").value = account.loginUrl || "";
  document.querySelector("#accountUsername").value = account.username || "";
  document.querySelector("#accountPassword").value = "";
  document.querySelector("#accountCashBalance").value = account.cashBalance ?? "";
  document.querySelector("#accountBalanceOwed").value = account.balanceOwed ?? "";
  document.querySelector("#accountCreditLimit").value = account.creditLimit ?? "";
  document.querySelector("#accountCreditAvailable").value = account.creditAvailable ?? "";
  document.querySelector("#accountAprPercent").value = account.aprPercent ?? "";
  document.querySelector("#accountCollectorNotes").value = account.collectorNotes || "";
  financeEls.accountFormTitle.textContent = `Edit ${account.name}`;
  financeEls.accountForm.hidden = false;
  financeEls.showAccountForm.hidden = true;
  financeEls.accountFormStatus.textContent = "Password is not shown. Leave it blank to keep the saved password, or enter a new one.";
  document.querySelector("#accountName").focus();
}

async function loadFinance() {
  financeState.data = await fetchJson("/api/finance/state");
  renderFinance();
}

async function fetchJson(url, options = {}) {
  const response = await fetch(url, {
    headers: { "Content-Type": "application/json" },
    ...options
  });
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`);
  }
  return response.json();
}

function renderFinance() {
  const data = financeState.data;
  const current = data.current;
  const refresh = data.refresh || {};
  financeEls.summary.textContent = summaryText(data);
  renderRefreshAlert(data);

  financeEls.netAfterDebt.textContent = money(current.netAfterDebt, data.currency);
  financeEls.totalCash.textContent = money(current.totalCash, data.currency);
  financeEls.totalCredit.textContent = money(current.totalCreditAvailable, data.currency);
  financeEls.totalDebt.textContent = money(current.totalDebt, data.currency);
  financeEls.netBand.classList.toggle("positive", current.netAfterDebt > 0);
  financeEls.netBand.classList.toggle("negative", current.netAfterDebt < 0);

  renderChart(data);
  renderAccountSelector(data);
  renderTables(data);
  renderLog(data.refreshLog || []);
  document.title = `${money(current.netAfterDebt, data.currency)} - Finances`;
}

function renderAccountSelector(data) {
  const selected = financeEls.editAccountSelect.value;
  const accounts = data.current.accounts || [];
  financeEls.editAccountSelect.textContent = "";
  const placeholder = document.createElement("option");
  placeholder.value = "";
  placeholder.textContent = accounts.length === 0 ? "No accounts to edit" : "Edit account...";
  financeEls.editAccountSelect.append(placeholder);
  for (const account of accounts) {
    const option = document.createElement("option");
    option.value = account.id;
    option.textContent = account.name;
    financeEls.editAccountSelect.append(option);
  }
  financeEls.editAccountSelect.value = accounts.some(account => account.id === selected) ? selected : "";
}

function summaryText(data) {
  const refresh = data.refresh || {};
  const accountText = `${data.configuredAccountCount} configured account${data.configuredAccountCount === 1 ? "" : "s"}`;
  const refreshed = refresh.lastCompletedUtc ? `last refresh ${formatDateTime(refresh.lastCompletedUtc)}` : "not refreshed yet";
  return `${accountText} - daily refresh ${data.dailyRefreshTime} - ${refreshed}`;
}

function renderRefreshAlert(data) {
  const refresh = data.refresh || {};
  const noAccounts = data.configuredAccountCount === 0;
  const hasError = Boolean(refresh.error);
  const hasWarning = noAccounts || Boolean(refresh.message && !refresh.lastSucceeded);
  if (!hasError && !hasWarning) {
    financeEls.alert.hidden = true;
    financeEls.alert.textContent = "";
    financeEls.alert.className = "poll-alert";
    return;
  }

  financeEls.alert.hidden = false;
  financeEls.alert.className = `poll-alert ${hasError ? "poll-alert-failed" : "poll-alert-warning"}`;
  financeEls.alert.textContent = hasError
    ? `Finance refresh failed: ${refresh.error}`
    : noAccounts
      ? `No finance accounts configured. Add accounts to ${data.envPath}; manual values can use CASH_BALANCE, BALANCE_OWED, CREDIT_LIMIT, and CREDIT_AVAILABLE fields.`
      : `Finance refresh needs attention: ${refresh.message}`;
}

function renderTables(data) {
  const accounts = data.current.accounts || [];
  const cards = accounts.filter(account => account.kind === "credit_card");
  financeEls.cardCount.textContent = `${cards.length} card${cards.length === 1 ? "" : "s"}`;
  financeEls.accountCount.textContent = `${accounts.length} account${accounts.length === 1 ? "" : "s"}`;
  financeEls.cardRows.textContent = "";
  financeEls.accountRows.textContent = "";

  if (cards.length === 0) {
    financeEls.cardRows.append(emptyRow(6, "No credit cards configured yet."));
  } else {
    for (const card of cards) {
      const row = document.createElement("tr");
      row.append(
        accountCell(card),
        moneyCell(card.balanceOwed, data.currency),
        moneyCell(card.creditAvailable, data.currency),
        percentCell(card.aprPercent),
        textCell(card.utilizationPercent === null ? "--" : `${card.utilizationPercent}%`),
        statusCell(card)
      );
      financeEls.cardRows.append(row);
    }
  }

  if (accounts.length === 0) {
    financeEls.accountRows.append(emptyRow(6, "No accounts configured yet."));
  } else {
    for (const account of accounts) {
      const row = document.createElement("tr");
      row.append(
        accountCell(account),
        textCell(account.kind.replaceAll("_", " ")),
        moneyCell(account.cashBalance, data.currency),
        moneyCell(account.balanceOwed, data.currency),
        percentCell(account.aprPercent),
        statusCell(account)
      );
      financeEls.accountRows.append(row);
    }
  }
}

function renderLog(logs) {
  financeEls.refreshCaption.textContent = logs.length === 0 ? "No refreshes yet" : `${logs.length} recent entries`;
  financeEls.refreshLog.textContent = "";
  if (logs.length === 0) {
    const empty = document.createElement("div");
    empty.className = "empty-state";
    empty.textContent = "Refresh history will appear here.";
    financeEls.refreshLog.append(empty);
    return;
  }

  for (const log of logs) {
    const row = document.createElement("div");
    row.className = "refresh-log-row";
    const pill = document.createElement("span");
    pill.className = `state-pill ${log.status === "ok" || log.status === "queued" ? "online" : log.status === "warning" || log.status === "partial" ? "stale" : ""}`;
    pill.textContent = log.status;
    const message = document.createElement("div");
    message.textContent = log.message;
    const time = document.createElement("div");
    time.className = "event-time";
    time.textContent = formatDateTime(log.atUtc);
    row.append(pill, message, time);
    financeEls.refreshLog.append(row);
  }
}

function renderChart(data) {
  const allHistory = data.history || [];
  const history = filterHistoryByRange(allHistory, financeState.historyRange);
  const rangeLabel = selectedRangeLabel();
  const svg = financeEls.chart;
  svg.textContent = "";
  if (history.length < 2) {
    svg.setAttribute("height", "180");
    svg.setAttribute("viewBox", "0 0 820 180");
    drawSvgText(svg, 24, 92, history.length === 0 ? `No finance history in ${rangeLabel}.` : "One snapshot in this range. More refreshes will build the graph.", "empty-svg");
    financeEls.historyCaption.textContent = history.length === 0
      ? `${rangeLabel} - no snapshots`
      : `${rangeLabel} - 1 snapshot`;
    return;
  }

  financeEls.historyCaption.textContent = `${rangeLabel} - ${history.length} of ${allHistory.length} snapshots`;
  const width = Math.max(820, svg.parentElement.clientWidth - 24);
  const height = 280;
  const left = 72;
  const right = 24;
  const top = 22;
  const bottom = 42;
  const plotWidth = width - left - right;
  const plotHeight = height - top - bottom;
  const series = [
    { key: "netAfterDebt", label: "Net", className: "chart-line-net" },
    { key: "totalCash", label: "Cash", className: "chart-line-cash" },
    { key: "totalDebt", label: "Debt", className: "chart-line-debt" },
    { key: "totalCreditAvailable", label: "Credit", className: "chart-line-credit" }
  ];
  const values = history.flatMap(snapshot => series.map(item => Number(snapshot[item.key] || 0)));
  const minValue = Math.min(0, ...values);
  const maxValue = Math.max(1, ...values);
  const start = new Date(history[0].sampledAtUtc);
  const end = new Date(history[history.length - 1].sampledAtUtc);
  const span = end - start || 1;

  svg.setAttribute("height", String(height));
  svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
  drawLine(svg, left, top, left, height - bottom, "#d5ddd7", 1, "axis-grid");
  drawLine(svg, left, height - bottom, width - right, height - bottom, "#b8c2bc", 1, "axis-baseline");

  for (let i = 0; i <= 4; i++) {
    const value = minValue + ((maxValue - minValue) * i) / 4;
    const y = valueToY(value, minValue, maxValue, top, plotHeight);
    drawLine(svg, left, y, width - right, y, "#edf0ee", 1, "axis-grid");
    drawSvgText(svg, 8, y + 4, compactMoney(value, data.currency), "axis-label");
  }

  for (const item of series) {
    const points = history.map(snapshot => {
      const x = left + ((new Date(snapshot.sampledAtUtc) - start) / span) * plotWidth;
      const y = valueToY(Number(snapshot[item.key] || 0), minValue, maxValue, top, plotHeight);
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    }).join(" ");
    const polyline = document.createElementNS("http://www.w3.org/2000/svg", "polyline");
    polyline.setAttribute("points", points);
    polyline.setAttribute("fill", "none");
    polyline.setAttribute("stroke-width", "2.6");
    polyline.setAttribute("class", item.className);
    svg.append(polyline);
  }

  drawSvgText(svg, left, height - 14, formatDate(history[0].sampledAtUtc), "axis-label");
  drawSvgText(svg, width - right - 70, height - 14, formatDate(history[history.length - 1].sampledAtUtc), "axis-label");
  series.forEach((item, index) => {
    const x = left + index * 86;
    drawLine(svg, x, 12, x + 20, 12, colorForSeries(item.className), 3, "");
    drawSvgText(svg, x + 26, 16, item.label, "axis-label");
  });
}

function filterHistoryByRange(history, range) {
  if (history.length === 0) {
    return [];
  }

  const latest = history.reduce((max, snapshot) => {
    const sampledAt = new Date(snapshot.sampledAtUtc).getTime();
    return Number.isFinite(sampledAt) ? Math.max(max, sampledAt) : max;
  }, 0);
  if (!latest) {
    return history;
  }

  const cutoff = latest - rangeToMilliseconds(range);
  return history.filter(snapshot => {
    const sampledAt = new Date(snapshot.sampledAtUtc).getTime();
    return Number.isFinite(sampledAt) && sampledAt >= cutoff;
  });
}

function rangeToMilliseconds(range) {
  return {
    "24h": 24 * 60 * 60 * 1000,
    "1w": 7 * 24 * 60 * 60 * 1000,
    "1m": 31 * 24 * 60 * 60 * 1000,
    "3m": 93 * 24 * 60 * 60 * 1000,
    "6m": 183 * 24 * 60 * 60 * 1000,
    "12m": 366 * 24 * 60 * 60 * 1000,
    "24m": 732 * 24 * 60 * 60 * 1000
  }[range] || 7 * 24 * 60 * 60 * 1000;
}

function selectedRangeLabel() {
  const option = financeEls.historyRange.options[financeEls.historyRange.selectedIndex];
  return option ? option.textContent : "1 week";
}

function accountCell(account) {
  const cell = document.createElement("td");
  const wrapper = document.createElement("div");
  wrapper.className = "account-name";
  wrapper.textContent = account.name;
  const institution = document.createElement("span");
  institution.textContent = [account.institution, account.loginUrl ? "website linked" : null].filter(Boolean).join(" - ");
  wrapper.append(institution);
  cell.append(wrapper);
  return cell;
}

function moneyCell(value, currency) {
  const cell = document.createElement("td");
  cell.className = "money-cell";
  cell.textContent = value === null || value === undefined ? "--" : money(value, currency);
  return cell;
}

function textCell(value) {
  const cell = document.createElement("td");
  cell.textContent = value;
  return cell;
}

function percentCell(value) {
  const cell = document.createElement("td");
  cell.className = "money-cell";
  cell.textContent = value === null || value === undefined ? "--" : `${Number(value).toFixed(2)}%`;
  return cell;
}

function statusCell(account) {
  const cell = document.createElement("td");
  const pill = document.createElement("span");
  pill.className = `state-pill ${account.status === "ok" ? "online" : "stale"}`;
  pill.textContent = account.status;
  cell.append(pill);
  if (account.message) {
    const message = document.createElement("div");
    message.className = "status-text";
    message.textContent = account.message;
    cell.append(message);
  }
  if (account.collector === "computer_control") {
    const collector = document.createElement("div");
    collector.className = "status-text";
    collector.textContent = "collector: Codex Computer Use";
    cell.append(collector);
  }
  if (account.collectorNotes) {
    const notes = document.createElement("button");
    notes.type = "button";
    notes.className = "status-text status-note collapsed";
    notes.title = "Click to expand notes";
    notes.textContent = `notes: ${account.collectorNotes}`;
    notes.addEventListener("click", () => {
      const collapsed = notes.classList.toggle("collapsed");
      notes.title = collapsed ? "Click to expand notes" : "Click to collapse notes";
    });
    cell.append(notes);
  }
  return cell;
}

function emptyRow(columns, text) {
  const row = document.createElement("tr");
  const cell = document.createElement("td");
  cell.colSpan = columns;
  cell.className = "empty-state";
  cell.textContent = text;
  row.append(cell);
  return row;
}

function valueToY(value, minValue, maxValue, top, height) {
  const span = maxValue - minValue || 1;
  return top + height - ((value - minValue) / span) * height;
}

function drawLine(svg, x1, y1, x2, y2, stroke, width, className = "") {
  const line = document.createElementNS("http://www.w3.org/2000/svg", "line");
  line.setAttribute("x1", x1.toFixed(1));
  line.setAttribute("y1", y1.toFixed(1));
  line.setAttribute("x2", x2.toFixed(1));
  line.setAttribute("y2", y2.toFixed(1));
  line.setAttribute("stroke", stroke);
  line.setAttribute("stroke-width", String(width));
  if (className) {
    line.setAttribute("class", className);
  }
  svg.append(line);
}

function drawSvgText(svg, x, y, text, className) {
  const label = document.createElementNS("http://www.w3.org/2000/svg", "text");
  label.setAttribute("x", String(x));
  label.setAttribute("y", String(y));
  label.setAttribute("class", className);
  label.textContent = text;
  svg.append(label);
}

function colorForSeries(className) {
  return {
    "chart-line-net": "#16845f",
    "chart-line-cash": "#276f9f",
    "chart-line-debt": "#bd4f43",
    "chart-line-credit": "#7a5ea8"
  }[className] || "#65736e";
}

function money(value, currency) {
  return new Intl.NumberFormat([], { style: "currency", currency }).format(Number(value || 0));
}

function compactMoney(value, currency) {
  return new Intl.NumberFormat([], { style: "currency", currency, notation: "compact", maximumFractionDigits: 1 }).format(Number(value || 0));
}

function formatDateTime(value) {
  return new Date(value).toLocaleString([], {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  });
}

function formatDate(value) {
  return new Date(value).toLocaleDateString([], { month: "short", day: "numeric" });
}
