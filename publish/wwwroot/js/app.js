"use strict";

/* ------------------------------------------------------------------ *
 *  Quiz Web App – client logic (fully offline, no external libraries)
 * ------------------------------------------------------------------ */

const $ = (id) => document.getElementById(id);

/* ------------------------------ state ------------------------------ */
const state = {
  config: null,          // from /api/config
  quizInfo: { subject: "", className: "", examType: "" },
  bank: [],              // questions in bank order (numbers = original, no correctIndices until review/detail)
  order: [],             // shuffled display order (subset/references of bank)
  index: 0,              // current display index
  answers: new Map(),    // displayIndex -> Set of option indices
  review: false,
  reviewData: null,      // Map<number, int[]> — populated only after submit when allowReview
  taken: false,
  zoom: 1,
  timerHandle: null,
  endTime: 0,
  student: { name: "", className: "", section: "" },
  submitting: false,
};

/* ----------------------------- helpers ----------------------------- */
async function api(url, options) {
  const res = await fetch(url, options);
  if (!res.ok) {
    const body = await res.text().catch(() => "");
    throw new Error("Server error (" + res.status + ")" + (body ? ": " + body.slice(0, 200) : ""));
  }
  return res.json();
}

function showView(name) {
  for (const v of ["view-start", "view-quiz", "view-score", "view-results"])
    $(v).hidden = v !== "view-" + name;
  window.scrollTo(0, 0);
}

function confirmDialog(title, text) {
  return new Promise((resolve) => {
    $("modal-title").textContent = title;
    $("modal-text").textContent = text;
    $("modal-no").hidden = false;
    $("modal-yes").textContent = "Yes";
    $("modal-backdrop").hidden = false;
    $("modal-yes").onclick = () => { $("modal-backdrop").hidden = true; resolve(true); };
    $("modal-no").onclick = () => { $("modal-backdrop").hidden = true; resolve(false); };
  });
}

function infoDialog(title, text) {
  return new Promise((resolve) => {
    $("modal-title").textContent = title;
    $("modal-text").textContent = text;
    $("modal-no").hidden = true;
    $("modal-yes").textContent = "OK";
    $("modal-backdrop").hidden = false;
    $("modal-yes").onclick = () => { $("modal-backdrop").hidden = true; $("modal-no").hidden = false; $("modal-yes").textContent = "Yes"; resolve(true); };
  });
}

/* ------------------------- input validation ------------------------- */
// Letters, digits, space, and a few safe punctuation marks only. Commas are
// rejected because a comma in a name would split the result CSV column.
function sanitize(value) {
  return value.replace(/[^A-Za-z0-9 .'\-]/g, "");
}
function sanitizeClass(value) {
  return value.replace(/[^0-9]/g, "").slice(0, 2);
}
function sanitizeSection(value) {
  return value.replace(/[^A-Za-z]/g, "").slice(0, 1).toUpperCase();
}
function sanitizeFor(el, value) {
  if (el.id === "in-class") return sanitizeClass(value);
  if (el.id === "in-section") return sanitizeSection(value);
  // name: allow 60 chars max, filtered
  if (el.id === "in-name") return sanitize(value).slice(0, 60);
  return sanitize(value);
}

function escapeHtml(value) {
  return String(value)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function wireStudentInput(el) {
  el.addEventListener("input", () => {
    const clean = sanitizeFor(el, el.value);
    if (clean !== el.value) {
      const pos = el.selectionStart;
      el.value = clean;
      el.selectionStart = el.selectionEnd = Math.min(pos, clean.length);
    }
    toggleStart();
  });
  el.addEventListener("paste", (e) => {
    e.preventDefault();
    const text = sanitizeFor(el, (e.clipboardData || window.clipboardData).getData("text") || "");
    const start = el.selectionStart, end = el.selectionEnd;
    const val = el.value.slice(0, start) + text + el.value.slice(end);
    // re-sanitize after splice (enforces max length)
    el.value = sanitizeFor(el, val);
    const pos = start + text.length;
    el.selectionStart = el.selectionEnd = Math.min(pos, el.value.length);
    toggleStart();
  });
}

function toggleStart() {
  $("btn-start").disabled = !(
    $("in-name").value.trim() && $("in-class").value.trim() && $("in-section").value.trim()
  );
}

/* ------------------------------- zoom ------------------------------- */
function applyZoom() {
  document.documentElement.style.setProperty("--zoom", state.zoom.toFixed(2));
  $("zoom-label").textContent = Math.round(state.zoom * 100) + "%";
  if (!state.review && $("question-image").src) sizeImage();
}

function changeZoom(delta) {
  state.zoom = Math.min(3, Math.max(0.5, +(state.zoom + delta).toFixed(2)));
  applyZoom();
}

/* ---------------------------- timer logic --------------------------- */
function startTimer() {
  stopTimer();
  state.endTime = Date.now() + state.config.timeMinutes * 60000;
  updateTimer();
  state.timerHandle = setInterval(tick, 250);
}

function stopTimer() {
  if (state.timerHandle) clearInterval(state.timerHandle);
  state.timerHandle = null;
}

function tick() {
  updateTimer();
  if (Date.now() >= state.endTime) {
    stopTimer();
    submitQuiz(false); // time up → auto submit
  }
}

function updateTimer() {
  const remain = Math.max(0, Math.ceil((state.endTime - Date.now()) / 1000));
  const m = String(Math.floor(remain / 60)).padStart(2, "0");
  const s = String(remain % 60).padStart(2, "0");
  const el = $("tb-timer");
  el.textContent = m + ":" + s;
  el.classList.toggle("low", remain <= 60);
}

/* ---------------------------- quiz header --------------------------- */
function quizHeaderInfo() {
  const info = state.quizInfo;
  const left = [];
  if (info.examType) left.push(info.examType);
  if (info.className) left.push("Class " + info.className);
  let result = left.join(", ");
  if (info.subject) result = result ? result + " - " + info.subject : info.subject;
  return result;
}

function renderCounter() {
  const label = state.review ? "Question (Review)" : "Question";
  $("tb-counter").textContent = label + " " + (state.index + 1) + " of " + state.order.length;
}

/* ------------------------- shuffle (Fisher–Yates) ------------------------- */
function shuffleArray(arr) {
  for (let i = arr.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [arr[i], arr[j]] = [arr[j], arr[i]];
  }
  return arr;
}

function shuffleBank() {
  const groups = new Map();
  for (const q of state.bank) {
    const key = q.groupId || "";
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(q);
  }
  const groupKeys = shuffleArray([...groups.keys()]);
  const order = [];
  for (const key of groupKeys) order.push(...shuffleArray(groups.get(key)));
  return order;
}

/* --------------------------- image handling -------------------------- */
function sizeImage() {
  const img = $("question-image");
  if (img.naturalWidth) img.style.width = Math.max(1, img.naturalWidth * state.zoom) + "px";
}

function wireImageResize() {
  const wrap = $("image-wrap");
  const handle = $("image-resize");
  let dragging = false, startY = 0, startH = 0;
  handle.addEventListener("mousedown", (e) => {
    dragging = true;
    startY = e.clientY;
    startH = wrap.offsetHeight;
    document.body.style.cursor = "ns-resize";
    e.preventDefault();
  });
  window.addEventListener("mousemove", (e) => {
    if (!dragging) return;
    const maxH = window.innerHeight * 0.6;
    const h = Math.max(60, Math.min(maxH, startH + (e.clientY - startY)));
    wrap.style.height = h + "px";
  });
  window.addEventListener("mouseup", () => {
    dragging = false;
    document.body.style.cursor = "";
  });
}

/* ---------------------------- render question ---------------------------- */
function renderQuestion() {
  const q = state.order[state.index];
  renderCounter();

  // Passage pane. Kept in the grid even when empty so the question/options
  // always occupy the same right-hand column (as if a passage were present).
  const pane = $("passage-pane");
  if (q.passage) {
    $("passage-text").textContent = q.passage;
    pane.classList.remove("empty");
  } else {
    $("passage-text").textContent = "";
    pane.classList.add("empty");
  }
  pane.hidden = false;

  $("question-text").innerHTML = "<pre>" + escapeHtml(q.text) + "</pre>";

  // Image
  const wrap = $("image-wrap");
  const img = $("question-image");
  if (q.image) {
    img.onload = () => sizeImage();
    img.src = q.image;
    img.alt = "Question image";
    wrap.hidden = false;
    sizeImage();
  } else {
    img.removeAttribute("src");
    wrap.hidden = true;
  }

  // Options
  const container = $("options");
  container.innerHTML = "";
  const multi = q.isMultiCorrect;
  const selected = state.answers.get(state.index) || new Set();

  // Review-mode status tag (marks questions the student never attempted).
  // The element is created on the fly if a stale/cached index.html does not
  // contain it, so a missing element can never break question rendering.
  let reviewStatus = $("review-status");
  if (!reviewStatus) {
    reviewStatus = document.createElement("div");
    reviewStatus.id = "review-status";
    reviewStatus.className = "review-status";
    reviewStatus.hidden = true;
    $("question-text").parentNode.insertBefore(reviewStatus, $("question-text"));
  }
  if (state.review) {
    if (selected.size === 0) {
      reviewStatus.textContent = "Not attempted";
      reviewStatus.hidden = false;
    } else {
      reviewStatus.hidden = true;
    }
  } else {
    reviewStatus.hidden = true;
  }

  q.options.forEach((optText, i) => {
    const letter = String.fromCharCode(65 + i);
    const row = document.createElement("label");
    row.className = "option";
    row.dataset.index = i;

    if (state.review) {
      const correctIndices = state.reviewData ? (state.reviewData.get(q.number) || []) : [];
      const isCorrect = correctIndices.includes(i);
      const isSelected = selected.has(i);
      if (isCorrect) row.classList.add("correct");
      else if (isSelected) row.classList.add("wrong");

      const input = document.createElement("span");
      input.className = "opt-letter";
      input.textContent = letter + ".";
      const text = document.createElement("span");
      text.textContent = "  " + optText;
      const mark = document.createElement("span");
      mark.className = "mark";
      if (isCorrect) mark.textContent = "✓ Correct answer";
      else if (isSelected) mark.textContent = "✗ Your answer";
      row.append(input, text, mark);
    } else {
      const input = document.createElement("input");
      input.type = multi ? "checkbox" : "radio";
      input.name = multi ? "opt" + state.index : "q" + state.index;
      input.value = i;
      input.checked = selected.has(i);
      input.addEventListener("change", () => onOptionChange(i, multi));

      const letterSpan = document.createElement("span");
      letterSpan.className = "opt-letter";
      letterSpan.textContent = letter + ".";
      const text = document.createElement("span");
      text.textContent = "  " + optText;
      row.append(input, letterSpan, text);
    }

    if (selected.has(i)) row.classList.add("selected");
    container.appendChild(row);
  });

  // Navigation
  $("btn-prev").disabled = state.index === 0;
  if (state.review) {
    $("btn-next").textContent = state.index === state.order.length - 1 ? "Finish Review" : "Next →";
  } else {
    $("btn-next").textContent = state.index === state.order.length - 1 ? "Submit" : "Next →";
  }
}

function onOptionChange(idx, multi) {
  if (state.review) return;
  const selected = state.answers.get(state.index) || new Set();
  if (multi) {
    if (selected.has(idx)) selected.delete(idx);
    else selected.add(idx);
  } else {
    selected.clear();
    selected.add(idx);
  }
  state.answers.set(state.index, selected);

  // Reflect selection styling
  document.querySelectorAll("#options .option").forEach((row) => {
    const i = +row.dataset.index;
    row.classList.toggle("selected", selected.has(i));
    if (!multi) {
      const input = row.querySelector("input");
      if (input) input.checked = selected.has(i);
    }
  });
}

/* ------------------------------ navigation ------------------------------ */
function go(delta) {
  const next = state.index + delta;
  if (next < 0 || next >= state.order.length) return;
  state.index = next;
  renderQuestion();
}

function saveProgressNow() {
  saveProgress();
}

/* ------------------------------ save progress ------------------------------ */
async function saveProgress() {
  if (state.submitting) return;
  if (!state.student.name) {
    showInfoDialog("Cannot Save", "Please enter your name first.");
    return;
  }
  if (state.answers.size === 0) {
    showInfoDialog("Cannot Save", "Please attempt at least one question first.");
    return;
  }
  state.submitting = true;
  stopTimer();

  const payload = {
    name: state.student.name,
    class: state.student.className,
    section: state.student.section,
    answers: buildAnswerPayload(),
  };

  try {
    const res = await api("/api/submit", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });

    state.submitting = false;
    if (res.alreadyTaken) {
      showInfoDialog("Already Taken", "You have already taken this test.");
      return;
    }
    showScore(res);
  } catch (err) {
    state.submitting = false;
    showInfoDialog("Error", "Could not save progress: " + err.message);
  }
}

// Helper: show a non-modal info dialog
function showInfoDialog(title, text) {
  return new Promise((resolve) => {
    $("modal-title").textContent = title;
    $("modal-text").textContent = text;
    $("modal-no").hidden = false;
    $("modal-yes").textContent = "OK";
    $("modal-backdrop").hidden = false;
    $("modal-yes").onclick = () => {
      $("modal-backdrop").hidden = true;
      resolve(true);
    };
    $("modal-no").onclick = () => {
      $("modal-backdrop").hidden = true;
      resolve(false);
    };
  });
}

async function nextClicked() {
  if (state.submitting) return;
  if (state.review) {
    if (state.index === state.order.length - 1) {
      const ok = await confirmDialog("Finish Review", "You have finished reviewing your answers.\n\nDo you want to exit the quiz?");
      if (ok) resetToStart();
    } else {
      go(1);
    }
    return;
  }
  if (state.index === state.order.length - 1) {
    const ok = await confirmDialog(
      "Final Submission",
      "This is the last question. Submitting will finalize your answers.\nYou will not be able to go back and revise.\n\nDo you want to submit?"
    );
    if (ok) submitQuiz(false);
  } else {
    go(1);
  }
}

/* ---------------------------- get local IP ---------------------------- */
function getLocalIP() {
  return new Promise((resolve) => {
    // Fast: try api.ipify.org with 5s timeout
    const xhr = new XMLHttpRequest();
    xhr.timeout = 5000;
    xhr.onerror = () => resolve('');
    xhr.ontimeout = () => resolve('');
    xhr.onload = () => {
      try {
        const data = JSON.parse(xhr.responseText);
        resolve(data.ip || '');
      } catch {
        resolve('');
      }
    };
    xhr.open("GET", "https://api.ipify.org?format=json", true);
    xhr.send();
  });
}

/* ------------------------------- submit ------------------------------- */
function buildAnswerPayload() {
  const payload = {};
  state.order.forEach((q, di) => {
    const selected = state.answers.get(di);
    if (!selected || selected.size === 0) return; // not attempted → blank
    const letters = [...selected].sort((a, b) => a - b).map((i) => String.fromCharCode(65 + i));
    payload[q.number] = letters.join("&");
  });
  return payload;
}

async function submitQuiz(confirmFirst) {
  if (state.submitting) return;
  if (confirmFirst) {
    const ok = await confirmDialog(
      "Final Submission",
      "This is the last question. Submitting will finalize your answers.\nYou will not be able to go back and revise.\n\nDo you want to submit?"
    );
    if (!ok) return;
  }
  state.submitting = true;
  stopTimer();

  // Submit immediately - IP will be captured server-side
  try {
    const res = await api("/api/submit", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: state.student.name,
        class: state.student.className,
        section: state.student.section,
        answers: buildAnswerPayload(),
        // ipAddress and computerName will be added server-side
      }),
    });
    if (res.alreadyTaken) {
      state.submitting = false;
      showTakenState(res.previousMarks);
      return;
    }

    if (res.review && Array.isArray(res.review)) {
      state.reviewData = new Map(res.review.map(r => [r.number, r.correctIndices || []]));
    } else {
      state.reviewData = null;
    }
    state.submitting = false;
    showScore(res);
  } catch (err) {
    state.submitting = false;
    const remain = Math.max(0, state.endTime - Date.now());
    if (remain > 0) {
      // Resume the clock with whatever time was left.
      state.endTime = Date.now() + remain;
      state.timerHandle = setInterval(tick, 250);
      showView("quiz");
      infoDialog("Error", "Your result could not be submitted: " + err.message +
        "\n\nPlease try again.");
    } else {
      infoDialog("Error", "Your result could not be submitted: " + err.message +
        "\n\nPlease contact the teacher.");
    }
  }
}

/* --------------------------- submit anyway --------------------------- */
async function submitAnywayClicked() {
  if (state.submitting || state.review) return;
  const ok = await confirmDialog(
    "Final Submission",
    "Submitting will finalize your answers.\nYou will not be able to go back and revise.\n\nDo you want to submit?"
  );
  if (ok) submitQuiz(false);
}

/* ------------------------------ score view ------------------------------ */
function showScore(res) {
  $("tb-counter").hidden = true;
  $("tb-timer").hidden = true;
  $("btn-submit-anyway").hidden = true;
  const info = quizHeaderInfo();
  let banner = state.student.name + "  |  Class: " + state.student.className +
    "  |  Section: " + state.student.section;
  if (info) banner += "\n" + info;
  banner += "\n\n";

  if (state.config.negativeMarkingPct > 0) {
    const pct = Math.round(state.config.negativeMarkingPct);
    banner += "Correct: " + res.correct + "\n" +
              "Wrong: " + res.wrong + "\n\n" +
              "Marks obtained: " + res.marks.toFixed(2) +
              "  (" + res.correct + " – " + res.wrong + " × " + pct + "%)\n" +
              "Out of " + res.total;
  } else {
    banner += "You scored " + res.correct + " out of " + res.total;
  }
  $("score-banner").textContent = banner;
  $("save-note").textContent = res.savePending
    ? "Your result is being saved in the background (the result file was busy)."
    : "";
  $("save-note").hidden = !res.savePending;
  showView("score");
}

function startReview() {
  state.review = true;
  state.index = 0;
  $("tb-counter").hidden = false;
  $("tb-timer").hidden = true;
  $("btn-submit-anyway").hidden = true;
  showView("quiz");
  renderQuestion();
}

/* ----------------------------- helper: ensure bank loaded ----------------------------- */
async function ensureBank() {
  if (state.bank.length > 0) return;
  const bankRes = await api("/api/questions");
  state.bank = bankRes.questions || [];
  if (bankRes.quizInfo) {
    state.quizInfo = {
      subject: bankRes.quizInfo.subject || "",
      className: bankRes.quizInfo.class || "",
      examType: bankRes.quizInfo.examType || ""
    };
  }
}

/* ----------------------------- start screen ----------------------------- */
async function startClicked() {
  if (state.submitting || state.taken) return;
  state.student.name = $("in-name").value.trim();
  state.student.className = $("in-class").value.trim();
  state.student.section = $("in-section").value.trim();
  if (!state.student.name || !state.student.className || !state.student.section) return;

  $("btn-start").disabled = true;
  try {
    const res = await api("/api/check", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(state.student),
    });
    if (res.activeSession) {
      $("btn-start").disabled = true;
      const info = quizHeaderInfo();
      let txt = state.student.name + "  |  Class: " + state.student.className +
        "  |  Section: " + state.student.section;
      if (info) txt += "\n" + info;
      txt += "\n\nThis student has given the test.\nView Results instead?";
      $("taken-banner").textContent = txt;
      $("taken-banner").hidden = false;
      $("btn-start").hidden = true;
      $("btn-exit-taken").hidden = false;
      $("btn-start").disabled = false;
      return;
    }
    if (res.alreadyTaken) {
      showTakenState(res.previousMarks);
      return;
    }
    await ensureBank();
    if (state.bank.length === 0) throw new Error("No questions available.");
    beginQuiz();
  } catch (err) {
    $("btn-start").disabled = false;
    showLoadError("Could not reach the quiz server: " + err.message);
  }
}

function showTakenState(prevMarks) {
  state.taken = true;
  $("btn-start").disabled = true;
  for (const id of ["in-name", "in-class", "in-section"]) $(id).readOnly = true;
  const info = quizHeaderInfo();
  let txt = state.student.name + "  |  Class: " + state.student.className +
    "  |  Section: " + state.student.section;
  if (info) txt += "\n" + info;
  const totalStr = state.bank.length > 0 ? " out of " + state.bank.length : "";
  txt += "\n\nYou have already taken this test.\nYour previous score: " +
    prevMarks.toFixed(2) + totalStr;
  $("taken-banner").textContent = txt;
  $("taken-banner").hidden = false;
  $("btn-start").hidden = true;
  $("btn-exit-taken").hidden = false;
  showView("start");
}

function beginQuiz() {
  state.order = shuffleBank();
  state.index = 0;
  state.answers = new Map();
  state.review = false;
  state.reviewData = null;
  state.taken = false;

  $("taken-banner").hidden = true;
  $("tb-counter").hidden = false;
  $("tb-timer").hidden = false;
  $("btn-submit-anyway").hidden = false;
  showView("quiz");
  renderQuestion();
  startTimer();
}

/* ------------------------------- results ------------------------------- */
async function openResults() {
  try {
    await ensureBank();
    const students = await api("/api/results/students");
    const sel = $("student-select");
    sel.innerHTML = "";
    state.resultsStudents = students;
    state.resultsSortKey = null;
    state.resultsSortDir = 1;
    $("results-table-wrap").hidden = true;
    $("results-table-btn-text").textContent = "Show Student Table";
    if (students.length === 0) {
      $("results-body").innerHTML = "<p class='hint'>No student results found yet.</p>";
    } else {
      students.forEach((s, i) => {
        const opt = document.createElement("option");
        opt.value = i;
        opt.dataset.name = s.name;
        opt.dataset.className = s.class;
        opt.dataset.section = s.section;
        opt.textContent = s.name + "  |  Marks: " + s.marks.toFixed(2) +
          "  |  IP: " + (s.ipAddress || "N/A") +
          "  |  Class: " + s.class + "  |  Section: " + s.section;
        sel.appendChild(opt);
      });
      sel.selectedIndex = 0;
    }
    $("btn-results-table").hidden = students.length === 0;
    renderResultsTable();
    showView("results");
    renderStudentDetail();
  } catch (err) {
    showLoadError("Could not load results: " + err.message);
  }
}

function resultsSortValue(key, s) {
  if (key === "marks") return s.marks;
  const v = s[key];
  return (v == null ? "" : String(v)).toLowerCase();
}

function renderResultsTable() {
  const key = state.resultsSortKey;
  const dir = state.resultsSortDir;
  let rows = state.resultsStudents.slice();
  if (key) {
    rows.sort((a, b) => {
      const av = resultsSortValue(key, a);
      const bv = resultsSortValue(key, b);
      if (av < bv) return -1 * dir;
      if (av > bv) return 1 * dir;
      return 0;
    });
  }
  const tb = $("results-tbody");
  tb.innerHTML = "";
  rows.forEach((s) => {
    const tr = document.createElement("tr");
    const displayName = s.name.length > 40 ? s.name.slice(0, 40) + "..." : s.name;
    const nameTd = document.createElement("td");
    nameTd.textContent = displayName;
    if (s.name.length > 40) nameTd.title = s.name;
    tr.appendChild(nameTd);
    [s.marks.toFixed(2), s.ipAddress || "N/A", s.class, s.section].forEach((c) => {
      const td = document.createElement("td");
      td.textContent = c;
      tr.appendChild(td);
    });
    tb.appendChild(tr);
  });
  document.querySelectorAll(".results-table thead th").forEach((th) => {
    const arrow = th.querySelector(".sort-arrow");
    if (th.dataset.sort === key) arrow.textContent = dir > 0 ? " \u25B2" : " \u25BC";
    else arrow.textContent = "";
  });
}

function resultsTableHeaderClicked(th) {
  const key = th.dataset.sort;
  if (!key) return;
  if (state.resultsSortKey === key) state.resultsSortDir *= -1;
  else { state.resultsSortKey = key; state.resultsSortDir = 1; }
  renderResultsTable();
}

function toggleResultsTable() {
  const wrap = $("results-table-wrap");
  wrap.hidden = !wrap.hidden;
  $("results-table-btn-text").textContent = wrap.hidden ? "Show Student Table" : "Hide Student Table";
  if (!wrap.hidden) renderResultsTable();
}

async function refreshResults() {
  const btn = $("btn-results-refresh");
  if (btn.disabled) return;
  btn.disabled = true;
  const origLabel = btn.textContent;
  btn.textContent = "\u21BB Refreshing...";
  try {
    const students = await api("/api/results/students");
    const sel = $("student-select");
    const prevOpt = sel.selectedOptions[0];
    const prevKey = prevOpt
      ? prevOpt.dataset.name + "\u0001" + prevOpt.dataset.className + "\u0001" + prevOpt.dataset.section
      : null;
    const wasOpen = !$("results-table-wrap").hidden;

    state.resultsStudents = students;
    sel.innerHTML = "";
    if (students.length === 0) {
      $("results-body").innerHTML = "<p class='hint'>No student results found yet.</p>";
      $("btn-results-table").hidden = true;
      $("results-table-wrap").hidden = true;
    } else {
      students.forEach((s, i) => {
        const opt = document.createElement("option");
        opt.value = i;
        opt.dataset.name = s.name;
        opt.dataset.className = s.class;
        opt.dataset.section = s.section;
        opt.textContent = s.name + "  |  Marks: " + s.marks.toFixed(2) +
          "  |  IP: " + (s.ipAddress || "N/A") +
          "  |  Class: " + s.class + "  |  Section: " + s.section;
        sel.appendChild(opt);
      });
      let restoreIdx = 0;
      if (prevKey) {
        for (let i = 0; i < sel.options.length; i++) {
          const o = sel.options[i];
          const k = o.dataset.name + "\u0001" + o.dataset.className + "\u0001" + o.dataset.section;
          if (k.toLowerCase() === prevKey.toLowerCase()) { restoreIdx = i; break; }
        }
      }
      sel.selectedIndex = restoreIdx;
      $("btn-results-table").hidden = false;
      $("results-table-wrap").hidden = wasOpen ? false : true;
    }
    renderResultsTable();
    await renderStudentDetail();
  } catch (err) {
    await infoDialog("Refresh failed", err.message);
  } finally {
    btn.disabled = false;
    btn.textContent = origLabel;
  }
}

async function renderStudentDetail() {
  const sel = $("student-select");
  const opt = sel.selectedOptions[0];
  $("results-body").innerHTML = "";
  // When answer details are disabled by config, show only the student list
  // in the dropdown and keep the detail area hidden (no details fetched).
  if (state.config && !state.config.allowAnswerDetails) {
    $("results-body").hidden = true;
    if (opt) {
      const hint = document.createElement("p");
      hint.className = "hint";
      hint.textContent = "Answer details are disabled by the teacher.";
      $("results-body").appendChild(hint);
      $("results-body").hidden = false;
    }
    return;
  }
  $("results-body").hidden = false;
  if (!opt) return;
  try {
    const d = await api("/api/results/detail?name=" + encodeURIComponent(opt.dataset.name) +
      "&className=" + encodeURIComponent(opt.dataset.className) +
      "&section=" + encodeURIComponent(opt.dataset.section));

    const summary = document.createElement("div");
    summary.className = "results-summary";
    summary.textContent = d.name + "   |   Class: " + d.class + "   |   Section: " + d.section +
      "\nMarks: " + d.marks.toFixed(2) + "   |   Date: " + d.date +
      "   |   IP: " + (d.ipAddress || "N/A") +
      "   |   Computer: " + (d.computerName || "N/A");
    $("results-body").appendChild(summary);

    const correctMap = d.correctMap || {};
    for (const q of state.bank) {
      const qno = q.number;
      const correctIndices = correctMap[qno] ?? correctMap[String(qno)] ?? [];
      const studentAns = d.answers && d.answers[qno] ? String(d.answers[qno]) : "";
      const correctLetters = correctIndices.map((i) => String.fromCharCode(65 + i)).join("&");
      const answered = !!studentAns.trim();
      const correct = answered && studentAns.toUpperCase() === correctLetters.toUpperCase();
      const verdict = !answered ? "Not attempted" : correct ? "Correct" : "Wrong";

      const block = document.createElement("div");
      block.className = "rq-block";

      const qel = document.createElement("div");
      qel.className = "rq-q";
      qel.textContent = "Q" + qno + ".  " + q.text;
      block.appendChild(qel);

      if (q.passage) {
        const pel = document.createElement("div");
        pel.className = "rq-passage";
        pel.textContent = q.passage;
        block.appendChild(pel);
      }

      const vel = document.createElement("div");
      vel.className = "rq-verdict " + (!answered ? "na" : correct ? "correct" : "wrong");
      vel.textContent = "Your answer: " + (answered ? studentAns : "(not answered)") +
        "      Correct: " + correctLetters + "      [" + verdict + "]";
      block.appendChild(vel);

      const chosen = answered ? studentAns.toUpperCase().split("&") : [];
      q.options.forEach((optText, i) => {
        const letter = String.fromCharCode(65 + i);
        const oel = document.createElement("div");
        oel.className = "rq-option";
        const isCorrectOpt = correctIndices.includes(i);
        const isSelected = chosen.includes(letter);
        if (isCorrectOpt) oel.classList.add("correct");
        if (isSelected && !isCorrectOpt) oel.classList.add("wrong");
        oel.textContent = letter + ".  " + optText +
          (isCorrectOpt ? "   (correct)" : "") +
          (isSelected && !isCorrectOpt ? "   <- your answer" : "");
        block.appendChild(oel);
      });

      $("results-body").appendChild(block);
    }
  } catch (err) {
    $("results-body").innerHTML = "<p class='hint'>Could not load details: " + err.message + "</p>";
  }
}

/* ------------------------------- import ------------------------------- */
async function importFile(file) {
  const fd = new FormData();
  fd.append("questions", file);
  try {
    const res = await api("/api/import", { method: "POST", body: fd });
    let msg = "Imported " + res.questionsWritten + " question(s) to questions.txt.";
    if (res.rowsSkipped > 0) msg += "\n" + res.rowsSkipped + " row(s) were skipped.";
    if (res.warnings.length > 0) {
      msg += "\n\n" + res.warnings.slice(0, 10).join("\n");
      if (res.warnings.length > 10) msg += "\n...and " + (res.warnings.length - 10) + " more warnings.";
    }
    msg += "\n\nYou can now start the quiz to test the questions.";
    await infoDialog("Import Complete", msg);
  } catch (err) {
    await infoDialog("Import Failed", err.message);
  }
}

/* ------------------------------ misc UI ------------------------------ */
function showLoadError(msg) {
  $("load-error").textContent = msg;
  $("load-error").hidden = false;
}

function resetToStart() {
  stopTimer();
  state.review = false;
  state.reviewData = null;
  state.taken = false;
  state.order = [];
  state.answers = new Map();
  state.index = 0;
  state.submitting = false;
  for (const id of ["in-name", "in-class", "in-section"]) {
    $(id).readOnly = false;
    $(id).value = "";
  }
  $("taken-banner").hidden = true;
  $("load-error").hidden = true;
  $("btn-start").hidden = false;
  $("btn-exit-taken").hidden = true;
  $("tb-counter").hidden = true;
  $("tb-timer").hidden = true;
  $("btn-submit-anyway").hidden = true;
  toggleStart();
  showView("start");
}

/* ------------------------------ boot ------------------------------ */
async function init() {
  // Extra "Exit" button only appears after the retake guard blocks someone.
  const exitBtn = document.createElement("button");
  exitBtn.id = "btn-exit-taken";
  exitBtn.className = "ghost";
  exitBtn.textContent = "Exit";
  exitBtn.type = "button";
  exitBtn.hidden = true;
  exitBtn.addEventListener("click", () => {
    state.taken = false;
    state.order = state.bank.slice();
    resetToStart();
  });
  document.querySelector(".start-card .actions").appendChild(exitBtn);

  wireStudentInput($("in-name"));
  wireStudentInput($("in-class"));
  wireStudentInput($("in-section"));
  wireImageResize();

  for (const id of ["in-name", "in-class", "in-section"]) {
    $(id).addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !$("btn-start").disabled) startClicked();
    });
  }

  $("btn-start").addEventListener("click", startClicked);
  $("btn-prev").addEventListener("click", () => go(-1));
  $("btn-next").addEventListener("click", nextClicked);
  $("btn-zoom-in").addEventListener("click", () => changeZoom(0.1));
  $("btn-zoom-out").addEventListener("click", () => changeZoom(-0.1));
  $("btn-submit-anyway").addEventListener("click", submitAnywayClicked);
  $("btn-review").addEventListener("click", startReview);
  $("btn-finish").addEventListener("click", async () => {
    const ok = await confirmDialog("Exit Quiz", "Do you want to exit the quiz?");
    if (ok) resetToStart();
  });
  $("btn-back-results").addEventListener("click", resetToStart);
  $("btn-results").addEventListener("click", openResults);
  $("btn-import").addEventListener("click", () => $("import-file").click());
  $("import-file").addEventListener("change", (e) => {
    if (e.target.files.length) importFile(e.target.files[0]);
    e.target.value = "";
  });
  $("student-select").addEventListener("change", renderStudentDetail);
  $("btn-results-table").addEventListener("click", toggleResultsTable);
  $("btn-results-refresh").addEventListener("click", refreshResults);
  document.querySelectorAll(".results-table thead th").forEach((th) =>
    th.addEventListener("click", () => resultsTableHeaderClicked(th)));

  try {
    state.config = await api("/api/config");
    state.quizInfo = {
      subject: state.config.subject || "",
      className: state.config.className || "",
      examType: state.config.examType || ""
    };

    $("quiz-title").textContent = state.quizInfo.examType || "Quiz";
    $("tb-title").textContent = state.quizInfo.examType || "Quiz";

    const infoParts = [];
    if (state.quizInfo.subject) infoParts.push(state.quizInfo.subject);
    if (state.quizInfo.className) infoParts.push("Class " + state.quizInfo.className);
    $("quiz-info").textContent = infoParts.join("  •  ");

    const tq = state.config.totalQuestions;
    const hintEl = $("hint-time");
    hintEl.classList.add("hint--meta");
    let html = "";
    if (typeof tq === "number" && tq > 0) html += "Number of questions: " + tq;
    html += (html ? "  |  " : "") + "Time: " + state.config.timeMinutes + " min";
    if (state.config.negativeMarkingPct > 0) {
      html += '  |  <span style="color:var(--red);font-weight:700">Negative marking: ' + Math.round(state.config.negativeMarkingPct) + '%</span>';
    }
    hintEl.innerHTML = html;

    $("btn-results").hidden = !state.config.allowResultViewing;
    $("btn-import").hidden = !state.config.allowImport;
    $("btn-review").hidden = !state.config.allowReview;
    toggleStart();
  } catch (err) {
    showLoadError("Could not load quiz config: " + err.message);
  }
}

document.addEventListener("DOMContentLoaded", init);

/* ------------------------------ admin ------------------------------ */
let adminToken = localStorage.getItem("adminToken") || null;

async function openAdmin() {
  $("admin-login-error").hidden = true;
  $("admin-msg").hidden = true;
  $("admin-pass").value = "";
  $("admin-newpass").value = "";
  $("admin-backdrop").hidden = false;
  try {
    const st = await fetch("/api/admin/status").then(r => r.json());
    $("admin-setup-hint").hidden = !!st.hasPassword;
    $("admin-login-btn").textContent = st.hasPassword ? "Login" : "Create Password";
  } catch {}
  $("admin-login").hidden = false;
  $("admin-panel").hidden = true;
  $("admin-pass").focus();
}

function closeAdmin() {
  $("admin-backdrop").hidden = true;
}

async function adminLogin() {
  const pass = $("admin-pass").value;
  if (!pass) { $("admin-login-error").textContent = "Enter password."; $("admin-login-error").hidden = false; return; }
  $("admin-login-error").hidden = true;
  try {
    const st = await fetch("/api/admin/status").then(r => r.json());
    let res;
    if (!st.hasPassword) {
      res = await fetch("/api/admin/setup", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ password: pass }) });
      if (!res.ok) throw new Error(await res.text());
      // now login
      res = await fetch("/api/admin/login", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ password: pass }) });
    } else {
      res = await fetch("/api/admin/login", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ password: pass }) });
    }
    if (!res.ok) {
      const txt = await res.text();
      throw new Error(res.status === 429 ? "Too many attempts. Try later." : (txt || "Login failed."));
    }
    const data = await res.json();
    if (!data.ok && !data.token) throw new Error("Login failed.");
    adminToken = data.token;
    localStorage.setItem("adminToken", adminToken);
    await loadAdminConfig();
    $("admin-login").hidden = true;
    $("admin-panel").hidden = false;
  } catch (err) {
    $("admin-login-error").textContent = err.message;
    $("admin-login-error").hidden = false;
  }
}

async function loadAdminConfig() {
  const res = await fetch("/api/admin/config", { headers: { "X-Admin-Token": adminToken || "" } });
  if (!res.ok) throw new Error("Failed to load config.");
  const c = await res.json();
  $("admin-time").value = c.timeMinutes ?? "";
  $("admin-neg").value = c.negativeMarkingPct ?? 0;
  $("admin-folder").value = c.quizFolder ?? "";
  $("admin-resultfile").value = c.resultFile ?? "";
  $("admin-port").value = c.port ?? "";
  $("admin-allowResultViewing").checked = !!c.allowResultViewing;
  $("admin-allowImport").checked = !!c.allowImport;
  $("admin-allowReview").checked = !!c.allowReview;
  $("admin-allowAnswerDetails").checked = !!c.allowAnswerDetails;
  $("admin-folders").textContent = (c.availableFolders && c.availableFolders.length) ? c.availableFolders.join(", ") : "(none)";
  $("admin-msg").hidden = true;
}

async function adminSave() {
  const payload = {
    time: parseInt($("admin-time").value, 10) || undefined,
    negativeMarking: $("admin-neg").value,
    quizFolder: $("admin-folder").value,
    resultFile: $("admin-resultfile").value || undefined,
    port: $("admin-port").value ? parseInt($("admin-port").value, 10) : undefined,
    allowResultViewing: $("admin-allowResultViewing").checked,
    allowImport: $("admin-allowImport").checked,
    allowReview: $("admin-allowReview").checked,
    allowAnswerDetails: $("admin-allowAnswerDetails").checked,
  };
  const newPass = $("admin-newpass").value.trim();
  if (newPass) {
    if (newPass.length < 4) { $("admin-msg").textContent = "New password must be at least 4 characters."; $("admin-msg").hidden = false; return; }
    payload.newAdminPassword = newPass;
  }
  $("admin-msg").textContent = "Saving...";
  $("admin-msg").hidden = false;
  try {
    const res = await fetch("/api/admin/config", { method: "POST", headers: { "Content-Type": "application/json", "X-Admin-Token": adminToken || "" }, body: JSON.stringify(payload) });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data.message || await res.text() || "Save failed.");
    if (data.needRestart) {
      $("admin-msg").textContent = "Saved. Restart required for folder/port. Click Restart Server.";
    } else {
      $("admin-msg").textContent = "Saved and hot-reloaded.";
      // refresh local config display
      try {
        state.config = await api("/api/config");
        state.quizInfo = { subject: state.config.subject || "", className: state.config.className || "", examType: state.config.examType || "" };
        $("quiz-title").textContent = state.quizInfo.examType || "Quiz";
        $("tb-title").textContent = state.quizInfo.examType || "Quiz";
        const tq2 = state.config.totalQuestions;
        const hintEl2 = $("hint-time");
        let html2 = "";
        if (typeof tq2 === "number" && tq2 > 0) html2 += "Number of questions: " + tq2;
        html2 += (html2 ? "  |  " : "") + "Time: " + state.config.timeMinutes + " min";
        if (state.config.negativeMarkingPct > 0) html2 += '  |  <span style="color:var(--red);font-weight:700">Negative marking: ' + Math.round(state.config.negativeMarkingPct) + '%</span>';
        hintEl2.innerHTML = html2;
        $("btn-results").hidden = !state.config.allowResultViewing;
        $("btn-review").hidden = !state.config.allowReview;
      } catch {}
    }
    $("admin-newpass").value = "";
  } catch (err) {
    $("admin-msg").textContent = "Save failed: " + err.message;
  }
  $("admin-msg").hidden = false;
}

async function adminRestart() {
  if (!confirm("Restart the server? Students will be disconnected for a moment.")) return;
  $("admin-msg").textContent = "Restarting...";
  $("admin-msg").hidden = false;
  try {
    const res = await fetch("/api/admin/restart", { method: "POST", headers: { "X-Admin-Token": adminToken || "" } });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data.title || "Restart failed.");
    $("admin-msg").textContent = "Restarting... Please wait 3 seconds and refresh (Ctrl+F5).";
    setTimeout(() => { closeAdmin(); location.reload(); }, 3500);
  } catch (err) {
    $("admin-msg").textContent = "Restart failed: " + err.message;
    $("admin-msg").hidden = false;
  }
}

function wireAdmin() {
  const b = $("btn-admin");
  if (b) b.addEventListener("click", openAdmin);
  const c1 = $("admin-cancel");
  if (c1) c1.addEventListener("click", closeAdmin);
  const c2 = $("admin-close");
  if (c2) c2.addEventListener("click", closeAdmin);
  const lb = $("admin-login-btn");
  if (lb) lb.addEventListener("click", adminLogin);
  const pass = $("admin-pass");
  if (pass) pass.addEventListener("keydown", e => { if (e.key === "Enter") adminLogin(); });
  const save = $("admin-save");
  if (save) save.addEventListener("click", adminSave);
  const rst = $("admin-restart");
  if (rst) rst.addEventListener("click", adminRestart);
  const backdrop = $("admin-backdrop");
  if (backdrop) backdrop.addEventListener("click", e => { if (e.target === backdrop) closeAdmin(); });
}
setTimeout(wireAdmin, 0);

// Save progress when user tries to leave the page (close tab, navigate, etc.)
window.addEventListener('beforeunload', function(event) {
    // Don't save if already submitting
    if (state.submitting) return;

    // Submit current progress before unloading
    saveProgressSilent();

    // Note: Modern browsers may still show their own beforeunload dialog,
    // but the progress will be saved regardless
    event.preventDefault();
    event.returnValue = '';
});

// Save progress when user switches tabs or minimizes the window
document.addEventListener('visibilitychange', function() {
    // If quiz is active and tab becomes hidden (user switches/minimizes)
    if (!state.submitting && document.visibilityState === 'hidden' && state.student.name) {
        // Don't save if quiz is resumed within 3 seconds
        // (user might just be reading something else briefly)
        setTimeout(() => saveProgressSilent(), 3000);
    }
});

// Helper function to submit current progress without confirm dialogs
async function saveProgressSilent() {
    if (state.submitting) return;
    if (!state.student.name) return; // Don't save if no student name
    if (state.answers.size === 0) return; // Don't save if no questions attempted

    state.submitting = true;
    stopTimer();

    // Build payload with current answers (only attempted questions)
    const payload = {
        name: state.student.name,
        class: state.student.className,
        section: state.student.section,
        answers: buildAnswerPayload(), // Only includes attempted questions
    };

    try {
        const res = await api("/api/submit", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(payload),
        });

        if (res.alreadyTaken) {
            state.submitting = false;
            // User already took it - just allow unload
            return;
        }

        state.submitting = false;
        // Show their progress/score
        showScore(res);
    } catch (err) {
        state.submitting = false;
        console.log("Could not save progress:", err);
    }
}
