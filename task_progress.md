# Task Progress: Bit-Perfect Volume Management Refactoring

## Цель
Переработать управление громкостью: убрать переключатель WASAPI Exclusive/Shared из UI, заменить на кнопку "Bit Perfect", реализовать честный bit-perfect режим без DSP-регулировки громкости.

## План

- [x] Проанализировать текущую архитектуру (AudioService, MainViewModel, MainWindow.xaml)
- [x] Согласовать новую архитектуру с пользователем
- [ ] **Шаг 1:** AudioService.cs — переработать логику:
  - Убрать кубическую кривую громкости
  - Добавить метод `SetBitPerfectMode(bool enable)` 
  - В bit-perfect режиме: Volume = 1.0 (без DSP)
  - В обычном режиме: Volume = 1.0 (Shared, системный микшер)
  - При переключении в bit-perfect: устанавливать громкость 100%, сохранять предыдущее значение для возврата
- [ ] **Шаг 2:** AudioSettings.cs — добавить поля:
  - `BitPerfectEnabled` (bool)
  - `WarningAccepted` (bool) — для галочки "больше не показывать"
  - `PreviousVolume` (double) — для восстановления громкости при выходе из bit-perfect
- [ ] **Шаг 3:** MainViewModel.cs — переработать:
  - Убрать `WasapiExclusive`, `ToggleWasapiCommand`, `WasapiModeText`
  - Добавить `IsBitPerfectMode`, `BitPerfectCommand`
  - Логика блокировки ползунка в bit-perfect режиме
  - Индикация: золотой/серый для кнопки и битности/частоты
  - Диалог предупреждения при первом включении
- [ ] **Шаг 4:** MainWindow.xaml — обновить UI:
  - Заменить кнопку WASAPI Exclusive/Shared на кнопку "Bit Perfect"
  - Ползунок громкости: блокируется в bit-perfect режиме
  - Индикаторы битности/частоты: золотые в bit-perfect, серые в обычном режиме
- [ ] **Шаг 5:** Проверить и оттестировать изменения
