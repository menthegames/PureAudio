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

### 🔄 В процессе / Планируется

- [ ] Тестирование на реальном ЦАПе
- [ ] Оптимизация FFT для разных форматов
- [ ] Поддержка DSD (возможно, в будущем)
- [ ] Отображение формата в плейлисте (битность/частота для каждого трека)

### ✅ Исправления R8brainResampler (07.07.2026)

1. **Исправлена критическая ошибка в `PcmToDouble()` — ГЛАВНАЯ ПРИЧИНА ХРИПЛОГО ЗВУКА**:
   - **Проблема:** Параметр назывался `sampleCount`, но в него передавалось количество **фреймов** (`framesRead`). Внутри функции цикл шёл до `sampleCount * channels`, что для стерео (2 канала) давало `framesRead * 2 * 2 = framesRead * 4` — выход за границы буфера и чтение мусорных данных.
   - **Исправление:** Параметр переименован в `frameCount`, цикл идёт до `frameCount * channels` (один раз), что соответствует правильному количеству сэмплов.

2. **Исправлена логика `ProcessAccumulated()` — потеря данных при `r8b_process` возвращающем 0**:
   - **Проблема:** Если `r8b_process` возвращал 0 (нужно больше входных данных для генерации выхода), код всё равно удалял обработанные сэмплы из аккумулятора, что приводило к потере данных и артефактам.
   - **Исправление:** Удаление сэмплов из аккумулятора теперь происходит **только** когда `r8b_process` успешно вернул >0 выходных фреймов.

3. **Исправлен `FlushRemaining()`**:
   - Исправлена передача `IntPtr.Zero` → передача `double[1]` как валидного буфера (согласно C API `CDSPProcessor::process` ожидает валидный `double*` даже при `l=0`)

4. **Оптимизирован `ProcessAccumulated()`**:
   - Убран лишний `.ToArray()`, теперь используется прямой `GCHandle` на `_accumulatedInput`

5. **Исправлен `AudioService.cs`**:
   - Добавлено поле `_resampler` для отслеживания экземпляра ресемплера
   - `StopInternal()`: добавлен `_resampler?.Dispose()`
   - `Pause()` (Exclusive): добавлен `_resampler?.Dispose()`
   - При создании `R8brainResampler` сохраняется ссылка в `_resampler`
   - При ошибке инициализации ресемплера `_resampler` устанавливается в `null`
   - `CurrentPosition` и `Duration` теперь корректно масштабируются при активном ресемплере (учитывают ratio output/input sample rate)
   - `CurrentSampleRate` и `CurrentBitDepth` возвращают OUTPUT формат (что реально идёт в ЦАП) при активном ресемплере
   - `StartPositionTracking()` теперь использует `CurrentPosition` вместо прямого обращения к `_bitPerfectProvider.CurrentTime`
   - `Seek()` при активном ресемплере пересоздаёт ресемплер и переинициализирует устройство вывода

6. **Удалены/исправлены тестовые файлы**:
   - `test_r8b.cs` — удалён (содержал неправильные P/Invoke сигнатуры)
   - `TestR8b/Program.cs` — исправлен (теперь использует корректные сигнатуры из `R8brainResampler.cs`)

7. **Сборка проекта**: 0 ошибок ✅


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
