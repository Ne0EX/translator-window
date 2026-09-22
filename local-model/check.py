"""Check protocol guards; --model adds real inference with all networking denied."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
from worker import Translator


def command(model):
    code = '''import runpy, socket, sys
def deny_network(*args, **kwargs):
    raise AssertionError("Network access attempted during offline translation")
socket.socket.connect = deny_network
socket.create_connection = deny_network
sys.argv = sys.argv[1:]
runpy.run_path(sys.argv[0], run_name="__main__")
'''
    return [sys.executable, '-c', code, str(Path(__file__).with_name('worker.py')), '--model', str(model)]


def run(model, lines):
    process = subprocess.run(command(model), input='\n'.join(lines)+'\n', capture_output=True,
                             text=True, encoding='utf-8', timeout=180)
    assert process.returncode == 0, process.stderr
    replies = [json.loads(line) for line in process.stdout.splitlines()]
    assert len(replies) == len(lines), process.stdout
    return replies


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', type=Path)
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding='utf-8')
    with tempfile.TemporaryDirectory() as empty:
        replies = run(empty, ['invalid json', '[]', json.dumps({'texts':[42],'source':'ko','target':'en'}),
                             json.dumps({'texts':['hello'],'source':'en','target':'th'})])
        assert all('error' in reply for reply in replies), replies
        assert 'Local model is missing' in replies[-1]['error'], replies[-1]

    class Engine:
        def tokenize(self, text): return [1, 2]
        def create_chat_completion(self, **kwargs):
            return {'choices':[{'finish_reason':'stop','message':{'content':'translated'}}]}
        def close(self): pass
    class MissingCuda(Engine):
        def create_chat_completion(self, **kwargs): raise RuntimeError('CUDA allocation failed')
    class Truncated(Engine):
        def create_chat_completion(self, **kwargs):
            return {'choices':[{'finish_reason':'length','message':{'content':'partial'}}]}
    probe = Translator('unused','auto')
    probe.engine, probe.using_cuda = MissingCuda(), True
    probe.load = lambda: setattr(probe,'engine',Engine())
    assert probe.complete('test')['choices'][0]['message']['content'] == 'translated'
    assert probe.device == 'cpu'
    probe.engine, probe.load = Truncated(), lambda: None
    try:
        probe.translate({'texts':['test'],'source':'en','target':'th'})
        raise AssertionError('A partial translation was accepted')
    except ValueError as error:
        assert 'output limit' in str(error)
    assert not probe.cache, 'Partial translations must not enter the cache'
    print('PASS: invalid input stays in sync; missing assets fail offline; CUDA failure falls back; output truncation is rejected.')
    if not args.model:
        return
    for source, target in [('MODEL_CARD.md','MODEL_CARD.md'),('LICENSE-APACHE-2.0.txt','LICENSE.txt'),('ATTRIBUTION.md','ATTRIBUTION.md')]:
        assert (args.model/target).read_bytes() == (Path(__file__).parent/'attribution'/source).read_bytes(), target
    process = subprocess.Popen(command(args.model), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True, encoding='utf-8')
    def request(source,target,texts):
        started=time.perf_counter()
        process.stdin.write(json.dumps({'texts':texts,'source':source,'target':target})+'\n');process.stdin.flush()
        line=process.stdout.readline()
        assert line, process.stderr.read()
        reply=json.loads(line)
        assert 'error' not in reply, reply
        output=reply['translations']
        assert len(output)==len(texts) and all(output), reply
        print(json.dumps({'pair':source+'->'+target,'seconds':round(time.perf_counter()-started,3),'translations':output},ensure_ascii=False))
        return output
    try:
        assert request('ja-comic','th',[]) == []
        samples={'en':'The train leaves at 9.','ja':'電車は9時に出発します。','ko':'기차는 9시에 출발합니다.','th':'รถไฟออกตอน 9 โมง'}
        for source,text in samples.items():
            for target in samples:
                if source==target: continue
                output=request(source,target,[text])[0]
                assert output!=text
                if target=='th': assert any('\u0e00'<=c<='\u0e7f' for c in output)
                if target=='ko': assert any('\uac00'<=c<='\ud7a3' for c in output)
                if target=='ja': assert any('\u3040'<=c<='\u30ff' for c in output)
        first=request('ja-vert','th-TH',['本日は君に当院の当直のアルバイトをお願いする'])
        assert 'พาร์ทไทม์' in first[0] and 'โรงพยาบาล' in first[0], 'The work and hospital meanings must survive.'
        assert request('ja-comic','th',['本日は君に当院の当直のアルバイトをお願いする'])==first
        assert request('en-US','en',['Unchanged.'])==['Unchanged.']
        print('PASS: all twelve directions among Japanese, Korean, English and Thai; region tags; warm cache; semantic work/hospital check; no network.')
    finally:
        process.stdin.close()
        try: process.wait(timeout=30)
        except subprocess.TimeoutExpired: process.kill(); process.wait()


if __name__=='__main__':
    main()
