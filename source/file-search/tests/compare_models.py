"""Compare local Qwen/BGE embeddings and optional Qwen reranking on real files.
Dependencies: existing requirements.txt environment; local OpenVINO model exports.
Outputs: new JSON, CSV and SVG under --output, no production vector changes.
Command: .venv/Scripts/python.exe tests/compare_models.py --cases results/NEW_RUN/cases.json
         --output results/NEW_RUN/models01 --corpus ../screen-translator/ToRead
Candidate pool is the selected folder, not the entire desktop index.
"""
import argparse
import csv
import hashlib
import html
import json
import logging
import os
from pathlib import Path
import sys
import time
import numpy as np
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import load
from xiaomi_search.embedding import Embedder
from xiaomi_search.extract import chunks,SUPPORTED


def main():
    sys.stdout.reconfigure(encoding='utf-8',errors='backslashreplace')
    os.environ.update(HF_HUB_OFFLINE='1',TRANSFORMERS_OFFLINE='1',HF_HUB_DISABLE_TELEMETRY='1')
    logging.basicConfig(level=logging.INFO,format='%(asctime)s %(levelname)s %(message)s')
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cases',required=True,type=Path,help='Queries and expected basenames in excluded results')
    parser.add_argument('--output',required=True,type=Path,help='New evidence directory')
    parser.add_argument('--corpus',required=True,type=Path,help='Folder of real PDFs and text/config files')
    parser.add_argument('--bge',default='data/models/bge-small-en-v1.5-int8-ov',help='Optional downloaded OpenVINO BGE export')
    parser.add_argument('--reranker',default='C:/ProgramData/MI/AIModel/AIModelSearch/3.1.7/qwen3_reranker_int8_sym',help='Optional existing Qwen reranker export')
    parser.add_argument('--device',choices=['GPU','NPU','CPU'],default='GPU',help='Explicit device, no fallback')
    parser.add_argument('--passages',type=int,default=12,help='Maximum evenly sampled passages per file, includes the opening')
    args=parser.parse_args();args.output.mkdir(parents=True,exist_ok=False)
    settings=load('config.json');settings.update(preferred_device='auto',devices=[args.device],model_standby='keep_loaded')
    cases=json.loads(args.cases.read_text(encoding='utf-8-sig'))
    documents=[];inputs=[]
    for path in sorted(args.corpus.iterdir()):
        if path.suffix.lower() not in SUPPORTED:continue
        rows=chunks(path,settings)
        # Include each file's opening and spread remaining passages through the body.
        indices=np.unique(np.linspace(0,len(rows)-1,min(args.passages,len(rows)),dtype=int)) if rows else [] # shape: [K]
        inputs.append({'path':str(path.resolve()),'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'all_passages':len(rows),'selected_indices':[int(i) for i in indices]})
        for i in indices:documents.append({'name':path.name,'location':rows[i][0],'text':rows[i][1]})
    if not documents:raise ValueError('No extractable documents')
    report={'seed':42,'configuration':settings,'inputs':inputs,'protocol':'same evenly sampled passages; filename is excluded from embedding input; max passage cosine per file; rerank top 10 Qwen candidates','cases':cases,'variants':[]}
    profiles=[('Qwen3',{})]
    if (Path(args.bge)/'openvino_model.xml').is_file():
        profiles.append(('BGE-small',{'model_path':str(Path(args.bge).resolve()),'embedding_pooling':'CLS','embedding_padding_side':'right','query_instruction':'Represent this sentence for searching relevant passages: '}))
    qwen_candidates=[]
    for name,overrides in profiles:
        cfg={**settings,**overrides};engine=Embedder(cfg,args.output/name);variant={'name':name,'configuration':cfg,'cases':[]}
        try:
            print(f'EMBED {name}: {len(documents)} passages on {args.device}',flush=True)
            begin=time.perf_counter();vectors=engine.encode([d['text'] for d in documents]) # shape: [P,D]
            variant.update(document_setup_ms=(time.perf_counter()-begin)*1000,model_id=engine.identity(),device_actual=engine.device,vector_shape=list(vectors.shape))
            if engine.device!=args.device:raise AssertionError('Fallback is forbidden')
            for case in cases:
                begin=time.perf_counter();query=engine.encode([case['query']],query=True)[0] # shape: [D]
                # Normalized dot products are cosine relevance; choose the strongest passage per file.
                similarities=vectors@query # shape: [P], reduces D
                best={}
                for i,score in enumerate(similarities):
                    doc=documents[i]
                    if doc['name'] not in best or score>best[doc['name']][0]:best[doc['name']]=(float(score),doc)
                ranked=sorted(best.values(),key=lambda row:row[0],reverse=True)
                rank=next((i+1 for i,row in enumerate(ranked) if row[1]['name'] in case['expected']),None)
                value={'query':case['query'],'expected':case['expected'],'rank':rank,'ms':(time.perf_counter()-begin)*1000,'top5':[{'name':d['name'],'score':score,'location':d['location']} for score,d in ranked[:5]]}
                variant['cases'].append(value);print(f"RESULT {name}: rank={rank} {case['query']}",flush=True)
                if name=='Qwen3':qwen_candidates.append([d for _,d in ranked[:10]])
            variant['success']=True
        except Exception as error:variant.update(success=False,error=str(error));logging.exception('Model comparison failed')
        finally:engine.unload()
        report['variants'].append(variant)
        with (args.output/(name+'.json')).open('x',encoding='utf-8') as stream:json.dump(variant,stream,indent=2)
    if qwen_candidates and Path(args.reranker).is_dir():
        import openvino_genai as genai
        variant={'name':'Qwen3 + reranker','cases':[],'model_path':args.reranker}
        try:
            options=genai.TextRerankPipeline.Config();options.max_length=512;options.top_n=10;options.pad_to_max_length=True;options.padding_side='left'
            begin=time.perf_counter();print('LOAD existing Qwen reranker',flush=True)
            pipeline=genai.TextRerankPipeline(args.reranker,args.device,options,CACHE_DIR=str(args.output/'reranker_cache'))
            variant['model_setup_ms']=(time.perf_counter()-begin)*1000
            tokenizer=genai.Tokenizer(args.reranker)
            prefix='<|im_start|>system\nJudge whether the Document meets the requirements based on the Query and the Instruct provided. Note that the answer can only be "yes" or "no".<|im_end|>\n<|im_start|>user\n'
            suffix='<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n'
            variant['prompt_protocol']='Qwen author yes/no template; each complete prompt capped at 512 tokens, preserving suffix'
            for case,candidates in zip(cases,qwen_candidates):
                header=prefix+'<Instruct>: Given a description of a local file, retrieve relevant document passages.\n<Query>: '+case['query']+'\n<Document>: '
                texts=[]
                for document in candidates:
                    body=document['text']
                    while tokenizer.encode(header+body+suffix).input_ids.get_size()>512:
                        body=body[:int(len(body)*.9)]
                        if not body:raise ValueError('Query exceeds reranker token budget')
                    texts.append(body+suffix)
                begin=time.perf_counter();reranked=pipeline.rerank(header,texts)
                ranked=[{'name':candidates[int(index)]['name'],'score':float(score)} for index,score in reranked]
                rank=next((i+1 for i,row in enumerate(ranked) if row['name'] in case['expected']),None)
                variant['cases'].append({'query':case['query'],'expected':case['expected'],'rank':rank,'ms':(time.perf_counter()-begin)*1000,'top5':ranked[:5]})
                print(f"RERANK rank={rank} {case['query']}",flush=True)
            variant['success']=True
        except Exception as error:variant.update(success=False,error=str(error));logging.exception('Reranker comparison failed')
        report['variants'].append(variant)
    for variant in report['variants']:
        rows=variant['cases'];variant['top1']=sum(r['rank']==1 for r in rows);variant['top5']=sum(r['rank'] is not None and r['rank']<=5 for r in rows);variant['case_count']=len(rows)
    with (args.output/'summary.json').open('x',encoding='utf-8') as stream:json.dump(report,stream,indent=2)
    with (args.output/'ranks.csv').open('x',encoding='utf-8',newline='') as stream:
        writer=csv.writer(stream);writer.writerow(['variant','query','rank','ms'])
        for v in report['variants']:
            for r in v['cases']:writer.writerow([v['name'],r['query'],r['rank'],r['ms']])
    svg=['<svg xmlns="http://www.w3.org/2000/svg" width="900" height="260"><rect width="100%" height="100%" fill="white"/><g font-family="Segoe UI" fill="#535861"><text x="24" y="30" font-size="20">Local model comparison: sampled real-file retrieval</text><text x="24" y="52" font-size="12">Top-1 hits among the same PDF/YAML candidate pool. Higher is better; not whole-desktop recall.</text>']
    for i,v in enumerate(report['variants']):
        y=80+i*48;width=450*v['top1']/max(1,len(cases))
        svg.append(f'<text x="24" y="{y+19}">{html.escape(v["name"])}</text><rect x="240" y="{y}" width="{width}" height="28" rx="6" fill="#3482ff"/><text x="{255+width}" y="{y+19}">{v["top1"]}/{len(cases)}</text>')
    svg.append('</g></svg>')
    with (args.output/'quality.svg').open('x',encoding='utf-8') as stream:stream.write(''.join(svg))
    return 0 if all(v.get('success') for v in report['variants']) else 1


if __name__=='__main__':sys.exit(main())
