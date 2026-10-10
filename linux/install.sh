#!/usr/bin/env bash
# Purpose: set up what the Linux apps need on Arch Linux: system packages, a Python environment for the search and
#          translation engines, the access rule for hardware controls, launchers in ~/.local/bin, and optionally
#          the model folders copied from a Windows installation.
# Dependencies: Arch Linux with pacman and sudo. Run it yourself; it asks for your password where root is needed.
# Outputs: ~/.local/share/xiaomi-revamp/venv, /etc/udev/rules.d/99-xiaomi-revamp.rules, ~/.local/bin/revamp-*.
# Command: linux/install.sh [--models-from "/mnt/windows/Program Files/Xiaomi Revamp"] [--no-packages]
# Status: written on Windows and never run. Read it before running it.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}/xiaomi-revamp"
models_from=""
packages=1
while [ $# -gt 0 ]; do
  case "$1" in
    --models-from) models_from="$2"; shift 2 ;;
    --no-packages) packages=0; shift ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

if [ "$packages" = 1 ]; then
  # Windows and overlays: gtk4, webkitgtk-6.0, gtk4-layer-shell. Capture: grim, slurp. Desktop: wl-clipboard,
  # libnotify, xdg-utils. Hardware: brightnessctl. power-profiles-daemon is left to you: it conflicts with tlp.
  sudo pacman -S --needed python python-gobject gtk4 webkitgtk-6.0 gtk4-layer-shell grim slurp wl-clipboard libnotify xdg-utils brightnessctl
fi

# The engines' Python packages live in their own environment; the windows use the system Python (python-gobject).
if [ ! -x "$data/venv/bin/python" ]; then
  python -m venv "$data/venv"
fi
"$data/venv/bin/python" -m pip install --upgrade pip
"$data/venv/bin/python" -m pip install -r "$here/requirements.txt"

# Hardware controls the kernel keeps for root: the charge limit and the performance profile. This rule hands them
# to the "video" group at boot, the group that already owns the backlight on Arch.
rule='/etc/udev/rules.d/99-xiaomi-revamp.rules'
sudo install -m 0644 "$here/udev/99-xiaomi-revamp.rules" "$rule"
sudo udevadm control --reload
sudo udevadm trigger --subsystem-match=power_supply --subsystem-match=platform-profile || true
if ! id -nG "$USER" | tr ' ' '\n' | grep -qx video; then
  sudo usermod -aG video "$USER"
  echo "Added $USER to the video group. Log out and in again for the hardware controls."
fi

mkdir -p "$HOME/.local/bin"
for pair in "pc-manager:pc-manager/pc_manager.py" "ai-center:file-search/ai_center.py" "translator:screen-translator/translator.py"; do
  name="${pair%%:*}"; script="${pair#*:}"
  printf '#!/usr/bin/env bash\nexec python -B "%s" "$@"\n' "$here/$script" > "$HOME/.local/bin/revamp-$name"
  chmod +x "$HOME/.local/bin/revamp-$name"
done

if [ -n "$models_from" ]; then
  # The models are the same files on both systems (about 700 MB): copy them from the mounted Windows partition.
  mkdir -p "$data/file-search/models" "$data/screen-translator/models"
  cp -rn "$models_from/AI Center/models/qwen3-embedding" "$data/file-search/models/"
  cp -rn "$models_from/Screen Translator/models/zh-en" "$data/screen-translator/models/"
  echo "Models copied to $data."
fi

echo "Done. Next: add linux/hypr/revamp.conf to your Hyprland configuration, then run linux/probe/probe.sh."
