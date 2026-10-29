<div align="center">

# ClipNest

**Your clipboard, but it remembers.**

A local-first clipboard history for Windows. Every snippet you copy stays searchable —
offline, on your own disk, in a single SQLite file.

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4?logo=windows11&logoColor=white)](#requirements)
[![UI](https://img.shields.io/badge/UI-WPF-512BD4)](#)
[![Tests](https://img.shields.io/badge/tests-66%20passing-2C7A6B)](#build--test)
[![Network](https://img.shields.io/badge/network-0%20requests-2C7A6B)](#privacy)
[![License](https://img.shields.io/badge/license-TBD-lightgrey)](#license)

**English** · [简体中文](README.zh-CN.md)

</div>

---

## The problem

You copy a stack trace. Then a URL. Then a phone number.

The stack trace is gone. You were going to need it.

Windows keeps exactly one thing on the clipboard — the most recent. Everything else is silently
thrown away, dozens of times an hour. ClipNest is the net underneath: it quietly keeps every text
snippet you copy, and gives it back to you in under a second.

## What you get

- **Instant recall** — hit `Ctrl+Alt+V`, type two or three characters, press `Enter`. The snippet is
  back on your clipboard and the window is gone. No tab switching, no note-taking app.
- **Search that actually works for Chinese** — powered by SQLite FTS5 with a `trigram` tokenizer, so
  a one-character query like `码` matches immediately. Most clipboard tools drop to a full table scan
  here, or worse, match nothing at all.
- **Deduplication that respects your habits** — copy the same text ten times and you get *one* entry
  with a `copied 10×` badge, not ten rows. Copy A, then B, then A again: still two entries.
- **Quiet by design** — tray only. No toast on every copy, no splash, no account, no "sign in to
  continue". It never phones home because it has nowhere to phone.
- **Pause and denylist** — one click to stop collecting; password managers are excluded out of the
  box. `Ctrl+Alt+C` force-saves the current clipboard even while paused.
- **Pinned and starred entries survive cleanup** — when the store hits its cap, the oldest
  *unprotected* entries go first. Your keepers don't.

## Requirements

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download) **or newer** (`global.json` rolls forward to
  any later major, so .NET 9/10 works too)

## Try it

```bash
git clone https://github.com/mingq360/ClipNest.git
cd ClipNest
dotnet run --project src/ClipNest
```

First launch opens a short setup window: pick the hotkey that summons the window, and confirm which
apps should never be recorded. Then it disappears into the tray and starts collecting.

## Keyboard

The whole loop is keyboard-only — the window takes focus in the search box, so your hands never
leave the home row.

| Key | Action |
| --- | --- |
| `Ctrl+Alt+V` | Summon / dismiss the window (configurable) |
| `Ctrl+Alt+C` | Save the current clipboard right now (works while paused) |
| `↑` `↓` | Move through results without leaving the search box |
| `Enter` | Copy the selected snippet and hide |
| `Ctrl+P` | Pin / unpin |
| `Ctrl+S` | Star / unstar |
| `Delete` | Delete the selected snippet |
| `Esc` | Hide |

## How it works

```mermaid
flowchart LR
    A["Copy anything"] --> B["WM_CLIPBOARDUPDATE"]
    B --> C{"Denylisted app?"}
    C -- yes --> Z["ignored"]
    C -- no --> D["Fingerprint / dedupe"]
    D --> E[("SQLite + FTS5 trigram")]
    E --> F["Hotkey → type 2 chars"]
    F --> G["Enter → back to clipboard"]
```

There is no polling. The app registers `AddClipboardFormatListener` on a hidden message window and
wakes up only when the clipboard actually changes — idle CPU is zero.

## Design notes

The parts that took actual thought, in case you are building something similar.

**An index that quietly did nothing.** The obvious schema is `CREATE INDEX ... ON clips(content)` and
then query with `LIKE '%keyword%'`. `EXPLAIN QUERY PLAN` says `SCAN clips`: a leading wildcard can
never use a B-tree index. The index was decoration. Real fix: an FTS5 table with
`tokenize='trigram'`, where `LIKE` *can* be index-accelerated.

**`MATCH` is the wrong tool for Chinese.** Trigrams need at least three characters, so `MATCH '密码'`
returns zero rows — and two-character queries are exactly what people type in Chinese. `LIKE '%密码%'`
against the trigram table returns correct results for one, two, or three characters. So the query
path uses `LIKE ... ESCAPE '\'` uniformly. Bonus: `MATCH` chokes on an unescaped `"` with
`unterminated string`, while a parameterized `LIKE` cannot.

**A denylist that leaked.** The naive design asks `GetForegroundWindow()` when the clipboard event
arrives. Copy a password in your password manager, hit `Alt+Tab` immediately, and by the time the
message is processed the foreground window is something else — the denylist misses, and the password
lands in the database. ClipNest instead tracks the foreground window continuously with
`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` and reads a snapshot at event time.

**The clipboard echo.** Clicking a history entry writes it back to the clipboard, which fires another
`WM_CLIPBOARDUPDATE`, which would create a new entry — a small self-feeding loop. The writer records a
fingerprint of what it just wrote; an identical read within 1.5 s is recognised as an echo and dropped.

**Numbers, measured not guessed.** 5,000 entries: query latency 1–3 ms. A full
`INSERT` of 5,000 rows through the triggers: ~136 ms. Release build at rest: 48 MB private bytes,
128 MB working set (WPF's shared pages account for most of the gap — judge by private bytes).

## Privacy

ClipNest has no server, no telemetry, no update checker, no crash reporter. You can verify that with
a firewall rule and watch it make zero connections.

- **Data location** — `%LOCALAPPDATA%\ClipNest\` (`clipnest.db`, `settings.json`, `logs\`)
- **Denylist** — matched by process name, pre-seeded with common password managers
- **Pause** — stops the clipboard listener entirely, not just the writes
- **Honest limitation** — a denylist based on process names cannot tell apart a web-based password
  manager running inside your browser. If you keep secrets in a browser tab, use pause, or put the
  whole browser on the list.

## Status

| Area | State |
| --- | --- |
| Clipboard capture, history, search, copy-back | ✅ working |
| Pin / star / delete / clear, capacity cleanup | ✅ working |
| Tray, global hotkeys, single instance, autostart | ✅ working |
| Pause, denylist, first-run setup | ✅ working |
| Tags, source filtering, import/export | ⬜ roadmap |
| Images / rich text | ⬜ deliberately out of scope (text only) |

## Build & test

```bash
dotnet build ClipNest.sln
dotnet test tests/ClipNest.Tests
```

66 tests cover the repository layer, schema migrations, the classifier, the fingerprinting rules, and
the capture orchestration (pause, denylist, dedupe, truncation, capacity pruning).

```
src/ClipNest/
├── Domain/           entities, classification, dedupe fingerprint, repository contract
├── Services/         capture orchestration, settings, hotkey grammar
├── Infrastructure/   SQLite, clipboard listener, foreground tracker, hotkeys, logging
└── UI/               main window, settings, tray, view models
tests/ClipNest.Tests/ 66 tests
docs/                  requirements and design docs (Chinese)
```

The design docs in `docs/` record the decisions *and* the corrections — including the three bugs
above, which were caught and fixed while writing this code rather than after shipping it.

## License

No license has been chosen yet, which means all rights are reserved by default. If you want to use
any of this, open an issue and we will sort it out.
