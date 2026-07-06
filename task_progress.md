# PureAudio — Task Progress

## Bit Perfect Mode: WASAPI Exclusive + Device Capabilities

### ✅ Выполнено

1. **Создан `WasapiExclusivePlayer`** — кастомный `IWavePlayer` для WASAPI Exclusive
   - Использует `AudioClient` в режиме `AudioClientShareMode.Exclusive`
   - Подаёт исходный PCM напрямую в драйвер/ЦАП без преобразований
   - Поддерживает все форматы: 16/24/32 bit, 44.1/48/96/192 kHz
   - Обрабатывает ошибки инициализации (неподдерживаемые форматы)

2. **Создан `DeviceCapabilities`** — определение возможностей аудиоустройства
   - Определяет максимальные Sample Rate, Bit Depth, Channels
   - Метод `GetBitPerfectStatus()` — проверяет, является ли текущий формат Perfect или Limited
   - Логирование диагностики при инициализации

3. **Создан `BitPerfectWaveProvider`** — IWaveProvider для Bit Perfect режима
   - Для WAV/FLAC — читает исходные PCM данные напрямую (без конвертации)
   - Для MP3/AAC — использует AudioFileReader (эти форматы lossy)
   - Встроенный `PcmToFloatConverter` для FFT (преобразует PCM → float для спектроанализатора)
   - Поддержка позиционирования (CurrentTime, TotalTime, Seek)

4. **Обновлён `AudioService`** — поддержка двух режимов воспроизведения
   - Bit Perfect (Exclusive) — через `WasapiExclusivePlayer` + `BitPerfectWaveProvider`
   - Normal (Shared) — через стандартный `WasapiOut` + `AudioFileReader`
   - Плавное переключение между режимами с сохранением позиции
   - Fallback на Shared при ошибке Exclusive
   - Событие `BitPerfectStatusChanged` для UI

5. **Обновлён `MainViewModel`** — UI для Bit Perfect режима
   - Кнопка "Bit Perfect" с золотым/серым индикатором
   - Отображение Bit Depth / Sample Rate текущего трека
   - Индикатор статуса: Perfect (зелёный) / Limited (жёлтый) / Off (серый)
   - Отключение ползунка громкости в Bit Perfect режиме
   - Индикатор возможностей устройства (макс. битность/частота)
   - Предупреждение при первом включении Bit Perfect

6. **Обновлён `MainWindow.xaml`** — визуальные элементы
   - Индикатор Bit Perfect статуса (Perfect/Limited/Off)
   - Индикатор возможностей устройства (например "32 bit / 192 kHz")
   - Все индикаторы в едином стиле с золотым акцентом

### ✅ Замена r8brain на WDL Resampler (NAudio built-in)

1. **Проблема:** `r8bsrc.dll` из libs/ несовместима — другие имена экспортируемых функций, другой ABI.
   - Файлы в libs/ (`r8bsrc.dll`, `r8bsrc.lib`) — **НЕ от r8brain**. Это DLL от другого проекта.
   - В репозитории r8brain нет предкомпилированных DLL в релизах.
   - Исходники r8brain есть в `r8brain_repo/`, но сборка DLL требует Visual Studio C++.

2. **Решение:** Создан `SoxResampler.cs` — использует **NAudio `WdlResamplingSampleProvider`** (встроенный ресемплер из REAPER).
   - Не требует внешних DLL
   - Высокое качество (WDL — тот же движок, что в REAPER)
   - Работает через PCM → float → resample → PCM

3. **Исправления в `SoxResampler.cs`:**
   - Исправлена критическая ошибка: `WdlResamplingSampleProvider.Read()` ожидает количество **сэмплов** (float), а не фреймов. Раньше передавалось `outputFramesNeeded` вместо `floatSamplesNeeded = outputFramesNeeded * channels`, что для стерео давало в 2 раза меньше данных.
   - Добавлен внутренний `PcmToSampleProvider` для конвертации PCM → float.

4. **Исправления в `AudioService.cs`:**
   - `CurrentSampleRate` и `CurrentBitDepth` теперь всегда возвращают **исходный** формат трека (из `_bitPerfectProvider.WaveFormat`), а не выходной формат ресемплера. UI показывает правильный формат (24/96, а не 16/48).
   - `UpdateBitPerfectStatus()` проверяет **source** формат, а не output.
   - `CurrentPosition` больше не умножает на ratio (1 секунда = 1 секунда независимо от ресемплинга).

5. **Сборка проекта:** 0 ошибок ✅

### 🔄 В процессе / Планируется

- [ ] **Тестирование на реальном ЦАПе** — проверить Exclusive режим с ресемплингом
- [ ] **Спектр замирает при ресемплинге** — FFT может не получать данные, если `WdlResamplingSampleProvider` буферизует внутри себя и не вызывает `BitPerfectWaveProvider.Read()` достаточно часто
- [ ] Оптимизация FFT для разных форматов
- [ ] Поддержка DSD (возможно, в будущем)
- [ ] Отображение формата в плейлисте (битность/частота для каждого трека)

### 📝 Архитектурные решения

- **Почему IWaveProvider, а не ISampleProvider для Bit Perfect?**
  - WASAPI Exclusive требует raw PCM данные в исходном формате
  - ISampleProvider конвертирует всё в IEEE float (32 бита), что нарушает Bit Perfect
  - IWaveProvider передаёт байты как есть, без изменений

- **Почему отдельный `WasapiExclusivePlayer`, а не `WasapiOut` с `AudioClientShareMode.Exclusive`?**
  - NAudio `WasapiOut` в Exclusive режиме имеет ограничения
  - Кастомный `WasapiExclusivePlayer` даёт полный контроль над форматом вывода
  - Позволяет точно сопоставлять формат источника с поддерживаемым форматом устройства

- **Как работает определение Bit Perfect статуса?**
  - `DeviceCapabilities` при инициализации проверяет все поддерживаемые форматы
  - `GetBitPerfectStatus(sr, bd, ch)` сравнивает формат трека с возможностями устройства
  - `Perfect` — точное совпадение с поддерживаемым форматом
  - `Limited` — формат не поддерживается напрямую (будет использован ближайший поддерживаемый)
