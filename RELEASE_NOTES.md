# PC Manager 0.2.0

This cumulative update repairs connected shortcut launches and continuous AI Center indexing.
The complete offline EXE installer includes PC Manager, optional AI Center and Screen Translator,
their models and private runtimes, editable development sources and the offline development toolchain.
The installer ZIP contains the same complete setup.

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
