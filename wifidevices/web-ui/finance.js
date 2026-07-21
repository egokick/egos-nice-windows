const financeState = {
  data: null,
  historyRange: "6m",
  visibleSeries: readStoredVisibleFinanceSeries()
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
  refreshLog: document.querySelector("#refreshLog"),
  interestPreviewDialog: document.querySelector("#interestPreviewDialog"),
  interestPreviewAccount: document.querySelector("#interestPreviewAccount"),
  interestPreviewBalance: document.querySelector("#interestPreviewBalance"),
  interestPreviewCurrent: document.querySelector("#interestPreviewCurrent"),
  interestPreviewPayment: document.querySelector("#interestPreviewPayment"),
  interestPreviewResult: document.querySelector("#interestPreviewResult")
};

let interestPreview = null;

financeEls.refresh.addEventListener("click", async () => {
  financeEls.refresh.disabled = true;
  try {
    const result = await fetchJson("/api/finance/refresh", { method: "POST" });
    await loadFinance();
    financeEls.alert.hidden = false;
    financeEls.alert.className = `poll-alert ${result.started || result.alreadyRunning ? "poll-alert-warning" : "poll-alert-failed"}`;
    financeEls.alert.textContent = result.started
      ? "Codex finance refresh started in the background."
      : result.message || result.error || "Codex finance refresh could not be started.";
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

financeEls.interestPreviewPayment.addEventListener("input", renderInterestPreview);
financeEls.interestPreviewDialog.addEventListener("close", () => {
  interestPreview = null;
});

loadFinance();
setInterval(loadFinance, 60000);

function readStoredVisibleFinanceSeries() {
  try {
    const parsed = JSON.parse(localStorage.getItem("finance:visibleSeries") || "null");
    if (Array.isArray(parsed) && parsed.length > 0) {
      return new Set(parsed);
    }
  } catch {
    // Ignore corrupt local preferences and fall back to all series.
  }

  return new Set(["netAfterDebt", "totalCash", "totalDebt", "totalCreditAvailable"]);
}

function persistVisibleFinanceSeries() {
  localStorage.setItem("finance:visibleSeries", JSON.stringify([...financeState.visibleSeries]));
}

function toggleFinanceSeries(key) {
  if (financeState.visibleSeries.has(key)) {
    financeState.visibleSeries.delete(key);
  } else {
    financeState.visibleSeries.add(key);
  }

  if (financeState.visibleSeries.size === 0) {
    financeState.visibleSeries.add(key);
  }

  persistVisibleFinanceSeries();
  if (financeState.data) {
    renderChart(financeState.data);
  }
}

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
  const creditLoans = accounts
    .filter(account => account.kind === "credit_card" || account.kind === "loan")
    .sort((left, right) => compareNullableNumbersDescending(left.balanceOwed, right.balanceOwed));
  const sortedAccounts = accounts
    .filter(account => account.kind !== "credit_card" && account.kind !== "loan")
    .sort((left, right) => compareNullableNumbersDescending(left.balanceOwed, right.balanceOwed));
  financeEls.cardCount.textContent = `${creditLoans.length} credit/loan${creditLoans.length === 1 ? "" : "s"}`;
  financeEls.accountCount.textContent = `${sortedAccounts.length} account${sortedAccounts.length === 1 ? "" : "s"}`;
  financeEls.cardRows.textContent = "";
  financeEls.accountRows.textContent = "";

  if (creditLoans.length === 0) {
    financeEls.cardRows.append(emptyRow(7, "No credit cards or loans configured yet."));
  } else {
    for (const card of creditLoans) {
      const row = document.createElement("tr");
      row.append(
        accountCell(card),
        moneyCell(card.balanceOwed, data.currency),
        moneyCell(card.creditAvailable, data.currency),
        percentCell(card.aprPercent),
        interestPreviewCell(card, data.currency),
        textCell(card.utilizationPercent === null ? "--" : `${card.utilizationPercent}%`),
        statusCell(card)
      );
      financeEls.cardRows.append(row);
    }
  }

  if (sortedAccounts.length === 0) {
    financeEls.accountRows.append(emptyRow(6, "No accounts configured yet."));
  } else {
    for (const account of sortedAccounts) {
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

function compareNullableNumbersDescending(left, right) {
  const leftValue = Number(left);
  const rightValue = Number(right);
  const leftMissing = left === null || left === undefined || !Number.isFinite(leftValue);
  const rightMissing = right === null || right === undefined || !Number.isFinite(rightValue);
  if (leftMissing && rightMissing) return 0;
  if (leftMissing) return 1;
  if (rightMissing) return -1;
  return rightValue - leftValue;
}

function monthlyInterest(balanceOwed, aprPercent) {
  const owed = Number(balanceOwed);
  const apr = Number(aprPercent);
  if (balanceOwed === null || balanceOwed === undefined || aprPercent === null || aprPercent === undefined
    || !Number.isFinite(owed) || !Number.isFinite(apr)) {
    return null;
  }

  return owed * (apr / 100) / 12;
}

function interestPreviewCell(account, currency) {
  const interest = monthlyInterest(account.balanceOwed, account.aprPercent);
  if (interest === null) {
    return moneyCell(null, currency);
  }

  const cell = document.createElement("td");
  cell.className = "money-cell";
  const button = document.createElement("button");
  button.type = "button";
  button.className = "interest-preview-trigger";
  button.textContent = money(interest, currency);
  button.title = "Preview monthly interest after a payment";
  button.setAttribute("aria-haspopup", "dialog");
  button.setAttribute("aria-label", `Preview monthly interest for ${account.name}, currently ${money(interest, currency)}`);
  button.addEventListener("click", () => openInterestPreview(account, currency));
  cell.append(button);
  return cell;
}

function openInterestPreview(account, currency) {
  const owed = Number(account.balanceOwed);
  const apr = Number(account.aprPercent);
  if (!Number.isFinite(owed) || !Number.isFinite(apr)) {
    return;
  }

  interestPreview = { account, currency, owed, apr };
  financeEls.interestPreviewAccount.textContent = `${account.name} at ${apr.toFixed(2)}% APR`;
  financeEls.interestPreviewBalance.textContent = money(owed, currency);
  financeEls.interestPreviewCurrent.textContent = money(monthlyInterest(owed, apr), currency);
  financeEls.interestPreviewPayment.value = "";
  renderInterestPreview();
  if (!financeEls.interestPreviewDialog.open) {
    financeEls.interestPreviewDialog.showModal();
  }
  financeEls.interestPreviewPayment.focus();
}

function renderInterestPreview() {
  if (!interestPreview) {
    return;
  }

  const enteredValue = financeEls.interestPreviewPayment.value.trim();
  const requestedPayment = enteredValue === "" ? 0 : Number(enteredValue);
  if (!Number.isFinite(requestedPayment) || requestedPayment < 0) {
    financeEls.interestPreviewResult.textContent = "Enter a payment amount of zero or more.";
    return;
  }

  const appliedPayment = Math.min(requestedPayment, interestPreview.owed);
  const remainingBalance = interestPreview.owed - appliedPayment;
  const newInterest = monthlyInterest(remainingBalance, interestPreview.apr);
  const cappedNotice = requestedPayment > interestPreview.owed
    ? ` Payment is capped at ${money(interestPreview.owed, interestPreview.currency)}.`
    : "";
  financeEls.interestPreviewResult.textContent = `${money(remainingBalance, interestPreview.currency)} remaining — estimated monthly interest: ${money(newInterest, interestPreview.currency)}.${cappedNotice}`;
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
  const rangedHistory = filterHistoryByRange(allHistory, financeState.historyRange);
  const history = latestSnapshotPerDay(rangedHistory);
  const rangeLabel = selectedRangeLabel();
  const svg = financeEls.chart;
  svg.textContent = "";
  if (history.length < 2) {
    svg.setAttribute("height", "180");
    svg.setAttribute("viewBox", "0 0 820 180");
    drawSvgText(svg, 24, 92, history.length === 0 ? `No finance history in ${rangeLabel}.` : "One daily value in this range. More days will build the graph.", "empty-svg");
    financeEls.historyCaption.textContent = history.length === 0
      ? `${rangeLabel} - no daily values`
      : `${rangeLabel} - 1 daily value from ${rangedHistory.length} snapshots`;
    return;
  }

  financeEls.historyCaption.textContent = `${rangeLabel} - ${history.length} daily values from ${rangedHistory.length} snapshots`;
  const width = Math.max(minChartWidthForRange(financeState.historyRange), svg.parentElement.clientWidth - 24);
  const height = 330;
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
  const visibleSeries = series.filter(item => financeState.visibleSeries.has(item.key));
  const chartValues = history.map(snapshot => ({
    snapshot,
    values: Object.fromEntries(series.map(item => {
      const value = Number(snapshot[item.key] || 0);
      return [item.key, value];
    }))
  }));
  const values = chartValues.flatMap(point => visibleSeries.map(item => point.values[item.key]));
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

  drawTimeAxis(svg, start, end, {
    left,
    bottom,
    width,
    height,
    plotWidth,
    top,
    plotHeight,
    range: financeState.historyRange
  });

  for (const item of visibleSeries) {
    const points = chartValues.map(point => {
      const x = left + ((new Date(point.snapshot.sampledAtUtc) - start) / span) * plotWidth;
      const y = valueToY(point.values[item.key], minValue, maxValue, top, plotHeight);
      return { x, y, value: point.values[item.key], snapshot: point.snapshot };
    });
    const polyline = document.createElementNS("http://www.w3.org/2000/svg", "polyline");
    polyline.setAttribute("points", points.map(point => `${point.x.toFixed(1)},${point.y.toFixed(1)}`).join(" "));
    polyline.setAttribute("fill", "none");
    polyline.setAttribute("stroke-width", "2.6");
    polyline.setAttribute("class", item.className);
    svg.append(polyline);

    for (const point of points) {
      drawCircle(svg, point.x, point.y, 3.8, colorForSeries(item.className), "chart-point");
    }
  }

  series.forEach((item, index) => {
    const x = left + index * 86;
    drawLegendToggle(svg, x, 4, item, financeState.visibleSeries.has(item.key));
  });

  attachChartTooltip(svg, chartValues, visibleSeries, {
    left,
    right,
    top,
    width,
    height,
    plotHeight,
    plotWidth,
    bottom,
    start,
    span,
    minValue,
    maxValue,
    currency: data.currency
  });
}

function attachChartTooltip(svg, chartValues, visibleSeries, chart) {
  const overlay = document.createElementNS("http://www.w3.org/2000/svg", "rect");
  overlay.setAttribute("x", String(chart.left));
  overlay.setAttribute("y", String(chart.top));
  overlay.setAttribute("width", String(chart.plotWidth));
  overlay.setAttribute("height", String(chart.plotHeight));
  overlay.setAttribute("class", "chart-hover-overlay");
  svg.append(overlay);

  const tooltip = document.createElementNS("http://www.w3.org/2000/svg", "g");
  tooltip.setAttribute("class", "chart-tooltip");
  tooltip.setAttribute("visibility", "hidden");

  const hoverLine = document.createElementNS("http://www.w3.org/2000/svg", "line");
  hoverLine.setAttribute("class", "chart-hover-line");
  hoverLine.setAttribute("y1", String(chart.top));
  hoverLine.setAttribute("y2", String(chart.height - chart.bottom));
  tooltip.append(hoverLine);

  const box = document.createElementNS("http://www.w3.org/2000/svg", "rect");
  box.setAttribute("rx", "7");
  box.setAttribute("class", "chart-tooltip-box");
  tooltip.append(box);

  const rows = [document.createElementNS("http://www.w3.org/2000/svg", "text")];
  rows[0].setAttribute("class", "chart-tooltip-title");
  tooltip.append(rows[0]);

  for (const item of visibleSeries) {
    const row = document.createElementNS("http://www.w3.org/2000/svg", "text");
    row.setAttribute("class", "chart-tooltip-row");
    row.setAttribute("fill", colorForSeries(item.className));
    tooltip.append(row);
    rows.push(row);
  }

  svg.append(tooltip);

  const tooltipWidth = 210;
  const tooltipHeight = 30 + visibleSeries.length * 18;
  box.setAttribute("width", String(tooltipWidth));
  box.setAttribute("height", String(tooltipHeight));

  const moveTooltip = event => {
    const point = svg.createSVGPoint();
    point.x = event.clientX;
    point.y = event.clientY;
    const svgPoint = point.matrixTransform(svg.getScreenCTM().inverse());
    const boundedX = Math.max(chart.left, Math.min(chart.left + chart.plotWidth, svgPoint.x));
    const at = chart.start.getTime() + ((boundedX - chart.left) / chart.plotWidth) * chart.span;
    const nearest = nearestChartValue(chartValues, at);
    if (!nearest) {
      return;
    }

    const x = chart.left + ((new Date(nearest.snapshot.sampledAtUtc) - chart.start) / chart.span) * chart.plotWidth;
    hoverLine.setAttribute("x1", x.toFixed(1));
    hoverLine.setAttribute("x2", x.toFixed(1));

    let tooltipX = x + 12;
    if (tooltipX + tooltipWidth > chart.width - chart.right) {
      tooltipX = x - tooltipWidth - 12;
    }
    tooltipX = Math.max(8, tooltipX);
    const tooltipY = Math.max(8, Math.min(chart.height - chart.bottom - tooltipHeight - 8, svgPoint.y - tooltipHeight / 2));
    box.setAttribute("x", tooltipX.toFixed(1));
    box.setAttribute("y", tooltipY.toFixed(1));

    rows[0].setAttribute("x", String(tooltipX + 12));
    rows[0].setAttribute("y", String(tooltipY + 20));
    rows[0].textContent = formatDateTime(nearest.snapshot.sampledAtUtc);

    visibleSeries.forEach((item, index) => {
      const row = rows[index + 1];
      row.setAttribute("x", String(tooltipX + 12));
      row.setAttribute("y", String(tooltipY + 42 + index * 18));
      const accountValue = Number(nearest.snapshot[item.key] || 0);
      row.textContent = `${item.label}: ${money(accountValue, chart.currency)}`;
    });

    tooltip.setAttribute("visibility", "visible");
  };

  overlay.addEventListener("mousemove", moveTooltip);
  overlay.addEventListener("mouseenter", moveTooltip);
  overlay.addEventListener("mouseleave", () => tooltip.setAttribute("visibility", "hidden"));
}

function nearestChartValue(chartValues, time) {
  let nearest = null;
  let nearestDistance = Number.POSITIVE_INFINITY;
  for (const point of chartValues) {
    const distance = Math.abs(new Date(point.snapshot.sampledAtUtc).getTime() - time);
    if (distance < nearestDistance) {
      nearest = point;
      nearestDistance = distance;
    }
  }

  return nearest;
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

function latestSnapshotPerDay(history) {
  const byDay = new Map();
  for (const snapshot of history) {
    const sampledAt = new Date(snapshot.sampledAtUtc);
    const sampledAtMs = sampledAt.getTime();
    if (!Number.isFinite(sampledAtMs)) {
      continue;
    }

    const key = localDateKey(sampledAt);
    const existing = byDay.get(key);
    if (!existing || sampledAtMs > new Date(existing.sampledAtUtc).getTime()) {
      byDay.set(key, snapshot);
    }
  }

  return [...byDay.values()].sort((left, right) => new Date(left.sampledAtUtc) - new Date(right.sampledAtUtc));
}

function localDateKey(date) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
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

function minChartWidthForRange(range) {
  return {
    "24h": 820,
    "1w": 820,
    "1m": 1500,
    "3m": 940,
    "6m": 940,
    "12m": 940,
    "24m": 1040
  }[range] || 820;
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

function drawTimeAxis(svg, start, end, chart) {
  const span = end - start || 1;
  const ticks = timeAxisTicks(start, end, chart.range);
  const seenLabels = new Set();
  for (const tick of ticks) {
    const x = chart.left + ((tick.date - start) / span) * chart.plotWidth;
    if (x < chart.left - 0.5 || x > chart.left + chart.plotWidth + 0.5) {
      continue;
    }

    drawLine(svg, x, chart.top, x, chart.top + chart.plotHeight, "#f0f3f1", 1, "axis-grid");
    const label = tick.label;
    if (seenLabels.has(`${Math.round(x)}:${label}`)) {
      continue;
    }

    seenLabels.add(`${Math.round(x)}:${label}`);
    drawSvgText(svg, x, chart.height - chart.bottom + 20, label, "axis-label axis-label-x");
  }
}

function timeAxisTicks(start, end, range) {
  const ticks = [];
  if (range === "24h") {
    return [
      { date: start, label: formatTimeOnly(start) },
      { date: end, label: formatTimeOnly(end) }
    ];
  }

  if (range === "1w" || range === "1m") {
    ticks.push({ date: start, label: formatDailyTick(start, range) });
    for (const tickDate = startOfLocalDay(start); tickDate <= end; tickDate.setDate(tickDate.getDate() + 1)) {
      if (tickDate > start && !sameLocalDate(tickDate, start)) {
        ticks.push({ date: new Date(tickDate), label: formatDailyTick(tickDate, range) });
      }
    }
  } else {
    const stepMonths = range === "3m" ? 1 : range === "6m" ? 2 : range === "12m" ? 3 : 6;
    const monthTick = startOfLocalMonth(start);
    if (monthTick < start) {
      monthTick.setMonth(monthTick.getMonth() + 1);
    }

    for (const tickDate = new Date(monthTick); tickDate <= end; tickDate.setMonth(tickDate.getMonth() + stepMonths)) {
      ticks.push({ date: new Date(tickDate), label: formatMonthTick(tickDate, range) });
    }
  }

  if (ticks.length === 0 || (!isDailyAxisRange(range) && ticks[0].date - start > 12 * 60 * 60 * 1000)) {
    ticks.unshift({ date: start, label: formatDate(start) });
  }

  const last = ticks[ticks.length - 1];
  if (!last || (!isDailyAxisRange(range) && end - last.date > 12 * 60 * 60 * 1000)) {
    ticks.push({ date: end, label: formatDate(end) });
  }

  return ticks;
}

function startOfLocalDay(date) {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate());
}

function startOfLocalMonth(date) {
  return new Date(date.getFullYear(), date.getMonth(), 1);
}

function sameLocalDate(left, right) {
  return left.getFullYear() === right.getFullYear()
    && left.getMonth() === right.getMonth()
    && left.getDate() === right.getDate();
}

function isDailyAxisRange(range) {
  return range === "1w" || range === "1m";
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

function drawCircle(svg, x, y, radius, fill, className = "") {
  const circle = document.createElementNS("http://www.w3.org/2000/svg", "circle");
  circle.setAttribute("cx", x.toFixed(1));
  circle.setAttribute("cy", y.toFixed(1));
  circle.setAttribute("r", String(radius));
  circle.setAttribute("fill", fill);
  if (className) {
    circle.setAttribute("class", className);
  }
  svg.append(circle);
}

function drawLegendToggle(svg, x, y, item, visible) {
  const group = document.createElementNS("http://www.w3.org/2000/svg", "g");
  group.setAttribute("class", `chart-legend-toggle${visible ? "" : " chart-legend-hidden"}`);
  group.setAttribute("role", "button");
  group.setAttribute("tabindex", "0");
  group.setAttribute("aria-label", `${visible ? "Hide" : "Show"} ${item.label} values over time`);

  const hit = document.createElementNS("http://www.w3.org/2000/svg", "rect");
  hit.setAttribute("x", String(x - 7));
  hit.setAttribute("y", String(y - 2));
  hit.setAttribute("width", "78");
  hit.setAttribute("height", "24");
  hit.setAttribute("rx", "6");
  hit.setAttribute("class", "chart-legend-hit");
  group.append(hit);

  const line = document.createElementNS("http://www.w3.org/2000/svg", "line");
  line.setAttribute("x1", String(x));
  line.setAttribute("y1", String(y + 8));
  line.setAttribute("x2", String(x + 20));
  line.setAttribute("y2", String(y + 8));
  line.setAttribute("stroke", colorForSeries(item.className));
  line.setAttribute("stroke-width", "3");
  group.append(line);

  const label = document.createElementNS("http://www.w3.org/2000/svg", "text");
  label.setAttribute("x", String(x + 26));
  label.setAttribute("y", String(y + 12));
  label.setAttribute("class", "axis-label chart-legend-label");
  label.textContent = item.label;
  group.append(label);

  const toggle = event => {
    event.preventDefault();
    event.stopPropagation();
    toggleFinanceSeries(item.key);
  };
  group.addEventListener("click", toggle);
  group.addEventListener("keydown", event => {
    if (event.key === "Enter" || event.key === " ") {
      toggle(event);
    }
  });

  svg.append(group);
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

function formatWeekday(value) {
  return new Date(value).toLocaleDateString([], { weekday: "short" });
}

function formatDailyTick(value, range) {
  return range === "1w" ? formatWeekday(value) : formatDate(value);
}

function formatTimeOnly(value) {
  return new Date(value).toLocaleTimeString([], { hour: "numeric" });
}

function formatMonthTick(value, range) {
  const options = range === "24m"
    ? { month: "short", year: "2-digit" }
    : { month: "short" };
  return new Date(value).toLocaleDateString([], options);
}
