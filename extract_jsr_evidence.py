import json
import pathlib
import re

project = pathlib.Path(__file__).resolve().parent
source = project.parents[1] / 'work/exact-app/recovered.js'
text = source.read_text(encoding='utf-8')
lines = text.splitlines()
keys = ['irrSource', 'guardAroundWell', 'irrSourceWidth', 'irrSourceDepth', 'requiredPumpHead', 'landUnderShadow', 'shadowFreeSpaceAvailable', 'mobileNetAvailable']
labels = {key: re.findall("'" + key + "': '([^']*)'", text)[:3] for key in keys}
role_assignments = [f'{i + 1}: {lines[i]}' for i in range(527676, 530342) if lines[i].startswith('                r14 =')]
evidence = {
    'source': str(source),
    'strength': 'observed source evidence; runtime parity unverified',
    'initial_values_line': 528722,
    'validation_line_range': [528725, 529010],
    'ui_options_line_range': [529990, 530390],
    'label_matches': labels,
    'role_flag_assignments': role_assignments,
    'confirmed_correction': 'Network-provider selection is required only when mobileNetwork is Yes.',
}
out = project / 'docs/evidence'
out.mkdir(parents=True, exist_ok=True)
(out / 'jsr-source.json').write_text(json.dumps(evidence, indent=2), encoding='utf-8')
print(json.dumps({'labels': labels, 'role_assignments': role_assignments}, indent=2))
print('RESULT=success')
