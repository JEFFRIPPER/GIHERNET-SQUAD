@echo off
rem Сборка на Windows (нужен Go 1.22+): build.cmd
setlocal
if not exist dist\GnirehtetSquad mkdir dist\GnirehtetSquad
pushd app
go build -trimpath -ldflags "-s -w -H=windowsgui" -o ..\dist\GnirehtetSquad\GnirehtetSquad.exe . || (popd & exit /b 1)
popd
copy /y bin\gnirehtet.exe dist\GnirehtetSquad\ >nul
copy /y bin\gnirehtet.apk dist\GnirehtetSquad\ >nul
copy /y bin\gnirehtet-run.cmd dist\GnirehtetSquad\ >nul
copy /y docs\README.txt dist\GnirehtetSquad\README.txt >nul
echo Готово: dist\GnirehtetSquad
