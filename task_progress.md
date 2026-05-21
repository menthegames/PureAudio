# Task Progress - Fix Slow Startup / No Window After Reboot

## Todo List

- [x] Analyze all project files and understand the codebase
- [ ] **1. App.xaml.cs** — Refactor startup: show window immediately, defer heavy operations
- [ ] **2. MainWindow.xaml** — Add status bar for loading indicators
- [ ] **3. MainViewModel.cs** — Add async loading methods and status properties
- [ ] **4. ExpandedPanelViewModel.cs** — Add async library scanning with status
- [ ] **5. AudioService.cs** — Add timeout for WASAPI Exclusive initialization
- [ ] **6. MainWindow.xaml.cs** — Wire up post-startup loading
- [ ] **7. PureAudio.csproj** — Switch from single-file to folder-based publish
- [ ] **8. RELEASE_BUILD.md** — Update build instructions
- [ ] **9. Build and test**
