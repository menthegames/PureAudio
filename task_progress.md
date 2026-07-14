# PureAudio — Рефакторинг

## Статус выполнения

### 🔴 Критические
- [x] 1. Рефакторинг `PlaybackEngine.PlayInternal()` — разделение на методы
- [x] 2. Исправление `CurrentPosition` (dead code / избыточная логика)
- [x] 3. Устранение дублирования `UpdateBitPerfectStatus()` между `PlaybackEngine` и `AudioStateManager`
- [x] 4. `StatusDisplayController` — константы для цветов вместо магических строк

### 🟡 Средние
- [x] 5. `LibraryService` — замена `ObservableCollection` на `List` в сервисе
- [x] 6. `LibraryCacheService.BuildTreeFromCache()` — выделение методов
- [x] 7. `PlaybackEngine.Seek()` — вынесение CUE-логики в отдельный метод
- [x] 8. Магические числа/строки — вынесение в константы
- [x] 9. `MetadataService` — разделение на `TagReader`, `BitDepthDetector`, `CoverArtExtractor`

### 🟢 Лёгкие
- [x] 10. `RelayCommand` — добавлена поддержка `NotifyCanExecuteChanged()`
- [x] 11. Silent catch — заменены на логирование через `Logger.Log()`
- [x] 12. `DeviceCapabilities.DacCapabilitiesText` — кэширование результата
- [x] 13. `WasapiExclusivePlayer` — комментарии приведены к английскому
