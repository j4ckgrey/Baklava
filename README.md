<h1 align="center">Baklava</h1>
<h3 align="center">Smart Media Layer for Jellyfin</h3>

<p align="center">
  <img src="Baklava.png" alt="Baklava Logo" width="200" />
</p>

<p align="center">
  <i>
    A next-generation <b>Jellyfin plugin</b> that transforms how your server handles media requests, caching, and playback.
    Baklava bridges <b>Gelato</b> and <b>Jellyfin</b> with intelligent caching and adaptive remote stream probing.
  </i>
</p>

---

## 🍯 Overview

**Baklava** is both a **server-side powerhouse** and a **frontend overhaul** for Jellyfin.  
It introduces smart caching, remote stream integration, complete user-to-admin request handling, 
and dynamic UI enhancements for effortless media management.

Whether you're streaming from remote sources or managing requests from multiple users,  
Baklava ensures your Jellyfin experience stays fast, reliable, and visually polished.

---

## ⚙️ Key Features

### 🧠 Server-Side Intelligence

#### 🔁 Adaptive Caching & Stream Handling
Baklava enhances Jellyfin’s caching layer, allowing it to **adapt dynamically to remote streams** (ideal for Gelato users).  
It reduces redundant probing and improves playback stability across all platforms — even TV clients.
#### 💾 Persistent Stream Caching
Baklava now includes **persistent database caching** for Gelato streams. Unlike Jellyfin which disposes streams quickly, Baklava stores stream information in SQLite for up to 7 days. This provides:
- **Faster playback**: No need to re-query aiostreams every time
- **Reduced API load**: Less strain on Stremio addon infrastructure  
- **Better reliability**: Fallback when remote services are slow or unavailable
- **User-specific caching**: Different users get personalized stream preferences

See [STREAM_CACHING.md](./STREAM_CACHING.md) for detailed documentation.
#### 🧩 Smart Probing for Tracks
Automatically fetches **audio and subtitle tracks** from remote streams for every platform, including Android TV and Fire TV.  
Baklava ensures your playback options are complete before the media even starts loading.

> This makes it a perfect companion for the **Gelato** plugin — unlocking full hybrid-source playback.

---

### 📬 Complete Requests System

#### 🙋 User-to-Admin Requests (Jellyseerr-like)
Baklava introduces a **fully native request system** within Jellyfin — bringing the power of Jellyseerr directly into your dashboard.  
Users can submit requests for unavailable content, track status, and get notified upon approval or import.

- Requests are stored **per user** with status tracking  
- Admins can **approve or deny** requests via a built-in interface  
- Works seamlessly with **manual imports** and **Gelato discovery**

![Requests Window](./Screenshots/3.png)
![Request Details](./Screenshots/4.png)
![Admin Approval View](./Screenshots/5.png)

#### ⚙️ Configurable Behavior
From the **plugin configuration page**, you can:
- Disable **Global Search Toggle** for TV clients (enforcing safe, local searches)
- Toggle **Auto Import** to allow or restrict direct imports (ideal for managing user permissions)
- Enable/disable the **Requests Feature** entirely for automation scenarios

![Config Controls](./Screenshots/6.png)

---

### 🧭 Manual Import System

Baklava introduces a **Manual Import Modal**, acting as a **middle-layer between Gelato and Jellyfin**.  
Instead of automatically importing everything Gelato finds, Baklava opens a detailed preview modal:

- Displays **cast, metadata, reviews, and artwork**  
- Lets users confirm before import  
- Prevents redundant or accidental imports  
- Streamlines admin control

![Manual Import Modal](./Screenshots/2.png)

---

### 🎨 Interface Enhancements

Baklava replaces Jellyfin’s old dropdowns with modern, flexible UI components.

#### 🧱 Smart Selectors
Each version, audio, and subtitle field now uses a **responsive carousel or dropdown**, designed for clarity and speed.  
Long filenames are truncated elegantly, with hover or click revealing full details.

![Version Selector](./Screenshots/1.png)

#### 🌈 Stremio-Inspired Look
The new design blends Jellyfin’s structure with Stremio-like cues —  
clear visual hierarchy, smooth transitions, and adaptive layouts that look great across web and TV clients.

---

## 🧩 Installation

### ✅ Via Jellyfin Plugin Repository

1. Open **Jellyfin Dashboard**  
2. Go to **Plugins → Repositories**  
3. Add: https://raw.githubusercontent.com/j4ckgrey/Baklava/main/manifest.json
4. Open **Catalog**, install **Baklava**  
5. Restart Jellyfin  

---

## 🔧 Configuration

### Requirements
| Plugin | Purpose | Link |
|--------|----------|------|
| **Gelato** | External source search integration | [lostb1t/Gelato](https://github.com/lostb1t/Gelato) |
| **File Transformation** | Stream transformation and compatibility | [IAmParadox27/jellyfin-plugin-file-transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) |

### Plugin Settings
Located under:  
**Dashboard → Plugins → Baklava**

Includes toggles for:
- Search filter handling  
- Force local search for TV clients  
- Auto import control  
- Requests system enable/disable  
- Advanced caching and stream probing  

---

## 🧠 How It Works

Baklava intercepts Jellyfin search, playback, and request actions,  
and intelligently routes them through its own optimized logic layer.

- **SearchActionFilter:** Handles routing between local and global sources  
- **StreamInterceptor:** Manages stream probing and caching  
- **RequestManager:** Tracks user/admin request workflows  
- **UI Injector:** Replaces Jellyfin’s version/audio/subtitle selection system with the new visual interface

---

## 🧩 Troubleshooting

| Issue | Fix |
|-------|-----|
| UI not loading correctly | Clear browser cache and restart Jellyfin |
| Requests not saving | Check file permissions under Jellyfin data directory |
| Global search toggle missing | Ensure Gelato is installed and enabled |
| Missing subtitles/audio | Verify probing is enabled in plugin settings |

---

## 💬 Community & Support

- 🐞 **Bugs & Issues:** [GitHub Issues](https://github.com/j4ckgrey/Baklava/issues)  
- 💡 **Discussions:** [GitHub Discussions](https://github.com/j4ckgrey/Baklava/discussions)  
If you like my work and want to support development, consider buying me a coffee:
👉 https://ko-fi.com/j4ckgrey
Join the community and stay updated on Discord:  
👉 https://discordapp.com/channels/1433689453158862943/1441378427239137300
---

<p align="center">
<img src="https://img.shields.io/badge/Platform-Jellyfin-blue?style=for-the-badge&logo=jellyfin" alt="Jellyfin Badge"/>
<img src="https://img.shields.io/badge/Plugin-Baklava-orange?style=for-the-badge&logo=github" alt="Baklava Badge"/>
</p>

<p align="center">
<sub>Baklava — Smart Media Layer for Jellyfin. Built with ❤️ by j4ckgrey.</sub>
</p>
