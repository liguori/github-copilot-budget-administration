"use strict";

const search = document.getElementById("budget-search");
const filters = document.querySelectorAll(".scope-filter");
let activeScope = "all";

function filterBudgets() {
    const query = (search?.value ?? "").trim().toLocaleLowerCase("it");
    let visible = 0;
    document.querySelectorAll("[data-budget-row]").forEach(row => {
        const matches = (activeScope === "all" || row.dataset.scope === activeScope)
            && row.dataset.search.toLocaleLowerCase("it").includes(query);
        row.hidden = !matches;
        if (matches) visible++;
    });
    const empty = document.getElementById("no-results");
    if (empty) empty.hidden = visible !== 0;
}
search?.addEventListener("input", filterBudgets);
filters.forEach(button => button.addEventListener("click", () => {
    activeScope = button.dataset.scope;
    filters.forEach(filter => {
        const selected = filter === button;
        filter.classList.toggle("selected", selected);
        filter.setAttribute("aria-pressed", String(selected));
    });
    filterBudgets();
}));

const scopeSelect = document.querySelector("select[name='Input.Scope']");
const targetSelect = document.querySelector("select[name='Input.Target']");
const expirationField = document.getElementById("expiration-field");
const expiration = document.getElementById("Input_ExpiresAt");
const costCenterUserRequirements = document.getElementById("cost-center-user-requirements");

function updateScope() {
    const scope = scopeSelect?.value ?? document.querySelector("input[name='Input.Scope']")?.value;
    if (!scope) return;
    if (costCenterUserRequirements) costCenterUserRequirements.hidden = scope !== "CostCenterUsers";
    if (targetSelect) {
        Array.from(targetSelect.options).forEach(option => {
            if (!option.value) return;
            const supported = option.dataset.forScope?.split(" ").includes(scope) === true;
            option.hidden = !supported;
            option.disabled = !supported;
        });
        if (targetSelect.selectedOptions[0]?.disabled) targetSelect.value = "";
    }
    if (expirationField && expiration) {
        expirationField.hidden = scope !== "User";
        if (scope !== "User") expiration.value = "";
    }
}
scopeSelect?.addEventListener("change", updateScope);
updateScope();

document.querySelectorAll("[data-language-select]").forEach(select => {
    select.addEventListener("change", () => select.form.requestSubmit());
});

document.querySelectorAll("form").forEach(form => {
    form.addEventListener("submit", () => {
        if (!form.checkValidity()) return;
        form.querySelectorAll("button[type='submit']").forEach(button => {
            button.disabled = true;
            button.textContent = document.body.dataset.loadingText;
        });
    });
});
