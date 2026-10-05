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
