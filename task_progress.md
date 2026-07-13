# PureAudio Task Progress

## ✅ Completed
- Buffer latency fix: Exclusive 200→100, Shared 150→100

## ✅ CUE Track Support Fixes
- [x] Add `PlayCueTrack` method to `AudioService` for proper CUE track playback
- [x] Fix `PlayInternal` to use CUE track info (already had seek to StartPosition)
- [x] Fix `Next()` and `Previous()` to preserve CUE context (PlayInternal reads CurrentItem.CueTrack)
- [x] Fix `Seek()` to clamp position within CUE track bounds
- [x] Fix `OnPlaybackStopped` to handle CUE track end properly (via StartPositionTracking)
- [x] Verify all changes compile and are consistent

## ✅ CUE Library Scanning & Cache Fixes
- [x] Verify CUE logic is called when adding new source (AddHiresSource → ScanFolderToTree → BuildTreeRecursive)
- [x] Fix CUE priority: if all CUE files are invalid, fall back to adding audio files normally
- [x] Verify CUE tracks are saved to cache (ConvertTreeToCache serializes CueTrack → CachedCueTrack)
- [x] Verify CUE tracks are restored from cache (BuildTreeFromCache reconstructs CueTrack from CachedCueTrack)
- [x] Add cache invalidation when CUE files are modified (CollectCueFilePaths + timestamp check in TryLoadCache)
- [x] Add PlaylistService.Add(AudioFile, CueTrack) overload for CUE track playlist items
- [x] Fix AddFolderToPlaylist to add CUE tracks with CueTrack context (not as plain AudioFile)
- [x] Add cache version check (bump to "1.1") to force rebuild of old caches without CUE data
- [x] Verify build succeeds with 0 errors


