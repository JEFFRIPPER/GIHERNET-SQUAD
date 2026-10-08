# Gnirehtet Squad

Windows-программа для [gnirehtet](https://github.com/Genymobile/gnirehtet) 2.5.1: раздаёт интернет компьютера на Android-телефон по USB (reverse tethering), root не нужен.

C# WPF, один `GnirehtetSquad.exe` (self-contained, .NET ставить не нужно). Дизайн в стиле Material Design 3: почти чёрный фон, насыщенный кроваво-алый со свечением, тёмный заголовок окна. `gnirehtet.exe` и `gnirehtet.apk` вшиты в exe.

## Быстрый старт

1. Скачайте `GnirehtetSquad-*-win64.zip` из [Releases](../../releases) и распакуйте.
2. На телефоне включите «Параметры разработчика → Отладка по USB», подключите кабель.
3. Запустите `GnirehtetSquad.exe`.
4. Если adb не найден, нажмите «Скачать platform-tools» (официальный архив Google).
5. Нажмите красную кнопку питания и подтвердите запрос VPN на телефоне.

## Возможности

- Старт, стоп и перезапуск relay; режимы «Все устройства» (`autorun`) и «Одно устройство» (`run <serial>`).
- Список устройств с моделью и статусом; VPN вкл/выкл, установка, переустановка и удаление APK на каждом телефоне.
- Подключение по Wi-Fi (`adb connect`), перезапуск adb-сервера, скачивание platform-tools.
- Журнал с фильтрами и поиском; время работы, число клиентов, автоперезапуск relay при падении.
- DNS-пресеты (Cloudflare, Google, Quad9, AdGuard, Яндекс), маршруты, порт, путь к adb.
- Выключение VPN на телефонах при остановке.
- Трей, работа в фоне, автозагрузка с Windows (`--background`), один экземпляр.
- **OTA-обновления** через GitHub Releases: проверка при запуске и каждые 6 часов, установка в один клик или автоматически, проверка SHA-256, замена файлов с откатом и перезапуск с возвратом relay в работу.

## Как устроено

```
src/GnirehtetSquad/
  App.xaml(.cs)          запуск, один экземпляр, трей, аргументы командной строки
  Crash.cs               crash.log и сообщение об ошибке запуска
  MainWindow.xaml(.cs)   интерфейс: главная, устройства, журнал, настройки
  Core/Engine.cs         relay gnirehtet, adb, журнал, настройки, platform-tools
  Core/Updater.cs        OTA: releases/latest → zip → SHA-256 → замена → перезапуск
  Core/Models.cs         модели и настройки
bin/                     оригинальные gnirehtet.exe / gnirehtet.apk 2.5.1 (вшиваются в exe)
scripts/publish.ps1      сборка exe и zip для релиза
docs/README.txt          README внутри архива
```

Вшитые `gnirehtet.exe` и `gnirehtet.apk` при запуске распаковываются в `%LOCALAPPDATA%\GnirehtetSquad\bin`. Настройки: `%APPDATA%\GnirehtetSquad\settings.json`, ошибки запуска: `%APPDATA%\GnirehtetSquad\crash.log`.

Запуск: окно и значок в трее появляются сразу, распаковка gnirehtet, поиск adb и автозапуск relay идут в фоне. Повторный запуск просит работающую копию показать окно; если она не отвечает 7 секунд (зависла), она завершается и программа запускается заново.

### OTA

1. Запрос `api.github.com/repos/JEFFRIPPER/GIHERNET-SQUAD/releases/latest`, сравнение тега с версией программы.
2. Скачивание `GnirehtetSquad-X.Y.Z-win64.zip`, сверка SHA-256 с `digest` ассета.
3. Остановка relay, текущие файлы переименовываются в `*.old`, новые кладутся на их место; при ошибке откат.
4. Запуск новой версии с `--after-update [--start-relay] [--background]`, `*.old` удаляются.

Формат zip (`GnirehtetSquad/GnirehtetSquad.exe` на верхнем уровне) совместим с апдейтером версии 1.1.0, поэтому старые установки обновляются сами.

## Сборка

Нужен .NET 8 SDK.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
```

Результат: `dist\GnirehtetSquad\GnirehtetSquad.exe`, архив `dist\GnirehtetSquad-<версия>-win64.zip` и копия exe в корне проекта.

## Релизы

GitHub Actions (`.github/workflows/build.yml`, windows-latest) собирает exe на каждый push. Чтобы выпустить релиз, поднимите `<Version>` в `src/GnirehtetSquad/GnirehtetSquad.csproj` и запушьте в `main`: workflow сам создаст тег `vX.Y.Z` и релиз с zip.

## Лицензии

gnirehtet © Genymobile, Apache License 2.0 (см. `bin/NOTICE.md`).
