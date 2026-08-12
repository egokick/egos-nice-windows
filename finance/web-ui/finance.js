const financeSeriesStorageKey = "finance:visibleSeries:v2";
const legacyFinanceSeriesStorageKey = "finance:visibleSeries";

const financeDayMilliseconds = 24 * 60 * 60 * 1000;
const staleAccountMilliseconds = 3 * financeDayMilliseconds;
const defaultHistoryMonths = 3;
const defaultProjectionDays = 14;
const projectionHorizonMonths = 3;
const maximumProjectionMonths = 24;
const salaryScheduleMatchToleranceDays = 3;
const salaryBalanceMatchToleranceRatio = 0.35;
const taxCountryCodes = "AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS XK YE YT ZA ZM ZW"
  .split(" ");
const usStates = Object.freeze([
  ["AL", "Alabama"], ["AK", "Alaska"], ["AZ", "Arizona"], ["AR", "Arkansas"],
  ["CA", "California"], ["CO", "Colorado"], ["CT", "Connecticut"], ["DE", "Delaware"],
  ["FL", "Florida"], ["GA", "Georgia"], ["HI", "Hawaii"], ["ID", "Idaho"],
  ["IL", "Illinois"], ["IN", "Indiana"], ["IA", "Iowa"], ["KS", "Kansas"],
  ["KY", "Kentucky"], ["LA", "Louisiana"], ["ME", "Maine"], ["MD", "Maryland"],
  ["MA", "Massachusetts"], ["MI", "Michigan"], ["MN", "Minnesota"], ["MS", "Mississippi"],
  ["MO", "Missouri"], ["MT", "Montana"], ["NE", "Nebraska"], ["NV", "Nevada"],
  ["NH", "New Hampshire"], ["NJ", "New Jersey"], ["NM", "New Mexico"], ["NY", "New York"],
  ["NC", "North Carolina"], ["ND", "North Dakota"], ["OH", "Ohio"], ["OK", "Oklahoma"],
  ["OR", "Oregon"], ["PA", "Pennsylvania"], ["RI", "Rhode Island"], ["SC", "South Carolina"],
  ["SD", "South Dakota"], ["TN", "Tennessee"], ["TX", "Texas"], ["UT", "Utah"],
  ["VT", "Vermont"], ["VA", "Virginia"], ["WA", "Washington"], ["WV", "West Virginia"],
  ["WI", "Wisconsin"], ["WY", "Wyoming"], ["DC", "District of Columbia"]
]);
const incomeSources = Object.freeze([
  ["employee_salary", "Employee Salary"],
  ["self_employment", "Self-Employment"],
  ["contract_freelance", "Contract / Freelance"],
  ["business_income", "Business Income"],
  ["investment_income", "Investment Income"],
  ["rental_income", "Rental Income"],
  ["retirement_pension", "Retirement / Pension"],
  ["other", "Other"]
]);
const federalTaxTables = Object.freeze({
  2024: {
    standardDeduction: { single: 14600, married: 29200 },
    brackets: {
      single: [[11600, 0.10], [47150, 0.12], [100525, 0.22], [191950, 0.24], [243725, 0.32], [609350, 0.35], [Infinity, 0.37]],
      married: [[23200, 0.10], [94300, 0.12], [201050, 0.22], [383900, 0.24], [487450, 0.32], [731200, 0.35], [Infinity, 0.37]]
    }
  },
  2025: {
    standardDeduction: { single: 15750, married: 31500 },
    brackets: {
      single: [[11925, 0.10], [48475, 0.12], [103350, 0.22], [197300, 0.24], [250525, 0.32], [626350, 0.35], [Infinity, 0.37]],
      married: [[23850, 0.10], [96950, 0.12], [206700, 0.22], [394600, 0.24], [501050, 0.32], [751600, 0.35], [Infinity, 0.37]]
    }
  },
  2026: {
    standardDeduction: { single: 16100, married: 32200 },
    brackets: {
      single: [[12400, 0.10], [50400, 0.12], [105700, 0.22], [201775, 0.24], [256225, 0.32], [640600, 0.35], [Infinity, 0.37]],
      married: [[24800, 0.10], [100800, 0.12], [211400, 0.22], [403550, 0.24], [512450, 0.32], [768700, 0.35], [Infinity, 0.37]]
    }
  }
});

const financeState = {
  data: null,
  dateRange: {
    minDay: null,
    maxDay: null,
    startDay: null,
    endDay: null,
    initialized: false,
    userAdjusted: false,
    hasData: false
  },
  projection: {
    enabled: false,
    day: null,
    limitDay: null,
    todayDay: null,
    sliderMinDay: null,
    savedRange: null,
    userAdjusted: false,
    autoScrollPending: false
  },
  visibleSeries: readStoredVisibleFinanceSeries(),
  hiddenValueSections: new Set(),
  uiPreferences: null,
  uiPreferencesLoaded: false,
  workflowPollTimer: null,
  uiPreferencesApplied: false,
  selectedTransactionAccountId: null,
  transactionFilters: {
    scopeAll: false,
    query: "",
    advancedOpen: false,
    dateFrom: "",
    dateTo: "",
    description: "",
    merchant: "",
    direction: "",
    amountMin: "",
    amountMax: "",
    currency: "",
    status: "",
    label: "",
    person: "",
    notes: "",
    reference: ""
  },
  transactionMatchIds: []
};

const financeEls = {
  summary: document.querySelector("#financeSummary"),
  alert: document.querySelector("#financeAlert"),
  showCurrencySettings: document.querySelector("#showCurrencySettings"),
  currencySettingsDialog: document.querySelector("#currencySettingsDialog"),
  masterCurrency: document.querySelector("#masterCurrency"),
  currencyRateStatus: document.querySelector("#currencyRateStatus"),
  taxCountry: document.querySelector("#taxCountry"),
  taxStateField: document.querySelector("#taxStateField"),
  taxState: document.querySelector("#taxState"),
  incomeSource: document.querySelector("#incomeSource"),
  maritalStatusToggle: document.querySelector("#maritalStatusToggle"),
  taxProfileStatus: document.querySelector("#taxProfileStatus"),
  refresh: document.querySelector("#refreshFinance"),
  refreshTransactions: document.querySelector("#refreshTransactions"),
  netAfterDebt: document.querySelector("#netAfterDebt"),
  totalCash: document.querySelector("#totalCash"),
  totalCredit: document.querySelector("#totalCredit"),
  privacyToggles: [...document.querySelectorAll(".finance-privacy-toggle")],
  totalDebt: document.querySelector("#totalDebt"),
  netBand: document.querySelector(".net-band"),
  salaryCaption: document.querySelector("#salaryCaption"),
  salarySummary: document.querySelector("#salarySummary"),
  showSalaryPlanForm: document.querySelector("#showSalaryPlanForm"),
  salaryPlanDialog: document.querySelector("#salaryPlanDialog"),
  salaryPlanForm: document.querySelector("#salaryPlanForm"),
  closeSalaryPlanForm: document.querySelector("#closeSalaryPlanForm"),
  cancelSalaryPlanForm: document.querySelector("#cancelSalaryPlanForm"),
  salaryPlanAmount: document.querySelector("#salaryPlanAmount"),
  salaryPlanCurrency: document.querySelector("#salaryPlanCurrency"),
  salaryPlanInterval: document.querySelector("#salaryPlanInterval"),
  salaryPlanNextOn: document.querySelector("#salaryPlanNextOn"),
  addSalaryBonus: document.querySelector("#addSalaryBonus"),
  salaryBonusRows: document.querySelector("#salaryBonusRows"),
  salaryBonusEmpty: document.querySelector("#salaryBonusEmpty"),
  salaryPlanFormStatus: document.querySelector("#salaryPlanFormStatus"),
  saveSalaryPlan: document.querySelector("#saveSalaryPlan"),
  accountSetup: document.querySelector(".account-setup-section"),
  accountFormTitle: document.querySelector("#accountFormTitle"),
  editAccountSelect: document.querySelector("#editAccountSelect"),
  showAccountForm: document.querySelector("#showAccountForm"),
  accountForm: document.querySelector("#accountForm"),
  cancelAccountForm: document.querySelector("#cancelAccountForm"),
  accountFormStatus: document.querySelector("#accountFormStatus"),
  accountUsername: document.querySelector("#accountUsername"),
  accountPassword: document.querySelector("#accountPassword"),
  accountCredentialsStatus: document.querySelector("#accountCredentialsStatus"),
  deleteAccountCredentials: document.querySelector("#deleteAccountCredentials"),
  historyCaption: document.querySelector("#historyCaption"),
  futureProjectionToggle: document.querySelector("#futureProjectionToggle"),
  futureProjectionIndicator: document.querySelector(".projection-toggle-indicator"),
  futureProjectionState: document.querySelector("#futureProjectionState"),
  historyRangeControl: document.querySelector("#historyRangeControl"),
  historyStart: document.querySelector("#historyStart"),
  historyEnd: document.querySelector("#historyEnd"),
  historyProjection: document.querySelector("#historyProjection"),
  historyStartLabel: document.querySelector("#historyStartLabel"),
  historyEndLabel: document.querySelector("#historyEndLabel"),
  historyEndKind: document.querySelector("#historyEndKind"),
  historyProjectionValue: document.querySelector("#historyProjectionValue"),
  historyProjectionLabel: document.querySelector("#historyProjectionLabel"),
  historyRangeLength: document.querySelector("#historyRangeLength"),
  historyRangeSelection: document.querySelector("#historyRangeSelection"),
  historyProjectionSelection: document.querySelector("#historyProjectionSelection"),
  historyMinLabel: document.querySelector("#historyMinLabel"),
  historyTodayLabel: document.querySelector("#historyTodayLabel"),
  historyMaxLabel: document.querySelector("#historyMaxLabel"),
  historyRangeHelp: document.querySelector("#historyRangeHelp"),
  projectionSummary: document.querySelector("#projectionSummary"),
  projectionSummaryDate: document.querySelector("#projectionSummaryDate"),
  projectionSummaryNote: document.querySelector("#projectionSummaryNote"),
  projectionCash: document.querySelector("#projectionCash"),
  projectionCashChange: document.querySelector("#projectionCashChange"),
  projectionNet: document.querySelector("#projectionNet"),
  projectionNetChange: document.querySelector("#projectionNetChange"),
  projectionSalaryCount: document.querySelector("#projectionSalaryCount"),
  projectionSalaryDetail: document.querySelector("#projectionSalaryDetail"),
  chart: document.querySelector("#financeChart"),
  cardCount: document.querySelector("#cardCount"),
  accountCount: document.querySelector("#accountCount"),
  cardRows: document.querySelector("#cardRows"),
  accountRows: document.querySelector("#accountRows"),
  transactionsSection: document.querySelector("#transactionsSection"),
  transactionCaption: document.querySelector("#transactionCaption"),
  transactionCount: document.querySelector("#transactionCount"),
  transactionRows: document.querySelector("#transactionRows"),
  showTransactionForm: document.querySelector("#showTransactionForm"),
  transactionScopeToggle: document.querySelector("#transactionScopeToggle"),
  transactionSearch: document.querySelector("#transactionSearch"),
  transactionAdvancedToggle: document.querySelector("#transactionAdvancedToggle"),
  transactionAdvancedFilters: document.querySelector("#transactionAdvancedFilters"),
  transactionFilterAccount: document.querySelector("#transactionFilterAccount"),
  clearTransactionFilters: document.querySelector("#clearTransactionFilters"),
  transactionMatchCount: document.querySelector("#transactionMatchCount"),
  transactionMoneyOut: document.querySelector("#transactionMoneyOut"),
  transactionMoneyIn: document.querySelector("#transactionMoneyIn"),
  transactionBulkLabelBar: document.querySelector("#transactionBulkLabelBar"),
  transactionBulkLabelCaption: document.querySelector("#transactionBulkLabelCaption"),
  transactionBulkLabel: document.querySelector("#transactionBulkLabel"),
  transactionBulkNewLabel: document.querySelector("#transactionBulkNewLabel"),
  applyTransactionLabelToMatches: document.querySelector("#applyTransactionLabelToMatches"),
  transactionBulkLabelStatus: document.querySelector("#transactionBulkLabelStatus"),
  transactionDialog: document.querySelector("#transactionDialog"),
  transactionForm: document.querySelector("#transactionForm"),
  transactionDialogTitle: document.querySelector("#transactionDialogTitle"),
  closeTransactionForm: document.querySelector("#closeTransactionForm"),
  cancelTransactionForm: document.querySelector("#cancelTransactionForm"),
  saveTransaction: document.querySelector("#saveTransaction"),
  transactionFormStatus: document.querySelector("#transactionFormStatus"),
  transactionAccount: document.querySelector("#transactionAccount"),
  transactionPostedOn: document.querySelector("#transactionPostedOn"),
  transactionTransactedOn: document.querySelector("#transactionTransactedOn"),
  transactionStatus: document.querySelector("#transactionStatus"),
  transactionDescription: document.querySelector("#transactionDescription"),
  transactionMerchant: document.querySelector("#transactionMerchant"),
  transactionDirection: document.querySelector("#transactionDirection"),
  transactionAmount: document.querySelector("#transactionAmount"),
  transactionCurrency: document.querySelector("#transactionCurrency"),
  transactionReference: document.querySelector("#transactionReference"),
  transactionSourceId: document.querySelector("#transactionSourceId"),
  transactionLabelsPicker: document.querySelector("#transactionLabelsPicker"),
  transactionLabelsSummary: document.querySelector("#transactionLabelsSummary"),
  transactionLabelOptions: document.querySelector("#transactionLabelOptions"),
  transactionNewLabel: document.querySelector("#transactionNewLabel"),
  addTransactionLabel: document.querySelector("#addTransactionLabel"),
  transactionPerson: document.querySelector("#transactionPerson"),
  transactionPeople: document.querySelector("#transactionPeople"),
  transactionNotes: document.querySelector("#transactionNotes"),
  recurringTransactionCaption: document.querySelector("#recurringTransactionCaption"),
  recurringTransactionCount: document.querySelector("#recurringTransactionCount"),
  recurringTransactionRows: document.querySelector("#recurringTransactionRows"),
  showRecurringTransactionForm: document.querySelector("#showRecurringTransactionForm"),
  recurringTransactionDialog: document.querySelector("#recurringTransactionDialog"),
  recurringTransactionForm: document.querySelector("#recurringTransactionForm"),
  closeRecurringTransactionForm: document.querySelector("#closeRecurringTransactionForm"),
  cancelRecurringTransactionForm: document.querySelector("#cancelRecurringTransactionForm"),
  recurringAccount: document.querySelector("#recurringAccount"),
  recurringDescription: document.querySelector("#recurringDescription"),
  recurringAmount: document.querySelector("#recurringAmount"),
  recurringCurrency: document.querySelector("#recurringCurrency"),
  recurringNextOn: document.querySelector("#recurringNextOn"),
  recurringTransactionDialogTitle: document.querySelector("#recurringTransactionDialogTitle"),
  saveRecurringTransaction: document.querySelector("#saveRecurringTransaction"),
  recurringTransactionFormStatus: document.querySelector("#recurringTransactionFormStatus"),
  refreshCaption: document.querySelector("#refreshCaption"),
  refreshLog: document.querySelector("#refreshLog"),
  interestPreviewDialog: document.querySelector("#interestPreviewDialog"),
  interestPreviewAccount: document.querySelector("#interestPreviewAccount"),
  interestPreviewBalance: document.querySelector("#interestPreviewBalance"),
  interestPreviewCurrent: document.querySelector("#interestPreviewCurrent"),
  interestPreviewPayment: document.querySelector("#interestPreviewPayment"),
  interestPreviewResult: document.querySelector("#interestPreviewResult"),
  aprEditorDialog: document.querySelector("#aprEditorDialog"),
  aprEditorForm: document.querySelector("#aprEditorForm"),
  aprEditorTitle: document.querySelector("#aprEditorTitle"),
  aprEditorAccount: document.querySelector("#aprEditorAccount"),
  aprEditorEffective: document.querySelector("#aprEditorEffective"),
  aprEditorSchedule: document.querySelector("#aprEditorSchedule"),
  aprEditorRegular: document.querySelector("#aprEditorRegular"),
  aprEditorPromotional: document.querySelector("#aprEditorPromotional"),
  aprEditorEndsOn: document.querySelector("#aprEditorEndsOn"),
  aprEditorStatus: document.querySelector("#aprEditorStatus"),
  closeAprEditor: document.querySelector("#closeAprEditor"),
  cancelAprEditor: document.querySelector("#cancelAprEditor"),
  clearAprPromotion: document.querySelector("#clearAprPromotion"),
  saveAprEditor: document.querySelector("#saveAprEditor")
};

let interestPreview = null;
let editingAprAccount = null;
let historyRenderFrame = null;
let editingRecurringTransactionId = null;
let editingTransactionId = null;
let transactionDraftLabels = new Set();
let transactionLabelChoices = [];
let salaryBonusDraftCounter = 0;
let uiPreferencesSaveTimer = null;
let uiPreferencesSaveChain = Promise.resolve();


function financeUiPreferencesPayload() {
  const range = financeState.dateRange;
  const projection = financeState.projection;
  const storedRange = projection.enabled && projection.savedRange
    ? projection.savedRange
    : range;
  const persistStoredRange = Boolean(storedRange?.userAdjusted);
  return {
    historyStartOn: persistStoredRange && Number.isFinite(storedRange.startDay)
      ? dayIndexToPostedOn(storedRange.startDay)
      : null,
    historyEndOn: persistStoredRange && Number.isFinite(storedRange.endDay)
      ? dayIndexToPostedOn(storedRange.endDay)
      : null,
    projectionEnabled: Boolean(projection.enabled),
    projectionStartOn: projection.enabled && Number.isFinite(range.startDay)
      ? dayIndexToPostedOn(range.startDay)
      : null,
    projectionOn: projection.enabled && Number.isFinite(projection.day)
      ? dayIndexToPostedOn(projection.day)
      : null,
    hiddenValueSections: [...financeState.hiddenValueSections]
  };
}

function queueFinanceUiPreferencesSave() {
  clearTimeout(uiPreferencesSaveTimer);
  uiPreferencesSaveTimer = window.setTimeout(() => {
    uiPreferencesSaveTimer = null;
    void persistFinanceUiPreferences();
  }, 250);
}

function persistFinanceUiPreferences() {
  clearTimeout(uiPreferencesSaveTimer);
  uiPreferencesSaveTimer = null;
  const payload = financeUiPreferencesPayload();
  uiPreferencesSaveChain = uiPreferencesSaveChain
    .catch(() => undefined)
    .then(async () => {
      const saved = await fetchJson("/api/finance/settings/ui-preferences", {
        method: "PUT",
        body: JSON.stringify(payload)
      });
      financeState.uiPreferences = saved;
      return saved;
    })
    .catch(error => {
      console.error("Could not save finance UI preferences.", error);
    });
  return uiPreferencesSaveChain;
}

function applyFinanceValueVisibility() {
  for (const section of document.querySelectorAll("[data-finance-privacy-section]")) {
    const sectionKey = section.dataset.financePrivacySection;
    const hidden = financeState.hiddenValueSections.has(sectionKey);
    const sectionName = section.querySelector(".collapsible-section-title, .net-copy p, .finance-metric > span")?.textContent?.trim() || "section";
    const action = hidden ? `Show ${sectionName} monetary values` : `Hide ${sectionName} monetary values`;
    section.classList.toggle("finance-values-hidden", hidden);
    const toggle = section.querySelector(".finance-privacy-toggle");
    if (toggle) {
      toggle.setAttribute("aria-pressed", String(hidden));
      toggle.setAttribute("aria-label", action);
      toggle.title = action;
    }
    for (const value of section.querySelectorAll(".finance-private-value")) {
      value.setAttribute("aria-hidden", String(hidden));
    }
  }
}

for (const toggle of financeEls.privacyToggles) {
  toggle.addEventListener("click", event => {
    event.preventDefault();
    event.stopPropagation();
    const section = toggle.closest("[data-finance-privacy-section]");
    const sectionKey = section?.dataset.financePrivacySection;
    if (!sectionKey) {
      return;
    }
    if (financeState.hiddenValueSections.has(sectionKey)) {
      financeState.hiddenValueSections.delete(sectionKey);
    } else {
      financeState.hiddenValueSections.add(sectionKey);
    }
    void persistFinanceUiPreferences();
    applyFinanceValueVisibility();
  });
  toggle.addEventListener("keydown", event => event.stopPropagation());
}
for (const control of [
  financeEls.showTransactionForm,
  financeEls.transactionScopeToggle,
  financeEls.transactionSearch,
  financeEls.transactionAdvancedToggle,
  financeEls.transactionAdvancedFilters
]) {
  control.addEventListener("click", event => event.stopPropagation());
  control.addEventListener("keydown", event => event.stopPropagation());
}

financeEls.transactionScopeToggle.addEventListener("click", event => {
  event.preventDefault();
  financeState.transactionFilters.scopeAll = !financeState.transactionFilters.scopeAll;
  financeEls.transactionsSection.open = true;
  renderTransactions(financeState.data);
  renderTables(financeState.data);
});

financeEls.transactionSearch.addEventListener("input", () => {
  financeState.transactionFilters.query = financeEls.transactionSearch.value;
  financeEls.transactionsSection.open = true;
  renderTransactions(financeState.data);
});

financeEls.transactionAdvancedToggle.addEventListener("click", event => {
  event.preventDefault();
  const filters = financeState.transactionFilters;
  filters.advancedOpen = !filters.advancedOpen;
  financeEls.transactionAdvancedFilters.hidden = !filters.advancedOpen;
  financeEls.transactionAdvancedToggle.setAttribute("aria-expanded", String(filters.advancedOpen));
  financeEls.transactionsSection.open = true;
});

function updateTransactionAdvancedFilter(event) {
  const control = event.target.closest("[name]");
  if (!control || !financeEls.transactionAdvancedFilters.contains(control)) return;
  if (event.type === "input" && control.tagName === "SELECT") return;
  if (event.type === "change" && control.tagName !== "SELECT") return;
  if (control.name === "account") {
    if (control.value === "all") {
      financeState.transactionFilters.scopeAll = true;
    } else {
      financeState.transactionFilters.scopeAll = false;
      financeState.selectedTransactionAccountId = control.value;
    }
  } else {
    financeState.transactionFilters[control.name] = control.value;
  }
  renderTransactions(financeState.data);
  renderTables(financeState.data);
}

financeEls.transactionAdvancedFilters.addEventListener("input", updateTransactionAdvancedFilter);
financeEls.transactionAdvancedFilters.addEventListener("change", updateTransactionAdvancedFilter);
financeEls.clearTransactionFilters.addEventListener("click", event => {
  event.stopPropagation();
  const filters = financeState.transactionFilters;
  filters.query = "";
  filters.scopeAll = false;
  for (const name of ["dateFrom", "dateTo", "description", "merchant", "direction", "amountMin", "amountMax", "currency", "status", "label", "person", "notes", "reference"]) {
    filters[name] = "";
  }
  financeEls.transactionSearch.value = "";
  for (const control of financeEls.transactionAdvancedFilters.querySelectorAll("input, select")) {
    if (control.name !== "account") control.value = "";
  }
  renderTransactions(financeState.data);
  renderTables(financeState.data);
});

financeEls.showCurrencySettings.addEventListener("click", () => {
  renderFinanceSettings(financeState.data);
  financeEls.currencySettingsDialog.showModal();
});

financeEls.masterCurrency.addEventListener("change", async () => {
  const currency = financeEls.masterCurrency.value;
  financeEls.masterCurrency.disabled = true;
  financeEls.currencyRateStatus.textContent = `Changing display currency to ${currency}...`;
  try {
    await fetchJson("/api/finance/settings/currency", {
      method: "PUT",
      body: JSON.stringify({ currency })
    });
    await loadFinance();
  } catch (error) {
    financeEls.currencyRateStatus.textContent = `Could not change currency: ${error.message || error}`;
  } finally {
    financeEls.masterCurrency.disabled = false;
  }
});

financeEls.taxCountry.addEventListener("change", () => {
  updateTaxStateVisibility();
  saveTaxProfile();
});
financeEls.taxState.addEventListener("change", saveTaxProfile);
financeEls.incomeSource.addEventListener("change", saveTaxProfile);
financeEls.maritalStatusToggle.addEventListener("click", () => {
  const married = financeEls.maritalStatusToggle.getAttribute("aria-checked") !== "true";
  setMaritalStatusToggle(married);
  saveTaxProfile();
});

financeEls.refresh.addEventListener("click", () => {
  startFinanceWorkflow(
    financeEls.refresh,
    "/api/finance/refresh",
    "account values",
    "The first account's Codex session has opened and the remaining accounts are queued. Each account runs in its own session, one at a time, so it can safely control the active Edge tab."
  );
});

financeEls.refreshTransactions.addEventListener("click", () => {
  startFinanceWorkflow(
    financeEls.refreshTransactions,
    "/api/finance/transactions/refresh",
    "transactions",
    "The first transaction Codex session has opened and the remaining accounts are queued. Each account runs in its own session, one at a time, so it can safely control the active Edge tab."
  );
});

financeEls.showSalaryPlanForm.addEventListener("keydown", event => event.stopPropagation());

financeEls.showSalaryPlanForm.addEventListener("click", event => {
  event.preventDefault();
  event.stopPropagation();
  openSalaryPlanForm();
});

for (const button of [financeEls.closeSalaryPlanForm, financeEls.cancelSalaryPlanForm]) {
  button.addEventListener("click", () => financeEls.salaryPlanDialog.close());
}

financeEls.salaryPlanDialog.addEventListener("close", () => {
  financeEls.salaryPlanForm.reset();
  financeEls.salaryBonusRows.textContent = "";
  financeEls.salaryPlanFormStatus.textContent = "";
  updateSalaryBonusEmptyState();
});

financeEls.addSalaryBonus.addEventListener("click", () => addSalaryBonusRow());

function salaryIntervalLabel(interval) {
  return {
    weekly: "Weekly",
    biweekly: "Every 2 weeks",
    semimonthly: "Twice monthly",
    monthly: "Monthly"
  }[interval] || "Custom";
}

function salaryIntervalFromCadenceDays(days) {
  if (days <= 8) return "weekly";
  if (days <= 16) return days >= 15 ? "semimonthly" : "biweekly";
  if (days <= 24) return "semimonthly";
  return "monthly";
}

function nextSalaryPlanPostedOn(salary) {
  let day = dayIndexFromPostedOn(salary?.nextOn);
  if (day === null) return salary?.nextOn || "";
  const date = new Date(day * financeDayMilliseconds);
  const schedule = {
    interval: salary.interval,
    dayOfMonth: date.getUTCDate()
  };
  const todayDay = dayIndexFromTimestamp(new Date());
  while (day < todayDay) {
    day = nextSalaryOccurrenceDay(day, schedule);
  }
  return dayIndexToPostedOn(day);
}

function openSalaryPlanForm() {
  const data = financeState.data;
  const saved = data?.salaryPlan?.salary || null;
  const todayDay = dayIndexFromTimestamp(new Date());
  const inferred = saved ? null : inferSalarySchedules(
    data?.income?.salaryPayments || [],
    data?.currency || "USD",
    todayDay,
    data?.history || [],
    data?.current
  )[0] || null;

  financeEls.salaryPlanForm.reset();
  financeEls.salaryBonusRows.textContent = "";
  financeEls.salaryPlanFormStatus.textContent = saved
    ? ""
    : inferred
      ? "Pre-filled from recent salary deposits. Saving makes this schedule authoritative for projections."
      : "Set a salary schedule to replace automatic projection inference.";
  populateCurrencySelect(
    financeEls.salaryPlanCurrency,
    saved?.enteredCurrency || saved?.currency || data?.currency || "USD"
  );
  financeEls.salaryPlanAmount.value = saved
    ? String(saved.enteredAmount ?? saved.amount ?? "")
    : inferred
      ? String(centsToNumber(inferred.amountCents))
      : "";
  financeEls.salaryPlanInterval.value = saved?.interval || (inferred ? salaryIntervalFromCadenceDays(inferred.cadenceDays) : "biweekly");
  financeEls.salaryPlanNextOn.value = saved ? nextSalaryPlanPostedOn(saved)
    : (inferred ? dayIndexToPostedOn(inferred.nextDay) : dayIndexToPostedOn(todayDay + 14));

  for (const bonus of data?.salaryPlan?.bonuses || []) {
    addSalaryBonusRow(bonus);
  }
  updateSalaryBonusEmptyState();
  financeEls.salaryPlanDialog.showModal();
  financeEls.salaryPlanAmount.focus();
}

function addSalaryBonusRow(bonus = null) {
  salaryBonusDraftCounter += 1;
  const row = document.createElement("div");
  row.className = "salary-bonus-row";
  row.dataset.bonusId = bonus?.id || `draft-bonus-${salaryBonusDraftCounter}`;

  const description = salaryBonusField("Description", "text", "description", bonus?.description || "Bonus");
  description.querySelector("input").maxLength = 120;
  const amount = salaryBonusField("Amount", "number", "amount", bonus?.enteredAmount ?? bonus?.amount ?? "");
  const amountInput = amount.querySelector("input");
  amountInput.min = "0.01";
  amountInput.step = "0.01";
  amountInput.inputMode = "decimal";
  const currencyField = document.createElement("label");
  currencyField.className = "salary-bonus-field";
  const currencyLabel = document.createElement("span");
  currencyLabel.textContent = "Currency";
  const currency = document.createElement("select");
  currency.dataset.bonusField = "currency";
  populateCurrencySelect(currency, bonus?.enteredCurrency || bonus?.currency || financeState.data?.currency || "USD");
  currencyField.append(currencyLabel, currency);
  const paidOn = salaryBonusField("Payment date", "date", "paidOn", bonus?.paidOn || "");

  const remove = document.createElement("button");
  remove.type = "button";
  remove.className = "salary-bonus-remove";
  remove.textContent = "\u00d7";
  remove.title = "Remove bonus";
  remove.setAttribute("aria-label", `Remove ${bonus?.description || "bonus"}`);
  remove.addEventListener("click", () => {
    row.remove();
    updateSalaryBonusEmptyState();
  });

  row.append(description, amount, currencyField, paidOn, remove);
  financeEls.salaryBonusRows.append(row);
  updateSalaryBonusEmptyState();
}

function salaryBonusField(labelText, type, fieldName, value) {
  const label = document.createElement("label");
  label.className = "salary-bonus-field";
  const caption = document.createElement("span");
  caption.textContent = labelText;
  const input = document.createElement("input");
  input.type = type;
  input.dataset.bonusField = fieldName;
  input.value = value === null || value === undefined ? "" : String(value);
  input.required = true;
  label.append(caption, input);
  return label;
}

function updateSalaryBonusEmptyState() {
  financeEls.salaryBonusEmpty.hidden = financeEls.salaryBonusRows.childElementCount > 0;
}

financeEls.salaryPlanForm.addEventListener("submit", async event => {
  event.preventDefault();
  const bonuses = [...financeEls.salaryBonusRows.querySelectorAll(".salary-bonus-row")].map(row => ({
    id: row.dataset.bonusId,
    description: row.querySelector('[data-bonus-field="description"]').value.trim() || "Bonus",
    amount: Number(row.querySelector('[data-bonus-field="amount"]').value),
    currency: row.querySelector('[data-bonus-field="currency"]').value,
    paidOn: row.querySelector('[data-bonus-field="paidOn"]').value
  }));
  const payload = {
    amount: Number(financeEls.salaryPlanAmount.value),
    currency: financeEls.salaryPlanCurrency.value,
    interval: financeEls.salaryPlanInterval.value,
    nextOn: financeEls.salaryPlanNextOn.value,
    bonuses
  };
  financeEls.saveSalaryPlan.disabled = true;
  financeEls.salaryPlanFormStatus.textContent = "Saving salary projection...";
  try {
    await fetchJson("/api/finance/salary-plan", {
      method: "PUT",
      body: JSON.stringify(payload)
    });
    financeEls.salaryPlanDialog.close();
    await loadFinance();
  } catch (error) {
    financeEls.salaryPlanFormStatus.textContent = `Could not save salary projection: ${error.message || error}`;
  } finally {
    financeEls.saveSalaryPlan.disabled = false;
  }
});

financeEls.showTransactionForm.addEventListener("click", event => {
  event.preventDefault();
  event.stopPropagation();
  openTransactionForm();
});

for (const button of [financeEls.closeTransactionForm, financeEls.cancelTransactionForm]) {
  button.addEventListener("click", () => financeEls.transactionDialog.close());
}

financeEls.transactionDialog.addEventListener("close", () => {
  editingTransactionId = null;
  transactionDraftLabels = new Set();
  financeEls.transactionAccount.disabled = false;
  financeEls.transactionForm.reset();
  financeEls.transactionFormStatus.textContent = "";
});

financeEls.transactionLabelOptions.addEventListener("change", event => {
  const checkbox = event.target.closest('input[type="checkbox"][data-label]');
  if (!checkbox) return;
  setTransactionDraftLabel(checkbox.dataset.label, checkbox.checked);
  updateTransactionLabelsSummary();
});

financeEls.addTransactionLabel.addEventListener("click", () => {
  const label = financeEls.transactionNewLabel.value.trim();
  if (!label) {
    financeEls.transactionNewLabel.focus();
    return;
  }
  setTransactionDraftLabel(label, true);
  if (!transactionLabelChoices.some(choice => choice.toLocaleLowerCase() === label.toLocaleLowerCase())) {
    transactionLabelChoices.push(label);
  }
  financeEls.transactionNewLabel.value = "";
  renderTransactionLabelOptions();
});

financeEls.transactionNewLabel.addEventListener("keydown", event => {
  if (event.key !== "Enter") return;
  event.preventDefault();
  financeEls.addTransactionLabel.click();
});

function transactionLabels(transaction) {
  const values = Array.isArray(transaction?.labels) ? [...transaction.labels] : [];
  if (transaction?.label) values.push(transaction.label);
  const unique = [];
  for (const value of values) {
    const cleaned = String(value || "").trim();
    if (cleaned && !unique.some(label => label.toLocaleLowerCase() === cleaned.toLocaleLowerCase())) {
      unique.push(cleaned);
    }
  }
  return unique;
}

function setTransactionDraftLabel(label, selected) {
  const normalized = String(label).trim();
  const existing = [...transactionDraftLabels].find(value => value.toLocaleLowerCase() === normalized.toLocaleLowerCase());
  const canonical = existing || transactionLabelChoices.find(value => value.toLocaleLowerCase() === normalized.toLocaleLowerCase());
  if (existing) transactionDraftLabels.delete(existing);
  if (selected && normalized) transactionDraftLabels.add(canonical || normalized);
}

function updateTransactionLabelsSummary() {
  const labels = [...transactionDraftLabels];
  financeEls.transactionLabelsSummary.textContent = labels.length === 0
    ? "None"
    : labels.length <= 2
      ? labels.join(", ")
      : `${labels.length} labels selected`;
}

function renderTransactionLabelOptions() {
  const choices = [...new Set([...transactionLabelChoices, ...transactionDraftLabels])]
    .sort((left, right) => left.localeCompare(right));
  financeEls.transactionLabelOptions.textContent = "";
  if (choices.length === 0) {
    const empty = document.createElement("span");
    empty.className = "transaction-label-empty";
    empty.textContent = "No saved labels yet. Add one below.";
    financeEls.transactionLabelOptions.append(empty);
  }
  for (const label of choices) {
    const option = document.createElement("label");
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.dataset.label = label;
    checkbox.checked = [...transactionDraftLabels].some(value => value.toLocaleLowerCase() === label.toLocaleLowerCase());
    option.append(checkbox, document.createTextNode(label));
    financeEls.transactionLabelOptions.append(option);
  }
  updateTransactionLabelsSummary();
}

function openTransactionForm(transaction = null) {
  const data = financeState.data;
  const transactionData = data?.transactions || { accounts: [], records: [] };
  const accounts = transactionData.accounts || [];
  const records = transactionData.records || [];
  editingTransactionId = transaction?.id || null;
  financeEls.transactionForm.reset();
  financeEls.transactionFormStatus.textContent = "";
  financeEls.transactionAccount.textContent = "";
  for (const account of accounts) {
    const option = document.createElement("option");
    option.value = account.accountId;
    option.textContent = `${account.accountName} - ${account.institution}`;
    financeEls.transactionAccount.append(option);
  }
  financeEls.transactionAccount.value = transaction?.accountId || financeState.selectedTransactionAccountId || accounts[0]?.accountId || "";
  financeEls.transactionAccount.disabled = Boolean(transaction);
  populateCurrencySelect(financeEls.transactionCurrency, transaction?.enteredCurrency || transaction?.currency || data?.currency || "USD");

  transactionLabelChoices = records.flatMap(transactionLabels)
    .filter((label, index, values) => values.findIndex(value => value.toLocaleLowerCase() === label.toLocaleLowerCase()) === index);
  transactionDraftLabels = new Set(transactionLabels(transaction));
  renderTransactionLabelOptions();
  financeEls.transactionLabelsPicker.open = false;

  const people = [...new Set(records.map(record => String(record.person || "").trim()).filter(Boolean))]
    .sort((left, right) => left.localeCompare(right));
  financeEls.transactionPeople.textContent = "";
  for (const person of people) {
    const option = document.createElement("option");
    option.value = person;
    financeEls.transactionPeople.append(option);
  }

  financeEls.transactionDialogTitle.textContent = transaction ? "Edit transaction" : "Add transaction";
  financeEls.saveTransaction.textContent = transaction ? "Save changes" : "Add transaction";
  financeEls.transactionPostedOn.value = transaction?.postedOn || dayIndexToPostedOn(dayIndexFromTimestamp(new Date()));
  financeEls.transactionTransactedOn.value = transaction?.transactedOn || "";
  financeEls.transactionStatus.value = transaction?.status || "posted";
  financeEls.transactionDescription.value = transaction?.description || "";
  financeEls.transactionMerchant.value = transaction?.merchant || "";
  financeEls.transactionDirection.value = transaction?.direction || "money_out";
  financeEls.transactionAmount.value = transaction ? String(Math.abs(Number(transaction.enteredAmount ?? transaction.amount ?? 0))) : "";
  financeEls.transactionReference.value = transaction?.reference || "";
  financeEls.transactionSourceId.value = transaction?.sourceTransactionId || "";
  financeEls.transactionPerson.value = transaction?.person || "";
  financeEls.transactionNotes.value = transaction?.notes || "";
  financeEls.transactionDialog.showModal();
  financeEls.transactionDescription.focus();
}

financeEls.transactionForm.addEventListener("submit", async event => {
  event.preventDefault();
  const direction = financeEls.transactionDirection.value;
  const magnitude = Math.abs(Number(financeEls.transactionAmount.value));
  const payload = {
    accountId: financeEls.transactionAccount.value,
    postedOn: financeEls.transactionPostedOn.value,
    transactedOn: financeEls.transactionTransactedOn.value || null,
    amount: direction === "money_out" ? -magnitude : magnitude,
    currency: financeEls.transactionCurrency.value || financeState.data?.currency || "USD",
    direction,
    description: financeEls.transactionDescription.value.trim(),
    merchant: financeEls.transactionMerchant.value.trim() || null,
    status: financeEls.transactionStatus.value,
    reference: financeEls.transactionReference.value.trim() || null,
    sourceTransactionId: financeEls.transactionSourceId.value.trim() || null,
    labels: [...transactionDraftLabels],
    person: financeEls.transactionPerson.value.trim() || null,
    notes: financeEls.transactionNotes.value.trim() || null,
    replaceMetadata: true
  };
  const transactionId = editingTransactionId;
  financeEls.transactionFormStatus.textContent = transactionId ? "Saving transaction..." : "Adding transaction...";
  try {
    await fetchJson(transactionId
      ? `/api/finance/transactions/${encodeURIComponent(transactionId)}`
      : "/api/finance/transactions", {
      method: transactionId ? "PUT" : "POST",
      body: JSON.stringify(payload)
    });
    financeEls.transactionDialog.close();
    await loadFinance();
  } catch (error) {
    financeEls.transactionFormStatus.textContent = `Could not ${transactionId ? "save" : "add"} transaction: ${error.message || error}`;
  }
});

financeEls.applyTransactionLabelToMatches.addEventListener("click", async () => {
  const label = financeEls.transactionBulkNewLabel.value.trim() || financeEls.transactionBulkLabel.value;
  const transactionIds = [...financeState.transactionMatchIds];
  if (!label) {
    financeEls.transactionBulkLabelStatus.textContent = "Choose or enter a label first.";
    financeEls.transactionBulkNewLabel.focus();
    return;
  }
  if (transactionIds.length === 0) return;
  financeEls.applyTransactionLabelToMatches.disabled = true;
  financeEls.transactionBulkLabelStatus.textContent = `Applying ${label} to ${transactionIds.length} matches...`;
  try {
    const result = await fetchJson("/api/finance/transactions/bulk-label", {
      method: "PUT",
      body: JSON.stringify({ transactionIds, label })
    });
    financeEls.transactionBulkNewLabel.value = "";
    await loadFinance();
    financeEls.transactionBulkLabelStatus.textContent = result.updatedCount === 0
      ? `All ${result.requestedCount} matches already had ${result.label}.`
      : `Added ${result.label} to ${result.updatedCount} matching transaction${result.updatedCount === 1 ? "" : "s"}.`;
  } catch (error) {
    financeEls.transactionBulkLabelStatus.textContent = `Could not apply label: ${error.message || error}`;
  } finally {
    financeEls.applyTransactionLabelToMatches.disabled = false;
  }
});

financeEls.showRecurringTransactionForm.addEventListener("click", event => {
  event.preventDefault();
  event.stopPropagation();
  openRecurringTransactionForm();
});

for (const button of [financeEls.closeRecurringTransactionForm, financeEls.cancelRecurringTransactionForm]) {
  button.addEventListener("click", () => financeEls.recurringTransactionDialog.close());
}

financeEls.recurringTransactionDialog.addEventListener("close", () => {
  editingRecurringTransactionId = null;
  financeEls.recurringTransactionForm.reset();
  financeEls.recurringTransactionFormStatus.textContent = "";
});

function openRecurringTransactionForm(recurringTransaction = null) {
  editingRecurringTransactionId = recurringTransaction?.id || null;
  financeEls.recurringTransactionForm.reset();
  populateRecurringAccountSelect();
  populateCurrencySelect(
    financeEls.recurringCurrency,
    recurringTransaction?.enteredCurrency || recurringTransaction?.currency || financeState.data?.currency || "USD"
  );
  financeEls.recurringTransactionFormStatus.textContent = "";
  financeEls.recurringTransactionDialogTitle.textContent = recurringTransaction
    ? `Edit ${recurringTransaction.description}`
    : "Add recurring transaction";
  financeEls.saveRecurringTransaction.textContent = recurringTransaction
    ? "Save changes"
    : "Add recurring transaction";

  if (recurringTransaction) {
    financeEls.recurringAccount.value = recurringTransaction.accountId;
    financeEls.recurringDescription.value = recurringTransaction.description;
    financeEls.recurringAmount.value = String(Math.abs(Number(recurringTransaction.enteredAmount ?? recurringTransaction.amount ?? 0)));
    financeEls.recurringNextOn.value = recurringTransaction.nextOn;
  } else {
    const todayDay = dayIndexFromTimestamp(new Date());
    financeEls.recurringNextOn.value = dayIndexToPostedOn(addCalendarMonthsToDayIndex(todayDay, 1));
  }
  financeEls.recurringTransactionDialog.showModal();
}

financeEls.recurringTransactionForm.addEventListener("submit", async event => {
  event.preventDefault();
  const formData = new FormData(financeEls.recurringTransactionForm);
  const payload = {
    accountId: String(formData.get("accountId") || ""),
    description: String(formData.get("description") || "").trim(),
    amount: Number(formData.get("amount")),
    currency: String(formData.get("currency") || financeState.data?.currency || "USD"),
    nextOn: String(formData.get("nextOn") || "")
  };
  const recurringId = editingRecurringTransactionId;
  financeEls.recurringTransactionFormStatus.textContent = recurringId
    ? "Saving recurring transaction..."
    : "Adding recurring transaction...";
  try {
    await fetchJson(
      recurringId
        ? `/api/finance/recurring-transactions/${encodeURIComponent(recurringId)}`
        : "/api/finance/recurring-transactions",
      {
        method: recurringId ? "PUT" : "POST",
        body: JSON.stringify(payload)
      }
    );
    financeEls.recurringTransactionDialog.close();
    await loadFinance();
  } catch (error) {
    financeEls.recurringTransactionFormStatus.textContent = `Could not ${recurringId ? "save" : "add"} recurring transaction: ${error.message || error}`;
  }
});
async function startFinanceWorkflow(button, endpoint, workflowName, startedMessage) {
  button.disabled = true;
  const originalContent = button.innerHTML;
  button.textContent = "Starting Codex...";
  try {
    const result = await fetchJson(endpoint, { method: "POST" });
    await loadFinance();
    financeEls.alert.hidden = false;
    financeEls.alert.className = `poll-alert ${result.started || result.alreadyRunning ? "poll-alert-warning" : "poll-alert-failed"}`;
    financeEls.alert.textContent = result.started
      ? result.message || startedMessage
      : result.alreadyRunning
        ? "A Codex finance workflow is already running. Check its open Codex terminal window for progress."
        : result.message || result.error || `Codex ${workflowName} refresh could not be started.`;
  } catch (error) {
    financeEls.alert.hidden = false;
    financeEls.alert.className = "poll-alert poll-alert-failed";
    financeEls.alert.textContent = `Codex ${workflowName} refresh could not be started: ${error.message || error}`;
  } finally {
    button.disabled = false;
    button.innerHTML = originalContent;
  }
}
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
  const numericFields = new Set([
    "cashBalance",
    "balanceOwed",
    "creditLimit",
    "creditAvailable",
    "aprPercent",
    "minimumPayment"
  ]);
  const payload = Object.fromEntries([...formData.entries()].map(([key, value]) => {
    const cleaned = String(value).trim();
    return [key, numericFields.has(key) ? (cleaned === "" ? null : Number(cleaned)) : cleaned || null];
  }));
  payload.minimumPaymentMet = payload.minimumPaymentMet === null ? null : payload.minimumPaymentMet === "true";
  const accountId = payload.id;
  delete payload.id;
  const username = financeEls.accountUsername.value.trim();
  const password = financeEls.accountPassword.value;
  const replaceCredentials = username !== "" || password !== "";
  if (replaceCredentials && (username === "" || password === "")) {
    financeEls.accountFormStatus.textContent = "Enter both username and password to replace saved credentials.";
    return;
  }

  financeEls.accountFormStatus.textContent = accountId ? "Saving account..." : "Adding account...";
  let savedAccount = null;
  let credentialsSaved = !replaceCredentials;
  try {
    savedAccount = await fetchJson(accountId ? `/api/finance/accounts/${encodeURIComponent(accountId)}` : "/api/finance/accounts", {
      method: accountId ? "PUT" : "POST",
      body: JSON.stringify(payload)
    });
    document.querySelector("#accountId").value = savedAccount.id;
    financeEls.accountFormTitle.textContent = `Edit ${savedAccount.name || payload.name}`;
    if (replaceCredentials) {
      financeEls.accountFormStatus.textContent = "Account saved. Securing credentials in Windows Credential Manager...";
      await fetchJson(`/api/finance/accounts/${encodeURIComponent(savedAccount.id)}/credentials`, {
        method: "PUT",
        body: JSON.stringify({ username, password })
      });
      credentialsSaved = true;
    }
    financeEls.accountUsername.value = "";
    financeEls.accountPassword.value = "";
    await loadFinance();
    hideAccountForm();
  } catch (error) {
    financeEls.accountPassword.value = "";
    if (savedAccount && !credentialsSaved) {
      financeEls.accountFormStatus.textContent = `Account saved, but credentials were not: ${error.message || error}. Re-enter the password to retry without creating a duplicate account.`;
      financeEls.accountCredentialsStatus.textContent = "The account exists, but its credentials still need to be saved.";
    } else if (savedAccount) {
      financeEls.accountFormStatus.textContent = `Account saved, but the dashboard could not reload: ${error.message || error}`;
    } else {
      financeEls.accountFormStatus.textContent = `Could not save account: ${error.message || error}`;
    }
  }
});

financeEls.deleteAccountCredentials.addEventListener("click", async () => {
  const accountId = document.querySelector("#accountId").value;
  if (!accountId || !window.confirm("Remove the saved username and password for this account?")) {
    return;
  }

  financeEls.deleteAccountCredentials.disabled = true;
  financeEls.accountCredentialsStatus.textContent = "Removing saved credentials...";
  try {
    await fetchJson(`/api/finance/accounts/${encodeURIComponent(accountId)}/credentials`, { method: "DELETE" });
    financeEls.accountUsername.value = "";
    financeEls.accountPassword.value = "";
    financeEls.deleteAccountCredentials.hidden = true;
    financeEls.accountCredentialsStatus.textContent = "No credentials are saved for this account.";
    await loadFinance();
  } catch (error) {
    financeEls.accountCredentialsStatus.textContent = `Could not remove saved credentials: ${error.message || error}`;
  } finally {
    financeEls.deleteAccountCredentials.disabled = false;
  }
});

for (const [input, handle] of [[financeEls.historyStart, "start"], [financeEls.historyEnd, "end"]]) {
  input.addEventListener("input", () => {
    updateHistoryRangeFromInput(handle);
    queueFinanceUiPreferencesSave();
  });
  input.addEventListener("change", () => void persistFinanceUiPreferences());
  input.addEventListener("pointerdown", () => setActiveHistoryHandle(input));
  input.addEventListener("focus", () => setActiveHistoryHandle(input));
}

financeEls.futureProjectionToggle.addEventListener("click", toggleFutureProjection);
financeEls.historyProjection.addEventListener("input", () => {
  updateProjectionFromInput();
  queueFinanceUiPreferencesSave();
});
financeEls.historyProjection.addEventListener("change", () => void persistFinanceUiPreferences());
financeEls.historyProjection.addEventListener("pointerdown", () => setActiveHistoryHandle(financeEls.historyProjection));
financeEls.historyProjection.addEventListener("focus", () => setActiveHistoryHandle(financeEls.historyProjection));

financeEls.interestPreviewPayment.addEventListener("input", renderInterestPreview);
financeEls.interestPreviewDialog.addEventListener("close", () => {
  interestPreview = null;
});
for (const input of [financeEls.aprEditorRegular, financeEls.aprEditorPromotional, financeEls.aprEditorEndsOn]) {
  input.addEventListener("input", () => {
    setAprEditorStatus("");
    renderAprEditorPreview();
  });
}
financeEls.aprEditorForm.addEventListener("submit", event => {
  event.preventDefault();
  void saveAprEditor(false);
});
financeEls.clearAprPromotion.addEventListener("click", () => void saveAprEditor(true));
financeEls.closeAprEditor.addEventListener("click", () => financeEls.aprEditorDialog.close());
financeEls.cancelAprEditor.addEventListener("click", () => financeEls.aprEditorDialog.close());
financeEls.aprEditorDialog.addEventListener("close", () => {
  editingAprAccount = null;
  financeEls.aprEditorForm.reset();
  setAprEditorStatus("");
});

applyFinanceValueVisibility();
loadFinance();
setInterval(loadFinance, 60000);

function readStoredVisibleFinanceSeries() {
  try {
    const parsed = JSON.parse(localStorage.getItem(financeSeriesStorageKey) || "null");
    if (Array.isArray(parsed) && parsed.length > 0) {
      return new Set(parsed);
    }

    const legacy = JSON.parse(localStorage.getItem(legacyFinanceSeriesStorageKey) || "null");
    if (Array.isArray(legacy) && legacy.length > 0) {
      return new Set([...legacy, "salary"]);
    }
  } catch {
    // Ignore corrupt local preferences and fall back to all series.
  }

  return new Set(["netAfterDebt", "totalCash", "totalDebt", "totalCreditAvailable", "salary"]);
}

function persistVisibleFinanceSeries() {
  localStorage.setItem(financeSeriesStorageKey, JSON.stringify([...financeState.visibleSeries]));
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

function toggleFutureProjection() {
  if (!financeState.data) {
    return;
  }

  const range = syncHistoryDateRange(financeState.data);
  const projection = financeState.projection;
  if (!projection.enabled) {
    const todayDay = dayIndexFromTimestamp(financeState.data.nowUtc) ?? range.maxDay;
    projection.enabled = true;
    projection.todayDay = todayDay;
    projection.day = defaultProjectionTargetDay(financeState.data, todayDay);
    projection.limitDay = addCalendarMonthsToDayIndex(todayDay, projectionHorizonMonths);
    projection.sliderMinDay = Math.min(range.minDay, todayDay);
    projection.savedRange = {
      startDay: range.startDay,
      endDay: range.endDay,
      userAdjusted: range.userAdjusted
    };
    projection.userAdjusted = false;
    projection.autoScrollPending = true;
    range.startDay = projection.sliderMinDay;
    range.endDay = todayDay;
  } else {
    const savedRange = projection.savedRange;
    projection.enabled = false;
    if (savedRange) {
      range.startDay = clampNumber(savedRange.startDay, range.minDay, range.maxDay);
      range.endDay = clampNumber(savedRange.endDay, range.startDay, range.maxDay);
      range.userAdjusted = savedRange.userAdjusted;
    }
    Object.assign(projection, {
      day: null,
      limitDay: null,
      todayDay: null,
      sliderMinDay: null,
      savedRange: null,
      userAdjusted: false,
      autoScrollPending: false
    });
  }

  updateHistoryRangeControl();
  renderChart(financeState.data);
  void persistFinanceUiPreferences();
}

function updateProjectionFromInput() {
  const projection = financeState.projection;
  if (!projection.enabled || projection.todayDay === null) {
    return;
  }

  const maximumDay = addCalendarMonthsToDayIndex(projection.todayDay, maximumProjectionMonths);
  const selectedDay = clampNumber(
    Math.round(Number(financeEls.historyProjection.value)),
    projection.todayDay + 1,
    projection.limitDay
  );
  projection.day = selectedDay;
  projection.userAdjusted = true;
  projection.autoScrollPending = true;
  if (selectedDay >= projection.limitDay && projection.limitDay < maximumDay) {
    projection.limitDay = Math.min(
      addCalendarMonthsToDayIndex(projection.limitDay, projectionHorizonMonths),
      maximumDay
    );
  }

  updateHistoryRangeControl();
  scheduleHistoryChartRender();
}

function syncProjectionRange(todayDay, range) {
  const projection = financeState.projection;
  if (!projection.enabled || todayDay === null) {
    return;
  }

  if (projection.todayDay !== todayDay) {
    if (!projection.userAdjusted) {
      projection.day = defaultProjectionTargetDay(financeState.data, todayDay);
    } else {
      projection.day = Math.max(todayDay + 1, projection.day ?? todayDay + defaultProjectionDays);
    }
    projection.todayDay = todayDay;
    projection.limitDay = addCalendarMonthsToDayIndex(todayDay, projectionHorizonMonths);
  }

  if (!projection.userAdjusted) {
    projection.day = defaultProjectionTargetDay(financeState.data, todayDay);
  }
  projection.sliderMinDay = Math.min(range.minDay, todayDay);
  projection.day = Math.max(todayDay + 1, projection.day ?? todayDay + defaultProjectionDays);
  const maximumDay = addCalendarMonthsToDayIndex(todayDay, maximumProjectionMonths);
  while (projection.limitDay < projection.day && projection.limitDay < maximumDay) {
    projection.limitDay = Math.min(
      addCalendarMonthsToDayIndex(projection.limitDay, projectionHorizonMonths),
      maximumDay
    );
  }
  projection.day = Math.min(projection.day, maximumDay);
  range.startDay = clampNumber(range.startDay, projection.sliderMinDay, todayDay);
  range.endDay = todayDay;
}

function defaultProjectionTargetDay(data, todayDay) {
  const maximumDay = addCalendarMonthsToDayIndex(todayDay, maximumProjectionMonths);
  const nextRecurringDay = (data?.recurringTransactions?.records || [])
    .filter(item => item.status === "approved")
    .map(item => dayIndexFromPostedOn(item.nextOn))
    .filter(day => day !== null && day > todayDay && day <= maximumDay)
    .sort((left, right) => left - right)[0];
  const recurringTarget = nextRecurringDay === undefined ? todayDay : nextRecurringDay + 1;
  return Math.min(maximumDay, Math.max(todayDay + defaultProjectionDays, recurringTarget));
}
function hideAccountForm() {
  financeEls.accountForm.reset();
  financeEls.accountUsername.value = "";
  financeEls.accountPassword.value = "";
  financeEls.deleteAccountCredentials.hidden = true;
  financeEls.accountFormTitle.textContent = "Add Account";
  financeEls.accountForm.hidden = true;
  financeEls.accountSetup.hidden = true;
  financeEls.showAccountForm.hidden = false;
  financeEls.editAccountSelect.value = "";
}

function showAddAccountForm() {
  financeEls.accountForm.reset();
  document.querySelector("#accountId").value = "";
  populateCurrencySelect(document.querySelector("#accountCurrency"), "USD");
  financeEls.accountFormTitle.textContent = "Add Account";
  financeEls.accountSetup.hidden = false;
  financeEls.accountForm.hidden = false;
  financeEls.showAccountForm.hidden = true;
  financeEls.accountFormStatus.textContent = "Values are stored locally. Website refreshes require a Codex-assisted session.";
  financeEls.accountCredentialsStatus.textContent = "Credentials will be saved separately in Windows Credential Manager.";
  financeEls.deleteAccountCredentials.hidden = true;
  document.querySelector("#accountName").focus();
}

function showEditAccountForm(account) {
  financeEls.accountForm.reset();
  document.querySelector("#accountId").value = account.id;
  document.querySelector("#accountName").value = account.name || "";
  document.querySelector("#accountKind").value = account.kind || "credit_card";
  populateCurrencySelect(document.querySelector("#accountCurrency"), account.currency || "USD");
  document.querySelector("#accountInstitution").value = account.institution || "";
  document.querySelector("#accountLoginUrl").value = account.loginUrl || "";
  financeEls.accountUsername.value = "";
  financeEls.accountPassword.value = "";
  document.querySelector("#accountCashBalance").value = account.cashBalance ?? "";
  document.querySelector("#accountBalanceOwed").value = account.balanceOwed ?? "";
  document.querySelector("#accountMinimumPayment").value = account.minimumPayment ?? "";
  document.querySelector("#accountPaymentDueDate").value = account.paymentDueDate || "";
  document.querySelector("#accountMinimumPaymentMet").value = account.minimumPaymentMet === null || account.minimumPaymentMet === undefined
    ? ""
    : String(account.minimumPaymentMet);
  document.querySelector("#accountCreditLimit").value = account.creditLimit ?? "";
  document.querySelector("#accountCreditAvailable").value = account.creditAvailable ?? "";
  document.querySelector("#accountAprPercent").value = account.aprPercent ?? "";
  document.querySelector("#accountCollectorNotes").value = account.collectorNotes || "";
  financeEls.accountFormTitle.textContent = `Edit ${account.name}`;
  financeEls.accountSetup.hidden = false;
  financeEls.accountForm.hidden = false;
  financeEls.showAccountForm.hidden = true;
  financeEls.accountFormStatus.textContent = "Account values and connection details can be updated independently of saved credentials.";
  financeEls.accountCredentialsStatus.textContent = account.credentialsConfigured
    ? "Credentials are saved in Windows Credential Manager. Enter both fields to replace them."
    : "No credentials are saved for this account. Enter both fields to add them.";
  financeEls.deleteAccountCredentials.hidden = !account.credentialsConfigured;
  document.querySelector("#accountName").focus();
}

async function loadFinance() {
  const shouldLoadPreferences = !financeState.uiPreferencesLoaded;
  const [data, preferences, codexRefresh] = await Promise.all([
    fetchJson("/api/finance/state"),
    shouldLoadPreferences
      ? fetchJson("/api/finance/settings/ui-preferences")
      : Promise.resolve(null),
    fetchJson("/api/finance/codex-refresh/status")
  ]);
  data.codexRefresh = codexRefresh;
  financeState.data = data;
  if (shouldLoadPreferences && preferences) {
    financeState.uiPreferences = preferences;
    financeState.uiPreferencesLoaded = true;
    financeState.hiddenValueSections = new Set(preferences.hiddenValueSections || []);
  }
  renderFinance();
  syncFinanceWorkflowPolling(codexRefresh);
}

function syncFinanceWorkflowPolling(codexRefresh) {
  if (!codexRefresh?.isRunning) {
    if (financeState.workflowPollTimer !== null) {
      clearTimeout(financeState.workflowPollTimer);
      financeState.workflowPollTimer = null;
    }
    return;
  }

  if (financeState.workflowPollTimer !== null) return;
  financeState.workflowPollTimer = setTimeout(async () => {
    financeState.workflowPollTimer = null;
    try {
      await loadFinance();
    } catch (error) {
      financeEls.alert.textContent = `Could not refresh Codex run status: ${error.message || error}`;
      syncFinanceWorkflowPolling({ isRunning: true });
    }
  }, 3000);
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
  renderFinanceSettings(data);

  financeEls.netAfterDebt.textContent = money(current.netAfterDebt, data.currency);
  financeEls.totalCash.textContent = money(current.totalCash, data.currency);
  financeEls.totalCredit.textContent = money(current.totalCreditAvailable, data.currency);
  financeEls.totalDebt.textContent = money(current.totalDebt, data.currency);
  financeEls.netBand.classList.toggle("positive", current.netAfterDebt > 0);
  financeEls.netBand.classList.toggle("negative", current.netAfterDebt < 0);

  renderChart(data);
  renderAccountSelector(data);
  renderRecurringTransactions(data);
  renderTransactions(data);
  renderTables(data);
  renderLog(data.refreshLog || []);
  applyFinanceValueVisibility();
  document.title = "Finance";
}

function renderSalary(payments, data, startDay, endDay) {
  const defaultCurrency = data.currency;
  const rangeDescription = describeHistoryDayRange(startDay, endDay);
  const rangeLabel = formatHistoryDateRange(startDay, endDay);
  const grouped = new Map();

  for (const point of payments) {
    const payment = point.payment || {};
    const currency = payment.currency || defaultCurrency;
    const key = `${payment.accountId || payment.accountName || "salary"}:${currency}`;
    if (!grouped.has(key)) {
      grouped.set(key, {
        accountId: payment.accountId || null,
        accountName: payment.accountName || "Salary",
        currency,
        points: []
      });
    }
    grouped.get(key).points.push(point);
  }

  const sources = [...grouped.values()]
    .map(source => ({
      ...source,
      points: source.points.sort((left, right) => left.day - right.day)
    }))
    .sort((left, right) => {
      const leftDay = left.points[left.points.length - 1]?.day ?? Number.NEGATIVE_INFINITY;
      const rightDay = right.points[right.points.length - 1]?.day ?? Number.NEGATIVE_INFINITY;
      return rightDay - leftDay || left.accountName.localeCompare(right.accountName);
    });
  const paymentCount = sources.reduce((total, source) => total + source.points.length, 0);

  financeEls.salarySummary.textContent = "";
  const configuredSalary = data.salaryPlan?.salary || null;
  if (configuredSalary) {
    financeEls.salarySummary.append(salaryPlanOverviewCard(data.salaryPlan, data.currency));
  }
  financeEls.salaryCaption.textContent = paymentCount === 0
    ? configuredSalary
      ? `${money(configuredSalary.amount, data.currency)} ${salaryIntervalLabel(configuredSalary.interval).toLowerCase()} \u00b7 next ${formatPostedOn(configuredSalary.nextOn)}`
      : `No salary payments shown in ${rangeLabel}`
    : `${paymentCount} salary or bonus payment${paymentCount === 1 ? "" : "s"} shown from ${sources.length} source${sources.length === 1 ? "" : "s"} in ${rangeLabel}`;

  if (paymentCount === 0) {
    const empty = document.createElement("p");
    empty.className = "empty-state salary-empty";
    empty.textContent = "No salary payments are displayed for the selected graph range.";
    financeEls.salarySummary.append(empty);
    return;
  }

  const taxEstimate = isSalaryTaxProfileActive(data.taxProfile)
    ? estimateSalaryIncomeTax(
        data.income?.salaryPayments || [],
        payments,
        data.currency,
        startDay,
        endDay,
        data.taxProfile)
    : null;
  if (taxEstimate) {
    financeEls.salarySummary.append(salaryTaxOverviewCard(
      taxEstimate,
      data.taxProfile,
      data.currency,
      rangeLabel));
  }

  for (const source of sources) {
    const card = document.createElement("article");
    card.className = "salary-card";

    const header = document.createElement("div");
    header.className = "salary-card-header";
    const account = document.createElement("strong");
    account.textContent = source.accountName;
    const latestPoint = source.points[source.points.length - 1];
    const date = document.createElement("span");
    date.textContent = `Latest shown ${formatPostedOn(latestPoint.payment.postedOn)}`;
    header.append(account, date);

    const amounts = source.points.map(point => point.amount).sort((left, right) => left - right);
    const middle = Math.floor(amounts.length / 2);
    const median = amounts.length % 2 === 1
      ? amounts[middle]
      : (amounts[middle - 1] + amounts[middle]) / 2;
    const total = source.points.reduce((sum, point) => sum + point.amount, 0);

    const values = document.createElement("div");
    values.className = "salary-card-values";
    values.append(
      salaryValue(`Income shown, last ${rangeDescription}`, money(total, source.currency), "salary-total-value"),
      salaryValue("Median income payment", money(median, source.currency)),
      salaryValue("Payments shown", String(source.points.length))
    );

    card.append(header, values);
    financeEls.salarySummary.append(card);
  }
}

function salaryTaxOverviewCard(estimate, profile, currency, rangeLabel) {
  const card = document.createElement("article");
  card.className = "salary-card salary-tax-overview";
  const header = document.createElement("div");
  header.className = "salary-card-header";
  const title = document.createElement("strong");
  title.textContent = "Estimated taxes - all salary sources";
  const range = document.createElement("span");
  range.textContent = rangeLabel;
  header.append(title, range);

  const values = document.createElement("div");
  values.className = "salary-card-values";
  values.append(
    salaryValue("Est. federal income tax", money(estimate.federalTax, currency), "salary-tax-value"),
    salaryValue("Extra kept because married", money(estimate.marriageSavings, currency), "salary-savings-value"),
    salaryValue(
      profile.stateCode === "TX" ? "Texas income tax (0%)" : `${stateName(profile.stateCode)} income tax`,
      profile.stateCode === "TX" ? money(0, currency) : "Not estimated",
      "salary-state-tax-value")
  );
  const note = document.createElement("p");
  note.className = "salary-tax-note";
  note.textContent = `One combined estimate using IRS brackets and one standard deduction across every salary source from ${formatMonthYear(profile.salaryStartOn)}. Recorded salary is used as a taxable-pay proxy; federal income tax only, excluding FICA, credits, other deductions, and spouse income${estimate.includesProjection ? "; projected pay is included once" : ""}.`;
  card.append(header, values, note);
  return card;
}

function salaryPlanOverviewCard(plan, currency) {
  const salary = plan.salary;
  const bonuses = plan.bonuses || [];
  const card = document.createElement("article");
  card.className = "salary-card salary-plan-overview";
  const header = document.createElement("div");
  header.className = "salary-card-header";
  const title = document.createElement("strong");
  title.textContent = "Configured projection";
  const next = document.createElement("span");
  next.textContent = `Next payday ${formatPostedOn(nextSalaryPlanPostedOn(salary))}`;
  header.append(title, next);
  const values = document.createElement("div");
  values.className = "salary-card-values";
  values.append(
    salaryValue("Take-home payment", money(salary.amount, currency), "salary-total-value"),
    salaryValue("Pay interval", salaryIntervalLabel(salary.interval)),
    salaryValue("One-time bonuses", String(bonuses.length))
  );
  card.append(header, values);
  if (bonuses.length > 0) {
    const bonusList = document.createElement("p");
    bonusList.className = "salary-tax-note salary-plan-bonus-note";
    bonusList.textContent = bonuses
      .map(bonus => `${formatPostedOn(bonus.paidOn)} ${bonus.description}: ${money(bonus.amount, currency)}`)
      .join(" \u00b7 ");
    card.append(bonusList);
  }
  return card;
}

function salaryValue(label, value, className = "") {
  const item = document.createElement("div");
  if (className) {
    item.className = className;
  }
  const caption = document.createElement("span");
  caption.textContent = label;
  const amount = document.createElement("strong");
  amount.classList.add("finance-private-value");
  amount.textContent = value;
  item.append(caption, amount);
  return item;
}

function isSalaryTaxProfileActive(profile) {
  return profile?.countryCode === "US"
    && profile?.incomeSource === "employee_salary"
    && profile?.married === true;
}

function estimateSalaryIncomeTax(
  recordedPayments,
  visiblePoints,
  currency,
  startDay,
  endDay,
  profile) {
  const salaryStartDay = dayIndexFromPostedOn(profile.salaryStartOn) ?? dayIndexFromPostedOn("2024-12-01");
  const uniquePayments = new Map();
  for (const payment of recordedPayments.filter(payment => payment.kind === "salary" && payment.currency === currency)) {
    const postedDay = dayIndexFromPostedOn(payment.postedOn);
    const key = `recorded:${payment.id || `${payment.accountId || "salary"}:${payment.postedOn}:${payment.amount}`}`;
    if (!uniquePayments.has(key)) {
      uniquePayments.set(key, {
        amount: Number(payment.amount || 0),
        chartDay: salaryDayFromPostedOn(payment.postedOn),
        taxYear: postedDay === null ? null : dayIndexToLocalDate(postedDay).getFullYear(),
        projected: false
      });
    }
  }
  for (const point of visiblePoints.filter(point => point.projected)) {
    const payment = point.payment || {};
    if ((payment.currency || currency) !== currency || !["salary", "bonus"].includes(payment.kind || "salary")) {
      continue;
    }
    const key = `projected:${payment.id || `${payment.accountId || payment.accountName || "salary"}:${point.day}:${point.amount}`}`;
    if (!uniquePayments.has(key)) {
      uniquePayments.set(key, {
        amount: Number(point.amount || 0),
        chartDay: point.day,
        taxYear: point.day === null ? null : dayIndexToLocalDate(point.day).getFullYear(),
        projected: true
      });
    }
  }

  const payments = [...uniquePayments.values()];
  const yearlyIncome = new Map();
  let federalTax = 0;
  let singleTax = 0;
  let includesProjection = false;
  for (const payment of payments
    .filter(payment => Number.isFinite(payment.amount)
      && payment.amount > 0
      && payment.chartDay !== null
      && payment.taxYear !== null
      && payment.chartDay >= salaryStartDay
      && payment.chartDay <= endDay)
    .sort((left, right) => left.chartDay - right.chartDay)) {
    const priorIncome = yearlyIncome.get(payment.taxYear) || 0;
    const currentIncome = priorIncome + payment.amount;
    yearlyIncome.set(payment.taxYear, currentIncome);

    const marriedIncrement = federalIncomeTax(currentIncome, payment.taxYear, "married")
      - federalIncomeTax(priorIncome, payment.taxYear, "married");
    const singleIncrement = federalIncomeTax(currentIncome, payment.taxYear, "single")
      - federalIncomeTax(priorIncome, payment.taxYear, "single");
    if (payment.chartDay >= startDay) {
      federalTax += Math.max(0, marriedIncrement);
      singleTax += Math.max(0, singleIncrement);
      includesProjection ||= payment.projected;
    }
  }

  return {
    federalTax: roundCurrency(federalTax),
    marriageSavings: roundCurrency(Math.max(0, singleTax - federalTax)),
    includesProjection
  };
}

function federalIncomeTax(income, taxYear, filingStatus) {
  const table = federalTaxTableForYear(taxYear);
  const taxableIncome = Math.max(0, income - table.standardDeduction[filingStatus]);
  let tax = 0;
  let lowerBound = 0;
  for (const [upperBound, rate] of table.brackets[filingStatus]) {
    const taxableAtRate = Math.max(0, Math.min(taxableIncome, upperBound) - lowerBound);
    tax += taxableAtRate * rate;
    if (taxableIncome <= upperBound) {
      break;
    }
    lowerBound = upperBound;
  }
  return tax;
}

function federalTaxTableForYear(taxYear) {
  if (federalTaxTables[taxYear]) {
    return federalTaxTables[taxYear];
  }
  return taxYear < 2024 ? federalTaxTables[2024] : federalTaxTables[2026];
}

function roundCurrency(value) {
  return Math.round((value + Number.EPSILON) * 100) / 100;
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

function renderFinanceSettings(data) {
  const settings = data?.currencySettings;
  const profile = data?.taxProfile;
  if (!settings || !profile) return;

  populateCurrencySelect(financeEls.masterCurrency, settings.masterCurrency);
  populateCountrySelect(financeEls.taxCountry, profile.countryCode);
  populateStateSelect(financeEls.taxState, profile.stateCode || "TX");
  populateIncomeSourceSelect(financeEls.incomeSource, profile.incomeSource);
  setMaritalStatusToggle(profile.married);
  updateTaxStateVisibility();

  const accountCurrency = document.querySelector("#accountCurrency");
  if (accountCurrency && accountCurrency.options.length === 0) {
    populateCurrencySelect(accountCurrency, "USD");
  }

  const location = profile.countryCode === "US"
    ? `${countryName(profile.countryCode)}, ${stateName(profile.stateCode)}`
    : countryName(profile.countryCode);
  financeEls.taxProfileStatus.textContent =
    `Saved locally \u00b7 ${location} \u00b7 ${incomeSourceName(profile.incomeSource)} \u00b7 ${profile.married ? "Married" : "Unmarried"} \u00b7 estimates from ${formatMonthYear(profile.salaryStartOn)}.`;

  const updated = settings.ratesLastUpdatedUtc ? formatDateTime(settings.ratesLastUpdatedUtc) : "an unknown time";
  financeEls.currencyRateStatus.textContent = settings.lastRefreshSucceeded
    ? `Rates updated ${updated} and cached locally for offline fallback.`
    : settings.hasCachedRates
      ? `Latest refresh failed; using cached rates from ${updated}. ${settings.lastRefreshError || ""}`.trim()
      : `Exchange rates are unavailable, so foreign values cannot be converted yet. ${settings.lastRefreshError || ""}`.trim();
}

async function saveTaxProfile() {
  const payload = {
    countryCode: financeEls.taxCountry.value,
    stateCode: financeEls.taxCountry.value === "US" ? financeEls.taxState.value : null,
    incomeSource: financeEls.incomeSource.value,
    married: financeEls.maritalStatusToggle.getAttribute("aria-checked") === "true"
  };
  setTaxProfileControlsDisabled(true);
  financeEls.taxProfileStatus.textContent = "Saving tax profile...";
  try {
    const profile = await fetchJson("/api/finance/settings/tax-profile", {
      method: "PUT",
      body: JSON.stringify(payload)
    });
    financeState.data.taxProfile = profile;
    renderFinanceSettings(financeState.data);
    renderChart(financeState.data);
  } catch (error) {
    renderFinanceSettings(financeState.data);
    financeEls.taxProfileStatus.textContent = `Could not save tax profile: ${error.message || error}`;
  } finally {
    setTaxProfileControlsDisabled(false);
  }
}

function setTaxProfileControlsDisabled(disabled) {
  financeEls.taxCountry.disabled = disabled;
  financeEls.taxState.disabled = disabled || financeEls.taxCountry.value !== "US";
  financeEls.incomeSource.disabled = disabled;
  financeEls.maritalStatusToggle.disabled = disabled;
}

function updateTaxStateVisibility() {
  const showState = financeEls.taxCountry.value === "US";
  financeEls.taxStateField.hidden = !showState;
  financeEls.taxState.disabled = !showState;
  if (showState && !financeEls.taxState.value) {
    financeEls.taxState.value = "TX";
  }
}

function setMaritalStatusToggle(married) {
  financeEls.maritalStatusToggle.setAttribute("aria-checked", String(Boolean(married)));
  financeEls.maritalStatusToggle.setAttribute("aria-label", `Marital status: ${married ? "married" : "unmarried"}`);
}

function populateCountrySelect(select, selectedCountry) {
  const selected = selectedCountry || "US";
  const countries = taxCountryCodes
    .map(code => ({ code, name: countryName(code) }))
    .sort((left, right) => {
      if (left.code === "US") return -1;
      if (right.code === "US") return 1;
      return left.name.localeCompare(right.name);
    });
  select.textContent = "";
  for (const country of countries) {
    const option = document.createElement("option");
    option.value = country.code;
    option.textContent = country.name;
    select.append(option);
  }
  select.value = taxCountryCodes.includes(selected) ? selected : "US";
}

function populateStateSelect(select, selectedState) {
  select.textContent = "";
  for (const [code, name] of usStates) {
    const option = document.createElement("option");
    option.value = code;
    option.textContent = name;
    select.append(option);
  }
  select.value = usStates.some(([code]) => code === selectedState) ? selectedState : "TX";
}

function populateIncomeSourceSelect(select, selectedSource) {
  select.textContent = "";
  for (const [value, label] of incomeSources) {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = label;
    select.append(option);
  }
  select.value = incomeSources.some(([value]) => value === selectedSource) ? selectedSource : "employee_salary";
}

function countryName(countryCode) {
  if (!countryCode) return "Unknown country";
  try {
    return new Intl.DisplayNames([navigator.language || "en-US"], { type: "region" }).of(countryCode) || countryCode;
  } catch {
    return countryCode === "US" ? "United States" : countryCode;
  }
}

function stateName(stateCode) {
  return usStates.find(([code]) => code === stateCode)?.[1] || stateCode || "State not set";
}

function incomeSourceName(incomeSource) {
  return incomeSources.find(([value]) => value === incomeSource)?.[1] || "Other";
}

function formatMonthYear(postedOn) {
  const day = dayIndexFromPostedOn(postedOn);
  return day === null
    ? "December 2024"
    : new Intl.DateTimeFormat(undefined, { month: "long", year: "numeric" }).format(dayIndexToLocalDate(day));
}

function supportedCurrencies() {
  return financeState.data?.currencySettings?.supportedCurrencies || ["USD", "CAD", "GBP", "EUR", "AUD", "JPY", "CNY"];
}

function populateCurrencySelect(select, selectedCurrency) {
  const selected = selectedCurrency || "USD";
  select.textContent = "";
  for (const currency of supportedCurrencies()) {
    const option = document.createElement("option");
    option.value = currency;
    option.textContent = currency;
    select.append(option);
  }
  select.value = supportedCurrencies().includes(selected) ? selected : "USD";
}

function currencySelectCell(account) {
  const cell = document.createElement("td");
  cell.className = "account-currency-cell";
  const select = document.createElement("select");
  select.className = "account-currency-select";
  select.setAttribute("aria-label", `Currency for ${account.name}`);
  populateCurrencySelect(select, account.currency || "USD");
  select.addEventListener("click", event => event.stopPropagation());
  select.addEventListener("change", async event => {
    event.stopPropagation();
    const currency = select.value;
    select.disabled = true;
    try {
      await fetchJson(`/api/finance/accounts/${encodeURIComponent(account.id)}/currency`, {
        method: "PUT",
        body: JSON.stringify({ currency })
      });
      await loadFinance();
    } catch (error) {
      select.value = account.currency || "USD";
      financeEls.alert.hidden = false;
      financeEls.alert.className = "poll-alert poll-alert-failed";
      financeEls.alert.textContent = `Could not update ${account.name} currency: ${error.message || error}`;
    } finally {
      select.disabled = false;
    }
  });
  cell.append(select);
  return cell;
}

function summaryText(data) {
  const refresh = data.refresh || {};
  const accountText = `${data.configuredAccountCount} configured account${data.configuredAccountCount === 1 ? "" : "s"}`;
  const refreshed = refresh.lastCompletedUtc ? `last refresh ${formatDateTime(refresh.lastCompletedUtc)}` : "not refreshed yet";
  return `${accountText} - master ${data.currency} - daily refresh ${data.dailyRefreshTime} - ${refreshed}`;
}

function renderRefreshAlert(data) {
  const refresh = data.refresh || {};
  const codexRefreshRunning = Boolean(data.codexRefresh?.isRunning);
  const currencyUnavailable = data.currencySettings && !data.currencySettings.hasCachedRates;
  const noAccounts = data.configuredAccountCount === 0;
  const hasError = Boolean(refresh.error);
  const hasWarning = codexRefreshRunning || currencyUnavailable || noAccounts || Boolean(refresh.message && !refresh.lastSucceeded);
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
    : codexRefreshRunning
      ? "Codex is refreshing an account. The dashboard will keep checking until the session completes or reports what needs your attention."
    : currencyUnavailable
      ? "Exchange rates are unavailable and no cached rates exist yet. Foreign values will remain unconverted until a later app start can refresh rates."
    : noAccounts
      ? `No finance accounts configured. Add accounts to ${data.envPath}; manual values can include CASH_BALANCE, BALANCE_OWED, CREDIT_LIMIT, CREDIT_AVAILABLE, MINIMUM_PAYMENT, PAYMENT_DUE_DATE, and MINIMUM_PAYMENT_MET fields.`
      : `Finance refresh needs attention: ${refresh.message}`;
}

function renderTables(data) {
  const accounts = data.current.accounts || [];
  const renderedAt = Date.now();
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
    financeEls.cardRows.append(emptyRow(11, "No credit cards or loans configured yet."));
  } else {
    for (const card of creditLoans) {
      const row = document.createElement("tr");
      const updateInfo = accountUpdateInfo(card.lastUpdatedUtc, renderedAt);
      row.classList.toggle("account-update-stale", updateInfo.isStale);
      row.append(
        accountCell(card),
        currencySelectCell(card),
        moneyCell(card.balanceOwed, data.currency),
        moneyCell(card.minimumPayment, data.currency),
        textCell(card.paymentDueDate || "--"),
        textCell(card.minimumPaymentMet === null || card.minimumPaymentMet === undefined
          ? "--"
          : card.minimumPaymentMet ? "Paid" : "Outstanding"),
        moneyCell(card.creditAvailable, data.currency),
        aprCell(card),
        interestPreviewCell(card, data.currency),
        textCell(card.utilizationPercent === null ? "--" : `${card.utilizationPercent}%`),
        lastUpdatedCell(card, updateInfo),
      );
      financeEls.cardRows.append(row);
    }
  }

  if (sortedAccounts.length === 0) {
    financeEls.accountRows.append(emptyRow(5, "No accounts configured yet."));
  } else {
    for (const account of sortedAccounts) {
      const row = document.createElement("tr");
      const updateInfo = accountUpdateInfo(account.lastUpdatedUtc, renderedAt);
      row.classList.toggle("is-selected", !financeState.transactionFilters.scopeAll && account.id === financeState.selectedTransactionAccountId);
      row.classList.toggle("account-update-stale", updateInfo.isStale);
      row.append(
        accountCell(account, true),
        textCell(account.kind.replaceAll("_", " ")),
        moneyCell(account.cashBalance, data.currency),
        currencySelectCell(account),
        lastUpdatedCell(account, updateInfo)
      );
      financeEls.accountRows.append(row);
    }
  }
}

function populateRecurringAccountSelect() {
  const selected = financeEls.recurringAccount.value;
  const accounts = (financeState.data?.current?.accounts || [])
    .filter(account => ["bank", "cash", "checking", "savings"].includes(account.kind));
  financeEls.recurringAccount.textContent = "";
  for (const account of accounts) {
    const option = document.createElement("option");
    option.value = account.id;
    option.textContent = `${account.name} \u00b7 ${account.institution}`;
    financeEls.recurringAccount.append(option);
  }
  financeEls.recurringAccount.value = accounts.some(account => account.id === selected)
    ? selected
    : accounts[0]?.id || "";
}

function renderRecurringTransactions(data) {
  const recurring = data.recurringTransactions || {
    recordCount: 0,
    pendingCount: 0,
    approvedCount: 0,
    rejectedCount: 0,
    records: []
  };
  const records = recurring.records || [];
  const approvedTotal = records
    .filter(record => record.status === "approved")
    .reduce((total, record) => total + Math.abs(Number(record.amount || 0)), 0);
  financeEls.recurringTransactionRows.textContent = "";
  financeEls.recurringTransactionCount.textContent = `${records.length} recurring`;
  financeEls.recurringTransactionCaption.textContent = records.length === 0
    ? "Monthly patterns found across all cash accounts"
    : `${recurring.approvedCount} approved (${money(approvedTotal, data.currency)}/month) \u00b7 ${recurring.pendingCount} disabled \u00b7 only approved items affect projections`;

  if (records.length === 0) {
    financeEls.recurringTransactionRows.append(emptyRow(8, "No monthly recurring patterns found yet. Refresh transactions or add one manually."));
    return;
  }

  for (const recurringTransaction of records) {
    const row = document.createElement("tr");
    row.className = `recurring-row recurring-${recurringTransaction.status}`;
    const description = document.createElement("td");
    const descriptionName = document.createElement("strong");
    descriptionName.textContent = recurringTransaction.description;
    const source = document.createElement("span");
    source.className = "recurring-source";
    source.textContent = recurringTransaction.source === "manual"
      ? "Added manually"
      : recurringTransaction.source === "custom" ? "Edited from detected pattern" : "Detected from transactions";
    description.append(descriptionName, source);

    const amount = moneyCell(
      recurringTransaction.enteredAmount ?? recurringTransaction.amount,
      recurringTransaction.enteredCurrency || recurringTransaction.currency || data.currency
    );
    amount.classList.add("money-out");
    const pattern = recurringTransaction.source === "manual"
      ? `Every month on day ${recurringTransaction.dayOfMonth}`
      : recurringTransaction.source === "custom"
        ? `Edited monthly entry \u00b7 ${recurringTransaction.evidenceCount} source matches`
        : `${recurringTransaction.evidenceCount} matches \u00b7 ${formatPostedOn(recurringTransaction.firstObservedOn)} \u2013 ${formatPostedOn(recurringTransaction.lastObservedOn)}`;
    const status = document.createElement("td");
    const statusPill = document.createElement("span");
    statusPill.className = `recurring-status recurring-status-${recurringTransaction.status}`;
    statusPill.textContent = recurringTransaction.status === "approved" ? "approved" : "disabled";
    status.append(statusPill);

    const actions = document.createElement("td");
    actions.className = "recurring-actions-cell";
    actions.append(recurringEditButton(recurringTransaction));
    actions.append(recurringStatusButton(
      recurringTransaction,
      recurringTransaction.status === "approved" ? "pending" : "approved",
      recurringTransaction.status === "approved" ? "Disable" : "Approve"
    ));
    actions.append(recurringRemoveButton(recurringTransaction));

    row.append(
      description,
      textCell(recurringTransaction.accountName),
      textCell(formatPostedOn(recurringTransaction.nextOn)),
      amount,
      recurringCurrencySelectCell(recurringTransaction),
      textCell(pattern),
      status,
      actions
    );
    financeEls.recurringTransactionRows.append(row);
  }
}

function recurringCurrencySelectCell(recurringTransaction) {
  const cell = document.createElement("td");
  const select = document.createElement("select");
  select.className = "recurring-currency-select";
  select.setAttribute("aria-label", `Currency for ${recurringTransaction.description}`);
  populateCurrencySelect(
    select,
    recurringTransaction.enteredCurrency || recurringTransaction.currency || financeState.data?.currency || "USD"
  );
  select.addEventListener("change", async () => {
    select.disabled = true;
    try {
      await fetchJson(`/api/finance/recurring-transactions/${encodeURIComponent(recurringTransaction.id)}`, {
        method: "PUT",
        body: JSON.stringify({
          accountId: recurringTransaction.accountId,
          description: recurringTransaction.description,
          amount: Math.abs(Number(recurringTransaction.enteredAmount ?? recurringTransaction.amount ?? 0)),
          currency: select.value,
          nextOn: recurringTransaction.nextOn
        })
      });
      await loadFinance();
    } catch (error) {
      financeEls.alert.hidden = false;
      financeEls.alert.className = "poll-alert poll-alert-failed";
      financeEls.alert.textContent = `Could not change currency for ${recurringTransaction.description}: ${error.message || error}`;
      select.disabled = false;
    }
  });
  cell.append(select);
  return cell;
}

function recurringEditButton(recurringTransaction) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "recurring-action recurring-action-edit";
  button.textContent = "Edit";
  button.setAttribute("aria-label", `Edit ${recurringTransaction.description}`);
  button.addEventListener("click", () => openRecurringTransactionForm(recurringTransaction));
  return button;
}
function recurringStatusButton(recurringTransaction, status, label) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = `recurring-action recurring-action-${status}`;
  button.textContent = label;
  button.setAttribute("aria-label", `${label} ${recurringTransaction.description}`);
  button.addEventListener("click", async () => {
    const row = button.closest("tr");
    for (const action of row.querySelectorAll("button")) action.disabled = true;
    try {
      await fetchJson(`/api/finance/recurring-transactions/${encodeURIComponent(recurringTransaction.id)}/status`, {
        method: "PUT",
        body: JSON.stringify({ status })
      });
      await loadFinance();
    } catch (error) {
      financeEls.alert.hidden = false;
      financeEls.alert.className = "poll-alert poll-alert-failed";
      financeEls.alert.textContent = `Could not ${label.toLowerCase()} ${recurringTransaction.description}: ${error.message || error}`;
      for (const action of row.querySelectorAll("button")) action.disabled = false;
    }
  });
  return button;
}
function recurringRemoveButton(recurringTransaction) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "recurring-action recurring-action-remove";
  button.textContent = "\u00d7";
  button.title = "Remove recurring transaction";
  button.setAttribute("aria-label", `Remove ${recurringTransaction.description}`);
  button.addEventListener("click", async () => {
    const row = button.closest("tr");
    for (const action of row.querySelectorAll("button")) action.disabled = true;
    try {
      await fetchJson(`/api/finance/recurring-transactions/${encodeURIComponent(recurringTransaction.id)}`, {
        method: "DELETE"
      });
      await loadFinance();
    } catch (error) {
      financeEls.alert.hidden = false;
      financeEls.alert.className = "poll-alert poll-alert-failed";
      financeEls.alert.textContent = `Could not remove ${recurringTransaction.description}: ${error.message || error}`;
      for (const action of row.querySelectorAll("button")) action.disabled = false;
    }
  });
  return button;
}

function renderTransactions(data) {
  if (!data) return;
  const transactionData = data.transactions || { accounts: [], records: [] };
  const targets = transactionData.accounts || [];
  const allRecords = transactionData.records || [];
  const validIds = new Set(targets.map(account => account.accountId));
  if (!validIds.has(financeState.selectedTransactionAccountId)) {
    financeState.selectedTransactionAccountId = targets[0]?.accountId || null;
  }

  const filters = financeState.transactionFilters;
  const selected = targets.find(account => account.accountId === financeState.selectedTransactionAccountId);
  renderTransactionFilterControls(targets, allRecords, data);
  const scopedRecords = filters.scopeAll
    ? allRecords
    : allRecords.filter(record => record.accountId === financeState.selectedTransactionAccountId);
  const records = scopedRecords
    .filter(record => transactionMatchesFilters(record, filters))
    .sort((left, right) => String(right.postedOn).localeCompare(String(left.postedOn))
      || String(left.accountName || "").localeCompare(String(right.accountName || "")));
  const hasSearchOrFilters = Boolean(filters.query.trim()) || transactionHasAdvancedFilters(filters);
  financeState.transactionMatchIds = records.map(record => record.id);
  renderTransactionBulkLabelBar(records, allRecords, hasSearchOrFilters);

  financeEls.transactionRows.textContent = "";
  financeEls.transactionCount.textContent = hasSearchOrFilters
    ? `${records.length} matching`
    : `${records.length} transaction${records.length === 1 ? "" : "s"}`;
  renderTransactionMatchSummary(records, data.currency);

  const scopeCaption = filters.scopeAll
    ? `All ${targets.length} transaction accounts`
    : selected
      ? `${selected.accountName} \u00b7 ${selected.institution} \u00b7 ${selected.initialBackfillComplete ? "monthly refresh" : "24-month backfill needed"} from ${formatPostedOn(selected.requiredStartOn)}`
      : "Select an account above to view its transactions.";
  financeEls.transactionCaption.textContent = hasSearchOrFilters
    ? `${scopeCaption} \u00b7 ${records.length} of ${scopedRecords.length} match`
    : scopeCaption;

  if (!filters.scopeAll && !selected) {
    financeEls.transactionRows.append(emptyRow(12, "No transaction-eligible accounts are configured."));
    return;
  }
  if (records.length === 0) {
    const emptyMessage = hasSearchOrFilters
      ? "No transactions match the current search and advanced filters."
      : filters.scopeAll
        ? "No stored transactions across the configured accounts. Use Refresh Transactions to collect them."
        : `No stored transactions for ${selected.accountName}. Use Refresh Transactions to collect them.`;
    financeEls.transactionRows.append(emptyRow(12, emptyMessage));
    return;
  }

  for (const transaction of records) {
    const row = document.createElement("tr");
    const date = document.createElement("td");
    date.className = "transaction-date";
    date.textContent = formatPostedOn(transaction.postedOn);
    if (transaction.transactedOn && transaction.transactedOn !== transaction.postedOn) {
      const transacted = document.createElement("span");
      transacted.textContent = `Transaction ${formatPostedOn(transaction.transactedOn)}`;
      date.append(transacted);
    }
    const direction = document.createElement("td");
    const directionPill = document.createElement("span");
    directionPill.className = `transaction-direction ${transaction.direction === "money_in" ? "money-in" : "money-out"}`;
    directionPill.textContent = transaction.direction === "money_in" ? "Money in" : "Money out";
    direction.append(directionPill);
    const amount = moneyCell(transaction.amount, transaction.currency || data.currency);
    amount.classList.add(transaction.direction === "money_in" ? "money-in" : "money-out");
    row.append(
      date,
      textCell(transaction.accountName || "--"),
      textCell(transaction.description || "--"),
      textCell(transaction.merchant || "--"),
      direction,
      amount,
      textCell(transaction.status || "posted"),
      transactionLabelsCell(transaction),
      textCell(transaction.person || "--"),
      notesCell(transaction.notes),
      textCell(transaction.reference || "--"),
      transactionEditCell(transaction)
    );
    financeEls.transactionRows.append(row);
  }
}

function transactionLabelsCell(transaction) {
  const cell = document.createElement("td");
  cell.className = "transaction-label-cell";
  const labels = transactionLabels(transaction);
  if (labels.length === 0) {
    cell.textContent = "--";
    return cell;
  }
  for (const label of labels) {
    const chip = document.createElement("span");
    chip.className = "transaction-label-chip";
    chip.textContent = label;
    cell.append(chip);
  }
  return cell;
}

function notesCell(notes) {
  const cell = document.createElement("td");
  cell.className = "transaction-notes-cell";
  cell.textContent = notes || "--";
  if (notes) cell.title = notes;
  return cell;
}

function transactionEditCell(transaction) {
  const cell = document.createElement("td");
  const button = document.createElement("button");
  button.type = "button";
  button.className = "transaction-edit-button";
  button.textContent = "Edit";
  button.setAttribute("aria-label", `Edit ${transaction.description || "transaction"}`);
  button.addEventListener("click", () => openTransactionForm(transaction));
  cell.append(button);
  return cell;
}

function renderTransactionBulkLabelBar(records, allRecords, visible) {
  financeEls.transactionBulkLabelBar.hidden = !visible;
  financeEls.transactionBulkLabelCaption.textContent = `${records.length} current match${records.length === 1 ? "" : "es"}. Existing labels are preserved.`;
  financeEls.applyTransactionLabelToMatches.textContent = `Apply to ${records.length}`;
  financeEls.applyTransactionLabelToMatches.disabled = records.length === 0;
  const selectedLabel = financeEls.transactionBulkLabel.value;
  const labels = allRecords.flatMap(transactionLabels)
    .filter((label, index, values) => values.findIndex(value => value.toLocaleLowerCase() === label.toLocaleLowerCase()) === index)
    .sort((left, right) => left.localeCompare(right));
  financeEls.transactionBulkLabel.textContent = "";
  const empty = document.createElement("option");
  empty.value = "";
  empty.textContent = labels.length === 0 ? "No existing labels" : "Choose existing label";
  financeEls.transactionBulkLabel.append(empty);
  for (const label of labels) {
    const option = document.createElement("option");
    option.value = label;
    option.textContent = label;
    financeEls.transactionBulkLabel.append(option);
  }
  financeEls.transactionBulkLabel.value = labels.includes(selectedLabel) ? selectedLabel : "";
}

function renderTransactionFilterControls(targets, records, data) {
  const filters = financeState.transactionFilters;
  financeEls.transactionScopeToggle.textContent = filters.scopeAll ? "Show selected account" : "Show all accounts";
  financeEls.transactionScopeToggle.setAttribute("aria-pressed", String(filters.scopeAll));
  financeEls.transactionScopeToggle.disabled = targets.length === 0;
  if (financeEls.transactionSearch.value !== filters.query) {
    financeEls.transactionSearch.value = filters.query;
  }
  financeEls.transactionAdvancedFilters.hidden = !filters.advancedOpen;
  financeEls.transactionAdvancedToggle.setAttribute("aria-expanded", String(filters.advancedOpen));
  financeEls.transactionAdvancedToggle.classList.toggle("is-active", filters.advancedOpen || transactionHasAdvancedFilters(filters));

  const selectedAccountFilter = filters.scopeAll ? "all" : financeState.selectedTransactionAccountId || "all";
  financeEls.transactionFilterAccount.textContent = "";
  const allOption = document.createElement("option");
  allOption.value = "all";
  allOption.textContent = `All accounts (${records.length})`;
  financeEls.transactionFilterAccount.append(allOption);
  for (const account of targets) {
    const option = document.createElement("option");
    option.value = account.accountId;
    option.textContent = `${account.accountName} (${account.recordCount})`;
    financeEls.transactionFilterAccount.append(option);
  }
  financeEls.transactionFilterAccount.value = selectedAccountFilter;

  const currencySelect = financeEls.transactionAdvancedFilters.querySelector('[name="currency"]');
  const selectedCurrency = filters.currency;
  const currencies = [...new Set(records.map(record => record.currency).filter(Boolean))]
    .sort((left, right) => left.localeCompare(right));
  currencySelect.textContent = "";
  const anyCurrency = document.createElement("option");
  anyCurrency.value = "";
  anyCurrency.textContent = "Any currency";
  currencySelect.append(anyCurrency);
  for (const currency of currencies.length > 0 ? currencies : [data.currency]) {
    const option = document.createElement("option");
    option.value = currency;
    option.textContent = currency;
    currencySelect.append(option);
  }
  currencySelect.value = currencies.includes(selectedCurrency) ? selectedCurrency : "";

  for (const control of financeEls.transactionAdvancedFilters.querySelectorAll("input[name], select[name]")) {
    if (control.name === "account" || control.name === "currency") continue;
    const value = filters[control.name] || "";
    if (control.value !== value) control.value = value;
  }
}

function transactionMatchesFilters(record, filters) {
  const query = filters.query.trim().toLocaleLowerCase();
  if (query) {
    const searchableValues = [
      ...Object.values(record),
      String(record.direction || "").replaceAll("_", " "),
      Math.abs(Number(record.amount || 0))
    ];
    if (!searchableValues.some(value => transactionFieldIncludes(value, query))) return false;
  }

  if (filters.dateFrom && String(record.postedOn) < filters.dateFrom) return false;
  if (filters.dateTo && String(record.postedOn) > filters.dateTo) return false;
  if (filters.description && !transactionFieldIncludes(record.description, filters.description)) return false;
  if (filters.merchant && !transactionFieldIncludes(record.merchant, filters.merchant)) return false;
  if (filters.direction && record.direction !== filters.direction) return false;
  if (filters.currency && record.currency !== filters.currency) return false;
  if (filters.status && !transactionFieldIncludes(record.status, filters.status)) return false;
  if (filters.label && !transactionFieldIncludes(transactionLabels(record).join(" "), filters.label)) return false;
  if (filters.person && !transactionFieldIncludes(record.person, filters.person)) return false;
  if (filters.notes && !transactionFieldIncludes(record.notes, filters.notes)) return false;
  if (filters.reference && !transactionFieldIncludes(record.reference, filters.reference)) return false;

  const absoluteAmount = Math.abs(Number(record.amount || 0));
  if (filters.amountMin !== "" && absoluteAmount < Number(filters.amountMin)) return false;
  if (filters.amountMax !== "" && absoluteAmount > Number(filters.amountMax)) return false;
  return true;
}

function transactionFieldIncludes(value, query) {
  return value !== null
    && value !== undefined
    && String(value).toLocaleLowerCase().includes(String(query).trim().toLocaleLowerCase());
}

function transactionHasAdvancedFilters(filters) {
  return ["dateFrom", "dateTo", "description", "merchant", "direction", "amountMin", "amountMax", "currency", "status", "label", "person", "notes", "reference"]
    .some(name => String(filters[name] || "").trim() !== "");
}

function renderTransactionMatchSummary(records, currency) {
  const moneyOut = records
    .filter(record => record.direction === "money_out" || Number(record.amount) < 0)
    .reduce((total, record) => total + Math.abs(Number(record.amount || 0)), 0);
  const moneyIn = records
    .filter(record => record.direction === "money_in" || Number(record.amount) > 0)
    .reduce((total, record) => total + Math.abs(Number(record.amount || 0)), 0);
  financeEls.transactionMatchCount.textContent = String(records.length);
  financeEls.transactionMoneyOut.textContent = money(moneyOut, currency);
  financeEls.transactionMoneyIn.textContent = money(moneyIn, currency);
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
  const effectiveApr = effectiveAprPercent(account);
  const interest = monthlyInterest(account.balanceOwed, effectiveApr);
  if (interest === null) {
    return moneyCell(null, currency);
  }

  const cell = document.createElement("td");
  cell.className = "money-cell finance-private-value";
  const button = document.createElement("button");
  button.type = "button";
  button.className = "interest-preview-trigger";
  button.textContent = money(interest, currency);
  button.title = "Preview monthly interest after a payment";
  button.setAttribute("aria-haspopup", "dialog");
  button.setAttribute("aria-label", `Preview monthly interest for ${account.name}, currently ${money(interest, currency)}`);
  button.addEventListener("click", () => openInterestPreview(account, currency, effectiveApr));
  cell.append(button);
  return cell;
}

function openInterestPreview(account, currency, effectiveApr = account.aprPercent) {
  const owed = Number(account.balanceOwed);
  const apr = Number(effectiveApr);
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
  financeEls.interestPreviewResult.textContent = `${money(remainingBalance, interestPreview.currency)} remaining \u2014 estimated monthly interest: ${money(newInterest, interestPreview.currency)}.${cappedNotice}`;
}

function openAprEditor(account) {
  editingAprAccount = account;
  financeEls.aprEditorTitle.textContent = `Edit ${account.name} APR`;
  financeEls.aprEditorAccount.textContent = `${account.institution || "Credit or loan account"} - ${account.kind.replaceAll("_", " ")}`;
  financeEls.aprEditorRegular.value = account.aprPercent ?? "";
  financeEls.aprEditorPromotional.value = account.promotionalAprPercent ?? "";
  financeEls.aprEditorEndsOn.value = account.promotionalAprEndsOn || "";
  financeEls.clearAprPromotion.disabled = account.promotionalAprPercent === null
    || account.promotionalAprPercent === undefined
    || !account.promotionalAprEndsOn;
  setAprEditorStatus("");
  renderAprEditorPreview();
  if (!financeEls.aprEditorDialog.open) {
    financeEls.aprEditorDialog.showModal();
  }
  financeEls.aprEditorRegular.focus();
}

function aprEditorPayload(clearPromotion = false) {
  const regularText = financeEls.aprEditorRegular.value.trim();
  const promotionalText = financeEls.aprEditorPromotional.value.trim();
  const promotionalAprEndsOn = financeEls.aprEditorEndsOn.value || null;
  const aprPercent = regularText === "" ? null : Number(regularText);
  const promotionalAprPercent = clearPromotion || promotionalText === "" ? null : Number(promotionalText);
  const endsOn = clearPromotion ? null : promotionalAprEndsOn;

  if (aprPercent === null) {
    throw new Error("Enter the regular APR.");
  }
  if ((!Number.isFinite(aprPercent) || aprPercent < 0)
    || (promotionalAprPercent !== null && (!Number.isFinite(promotionalAprPercent) || promotionalAprPercent < 0))) {
    throw new Error("APR values must be zero or greater.");
  }
  if ((promotionalAprPercent === null) !== (endsOn === null)) {
    throw new Error("Enter both a promotional APR and its end date, or leave both blank.");
  }

  return { aprPercent, promotionalAprPercent, promotionalAprEndsOn: endsOn };
}

function renderAprEditorPreview() {
  const regularApr = nullableFiniteNumber(financeEls.aprEditorRegular.value);
  const promotionalApr = nullableFiniteNumber(financeEls.aprEditorPromotional.value);
  const promotionalAprEndsOn = financeEls.aprEditorEndsOn.value || null;
  const todayDay = dayIndexFromTimestamp(financeState.data?.nowUtc) ?? dayIndexFromTimestamp(new Date());
  const effectiveApr = effectiveAprPercent({
    aprPercent: regularApr,
    promotionalAprPercent: promotionalApr,
    promotionalAprEndsOn
  }, todayDay);
  financeEls.aprEditorEffective.textContent = formatAprPercent(effectiveApr);

  const endsDay = dayIndexFromPostedOn(promotionalAprEndsOn);
  if (promotionalApr !== null && endsDay !== null) {
    const lastPromotionalOn = dayIndexToPostedOn(endsDay - 1);
    financeEls.aprEditorSchedule.textContent = `${formatAprPercent(promotionalApr)} through ${formatPostedOn(lastPromotionalOn)}; ${formatAprPercent(regularApr)} from ${formatPostedOn(promotionalAprEndsOn)}`;
  } else if (promotionalApr !== null || promotionalAprEndsOn) {
    financeEls.aprEditorSchedule.textContent = "Complete both promotional fields";
  } else {
    financeEls.aprEditorSchedule.textContent = `${formatAprPercent(regularApr)} regular APR`;
  }
}

function accountUpdateInfo(lastUpdatedUtc, now = Date.now()) {
  const updatedAt = lastUpdatedUtc ? Date.parse(lastUpdatedUtc) : Number.NaN;
  if (!Number.isFinite(updatedAt)) {
    return { ageText: "Never", isStale: true, updatedAt: null };
  }

  const elapsed = Math.max(0, now - updatedAt);
  if (elapsed >= financeDayMilliseconds) {
    const days = Math.floor(elapsed / financeDayMilliseconds);
    return {
      ageText: `${days} day${days === 1 ? "" : "s"}`,
      isStale: elapsed > staleAccountMilliseconds,
      updatedAt: lastUpdatedUtc
    };
  }

  const hourMilliseconds = 60 * 60 * 1000;
  if (elapsed >= hourMilliseconds) {
    const hours = Math.floor(elapsed / hourMilliseconds);
    return {
      ageText: `${hours} hour${hours === 1 ? "" : "s"}`,
      isStale: false,
      updatedAt: lastUpdatedUtc
    };
  }

  const minutes = Math.floor(elapsed / (60 * 1000));
  return {
    ageText: `${minutes} minute${minutes === 1 ? "" : "s"}`,
    isStale: false,
    updatedAt: lastUpdatedUtc
  };
}

function lastUpdatedCell(account, updateInfo) {
  const cell = document.createElement("td");
  cell.className = "last-updated-cell";
  if (!updateInfo.isStale) {
    cell.textContent = updateInfo.ageText;
    cell.title = `Last updated ${formatDateTime(updateInfo.updatedAt)}`;
    return cell;
  }

  const button = document.createElement("button");
  button.type = "button";
  button.className = "account-refresh-trigger";
  button.title = "Update Account";
  button.setAttribute("aria-label", `Update ${account.name}; last updated ${updateInfo.ageText.toLowerCase()}`);

  const age = document.createElement("span");
  age.className = "last-updated-age";
  age.textContent = updateInfo.ageText;
  const action = document.createElement("span");
  action.className = "last-updated-action";
  action.textContent = "Update Account";
  button.append(age, action);
  button.addEventListener("click", event => {
    event.stopPropagation();
    void startFinanceWorkflow(
      button,
      `/api/finance/accounts/${encodeURIComponent(account.id)}/refresh`,
      `${account.name} account values`,
      `A Codex session has opened to update only ${account.name}.`
    );
  });
  cell.append(button);
  return cell;
}

function setAprEditorStatus(message, isError = false) {
  financeEls.aprEditorStatus.textContent = message;
  financeEls.aprEditorStatus.classList.toggle("is-error", isError);
}

function setAprEditorBusy(isBusy) {
  for (const control of [
    financeEls.aprEditorRegular,
    financeEls.aprEditorPromotional,
    financeEls.aprEditorEndsOn,
    financeEls.closeAprEditor,
    financeEls.cancelAprEditor,
    financeEls.clearAprPromotion,
    financeEls.saveAprEditor
  ]) {
    control.disabled = isBusy;
  }
}

async function saveAprEditor(clearPromotion = false) {
  if (!editingAprAccount) {
    return;
  }

  let payload;
  try {
    payload = aprEditorPayload(clearPromotion);
  } catch (error) {
    setAprEditorStatus(error.message || String(error), true);
    return;
  }

  setAprEditorBusy(true);
  setAprEditorStatus(clearPromotion ? "Clearing promotional APR..." : "Saving APR...");
  try {
    await fetchJson(`/api/finance/accounts/${encodeURIComponent(editingAprAccount.id)}/apr`, {
      method: "PUT",
      body: JSON.stringify(payload)
    });
    await loadFinance();
    financeEls.aprEditorDialog.close();
  } catch (error) {
    setAprEditorStatus(`Could not save APR: ${error.message || error}`, true);
  } finally {
    setAprEditorBusy(false);
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
    pill.className = `state-pill ${log.status === "ok" || log.status === "queued" ? "online" : log.status === "warning" || log.status === "partial" || log.status === "blocked" ? "stale" : log.status === "failed" ? "failed" : ""}`;
    pill.textContent = log.status;
    const message = document.createElement(log.message.length > 180 || log.message.includes("\n") ? "button" : "div");
    if (message instanceof HTMLButtonElement) {
      message.type = "button";
      message.className = "status-note collapsed";
      message.title = "Click to expand or collapse this Codex explanation";
      message.addEventListener("click", () => message.classList.toggle("collapsed"));
    }
    message.textContent = log.message;
    const time = document.createElement("div");
    time.className = "event-time";
    time.textContent = formatDateTime(log.atUtc);
    row.append(pill, message, time);
    financeEls.refreshLog.append(row);
  }
}

function renderProjectionSummary(data, projectionModel, projectionDay) {
  const paymentCount = projectionModel.payments.length;
  const salaryPayments = projectionModel.payments.filter(payment => !payment.bonus);
  const bonusPayments = projectionModel.payments.filter(payment => payment.bonus);
  const salaryIncome = salaryPayments.reduce((total, payment) => total + payment.amount, 0);
  const bonusIncome = bonusPayments.reduce((total, payment) => total + payment.amount, 0);
  const recurringCount = projectionModel.recurringPayments.length;
  const projectedIncome = centsToNumber(projectionModel.incomeCents);
  const projectedExpenses = Math.abs(centsToNumber(projectionModel.recurringCents));
  const projectedChange = centsToNumber(projectionModel.incomeCents + projectionModel.recurringCents);
  financeEls.projectionSummaryDate.textContent = `Projected for ${formatHistoryDay(projectionDay)}`;
  financeEls.projectionCash.textContent = money(centsToNumber(projectionModel.cashCents), data.currency);
  financeEls.projectionNet.textContent = money(centsToNumber(projectionModel.netCents), data.currency);
  const countParts = [];
  if (salaryPayments.length > 0) countParts.push(`${salaryPayments.length} salary`);
  if (bonusPayments.length > 0) countParts.push(`${bonusPayments.length} bonus`);
  financeEls.projectionSalaryCount.textContent = countParts.length > 0 ? countParts.join(" \u00b7 ") : "0 payments";

  if (paymentCount > 0 || recurringCount > 0) {
    const signedChange = `${projectedChange >= 0 ? "+" : ""}${money(projectedChange, data.currency)}`;
    const components = [];
    if (salaryPayments.length > 0) components.push(`${money(salaryIncome, data.currency)} salary`);
    if (bonusPayments.length > 0) components.push(`${money(bonusIncome, data.currency)} bonuses`);
    if (recurringCount > 0) components.push(`${money(projectedExpenses, data.currency)} recurring expenses`);
    const changeText = `${signedChange} projected \u00b7 ${components.join(" \u2212 ")}`;
    financeEls.projectionCashChange.textContent = changeText;
    financeEls.projectionNetChange.textContent = changeText;
  } else {
    financeEls.projectionCashChange.textContent = "No approved cash changes by this date";
    financeEls.projectionNetChange.textContent = "No approved cash changes by this date";
  }

  if (paymentCount === 1) {
    const payment = projectionModel.payments[0];
    financeEls.projectionSalaryDetail.textContent = `${payment.bonus ? "Bonus" : "Salary"} ${formatHistoryDay(payment.day)} \u00b7 ${money(payment.amount, data.currency)}`;
  } else if (paymentCount > 1) {
    const first = projectionModel.payments[0];
    const last = projectionModel.payments[paymentCount - 1];
    financeEls.projectionSalaryDetail.textContent = `${formatHistoryDay(first.day)} \u2013 ${formatHistoryDay(last.day)} \u00b7 ${money(projectedIncome, data.currency)} total`;
  } else {
    const next = projectionModel.nextEstimatedPayment;
    financeEls.projectionSalaryDetail.textContent = next
      ? `Next salary ${formatHistoryDay(next.day)} \u00b7 ${money(centsToNumber(next.amountCents), data.currency)}`
      : "No salary or bonus falls in this projection";
  }

  const recurringNote = recurringCount > 0
    ? `${recurringCount} approved recurring deduction${recurringCount === 1 ? "" : "s"} included. `
    : "No approved recurring deductions fall in this range. ";
  const salaryNote = data.salaryPlan?.salary
    ? "The saved salary schedule is used. "
    : projectionModel.schedules.length > 0
      ? "Salary is inferred from recent deposits. "
      : "No salary schedule or stable recent cadence was found. ";
  financeEls.projectionSummaryNote.textContent = `${salaryNote}${recurringNote}Debt and credit stay at today's values.`;
}
function renderChart(data) {
  const allHistory = reconcileEarlySalaryCashHistory(
    data.history || [],
    data.income?.salaryPayments || [],
    data.currency
  );
  const selectedRange = syncHistoryDateRange(data);
  const projectionActive = financeState.projection.enabled;
  const todayDay = projectionActive ? financeState.projection.todayDay : null;
  const chartEndDay = projectionActive ? financeState.projection.day : selectedRange.endDay;
  const actualEndDay = projectionActive ? todayDay : selectedRange.endDay;
  const rangedHistory = filterHistoryByDateRange(allHistory, selectedRange.startDay, actualEndDay);
  if (projectionActive) {
    rangedHistory.push({
      ...data.current,
      sampledAtUtc: data.nowUtc,
      projected: false,
      projectionBaseline: true
    });
  }
  const history = latestSnapshotPerDay(rangedHistory);
  const projectionModel = projectionActive
    ? buildSalaryProjection(data, todayDay, chartEndDay)
    : null;
  if (projectionModel) {
    renderProjectionSummary(data, projectionModel, chartEndDay);
  }
  const recordedSalaryPayments = filterSalaryPaymentsByDateRange(
    data.income?.salaryPayments || [],
    selectedRange.startDay,
    actualEndDay,
    data.currency
  );
  const salaryPayments = projectionModel
    ? [...recordedSalaryPayments, ...projectionModel.payments]
    : recordedSalaryPayments;
  const rangeLabel = formatHistoryDateRange(selectedRange.startDay, chartEndDay);
  const axisRange = axisRangeForDaySpan(chartEndDay - selectedRange.startDay);
  const svg = financeEls.chart;
  svg.textContent = "";
  const continuousSeries = [
    { key: "netAfterDebt", label: "Net", className: "chart-line-net" },
    { key: "totalCash", label: "Cash", className: "chart-line-cash" },
    { key: "totalDebt", label: "Debt", className: "chart-line-debt" },
    { key: "totalCreditAvailable", label: "Credit", className: "chart-line-credit" }
  ];
  const salarySeries = { key: "salary", label: "Salary & bonuses", className: "chart-line-salary", discrete: true };
  const series = [...continuousSeries, salarySeries];
  const visibleContinuousSeries = continuousSeries.filter(item => financeState.visibleSeries.has(item.key));
  const salaryVisible = financeState.visibleSeries.has(salarySeries.key);
  renderSalary(salaryVisible ? salaryPayments : [], data, selectedRange.startDay, chartEndDay);
  const hasPlottableSalary = salaryVisible && salaryPayments.length > 0;
  const hasProjectionValues = projectionModel?.snapshots.length > 0;
  if (history.length < 2 && !hasPlottableSalary && !hasProjectionValues) {
    svg.setAttribute("height", "360");
    svg.setAttribute("viewBox", "0 0 820 360");
    drawSvgText(svg, 24, 182, history.length === 0 ? `No finance history in ${rangeLabel}.` : "One daily value in this range. More days will build the graph.", "empty-svg");
    financeEls.historyCaption.textContent = history.length === 0
      ? `${rangeLabel} - no daily values`
      : `${rangeLabel} - 1 daily value from ${rangedHistory.length} snapshots`;
    return;
  }

  financeEls.historyCaption.textContent = projectionModel
    ? `${rangeLabel} \u00b7 ${history.length} recorded days, ${projectionModel.payments.length} projected income payment${projectionModel.payments.length === 1 ? "" : "s"}, ${projectionModel.recurringPayments.length} recurring deduction${projectionModel.recurringPayments.length === 1 ? "" : "s"}`
    : `${rangeLabel} \u00b7 ${history.length} daily values, ${salaryPayments.length} salary or bonus payment${salaryPayments.length === 1 ? "" : "s"}`;
  const fitProjectionToTablet = projectionModel && window.innerWidth > 640 && window.innerWidth <= 900;
  const chartMinimumWidth = fitProjectionToTablet ? 640 : minChartWidthForRange(axisRange);
  const width = Math.max(chartMinimumWidth, svg.parentElement.clientWidth - 24);
  const height = 660;
  const left = 72;
  const right = 24;
  const top = 38;
  const bottom = 42;
  const plotWidth = width - left - right;
  const plotHeight = height - top - bottom;
  const recordedChartValues = history.map(snapshot => ({
    snapshot,
    values: Object.fromEntries(continuousSeries.map(item => {
      const value = Number(snapshot[item.key] || 0);
      return [item.key, value];
    }))
  }));
  const projectedChartValues = (projectionModel?.snapshots || []).map(snapshot => ({
    snapshot,
    values: Object.fromEntries(continuousSeries.map(item => {
      const value = Number(snapshot[item.key] || 0);
      return [item.key, value];
    }))
  }));
  const chartValues = [...recordedChartValues, ...projectedChartValues];
  const values = [
    ...chartValues.flatMap(point => visibleContinuousSeries.map(item => point.values[item.key])),
    ...(salaryVisible ? salaryPayments.map(point => point.amount) : [])
  ];
  const minValue = Math.min(0, ...values);
  const maxValue = Math.max(1, ...values);
  const start = dayIndexToLocalStartDate(selectedRange.startDay);
  const end = dayIndexToLocalEndDate(chartEndDay);
  const span = end - start || 1;

  svg.setAttribute("height", String(height));
  svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
  svg.setAttribute("aria-label", projectionModel
    ? `Finance chart with ${history.length} recorded daily values, ${projectionModel.payments.length} projected salary or bonus payments, and a cash projection with approved recurring expenses through ${formatHistoryDay(chartEndDay)}`
    : `Finance history chart with ${history.length} daily values and ${salaryPayments.length} discrete salary or bonus payments in ${rangeLabel}`);
  let projectionBoundaryX = null;
  if (projectionModel) {
    const projectionBoundary = dayIndexToLocalEndDate(todayDay);
    projectionBoundaryX = left + ((projectionBoundary - start) / span) * plotWidth;
    const zone = document.createElementNS("http://www.w3.org/2000/svg", "rect");
    zone.setAttribute("x", projectionBoundaryX.toFixed(1));
    zone.setAttribute("y", String(top));
    zone.setAttribute("width", Math.max(0, width - right - projectionBoundaryX).toFixed(1));
    zone.setAttribute("height", String(plotHeight));
    zone.setAttribute("class", "chart-projection-zone");
    svg.append(zone);
  }
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
    range: axisRange
  });

  if (projectionBoundaryX !== null) {
    drawLine(svg, projectionBoundaryX, top, projectionBoundaryX, height - bottom, colorForSeries("chart-line-salary"), 1, "chart-projection-boundary");
    drawSvgText(svg, projectionBoundaryX + 7, top + 14, "PROJECTED", "chart-projection-label");
  }

  for (const item of visibleContinuousSeries) {
    const recordedPoints = recordedChartValues.map(point => {
      const x = left + ((new Date(point.snapshot.sampledAtUtc) - start) / span) * plotWidth;
      const y = valueToY(point.values[item.key], minValue, maxValue, top, plotHeight);
      return { x, y, value: point.values[item.key], snapshot: point.snapshot };
    });
    drawChartStepPath(svg, recordedPoints, item.className);
    for (const point of recordedPoints) {
      drawCircle(svg, point.x, point.y, 3.8, colorForSeries(item.className), "chart-point");
    }

    if (projectionModel && recordedPoints.length > 0) {
      const projectedPoints = [recordedPoints[recordedPoints.length - 1], ...projectedChartValues.map(point => {
        const x = left + ((new Date(point.snapshot.sampledAtUtc) - start) / span) * plotWidth;
        const y = valueToY(point.values[item.key], minValue, maxValue, top, plotHeight);
        return { x, y, value: point.values[item.key], snapshot: point.snapshot };
      })];
      drawChartStepPath(svg, projectedPoints, `${item.className} chart-line-projected`);
      if (item.key === "totalCash") {
        for (const point of projectedPoints.filter(candidate => candidate.snapshot.recurringItems?.length > 0)) {
          const recurringCents = point.snapshot.recurringItems.reduce((total, recurring) => total + recurring.amountCents, 0);
          const marker = drawCircle(svg, point.x, point.y, 5.8, colorForSeries("chart-line-debt"), "chart-point chart-point-recurring");
          const title = document.createElementNS("http://www.w3.org/2000/svg", "title");
          title.textContent = point.snapshot.recurringItems
            .map(recurring => `${recurring.description}: ${money(centsToNumber(recurring.amountCents), data.currency)}`)
            .join("; ");
          marker.append(title);
          drawSvgText(
            svg,
            point.x + 8,
            point.y - 9,
            money(centsToNumber(recurringCents), data.currency),
            "chart-recurring-label"
          );
        }
      }
    }
  }

  if (salaryVisible) {
    const zeroY = valueToY(0, minValue, maxValue, top, plotHeight);
    for (const point of salaryPayments) {
      const x = left + ((point.date - start) / span) * plotWidth;
      const y = valueToY(point.amount, minValue, maxValue, top, plotHeight);
      drawLine(
        svg,
        x,
        zeroY,
        x,
        y,
        colorForSeries(salarySeries.className),
        1.4,
        `chart-salary-stem${point.projected ? " chart-salary-stem-projected" : ""}`
      );
      const marker = drawCircle(
        svg,
        x,
        y,
        point.projected ? 6.5 : 7.6,
        point.projected ? "#ffffff" : colorForSeries(salarySeries.className),
        `chart-point chart-point-salary${point.projected ? " chart-point-salary-projected" : ""}`
      );
      const title = document.createElementNS("http://www.w3.org/2000/svg", "title");
      title.textContent = `${point.projected ? "Estimated " : ""}${formatPostedOn(point.payment.postedOn)} ${point.projected ? "projected " : ""}${point.bonus ? "bonus" : "salary"}: ${money(point.amount, point.payment.currency)}`;
      marker.append(title);
    }
  }

  series.forEach((item, index) => {
    const x = left + index * 86;
    drawLegendToggle(svg, x, 4, item, financeState.visibleSeries.has(item.key));
  });

  attachChartTooltip(svg, chartValues, visibleContinuousSeries, salaryPayments, salaryVisible, {
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
  scheduleProjectionChartScroll();
}

function drawChartStepPath(svg, points, className) {
  if (points.length < 2) {
    return;
  }
  const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
  const commands = [`M ${points[0].x.toFixed(1)} ${points[0].y.toFixed(1)}`];
  for (const point of points.slice(1)) {
    commands.push(`H ${point.x.toFixed(1)}`, `V ${point.y.toFixed(1)}`);
  }
  path.setAttribute("d", commands.join(" "));
  path.setAttribute("fill", "none");
  path.setAttribute("stroke-width", "2.6");
  path.setAttribute("class", className);
  svg.append(path);
}

function scheduleProjectionChartScroll() {
  if (!financeState.projection.enabled || !financeState.projection.autoScrollPending) {
    return;
  }
  financeState.projection.autoScrollPending = false;
  window.requestAnimationFrame(() => {
    if (window.innerWidth <= 640) {
      const shell = financeEls.chart.parentElement;
      shell.scrollLeft = shell.scrollWidth;
    }
  });
}

function attachChartTooltip(svg, chartValues, visibleSeries, salaryPayments, salaryVisible, chart) {
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

  const titleRow = document.createElementNS("http://www.w3.org/2000/svg", "text");
  titleRow.setAttribute("class", "chart-tooltip-title");
  tooltip.append(titleRow);

  const rows = [];
  const rowCount = Math.max(1, visibleSeries.length);
  for (let index = 0; index < rowCount; index++) {
    const row = document.createElementNS("http://www.w3.org/2000/svg", "text");
    row.setAttribute("class", "chart-tooltip-row");
    tooltip.append(row);
    rows.push(row);
  }

  svg.append(tooltip);

  const tooltipWidth = 260;
  box.setAttribute("width", String(tooltipWidth));

  const moveTooltip = event => {
    const point = svg.createSVGPoint();
    point.x = event.clientX;
    point.y = event.clientY;
    const svgPoint = point.matrixTransform(svg.getScreenCTM().inverse());
    const boundedX = Math.max(chart.left, Math.min(chart.left + chart.plotWidth, svgPoint.x));
    const at = chart.start.getTime() + ((boundedX - chart.left) / chart.plotWidth) * chart.span;
    const nearest = nearestChartValue(chartValues, at);
    const nearestSalary = salaryVisible ? nearestSalaryPayment(salaryPayments, at) : null;
    const historyDistance = nearest ? Math.abs(new Date(nearest.snapshot.sampledAtUtc).getTime() - at) : Number.POSITIVE_INFINITY;
    const salaryDistance = nearestSalary ? Math.abs(nearestSalary.date.getTime() - at) : Number.POSITIVE_INFINITY;
    if (!nearest && !nearestSalary) {
      return;
    }

    const showSalary = nearestSalary && salaryDistance <= historyDistance;
    const selectedTime = showSalary ? nearestSalary.date.getTime() : new Date(nearest.snapshot.sampledAtUtc).getTime();
    const x = chart.left + ((selectedTime - chart.start) / chart.span) * chart.plotWidth;
    hoverLine.setAttribute("x1", x.toFixed(1));
    hoverLine.setAttribute("x2", x.toFixed(1));

    const tooltipItems = showSalary
      ? [{
          text: `${nearestSalary.projected ? "Projected " : ""}${nearestSalary.bonus ? "Bonus" : "Salary"}${nearestSalary.payment.accountName ? ` - ${nearestSalary.payment.accountName}` : ""}: ${money(nearestSalary.amount, nearestSalary.payment.currency)}`,
          color: colorForSeries("chart-line-salary")
        }]
      : visibleSeries.map(item => ({
          text: `${item.label}: ${money(nearest.values[item.key], chart.currency)}`,
          color: colorForSeries(item.className)
        }));
    const tooltipHeight = 30 + Math.max(1, tooltipItems.length) * 18;
    box.setAttribute("height", String(tooltipHeight));

    let tooltipX = x + 12;
    if (tooltipX + tooltipWidth > chart.width - chart.right) {
      tooltipX = x - tooltipWidth - 12;
    }
    tooltipX = Math.max(8, tooltipX);
    const tooltipY = Math.max(8, Math.min(chart.height - chart.bottom - tooltipHeight - 8, svgPoint.y - tooltipHeight / 2));
    box.setAttribute("x", tooltipX.toFixed(1));
    box.setAttribute("y", tooltipY.toFixed(1));

    titleRow.setAttribute("x", String(tooltipX + 12));
    titleRow.setAttribute("y", String(tooltipY + 20));
    titleRow.textContent = showSalary
      ? `${nearestSalary.projected ? "Estimated " : ""}${formatPostedOn(nearestSalary.payment.postedOn)}`
      : `${nearest?.snapshot.projected ? "Projected \u00b7 " : ""}${formatDateTime(nearest.snapshot.sampledAtUtc)}`;

    rows.forEach((row, index) => {
      const item = tooltipItems[index];
      row.setAttribute("visibility", item ? "visible" : "hidden");
      if (!item) {
        return;
      }
      row.setAttribute("x", String(tooltipX + 12));
      row.setAttribute("y", String(tooltipY + 42 + index * 18));
      row.setAttribute("fill", item.color);
      row.textContent = item.text;
    });

    tooltip.setAttribute("visibility", "visible");
  };

  overlay.addEventListener("mousemove", moveTooltip);
  overlay.addEventListener("mouseenter", moveTooltip);
  overlay.addEventListener("mouseleave", () => tooltip.setAttribute("visibility", "hidden"));
}

function nearestSalaryPayment(salaryPayments, time) {
  let nearest = null;
  let nearestDistance = Number.POSITIVE_INFINITY;
  for (const payment of salaryPayments) {
    const distance = Math.abs(payment.date.getTime() - time);
    if (distance < nearestDistance) {
      nearest = payment;
      nearestDistance = distance;
    }
  }

  return nearest;
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

function setActiveHistoryHandle(input) {
  financeEls.historyStart.classList.toggle("is-active", input === financeEls.historyStart);
  financeEls.historyEnd.classList.toggle("is-active", input === financeEls.historyEnd);
  financeEls.historyProjection.classList.toggle("is-active", input === financeEls.historyProjection);
}

function updateHistoryRangeFromInput(handle) {
  const range = financeState.dateRange;
  if (!range.initialized || !range.hasData) {
    return;
  }

  let startDay = Math.round(Number(financeEls.historyStart.value));
  let endDay = Math.round(Number(financeEls.historyEnd.value));
  if (financeState.projection.enabled) {
    if (handle !== "start") {
      return;
    }
    startDay = clampNumber(startDay, financeState.projection.sliderMinDay, financeState.projection.todayDay);
    range.startDay = startDay;
    range.endDay = financeState.projection.todayDay;
    range.userAdjusted = true;
    updateHistoryRangeControl();
    scheduleHistoryChartRender();
    return;
  }

  if (handle === "start") {
    startDay = clampNumber(startDay, range.minDay, endDay);
  } else {
    endDay = clampNumber(endDay, startDay, range.maxDay);
  }

  range.startDay = startDay;
  range.endDay = endDay;
  range.userAdjusted = true;
  updateHistoryRangeControl();
  scheduleHistoryChartRender();
}

function scheduleHistoryChartRender() {
  if (!financeState.data || historyRenderFrame !== null) {
    return;
  }

  historyRenderFrame = window.requestAnimationFrame(() => {
    historyRenderFrame = null;
    renderChart(financeState.data);
  });
}

function applyStoredFinanceUiPreferences(todayDay, range) {
  if (financeState.uiPreferencesApplied || !financeState.uiPreferencesLoaded) {
    return;
  }

  financeState.uiPreferencesApplied = true;
  const preferences = financeState.uiPreferences || {};
  const preferredStartDay = dayIndexFromPostedOn(preferences.historyStartOn);
  const preferredEndDay = dayIndexFromPostedOn(preferences.historyEndOn);
  const storedRangeWasAdjusted = preferredStartDay !== null || preferredEndDay !== null;
  if (range.hasData && storedRangeWasAdjusted) {
    range.startDay = clampNumber(preferredStartDay ?? range.startDay, range.minDay, range.maxDay);
    range.endDay = clampNumber(preferredEndDay ?? range.endDay, range.startDay, range.maxDay);
    range.userAdjusted = true;
  }

  const projectionDay = dayIndexFromPostedOn(preferences.projectionOn);
  if (!preferences.projectionEnabled || todayDay === null || projectionDay === null) {
    return;
  }

  const maximumDay = addCalendarMonthsToDayIndex(todayDay, maximumProjectionMonths);
  const savedRange = {
    startDay: range.startDay,
    endDay: range.endDay,
    userAdjusted: range.userAdjusted
  };
  const sliderMinDay = Math.min(range.minDay, todayDay);
  const projectionStartDay = dayIndexFromPostedOn(preferences.projectionStartOn);
  Object.assign(financeState.projection, {
    enabled: true,
    day: clampNumber(projectionDay, todayDay + 1, maximumDay),
    limitDay: addCalendarMonthsToDayIndex(todayDay, projectionHorizonMonths),
    todayDay,
    sliderMinDay,
    savedRange,
    userAdjusted: true,
    autoScrollPending: false
  });
  range.startDay = clampNumber(projectionStartDay ?? savedRange.startDay, sliderMinDay, todayDay);
  range.endDay = todayDay;
  range.userAdjusted = true;
}
function syncHistoryDateRange(data) {
  const range = financeState.dateRange;
  const extent = financeDatasetDayExtent(data);
  const serverToday = dayIndexFromTimestamp(data.nowUtc) ?? dayIndexFromTimestamp(new Date());
  if (!extent) {
    const onlyDay = serverToday ?? 0;
    Object.assign(range, {
      minDay: onlyDay,
      maxDay: onlyDay,
      startDay: onlyDay,
      endDay: onlyDay,
      initialized: true,
      userAdjusted: false,
      hasData: false
    });
    applyStoredFinanceUiPreferences(serverToday, range);
    syncProjectionRange(serverToday, range);
    updateHistoryRangeControl();
    return range;
  }

  const minDay = extent.minDay;
  const maxDay = Math.max(extent.maxDay, serverToday ?? extent.maxDay);
  const previous = { ...range };
  let startDay;
  let endDay;
  let userAdjusted = range.userAdjusted;

  if (!range.initialized || !range.hasData) {
    endDay = maxDay;
    startDay = Math.max(minDay, addCalendarMonthsToDayIndex(maxDay, -defaultHistoryMonths));
    userAdjusted = false;
  } else if (!range.userAdjusted) {
    endDay = maxDay;
    startDay = Math.max(minDay, addCalendarMonthsToDayIndex(maxDay, -defaultHistoryMonths));
  } else {
    // A manually selected range represents exact dates, even when either date
    // happened to be at the edge of the available data when it was selected.
    // Only clamp when the saved date is no longer inside the available domain.
    startDay = clampNumber(previous.startDay, minDay, maxDay);
    endDay = clampNumber(previous.endDay, minDay, maxDay);
    if (startDay > endDay) {
      startDay = endDay;
    }
  }

  Object.assign(range, {
    minDay,
    maxDay,
    startDay,
    endDay,
    initialized: true,
    userAdjusted,
    hasData: true
  });
  applyStoredFinanceUiPreferences(serverToday, range);
  syncProjectionRange(serverToday, range);
  updateHistoryRangeControl();
  return range;
}

function financeDatasetDayExtent(data) {
  const days = [];
  for (const snapshot of data.history || []) {
    const day = dayIndexFromTimestamp(snapshot.sampledAtUtc);
    if (day !== null) {
      days.push(day);
    }
  }

  for (const payment of data.income?.salaryPayments || []) {
    const day = salaryDayFromPostedOn(payment.postedOn);
    if (day !== null) {
      days.push(day);
    }
  }

  return days.length === 0
    ? null
    : { minDay: Math.min(...days), maxDay: Math.max(...days) };
}

function updateHistoryRangeControl() {
  const range = financeState.dateRange;
  const projection = financeState.projection;
  const projectionActive = projection.enabled && projection.todayDay !== null;
  financeEls.futureProjectionToggle.setAttribute("aria-pressed", String(projectionActive));
  financeEls.futureProjectionIndicator.textContent = projectionActive ? "\u2713" : "\u2197";
  financeEls.futureProjectionState.textContent = projectionActive ? "On" : "Off";
  financeEls.historyRangeControl.classList.toggle("is-projection-active", projectionActive);
  financeEls.historyProjection.hidden = !projectionActive;
  financeEls.historyProjectionValue.hidden = !projectionActive;
  financeEls.historyProjectionSelection.hidden = !projectionActive;
  financeEls.historyTodayLabel.hidden = !projectionActive;
  financeEls.projectionSummary.hidden = !projectionActive;

  if (projectionActive) {
    updateProjectionRangeControl(range, projection);
    return;
  }

  financeEls.historyEndKind.textContent = "End";
  const inputs = [financeEls.historyStart, financeEls.historyEnd];
  for (const input of inputs) {
    input.min = String(range.minDay ?? 0);
    input.max = String(range.maxDay ?? 0);
    input.step = "1";
    input.disabled = !range.hasData || range.minDay === range.maxDay;
  }

  financeEls.historyStart.value = String(range.startDay ?? range.minDay ?? 0);
  financeEls.historyEnd.value = String(range.endDay ?? range.maxDay ?? 0);
  financeEls.historyRangeControl.classList.toggle("is-empty", !range.hasData);
  financeEls.historyRangeControl.classList.toggle(
    "is-collapsed-at-min",
    range.hasData && range.startDay === range.minDay && range.endDay === range.minDay
  );
  financeEls.historyRangeControl.classList.toggle(
    "is-collapsed-at-max",
    range.hasData && range.startDay === range.maxDay && range.endDay === range.maxDay
  );

  if (!range.hasData) {
    financeEls.historyStartLabel.textContent = "--";
    financeEls.historyEndLabel.textContent = "--";
    financeEls.historyRangeLength.textContent = "No dated history";
    financeEls.historyRangeHelp.textContent = "Date controls are unavailable until dated history is recorded.";
    financeEls.historyMinLabel.textContent = "Earliest data";
    financeEls.historyMaxLabel.textContent = "Latest data";
    financeEls.historyRangeSelection.style.left = "0%";
    financeEls.historyRangeSelection.style.width = "100%";
    financeEls.historyStart.setAttribute("aria-valuetext", "No start date available");
    financeEls.historyEnd.setAttribute("aria-valuetext", "No end date available");
    return;
  }

  const today = dayIndexFromTimestamp(financeState.data?.nowUtc);
  const startText = formatHistoryDay(range.startDay);
  const endText = formatHistoryDay(range.endDay);
  financeEls.historyStartLabel.textContent = startText;
  financeEls.historyEndLabel.textContent = range.endDay === today ? `Today \u00b7 ${endText}` : endText;
  financeEls.historyRangeLength.textContent = describeHistoryDayRange(range.startDay, range.endDay);
  financeEls.historyRangeHelp.textContent = range.minDay === range.maxDay
    ? "Only one dated day is available."
    : "Drag either handle, or use the arrow keys when focused.";
  financeEls.historyMinLabel.textContent = `Earliest data \u00b7 ${formatHistoryDay(range.minDay)}`;
  financeEls.historyMaxLabel.textContent = range.maxDay === today
    ? "Today"
    : `Latest data \u00b7 ${formatHistoryDay(range.maxDay)}`;
  financeEls.historyStart.setAttribute("aria-valuetext", startText);
  financeEls.historyEnd.setAttribute("aria-valuetext", range.endDay === today ? `Today, ${endText}` : endText);

  const domain = range.maxDay - range.minDay;
  const startPercent = domain > 0 ? ((range.startDay - range.minDay) / domain) * 100 : 0;
  const endPercent = domain > 0 ? ((range.endDay - range.minDay) / domain) * 100 : 100;
  financeEls.historyRangeSelection.style.left = `${startPercent}%`;
  financeEls.historyRangeSelection.style.width = `${Math.max(0, endPercent - startPercent)}%`;
}

function updateProjectionRangeControl(range, projection) {
  const sliderMinDay = projection.sliderMinDay;
  const sliderMaxDay = projection.limitDay;
  const todayDay = projection.todayDay;
  const projectionDay = projection.day;
  const inputs = [financeEls.historyStart, financeEls.historyEnd, financeEls.historyProjection];
  for (const input of inputs) {
    input.min = String(sliderMinDay);
    input.max = String(sliderMaxDay);
    input.step = "1";
  }

  financeEls.historyStart.disabled = !range.hasData || sliderMinDay === todayDay;
  financeEls.historyEnd.disabled = true;
  financeEls.historyProjection.disabled = false;
  financeEls.historyStart.value = String(range.startDay);
  financeEls.historyEnd.value = String(todayDay);
  financeEls.historyProjection.value = String(projectionDay);
  financeEls.historyRangeControl.classList.remove("is-empty", "is-collapsed-at-min", "is-collapsed-at-max");

  const startText = formatHistoryDay(range.startDay);
  const todayText = formatHistoryDay(todayDay);
  const projectionText = formatHistoryDay(projectionDay);
  financeEls.historyStartLabel.textContent = startText;
  financeEls.historyEndKind.textContent = "Today";
  financeEls.historyEndLabel.textContent = todayText;
  financeEls.historyProjectionLabel.textContent = projectionText;
  financeEls.historyRangeLength.textContent = describeProjectionOffset(todayDay, projectionDay);
  financeEls.historyRangeHelp.textContent = "Drag the amber projection handle to forecast salary deposits; the green marker stays fixed at today.";
  financeEls.historyMinLabel.textContent = `Visible start \u00b7 ${formatHistoryDay(sliderMinDay)}`;
  financeEls.historyMaxLabel.textContent = `Projection limit \u00b7 ${formatHistoryDay(sliderMaxDay)}`;
  financeEls.historyStart.setAttribute("aria-valuetext", startText);
  financeEls.historyEnd.setAttribute("aria-valuetext", `Today, ${todayText}`);
  financeEls.historyProjection.setAttribute("aria-valuemin", String(todayDay + 1));
  financeEls.historyProjection.setAttribute("aria-valuemax", String(sliderMaxDay));
  financeEls.historyProjection.setAttribute("aria-valuenow", String(projectionDay));
  financeEls.historyProjection.setAttribute("aria-valuetext", `Projected through ${projectionText}`);

  const domain = sliderMaxDay - sliderMinDay || 1;
  const startPercent = ((range.startDay - sliderMinDay) / domain) * 100;
  const todayPercent = ((todayDay - sliderMinDay) / domain) * 100;
  const projectionPercent = ((projectionDay - sliderMinDay) / domain) * 100;
  financeEls.historyRangeSelection.style.left = `${startPercent}%`;
  financeEls.historyRangeSelection.style.width = `${Math.max(0, todayPercent - startPercent)}%`;
  financeEls.historyProjectionSelection.style.left = `${todayPercent}%`;
  financeEls.historyProjectionSelection.style.width = `${Math.max(0, projectionPercent - todayPercent)}%`;
  financeEls.historyTodayLabel.style.left = `${todayPercent}%`;
}

function filterHistoryByDateRange(history, startDay, endDay) {
  return history.filter(snapshot => {
    const day = dayIndexFromTimestamp(snapshot.sampledAtUtc);
    return day !== null && day >= startDay && day <= endDay;
  });
}

function filterSalaryPaymentsByDateRange(payments, startDay, endDay, currency) {
  return payments
    .filter(payment => payment.kind === "salary" && payment.currency === currency)
    .map(payment => {
      const day = salaryDayFromPostedOn(payment.postedOn);
      return {
        payment,
        amount: Number(payment.amount || 0),
        date: day === null ? new Date(Number.NaN) : dayIndexToLocalDate(day),
        day,
        projected: Boolean(payment.projected)
      };
    })
    .filter(point => Number.isFinite(point.amount) && point.amount > 0 && Number.isFinite(point.date.getTime())
      && point.day !== null && point.day >= startDay && point.day <= endDay)
    .sort((left, right) => left.date - right.date);
}

function buildSalaryProjection(data, todayDay, projectionDay) {
  const configuredSchedule = buildConfiguredSalarySchedule(data, todayDay);
  const schedules = configuredSchedule
    ? [configuredSchedule]
    : inferSalarySchedules(
        data.income?.salaryPayments || [],
        data.currency,
        todayDay,
        data.history || [],
        data.current
      );
  const payments = [];
  const recurringPayments = buildRecurringProjectionPayments(data, todayDay, projectionDay);
  let nextEstimatedPayment = null;
  for (const schedule of schedules) {
    let paymentDay = schedule.nextDay;
    if (!nextEstimatedPayment || paymentDay < nextEstimatedPayment.day) {
      nextEstimatedPayment = { day: paymentDay, amountCents: schedule.amountCents };
    }
    while (paymentDay <= projectionDay) {
      const amount = centsToNumber(schedule.amountCents);
      const payment = {
        ...schedule.sourcePayment,
        id: `projected:${schedule.key}:${paymentDay}`,
        postedOn: dayIndexToPostedOn(paymentDay),
        amount,
        currency: data.currency,
        kind: "salary",
        description: "Estimated from recorded salary cadence",
        projected: true
      };
      payments.push({
        payment,
        amount,
        amountCents: schedule.amountCents,
        date: dayIndexToLocalDate(paymentDay),
        day: paymentDay,
        projected: true
      });
      paymentDay = nextSalaryOccurrenceDay(paymentDay, schedule);
    }
  }
  payments.push(...buildBonusProjectionPayments(data, todayDay, projectionDay));

  payments.sort((left, right) => left.day - right.day
    || (left.payment.accountName || "").localeCompare(right.payment.accountName || ""));
  const incomeCents = payments.reduce((total, payment) => total + payment.amountCents, 0);
  const recurringCents = recurringPayments.reduce((total, payment) => total + payment.amountCents, 0);
  const projectedChangeCents = incomeCents + recurringCents;
  const cashCents = moneyToCents(data.current.totalCash) + projectedChangeCents;
  const netCents = moneyToCents(data.current.netAfterDebt) + projectedChangeCents;
  return {
    schedules,
    payments,
    recurringPayments,
    nextEstimatedPayment,
    incomeCents,
    recurringCents,
    cashCents,
    netCents,
    snapshots: buildProjectionSnapshots(data, todayDay, projectionDay, [...payments, ...recurringPayments])
  };
}

function buildConfiguredSalarySchedule(data, todayDay) {
  const salary = data.salaryPlan?.salary;
  if (!salary) return null;
  const amountCents = moneyToCents(salary.amount);
  let nextDay = dayIndexFromPostedOn(salary.nextOn);
  if (amountCents <= 0 || nextDay === null) return null;
  const sourceDate = new Date(nextDay * financeDayMilliseconds);
  const schedule = {
    key: "configured-salary",
    accountName: "Configured salary",
    interval: salary.interval,
    dayOfMonth: sourceDate.getUTCDate(),
    amountCents,
    nextDay,
    sourcePayment: {
      id: "configured-salary",
      accountName: "Configured salary",
      postedOn: salary.nextOn,
      amount: salary.amount,
      currency: data.currency,
      kind: "salary",
      description: "Configured salary projection"
    }
  };
  while (schedule.nextDay <= todayDay) {
    schedule.nextDay = nextSalaryOccurrenceDay(schedule.nextDay, schedule);
  }
  return schedule;
}

function nextSalaryOccurrenceDay(day, schedule) {
  if (!schedule.interval) return day + schedule.cadenceDays;
  if (schedule.interval === "weekly") return day + 7;
  if (schedule.interval === "biweekly") return day + 14;
  if (schedule.interval === "monthly") return nextMonthlyOccurrenceDay(day, schedule.dayOfMonth);
  if (schedule.interval === "semimonthly") {
    const date = new Date(day * financeDayMilliseconds);
    const year = date.getUTCFullYear();
    const month = date.getUTCMonth();
    const dayOfMonth = date.getUTCDate();
    const lastDay = new Date(Date.UTC(year, month + 1, 0)).getUTCDate();
    if (dayOfMonth < 15) {
      return Math.round(Date.UTC(year, month, 15) / financeDayMilliseconds);
    }
    if (dayOfMonth < lastDay) {
      return Math.round(Date.UTC(year, month, lastDay) / financeDayMilliseconds);
    }
    return Math.round(Date.UTC(year, month + 1, 15) / financeDayMilliseconds);
  }
  return day + 14;
}

function buildBonusProjectionPayments(data, todayDay, projectionDay) {
  const payments = [];
  for (const bonus of data.salaryPlan?.bonuses || []) {
    const day = dayIndexFromPostedOn(bonus.paidOn);
    const amountCents = moneyToCents(bonus.amount);
    if (day === null || day <= todayDay || day > projectionDay || amountCents <= 0) continue;
    const payment = {
      id: `projected-bonus:${bonus.id}`,
      accountName: "Bonus",
      postedOn: bonus.paidOn,
      amount: bonus.amount,
      currency: data.currency,
      kind: "bonus",
      description: bonus.description || "Bonus",
      projected: true
    };
    payments.push({
      payment,
      amount: bonus.amount,
      amountCents,
      date: dayIndexToLocalDate(day),
      day,
      projected: true,
      bonus: true
    });
  }
  return payments;
}

function buildRecurringProjectionPayments(data, todayDay, projectionDay) {
  const payments = [];
  for (const recurring of (data.recurringTransactions?.records || []).filter(item => item.status === "approved")) {
    let paymentDay = dayIndexFromPostedOn(recurring.nextOn);
    const amountCents = -Math.abs(moneyToCents(recurring.amount));
    if (paymentDay === null || amountCents === 0) continue;
    while (paymentDay <= todayDay) {
      paymentDay = nextMonthlyOccurrenceDay(paymentDay, recurring.dayOfMonth);
    }
    while (paymentDay <= projectionDay) {
      payments.push({
        payment: recurring,
        amount: centsToNumber(amountCents),
        amountCents,
        date: dayIndexToLocalDate(paymentDay),
        day: paymentDay,
        projected: true,
        recurring: true
      });
      paymentDay = nextMonthlyOccurrenceDay(paymentDay, recurring.dayOfMonth);
    }
  }
  return payments.sort((left, right) => left.day - right.day
    || String(left.payment.description || "").localeCompare(String(right.payment.description || "")));
}

function inferSalarySchedules(payments, currency, todayDay, history = [], current = null) {
  // Forecast only a dominant recent fixed-day cadence. Calendar-monthly,
  // semi-monthly, holiday-shifted, or stale histories remain unprojected
  // instead of inventing pay dates that the ledger cannot confirm.
  const grouped = new Map();
  for (const payment of payments) {
    const day = salaryDayFromPostedOn(payment.postedOn);
    const amountCents = moneyToCents(payment.amount);
    if (payment.kind !== "salary" || payment.currency !== currency || day === null || day > todayDay || amountCents <= 0) {
      continue;
    }
    const sourceKey = `${payment.accountId || payment.accountName || "salary"}:${payment.currency}`;
    if (!grouped.has(sourceKey)) {
      grouped.set(sourceKey, new Map());
    }
    grouped.get(sourceKey).set(day, payment);
  }

  const schedules = [];
  for (const [key, byDay] of grouped) {
    const entries = [...byDay.entries()]
      .map(([day, payment]) => ({ day, payment }))
      .sort((left, right) => left.day - right.day);
    if (entries.length < 3) {
      continue;
    }
    const patternEntries = entries.slice(-18);

    const intervals = [];
    for (let index = 1; index < patternEntries.length; index++) {
      const interval = patternEntries[index].day - patternEntries[index - 1].day;
      if (interval >= 5 && interval <= 45) {
        intervals.push(interval);
      }
    }
    if (intervals.length < 2) {
      continue;
    }

    const sortedIntervals = [...intervals].sort((left, right) => left - right);
    const medianInterval = sortedIntervals[Math.floor(sortedIntervals.length / 2)];
    const intervalCounts = new Map();
    for (const interval of intervals) {
      intervalCounts.set(interval, (intervalCounts.get(interval) || 0) + 1);
    }
    const cadenceCandidate = [...intervalCounts.entries()]
      .map(([interval, count]) => ({ interval, count }))
      .sort((left, right) => right.count - left.count
        || Math.abs(left.interval - medianInterval) - Math.abs(right.interval - medianInterval)
        || left.interval - right.interval)[0];
    if (!cadenceCandidate || cadenceCandidate.count < Math.max(2, Math.ceil(intervals.length * 0.35))) {
      continue;
    }

    const cadenceDays = cadenceCandidate.interval;
    const phaseCounts = new Map();
    for (const entry of patternEntries) {
      const phase = positiveRemainder(entry.day, cadenceDays);
      phaseCounts.set(phase, (phaseCounts.get(phase) || 0) + 1);
    }
    const [dominantPhase, dominantPhaseCount] = [...phaseCounts.entries()]
      .sort((left, right) => right[1] - left[1] || left[0] - right[0])[0] || [];
    if (dominantPhase === undefined || dominantPhaseCount < Math.max(2, Math.ceil(patternEntries.length * 0.5))) {
      continue;
    }
    const anchor = [...patternEntries].reverse().find(entry => positiveRemainder(entry.day, cadenceDays) === dominantPhase);
    if (!anchor || todayDay - anchor.day > Math.max(45, cadenceDays * 3)) {
      continue;
    }

    let sourceEntry = anchor;
    let nextDay = anchor.day + cadenceDays;
    for (const entry of patternEntries) {
      if (entry.day <= anchor.day) {
        continue;
      }
      while (nextDay + salaryScheduleMatchToleranceDays < entry.day) {
        nextDay += cadenceDays;
      }
      if (Math.abs(entry.day - nextDay) <= salaryScheduleMatchToleranceDays) {
        sourceEntry = entry;
        nextDay += cadenceDays;
      }
    }

    const amountCents = moneyToCents(sourceEntry.payment.amount);
    if (amountCents <= 0) {
      continue;
    }
    if (salaryPaymentReflectedInBalance(
      history,
      current,
      sourceEntry.payment.accountId,
      nextDay,
      amountCents,
      todayDay
    )) {
      nextDay += cadenceDays;
    }
    while (nextDay <= todayDay) {
      nextDay += cadenceDays;
    }
    schedules.push({
      key,
      accountName: sourceEntry.payment.accountName || "Salary",
      cadenceDays,
      amountCents,
      nextDay,
      sourcePayment: sourceEntry.payment
    });
  }

  return schedules.sort((left, right) => left.nextDay - right.nextDay || left.key.localeCompare(right.key));
}

function salaryPaymentReflectedInBalance(history, current, accountId, expectedDay, expectedAmountCents, todayDay) {
  if (!accountId || expectedAmountCents <= 0 || Math.abs(expectedDay - todayDay) > salaryScheduleMatchToleranceDays) {
    return false;
  }

  const snapshots = [...history, current]
    .filter(Boolean)
    .map(snapshot => {
      const account = (snapshot.accounts || []).find(candidate => candidate.id === accountId);
      const cashBalance = account?.cashBalance;
      return {
        day: dayIndexFromTimestamp(snapshot.sampledAtUtc),
        timestamp: new Date(snapshot.sampledAtUtc).getTime(),
        balanceCents: cashBalance === null || cashBalance === undefined ? null : moneyToCents(cashBalance)
      };
    })
    .filter(snapshot => snapshot.day !== null && Number.isFinite(snapshot.timestamp) && snapshot.balanceCents !== null)
    .sort((left, right) => left.timestamp - right.timestamp);

  const allowedDifferenceCents = Math.round(expectedAmountCents * salaryBalanceMatchToleranceRatio);
  for (let index = snapshots.length - 1; index > 0; index--) {
    const snapshot = snapshots[index];
    const previous = snapshots[index - 1];
    const increaseCents = snapshot.balanceCents - previous.balanceCents;
    if (snapshot.day > todayDay || Math.abs(snapshot.day - expectedDay) > salaryScheduleMatchToleranceDays) {
      continue;
    }
    if (increaseCents > 0 && Math.abs(increaseCents - expectedAmountCents) <= allowedDifferenceCents) {
      return true;
    }
  }
  return false;
}

function reconcileEarlySalaryCashHistory(history, salaryPayments, currency) {
  const sourceHistory = Array.isArray(history) ? history : [];
  const masterCurrency = String(currency || "").trim().toUpperCase();
  if (sourceHistory.length < 2 || !masterCurrency) {
    return sourceHistory.slice();
  }

  const snapshots = sourceHistory
    .map((snapshot, originalIndex) => ({
      snapshot,
      originalIndex,
      timestamp: new Date(snapshot?.sampledAtUtc).getTime(),
      day: dayIndexFromTimestamp(snapshot?.sampledAtUtc)
    }))
    .filter(entry => entry.day !== null && Number.isFinite(entry.timestamp))
    .sort((left, right) => left.timestamp - right.timestamp || left.originalIndex - right.originalIndex);
  if (snapshots.length < 2) {
    return sourceHistory.slice();
  }

  const groupedPayments = new Map();
  for (const payment of Array.isArray(salaryPayments) ? salaryPayments : []) {
    const accountId = String(payment?.accountId || "").trim();
    const accountKey = accountId.toLowerCase();
    const paymentCurrency = String(payment?.currency || "").trim().toUpperCase();
    const postedDay = salaryDayFromPostedOn(payment?.postedOn);
    const amountCents = moneyToCents(payment?.amount);
    if (payment?.kind !== "salary" || !accountKey || paymentCurrency !== masterCurrency || postedDay === null || amountCents <= 0) {
      continue;
    }

    const key = `${accountKey}\u0000${postedDay}`;
    const existing = groupedPayments.get(key);
    if (existing) {
      existing.amountCents += amountCents;
      existing.paymentAmountsCents.push(amountCents);
    } else {
      groupedPayments.set(key, { accountKey, postedDay, amountCents, paymentAmountsCents: [amountCents] });
    }
  }

  const adjustmentsBySnapshot = new Map();
  const syntheticSourcesByDay = new Map();
  const consumedTransitions = new Set();
  const groups = [...groupedPayments.values()]
    .sort((left, right) => left.postedDay - right.postedDay || left.accountKey.localeCompare(right.accountKey));

  for (const group of groups) {
    const earlyDay = group.postedDay - 1;
    const candidates = [];
    for (let index = 1; index < snapshots.length; index++) {
      const current = snapshots[index];
      const previous = snapshots[index - 1];
      if (current.day !== earlyDay) {
        continue;
      }

      const currentBalanceCents = snapshotAccountCashBalanceCents(current.snapshot, group.accountKey);
      const previousBalanceCents = snapshotAccountCashBalanceCents(previous.snapshot, group.accountKey);
      if (currentBalanceCents === null || previousBalanceCents === null) {
        continue;
      }

      const increaseCents = currentBalanceCents - previousBalanceCents;
      const transitionKey = `${group.accountKey}\u0000${current.originalIndex}`;
      if (increaseCents > 0 && !consumedTransitions.has(transitionKey)) {
        candidates.push({ current, increaseCents, transitionKey });
      }
    }

    const matches = matchSalaryBalanceTransitions(candidates, group.paymentAmountsCents);
    if (matches.length === 0) {
      continue;
    }

    let syntheticSource = null;
    for (const match of matches) {
      consumedTransitions.add(match.transitionKey);
      const adjustedEntries = snapshots.filter(entry => entry.day === earlyDay && entry.timestamp >= match.current.timestamp);
      for (const entry of adjustedEntries) {
        const adjustment = adjustmentsBySnapshot.get(entry.originalIndex) || { totalCents: 0, accountCents: new Map() };
        adjustment.totalCents += match.amountCents;
        adjustment.accountCents.set(group.accountKey, (adjustment.accountCents.get(group.accountKey) || 0) + match.amountCents);
        adjustmentsBySnapshot.set(entry.originalIndex, adjustment);
      }
      const latestAdjustedEntry = adjustedEntries[adjustedEntries.length - 1];
      if (latestAdjustedEntry && (!syntheticSource || latestAdjustedEntry.timestamp > syntheticSource.timestamp)) {
        syntheticSource = latestAdjustedEntry;
      }
    }

    const sourceBalanceCents = snapshotAccountCashBalanceCents(syntheticSource?.snapshot, group.accountKey);
    const reflectedOnPostedDay = sourceBalanceCents !== null && snapshots.some(entry => {
      if (entry.day !== group.postedDay) {
        return false;
      }
      const postedBalanceCents = snapshotAccountCashBalanceCents(entry.snapshot, group.accountKey);
      const allowedDifferenceCents = Math.round(group.amountCents * salaryBalanceMatchToleranceRatio);
      return postedBalanceCents !== null && Math.abs(postedBalanceCents - sourceBalanceCents) <= allowedDifferenceCents;
    });
    if (!reflectedOnPostedDay && syntheticSource) {
      const existingSource = syntheticSourcesByDay.get(group.postedDay);
      if (!existingSource || syntheticSource.timestamp > existingSource.timestamp) {
        syntheticSourcesByDay.set(group.postedDay, syntheticSource);
      }
    }
  }

  const reconciled = sourceHistory.map((snapshot, index) => {
    const adjustment = adjustmentsBySnapshot.get(index);
    if (!adjustment) {
      return snapshot;
    }

    const accounts = Array.isArray(snapshot.accounts)
      ? snapshot.accounts.map(account => {
          const accountKey = String(account?.id || "").trim().toLowerCase();
          const accountAdjustmentCents = adjustment.accountCents.get(accountKey) || 0;
          if (accountAdjustmentCents <= 0 || account?.cashBalance === null || account?.cashBalance === undefined) {
            return account;
          }
          return {
            ...account,
            cashBalance: centsToNumber(moneyToCents(account.cashBalance) - accountAdjustmentCents)
          };
        })
      : snapshot.accounts;

    return {
      ...snapshot,
      totalCash: centsToNumber(moneyToCents(snapshot.totalCash) - adjustment.totalCents),
      netAfterDebt: centsToNumber(moneyToCents(snapshot.netAfterDebt) - adjustment.totalCents),
      accounts,
      chartSalaryReconciled: true
    };
  });

  for (const [postedDay, source] of syntheticSourcesByDay) {
    reconciled.push({
      ...source.snapshot,
      sampledAtUtc: dayIndexToLocalDate(postedDay).toISOString(),
      accounts: Array.isArray(source.snapshot.accounts)
        ? source.snapshot.accounts.map(account => ({ ...account }))
        : source.snapshot.accounts,
      persistable: false,
      chartSalaryReconciled: true,
      chartSalaryPostedPoint: true
    });
  }

  return reconciled;
}

function snapshotAccountCashBalanceCents(snapshot, accountKey) {
  const account = (snapshot?.accounts || []).find(candidate =>
    String(candidate?.id || "").trim().toLowerCase() === accountKey);
  if (!account || account.cashBalance === null || account.cashBalance === undefined) {
    return null;
  }

  const numericBalance = Number(account.cashBalance);
  return Number.isFinite(numericBalance) ? moneyToCents(numericBalance) : null;
}

function matchSalaryBalanceTransitions(candidates, paymentAmountsCents) {
  const amounts = [...paymentAmountsCents].sort((left, right) => right - left);
  if (amounts.length > 1) {
    const assignments = amounts
      .map((amountCents, amountIndex) => ({
        amountCents,
        amountIndex,
        options: candidates
          .map((candidate, candidateIndex) => ({
            candidate,
            candidateIndex,
            differenceCents: Math.abs(candidate.increaseCents - amountCents)
          }))
          .filter(option => option.differenceCents <= Math.round(amountCents * salaryBalanceMatchToleranceRatio))
          .sort((left, right) => left.differenceCents - right.differenceCents || right.candidate.current.timestamp - left.candidate.current.timestamp)
      }))
      .sort((left, right) => left.options.length - right.options.length || right.amountCents - left.amountCents || left.amountIndex - right.amountIndex);
    const individualMatches = findSalaryTransitionAssignment(assignments);
    if (individualMatches) {
      return individualMatches.sort((left, right) => left.current.timestamp - right.current.timestamp);
    }
  }

  const totalAmountCents = amounts.reduce((total, amountCents) => total + amountCents, 0);
  const properSubsetSums = salaryPaymentProperSubsetSums(amounts, totalAmountCents);
  const allowedDifferenceCents = Math.round(totalAmountCents * salaryBalanceMatchToleranceRatio);
  const combinedMatch = candidates
    .map(candidate => ({
      ...candidate,
      differenceCents: Math.abs(candidate.increaseCents - totalAmountCents),
      closestProperSubsetDifferenceCents: Math.min(
        Number.POSITIVE_INFINITY,
        ...properSubsetSums.map(subsetCents => Math.abs(candidate.increaseCents - subsetCents))
      )
    }))
    .filter(candidate => candidate.differenceCents <= allowedDifferenceCents
      && candidate.differenceCents < candidate.closestProperSubsetDifferenceCents)
    .sort((left, right) => left.differenceCents - right.differenceCents || right.current.timestamp - left.current.timestamp)[0];
  return combinedMatch ? [{ ...combinedMatch, amountCents: totalAmountCents }] : [];
}

function salaryPaymentProperSubsetSums(amounts, totalAmountCents) {
  const sums = new Set([0]);
  for (const amountCents of amounts) {
    for (const existing of [...sums]) {
      sums.add(existing + amountCents);
    }
  }
  sums.delete(0);
  sums.delete(totalAmountCents);
  return [...sums];
}

function findSalaryTransitionAssignment(assignments, assignmentIndex = 0, usedCandidateIndexes = new Set(), matches = []) {
  if (assignmentIndex >= assignments.length) {
    return matches.slice();
  }

  const assignment = assignments[assignmentIndex];
  for (const option of assignment.options) {
    if (usedCandidateIndexes.has(option.candidateIndex)) {
      continue;
    }
    usedCandidateIndexes.add(option.candidateIndex);
    matches.push({ ...option.candidate, amountCents: assignment.amountCents });
    const completed = findSalaryTransitionAssignment(assignments, assignmentIndex + 1, usedCandidateIndexes, matches);
    if (completed) {
      return completed;
    }
    matches.pop();
    usedCandidateIndexes.delete(option.candidateIndex);
  }
  return null;
}

function buildProjectionSnapshots(data, todayDay, projectionDay, payments) {
  const eventsByDay = new Map();
  for (const payment of payments) {
    if (!eventsByDay.has(payment.day)) {
      eventsByDay.set(payment.day, { amountCents: 0, recurringItems: [] });
    }
    const event = eventsByDay.get(payment.day);
    event.amountCents += payment.amountCents;
    if (payment.recurring) {
      event.recurringItems.push({
        description: payment.payment.description || "Recurring transaction",
        amountCents: payment.amountCents
      });
    }
  }

  let cashCents = moneyToCents(data.current.totalCash);
  const debtCents = moneyToCents(data.current.totalDebt);
  const creditCents = moneyToCents(data.current.totalCreditAvailable);
  const snapshots = [];
  for (const [day, event] of [...eventsByDay.entries()].sort((left, right) => left[0] - right[0])) {
    const eventStart = dayIndexToLocalStartDate(day);
    snapshots.push(projectedSnapshot(eventStart.getTime() - 1, cashCents, debtCents, creditCents, false));
    cashCents += event.amountCents;
    snapshots.push(projectedSnapshot(
      eventStart.getTime(),
      cashCents,
      debtCents,
      creditCents,
      true,
      false,
      event.recurringItems
    ));
  }

  const projectionEnd = dayIndexToLocalEndDate(projectionDay);
  const lastSnapshotTime = snapshots.length === 0 ? dayIndexToLocalEndDate(todayDay).getTime() : new Date(snapshots[snapshots.length - 1].sampledAtUtc).getTime();
  if (projectionEnd.getTime() > lastSnapshotTime) {
    snapshots.push(projectedSnapshot(projectionEnd.getTime(), cashCents, debtCents, creditCents, false, true));
  } else if (snapshots.length > 0) {
    snapshots[snapshots.length - 1].projectionEndpoint = true;
  }
  return snapshots;
}

function projectedSnapshot(
  timestamp,
  cashCents,
  debtCents,
  creditCents,
  projectionEvent,
  projectionEndpoint = false,
  recurringItems = []) {
  return {
    sampledAtUtc: new Date(timestamp).toISOString(),
    totalCash: centsToNumber(cashCents),
    totalDebt: centsToNumber(debtCents),
    totalCreditAvailable: centsToNumber(creditCents),
    netAfterDebt: centsToNumber(cashCents - debtCents),
    projected: true,
    projectionEvent,
    projectionEndpoint,
    recurringItems
  };
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
    const snapshotPriority = snapshot.projectionBaseline ? 2 : snapshot.chartSalaryPostedPoint ? 1 : 0;
    const existingPriority = existing?.projectionBaseline ? 2 : existing?.chartSalaryPostedPoint ? 1 : 0;
    if (!existing || snapshotPriority > existingPriority
      || (snapshotPriority === existingPriority && sampledAtMs > new Date(existing.sampledAtUtc).getTime())) {
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

function axisRangeForDaySpan(daySpan) {
  if (daySpan === 0) {
    return "24h";
  }
  if (daySpan <= 7) {
    return "1w";
  }
  if (daySpan <= 31) {
    return "1m";
  }
  if (daySpan <= 93) {
    return "3m";
  }
  if (daySpan <= 183) {
    return "6m";
  }
  if (daySpan <= 366) {
    return "12m";
  }
  return "24m";
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

function accountCell(account, selectable = false) {
  const cell = document.createElement("td");
  const wrapper = document.createElement(selectable ? "button" : "div");
  wrapper.className = "account-name";
  if (selectable) {
    wrapper.type = "button";
    wrapper.classList.add("transaction-account-trigger");
    wrapper.setAttribute("aria-pressed", String(!financeState.transactionFilters.scopeAll && account.id === financeState.selectedTransactionAccountId));
    wrapper.title = `Show transactions for ${account.name}`;
    wrapper.addEventListener("click", () => {
      financeState.selectedTransactionAccountId = account.id;
      financeState.transactionFilters.scopeAll = false;
      renderTransactions(financeState.data);
      renderTables(financeState.data);
    });
  }
  wrapper.textContent = account.name;
  const institution = document.createElement("span");
  institution.textContent = [account.institution, account.loginUrl ? "website linked" : null].filter(Boolean).join(" - ");
  wrapper.append(institution);
  cell.append(wrapper);
  return cell;
}

function moneyCell(value, currency) {
  const cell = document.createElement("td");
  cell.className = "money-cell finance-private-value";
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

function aprCell(account) {
  const cell = document.createElement("td");
  cell.className = "money-cell apr-detail-cell";
  const button = document.createElement("button");
  button.type = "button";
  button.className = "apr-detail-trigger";
  const effectiveApr = effectiveAprPercent(account);
  button.textContent = formatAprPercent(effectiveApr);
  button.title = `Edit APR for ${account.name}`;
  button.setAttribute("aria-haspopup", "dialog");
  button.setAttribute("aria-controls", "aprEditorDialog");
  button.setAttribute("aria-label", `Edit APR for ${account.name}, currently ${formatAprPercent(effectiveApr)}`);
  button.addEventListener("click", () => openAprEditor(account));
  cell.append(button);
  return cell;
}

function nullableFiniteNumber(value) {
  if (value === null || value === undefined || value === "") {
    return null;
  }
  const numeric = Number(value);
  return Number.isFinite(numeric) ? numeric : null;
}

function effectiveAprPercent(account, asOfDay = null) {
  const reported = nullableFiniteNumber(account.effectiveAprPercent);
  if (reported !== null && asOfDay === null) {
    return reported;
  }

  const regularApr = nullableFiniteNumber(account.aprPercent);
  const promotionalApr = nullableFiniteNumber(account.promotionalAprPercent);
  const promotionEndsDay = dayIndexFromPostedOn(account.promotionalAprEndsOn);
  const todayDay = asOfDay
    ?? dayIndexFromTimestamp(financeState.data?.nowUtc)
    ?? dayIndexFromTimestamp(new Date());
  return promotionalApr !== null
    && promotionEndsDay !== null
    && todayDay !== null
    && todayDay < promotionEndsDay
      ? promotionalApr
      : regularApr;
}

function formatAprPercent(value) {
  const apr = nullableFiniteNumber(value);
  return apr === null ? "--" : `${apr.toFixed(2)}%`;
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
  return circle;
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

  if (item.discrete) {
    const marker = document.createElementNS("http://www.w3.org/2000/svg", "circle");
    marker.setAttribute("cx", String(x + 10));
    marker.setAttribute("cy", String(y + 8));
    marker.setAttribute("r", "4.5");
    marker.setAttribute("fill", colorForSeries(item.className));
    group.append(marker);
  } else {
    const line = document.createElementNS("http://www.w3.org/2000/svg", "line");
    line.setAttribute("x1", String(x));
    line.setAttribute("y1", String(y + 8));
    line.setAttribute("x2", String(x + 20));
    line.setAttribute("y2", String(y + 8));
    line.setAttribute("stroke", colorForSeries(item.className));
    line.setAttribute("stroke-width", "3");
    group.append(line);
  }

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
    "chart-line-net": "#6f5aa6",
    "chart-line-cash": "#117a56",
    "chart-line-debt": "#bd4f43",
    "chart-line-credit": "#245f8f",
    "chart-line-salary": "#d07a18"
  }[className] || "#65736e";
}

function money(value, currency) {
  return new Intl.NumberFormat([], { style: "currency", currency }).format(Number(value || 0));
}

function compactMoney(value, currency) {
  return new Intl.NumberFormat([], { style: "currency", currency, notation: "compact", maximumFractionDigits: 1 }).format(Number(value || 0));
}

function clampNumber(value, min, max) {
  return Math.max(min, Math.min(max, value));
}

function moneyToCents(value) {
  const numeric = Number(value || 0);
  return Number.isFinite(numeric) ? Math.round(numeric * 100) : 0;
}

function centsToNumber(value) {
  return Number(value || 0) / 100;
}

function positiveRemainder(value, divisor) {
  return ((value % divisor) + divisor) % divisor;
}

function dayIndexFromTimestamp(value) {
  const date = value instanceof Date ? value : new Date(value);
  if (!Number.isFinite(date.getTime())) {
    return null;
  }

  return Math.round(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()) / financeDayMilliseconds);
}

function dayIndexFromPostedOn(value) {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    return null;
  }

  const [year, month, day] = value.split("-").map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) {
    return null;
  }

  return Math.round(date.getTime() / financeDayMilliseconds);
}

function salaryDayFromPostedOn(value) {
  return dayIndexFromPostedOn(value);
}

function dayIndexToLocalDate(dayIndex) {
  const date = new Date(dayIndex * financeDayMilliseconds);
  return new Date(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate(), 12);
}

function dayIndexToLocalStartDate(dayIndex) {
  const date = new Date(dayIndex * financeDayMilliseconds);
  return new Date(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate());
}

function dayIndexToLocalEndDate(dayIndex) {
  const date = new Date(dayIndex * financeDayMilliseconds);
  return new Date(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate(), 23, 59, 59, 999);
}

function dayIndexToPostedOn(dayIndex) {
  const date = new Date(dayIndex * financeDayMilliseconds);
  const year = date.getUTCFullYear();
  const month = String(date.getUTCMonth() + 1).padStart(2, "0");
  const day = String(date.getUTCDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

function addCalendarMonthsToDayIndex(dayIndex, months) {
  const date = new Date(dayIndex * financeDayMilliseconds);
  const targetMonth = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + months, 1));
  const lastTargetDay = new Date(Date.UTC(targetMonth.getUTCFullYear(), targetMonth.getUTCMonth() + 1, 0)).getUTCDate();
  return Math.round(Date.UTC(
    targetMonth.getUTCFullYear(),
    targetMonth.getUTCMonth(),
    Math.min(date.getUTCDate(), lastTargetDay)
  ) / financeDayMilliseconds);
}

function nextMonthlyOccurrenceDay(dayIndex, requestedDayOfMonth) {
  const date = new Date(dayIndex * financeDayMilliseconds);
  const targetMonth = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + 1, 1));
  const lastTargetDay = new Date(Date.UTC(targetMonth.getUTCFullYear(), targetMonth.getUTCMonth() + 1, 0)).getUTCDate();
  const dayOfMonth = Math.max(1, Math.min(Number(requestedDayOfMonth) || date.getUTCDate(), lastTargetDay));
  return Math.round(Date.UTC(targetMonth.getUTCFullYear(), targetMonth.getUTCMonth(), dayOfMonth) / financeDayMilliseconds);
}
function formatHistoryDay(dayIndex) {
  return dayIndexToLocalDate(dayIndex).toLocaleDateString([], {
    month: "short",
    day: "numeric",
    year: "numeric"
  });
}

function formatHistoryDateRange(startDay, endDay) {
  const start = dayIndexToLocalDate(startDay);
  const end = dayIndexToLocalDate(endDay);
  if (startDay === endDay) {
    return formatHistoryDay(startDay);
  }

  const startOptions = start.getFullYear() === end.getFullYear()
    ? { month: "short", day: "numeric" }
    : { month: "short", day: "numeric", year: "numeric" };
  const startText = start.toLocaleDateString([], startOptions);
  const endText = end.toLocaleDateString([], { month: "short", day: "numeric", year: "numeric" });
  return `${startText} \u2013 ${endText}`;
}

function describeHistoryDayRange(startDay, endDay) {
  if (startDay === endDay) {
    return "1 day";
  }

  const start = new Date(startDay * financeDayMilliseconds);
  const end = new Date(endDay * financeDayMilliseconds);
  const monthDifference = (end.getUTCFullYear() - start.getUTCFullYear()) * 12
    + end.getUTCMonth() - start.getUTCMonth();
  if (monthDifference > 0 && addCalendarMonthsToDayIndex(startDay, monthDifference) === endDay) {
    return `${monthDifference} month${monthDifference === 1 ? "" : "s"}`;
  }

  const inclusiveDays = endDay - startDay + 1;
  if (inclusiveDays < 60) {
    return `${inclusiveDays} days`;
  }

  const approximateMonths = Math.max(2, Math.round((endDay - startDay) / 30.4375));
  return `${approximateMonths} months`;
}

function describeProjectionOffset(todayDay, projectionDay) {
  const futureDays = Math.max(1, projectionDay - todayDay);
  const futureRange = futureDays % 7 === 0
    ? `${futureDays / 7} week${futureDays === 7 ? "" : "s"}`
    : `${futureDays} day${futureDays === 1 ? "" : "s"}`;
  return `+${futureRange}`;
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

function formatPostedOn(value) {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    return "--";
  }

  const [year, month, day] = value.split("-").map(Number);
  return new Date(year, month - 1, day).toLocaleDateString([], {
    month: "short",
    day: "numeric",
    year: "numeric"
  });
}

function postedOnToDate(value) {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    return new Date(Number.NaN);
  }

  const [year, month, day] = value.split("-").map(Number);
  return new Date(year, month - 1, day, 12);
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
