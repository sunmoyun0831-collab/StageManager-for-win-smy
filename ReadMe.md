# Stage Manager for Windows

This is an experimental approach to bring the macOS [Stage Manager](https://support.apple.com/en-us/HT213315) to Microsoft Windows.

> **Important:** This is a prototype and a feasibility study - I am not actively developing this project at the moment but I'd be happy to review and merge pull requests. 

![Stage Manager](media/StageManager%20Basics.gif)

This prototype groups applications by their process. By switching between so called "scenes" on the left, Stage Manager hides other windows and the desktop icons, helping you to focus.

## Usage

Download the latest `StageManager-<number>` artifact of a successful run from the Actions tab and run the executable in it, or 
 - clone this repository
 - cd into the repository directory
 - run `dotnet run --project StageManager`

To quit, find the app's tray icon (Windows might move it into the overflow menu) and use its context menu to close the app.

### Working with scenes

 - **Switch** to a scene by clicking it in the sidebar. Activating a window (taskbar, Alt+Tab) switches to its scene as well.
 - **Combine apps** into one scene by dragging a scene from the sidebar onto the stage, by Shift+clicking it or with "Add to current stage" from its context menu. Arrange the windows side by side with Windows' snap layouts. Combined apps stay together, also after a restart.
 - **Take an app out** of the current scene by dragging one of its windows onto the sidebar. Dropping it on another scene moves the app over there.
 - **Pin apps** that should stay visible in every scene, like a file manager on a second screen. Use the "Pinned apps" tray menu or right-click a scene. Focusing a pinned app does not switch scenes.
 - **Drag files to an app in another scene** by moving the drag to the left screen edge and holding it over the scene for a moment. Stage Manager switches to that scene and the file can be dropped on the app.
 - The sidebar slides away while windows overlap it and comes back when the mouse touches the left screen edge.
 - Windows of other scenes are not minimized, they wait behind a backdrop showing the wallpaper, so no minimize animations play and maximized or snapped windows stay as they are. The sidebar shows them as they looked when they left the stage.
 - Click the wallpaper to show the desktop (can be turned off in the tray menu).

Pinned apps and combined scenes are stored in `%APPDATA%\StageManager\settings.json`. Errors are written to `error.log` in the same folder.
 
### Requirements
 - Windows 10 version 2004 or newer
 - [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download)

## To do

This is an experimental fun project. I don't have any idea whether or not this is going to be a final product one day. 

|Topic|State|
|-|-|
|**Experimental stage**||
|initial windows grouping by process|✅|
|3D display of opened windows (static)|✅|
|hide/show windows of given scenes|✅|
|hide/show desktop icons|✅|
|scene management with drag&drop|✅|
|restore windows on quit/restart|✅|
|auto hide & fly-in scenes for maximized windows|✅|
|full screenshots for windows that were minimized on startup|✅|
|drag windows from other scenes into the current one|✅|
|place screenshots in relative size of the desktop|⬜|
|limit maximum scenes (like 6 for macOS?)|✅|
|limit window count per scene (like newest 5)|⬜|
|tray icon to start & stop|✅|
|start with Windows|✅|
|**Product stage**||
|virtual desktop support (pin window)|⬜|
|multi-monitor support|⬜|
|visual feedback when dragging windows from other scenes|✅|
|combine several apps in one scene|✅|
|pin apps to all scenes|✅|
|switch scenes while dragging files|✅|
|feature parity with macOS Stage Manager|⬜|
|**Polishing stage**||
|window animations|⬜|
|live dwm thumbnails|✅|
|adjust 3D angle according to screen position|⬜|
|flyover sidebar in desktop view mode if icons are close to the left|⬜|

Contributions very welcome :heart:

---

Stage Manager is using a few code files to handle window tracking from [workspacer](https://github.com/workspacer/workspacer), an amazing open source project by [Rick Button](https://github.com/rickbutton).
