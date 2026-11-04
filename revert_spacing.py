content = open(r'E:\Code\SystemMonitor\Services\WidgetTemplates.cs', 'r', encoding='utf-8').read()
marker = '"text": "Usage",\n                      "size": "Small",\n                      "isSubtle": true\n                    },\n                    {\n                      "type": "TextBlock",\n                      "text": "${gpu}",'
idx = content.find(marker)
if idx == -1:
    print('Usage marker not found')
else:
    region_start = max(0, idx - 500)
    region = content[region_start:idx]
    last_medium = region.rfind('"spacing": "Medium"')
    if last_medium != -1:
        abs_pos = region_start + last_medium
        content = content[:abs_pos] + '"spacing": "None"' + content[abs_pos + len('"spacing": "Medium"'):]
        open(r'E:\Code\SystemMonitor\Services\WidgetTemplates.cs', 'w', encoding='utf-8').write(content)
        print('Reverted GPU spacing to None at position', abs_pos)
    else:
        print('No Medium spacing found in region')
