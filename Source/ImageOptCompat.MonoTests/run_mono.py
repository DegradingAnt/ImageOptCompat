"""Execute the probe in an isolated process using RimWorld's embedded Mono, without starting Unity.

Usage: python run_mono.py GAME_DIRECTORY PROBE_EXE [--legacy]
The stand-ins cannot test native Unity rendering/audio. Real Mono and Harmony detours ARE tested.
"""
import ctypes
import os
from pathlib import Path
import sys

game = Path(sys.argv[1]).resolve()
probe = Path(sys.argv[2]).resolve()
runtime = game / "MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll"
managed = game / "RimWorldWin64_Data/Managed"
if not runtime.is_file() or not (managed / "mscorlib.dll").is_file() or not probe.is_file():
    raise SystemExit("Expected the installed Windows RimWorld runtime and a built probe executable.")
mono = ctypes.CDLL(str(runtime))


def api(name, result, *parameters):
    function = getattr(mono, name)
    function.restype = result
    function.argtypes = parameters
    return function


api("mono_set_assemblies_path", None, ctypes.c_char_p)(str(managed).encode())
domain = api("mono_jit_init_version", ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p)(
    b"ImageOptCompat regression", b"v4.0.30319")
if not domain:
    raise SystemExit("Mono initialization failed")
assembly = api("mono_domain_assembly_open", ctypes.c_void_p, ctypes.c_void_p, ctypes.c_char_p)(
    domain, str(probe).encode())
if not assembly:
    raise SystemExit("Mono could not load probe assembly")
arguments = [str(probe).encode()] + [arg.encode() for arg in sys.argv[3:]]
argv = (ctypes.c_char_p * len(arguments))(*arguments)
exit_code = api("mono_jit_exec", ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p,
                ctypes.c_int, ctypes.POINTER(ctypes.c_char_p))(domain, assembly, len(arguments), argv)
sys.stdout.flush()
sys.stderr.flush()
# End the isolated embedding process; do not run CPython teardown over Mono's process-global state.
os._exit(exit_code)
