"""Render measured boxes/track IDs on their source clip. Requires Pillow + FFmpeg.

Accepts raw SAM pixel-box output or the local product validation probe's normalized
boxes. No detections are generated, interpolated, or invented by this renderer.
"""
import argparse
from collections import defaultdict, deque
import json
from pathlib import Path
import subprocess
from PIL import Image, ImageDraw, ImageFont

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--video', required=True)
    parser.add_argument('--tracks', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--fps', type=float, default=10)
    parser.add_argument('--title', required=True)
    parser.add_argument('--source-label', required=True)
    parser.add_argument('--zone', help='JSON file with normalized polygon points')
    args = parser.parse_args()
    info = json.loads(subprocess.check_output(['ffprobe','-v','error','-select_streams','v:0',
        '-show_entries','stream=width,height','-of','json',args.video]))['streams'][0]
    w,h = info['width'],info['height']
    rows = {r['frame']:r for r in (json.loads(line) for line in Path(args.tracks).read_text().splitlines())}
    zone = json.loads(Path(args.zone).read_text()) if args.zone else None
    font_path = '/System/Library/Fonts/Menlo.ttc'
    font = ImageFont.truetype(font_path,14) if Path(font_path).exists() else ImageFont.load_default()
    small = ImageFont.truetype(font_path,11) if Path(font_path).exists() else font
    decode = subprocess.Popen(['ffmpeg','-v','error','-i',args.video,'-vf',f'fps={args.fps}',
        '-f','rawvideo','-pix_fmt','rgb24','pipe:1'],stdout=subprocess.PIPE)
    encode = subprocess.Popen(['ffmpeg','-y','-v','error','-f','rawvideo','-pix_fmt','rgb24',
        '-s',f'{w}x{h+96}','-r',str(args.fps),'-i','pipe:0','-an','-c:v','libx264','-crf','19',
        '-pix_fmt','yuv420p','-movflags','+faststart',args.out],stdin=subprocess.PIPE)
    trails = defaultdict(lambda:deque(maxlen=32))
    palette = ['#c5f277','#73d5ff','#ffc27c','#d5a5ff','#ff8b9e','#8cf0d0']
    n = 0
    events = 0
    try:
        while True:
            data = decode.stdout.read(w*h*3)
            if not data:
                break
            if len(data) != w*h*3:
                raise RuntimeError('Incomplete decoded video frame')
            im = Image.new('RGB',(w,h+96),'#081d20')
            im.paste(Image.frombytes('RGB',(w,h),data),(0,64))
            draw = ImageDraw.Draw(im)
            draw.text((12,8),args.title,font=font,fill='#c5f277')
            draw.text((12,33),args.source_label,font=small,fill='#c8d7d2')
            row = rows.get(n,{'tracks':[]})
            events += len(row.get('events',[]))
            if zone:
                points=[(round(x*w),64+round(y*h)) for x,y in zone]
                draw.line(points+[points[0]],fill='#ffcd68',width=2)
                draw.text((points[0][0]+4,points[0][1]+4),'DEMONSTRATION ZONE',font=small,fill='#ffcd68')
            matched = 0
            labels = []
            for t in row['tracks']:
                ident=t['id'];box=t['box'];stale=t.get('misses',0)>0
                if isinstance(box,dict):
                    x1,y1,x2,y2=box['x']*w,box['y']*h,(box['x']+box['w'])*w,(box['y']+box['h'])*h
                else:
                    x1,y1,x2,y2=box
                color='#9aa5a1' if stale else palette[ident%len(palette)]
                if not stale:
                    matched+=1;trails[ident].append(((x1+x2)/2,64+y2))
                if len(trails[ident])>1:draw.line(list(trails[ident]),fill=color,width=2)
                draw.rectangle((x1,64+y1,x2,64+y2),outline=color,width=2)
                label=f"ID {ident} | {t['confidence']:.2f}"+(' held' if stale else '')
                labelw=draw.textlength(label,font=small)+8
                left=max(0,min(w-labelw,x1))
                top=max(64,64+y1-18)
                for offset in (18,36,54,72):
                    top=max(64,64+y1-offset)
                    candidate=(left,top,left+labelw,top+17)
                    if not any(candidate[0]<b[2] and candidate[2]>b[0] and candidate[1]<b[3] and candidate[3]>b[1] for b in labels):break
                labels.append(candidate)
                draw.rectangle(candidate,fill='#081d20')
                draw.text((left+3,top+1),label,font=small,fill=color)
            draw.text((12,h+73),f'Frame {n} | matched tracks {matched} | zone events {events} | playback {args.fps:g} fps',font=small,fill='#d9e4df')
            encode.stdin.write(im.tobytes())
            if n==40:im.save(str(Path(args.out).with_suffix('.jpg')))
            n+=1
    finally:
        decode.stdout.close();encode.stdin.close()
        decode.wait();encode.wait()
    if decode.returncode or encode.returncode:
        raise RuntimeError('Video rendering failed')
    print(f'Rendered {n} source frames to {args.out}')

if __name__=='__main__':main()
