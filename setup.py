"""Setuptools entry: registers the deterministic slave-assets build hook.

Metadata lives in pyproject.toml; this module only wires the custom
``build_py`` command that generates ``titella_slave_assets/_data`` from the
implementations and loaders of this repository.
"""
import sys
from pathlib import Path

from setuptools import setup

sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_support import SlaveAssetsBuildPy  # noqa: E402

setup(cmdclass={"build_py": SlaveAssetsBuildPy})
