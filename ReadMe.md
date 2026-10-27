# Stage Manager for Windows

[![Latest release](https://img.shields.io/github/v/release/sunmoyun0831-collab/smy-room?label=download)](https://github.com/sunmoyun0831-collab/smy-room/releases/latest)
[![Build](https://github.com/sunmoyun0831-collab/smy-room/actions/workflows/dotnet-desktop.yml/badge.svg)](https://github.com/sunmoyun0831-collab/smy-room/actions/workflows/dotnet-desktop.yml)
![Windows 10 & 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)
[![MIT license](https://img.shields.io/badge/license-MIT-green)](LICENSE)

The macOS [Stage Manager](https://support.apple.com/en-us/HT213315) for Windows. Your apps are grouped into stages, the other stages wait as tilted cards in a sidebar on the left, and switching between them lets the windows fly — while Alt+Tab, the taskbar and snap layouts keep working as usual.

**[中文说明](#中文说明)**

## Download

Get **`StageManager.exe`** from the [latest release](https://github.com/sunmoyun0831-collab/smy-room/releases/latest) and run it. Nothing to install, .NET is included.

Windows SmartScreen may warn about the unsigned exe: click *More info* → *Run anyway*. Stage Manager lives in the notification area of the taskbar, its menu has all settings and *Quit*.

## Features

- **Stages** – the apps you work with are on stage, all others are minimized and wait in the sidebar. Click a card to switch, or just use Alt+Tab or the taskbar.
- **Fly animation** – windows shrink into the sidebar and come out of it, smoothly drawn by the graphics card. Can be turned off.
- **Several apps per stage** – drag a card onto the stage or Shift+click it to add its apps to the current stage, then arrange them with snap layouts. Drag a window onto the sidebar to take its app out again, or onto another card to move it there. Stages are remembered.
- **Pinned apps** – a file manager on a second screen or a music player stays visible on every stage and never switches stages.
- **Excluded apps** – apps that run in the background, like the windows of tray apps, can be left out entirely: Stage Manager neither hides nor shows them.
- **Drag files between stages** – hold a dragged file over a card for a moment, the stage opens and the file can be dropped on its apps.
- **macOS sidebar** – tilted cards that share one perspective, a stack of up to three windows per stage, cards turn to you on hover. Windows may reach into the sidebar, it only slides away once a window covers more than half of the cards and comes back at the left screen edge.
- **Light and dark** – the menus look like Windows 11 menus and follow the Windows theme and accent color.

## What is new compared to the original

This project continues the prototype [awaescher/StageManager](https://github.com/awaescher/StageManager) by Andreas Wäscher, which showed the idea with live thumbnails and basic switching. Added and changed here:

| | Original prototype | This version |
|-|-|-|
| Sidebar | flat live thumbnails | tilted 3D cards with one shared perspective, window stacks, hover effect |
| Switching | windows minimize and restore with the Windows animation | windows fly between stage and sidebar, Alt+Tab and taskbar switch stages too |
| Apps per stage | one app per stage | several apps per stage, remembered across restarts |
| Pinning | – | pinned apps stay visible on every stage, excluded apps are left alone |
| Drag and drop | – | hold dragged files over a card to open its stage |
| Sidebar hiding | hides when a window touches it | hides only when a window covers more than half of the cards |
| Quitting | could hang and leave windows minimized | always brings all windows back, also after a crash, and never hangs |
| Scaled screens | sidebar and overlap checks off on scaled or several screens | exact on every scaling and with several screens, e.g. 4K at 150 % |
| Odd windows | showed ghost windows of hung apps and hidden helper windows | ignores them, never shows windows apps keep hidden |
| Settings | – | pinned apps, stages and options are saved in `%APPDATA%\StageManager` |
| Tray menu | basic | Windows 11 style with switches, debug log for bug reports |

## Tips

- **Shift+click** a card adds its apps to the current stage, **right-click** a card for more.
- The tray menu has *Pinned apps*, *Excluded apps*, *Window animations*, *Start with Windows* and a *Debug log* that helps with bug reports.
- Errors are written to `%APPDATA%\StageManager\error.log`.

## Known limitations

- The sidebar is on the primary screen. Windows on other screens are part of stages as well, pin apps that should stay there.
- Virtual desktops are not supported.
- Windows of apps running as administrator may not be hidden unless Stage Manager runs as administrator as well.

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) on Windows.

```
git clone https://github.com/sunmoyun0831-collab/smy-room.git
cd smy-room
dotnet run --project StageManager
```

Every push is built by GitHub Actions, releases are made with the *Release* workflow.

---

## 中文说明

把 macOS 的「台前调度」搬到 Windows：应用按舞台分组，其他舞台以倾斜的卡片排在左侧栏，切换时窗口会飞进飞出；Alt+Tab、任务栏和贴靠布局照常可用。

### 下载

在 [最新发布](https://github.com/sunmoyun0831-collab/smy-room/releases/latest) 里下载 **`StageManager.exe`**，双击运行即可，无需安装，也不用装 .NET。

exe 没有数字签名，SmartScreen 可能会拦：点「更多信息」→「仍要运行」。程序在任务栏右下角的托盘里，所有设置和退出都在它的菜单里。

### 功能

- **舞台**：正在用的应用在舞台上，其他应用最小化后排在左侧栏。点卡片切换，也可以直接用 Alt+Tab 或任务栏。
- **飞行动画**：窗口缩小飞进侧栏、从侧栏飞出，由显卡绘制，流畅不卡；可在托盘里关闭。
- **一个舞台放多个应用**：把卡片拖到舞台上或 Shift+点击，就能把它的应用加入当前舞台，再用贴靠布局并排；把窗口拖回侧栏可拆出来。分组会被记住。
- **固定应用**：比如放在副屏的文件管理器，在所有舞台都保持可见，也不会触发切换。
- **排除应用**：在后台运行的程序（比如托盘软件的窗口）可以完全排除，Stage Manager 既不隐藏也不显示它们。
- **跨舞台拖文件**：拖着文件在卡片上停一会儿，就会切到那个舞台，直接松手放下。
- **macOS 式侧栏**：统一透视的倾斜卡片、最多三层的窗口堆叠、悬停时卡片转向你；窗口挡住一半以上卡片时侧栏才收起，鼠标碰到屏幕左边缘再滑出来。
- **深浅色**：菜单是 Windows 11 风格，跟随系统深浅色和强调色。

### 相比原作者的改动

本项目基于 Andreas Wäscher 的原型 [awaescher/StageManager](https://github.com/awaescher/StageManager)（MIT 许可）继续开发。原型只有实时缩略图和基本的点击切换，这里加入了：倾斜 3D 卡片和统一透视、飞行动画、Alt+Tab/任务栏切换、一个舞台多个应用、固定应用和排除应用、拖文件切换舞台、侧栏只在被挡住一半以上时收起、退出和崩溃时一定恢复所有窗口且不会卡死、高分屏（如 4K 150%）下清晰准确、忽略卡死程序的幽灵窗口和软件的隐藏辅助窗口、设置自动保存、Windows 11 风格托盘菜单、以及可直接下载的单文件 exe。

### 已知限制

- 侧栏只在主屏幕上；其他屏幕上的窗口也属于舞台，想一直留在副屏的应用请固定。
- 不支持 Windows 虚拟桌面。
- 以管理员身份运行的程序，Stage Manager 可能无法隐藏它的窗口，除非 Stage Manager 也以管理员身份运行。

---

Window tracking is based on code from [workspacer](https://github.com/workspacer/workspacer) by [Rick Button](https://github.com/rickbutton). Licensed under the [MIT license](LICENSE).
