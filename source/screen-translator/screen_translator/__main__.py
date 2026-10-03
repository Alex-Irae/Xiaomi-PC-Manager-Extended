"""Launch The Windows offline overlay. See README.md for provisioning.

Dependencies: root requirements.txt. Outputs: local settings and timing logs.
Command from the project root: python -m screen_translator
"""
from multiprocessing import freeze_support
import sys
from .bootstrap import prefer_system_runtime

if __name__ == "__main__":
    freeze_support()
    if sys.stdout is not None and hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="backslashreplace")
    prefer_system_runtime()
    from .desktop import main
    raise SystemExit(main())
