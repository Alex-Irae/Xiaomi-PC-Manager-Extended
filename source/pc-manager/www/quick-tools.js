/* Purpose: the editable lower tools row. Dependencies: quick.js and host capabilities.
   Output: buttons only for implemented tools. Launch: PCManager.exe --toggle.
   Add { id, label, icon, method, args } after adding the corresponding fixed host capability.
   No paths, arbitrary commands, downloads, or dynamic code are accepted here. */
"use strict";
const QuickTools = Object.freeze([
  // Translation and file search are added here when their native integrations are ready.
]);
const QuickSystemTools = Object.freeze([
  { id: "screenoff", label: "Screen off", icon: "screenoff", method: "window.screenOff", title: "Use the normal display timeout immediately, then restore your saved timeout on wake. Windows may still enter Modern Standby." },
  { id: "awake", label: "Stay awake", icon: "awake", toggle: "awake" },
  { id: "refresh", label: "Refresh rate", icon: "refresh", toggle: "refresh", title: "Cycle supported display rates" },
  { id: "autorefresh", label: "Auto rate", icon: "auto", toggle: "autorefresh", title: "Switch display rate with AC or battery power" },
  { id: "touchpad", label: "Touchpad", icon: "touchpad", toggle: "touchpad" },
  { id: "touchscreen", label: "Touchscreen", icon: "touch", toggle: "touchscreen" },
  { id: "monitor", label: "Monitor", icon: "cpu", method: "window.monitor" },
  { id: "sleepoff", label: "Prevent sleep", icon: "quiet", toggle: "sleepoff", title: "Request background execution without keeping the display on; Windows may limit this on battery" },
  { id: "calculator", label: "Calculator", icon: "calculator", shortcut: "Calculator" },
  { id: "notepad", label: "Notepad", icon: "notepad", shortcut: "Notepad" },
  { id: "screenshot", label: "Screenshot", icon: "screenshot", shortcut: "Screenshot" },
  { id: "clipboard", label: "Clipboard", icon: "clipboard", shortcut: "Clipboard" },
  { id: "projection", label: "Project", icon: "screen", shortcut: "Screen" }
]);
const DefaultQuickSystemActions = Object.freeze(QuickSystemTools.slice(0, 8).map(tool => tool.id));

