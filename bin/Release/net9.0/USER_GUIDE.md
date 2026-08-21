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
