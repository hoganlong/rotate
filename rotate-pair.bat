@echo off
setlocal
cd /d "%~dp0"

if "%~1"=="" (
  echo Usage: rotate-pair.bat ^<basename^> [--upload]
  echo.
  echo Runs rotate180 twice for the same artwork basename:
  echo   1. s3://keithlong-art-photos/scans/^<basename^>.tif
  echo   2. s3://keithlong-art-photos/scans/jpg/^<basename^>.jpg
  echo.
  echo Pass --upload as the second argument to overwrite both originals in S3.
  echo Without --upload, both rotations are saved locally as rot180_*.
  echo.
  echo Examples:
  echo   rotate-pair.bat WATT_057
  echo   rotate-pair.bat WATT_057 --upload
  exit /b 1
)

set "BASENAME=%~1"
set "UPLOAD=%~2"

echo === Rotating TIF: scans/%BASENAME%.tif ===
dotnet run -- s3://keithlong-art-photos/scans/%BASENAME%.tif %UPLOAD%
if errorlevel 1 (
  echo.
  echo TIF rotation failed; skipping JPG.
  exit /b 1
)

echo.
echo === Rotating JPG: scans/jpg/%BASENAME%.jpg ===
dotnet run -- s3://keithlong-art-photos/scans/jpg/%BASENAME%.jpg %UPLOAD%
exit /b %errorlevel%
