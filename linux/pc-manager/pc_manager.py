#!/usr/bin/env python3
"""PC Manager for Linux: the quick panel and the manager window, with the Windows app's web pages.

Purpose: one background app. A second start passes its arguments to the running one, so Hyprland bindings work:
    pc_manager.py --tray       start in the background (put this in Hyprland's exec-once)
    pc_manager.py --toggle     show or hide the quick panel
    pc_manager.py --manager    open the manager window
    pc_manager.py --quit
Dependencies (Arch): python-gobject, gtk4, webkitgtk-6.0, gtk4-layer-shell; optional brightnessctl,
    power-profiles-daemon, systemd. The pages come from ../../source/pc-manager/www, unchanged.
Outputs: preferences in ~/.config/xiaomi-revamp/pc-manager, web data in ~/.local/share/xiaomi-revamp/pc-manager.
Status: written on Windows; the hardware logic is checked by tests/check_backend.py, the windows are untested.
"""
import subprocess
import sys
import threading
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))

from backend import VERSION, Hardware, Preferences, Router, System, Unavailable  # noqa: E402
from revamp import desktop  # noqa: E402
from revamp.webhost import GLib, Pages, WebWindow, run  # noqa: E402

SOURCE = HERE.parents[1] / "source" / "pc-manager"


class Manager:
    def __init__(self, application):
        self.application = application
        data = desktop.data_dir("pc-manager")
        for name in ("icons", "custom-assets"):
            (data / name).mkdir(parents=True, exist_ok=True)
        self.hardware = Hardware(System(), Preferences(desktop.config_dir("pc-manager") / "settings.json"))
        self.router = Router(self.hardware)
        self.lock = threading.Lock()  # one hardware request at a time, as on Windows
        self.inhibitors = {}
        self.pages = Pages({"xiaomi-ai.local": SOURCE / "www", "xiaomi-assets.local": SOURCE / "assets",
                            "xiaomi-icons.local": data / "icons", "xiaomi-custom-assets.local": data / "custom-assets"}, data)
        self.quick = WebWindow(application, self.pages, self.pages.url("xiaomi-ai.local", "quick.html"), lambda message: self.request(self.quick, message),
                               title="PC Manager quick controls", size=(420, 640),
                               layer={"anchor": ("bottom", "right"), "margin": {"bottom": 12, "right": 12}, "keyboard": "on_demand", "namespace": "revamp-quick"})
        self.manager = WebWindow(application, self.pages, self.pages.url("xiaomi-ai.local", "index.html"), lambda message: self.request(self.manager, message),
                                 title="PC Manager", size=(1180, 800))
        GLib.timeout_add_seconds(30, self.policies)
        self.policies()

    def policies(self):
        def work():
            with self.lock:
                try:
                    for note in self.hardware.apply_policies():
                        desktop.notify("PC Manager", note)
                except Exception as error:  # the next check tries again
                    print(f"policy check: {error}", file=sys.stderr)
        threading.Thread(target=work, daemon=True).start()
        return True

    def inhibit(self, name, what, on):
        """Hold or release a systemd inhibitor lock ("idle" keeps the screen on, "sleep" keeps the laptop awake)."""
        running = self.inhibitors.pop(name, None)
        if running is not None:
            running.terminate()
        if on:
            self.inhibitors[name] = subprocess.Popen(["systemd-inhibit", "--what=" + what, "--who=PC Manager", "--why=Requested in PC Manager", "sleep", "infinity"])
        return {"message": ("On" if on else "Off") + "."}

    def window_request(self, window, method, args):
        if method == "window.state":
            return {"version": VERSION, "awake": "awake" in self.inhibitors, "preventSleep": "sleep" in self.inhibitors, "isolationStatus": "Not applicable on Linux"}
        if method == "window.hide":
            window.hide()
        elif method == "window.minimize":
            window.window.minimize()
        elif method == "window.maximize":
            window.window.unmaximize() if window.window.is_maximized() else window.window.maximize()
        elif method == "window.manager":
            self.quick.hide()
            self.manager.show()
            if args.get("page"):
                self.manager.send({"managerPage": args["page"]})
        elif method == "window.quick":
            self.quick.toggle()
        elif method == "window.awake":
            return self.inhibit("awake", "idle:sleep", bool(args["on"]))
        elif method == "window.preventSleep":
            return self.inhibit("sleep", "sleep", bool(args["on"]))
        elif method == "window.screenOff":
            window.hide()
            GLib.timeout_add(400, lambda: desktop.hyprctl("dispatch", "dpms", "off") and False)  # after the key or click is released
        elif method in ("window.shown", "window.drag", "window.reconnect"):
            pass  # Hyprland moves windows itself (SUPER and drag); nothing to reconnect to
        else:
            raise Unavailable(f"Not available on Linux yet ({method}).")
        return {"message": "Done."}

    def request(self, window, message):
        identifier, method, args = message.get("id"), message.get("method", ""), message.get("args") or {}
        if method.startswith("window."):
            try:
                window.send({"id": identifier, "ok": True, "data": self.window_request(window, method, args)})
            except (Unavailable, RuntimeError, KeyError, ValueError) as error:
                window.send({"id": identifier, "ok": False, "error": str(error)})
            return

        def work():
            try:
                with self.lock:
                    data = self.router.handle(method, args)
                window.send({"id": identifier, "ok": True, "data": data})
            except (Unavailable, ValueError, KeyError) as error:
                window.send({"id": identifier, "ok": False, "error": str(error)})
            except Exception as error:
                print(f"{method}: {type(error).__name__}: {error}", file=sys.stderr)
                window.send({"id": identifier, "ok": False, "error": "The operation could not be completed. See the terminal output for details."})
        threading.Thread(target=work, daemon=True).start()

    def command(self, arguments):
        if "--quit" in arguments:
            for running in self.inhibitors.values():
                running.terminate()
            self.application.quit()
        elif "--toggle" in arguments:
            self.quick.toggle()
            if self.quick.visible:
                self.quick.send({"activated": True})
        elif "--manager" in arguments or not arguments:
            self.manager.show()
            self.manager.send({"activated": True})
        # --tray: stay in the background


def main():
    state = {}
    return run("io.github.alexirae.revamp.PCManager", lambda application: state.update(manager=Manager(application)), lambda _application, arguments: state["manager"].command(arguments))


if __name__ == "__main__":
    sys.exit(main())
