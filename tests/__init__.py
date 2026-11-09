"""Tests of the Titella slave implementations.

The canonical protocol codec lives in ``src/titella_slave_assets``; this
package makes it importable from a checkout so the suite also runs before the
assets wheel is installed.
"""
import sys
from pathlib import Path

_SRC = str(Path(__file__).resolve().parents[1] / "src")
if _SRC not in sys.path:
    sys.path.insert(0, _SRC)
