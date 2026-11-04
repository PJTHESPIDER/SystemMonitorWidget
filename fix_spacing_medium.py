content = open(r'E:\Code\SystemMonitor\Services\WidgetTemplates.cs', 'r', encoding='utf-8').read()
marker = '"text": "Usage",\n                      "size": "Small",\n                      "isSubtle": true\n                    },\n                    {\n                      "type": "TextBlock",\n                      "text": "${gpu}",'
idx = content.find(marker)
if idx == -1:
    print('Usage marker not found')
else:
    region_start = max(0, idx - 500)
    region = content[region_start:idx]
    last_small = region.rfind('"spacing": "Small"')
    if last_small != -1:
        abs_pos = region_start + last_small
        content = content[:abs_pos] + '"spacing": "Medium"' + content[abs_pos + len('"spacing": "Small"'):]
        open(r'E:\Code\SystemMonitor\Services\WidgetTemplates.cs', 'w', encoding='utf-8').write(content)
        print('Replaced GPU spacing to Medium at position', abs_pos)
    else:
        print('No Small spacing found in region')
