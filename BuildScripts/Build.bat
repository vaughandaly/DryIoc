@echo off
setlocal EnableDelayedExpansion

set SLN="..\DryIoc.sln"
set OUTDIR="..\bin\Release"
set NUGET="..\.nuget\NuGet.exe"

set NOPAUSE=%1
set MSBUILDVER=%2
if "%MSBUILDVER%"=="" set MSBUILDVER=14

echo:
echo:Restoring packages for solution %SLN% . . .
%NUGET% restore %SLN%

echo:
echo:Building %SLN% into %OUTDIR% . . .

for /f "tokens=2*" %%S in ('reg query HKLM\SOFTWARE\Wow6432Node\Microsoft\MSBuild\ToolsVersions\%MSBUILDVER%.0 /v MSBuildToolsPath') do (

	if exist "%%T" (

		echo:
		echo:Using MSBuild from path "%%T\MSBuild.exe"

		"%%T\MSBuild.exe" %SLN% /t:Rebuild /v:minimal /m /fl /flp:LogFile=MSBuild.log ^
   			/p:OutDir=%OUTDIR% ^
   			/p:GenerateProjectSpecificOutputFolder=false ^
   			/p:Configuration=Release ^
   			/p:RestorePackages=false 

		find /C "Build succeeded." MSBuild.log
    )
)

endlocal
if not "%NOPAUSE%"=="-nopause" pause