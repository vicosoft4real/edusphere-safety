#!/usr/bin/env python3
"""Generate synthetic English narration; fall back to local macOS speech.
Only the narration text is sent to the speech service. No project data or keys.
Run with submission/.venv/bin/python after installing edge-tts in that venv.
"""
import asyncio
import hashlib
import json
from pathlib import Path
import subprocess
import edge_tts

ROOT = Path(__file__).resolve().parents[2]
SCENES = json.loads((ROOT/'submission/source/scenes.json').read_text())
OUT = ROOT/'submission/assets/audio'
OUT.mkdir(parents=True,exist_ok=True)

async def main():
    semaphore=asyncio.Semaphore(3)
    async def generate(scene):
        async with semaphore:
            out=OUT/f"{scene['id']:02d}.mp3"
            digest=hashlib.sha256(scene['narration'].encode()).hexdigest()
            marker=out.with_suffix('.sha256')
            if out.exists() and marker.exists() and marker.read_text()==digest:
                return
            try:
                await edge_tts.Communicate(scene['narration'],'en-GB-RyanNeural',rate='+2%').save(str(out))
            except Exception as error:
                print(f"Speech service unavailable for scene {scene['id']}: {type(error).__name__}; using macOS Daniel.")
                aiff=out.with_suffix('.aiff')
                subprocess.run(['say','-v','Daniel','-r','155','-o',str(aiff),scene['narration']],check=True)
                subprocess.run(['ffmpeg','-y','-loglevel','error','-i',str(aiff),str(out)],check=True)
            duration=float(subprocess.check_output(['ffprobe','-v','error','-show_entries','format=duration',
                '-of','default=noprint_wrappers=1:nokey=1',str(out)],text=True))
            marker.write_text(digest)
            print(f"Scene {scene['id']:02d}: {duration:.2f}s narration / {scene['duration']}s screen time",flush=True)
    await asyncio.gather(*(generate(s) for s in SCENES))

asyncio.run(main())
