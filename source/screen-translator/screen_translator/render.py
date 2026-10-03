"""Shared physical-pixel replacement layout for desktop overlays and benchmark PNGs."""
from dataclasses import dataclass
import math
from statistics import median
from time import perf_counter

from PyQt5 import QtCore, QtGui, QtWidgets
from .windows_surface import configure_surface, exclude_from_capture, set_surface_region


def pil_to_qimage(image):
    rgb = image.convert("RGB")
    return QtGui.QImage(rgb.tobytes(), rgb.width, rgb.height,
                        rgb.width*3, QtGui.QImage.Format_RGB888).copy()


def qimage_to_pil(image):
    from PIL import Image
    rgb = image.convertToFormat(QtGui.QImage.Format_RGB888)
    data = rgb.bits()
    data.setsize(rgb.byteCount())
    return Image.frombytes("RGB", (rgb.width(), rgb.height()), bytes(data), "raw", "RGB", rgb.bytesPerLine())


@dataclass
class Block:
    rect: object
    font: object
    text: str
    full_text: str
    background: object
    foreground: object
    flags: int
    clipped: bool


def palette(image, rect):
    """Perimeter median estimates flat UI backgrounds without sampling glyph interiors."""
    samples = []
    for step in range(13):
        fraction = step/12
        x = rect.left() + fraction*rect.width()
        y = rect.top() + fraction*rect.height()
        for px, py in ((x, rect.top()), (x, rect.bottom()), (rect.left(), y), (rect.right(), y)):
            color = image.pixelColor(max(0, min(image.width()-1, round(px))),
                                     max(0, min(image.height()-1, round(py))))
            samples.append((color.red(), color.green(), color.blue()))
    channels = [round(median(sample[i] for sample in samples)) for i in range(3)]
    background = QtGui.QColor(*channels)
    # Approximate UI contrast, preserving location; textured backgrounds need later inpainting.
    luminance = .2126*channels[0] + .7152*channels[1] + .0722*channels[2]
    foreground = QtGui.QColor("#202124" if luminance >= 145 else "#ffffff")
    return background, foreground


def layout(image, regions, font_scale=1.0):
    """Fit inside source bounds only; an ellipsis explicitly marks an incomplete fit.

    Shrink to 65% of estimated source size, then wrap. Never expand across controls.
    Full translation remains in region JSON; clipped count appears in diagnostics.
    """
    t = perf_counter()
    blocks = []
    for region in regions:
        if not region.translated or region.translated == region.source:
            continue
        x1, y1, x2, y2 = region.bounds
        rect = QtCore.QRectF(x1-1, y1-1, x2-x1+2, y2-y1+2).intersected(QtCore.QRectF(image.rect()))
        if rect.width() < 3 or rect.height() < 3:
            continue
        # Estimate font in physical pixels, so mixed-DPI screens use the captured image scale.
        maximum = max(7, round(rect.height()*.78*font_scale))
        minimum = max(7, round(maximum*.65))
        flags = int(QtCore.Qt.AlignLeft | QtCore.Qt.AlignVCenter)
        chosen, fits = None, False
        available = rect.adjusted(1, 0, -1, 0)
        for size in range(maximum, minimum-1, -1):
            font = QtGui.QFont("Segoe UI")
            font.setPixelSize(size)
            metrics = QtGui.QFontMetricsF(font)
            chosen = font
            if metrics.horizontalAdvance(region.translated) <= available.width() and metrics.height() <= available.height():
                fits = True
                break
        if not fits:
            wrapped = flags | int(QtCore.Qt.TextWordWrap)
            measure = QtGui.QFontMetricsF(chosen).boundingRect(available, wrapped, region.translated)
            if measure.height() <= available.height() and measure.width() <= available.width():
                flags, fits = wrapped, True
        text = region.translated if fits else QtGui.QFontMetrics(chosen).elidedText(
            region.translated, QtCore.Qt.ElideRight, max(1, math.floor(available.width())))
        background, foreground = palette(image, rect.adjusted(-1, -1, 1, 1))
        region.style = {"background_rgb": [background.red(), background.green(), background.blue()],
                        "foreground_rgb": [foreground.red(), foreground.green(), foreground.blue()],
                        "font_pixels": chosen.pixelSize(), "clipped": not fits}
        blocks.append(Block(rect, chosen, text, region.translated, background, foreground, flags, not fits))
    return blocks, {"layout_ms": (perf_counter()-t)*1000, "clipped_regions": sum(b.clipped for b in blocks)}


def paint_blocks(painter, blocks, debug=False):
    for block in blocks:
        painter.save()
        painter.setClipRect(block.rect)
        painter.fillRect(block.rect, block.background)
        painter.setFont(block.font)
        painter.setPen(block.foreground)
        painter.drawText(block.rect.adjusted(1, 0, -1, 0), block.flags, block.text)
        if debug:
            painter.setPen(QtGui.QColor("#ff6900"))
            painter.drawRect(block.rect.adjusted(.5, .5, -.5, -.5))
        painter.restore()


def render_image(image, regions, font_scale=1.0):
    """Return translated QImage and rendering timings; saving is caller controlled."""
    original = pil_to_qimage(image)
    blocks, timing = layout(original, regions, font_scale)
    output = original.copy()
    t = perf_counter()
    painter = QtGui.QPainter(output)
    paint_blocks(painter, blocks)
    painter.end()
    timing["render_ms"] = (perf_counter()-t)*1000
    return output, timing


class ReplacementOverlay(QtWidgets.QWidget):
    """Normal masked Windows surface, excluded from captures, permanently click-through."""
    painted = QtCore.pyqtSignal(float)

    def __init__(self):
        super().__init__(None, QtCore.Qt.Tool | QtCore.Qt.FramelessWindowHint |
                         QtCore.Qt.WindowStaysOnTopHint | QtCore.Qt.WindowTransparentForInput |
                         QtCore.Qt.WindowDoesNotAcceptFocus)
        configure_surface(self, windows=True)
        self.setAttribute(QtCore.Qt.WA_ShowWithoutActivating)
        self.setAttribute(QtCore.Qt.WA_TransparentForMouseEvents)
        self.blocks, self.image_size, self.debug = [], (1, 1), False
        self.capture_excluded = False
        self.report_next_paint = False

    def replace(self, image, regions, screen_rect, font_scale=1.0, debug=False):
        source = pil_to_qimage(image)
        self.blocks, timing = layout(source, regions, font_scale)
        self.image_size, self.debug = image.size, debug
        self.setGeometry(screen_rect)
        sx, sy = self.width()/image.width, self.height()/image.height
        shape = QtGui.QRegion()
        for block in self.blocks:
            rect = block.rect
            # Map physical screenshot pixels to the selected monitor's logical Qt coordinates.
            logical = QtCore.QRect(math.floor(rect.left()*sx), math.floor(rect.top()*sy),
                                   math.ceil(rect.width()*sx)+1, math.ceil(rect.height()*sy)+1)
            shape |= QtGui.QRegion(logical)
        set_surface_region(self, shape, windows=True)
        self.report_next_paint = True
        self.show()
        self.capture_excluded = exclude_from_capture(self)
        self.update()
        return timing

    def paintEvent(self, event):
        t = perf_counter()
        painter = QtGui.QPainter(self)
        painter.scale(self.width()/self.image_size[0], self.height()/self.image_size[1])
        paint_blocks(painter, self.blocks, self.debug)
        painter.end()
        if self.report_next_paint:
            self.report_next_paint = False
            self.painted.emit((perf_counter()-t)*1000)
