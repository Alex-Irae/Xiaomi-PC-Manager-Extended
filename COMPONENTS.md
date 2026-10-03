# Component branches

The integration parent is [`manager/main`](https://github.com/Alex-Irae/Xiaomi-PC-Manager-Extended/tree/manager/main).
The child branches [`manager/screen-translator`](https://github.com/Alex-Irae/Xiaomi-PC-Manager-Extended/tree/manager/screen-translator)
and [`manager/ai-center`](https://github.com/Alex-Irae/Xiaomi-PC-Manager-Extended/tree/manager/ai-center)
start from that parent's v0.2.0 commit. They retain the complete source tree so shared integration stays buildable.
Each child identifies its component in `COMPONENT.md`. The existing default `main` also tracks the parent integration.

| App | Source | Role |
| --- | --- | --- |
| PC Manager | `source/pc-manager` | Mandatory combined-installer hub; shortcut owner while running |
| Screen Translator | `source/screen-translator` | Optional child; independent EXE, data and uninstaller |
| AI Center | `source/file-search` | Optional child; independent EXE, data and uninstaller |

Git branch refs are flat, so names, shared ancestry and these links express parent/child relationships.
Original PC Manager 0.1.6 source remains under `archive/pc-manager-0.1.6` and in Git history.
Personal data, local preservation archives, packaged binaries and models are excluded from repository commits.
