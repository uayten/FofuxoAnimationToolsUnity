# Source import regression checks

Run isolated Editor checks for external source capture, metadata persistence,
renamed assets, missing files, and animation addition and replacement. Fixtures are
created in a unique Assets folder and a temporary external folder, then removed.

## Contents

- [Run with Ivan MCP](#run-with-ivan-mcp)
- [Manual drag check](#manual-drag-check)

## Run with Ivan MCP

Open the host project in Unity and wait for compilation. Submit
`SourceImportRegression.cs` as `csharpCode` to `script-execute`, with
`className: SourceImportRegression` and `methodName: Main`.

For a host project with the local Ivan CLI installed, run this PowerShell from its
root directory:

```powershell
npx --no-install unity-mcp-cli status .
npx --no-install unity-mcp-cli wait-for-ready .
$sourceTestCode = Get-Content -Raw 'Packages/com.uayten.fofuxoanimationtools/Tests~/SourceImportRegression.cs'
$sourceTestInput = Join-Path (Get-Location) 'Library/FofuxoSourceRegression.json'
@{
    csharpCode = $sourceTestCode
    className = 'SourceImportRegression'
    methodName = 'Main'
} | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $sourceTestInput
npx --no-install unity-mcp-cli run-tool script-execute . --input-file $sourceTestInput --raw --timeout 120000
```

Animation checks require UnityGLTF; the runner reports a skip if it is absent.
The test source lives in `Tests~` so Unity does not include it in project assemblies.

## Manual drag check

Drag an external file and a folder with nested files into Project. Check their
Inspector source paths, including a file from OneDrive when available. Edit an
external source, use **Update From Source**, and confirm the imported content
changes. Move the external source away and confirm **Locate File** appears.

The automated suite invokes the registered drop callback and reproduces Unity's
file-copy/import sequence; this manual check also exercises operating-system drag
events and the native overwrite dialog.
