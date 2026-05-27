# 🌿 Forage Tracker — Stardew Valley Mod
Know exactly what foraged items are waiting for you across the valley — right from the map.
<br>

## 📖 Overview
Forage Tracker is a Stardew Valley mod that displays daily forageable item counts directly in the map tooltip. Hover over any area and see what has spawned there that day, how many are left to pick and an icon for each item type — all updating in real time as you collect.
<br>
## ✨ Features

### 🗺️ Map Tooltip Integration
When you open the map and hover over an area, a clean info box appears below the vanilla area label. It shows:

| Tooltip Element | Description |
|---|---|
| Header | Displays **"Forage Today:"** |
| Forage Entries | One line per forageable type |
| Item Icons | Shows the forageable sprite beside the name |
| Count Display | Displays `remaining / total spawned` |

The tooltip is fully scale-aware — icon size, text, padding and positioning all adapt to your UI scale so it looks correct at any resolution.

### 📅 Daily Forage Scanning
At the start of every in-game day the mod scans every accessible location for naturally-spawned forageables. This runs automatically right after the game's own spawning pass, so the data is always accurate from the moment you wake up.

### 🔄 Real-Time Pickup Tracking
Every time you pick up a forageable the count updates immediately. No restart, no new day needed — the remaining count ticks down as you collect.

### ⚙️ In-Game Configuration
All options are available through Generic Mod Config Menu without touching any config file:

| Option | Description |
|---|---|
| Enable Forage Tracker | Master toggle — turns all tracking on or off |
| Show Item Icons | Show or hide the item sprite next to each entry |
| Show Remaining Only | Switch between `×3` and `×3 / 5` display format |
| Icon Scale | Fine-tune icon size from `0.5×` to `3×` |

When disabled, event handlers are fully unsubscribed — zero runtime overhead while inactive.
<br>

## 🎯 Who Is This For?
This mod is for players who want to be more efficient with foraging without relying on external wikis or spreadsheets. It is especially useful if you:

- Play with limited in-game time and want to know if a location is worth visiting before walking there.
- Are completing bundles or cooking recipes that require specific forage items.
- Enjoy a more informed playstyle without completely removing the exploration aspect.
- Use map expansion mods and have many locations to keep track of.

This is not a cheat mod. It only surfaces information about items already present on the ground. It does not spawn items, modify game data or give any mechanical advantage.
<br>

## 📋 Requirements
RequirementVersionStardew Valley1.6 or Smapi 4.5.2 or laterGeneric Mod Config MenuOptional — for in-game settings
<br>

## 📦 Installation

1. Install SMAPI by following the instructions at smapi.io.
2. Download the latest release from the Releases page.
3. Unzip the ForageTracker folder into your Stardew Valley/Mods/ directory.
4. Launch the game through SMAPI.

## ⚖️ License
© [Eugenia Vullioud - MadeInBoulogne]. All rights reserved.
This mod is provided free of charge for personal, non-commercial use only.
You may:

- Download and use this mod for personal enjoyment.
- Share a link to this page so others can download it.
- Fork the repository privately to learn from or experiment with the code.

You may not:

- Sell, sublicense or profit from this mod or any derivative of it.
- Redistribute compiled or modified versions without explicit written permission.
- Include this mod or its source code in any paid package, bundle or service.

If you would like to collaborate please open an issue or reach out directly.

💬 In plain terms: this mod is free, intended for enjoyment of the commmunity and no one except the original creator is allowed to make money from it.
<br>

## 🙋 About Me
Hi — I'm a student currently studying game development and software engineering, building mods like this one to grow my skills and give something back to communities I enjoy.
I'm genuinely grateful for every download, comment and kind word. Building things that other people find useful means a lot, and the Stardew Valley modding community has been wonderfully welcoming. If you're enjoying the mod, a ⭐ on the repo goes a long way — thank you for your support, it keeps me motivated.
<br>

### 💼 Open to Work & Commissions
I'm currently looking for opportunities in the games industry — testing, qa, programming, tools development or anything adjacent. I'm also open to paid commissions for SMAPI mods and other small development projects.
If you have something in mind, feel free to reach out through GitHub issues or the contact details on my profile.
<br>

Made with care.
