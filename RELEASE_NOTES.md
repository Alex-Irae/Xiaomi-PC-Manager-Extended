# Xiaomi Revamp 0.3.13

AI Center 0.3.9. PC Manager stays at 0.2.12 and Screen Translator at 0.2.7.

- **AI Center: "Name-only types" in Search settings.** The list of file types found by filename only (0.3.12) is now a field under "Excluded types", edited the same way: extensions separated by commas or spaces, applied at once, with undo and redo. A type added to the list loses its stored contents, and a type taken off is read again, as the indexer next visits each file; saving the field starts that pass unless indexing is set to manual, where "Index now" does it. Removing a type does not shrink the index file by itself; `compact` does.

# Xiaomi Revamp 0.3.12

AI Center 0.3.8. PC Manager stays at 0.2.12 and Screen Translator at 0.2.7.

- **AI Center: a window recovers when its embedded browser is gone.** After hours of standby, opening AI Center showed a black window and "The object invoked has disconnected from its clients", and only a reboot helped. The two windows share one browser process; when a window cannot be opened, that process is now ended and the window tries once more with a fresh one. If that fails as well, AI Center restarts itself (the launcher starts a new copy) instead of leaving a dead window. A window that loses its browser while open takes a new one. The resident check ends the browser under an open window and expects a working page again.
- **AI Center: source code is found by name only.** A new `name_only_extensions` setting in `config.json` lists types whose contents are never read or embedded (`.py .c .cpp .h .hpp .java .js .jsx .ts .tsx .html .css .sql .ps1 .sh`). On a whole-drive index with Program Files, code was 83% of 2.3 million passages and of the embedding work: the index had grown to 16 GB and the GPU worked through a night. Files stay findable by name and path. There is no control in Settings: edit the list and restart AI Center; an empty list reads every supported type again.
- **AI Center: `compact` command.** `python -m xiaomi_search --config <config.json> --data <data folder> compact`, run with AI Center closed, deletes the rows of files that are excluded or gone, removes the passages of name-only types, and shrinks the index file. Nothing is compacted automatically: rewriting a file of several gigabytes takes minutes.

# Xiaomi Revamp 0.3.11

PC Manager 0.2.12. AI Center stays at 0.3.7 and Screen Translator at 0.2.7.

- **PC Manager: the tray fix of 0.3.10 completed.** An app link still did nothing for Clash Verge in the tray: Windows reports its 13 by 13 helper window as the process's main window, and that window was added back after the filter. The same test now covers both ways a window is found. Checked on the installed program: with Clash Verge in the tray no window is offered for focusing, so the link starts it and its dashboard opens.

# Xiaomi Revamp 0.3.10

PC Manager 0.2.11, AI Center 0.3.7. Screen Translator stays at 0.2.7.

- **AI Center: results are ranked by file type.** A new `type_weights` setting in `config.json` multiplies a match's score by a number for its extension: 1.3 for documents and sheets (`.pdf .doc .docx .xls .xlsx .ppt .pptx .csv`), 1.2 for notes and settings files (`.json .yaml .yml .md .txt .toml .cfg .conf .xml .rtf`), 1.0 for audio and video, 0.7 for source code, and `default` (0.9) for everything else. Folders keep 1. A file opened from search before and an exact file name still come first, whatever their type, and a code file that matches by name, path and contents can still beat a document that matches once. Results from Windows Search of a low-priority type are lowered the same way. There is no control for it in Settings: edit the numbers in `config.json` and restart AI Center; a missing entry falls back to `default`, and an empty `{}` switches the ranking off.
- **AI Center: images are left out by default.** Image types (`.png .jpg .jpeg .gif .webp .bmp .ico .svg .tif .tiff .heic .avif .tga .dds`) join `.ini` and `.dll` in the default excluded file types; their names are rarely worth searching. Audio and video stay. Remove them from "Excluded file types" in Search settings to get images back.
- **AI Center: a whole-drive index reaches Program Files.** `Program Files` and `Program Files (x86)` are no longer in the default `excluded_names`, so settings files kept beside a program can be found. `Windows`, the recycle bin, dependency folders and the suite's own folder stay excluded. An existing `config.json` keeps its own list until edited.
- **PC Manager: an app link opens a program that sits in the tray.** A program in the tray can keep small helper windows; Clash Verge keeps a visible 13 by 13 tool window. 0.2.10 took that for the program's window, "focused" it and did nothing else. Tool windows are no longer counted, so the link starts the program again, and the running copy shows its window.

# Xiaomi Revamp 0.3.9

PC Manager 0.2.10. AI Center stays at 0.3.6 and Screen Translator at 0.2.7.

- **PC Manager: app links work with User Account Control switched off.** App links in the quick panel, and custom shortcuts that open an app, are started through the desktop's own Explorer so that they never inherit PC Manager's administrator rights, and PC Manager refused when that Explorer was itself elevated. With UAC off every program of an administrator account is elevated, Explorer included, so every link failed with "Windows Explorer could not open this app link". The desktop's Explorer is now accepted in that case. With UAC on the rule is unchanged.
- **PC Manager: an app link brings a running app forward.** When the linked program already shows a window, that window is restored and focused instead of the program being started again. When it is not running, or only sits in the tray, it is started as before, which lets it open its own window. Links with arguments (File Search, Screen Translator) always run their action.

# Xiaomi Revamp 0.3.8

PC Manager 0.2.9, AI Center 0.3.6. Screen Translator stays at 0.2.7.

- **AI Center: one quiet hint on every search result.** The "Meaning", "Windows" and "Recent choice" badges are gone; they looked different from each other and said little, since almost every result is found by meaning. Each row now shows, in the same muted style, "Opened 3 Oct" when you opened that file from search before, otherwise the date the file was last changed; programs still say "Run". The tooltip keeps the reasons a row matched. Windows' own last-opened date is not used: on a synchronised, indexed drive every file shows this month.
- **PC Manager: shared colours are published at start.** A palette chosen before 0.3.7 reached the other apps only after pressing Apply on the Shared appearance card or changing the appearance. PC Manager now writes its theme, accent and window colours to the shared settings when it starts, and only when they differ.
- **REQUIREMENTS.txt.** One plain list of what each app needs on the PC, what it carries with it and what a developer needs, kept at the top of the source and written into the installation folder by the upgrade.
- **Query benchmark.** `source/file-search/tests/benchmark_query_devices.py` times the device-dependent step of a search (turning the query into a vector) on GPU, NPU and CPU, back to back and after idle pauses.

# Xiaomi Revamp 0.3.7

PC Manager 0.2.8, AI Center 0.3.5, Screen Translator 0.2.7. FileSync 1.3.5 belongs with this release.

- **PC Manager: an Apps card on the Toolbox page.** One row per installed companion app (File Search, Screen Translator, FileSync): what it is doing, a Start with Windows switch, Open and Quit. It replaces the separate File Search and Screen Translator cards. The switch works whether the app is running or closed: a running app is told and applies it itself; for a closed suite app PC Manager writes that app's setting and its Windows startup entry; FileSync is asked through its own command line. For File Search the switch is the "start file search with Windows" setting; AI Center's tray icon keeps its own option.
- **Shared appearance carries the window colours too.** With "Share theme, accent and window colours" on, a custom or saved PC Manager palette also gives its background and card colours to File Search, AI Center, Screen Translator and FileSync. The four built-in presets share theme and accent as before and leave each theme's own colours. Every app can still opt out.
- **FileSync follows the shared appearance** when it is installed inside the Xiaomi Revamp folder (see FileSync 1.3.5). Installed anywhere else, or with PC Manager's sharing off, it keeps its own theme and accents.
- **Setup: an app can travel with its own installer.** The suite setup offers only the apps whose archive lies beside it, and the setup and launchers report the current versions (see 0.3.6).
- **Test copies say "-test".** PC Manager's test mode titles read "-test" instead of "· Test"; test copies are built from this source with `tools/test`.

Not changed: Screen Translator's floating toolbar keeps its own two background colours.

# Xiaomi Revamp 0.3.6

PC Manager 0.2.7, AI Center 0.3.4, Screen Translator 0.2.6.

- **AI Center: file search no longer needs AI Center.** They are one program with two faces. File search (the shortcut and the search bar) starts hidden with Windows, without a tray icon. AI Center is the launcher window and its tray icon, shown once AI Center is opened. "Quit AI Center" in the tray now removes the icon and the window and leaves the search shortcut working; before, it ended the program and the next shortcut press had to start everything again. Search settings has two startup options: "Start file search with Windows and keep it running without AI Center" (the existing option, on by default; when off, quitting AI Center also stops file search) and "Start AI Center with Windows (tray icon)" (new, off by default). PC Manager starts file search in the background only when one of the two is on.
- **AI Center: one startup entry.** An installation older than the suite left a second "LocalAICenter" startup entry for the same program; it is removed when the app starts.
- **Screen Translator: contour on the floating toolbar.** A two-pixel outline in the accent colour, so the toolbar stays visible on a background of its own colour.
- **Screen Translator: starting with Windows is off unless chosen.** The option stays in Settings ("Start in the tray at sign-in"). The translation shortcut still works through PC Manager, which starts the app on demand.
- **PC Manager: NPU in the monitor.** The full and medium monitor views gain an NPU row on laptops with a neural processor. The value is what Task Manager shows: the "Neural" engine type of Windows' GPU counters, summed over the processes using it. It is read only while the monitor is open. The single-metric view and the tray indicator have no NPU choice.
- **PC Manager: Screen Translator icon centred** in the quick panel. Its drawing sat half a unit up and left of the centre of its box.
- **AI Center: quieter logs.** The indexer wrote one line per file into two log files (393,000 lines in three days on a whole-drive index). It now reports every 250 files and when the queue empties.
- **Setup: an app can travel with its own installer.** The suite setup now offers only the apps whose archive lies beside it, so a folder holding the setup, `packages.json` and one archive installs that one app. The setup and the launchers also report the current versions again; they had stayed at 0.3.1 and 0.2.2 in Windows' installed-apps list. The build refuses a mismatch from now on. Launchers and setup programs built before this change still carry the old numbers.
- **Measurement tool.** `tools/measure_apps.py` samples CPU, memory, disk writes, GPU and NPU use of the running apps and FileSync into a numbered results folder.

# Xiaomi Revamp 0.3.5

PC Manager 0.2.6, AI Center 0.3.3, Screen Translator 0.2.5.

- **One set of control behaviour for every app.** Button states, the top-bar icon buttons, the bottom notice and the undo/redo commands now come from two shared files, `source/shared/ui/revamp.css` and `revamp.js`, copied into each app by `tools/sync_ui.py` (the build refuses a stale copy). FileSync 1.3.3 uses the same two files. Each app's own versions of these rules were removed.
- **Buttons.** Filled, soft and outlined buttons dim slightly on hover and darken with a ring while pressed; soft buttons gain a tinted fill and a thin ring on hover. Icon buttons in the top bar (undo, redo, minimize, maximize, close) get a tinted square on hover and close turns red. Disabled and keyboard-focus looks are the same everywhere. Before, only PC Manager had a pressed state.
- **Notice at the bottom.** AI Center showed its messages as small text under the settings form and Screen Translator showed none on save; both now use the same bottom notice as PC Manager: "Applied." in AI Center, "Settings saved.", "Change undone." and "Change restored." in Screen Translator. A notice stays 5 seconds, an error 8.
- **Undo and redo.** Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z now work in AI Center and Screen Translator as they did in PC Manager, and are left to the field while typing in a text box. PC Manager's undo, redo, minimize and close icons are the same drawings as in the other apps.

Not changed: the page layouts (PC Manager and FileSync keep their sidebar, AI Center and Screen Translator their tabs), PC Manager's quick panel and AI Center's search bar, which do not load the shared files.

# Xiaomi Revamp 0.3.4

PC Manager 0.2.5, AI Center 0.3.2, Screen Translator 0.2.4.

- **All apps: windows load about two seconds sooner.** Each window is served from disk under a made-up host name. Chromium still tried to resolve that name for every window and waited about two seconds for the lookup to fail before loading scripts and styles. Lookups now fail at once (`--host-resolver-rules="MAP * ~NOTFOUND"`); nothing in these pages uses the network. Measured on PC Manager's main window: page load 2,120 ms before, 131 ms after, with no extra memory or background work.
- **PC Manager: the Xiaomi key opens the quick panel on release.** With a double-press action set, a single press used to wait out the double-press window (300 ms by default) before opening the panel. The panel now opens at once; a second press inside the window closes it again and runs the double-press action. Other single-press actions still wait, since they cannot be undone.
- **AI Center: version and XiaoAI.** The version was always shown as unknown because the native window answered the settings request without it; it now shows, at the bottom of Search settings instead of the Overview. The Overview gains a XiaoAI card, shown when XiaoAI is installed, with the logo read from the app's own Assets folder.
- **Screen Translator: opening from the tray.** After a tray start, the first time the window was opened it hid itself again as soon as its page had loaded. It now stays open.
- **Screen Translator: top bar.** Same bar as AI Center: undo and redo on the left, the title as the drag area, drawn minimize, maximize and close buttons.

# Xiaomi Revamp 0.3.3

PC Manager 0.2.4. AI Center stays at 0.3.1 and Screen Translator at 0.2.3.

- **PC Manager: one shortcut list.** The Keyboard page has a single Shortcuts card. Custom shortcuts, the laptop keys and the connected apps' shortcuts are rows of the same list: the key on the left, what it does on the right. Connected-app rows sit in an accent frame, because the same shortcut also shows in that app and keeps working when PC Manager is closed; they appear only for installed apps and save as soon as a key is chosen, without a Save button. Choosing a key another row already uses swaps the two, which is how the Copilot key or Double Ctrl moves to any other action.

# Xiaomi Revamp 0.3.2

PC Manager 0.2.3, Screen Translator 0.2.3. AI Center stays at 0.3.1.

- **PC Manager: laptop keys in the shortcut list.** The Custom shortcuts list on the Keyboard page gains four fixed rows: XiaoAI key (F7), Project key (F8), Settings key (F9) and Xiaomi key. Their defaults are unchanged (XiaoAI, Windows projection, Windows Settings, quick panel) and each can be pointed at any listed action, an app or a command. The same four settings no longer appear a second time under Preferences. Custom shortcuts and the Copilot key are untouched.
- **PC Manager: Screen off and Stay awake no longer fight.** Screen off turns Stay awake off first, and turning Stay awake on cancels a Screen off that has not darkened the display yet. Stay awake starts off whenever PC Manager starts. Prevent sleep is not touched by either and stays saved.
- **PC Manager: test copies.** The `--disable` switch of the installed app no longer stops a `--test` copy from starting in the tray.
- **Screen Translator: settings save as you change them.** The Save and Discard buttons are replaced by undo and redo arrows. Numbers and colours save after a short pause, text paths when the field is left.
- **Screen Translator: version label.** The version is shown in the header tag; the 0.2.2 footer line was overwritten by status text.

# Xiaomi Revamp 0.3.1

PC Manager 0.2.2, AI Center 0.3.1, Screen Translator 0.2.2.

- **PC Manager: the performance OSD no longer appears by itself.** In Smart mode the firmware announces its internal steps with the same event as the mode key, about once a minute under load. Each one showed the OSD and rewrote the settings file. An event that does not change the mode is now ignored, and restoring the saved mode at startup or from the policy guard is silent.
- **All apps: no crash when a shared settings file is busy.** The shared writer retries when another program has the file open, and closing an app no longer fails on it.
- **AI Center: indexing hardware load.** A new setting rests the embedding device between passages: 100, 75, 50 or 25 percent. Measured on an Arc B390: 94, 79, 46 and 18 percent GPU use, at 54, 34, 24 and 9 passages per second. Searches are not slowed.
- **Screen Translator: faster, steadier model loading.** The tokenizer no longer imports a large framework (0.1 s instead of 2 s warm and up to 25 s cold; identical token ids and text on the test set). A warm load measured 2.8 s instead of 6.4 s. Models now stay ready for a chosen time after use (default 2 minutes, previously a fixed 10 seconds; loaded models hold about 1.9 GB). Pressing the shortcut again while models load no longer cancels the load.
- **Version in settings.** Each app shows its version: PC Manager at the bottom of Settings, AI Center on its overview page, Screen Translator in its footer.

Not changed: the first model load on a new Screen Translator profile still validates every device and is slow and memory-hungry once.

# Xiaomi Revamp 0.3.0

AI Center 0.3.0, PC Manager 0.2.1 and Screen Translator 0.2.1. The two 0.2.1 apps only receive the new launcher with the testing control switch; their own code is unchanged.

## AI Center 0.3.0

- **On-disk index.** The index is a SQLite file on disk, EFS-encrypted for the Windows account by default, instead of an encrypted snapshot held entirely in RAM and rewritten on every save. On the author's whole-drive index (143k files, 731k passages) the worker went from 8.1 GB to 1.4 GB of memory and from about 49 GB to about 3 GB written per hour while indexing. An existing 0.2 snapshot is converted on first start (77 s for 2.5 GB) and kept beside the new file as `index.sqlite3.dpapi.migrated`.
- **Two-pass meaning search.** A 256-dimension prefix of every passage vector is scanned in RAM, then the best 2,000 candidates are reranked with the exact vectors. Retrieval at 250,000 passages measured 38 ms against 1.4 s.
- **Windows channel.** Optional results from the existing Windows index, including its semantic matches (`CONTAINSSEMANTIC`), through one persistent local worker. Cloud providers are never queried, and every candidate passes the same folder, type and date rules as local results.
- **Programs.** A Programs category lists Start apps and executables (`.exe`, `.msi`, `.lnk`) inside included folders; `type:program` works in queries.
- **Indexer.** A new subfolder no longer triggers a pass over the whole index, vectors are written in batches, and PDF text with split Unicode surrogates no longer drops the file.
- **Settings.** Windows and Programs toggles; three index protection choices (encrypted on disk, plain on disk, 0.2 snapshot in RAM).
- **Check.** `"AI Center.exe" --check-live` drives the real search bar (program, typed extension, meaning), intercepts launches and saves previews to the data folder.

Windows Home and non-NTFS volumes have no EFS: the index is then stored unencrypted, and the log and status say so.

## Testing control switch (all three apps)

Each app EXE accepts:

~~~powershell
& "C:\Program Files\Xiaomi Revamp\AI Center\AI Center.exe" --disable   # stop it and block every start
& "C:\Program Files\Xiaomi Revamp\AI Center\AI Center.exe" --status | Out-Host
& "C:\Program Files\Xiaomi Revamp\AI Center\AI Center.exe" --enable    # allow starts again
~~~

While disabled, Windows startup, PC Manager's once-a-minute startup task, PC Manager's automatic `--tray` start of the other apps, shortcuts and AI Center's restart loop all end silently; opening the app by hand shows a message with the enable command. `--status` exits with 0 when enabled and 3 when disabled. The state is one per-user file, `%LOCALAPPDATA%\XiaomiRevamp\control\<component>.off`.

## Known limits

- Local and Windows results are merged with equal weights; a late Windows answer can reorder visible rows.
- A file whose exact name equals the query is listed above a program of the same name.
- Sentence-length lexical queries take 0.5 to 0.7 s on a 731k-passage index.
- Windows only returns what it has indexed, and its word breaking is fixed to English here.
- PC Manager's startup task keeps firing every minute while it is disabled; each start exits immediately. PC Manager runs elevated, so `--disable` asks it to quit but cannot force a hung instance to end.

# PC Manager 0.2.0


This cumulative update repairs connected shortcut launches and continuous AI Center indexing.
The complete offline EXE installer includes PC Manager, optional AI Center and Screen Translator,
their models and private runtimes, editable development sources and the offline development toolchain.
The installer ZIP contains the same complete setup.

- AI Center now opens the launcher, with a separate File Search shortcut for the bar.
  The misleading AI Center settings shortcut is removed during installation or upgrade.
- AI Center retains its tray icon while minimized/hidden, with a Quit AI Center menu action.
- Search settings displays the index location, saved size/time and separate model folder.
  Index backups preserve metadata and embeddings in new dated folders with SHA256 manifests
  and recovery instructions. Encrypted backups require the original Windows profile keys;
  copying to another laptop or a reinstalled Windows profile is not supported.

- Protected search checkpoints now stream authenticated compressed batches, removing the former
  single-buffer 2 GiB boundary. Existing encrypted indexes migrate atomically without discarding data.
- Search keeps its browser, backend and embedding model ready. Closing search does not interrupt
  indexing. Optional idle unloading also pins the model while indexing is active.
- Public app wrappers use the normal Windows desktop profile, preventing private AppData redirection
  from separating the PC Manager hub and its companion command queues.
- Occupied shortcuts no longer disable all other bindings. Individual conflicts remain visible and retry.
- PC Manager shortcut saves use the bottom confirmation after confirmed persistence and activation;
  buttons return to Save. Double Ctrl recovers from missed key releases and opens only the search popup.
  The translator toolbox no longer draws a rectangular white focus outline; keyboard focus uses a fill change.
  Buttons have distinct hover and pressed colors. Quick translation opens the toolbox directly; search does not open settings.
- Enter submits compact search without opening a document. The file-type selector and first/last
  extension tokens (`report pdf` / `pdf report`) use the existing exact extension filter before ranking.
  Quoted type words stay literal; remembered-file ranking keeps the original query.
- Chinese-to-English translation preserves English text. Translation inference still releases after
  ten seconds idle, independently of the search model.

Compact search passed eleven frontend checks and the native WebView fixture: Enter never opened a
document, exact extensions were filtered, explicit file actions and remembered-file ordering remained
functional. The shared gesture harness passed fifteen focused checks, including a missed key release.

Validation passed native builds, Windows shortcut registration/ownership/swaps/recovery, actual installed
shortcut-editor feedback, recursive exclusions and frontend interaction checks. A real 2.30 GB SQLite
fixture passed encrypted save, reopen and further growth. A copy of the retained 125,945-file index
preserved all 644,244 chunks, table counts, sampled data and integrity during migration. Installed indexing
continued in Normal mode and wrote the new encrypted checkpoint. Private user data and reports are excluded.

In isolated ready-state tests, search focused in 56 ms and 27 ms on repeat while indexing continued.
The translator toolbox appeared in 31 ms; cached model loading plus fixture translation took 6.6 seconds
in the successful repeated run. An earlier run took 27.7 seconds to load and translate, so a universal
ten-second cold-load guarantee is not made. Supported Windows input injection verified left/right Double Ctrl
on the installed desktop, with the search input focused and settings hidden. Hardware key delivery and
reboot/sign-in remain manual checks. The toolbar light/dark render check starts no model/backend.
Keeping search immediately ready consumes resident RAM. Index RAM scales with index size and checkpointing
temporarily holds a second database; individual serialized rows are limited to 64 MiB.

Installation supports chosen app/data locations, a separate search-data location, current-user/all-users
registration, optional components, per-app uninstallers and optional removal of settings/data/development files.
Each app retains its own Development folder, Launch.cmd and archived previous development files.
