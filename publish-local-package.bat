cd /d %~dp0
dotnet pack .\source\HL\HL.csproj -c Release -o ..\_nupkgs
pause
