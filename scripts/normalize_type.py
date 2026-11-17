"""Bring every font size, line height and weight in the Windows interface onto one type scale.

Scale (size / line height):  12/18 caption · 13/20 small · 14/22 body · 16/24 heading ·
                              20/28 title · 24/32 figure · 28/36 display
Weights (MiSans Regular / Medium / Semibold): 600 only for titles and big figures (16 px and up, plus the
wordmark); every other emphasis (buttons, list names, bold words, current tab) is 500; body text 400.
Run again after edits: it is idempotent and prints what it changed.
"""
import re, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SIZE = {11: 12, 11.5: 12, 12: 12, 12.5: 12, 13: 13, 14: 14, 15: 14, 16: 16, 17: 16, 18: 16,
        20: 20, 21: 20, 22: 20, 24: 24, 26: 24, 28: 28, 30: 28, 32: 28}
LINE = {12: 18, 13: 20, 14: 22, 16: 24, 20: 28, 24: 32, 28: 36}

changes = {'size': 0, 'line': 0, 'weight': 0}


def fix_block(block):
    """One CSS declaration block or inline style: map size, pair line height, cap small weights."""
    m = re.search(r'font-size:\s*([0-9.]+)px', block)
    if not m:
        return block
    old = float(m.group(1)); old = int(old) if old.is_integer() else old
    new = SIZE.get(old)
    if new is None:
        return block
    if new != old:
        changes['size'] += 1
    block = block[:m.start()] + 'font-size: %dpx' % new + block[m.end():]
    def line(mm):
        if int(float(mm.group(1))) != LINE[new]:
            changes['line'] += 1
        return 'line-height: %dpx' % LINE[new]
    block = re.sub(r'line-height:\s*([0-9.]+)px', line, block)
    if new < 14:
        def weight(mm):
            changes['weight'] += 1
            return 'font-weight: 500'
        block = re.sub(r'font-weight:\s*(600|700|bold)', weight, block)
    return block


KEEP_SEMIBOLD = ('.p-wordmark',)


def fix_weight(selector, block):
    """Semibold is kept for 16 px and larger (titles, figures); everything else emphasised becomes medium."""
    if '@font-face' in selector or not re.search(r'font-weight:\s*(600|700|bold)\b', block):
        return block
    m = re.search(r'font-size:\s*([0-9.]+)px', block)
    if (m and float(m.group(1)) >= 16) or selector.strip().endswith(KEEP_SEMIBOLD):
        return re.sub(r'font-weight:\s*(700|bold)\b', 'font-weight: 600', block)
    changes['weight'] += 1
    return re.sub(r'font-weight:\s*(600|700|bold)\b', 'font-weight: 500', block)


def css(path):
    text = path.read_text(encoding='utf-8')
    out = re.sub(r'\{[^{}]*\}', lambda m: fix_block(m.group(0)), text)
    out = re.sub(r'([^{}]*)(\{[^{}]*\})', lambda m: m.group(1) + fix_weight(m.group(1), m.group(2)), out)
    path.write_text(out, encoding='utf-8')


def js(path):
    text = path.read_text(encoding='utf-8')
    # Inline styles inside the interface's HTML strings: style="..."
    out = re.sub(r'style="[^"]*"', lambda m: fix_block(m.group(0)), text)
    path.write_text(out, encoding='utf-8')


css(ROOT / 'client-assets/win/assets/ui.css')
css(ROOT / 'client-assets/assets/app.css')
js(ROOT / 'client-assets/win/assets/ui.js')
print('font sizes changed %(size)d, line heights changed %(line)d, emphasis made medium %(weight)d' % changes)
