#!/bin/sh
#
# Builds SixDofDirect.dll for 64-bit and 32-bit Windows players into Assets/Plugins.
# Needs llvm-mingw (https://github.com/mstorsjo/llvm-mingw):
#
#   LLVM_MINGW=/path/to/llvm-mingw ./build.sh

set -eu

here=$(cd "$(dirname "$0")" && pwd)
plugins="$here/../../Assets/Plugins"
mkdir -p "$plugins/x86_64" "$plugins/x86"

: "${LLVM_MINGW:?set LLVM_MINGW}"

flags="-O2 -Wall -Wextra -shared -static-libgcc"

# shellcheck disable=SC2086
"$LLVM_MINGW/bin/x86_64-w64-mingw32-clang" $flags -o "$plugins/x86_64/SixDofDirect.dll" "$here/sixdof_direct.c"
# shellcheck disable=SC2086
"$LLVM_MINGW/bin/i686-w64-mingw32-clang" $flags -o "$plugins/x86/SixDofDirect.dll" "$here/sixdof_direct.c"

echo "  -> $plugins/x86_64/SixDofDirect.dll"
echo "  -> $plugins/x86/SixDofDirect.dll"
