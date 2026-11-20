#!/usr/bin/env bash
# Static accessibility audit: every interactive/AT-visible control in the
# Avalonia views must carry an explicit automation name (or be marked Raw when
# it only duplicates adjacent text). This does not replace a screen-reader run.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
python3 - "$repo_root" <<'PY'
import re
import sys
from pathlib import Path

root = Path(sys.argv[1])
views = sorted((root / 'src/Flow.Desktop/Views').glob('*.axaml'))
tags = r'(?:Button|ComboBox|TextBox|DatePicker|ListBox|ItemsControl|TimeSeriesChart|SplitBar|UsageBar)'
pattern = re.compile(r'<([\w]+:)?(' + tags + r')\b(?=[\s/>])([^>]*?)/?>', re.S)

gaps = []
total = 0
for path in views:
    text = path.read_text()
    for match in pattern.finditer(text):
        attrs = match.group(3)
        prefix = match.group(1) or ''
        tag = prefix + match.group(2)
        total += 1
        is_decorative = 'AccessibilityView="Raw"' in attrs
        is_named = 'AutomationProperties.Name=' in attrs
        if not is_named and not is_decorative:
            line = text[:match.start()].count('\n') + 1
            gaps.append(f'{path.relative_to(root)}:{line} <{tag}> has no automation name')

print(f'Checked {total} interactive/AT-visible controls across {len(views)} views.')
if gaps:
    print('Gaps:', file=sys.stderr)
    for gap in gaps:
        print('  ' + gap, file=sys.stderr)
    sys.exit(1)
print('All interactive/AT-visible controls have names or are explicitly Raw.')
PY
