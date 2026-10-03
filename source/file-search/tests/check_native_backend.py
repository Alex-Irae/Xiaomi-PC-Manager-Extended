"""Check the native shell's private stdio RPC without starting a GUI.

Dependencies: existing project environment. Outputs: temporary index and PASS.
Command from project root: .venv/Scripts/python.exe tests/check_native_backend.py
Real background inference: add --semantic --output results/NEW_RUN/stdio-semantic.
Only the child created here is shut down. Includes Unicode and native-action ACK.
"""
import argparse
import contextlib
import json
import queue
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import PROJECT, load


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--semantic',action='store_true',help='Check actual model inference on the background indexer')
    parser.add_argument('--output',type=Path,help='Preserve configuration, logs and index in a new directory')
    args=parser.parse_args()
    if args.output: args.output.mkdir(parents=True,exist_ok=False)
    context=contextlib.nullcontext(args.output) if args.output else tempfile.TemporaryDirectory(prefix='ai-center-stdio-')
    with context as temporary:
        directory=Path(temporary).resolve()
        corpus=directory/"corpus";corpus.mkdir()
        file=corpus/"résumé.md";file.write_text("Local embeddings find meaningful documents.",encoding="utf-8")
        settings=load(PROJECT/"config.json")
        settings.update(roots=[str(corpus.resolve())],excluded_folders=[],semantic_enabled=args.semantic,indexing_mode="normal",indexing_frequency='realtime')
        configuration=directory/"config.json";configuration.write_text(json.dumps(settings),encoding="utf-8")
        command=[sys.executable,"-m","xiaomi_search.backend","--config",str(configuration),"--data",str(directory/"data")]
        with (directory/"stderr.log").open("w",encoding="utf-8") as errors:
            process=subprocess.Popen(command,cwd=PROJECT,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=errors,text=True,encoding="utf-8")
            messages=queue.Queue()
            def reader():
                for line in process.stdout:messages.put(json.loads(line))
            threading.Thread(target=reader,daemon=True).start()
            def send(value):process.stdin.write(json.dumps(value,ensure_ascii=False)+"\n");process.stdin.flush()
            def request(identifier,method,params=None):
                send({"owner":"search","mode":0,"data":{"id":identifier,"request":{"method":method,"params":params or {}}}})
            def receive(identifier=None,kind=None):
                while True:
                    value=messages.get(timeout=10)
                    if (identifier is not None and value.get("id")==identifier) or (kind is not None and value.get("kind")==kind):return value
            try:
                assert receive(kind="ready")["settings"]["semantic_enabled"] is args.semantic
                for identifier in range(1,30):
                    request(identifier,"local_search",{"text":"résumé.md","semantic":False})
                    result=receive(identifier)["response"]
                    assert result["code"]==0
                    if result["data"]["results"]:break
                    threading.Event().wait(.1)
                else:raise AssertionError("Indexer did not discover Unicode filename")
                assert result["data"]["results"][0]["name"]=="résumé.md"
                request(100,"open_file",{"file_id":result["data"]["results"][0]["file_id"]})
                action=receive(kind="action")
                assert action["action"]=="open" and action["params"]["path"]==str(file)
                # ACK the request without opening an external document application.
                send({"kind":"action_result","token":action["token"],"value":None,"error":None})
                assert receive(100)["response"]["code"]==0
                if args.semantic:
                    deadline=time.monotonic()+90
                    while True:
                        request(101,'local_status')
                        status=receive(101)['response']['data']
                        assert not status['indexer']['semantic_error'],status
                        if status['counts']['vectors'] and not status['indexer']['busy'] and not status['indexer']['queued_embedding_files']:break
                        assert time.monotonic()<deadline,('Background native inference stalled',status)
                        time.sleep(.2)
                    request(102,'local_search',{'text':'finding documents by their meaning','semantic':True})
                    semantic=receive(102)['response']
                    assert semantic['code']==0 and 'Semantic' in semantic['data']['results'][0]['matches'],semantic
                    if args.output:
                        (directory/'semantic-check.json').write_text(json.dumps({'passed':True,'configuration':settings,'status':status,'search':semantic['data']},indent=2),encoding='utf-8')
                    print('PASS: real background embedding completed and semantic RPC retrieved Unicode file',flush=True)
                send({"kind":"shutdown"});process.stdin.close()
                code=process.wait(timeout=20)
                assert code==0, (directory/'stderr.log').read_text(encoding='utf-8')
            finally:
                if process.poll() is None:process.kill();process.wait()
    print("PASS: native stdio RPC, owner routing, Unicode search, native action acknowledgment, clean owned shutdown")


if __name__=="__main__":
    main()
