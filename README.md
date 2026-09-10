# ForceDelete Pro 🛡️

A small, **portable** Windows utility for deleting files Windows won't let you delete — the GUI version of the `takeown` → `icacls` → `Remove-Item` dance, plus the cases that chain can't handle. 

> [!NOTE]
> **Pro Edition**: This enhanced version includes recursive locking detection, smart window matching, and instant UI refresh notifications.

[查看中文说明 (README_zh-CN.md)](./README_zh-CN.md)

---

## 📖 Introduction

**ForceDelete Pro** is an advanced evolution of the lightweight Windows utility *ForceDelete*. It addresses the common frustration of "Access Denied" or "File in Use" errors by combining low-level kernel interactions with high-level workspace analysis. 

Whether you are dealing with stubborn system-owned files, hidden background locks deep in sub-folders, or "ghost" folders that refuse to disappear from Explorer, ForceDelete Pro provides a one-stop, portable solution to reclaim total control over your file system.

## 🖼️ Screenshot

![ForceDelete Pro Screenshot](docs/screenshot.png)

## 🛠️ What it does

| Reason a delete fails | How ForceDelete Pro handles it |
|---|---|
| Owned by SYSTEM / TrustedInstaller | Enables `SeTakeOwnership`/`SeRestore` privileges, takes ownership, grants full control |
| ReadOnly / Hidden / System attributes | Clears attributes first |
| Held open by a running process | **Shows which process** (Restart Manager) and can kill it + retry |
| **Deep Sub-file Lock** ✨ | **[PRO]** Recursively scans all sub-items to find locks hidden deep in folders |
| **"Soft" Lock (e.g. Notepad)** ✨ | **[PRO]** Matches window titles to identify processes without handle locks |
| Locked by the OS itself | Schedules deletion on next reboot (`MoveFileEx`) |
| **Explorer Ghosting** ✨ | **[PRO]** Triggers `SHChangeNotify` to force Explorer to refresh instantly |
| Staged driver package (DriverStore) | Removes it properly via `pnputil /delete-driver` |
| Long paths (>260) / reserved names | Uses the `\\?\` extended-length prefix |
| Sensitive data | Optional secure wipe (random overwrite) before delete |

## ✨ Niceties & Pro Features

- **Drag & drop** a file or folder anywhere onto the window.
- **Auto-scan on load** — shows size/attributes/owner and locking processes immediately.
- **Quarantine** — move-then-purge safety net instead of an instant permanent delete.
- **Safety guard** — refuses deletes inside `Windows`, `System32`, `Program Files`, and drive roots.
- **Recursive Scan [PRO]** — Automatically digs into sub-folders to find why a parent is locked.
- **Window Matching [PRO]** — Detects programs like **Notepad** via window titles.
- **Shell Notification [PRO]** — Forces Windows to refresh the folder view after deletion.
- **Action logging** — every delete/kill/quarantine is recorded.

## 🔑 How elevation works

The window runs at your **normal** user level, so drag-and-drop from Explorer just works. When an operation actually requires admin (a SYSTEM/TrustedInstaller-owned file, a reboot-delete, a driver package), the app spins up a short-lived **elevated helper process** for that one operation — a single UAC prompt — and reports the result back.

## 📋 Requirements

- Windows 10/11 x64
- .NET 10 SDK — **to build only**; the published `.exe` is self-contained
- An administrator account (for on-demand elevation)

## 🏗️ Build & Publish

```powershell
# Build (dev)
dotnet build

# Publish the portable single exe
dotnet publish -c Release -o .\dist
```

## 📂 Project layout

```text
ForceDelete-Pro/
├─ app.manifest             # asInvoker + long-path aware
├─ icon.ico                 # app icon
├─ MainWindow.xaml(.cs)     # UI + Smart Window Match & Auto-Kill logic
└─ Services/
   ├─ DeleteEngine.cs       # Ownership, ACLs, Attrs + Shell Refresh
   ├─ RestartManager.cs     # Enhanced Recursive Locking Scan
   ├─ Elevation.cs          # GUI ↔ Elevated worker bridge
   ├─ Privileges.cs         # Enables take-ownership / backup privileges
   ├─ DriverStore.cs        # Pnputil-based driver removal
   ├─ SafetyGuard.cs        # Critical-path guard + process kill
   ├─ Storage.cs            # Portable data location
   └─ Logger.cs             # Append-only action log
```

## 💻 Tech Stack

- WPF on .NET 10 (`net10.0-windows`)
- [WPF-UI](https://github.com/lepoco/wpfui) for Fluent / Mica design
- Win32 interop: Restart Manager, `AdjustTokenPrivileges`, `MoveFileEx`, `SHChangeNotify`

## 📄 License

MIT — see [LICENSE](LICENSE). Based on the original ForceDelete.
