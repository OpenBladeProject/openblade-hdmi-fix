# Build and verify

Use Windows with the .NET 10 SDK. Work in a dedicated worktree.

```powershell
dotnet restore OpenBlade.HdmiFix.slnx -p:NuGetAudit=false
dotnet build OpenBlade.HdmiFix.slnx -c Release -p:NuGetAudit=false --no-restore
dotnet test OpenBlade.HdmiFix.slnx -c Release -p:NuGetAudit=false --no-build --no-restore
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\Publish.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\Publish.ps1
```

Publish creates a self-contained Windows x64 folder, a ZIP, and its SHA-256 file under `artifacts`. Keep the complete extracted payload together. Users launch the executable; they do not need a terminal or a separate .NET installation.

Publish requires a clean Git worktree, including no untracked source files. Ignored build output does not block it. The script checks the worktree and commit again after the build, before it records provenance and creates the ZIP.

The payload includes the project license, runtime license notices, and `build.json` with the unchanged source commit and runtime package versions.

The internal `--preview <absolute.png> [width] [--expanded]` argument renders the real WPF window with synthetic monitor data. Patch and Restore are disabled in preview. It does not read or change monitor registry entries. Use 720 and 560 widths and the expanded view to inspect layout; this does not validate physical behavior.

The internal administrator entry point is an implementation detail of the GUI. No command-line patch interface is provided.
