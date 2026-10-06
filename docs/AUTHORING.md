# Writing documentation for this project

The first reader of these docs is usually an AI agent working in a clone of this repository: it searches with grep, reads whole files, and writes XML and SQL from what it finds. People read them too, mostly to review what an agent produced. So write for a reader who takes every sentence literally, can't ask a follow-up question, and will copy the first complete example it sees.

## Where things go

| File | Job | Size |
|---|---|---|
| `AGENTS.md` | Loaded automatically by most coding agents. Routes tasks to pages and lists the rules whose violation fails silently. `CLAUDE.md` imports it. | under 150 lines |
| `llms.txt` (and its identical copy `llms.md`) | The index, plus the rules and short patterns agents need most. One line per page, saying what it covers. | about 20 KB; move long prose to topic pages |
| `docs/reference/*.md` | Rules that apply to every endpoint, such as errors and status codes. | |
| `docs/topics/NN-*.md` | One reference page per feature, the source of truth. | under about 400 lines |
| `docs/tutorial/*.md` | A course for people. It must stay correct, but agents rarely read it. | |

## Topic page template

Sections in this order. [09 file uploads](topics/09-file-uploads.md) follows it fully. [10 file downloads](topics/10-file-downloads.md) is a shorter page and skips the sections it doesn't need.

1. **Front matter:** `title`, a one-sentence `summary`, and `keywords`. The keywords are the literal tags, placeholders, status codes and messages the page owns, so a grep for any of them finds the page. Add `applies_to` with the version the page describes.
2. **H1, then the agent banner:** `> For AI agents: read AGENTS.md first. The documentation index is llms.txt.`, with links.
3. **What it does,** when to use it, and what to use instead.
4. **Rules:** numbered and imperative. Each rule prevents a mistake that is silent or looks like it works.
5. **How it works:** the order of processing, especially what has happened before the query runs.
6. **Complete example:**
   - every file the task touches, with XML shown inside its real parent (`<settings><queries>`) and the SQL dialect named;
   - the tables;
   - the client request (browser and curl);
   - a table of what the client gets back for success and for each failure.
7. **Formats and choices:** a decision table where there is a choice.
8. **Contract:** what the query receives and must return, and a settings table with name, scope, default and notes.
9. **Errors:** exact messages and statuses, and a "silent failures" list of mistakes that return success.
10. **Do and don't:** short code pairs.
11. **Known issues:** with the version, linked to `TODO.md`.
12. **Related:** links one level deep.

## Writing rules

- **Copy names from the code.** Tags, column names, messages and defaults must match the source exactly. Check every claim against the code before you write it.
- **Match the shipped config.** Examples use the field names and settings in `DBToRestAPI/config/*.xml`, or say plainly where they differ. When an agent edits the shipped config and follows a page, the two must agree.
- **Say what happens when nothing happens.** A missing setting that makes a request succeed while doing nothing is the most important thing on a page.
- **State every default and every precedence order.** Don't leave a reader to guess either.
- **A bug is not a feature.** When the engine misbehaves, record it under Known issues and in `TODO.md`. Don't document the misbehaviour as the contract.
- **Keep one canonical example per task, and link to it.** Copies on other pages drift.
- **Name the SQL dialect.** Examples default to SQL Server. For anything dialect-specific (raising errors, parsing JSON), give the other databases too.
- **Keep it generic.** Never name a customer, a deployment or an internal system.

## Testing docs with agents

Run docs-only agents on real tasks before and after changing a page:

1. Give a fresh agent a realistic task. Allow it to read only `README.md`, `AGENTS.md`, `llms.txt`, `docs/` and `DBToRestAPI/config/*.xml`, never the C# source. Ask it to log every file it read and every search it ran.
2. Have a second agent grade the solution against the source code, with a rubric of what must be true, and name the documentation gap behind each failure.
3. Fix the page that misled it, or add an `AGENTS.md` rule if the failure is silent. Then run the task again.

The tasks used so far are:

- a form that posts fields and files, with validation;
- a partial update of a record's attachments;
- an owner-only download;
- a question-and-answer round on error statuses and rollback.
