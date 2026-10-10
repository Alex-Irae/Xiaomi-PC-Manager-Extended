/* Purpose: draw translated regions over the screen. Dependencies: overlay.html and the host bridge.
   Messages from translator.py:
     {status: "text"}                      a note at the top (loading, nothing found, an error)
     {regions: [...], scale, png}          regions in capture pixels, the monitor scale, the capture as base64 PNG
   Escape or a click closes the overlay ({action: "close"} to the host). */
"use strict";
const host = window.chrome?.webview;
const layer = document.getElementById("regions"), note = document.getElementById("status");

function close() { layer.replaceChildren(); note.hidden = true; host?.postMessage({ action: "close" }); }
document.addEventListener("keydown", event => { if (event.key === "Escape") close(); });
document.addEventListener("click", close);

// The colour around a region's edge is its background; the text takes black or white, whichever reads better.
function background(context, x1, y1, x2, y2) {
  const points = [[x1, y1], [x2 - 1, y1], [x1, y2 - 1], [x2 - 1, y2 - 1], [(x1 + x2) >> 1, y1], [(x1 + x2) >> 1, y2 - 1]];
  const samples = points.map(([x, y]) => context.getImageData(Math.max(0, x), Math.max(0, y), 1, 1).data);
  const middle = channel => samples.map(sample => sample[channel]).sort((a, b) => a - b)[samples.length >> 1];
  return [middle(0), middle(1), middle(2)];
}

function fit(box, text) {
  // Shrink the text until it fits its region, starting from the height of the original line.
  let size = Math.max(9, Math.min(box.clientHeight * 0.78, 40));
  text.style.fontSize = size + "px";
  while (size > 9 && (text.scrollWidth > box.clientWidth || text.offsetHeight > box.clientHeight)) { size -= 1; text.style.fontSize = size + "px"; }
}

function draw({ regions, scale, png }) {
  note.hidden = true;
  const image = new Image();
  image.onload = () => {
    const canvas = document.createElement("canvas");
    canvas.width = image.width; canvas.height = image.height;
    const context = canvas.getContext("2d", { willReadFrequently: true });
    context.drawImage(image, 0, 0);
    const nodes = [];
    for (const region of regions) {
      if (!region.translated || region.translated === region.source) continue;
      const xs = region.polygon.map(point => point[0]), ys = region.polygon.map(point => point[1]);
      const x1 = Math.floor(Math.min(...xs)), y1 = Math.floor(Math.min(...ys)), x2 = Math.ceil(Math.max(...xs)), y2 = Math.ceil(Math.max(...ys));
      const [r, g, b] = background(context, x1, y1, x2, y2);
      const box = document.createElement("div"), text = document.createElement("span");
      box.className = "region";
      Object.assign(box.style, { left: x1 / scale + "px", top: y1 / scale + "px", width: (x2 - x1) / scale + "px", height: (y2 - y1) / scale + "px",
        background: `rgb(${r},${g},${b})`, color: 0.299 * r + 0.587 * g + 0.114 * b > 140 ? "#111" : "#f4f4f4" });
      box.title = region.source;
      text.textContent = region.translated;
      box.append(text);
      nodes.push([box, text]);
    }
    layer.replaceChildren(...nodes.map(([box]) => box));
    for (const [box, text] of nodes) fit(box, text);
    if (!nodes.length) { note.textContent = "No text to translate was found."; note.hidden = false; }
  };
  image.src = "data:image/png;base64," + png;
}

host?.addEventListener("message", ({ data }) => {
  if (data.status !== undefined) { layer.replaceChildren(); note.textContent = data.status; note.hidden = !data.status; }
  if (data.regions) draw(data);
});
