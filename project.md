You are a senior WPF developer. Create a bit-perfect audio player called "PureAudio" in the currently opened empty folder. Use .NET 10 and C# with WPF, structured in MVVM (Models, Views, ViewModels). The project must compile and run successfully with dotnet run.

Core Audio Requirements:

Playback of FLAC, WAV, MP3 via NAudio.

Bit-perfect output using WASAPI (toggle between Shared/Exclusive modes).

No DSP, EQ, volume control, or resampling.

Gapless playback (toggle on/off).

Main Window Layout (960x220 px, not resizable):
Styled as a high-end Hi-Fi front panel with a dark background (#222) and amber/orange (#FFA500) text.

Left side (Display Area, ~2/3 width):

Cover Art: Square (180x180 px). Bound to CoverPath from metadata, with a "No Image" placeholder.

Timers & Bitrate: Two large timers (elapsed / total) and average bitrate. Bound to ViewModel properties.

Status Indicators: Show the state of WASAPI mode, Gapless, Play/Pause/Stop, Hires/MP3.

FFT Spectrum: Real-time visualization, even if placeholder data initially.

Scrolling Text: Marquee at the bottom showing "Artist - Album - Title".

Progress Bar: Seekable slider with smooth dragging.

Right side (Control Panel, 220x260 px area):
All buttons MUST fit within this 220x260 pixel area. Use a vertical StackPanel with spacing of 8px between rows.

Row 1: WASAPI: Excl and Gapless: On buttons.
Width: 108 px, Height: 28 px. Gap between them: 4 px.
Row 2: Transport controls (◄◄, ►/❚❚ (single toggle button), ■, ►►).
Width: 52 px, Height: 44 px. Gap: 4 px.
Row 3: Mode: Hires, +hires, +mp3 buttons.
Width: 70 px, Height: 28 px. Gap: 5 px.
Row 4: ? (Help) and ▼ (Collapse/Expand) buttons.
Width: 36 px, Height: 36 px. Align these to the right with a 4 px gap.
All labels must be fully visible without clipping. Adjust font size or abbreviate text slightly if needed, but never change the button's pixel dimensions.

Expanded Panel (Collapse/Expand):

When the ▼ button is clicked, the window height smoothly changes to 620 px (main 220 + panel 400). The panel slides out BELOW the main window.

The panel is split vertically: Library tree (left 50%) | Playlist (right 50%).

Library tree shows the active library (Hires or MP3). Double-click to add to playlist.

Playlist has working buttons: Up, Down, Delete (X), Clear (Basket icon).

Both library and playlist must have scrollbars when content overflows.

Functionality:

Help Window: The ? button opens a separate window with a brief user guide for the player. Fill it with real content.

Theme & Language: Include working buttons to toggle between a dark and a gray theme, and to switch the UI language (English/Russian).

Playlist Management: Implement all logic for adding folders (no duplicates), switching libraries, and managing the playback queue.

First Steps for You as the Agent:

Create the full project structure (.sln, .csproj, folders, all necessary classes and XAML files).

Implement the UI with the exact layout and button dimensions provided.

Run dotnet build to ensure zero compilation errors. Fix any errors before proceeding.

Do NOT test runtime functionality until the build is clean.