"""Shared window host for the Linux apps: shows an app's existing web pages in a native Wayland window.

Purpose: the Windows apps draw their interface with WebView2 and talk to it through `window.chrome.webview`
    (`postMessage` to the host, `message` events back). This module gives the same pages the same bridge on
    WebKitGTK, so the pages run unchanged.
Dependencies (Arch): python-gobject, gtk4, webkitgtk-6.0; gtk4-layer-shell for panels and overlays.
Outputs: none. Web data (localStorage) is kept under the data directory given to `Pages`.
Usage: imported by the app hosts in linux/<app>/. Not run directly.
Status: written on Windows, where it cannot be run. Treat it as untested until it has run on Arch.
"""
import json
import mimetypes
import sys
from ctypes import CDLL
from pathlib import Path

try:  # gtk4-layer-shell must be loaded before libwayland-client, which Gtk pulls in
    CDLL("libgtk4-layer-shell.so")
    LAYER_SHELL = True
except OSError:
    LAYER_SHELL = False

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Gdk", "4.0")
gi.require_version("WebKit", "6.0")
from gi.repository import Gdk, Gio, GLib, Gtk, WebKit  # noqa: E402

if LAYER_SHELL:
    try:
        gi.require_version("Gtk4LayerShell", "1.0")
        from gi.repository import Gtk4LayerShell as LayerShell  # noqa: E402
    except (ValueError, ImportError):
        LAYER_SHELL = False

SCHEME = "revamp"
TEXT = (".html", ".js", ".css", ".json", ".svg")
# The same bridge the pages use on Windows. Messages travel as JSON text in both directions.
SHIM = """
(() => {
  const listeners = new Set();
  const webview = {
    postMessage(message) { window.webkit.messageHandlers.host.postMessage(JSON.stringify(message)); },
    addEventListener(type, listener) { if (type === "message") listeners.add(listener); },
    removeEventListener(type, listener) { listeners.delete(listener); }
  };
  window.chrome = Object.assign(window.chrome || {}, { webview });
  window.__revampDeliver = data => { for (const listener of [...listeners]) { try { listener({ data }); } catch (error) { console.error(error); } } };
})();
"""


class Pages:
    """Serves folders under made-up host names, as WebView2's virtual host mapping does on Windows.

    `hosts` maps a host name (for example "xiaomi-ai.local") to a folder. Pages address each other as
    https://<host>/...; text files are rewritten to the custom scheme on the way out so those links resolve here.
    """

    def __init__(self, hosts, data_dir):
        self.hosts = {name: Path(folder) for name, folder in hosts.items()}
        data = Path(data_dir)
        data.mkdir(parents=True, exist_ok=True)
        self.session = WebKit.NetworkSession.new(str(data / "web-data"), str(data / "web-cache"))
        self.context = WebKit.WebContext()
        self.context.register_uri_scheme(SCHEME, self._serve)
        security = self.context.get_security_manager()
        security.register_uri_scheme_as_secure(SCHEME)
        security.register_uri_scheme_as_cors_enabled(SCHEME)

    def url(self, host, page):
        return f"{SCHEME}://{host}/{page.lstrip('/')}"

    def _serve(self, request):
        try:
            uri = GLib.Uri.parse(request.get_uri(), GLib.UriFlags.NONE)
            root = self.hosts[uri.get_host()].resolve()
            target = (root / uri.get_path().lstrip("/")).resolve()
            if root not in target.parents and target != root:
                raise FileNotFoundError(target)  # no path outside the mapped folder
            data = target.read_bytes()
            if target.suffix.lower() in TEXT:
                text = data.decode("utf-8")
                for host in self.hosts:
                    text = text.replace("https://" + host, f"{SCHEME}://{host}")
                data = text.encode("utf-8")
            kind = mimetypes.guess_type(target.name)[0] or "application/octet-stream"
            request.finish(Gio.MemoryInputStream.new_from_bytes(GLib.Bytes.new(data)), len(data), kind)
        except (KeyError, OSError, UnicodeDecodeError, GLib.Error) as error:
            request.finish_error(GLib.Error.new_literal(Gio.io_error_quark(), str(error), Gio.IOErrorEnum.NOT_FOUND))


class WebWindow:
    """One window showing one page. `on_message(dict)` receives what the page posts; `send(dict)` answers.

    `layer` is None for a normal window, or a dict for a layer surface (panel, search bar, overlay):
    {"anchor": ("top", "right"), "margin": {"top": 12, "right": 12}, "keyboard": "on_demand" | "exclusive" | "none",
     "layer": "overlay" | "top", "namespace": "revamp-quick"}.
    """

    def __init__(self, application, pages, url, on_message, *, title, size=(1100, 760), layer=None, transparent=False):
        self.on_message = on_message
        self.window = Gtk.ApplicationWindow(application=application, title=title)
        self.window.set_default_size(*size)
        self.window.set_hide_on_close(True)
        if layer is not None:
            if not LAYER_SHELL:
                print("gtk4-layer-shell is missing: " + title + " opens as a normal window.", file=sys.stderr)
            else:
                LayerShell.init_for_window(self.window)
                LayerShell.set_namespace(self.window, layer.get("namespace", "revamp"))
                LayerShell.set_layer(self.window, LayerShell.Layer.OVERLAY if layer.get("layer", "overlay") == "overlay" else LayerShell.Layer.TOP)
                edges = {"top": LayerShell.Edge.TOP, "bottom": LayerShell.Edge.BOTTOM, "left": LayerShell.Edge.LEFT, "right": LayerShell.Edge.RIGHT}
                for name in layer.get("anchor", ()):
                    LayerShell.set_anchor(self.window, edges[name], True)
                for name, pixels in layer.get("margin", {}).items():
                    LayerShell.set_margin(self.window, edges[name], pixels)
                modes = {"none": LayerShell.KeyboardMode.NONE, "exclusive": LayerShell.KeyboardMode.EXCLUSIVE, "on_demand": LayerShell.KeyboardMode.ON_DEMAND}
                LayerShell.set_keyboard_mode(self.window, modes[layer.get("keyboard", "on_demand")])
        manager = WebKit.UserContentManager()
        manager.add_script(WebKit.UserScript.new(SHIM, WebKit.UserContentInjectedFrames.TOP_FRAME, WebKit.UserScriptInjectionTime.START, None, None))
        manager.register_script_message_handler("host", None)
        manager.connect("script-message-received::host", self._received)
        self.web = WebKit.WebView(web_context=pages.context, network_session=pages.session, user_content_manager=manager)
        settings = self.web.get_settings()
        settings.set_enable_developer_extras(False)
        self.web.connect("context-menu", lambda *_: True)  # no browser menu, as on Windows
        self.web.connect("decide-policy", self._policy)
        if transparent:
            self.web.set_background_color(Gdk.RGBA(0, 0, 0, 0))
            self.window.add_css_class("revamp-transparent")
        self.window.set_child(self.web)
        self.web.load_uri(url)

    def _policy(self, _web, decision, kind):
        # The pages never leave their own scheme; anything else is refused.
        if kind == WebKit.PolicyDecisionType.NAVIGATION_ACTION:
            uri = decision.get_navigation_action().get_request().get_uri()
            if not uri.startswith(SCHEME + "://"):
                decision.ignore()
                return True
        return False

    def _received(self, _manager, value):
        try:
            message = json.loads(value.to_string())
        except (ValueError, AttributeError):
            return
        try:
            self.on_message(message)
        except Exception as error:  # a failing handler must not take the window down
            print(f"{type(error).__name__}: {error}", file=sys.stderr)

    def send(self, message):
        """Deliver a message to the page. Safe to call from any thread."""
        script = "window.__revampDeliver && window.__revampDeliver(" + json.dumps(message) + ")"
        GLib.idle_add(lambda: self.web.evaluate_javascript(script, -1, None, None, None, None, None) and False)

    def script(self, code):
        """Run a line of JavaScript in the page (the Windows hosts raise page events this way). Any thread."""
        GLib.idle_add(lambda: self.web.evaluate_javascript(code, -1, None, None, None, None, None) and False)

    def resize(self, width, height):
        self.window.set_default_size(width, height)
        self.web.set_size_request(width, height)  # a layer surface takes its size from its content

    def focus(self):
        self.web.grab_focus()

    @property
    def visible(self):
        return self.window.get_visible()

    def show(self):
        self.window.present()

    def hide(self):
        self.window.set_visible(False)

    def toggle(self):
        self.hide() if self.visible else self.show()


def run(application_id, activate, command=None):
    """Run a single-instance app. A second start hands its arguments to the first one, which is how Hyprland
    key bindings reach a running app (`bind = SUPER, M, exec, pc-manager --toggle`).

    `activate(application)` builds the windows once; `command(application, arguments)` handles every start.
    """
    application = Gtk.Application(application_id=application_id, flags=Gio.ApplicationFlags.HANDLES_COMMAND_LINE)
    built = []

    def on_command(app, line):
        if not built:
            built.append(True)
            app.hold()  # stays in the background when every window is hidden
            activate(app)
        if command is not None:
            command(app, [str(value) for value in line.get_arguments()[1:]])
        return 0

    application.connect("command-line", on_command)
    return application.run(sys.argv)
