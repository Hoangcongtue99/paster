@echo off
rem Self-contained single-file build: copy dist\Paster.exe anywhere, with
rem snippets.txt beside it, and it runs without installing .NET.
dotnet publish -c Release ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o dist
