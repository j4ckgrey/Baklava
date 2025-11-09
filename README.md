<h1 align="center">🍮 Baklava</h1>
<h3 align="center">Jellyfin Media Request & Search Enhancement Plugin</h3>

<p align="center">
  <img src="Baklava.png" alt="Baklava Logo" width="200" />
</p>

<p align="center">
  <i>
    A comprehensive <b>Jellyfin plugin</b> that adds intelligent media request management, enhanced search capabilities with local/global toggle, 
    and seamless integration with external search providers.
  </i>
</p>

---

## ✨ Features

### 🎬 Media Request System
- **User Requests:** Allow users to request movies and TV series  
- **Admin Approval Workflow:** Approve or deny requests through an intuitive interface  
- **Request Tracking:** Monitor pending and approved requests  
- **Responsive UI:** Optimized for all screen sizes with adaptive card layouts  

### 🔍 Enhanced Search
- **Search Toggle:** Switch between local (Jellyfin library) and global (external) search  
- **Visual Indicator:** Globe icon with slash overlay for local search mode  
- **Smart Defaults:** Global search by default for discovery, configurable local enforcement  
- **TV Client Support:** Automatic local search enforcement for TV clients (Android TV, Fire TV, etc.)  

### 🎯 Server-Side Processing
- **SearchActionFilter:** Intelligent request interception and routing  
- **Prefix Handling:** Automatic `local:` prefix management  
- **Gelato Integration:** Seamless handoff to external search providers  
- **Configurable:** Enable/disable features via plugin settings  

---

## 📦 Installation

### ✅ Via Jellyfin Plugin Repository (Recommended)

1. Open **Jellyfin Dashboard**  
2. Navigate to **Plugins → Repositories**  
3. Add repository URL:https://raw.githubusercontent.com/j4ckgrey/Baklava/main/manifest.json
4. Go to **Catalog** and install **Baklava**  
5. Restart Jellyfin  

---

## ⚙️ Configuration

### ⚠️ Prerequisites

Before installing **Baklava**, ensure the following Jellyfin plugins are installed and configured:

| Plugin | Description | Link |
|--------|--------------|------|
| **Gelato** | External search provider used by Baklava for global discovery | [lostb1t/Gelato](https://github.com/lostb1t/Gelato) |
| **File Transformation** | Required for certain media handling and transformations | [IAmParadox27/jellyfin-plugin-file-transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) |

> ⚠️ If these plugins are missing or misconfigured, Baklava may have limited functionality.

---

### 🔧 Plugin Settings

Navigate to:  
**Dashboard → Plugins → Baklava**

#### Search Filter Settings
- **Enable Search Filter:** Toggle server-side search prefix handling  
- **Force TV Client Local Search:** Automatically enforce local search for TV clients  

#### TMDB Integration
- **TMDB API Key:** For metadata lookups and poster images  
- **Default TMDB ID:** For configuration testing  

---

## 🚀 Usage

### 🌐 Search Toggle

The search toggle appears as a **globe icon** next to the search bar:

| Icon | Mode | Description |
|------|------|-------------|
| 🌐 | Global | Searches external sources via Gelato |
| 🚫🌐 | Local | Searches only your Jellyfin library |

#### To use:
1. Type your search query  
2. Click the globe icon to toggle modes  
3. Results refresh automatically  

---

### 🙋 Media Requests

#### For Users:
1. Browse or search for media  
2. Click **“Request”** on unavailable items  
3. Track requests in the **Requests** dropdown  
4. Get notified when approved  

#### For Admins:
1. Open the **Requests** dropdown (🔔)  
2. View pending requests (Movies/Series)  
3. Click to see details  
4. **Approve** or **Deny** with one click  
5. Approved items move to “Approved” section  

---

### 📺 TV Client Behavior

When **Force TV Client Local Search** is enabled (default: ON):
- Android TV, Fire TV, and other TV clients automatically use **local search**
- The `"local:"` prefix is added server-side  
- No user interaction required — fully transparent  

---

## 🧩 Troubleshooting

| Issue | Possible Fix |
|--------|---------------|
| 🔎 Search toggle not appearing | Clear browser cache, check console errors, verify plugin is loaded |
| 📺 TV client not using local search | Enable “Force TV Client Local Search”, check logs for “✓ Detected TV client” |
| 🗂 Requests not saving | Check file permissions, validate TMDB API key, inspect server logs |

---

## 📧 Support

- **Issues:** [Report a bug](https://github.com/j4ckgrey/Baklava/issues)  
- **Discussions:** [Join the conversation](https://github.com/j4ckgrey/Baklava/discussions)

---

<p align="center">
<img src="https://img.shields.io/badge/Platform-Jellyfin-blue?style=for-the-badge&logo=jellyfin" alt="Jellyfin Badge"/>
<img src="https://img.shields.io/badge/Plugin-Baklava-orange?style=for-the-badge&logo=github" alt="Baklava Badge"/>
</p>

<p align="center">
<i>Because every server deserves a sweet layer of Baklava 🍯</i>
</p>