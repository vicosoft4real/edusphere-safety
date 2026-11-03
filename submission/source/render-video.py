#!/usr/bin/env python3
"""Render the 180-second narrated demo from real recordings and original graphics.

Requires Pillow, FFmpeg and FFprobe. No stock footage, invented metrics or mocked
screens. Raw recordings and speech remain separate for a reproducible edit.
"""
import html
import json
from pathlib import Path
import re
import subprocess
import textwrap
from PIL import Image, ImageDraw, ImageFont

ROOT=Path(__file__).resolve().parents[2]
SUB=ROOT/'submission'
ASSETS=SUB/'assets'
RENDER=SUB/'render'
RENDER.mkdir(exist_ok=True)
SCENES=json.loads((SUB/'source/scenes.json').read_text())
W,H=1920,1080
BG='#081d20'; INK='#f2f4e8'; MUTED='#a7b9b4'; LIME='#c5f277'; LINE='#284347'; CARD='#102c30'
FONT='/System/Library/Fonts/Avenir Next.ttc'
MONO='/System/Library/Fonts/Menlo.ttc'

def font(size,mono=False):return ImageFont.truetype(MONO if mono else FONT,size)
def write(draw,xy,text,size=30,color=INK,width=None,spacing=10,mono=False):
    text=text.replace('→','>')
    x,y=xy; f=font(size,mono)
    lines=[]
    for part in text.split('\n'):
        if width:
            current=''
            for word in part.split():
                candidate=(current+' '+word).strip()
                if draw.textlength(candidate,font=f)>width and current:
                    lines.append(current);current=word
                else:current=candidate
            lines.append(current)
        else:lines.append(part)
    for line in lines:
        draw.text((x,y),line,font=f,fill=color,stroke_width=0)
        y+=size+spacing
    return y
def rect(d,box,fill=CARD,radius=22,outline=LINE):d.rounded_rectangle(box,radius,fill=fill,outline=outline,width=2)
def pill(d,x,y,label):
    f=font(22);w=d.textlength(label,font=f)+32
    d.rounded_rectangle((x,y,x+w,y+40),20,fill=LIME)
    d.text((x+16,y+5),label,font=f,fill=BG)
def base(scene):
    im=Image.new('RGB',(W,H),BG);d=ImageDraw.Draw(im)
    for x in range(64,W,80):
        for y in range(140,940,80):d.ellipse((x,y,x+1,y+1),fill='#244043')
    d.rounded_rectangle((64,48,110,94),12,fill=LIME)
    d.line([(76,77),(83,84),(100,64)],fill=BG,width=5)
    write(d,(128,49),'EDUSPHERE / SAFETY',29)
    write(d,(1370,53),'META GLOBAL AI HACKATHON',22,MUTED)
    d.line((64,120,1856,120),fill=LINE,width=2)
    write(d,(64,911),scene['tag'],21,MUTED,width=1740,spacing=2)
    d.line((64,1055,1856,1055),fill=LINE,width=4)
    d.line((64,1055,64+1792*scene['id']/len(SCENES),1055),fill=LIME,width=4)
    write(d,(1784,1004),f"{scene['id']:02d}/{len(SCENES):02d}",20,MUTED,mono=True)
    return im,d

def background(scene):
    im,d=base(scene);kind=scene['kind']
    if kind in ('screen','simulation'):
        write(d,(64,177),scene['chapter'],21,LIME,width=420)
        write(d,(64,245),scene['title'],53,width=430,spacing=10)
        y=545 if scene['id']!=8 else 560
        for n,b in enumerate(scene['bullets'],1):
            d.ellipse((64,y+9,73,y+18),fill=LIME)
            y=write(d,(88,y),b,25,MUTED,width=360,spacing=6)+24
        if kind=='simulation':
            rect(d,(64,751,482,877))
            write(d,(84,765),'ACTUAL EDGE RUN',19,LIME)
            write(d,(84,802),'frames=60  events=1',22,INK,mono=True)
            write(d,(84,838),'queued=1',20,MUTED,mono=True)
        d.rounded_rectangle((540,148,1836,904),18,fill='#d4dfd8',outline=LINE,width=2)
        for x,c in [(565,'#8a9c96'),(586,'#8a9c96'),(607,'#8a9c96')]:d.ellipse((x,165,x+10,175),fill=c)
        write(d,(640,152),'LOCAL DEMO  /  '+(scene.get('clip','synthetic edge proof')).upper(),19,'#29443d')
        d.rectangle((548,190,1828,910),fill='#f5f5f4')
    elif kind=='hook':
        write(d,(64,183),scene['chapter'],23,LIME)
        write(d,(64,273),scene['title'],97,width=1000,spacing=10)
        write(d,(70,548),'Camera events, human review,\nand an accountable response.',34,MUTED,spacing=14)
        pill(d,70,713,'BUILT WITH META LLAMA')
        for i,(label,sub) in enumerate([('01  DETECT','A scheduled zone rule fires'),('02  UNDERSTAND','Evidence + local Llama briefing'),('03  RESPOND','Acknowledge → investigate → resolve')]):
            y=248+i*185;rect(d,(1110,y,1828,y+153))
            write(d,(1142,y+23),label,29,LIME)
            write(d,(1142,y+78),sub,27,INK,width=650)
    elif kind=='architecture':
        write(d,(64,171),scene['chapter'],23,LIME)
        write(d,(64,221),scene['title'].replace('\n',' '),65)
        cards=[(64,520,'SCHOOL-SIDE EDGE','FFmpeg decode\nPerson tracks\nScheduled rules\nPersistent outbox'),
               (704,1210,'CLOUD SERVICES','.NET 10 API\nPostgreSQL incidents\nEvidence + audit log\nNotification worker'),
               (1392,1856,'STAFF CONSOLE','Next.js dashboard\nEvidence review\nLlama briefing\nRecorded staff actions')]
        for x1,x2,title,body in cards:
            rect(d,(x1,370,x2,695));write(d,(x1+28,394),title,23,LIME)
            write(d,(x1+28,460),body,29,spacing=15)
        for x1,x2,label in [(534,692,'demo handoff'),(1224,1380,'authorized API')]:
            d.line((x1,527,x2-14,527),fill=LIME,width=3)
            d.polygon([(x2-14,519),(x2-14,535),(x2-3,527)],fill=LIME)
            write(d,(x1-4,557),label,18,MUTED,width=x2-x1)
        rect(d,(704,734,1856,858))
        write(d,(738,751),'META LLAMA 3.2 / LOCAL OLLAMA',25,LIME)
        write(d,(738,799),'Authorized metadata → structured briefing → staff review',27)
        write(d,(64,747),'REPLAY USED IN DEMO',22,LIME)
        write(d,(64,790),'YOLOX / ONNX adapter exists.\nAccuracy benchmarks remain pending.',23,MUTED,width=570)
    elif kind=='validation':
        write(d,(64,171),scene['chapter'],23,LIME)
        write(d,(64,226),scene['title'].replace('\n',' '),65)
        write(d,(68,335),'509',190,LIME,spacing=0)
        write(d,(78,575),'EXISTING AUTOMATED TESTS PASSED',27)
        write(d,(78,629),'0 failed · 0 skipped\nIncludes PostgreSQL integration tests',26,MUTED,spacing=18)
        rect(d,(895,354,1856,856))
        write(d,(931,384),'VERIFIED IN THE RUNNING APP',24,LIME)
        write(d,(931,437),'Sign-in + tenant checks\nByte-identical evidence playback\nLocal Meta Llama inference\nIncident response workflow',30,spacing=17)
        d.line((931,654,1810,654),fill=LINE,width=2)
        write(d,(931,680),'NEXT: field cameras, detector benchmarks,\nautomatic edge delivery, production SSO',26,MUTED,spacing=15)
    elif kind=='close':
        write(d,(64,182),scene['chapter'],23,LIME)
        write(d,(64,286),scene['title'],99,width=1650,spacing=10)
        write(d,(70,569),'Built with Meta Llama. Designed for school staff.',35,MUTED)
        pill(d,70,704,'OPEN SOURCE / MIT')
        write(d,(70,786),'github.com/vicosoft4real/edusphere-safety',29,LIME,mono=True)
    path=RENDER/f"scene-{scene['id']:02d}.png";im.save(path)
    return path

def probe(path):
    return float(subprocess.check_output(['ffprobe','-v','error','-show_entries','format=duration','-of','default=noprint_wrappers=1:nokey=1',str(path)],text=True))
def stamp(sec):
    ms=round(sec*1000);return f'{ms//3600000:02d}:{ms//60000%60:02d}:{ms//1000%60:02d},{ms%1000:03d}'

def render():
    clips=json.loads((SUB/'recordings/clips.json').read_text())
    subtitles=[];global_time=0;script=[]
    for scene in SCENES:
        ident=scene['id'];duration=scene['duration'];bg=background(scene)
        audio=ASSETS/'audio'/f'{ident:02d}.mp3'
        audio_duration=probe(audio);speed=max(1,audio_duration/(duration-0.55));spoken=audio_duration/speed
        sentences=re.split(r'(?<=[.!?])\s+',scene['narration'])
        counts=[len(s.split()) for s in sentences];total=sum(counts);t=0.25;caption_inputs=[]
        for n,(sentence,count) in enumerate(zip(sentences,counts)):
            end=min(duration-0.1,t+spoken*count/total)
            cap=Image.new('RGBA',(W,116),(0,0,0,0));cd=ImageDraw.Draw(cap)
            cd.rounded_rectangle((64,5,1745,112),12,fill=(5,16,18,247))
            lines=textwrap.wrap(sentence,width=103)
            size=29 if len(lines)<=2 else 26
            for j,line in enumerate(lines):cd.text((88,21+j*(size+8)),line,font=font(size),fill=INK)
            cp=RENDER/f'caption-{ident:02d}-{n}.png';cap.save(cp)
            caption_inputs.append((cp,t,end))
            subtitles.append(f'{len(subtitles)+1}\n{stamp(global_time+t)} --> {stamp(global_time+end)}\n{sentence}\n')
            t=end
        cmd=['ffmpeg','-y','-hide_banner','-loglevel','error','-loop','1','-framerate','30','-i',str(bg)]
        filters=[];current='0:v';input_count=1
        if scene['kind']=='screen':
            clip=clips[scene['clip']];cmd+=['-i',clip['file']];input_count+=1
            if scene['clip']=='llama':
                # Show the actual click and actual generated result, omitting the
                # middle of the model wait. The on-screen tag discloses this edit.
                off=max(0,clip['offset']-0.15);ready=clip['offset']+clip['readyAt']
                filters += [f'[1:v]split=2[a][b]',f'[a]trim=start={off}:duration=3,setpts=PTS-STARTPTS[a1]',
                    f'[b]trim=start={ready}:duration=15,setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration=8[b1]',
                    f'[a1][b1]concat=n=2:v=1:a=0,scale=1280:720,fps=30[screen]']
            else:
                off=max(0,clip['offset']-0.15)
                filters += [f'[1:v]trim=start={off},setpts=PTS-STARTPTS,scale=1280:720,fps=30,tpad=stop_mode=clone:stop_duration=5[screen]']
            filters += ['[0:v][screen]overlay=548:190:shortest=0[app]'];current='app'
        elif scene['kind']=='simulation':
            cmd+=['-i',str(ASSETS/'sim/sim.mp4')];input_count+=1
            filters += [f'[1:v]setpts={duration/12}*(PTS-STARTPTS),scale=960:720,pad=1280:720:160:0:color=0x102c30,drawbox=x=496:y=0:w=288:h=720:color=0xc5f277@0.18:t=fill,drawbox=x=496:y=0:w=288:h=720:color=0xc5f277@0.8:t=3,fps=30[sim]',
                '[0:v][sim]overlay=548:190:shortest=0[app]'];current='app'
        for n,(cp,start,end) in enumerate(caption_inputs):
            idx=input_count;cmd+=['-loop','1','-framerate','30','-i',str(cp)];input_count+=1
            out=f'caption{n}'
            filters.append(f'[{current}][{idx}:v]overlay=0:940:enable=\'between(t,{start:.4f},{end:.4f})\'[{out}]');current=out
        audio_idx=input_count;cmd+=['-i',str(audio)]
        filters.append(f'[{audio_idx}:a]atempo={speed:.6f},adelay=250|250,apad,atrim=duration={duration},loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000[audio]')
        output=RENDER/f'{ident:02d}.mp4'
        cmd+=['-filter_complex_threads','1','-filter_complex',';'.join(filters),'-map',f'[{current}]','-map','[audio]',
            '-t',str(duration),'-r','30','-c:v','libx264','-preset','veryfast','-crf','20','-pix_fmt','yuv420p',
            '-threads','4','-c:a','aac','-b:a','160k','-ar','48000','-ac','2',str(output)]
        print(f"Rendering scene {ident:02d}/{len(SCENES)}: {duration}s, voice speed {speed:.2f}x",flush=True)
        subprocess.run(cmd,check=True)
        global_time+=duration
        script.append(f"### {stamp(global_time-duration)[:8]}–{stamp(global_time)[:8]} · {scene['chapter']}\n\n{scene['narration']}\n")
    (SUB/'edusphere-safety-demo.srt').write_text('\n'.join(subtitles))
    (SUB/'NARRATION.md').write_text('# EduSphere Safety · three-minute narration\n\nSynthetic English narration. All app footage is recorded from the running project.\n\n'+'\n'.join(script))
    concat=RENDER/'concat.txt';concat.write_text('\n'.join(f"file '{RENDER/f'{s['id']:02d}.mp4'}'" for s in SCENES))
    final=SUB/'edusphere-safety-demo-3min.mp4'
    subprocess.run(['ffmpeg','-y','-hide_banner','-loglevel','error','-f','concat','-safe','0','-i',str(concat),
        '-i',str(SUB/'edusphere-safety-demo.srt'),'-map','0:v','-map','0:a','-map','1:0','-c:v','copy','-c:a','copy',
        '-c:s','mov_text','-metadata:s:s:0','language=eng','-metadata','title=EduSphere Safety — Meta Global AI Developer Hackathon',
        '-metadata','comment=Real app recordings; synthetic pipeline footage; local Meta Llama briefing; inference wait shortened.',
        '-t','179.95','-movflags','+faststart',str(final)],check=True)
    subprocess.run(['ffmpeg','-y','-loglevel','error','-ss','3','-i',str(final),'-frames:v','1',str(SUB/'demo-poster.jpg')],check=True)
    print('Final duration:',probe(final),'seconds',flush=True)
    storyboard()

def storyboard():
    frames='\n'.join(f'<section class="frame" data-duration="{s["duration"]*1000}"><img src="render/scene-{s["id"]:02d}.png"/><div class="script">{html.escape(s["narration"])}</div><!-- frame:{s["id"]} duration:{s["duration"]*1000} transition:cut --></section>' for s in SCENES)
    meta=[{'duration':s['duration']*1000,'transition':'cut','sceneSummary':s['chapter']} for s in SCENES]
    (SUB/'storyboard.html').write_text('''<!doctype html><html lang="en"><meta charset="utf-8"><title>EduSphere Safety · demo storyboard</title><style>body{margin:0;background:#081d20;color:#f2f4e8;font:20px system-ui}.frame{position:relative;width:1920px;height:1080px;display:none;transform-origin:top left}.frame.active{display:block}.frame img{width:100%}.script{position:absolute;bottom:18px;left:70px;right:80px;background:#081d20;padding:20px;font-size:29px}#progress{position:fixed;bottom:0;left:0;background:#c5f277;height:4px}</style>'''+frames+'''<div id="progress"></div><script>const frames=[...document.querySelectorAll('.frame')];let i=0,timer;function show(n){clearTimeout(timer);i=(n+frames.length)%frames.length;frames.forEach((f,j)=>f.classList.toggle('active',j===i));const s=Math.min(innerWidth/1920,innerHeight/1080);frames[i].style.transform=`scale(${s})`;document.querySelector('#progress').style.width=((i+1)/frames.length*100)+'%';timer=setTimeout(()=>show(i+1),+frames[i].dataset.duration)}addEventListener('keydown',e=>show(i+(e.key==='ArrowLeft'?-1:1)));addEventListener('click',()=>show(i+1));addEventListener('resize',()=>show(i));show(0)</script>'''+f'<!-- HYPERFRAMES_META: {json.dumps(meta)} -->')

if __name__=='__main__':render()
