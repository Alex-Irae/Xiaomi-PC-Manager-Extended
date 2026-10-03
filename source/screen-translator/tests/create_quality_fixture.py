"""Generate public small-text/prose OCR comparison inputs, with no desktop capture or inference.

Dependencies: Pillow and Windows Microsoft YaHei. Outputs: new results/quality-fixture/NNN directory.
Command: python tests/create_quality_fixture.py
"""
import json
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from PIL import Image,ImageDraw,ImageFont
from screen_translator.cli import result_directory,save_json


def main():
    root=Path(__file__).resolve().parents[1];directory=result_directory(root/"results/quality-fixture")
    texts=json.loads((root/"tests/fixtures/translation-corpus.json").read_text(encoding="utf-8"))
    image=Image.new("RGB",(3120,2080),"white");draw=ImageDraw.Draw(image);truth=[]
    colours=[(40,40,45),(210,210,215),(100,104,112)];sizes=[14,18,24]
    draw.rectangle((1040,0,2079,2079),fill=(34,36,40))
    for column,size in enumerate(sizes):
        font=ImageFont.truetype("C:/Windows/Fonts/msyh.ttc",size)
        for index,text in enumerate(texts):
            x,y=40+1040*column,100+76*index;draw.text((x,y),text,font=font,fill=colours[column]);bounds=draw.textbbox((x,y),text,font=font)
            truth.append({"source":text,"bounds":list(bounds),"font_size":size,"panel":column})
    image.save(directory/"input.png")
    save_json(directory/"config.json",{"seed":0,"screen_captured":False,"image_size":list(image.size),"font":"Microsoft YaHei","sizes":sizes,"truth":truth,"protocol":"72 public Chinese prose lines, 14/18/24 physical pixels, light/dark/low-contrast panels; no Latin glossary shortcuts"})
    print(directory,flush=True)


if __name__=="__main__":main()
