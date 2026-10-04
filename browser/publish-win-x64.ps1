$ErrorActionPreference = 'Stop'
dotnet restore
dotnet publish -c Release -r win-x64 --self-contained false
