# Builds out\8bitdo-2c-battery-patch.exe for .NET Framework 4.8 (built into Windows 10/11: nothing to install to run it).
# Compiler: Roslyn, shipped with PowerShell 7. No Visual Studio or .NET SDK needed.
#   pwsh -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$out = Join-Path $root 'out'
New-Item -ItemType Directory -Force -Path $out | Out-Null

if ($PSVersionTable.PSVersion.Major -lt 7) { throw "Run this script with PowerShell 7 (pwsh), which provides the C# compiler." }
Add-Type -AssemblyName Microsoft.CodeAnalysis, Microsoft.CodeAnalysis.CSharp

$parse = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Latest)
# Relative paths only: no local path ends up in the exe.
[Microsoft.CodeAnalysis.SyntaxTree[]]$trees = Get-ChildItem $src -Filter *.cs | ForEach-Object {
    [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($_.FullName), $parse, "src\$($_.Name)", [Text.Encoding]::UTF8, [Threading.CancellationToken]::None)
}

$fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
[Microsoft.CodeAnalysis.MetadataReference[]]$refs = 'mscorlib', 'System', 'System.Core', 'System.Web.Extensions' |
    ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile("$fx\$_.dll") }

$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).
    WithPlatform([Microsoft.CodeAnalysis.Platform]::X64).
    WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).
    WithDeterministic($true)

$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('8bitdo-2c-battery-patch', $trees, $refs, $options)

$exe = Join-Path $out '8bitdo-2c-battery-patch.exe'
$pe = [IO.File]::Create($exe)
try { $result = $compilation.Emit($pe) } finally { $pe.Dispose() }

$result.Diagnostics | Where-Object { $_.Severity -ge [Microsoft.CodeAnalysis.DiagnosticSeverity]::Warning } | ForEach-Object { Write-Host $_.ToString() }
if (-not $result.Success) { Remove-Item $exe -EA SilentlyContinue; throw "Build failed" }
Write-Host "OK: out\8bitdo-2c-battery-patch.exe ($([math]::Round((Get-Item $exe).Length / 1KB)) KB)"
