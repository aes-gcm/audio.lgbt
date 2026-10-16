# Нативный клиент audio.lgbt для Windows

Рабочий проект: `AudioLgbt.Native`. Браузер и локальный HTTP-сервер не используются. C# отвечает за интерфейс, устройства и медиа; `audio-engine.exe` — дочерний Go/Pion-процесс связи. Захват монитора использует DXGI Desktop Duplication, отдельных окон — Windows Graphics Capture. Звук демонстрации передаётся отдельным источником `screen-audio`.

## Сборка

На Windows x64 установите .NET SDK 10, Go 1.26, Visual Studio 2022/2026 Build Tools с C++ и Windows SDK, NSIS 3. В PowerShell из корня проекта:

```powershell
./scripts/build-native.ps1 -Architecture x64
./scripts/build-native-installer.ps1 -SkipBuild -Nsis 'C:/Program Files (x86)/NSIS/makensis.exe'
```

Первая команда скачивает FFmpeg LGPL shared в `.cache`, проверяет SHA256, собирает C#, движок Go и C++ захват. Если upstream изменил пакет, проверка останавливает сборку: используйте архив соответствующего dependency-release, а не отключайте проверку. Результат сборки — `dist/native-x64`; единый установщик — `dist/audio.lgbt-Native-Preview-Setup.exe`.

Зависимости закреплены в NuGet lock-файле, `go.mod`/`go.sum` и `DEPENDENCIES.txt`. DLL FFmpeg загружаются динамически и могут быть заменены совместимой сборкой. Лицензии необходимо сохранять при распространении. ARM64 можно кросс-собрать, но полноценная проверка на Windows ARM64 пока не заявляется; текущий установщик предназначен для x64.

## Свой сервер

На стартовом экране выберите «Сервер», укажите корневой HTTPS URL и сохраните. Для ограничения создания комнат включите локальные аккаунты на сервере: [инструкция](../docs/SELF-HOSTING.md). История фильтруется по серверу, сессии не переносятся между адресами. Имя и сервер сохраняются в `%LOCALAPPDATA%/audio.lgbt/preferences.json`; история приглашений защищена DPAPI CurrentUser. Пароль не записывается.

Обновления официального клиента приходят только с audio.lgbt. Для независимого форка отключите их при сборке: `dotnet publish ... -p:OfficialUpdates=false`. Обычная смена сервера звонков не меняет источник обновлений.

## Проверка

```powershell
dotnet run --project desktop/tests -c Release
dotnet run --project test/windows -c Release -- ui-checks
dotnet run --project test/windows -c Release -- update-checks
```

Другие режимы `test/windows`: `render-ui`, `monitor-check`, `output-checks`. Проверки устройства требуют настоящих устройств; синтетические кадры не подтверждают качество реальной камеры. Для interop используйте `test/e2e/native-client.cjs` с `BASE_URL` и `NATIVE_ENGINE`. Перед выпуском проверяйте установку/обновление/удаление, браузерный звонок, TLS, захват и выход из приложения на Windows.
