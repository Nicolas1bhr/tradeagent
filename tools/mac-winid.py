#!/usr/bin/env python3
"""Print "<windowid> <x>,<y>,<w>,<h>" for the TradeAgent window, or exit 1.

Quartz rather than AppleScript on purpose: System Events cannot see Avalonia's windows at all, so
the obvious `position of window 1` route returns nothing and looks like the app is not running.
"""
import sys
import Quartz

for w in Quartz.CGWindowListCopyWindowInfo(
        Quartz.kCGWindowListOptionOnScreenOnly, Quartz.kCGNullWindowID):
    if 'TradeAgent' in str(w.get('kCGWindowOwnerName', '')):
        b = w.get('kCGWindowBounds')
        if b and b['Width'] > 300:
            print(f"{int(w.get('kCGWindowNumber'))} "
                  f"{int(b['X'])},{int(b['Y'])},{int(b['Width'])},{int(b['Height'])}")
            sys.exit(0)
sys.exit(1)
