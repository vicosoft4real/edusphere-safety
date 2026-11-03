import unittest
from sam_tracking import parse_tracks, make_request, response_text

def row(frame, obj=7, x=10, confidence='0.9'):
    return f'<{frame}f>{obj}<|box;x1={x};y1=20;x2={x+19};y2=59;w=100;h=80;c={confidence}|><|mask;x=0;y=0;c={confidence};data=40,20,!!!!!|>'

class SamTrackingTests(unittest.TestCase):
    def test_preserves_sparse_ids_and_explicit_frame_numbers(self):
        got = parse_tracks(row(2, 7)+'\n'+row(0, 42)+'\n'+row(3, 7, 30), 100, 80, 5)
        self.assertEqual([0, 2, 3], [r['frame'] for r in got])
        self.assertEqual([42, 7, 7], [r['tracks'][0]['id'] for r in got])
        self.assertEqual([10, 20, 30, 60], got[0]['tracks'][0]['box'])
        self.assertEqual([30, 20, 50, 60], got[2]['tracks'][0]['box'])

    def test_empty_output_is_no_detections(self):
        self.assertEqual([], parse_tracks('', 100, 80, 5))

    def test_rejects_corrupt_or_mismatched_records(self):
        for bad in [row(5), row(0, confidence='1.1'), row(0, x=95), row(0).replace('w=100','w=200'), 'garbage', row(0)+'\n'+row(0)]:
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                parse_tracks(bad, 100, 80, 5)

    def test_request_sends_video_pixels_and_requests_confidence(self):
        req = make_request(b'video-bytes')
        self.assertEqual('sam-3.1', req['model'])
        parts = req['input'][0]['content']
        self.assertEqual({'type':'input_text','text':'person'}, parts[0])
        self.assertEqual('data:video/mp4;base64,dmlkZW8tYnl0ZXM=', parts[1]['video_url'])
        self.assertEqual('true', req['metadata']['include_confidence'])

    def test_rejects_incomplete_response(self):
        with self.assertRaises(ValueError):
            response_text({'status':'incomplete','output_text':row(0)})

    def test_reads_completed_response_content_lane(self):
        r={'status':'completed','output':[{'type':'message','content':[{'type':'output_text','text':row(0)}]}]}
        self.assertEqual(row(0), response_text(r))

if __name__ == '__main__': unittest.main()
