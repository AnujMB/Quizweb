# Quiz System — A Simple Teacher's Guide

This guide is written for anyone using this quiz system for the first time. No technical knowledge is needed. If you can open a folder, edit a text file with Notepad, and use Excel, you can run a quiz.

---

## What is this?

A quiz program that runs on **one computer** in your classroom. Students open the quiz on their own computers in their web browser (Chrome, Edge, etc.) by typing one web address.

**No internet is needed.** Everything works over the school's own network.

---

## Part 1 — Starting the quiz

1. Open the folder that contains the program (the one called **publish**).
2. Double-click **QuizWeb.exe**.
3. A small black window opens. It shows a web address, for example:
   `http://192.168.100.4:5000`
4. Tell students to open their browser and type that address exactly, then press Enter.
5. **Keep the black window open** for the whole quiz. Closing it stops the quiz.

> The first time you run it, Windows may ask "Do you want to allow this app?" — click **Allow** (on private networks).

---

## Part 2 — Changing the settings (time, deductions, buttons)

All your settings are in one small text file called **config.txt**, in the same folder as `QuizWeb.exe`.

**How to change a setting:**

1. Right-click `config.txt`, choose **Open with** > **Notepad**.
2. Change a number or word, then click **File** > **Save**.
3. Close the black window and double-click `QuizWeb.exe` again to restart.

**What each line means:**

| Line | What it does | Example |
| --- | --- | --- |
| `Time=1` | Quiz time in **minutes**. Change the number to your desired time. | `Time=25` = 25 minutes |
| `NegativeMarking=25%` | Marks deducted for each wrong answer (as a % of one question). `0%` = no deduction. | `NegativeMarking=0%` |
| `allowResultViewing=true` | Shows the **View Results** button. Set to `false` to hide it from students. | `allowResultViewing=false` |
| `allowImport=true` | Shows the **Import Questions** button. Set to `false` to hide it from students. | `allowImport=false` |
| `allowReview=true` | Shows the **Review Answers** button (after the quiz). Set to `false` to hide it from students. | `allowReview=false` |
| `allowAnswerDetails=true` | In **View Results**, shows each student's detailed answers (question by question). Set to `false` to show only the student list in the dropdown, with no answer details. | `allowAnswerDetails=false` |
| `Port=5000` | Part of the web address. Leave it alone unless the address does not work. | `Port=5000` |
| `resultFile=Result.txt` | The file where results are saved. Leave it alone. | `resultFile=Result.txt` |

**To hide the two teacher buttons from students:** change `true` to `false` on the `allowResultViewing` and `allowImport` lines, save, and restart.

---

## Part 3 — Putting in your own questions

Your questions are in an Excel file called **questions.xlsx** (in the same folder as the program).

**The simplest way:**

1. Open **questions_template.xlsx** with Excel (it is a ready blank template).
2. Fill in the rows. **One row = one question.**
   - **Q.No** — the question number (1, 2, 3, ...).
   - **Question** — your question text.
   - **Options** — each option in its own column (A, B, C, D, ...).
   - **Correct** — the letter(s) of the right answer. For example `B`, or `B&D` if more than one answer is correct.
   - **Image** (optional) — the file name of a picture to show with the question.
   - **Passage** (optional) — a paragraph shown with the question (for example a reading passage). Questions that share the same passage stay together on screen.
3. Save the file, then copy it over the existing **questions.xlsx** (replace the old file).
4. Restart the program (close the black window and double-click `QuizWeb.exe` again).

**To check your questions work:** open the web address on your own computer and take the quiz yourself.

---

## Part 4 — During the quiz (what students see)

1. Students type their **Name, Class, and Section**, then click **Start Quiz**.
2. They answer the questions. **Next** and **Previous** buttons move between questions; the last question shows **Submit**.
3. The **timer** is at the top of the screen. Under 1 minute it turns red. When time runs out, the quiz saves automatically.
4. A student with the same Name/Class/Section cannot take the quiz twice — the program remembers and shows their previous score.

---

## Part 5 — After the quiz (results)

- Every result is saved automatically in **Result.txt** (same folder as the program). Open it with Excel to view, print, or copy.
- On the start screen, click **View Results** to look at each student's answers question by question.

---

## Quick help

- **Students cannot open the page?** Check the address is exactly what the black window shows, and that students are on the same school network. Check that you clicked **Allow** when Windows asked about the app.
- **The page looks old or nothing appears?** Ask students to press **Ctrl + F5** on their keyboard to refresh.
- **You changed config.txt but nothing changed?** You must restart the program after saving the file.

---

## Appendix A — Technical Guide (APIs, Security & App Behaviour)

> For IT support / advanced users. Teachers can skip this.

### 1. How the app works

* Single self-contained Kestrel server (`QuizWeb.exe`, `Program.cs:40`) serves static files from `publish/wwwroot` and an `images/` folder (`Program.cs:61`). Binds `http://0.0.0.0:{Port}` (default `5000`, `config.txt: Port=5000`). No database — questions from `questions.xlsx` (preferred) or `questions.txt`, results appended to `Result.txt` (`Data/ResultFile.cs`).
* Scoring is **server-side** (`Services/QuizEngine.cs:92`): client sends only selected letters (`A`, `B&D`), server recomputes `correct/wrong/marks` from the bank. Editing JS cannot inflate marks.
* Data files (`config.txt`, `questions.xlsx`, `Result.txt`) live next to the exe and are **not** web-served — only `wwwroot` + `/images` are exposed (`Program.cs:64`).

### 2. Config flags (server-enforced, restart required)

| Flag | File | Effect |
|---|---|---|
| `Time` | `config.txt` | Quiz minutes. Timer is client-side (`wwwroot/js/app.js:122`). |
| `NegativeMarking` | `config.txt` | `%` deducted per wrong (`QuizEngine.cs:108`). |
| `allowResultViewing` | `config.txt` | Gates `GET /api/results/students` and `/detail` → `403` if `false`. Hides **View Results** button (`app.js:1025`). |
| `allowReview` | `config.txt` | Gates `POST /api/submit` `review` field. Hides **Review Answers** button. When `false`, no correct answers ever leave the server. |
| `allowAnswerDetails` | `config.txt` | Gates `GET /api/results/detail` → `403` if `false`. Hides answer blocks in results view. |
| `allowImport` | `config.txt` | Gates `POST /api/import` → `403`. In `publish` set `false`. |
| `Port` / `resultFile` | `config.txt` | Network/file location. |

### 3. APIs (base `http://<host-ip>:5000`)

| Method | Path | Gating | Request | Response / Notes |
|---|---|---|---|---|
| `GET` | `/api/config` | none | — | `timeMinutes, negativeMarkingPct, allowResultViewing, allowImport, allowReview, allowAnswerDetails, subject, className, examType, resultFile` (`Program.cs:74`) |
| `GET` | `/api/questions` | none | — | `quizInfo, source, warnings, questions[]` where each `questions[i]` = `number, text, options, isMultiCorrect, image, passage, groupId` — **no `correctIndices`** (stripped `Program.cs:98`). Fetched **only after** `POST /api/check` succeeds (`app.js: ensureBank()`), so opening the site via `F12` shows no answers. |
| `POST` | `/api/check` | none | `{"name","class","section"}` | `alreadyTaken, previousMarks, activeSession`. Registers `Time+5 min` session to block same name on another PC (`Services/ActiveSessions.cs`). |
| `POST` | `/api/submit` | — | `{"name","class","section","answers":{"1":"A","2":"B&D"}}` | `alreadyTaken, previousMarks, marks, correct, wrong, attempted, total, saved, savePending` + **`review: [{number, correctIndices}]` only if `allowReview=true`** (`Program.cs:155`). `answers` keys are question numbers; server ignores unknown keys. IP auto-filled from TCP connection. |
| `GET` | `/api/results/students` | `allowResultViewing` | — | Array sorted `marks desc`: `name, class, section, subject, examType, quizClass, marks, date, computerName, ipAddress` (`Program.cs:166`). `403` if disabled. |
| `GET` | `/api/results/detail?name=&className=&section=` | `allowResultViewing && allowAnswerDetails` | query params | `name, class, section, marks, date, computerName, ipAddress, answers, correctMap` (`Program.cs:192`) where `correctMap` is `{ "1":[0], "2":[1,3] }` (indices A=0). `403` if gated, `404` if no match. |
| `POST` | `/api/import` | `allowImport` | `multipart/form-data` field `questions` (CSV) | `questionsWritten, rowsSkipped, warnings` — also regenerates `questions.xlsx`. `403` in `publish`. |

Test quickly (PowerShell):
```powershell
Invoke-RestMethod http://localhost:5000/api/config
Invoke-RestMethod http://localhost:5000/api/questions | % { $_.questions[0] | Format-List } # should NOT show correctIndices
Invoke-RestMethod http://localhost:5000/api/check -Method Post -ContentType "application/json" -Body '{"name":"Test","class":"9","section":"A"}'
```

### 4. Security — what was fixed and what remains

**Fixed (this release):**
* **Answer leak via `F12` closed.** `/api/questions` never contains answers; answers only return via `POST /api/submit` (`review`) and `GET /api/results/detail` (`correctMap`), both server-gated by `allowReview`/`allowAnswerDetails`. Before, the whole bank with answers was sent on page load.
* **Detail/Review now server-enforced.** Hiding the button alone is not enough — endpoints now return `403`/omit field when disabled.

**Still by design (LAN classroom, no login):**
* No authentication — anyone on LAN can submit under any name (sanitized `QuizEngine.cs:143`: letters/digits/space/`-` `.'` only), scrape `/api/results/students`, or spam `/api/check`. Mitigate by keeping `allowResultViewing=false` during exam and using `allowReview=false` until results time.
* Timer is client-side (`app.js:122`); a student can edit `state.endTime`. Server does not enforce deadline — acceptable for classroom, not for high-stakes.
* Plain `http://` — bank/results visible to a LAN packet sniffer. Use a tunnel (e.g. `cloudflared tunnel --url http://localhost:5000`) for HTTPS if exposing to internet.
* `isMultiCorrect` still sent (needed for radio vs checkbox) — reveals single vs multi-answer, minor.

### 5. App behaviour notes

* **Deferred bank load:** `init()` loads only `/api/config` (`app.js:1006`); questions load in `ensureBank()` after `Start` passes the retake guard. This is why `F12` before Start shows no questions.
* **Retake guard:** `Result.txt` + in-memory `ResultStore` + `ActiveSessions` (`Time+5 min`) block same `name/class/section`. Sanitized names prevent CSV injection (commas/newlines stripped `Data/ResultFile.cs:41`).
* **Result file:** exclusive OS file lock, header `Name,Class,Section,...,Q1..Qn` (`Data/ResultFile.cs:21`), migrated under lock if Q-count changes, queued retry if Excel has it open (`Services/ResultStore.cs`).
* **Static files:** `Cache-Control: no-cache, no-store` (`Program.cs:55`) forces fresh `index.html` on update — students `Ctrl+F5` if stale.

### 6. Going to internet

`0.0.0.0:5000` is LAN-only behind NAT. Connecting the host to internet does **not** expose it. For public access use `cloudflared tunnel --url http://localhost:5000` (gives `https://…trycloudflare.com`) — no port forwarding, works behind NTC CGNAT. Keep host awake (`Power > Screen and sleep = Never`).

---
