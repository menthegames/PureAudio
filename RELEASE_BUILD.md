# PureAudio — Release Build Instructions

## 1. Single-file self-contained сборка

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

**Готовый exe:** `bin\Release\net10.0-windows\win-x64\publish\PureAudio.exe`

Это **единый файл** (~166 МБ), который содержит всё необходимое для запуска на любой Windows x64 без установленного .NET Runtime.

В папке publish также будет:
- `PureAudio.pdb` — отладочные символы (необязателен для работы)
- `music_icon.ico` — иконка

## 2. Если приложение не запускается после перезагрузки

### Известная проблема
После перезагрузки компьютера приложение может виснуть в диспетчере задач, но окно не появляется. Это связано с тем, что на старте происходит инициализация WASAPI Exclusive mode, и если аудиоустройство недоступно или занято, возникает исключение.

### Диагностика
1. Запустите приложение из командной строки, чтобы увидеть ошибки:
   ```bash
   bin\Release\net10.0-windows\win-x64\publish\PureAudio.exe
   ```
2. Проверьте файл `error.log` в папке с приложением (если он появился).
3. Проверьте файл `output.log` — там может быть информация о последнем состоянии.

### Решение
Если приложение не запускается:
1. Удалите файл настроек: `%APPDATA%\PureAudio\settings.json`
2. Запустите приложение снова — оно должно стартовать с настройками по умолчанию (WASAPI Shared mode).
3. Если заработало — проблема была в WASAPI Exclusive mode при старте.

### Альтернативное решение
Запустите приложение с отключённым WASAPI Exclusive:
1. Откройте `%APPDATA%\PureAudio\settings.json`
2. Установите `"WasapiExclusive": false`
3. Сохраните файл и запустите приложение

## 3. Полная очистка и пересборка

```bash
dotnet clean -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 4. Требования

- Windows x64
- WASAPI-совместимая звуковая карта
- Для WASAPI Exclusive: поддержка Exclusive mode драйвером устройства
- .NET Runtime **не требуется** (self-contained)
