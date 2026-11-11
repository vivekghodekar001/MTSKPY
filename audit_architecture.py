import pathlib
import xml.etree.ElementTree as ET

root=pathlib.Path(__file__).resolve().parent
expected={'Domain':set(),'Application':{'Domain'},'Infrastructure':{'Application'},'Presentation':{'Application','Infrastructure'}}
for layer,dependencies in expected.items():
    project=root/'src'/layer/f'Mskpy.{layer}.csproj'
    tree=ET.parse(project)
    references={pathlib.PurePosixPath(node.attrib['Include'].replace('\\','/')).parts[-2] for node in tree.iter('ProjectReference')}
    if references!=dependencies:
        raise SystemExit(f'{layer}: unexpected dependency edges {references}, expected {dependencies}')
    if layer in ('Domain','Application','Infrastructure') and tree.find('.//UseWPF') is not None:
        raise SystemExit(f'{layer} must not depend on WPF')
    print(f'PASS {layer}: '+(', '.join(sorted(references)) or 'no project dependencies'))
code=(root/'src/Presentation/MainWindow.xaml.cs').read_text(encoding='utf-8-sig')
for forbidden in ('HttpClient','LoginAsync','File.Read','JsonDocument','NavigateTo','SendAsync'):
    if forbidden in code: raise SystemExit('MainWindow code-behind contains business/transport work: '+forbidden)
print('PASS main-window code-behind delegates behavior to bound commands and services')
jsr_code=(root/'src/Presentation/Views/JsrWindow.xaml.cs').read_text(encoding='utf-8-sig')
for forbidden in ('HttpClient','File.Read','JsonDocument','SaveAsync','SendAsync'):
    if forbidden in jsr_code: raise SystemExit('JSR code-behind contains business/transport work: '+forbidden)
print('PASS JSR view code-behind contains only presentation/lifetime interactions')
print('NOTE legacy inspection editor is explicitly transitional and is not counted as fully MVVM.')
print('RESULT=success')
