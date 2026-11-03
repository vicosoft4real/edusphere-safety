"""Meta SAM 3.1 clip tracking. Server/CLI only; never accepts a key on argv.

Contract: https://dev.meta.ai/docs/sam/segmenting and /sam/reading-segmentation.
Boxes are decoded; encoded masks are retained but not decoded by this adapter.
"""
import argparse
import base64
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import urllib.request
import urllib.error

RECORD = re.compile(r'(\d+)<\|box;x1=(\d+);y1=(\d+);x2=(\d+);y2=(\d+);w=(\d+);h=(\d+);c=([0-9.]+)\|><\|mask;x=0;y=0;c=([0-9.]+);data=(\d+),(\d+),([!~][^|\r\n]+)\|>')

def parse_tracks(text, width, height, frame_count):
    frames = {}
    for line in text.strip().splitlines():
        header = re.match(r'<(\d+)f>', line)
        if not header:
            raise ValueError('Malformed SAM frame marker')
        number = int(header[1])
        if number >= frame_count or number in frames:
            raise ValueError('Duplicate or out-of-range SAM frame')
        pos = header.end()
        tracks = []
        ids = set()
        while pos < len(line):
            match = RECORD.match(line, pos)
            if not match:
                raise ValueError('Malformed SAM object record')
            obj, x1, y1, x2, y2, w, h = map(int, match.groups()[:7])
            confidence, mask_confidence = map(float, match.groups()[7:9])
            mh, mw = map(int, match.groups()[9:11])
            if obj in ids or (w, h) != (width, height) or not (0 <= x1 <= x2 < w and 0 <= y1 <= y2 < h):
                raise ValueError('Invalid SAM identity or box geometry')
            if not (0 <= confidence <= 1 and confidence == mask_confidence) or not (0 < mh <= h and 0 < mw <= w):
                raise ValueError('Invalid SAM confidence or mask dimensions')
            ids.add(obj)
            # API x2/y2 are inclusive; downstream boxes are half-open.
            tracks.append({'id': obj, 'box': [x1, y1, x2 + 1, y2 + 1], 'confidence': confidence,
                           'encodedMask': {'height': mh, 'width': mw, 'data': match[12]}})
            pos = match.end()
            if pos < len(line):
                if line[pos] != ',':
                    raise ValueError('Malformed SAM record separator')
                pos += 1
                if pos == len(line):
                    raise ValueError('Trailing SAM separator')
        if not tracks:
            raise ValueError('Empty SAM frame record')
        frames[number] = {'frame': number, 'tracks': tracks}
    return [frames[n] for n in sorted(frames)]

def make_request(video):
    if not video or len(video) > 25 * 1024 * 1024:
        raise ValueError('Use an MP4 clip between 1 byte and 25 MiB')
    return {'model':'sam-3.1', 'input':[{'type':'message','role':'user','content':[
        {'type':'input_text','text':'person'},
        {'type':'input_video','video_url':'data:video/mp4;base64,' + base64.b64encode(video).decode('ascii')}
    ]}], 'stream':False, 'metadata':{'include_confidence':'true','mask_encoding':'one_bit'}}

def response_text(response):
    if response.get('status') != 'completed':
        raise ValueError('SAM response did not complete; no tracks accepted')
    if 'output_text' in response:
        return response['output_text']
    return ''.join(part['text'] for item in response.get('output', []) if item.get('type') == 'message'
                   for part in item.get('content', []) if part.get('type') == 'output_text')

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None

def main():
    parser = argparse.ArgumentParser(description='Track a real MP4 clip through Meta SAM 3.1. This uploads video to Meta and incurs API usage.')
    parser.add_argument('--video', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--key-file', type=Path, default=Path('.demo-runtime/meta-api.env'))
    args = parser.parse_args()
    key = os.environ.get('MODEL_API_KEY', '').strip()
    if not key and args.key_file.exists():
        for line in args.key_file.read_text().splitlines():
            if line.startswith('MODEL_API_KEY='):
                key = line.partition('=')[2].strip()
    if not key:
        raise ValueError('Set MODEL_API_KEY in the environment or ignored .demo-runtime/meta-api.env; never put it on the command line')
    if args.video.suffix.lower() != '.mp4':
        raise ValueError('Convert the input to MP4 before requesting tracking')
    info = json.loads(subprocess.check_output(['ffprobe','-v','error','-select_streams','v:0','-count_frames',
        '-show_entries','stream=width,height,avg_frame_rate,nb_read_frames','-of','json',str(args.video)]))['streams'][0]
    count = int(info['nb_read_frames'])
    if not 1 <= count <= 15000:
        raise ValueError('SAM accepts at most 15000 frames per clip')
    num, den = map(int, info['avg_frame_rate'].split('/'))
    fps = num / den
    if not math.isfinite(fps) or fps <= 0:
        raise ValueError('Invalid source frame rate')
    video = args.video.read_bytes()
    request = urllib.request.Request('https://api.meta.ai/v1/responses', data=json.dumps(make_request(video)).encode(),
        headers={'Authorization':'Bearer '+key,'Content-Type':'application/json'}, method='POST')
    try:
        with urllib.request.build_opener(NoRedirect).open(request, timeout=300) as res:
            raw = res.read(64 * 1024 * 1024 + 1)
            if len(raw) > 64 * 1024 * 1024:
                raise ValueError('SAM response exceeds local limit')
            result = json.loads(raw)
    except urllib.error.HTTPError as error:
        # Provider bodies may echo sensitive request data. Do not log them.
        raise RuntimeError(f'Meta SAM returned HTTP {error.code}; no fallback tracks generated') from None
    tracks = parse_tracks(response_text(result), info['width'], info['height'], count)
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out/'tracks.jsonl').write_text(''.join(json.dumps(row)+'\n' for row in tracks))
    summary = {'model':'sam-3.1','sourceSha256':hashlib.sha256(video).hexdigest(), 'width':info['width'],
        'height':info['height'],'fps':fps,'frames':count,'framesWithTracks':len(tracks),
        'uniqueTrackIds':len({t['id'] for r in tracks for t in r['tracks']}),
        'responseId':result.get('id'), 'usage':result.get('usage'),
        'note':'IDs come directly from SAM and are scoped to this response. Encoded masks retained; masks not decoded. No cross-clip identity claim.'}
    (args.out/'summary.json').write_text(json.dumps(summary, indent=2)+'\n')
    print(json.dumps(summary, indent=2))

if __name__ == '__main__':
    try:
        main()
    except (ValueError, RuntimeError, OSError, KeyError) as error:
        raise SystemExit(str(error))

