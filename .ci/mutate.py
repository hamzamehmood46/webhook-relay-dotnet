import json, subprocess, sys, shutil, re
cfg = json.load(open('.ci/mutations.json', encoding='utf-8-sig'))
tests = sys.argv[1]
print("MUTATION RUN")
for m in cfg:
    p = m['file']
    original = open(p, encoding='utf-8').read()
    if m['old'] not in original:
        print(f"{m['label']} : ANCHOR MISSING")
        continue
    open(p, 'w', encoding='utf-8').write(original.replace(m['old'], m['new'], 1))
    r = subprocess.run(['dotnet', 'test', tests, '-c', 'Release', '--verbosity', 'quiet', '--filter', 'FullyQualifiedName!~ApiTests'], capture_output=True, text=True)
    out = r.stdout + r.stderr
    build_error = re.search(r'error CS\d+', out)
    failed = re.search(r'Failed:\s+(\d+)', out)
    passed = re.search(r'Passed:\s+(\d+)', out)
    if build_error:
        status = 'BUILD ERROR'
    elif failed and int(failed.group(1)) > 0:
        status = f'caught ({failed.group(1)} tests failed)'
    elif passed:
        status = 'SURVIVED'
    else:
        status = 'unclear'
    print(f"{m['label']} : {status}")
    open(p, 'w', encoding='utf-8').write(original)
print("MUTATION RUN DONE")
