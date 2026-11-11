import concurrent.futures
import json
import pathlib
import subprocess

project=pathlib.Path(__file__).resolve().parent
workspace=project.parents[1]
dotnet=workspace/'work/tools/dotnet10/dotnet.exe'
artifacts=project/'artifacts'
artifacts.mkdir(exist_ok=True)

def run(name):
    args=[str(dotnet),'run','--project',f'tests/{name}/{name}.csproj','-c','Release','--no-build']
    if name=='UiRegression': args+=['--',str(workspace/'work/qa-net10')]
    process=subprocess.run(args,cwd=project,text=True,capture_output=True,timeout=90)
    output=process.stdout+process.stderr
    (artifacts/(name+'.log')).write_text(output,encoding='utf-8')
    return {'runner':name,'exit_code':process.returncode,'passed_checks':sum(line.startswith('PASS ') for line in output.splitlines()),'log':name+'.log'}

with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    results=list(pool.map(run,['Compatibility','Transport','ViewModels','UiRegression','Jsr']))
summary={'target':'.NET 10','scope':'Synthetic/injected services and in-process WPF only; no production requests','results':results,'total_passed_checks':sum(r['passed_checks'] for r in results)}
(artifacts/'test-results.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
print(json.dumps(summary,indent=2))
if any(r['exit_code'] for r in results): raise SystemExit(1)
print('RESULT=success')
