"""Resident PP-OCRv4 candidate: OpenVINO inference, RapidOCR decoding only."""
from pathlib import Path
from time import perf_counter
import math

import cv2
import numpy as np

from .core import TextRegion
from .runtime import compile_model


def detection_windows(size,side,overlap=192):
    """Cover physical image pixels with overlapping fixed-size detector windows.

    Args: size=(width,height), side>overlap>=0. Returns clipped XYXY rectangles.
    The final window ends at the image edge, so no narrow remainder is upscaled.
    """
    if min(size)<1 or not 0<=overlap<side:
        raise ValueError("Invalid detection window geometry")
    def starts(length):
        end=max(0,length-side)
        values=list(range(0,end+1,side-overlap))
        if values[-1]!=end:values.append(end)
        return values
    w,h=size
    return [(x,y,min(x+side,w),min(y+side,h)) for y in starts(h) for x in starts(w)]


def merge_tile_boxes(items):
    """Deduplicate overlapping detector quads; stitch horizontal seam fragments before OCR.

    items contains (quad [4,2], tile ID, touches internal tile edge).
    Separate detections from the same tile are never joined. Non-horizontal seam
    fragments remain separate. Returns physical-coordinate quads [N,4,2].
    """
    def bounds(points):
        xs,ys=zip(*points)
        return min(xs),min(ys),max(xs),max(ys)
    def area(points):
        a=bounds(points)
        return (a[2]-a[0])*(a[3]-a[1])
    kept=[]
    # Prefer complete boxes, then larger spans, over clipped copies in adjacent tiles.
    for points,tile,edge in sorted(items,key=lambda item:(item[2],-area(item[0]))):
        candidate={"points":points,"tiles":{tile},"edge":edge}
        index=0
        while index<len(kept):
            previous=kept[index]
            if candidate["tiles"] & previous["tiles"]:
                index+=1;continue
            a,b=bounds(candidate["points"]),bounds(previous["points"])
            width=min(a[2]-a[0],b[2]-b[0]);height=min(a[3]-a[1],b[3]-b[1])
            shared_x=max(0,min(a[2],b[2])-max(a[0],b[0]))
            shared_y=max(0,min(a[3],b[3])-max(a[1],b[1]))
            aligned=height>0 and abs((a[1]+a[3]-b[1]-b[3])/2)<height*.5
            duplicate=aligned and shared_x*shared_y>=min(area(candidate["points"]),area(previous["points"]))* .65
            horizontal=all(abs(p[0][1]-p[1][1])<height*.25 for p in [candidate["points"],previous["points"]])
            stitch=aligned and horizontal and candidate["edge"] and previous["edge"] and shared_x>width*.15 and shared_y>height*.75
            if not duplicate and not stitch:
                index+=1;continue
            if duplicate:
                best=min([candidate,previous],key=lambda record:(record["edge"],-area(record["points"])))
                candidate["points"],candidate["edge"]=best["points"],best["edge"]
            else:
                # A seam-crossing horizontal line is rectified once from the full original image.
                left,top,right,bottom=min(a[0],b[0]),min(a[1],b[1]),max(a[2],b[2]),max(a[3],b[3])
                candidate["points"]=[[left,top],[right,top],[right,bottom],[left,bottom]]
            candidate["tiles"] |= previous["tiles"]
            kept.pop(index);index=0
        kept.append(candidate)
    return [record["points"] for record in kept]


class OCR:
    """Input: RGB PIL image. Output: regions in original physical pixel coordinates.

    Fixed shapes avoid repeated kernel specialization; long lines use bounded strips. No angle classifier or engine
    fallback. The character dictionary MUST belong to the recognizer export.
    """
    def __init__(self, root, detector_device, recognizer_device, config, progress=lambda message: None, compiled=None):
        import openvino as ov
        from rapidocr_onnxruntime.ch_ppocr_det.utils import DBPostProcess
        from rapidocr_onnxruntime.ch_ppocr_rec.utils import CTCLabelDecode

        self.config = config
        self.side = int(config["detector_side"])
        self.overlap=int(config.get("tile_overlap",192))
        self.batch_size = int(config["recognition_batch"])
        self.height, self.width = map(int, config["recognition_hw"])
        if self.side < 32 or self.side % 32 or min(self.batch_size, self.height, self.width) < 1:
            raise ValueError("Invalid OCR tensor dimensions")
        core = ov.Core()
        self.dynamic_width = False
        progress(f"Compiling OCR detector on {detector_device}")
        self.detector, det_info = compiled[:2] if compiled else compile_model(
            core, Path(root)/"ocr/det.onnx", detector_device, [1, 3, self.side, self.side], precision="f32")
        progress(f"Compiling OCR recognizer on {recognizer_device}")
        self.recognizer, rec_info = compiled[2:] if compiled else compile_model(
            core, Path(root)/"ocr/rec.onnx", recognizer_device,
            [self.batch_size, 3, self.height, self.width], ctc_top1=True)
        self.narrow_width=min(320,self.width)
        progress(f"Compiling short-label recognizer on {recognizer_device}")
        self.narrow_recognizer,narrow_info=compile_model(core,Path(root)/"ocr/rec.onnx",recognizer_device,
            [self.batch_size,3,self.height,self.narrow_width],ctc_top1=True)
        self.postprocess = DBPostProcess(
            thresh=config["detection_threshold"], box_thresh=config["box_threshold"],
            unclip_ratio=config["unclip_ratio"], use_dilation=False)
        keys = Path(root)/"ocr/keys.txt"
        if keys.is_file():
            self.decode = CTCLabelDecode(character_path=str(keys))
        else:
            # Read the dictionary embedded in this exact recognizer, without executing its graph.
            import onnx
            source = onnx.load_model(str(Path(root)/"ocr/rec.onnx"), load_external_data=False)
            metadata = {entry.key: entry.value for entry in source.metadata_props}
            characters = metadata.get("character", "").splitlines()
            if not characters:
                raise ValueError("Recognizer has no embedded character dictionary")
            self.decode = CTCLabelDecode(character=characters)
        self.info = {"detector":det_info,"recognizer":rec_info,"short_label_recognizer":narrow_info,
                     "tile_overlap":self.overlap,"short_label_max_content_width":160}
        self.timings = {}

    def warmup(self):
        # Exercise both fixed tensor shapes before accepting captures.
        det = np.zeros((1, 3, self.side, self.side), np.float32)  # [1,3,S,S]
        rec = np.zeros((self.batch_size, 3, self.height, self.width), np.float32)  # [B,3,H,W]
        self.detector([det])
        self.recognizer([rec])  # two [B,T] outputs, class IDs and confidence
        narrow=np.zeros((self.batch_size,3,self.height,self.narrow_width),np.float32)  # [B,3,H,320]
        self.narrow_recognizer([narrow])
        if self.info["recognizer"]["ctc_classes"] != len(self.decode.character):
            raise ValueError("Recognizer dictionary does not match model output classes")

    def __call__(self, image):
        t = perf_counter()
        # PP-OCRv4's RapidOCR export consumes BGR, normalized to [-1,1].
        rgb = np.asarray(image.convert("RGB"))  # [H,W,3]
        bgr = cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)  # [H,W,3]
        h, w = bgr.shape[:2]
        items=[]
        windows=detection_windows((w,h),self.side,self.overlap)
        for tile,(left,top,right,bottom) in enumerate(windows):
            for points in self._detect(bgr[top:bottom,left:right]):
                # Flag crop-edge truncation before converting local points to global coordinates.
                edge=(left>0 and points[:,0].min()<12 or right<w and points[:,0].max()>right-left-13 or
                      top>0 and points[:,1].min()<12 or bottom<h and points[:,1].max()>bottom-top-13)
                points[:,0]+=left;points[:,1]+=top  # [4,2], offset physical tile origin
                items.append((points.tolist(),tile,bool(edge)))
        boxes=merge_tile_boxes(items)
        crops,polygons=[],[]
        for box in boxes:
            points=np.asarray(box,np.float32)  # [4,2], physical full-image polygon
            # Rectify each detected quadrilateral before character recognition.
            cw = round(max(np.linalg.norm(points[0]-points[1]), np.linalg.norm(points[2]-points[3])))
            ch = round(max(np.linalg.norm(points[0]-points[3]), np.linalg.norm(points[1]-points[2])))
            if min(cw, ch) < 2:
                continue
            target = np.array([[0, 0], [cw-1, 0], [cw-1, ch-1], [0, ch-1]], np.float32)  # [4,2]
            transform = cv2.getPerspectiveTransform(points, target)  # [3,3]
            crop = cv2.warpPerspective(bgr, transform, (cw, ch), borderMode=cv2.BORDER_REPLICATE)  # [ch,cw,3]
            # Split long lines into bounded strips instead of squeezing glyphs or changing graph shapes.
            # Linear interpolation maps each strip back onto the original quadrilateral.
            strip_width = max(1, math.floor(self.width*ch/self.height))
            for left in range(0, cw, strip_width):
                right = min(cw, left+strip_width)
                a, b = left/cw, right/cw
                tl = points[0]*(1-a)+points[1]*a  # [2], upper left strip corner
                tr = points[0]*(1-b)+points[1]*b  # [2]
                bl = points[3]*(1-a)+points[2]*a  # [2]
                br = points[3]*(1-b)+points[2]*b  # [2]
                crops.append(crop[:, left:right])  # [ch,strip_width,3]
                polygons.append(np.array([tl, tr, br, bl]).tolist())  # [4,2]
        self.timings = {"detection_ms":(perf_counter()-t)*1000,"ocr_tiles":len(windows),"detected_boxes":len(boxes)}
        t = perf_counter()
        regions = []
        # Similar aspect ratios share a batch; fixed widths avoid per-frame GPU specialization.
        order = sorted(range(len(crops)), key=lambda i: crops[i].shape[1]/crops[i].shape[0])
        crops, polygons = [crops[i] for i in order], [polygons[i] for i in order]
        # Compile two shapes once. Short labels use modest padding; longer lines keep the wide graph.
        narrow_count=sum(self.height*crop.shape[1]/crop.shape[0]<=160 for crop in crops)
        batches=[(offset,min(offset+self.batch_size,narrow_count),self.narrow_width,self.narrow_recognizer)
                 for offset in range(0,narrow_count,self.batch_size)]
        batches += [(offset,min(offset+self.batch_size,len(crops)),self.width,self.recognizer)
                    for offset in range(narrow_count,len(crops),self.batch_size)]
        for offset,end,width,recognizer in batches:
            batch = crops[offset:end]
            # Pad the last batch to the compiled shape; ignore dummy rows on decode.
            tensor = np.zeros((self.batch_size, 3, self.height, width), np.float32)  # [B,3,H,W_batch]
            for i, crop in enumerate(batch):
                new_width = min(width, math.ceil(self.height*crop.shape[1]/crop.shape[0]))
                resized = cv2.resize(crop, (new_width, self.height))  # [H,new_width,3]
                normalized = resized.astype(np.float32)/127.5 - 1.0  # [H,new_width,3]
                tensor[i, :, :, :new_width] = normalized.transpose(2, 0, 1)  # [3,H,new_width]
            prediction = recognizer([tensor])
            indices, scores = prediction[0], prediction[1]  # each [B,T]
            decoded = self.decode.decode(indices, scores, is_remove_duplicate=True)
            for i, (text, confidence) in enumerate(decoded[:len(batch)]):
                if text.strip() and confidence >= self.config["recognition_threshold"]:
                    regions.append(TextRegion(polygons[offset+i], text.strip(), float(confidence)))
        self.timings["recognition_ms"] = (perf_counter()-t)*1000
        return regions

    def _detect(self,bgr):
        """Return tile-local detector quads without reducing native text resolution."""
        h,w=bgr.shape[:2]
        ratio = min(1.0,self.side/w, self.side/h)
        rw, rh = max(1, round(w*ratio)), max(1, round(h*ratio))
        resized = cv2.resize(bgr, (rw, rh))  # [rh,rw,3]
        # Letterbox preserves glyph aspect ratios and maps polygons back exactly.
        canvas = np.zeros((self.side, self.side, 3), np.uint8)  # [S,S,3]
        canvas[:rh, :rw] = resized
        normalized = canvas.astype(np.float32)/127.5 - 1.0  # [S,S,3]
        tensor = normalized.transpose(2, 0, 1)[None].copy()  # [1,3,S,S]
        prediction = self.detector([tensor])[0]  # [1,1,S',S']
        boxes, _ = self.postprocess(prediction, (self.side, self.side))
        polygons=[]
        for box in boxes:
            # Remove detections in letterbox padding; undo the actual rounded resize.
            points = np.asarray(box, dtype=np.float32).copy()  # [4,2], TL/TR/BR/BL
            if points[:, 0].mean() >= rw or points[:, 1].mean() >= rh:
                continue
            points[:, 0] = np.clip(points[:, 0]*w/rw, 0, w-1)  # [4]
            points[:, 1] = np.clip(points[:, 1]*h/rh, 0, h-1)  # [4]
            polygons.append(points)
        return polygons
