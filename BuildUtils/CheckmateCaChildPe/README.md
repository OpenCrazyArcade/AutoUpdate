# CheckmateCaChildPe

Build step for `ClientStubs/HSUpdate`. Not shipped, runs only on the build machine.

`CA.exe` starts `hackshield\HSUpdate.exe` as a child process and, before it accepts the result, reads that process's
PE image (`HS_Updater_LaunchHSUpdate`, `sub_ABCBF0` and `sub_ABCF00`). A Native AOT exe fails both checks as linked:

| Check of CA.exe | Why an AOT exe fails | What this tool does |
|---|---|---|
| The import scan converts an RVA to a file offset as `rva - (VirtualAddress - PointerToRawData)` and loops forever when a section's delta is not 0 | `.data` is larger in memory than on disk, so every section after it is shifted | **Flatten**: lay the file out so each section starts at file offset == RVA (zero padding). RVAs, code and data are untouched |
| `DataDirectory[4].Size` (certificate table) must not be 0 | the exe is not signed | **Add a dummy certificate**: a 32-byte `WIN_CERTIFICATE` appended after the image. The signature is never verified |
| The image must not import `KERNEL32!VirtualProtect` | (it does not) | only **checked**, and reported if it ever does |

    CheckmateCaChildPe <exe> [<out>]     fix (in place when <out> is left out), then verify; the file is written only if the
                                         result passes
    CheckmateCaChildPe --check <exe>     verify only; exit code 2 when CA.exe would reject the image

`HSUpdate.csproj` runs it after `CopyNativeBinary` (the ILCompiler's own post-publish step: any earlier and its copy would
overwrite the fixed file), so `dotnet publish` of HSUpdate yields a finished exe and fails if it is not. The release
workflow checks the image once more before it publishes.

x86 / PE32 only. Tests: `CheckmateCaChildPe.Tests` (a synthetic PE shaped like a Native AOT image). The output was
compared byte for byte with the image the real game accepted (hackshield gate 1, 2026-11-07).
