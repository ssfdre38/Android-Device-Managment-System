# Private Device Management System (D.M.S.)

A lightweight, privacy-first, open-source Device Management System designed for developers, homelabs, and private device networks. DMS allows you to monitor telemetry and administer your Android devices (phones, tablets, smart displays) securely over private mesh networks (like NetBird or WireGuard) without relying on bloated enterprise platforms or privacy-invasive consumer trackers.

---

## 📂 Repository Branches

This repository is organized into separate development branches for each component of the ecosystem:

*   **[`server` (Default)](https://github.com/ssfdre38/Android-Device-Managment-System/tree/server)**: The host management console built with ASP.NET Core Minimal APIs on .NET 10, including the dynamic web dashboard console (`wwwroot/`).
*   **[`client`](https://github.com/ssfdre38/Android-Device-Managment-System/tree/client)**: The Android Client Agent (DMA) background service built in Kotlin.
*   **[`desktop-agent`](https://github.com/ssfdre38/Android-Device-Managment-System/tree/desktop-agent)**: The cross-platform desktop & server monitoring agent (DMA Desktop) built in C# for Windows, Linux, and macOS.

---

## 🌟 Key Features

* **Multi-Volume Storage Monitoring**: Track internal storage alongside external SD cards, USB OTG drives, and external SSD backups.
* **Hardware & Resource Telemetry**: Monitor battery levels, RAM usage (total vs. available), CPU architecture, display resolution, and system uptime in real-time.
* **Network Connection Diagnostics**: Instantly check local IP address, connection types (Wi-Fi, Cellular, Ethernet), and Wi-Fi signal strength.
* **Non-Invasive App Management**: Search, filter, and categorise user-installed vs. system applications. Remotely deploy new APKs or uninstall existing packages safely.
* **Interactive Remote Controls**: Adjust screen brightness, audio volumes (media stream), trigger device vibration, ping diagnostic checks, or launch specific applications remotely.
* **Device Administrator Integrations**: Remotely lock screen sessions or perform secure factory data wipes using Android's native `DevicePolicyManager`.

---

## 🔒 Abuse-Resistant & Privacy-First Design

DMS is built to be secure, transparent, and abuse-resistant to prevent it from ever being misused as stealthy spyware:

1. **Anti-Stealth Guardrail**: The Android client operates as a visible foreground service. If notification permissions are disabled or the notification channel is blocked, **the client service immediately stops running and goes offline**. It cannot operate invisibly.
2. **Zero Invasive Tracking**: The system contains **no GPS tracking, no SMS/message reading, and no call history access**. Only high-level system resource telemetry is processed.
3. **Explicit Consent Activation**: Remote actions (like screen lock and factory reset) require the app to be explicitly activated as a **Device Administrator** by the user through a prominent Android system prompt.

---

## 🏗️ Architecture

The system is designed to be lightweight, modern, and easy to deploy:

* **Host Server (`/server`)**: Built with ASP.NET Core Minimal APIs on .NET 10, running on SQLite. Features cookie-based authentication and configures network interface bindings via `appsettings.json`.
* **Client App (`/client`)**: Built in Kotlin for Android, running a lightweight background sync loop. Compatible with Android 11+ package visibility rules using the `QUERY_ALL_PACKAGES` permission.
* **Web Console (`/wwwroot`)**: A single-page, premium dark-themed dashboard using vanilla HTML5, CSS3, and JavaScript.

---

## 🚀 Getting Started

### 1. Host Server Setup
1. Ensure **.NET 10 SDK** is installed on the host (Windows Server 2025, Linux, or macOS).
2. Configure your server bindings and credentials in `appsettings.json`:
   ```json
   "ServerConfig": {
     "BindAddress": "10.10.10.1", // Set your private NetBird VPN IP or 0.0.0.0
     "Port": 18800,
     "AdminPassword": "your_secure_password"
   }
   ```
3. Run the host server:
   ```bash
   dotnet run
   ```
4. Access the web dashboard at `http://[BindAddress]:[Port]` and log in using your configured password.

### 2. Secure Network Connection (NetBird / WireGuard)
We highly recommend running the server and client over a private mesh network:
1. Connect both the host server and your Android device to your self-hosted **NetBird** network.
2. Note the private NetBird IP address assigned to your Android device.

### 3. Android Client Installation
1. Compile the APK using Gradle:
   ```bash
   ./gradlew assembleDebug
   ```
2. Sideload and install the APK on your Android device.
3. Open the app, enter the host server URL (e.g., `http://10.10.10.1:18800`), and tap **Start Service**.
4. (Optional) For full system remote controls, follow the in-app prompt to enable **Device Administrator** access.
5. To keep ADB debug connectivity active when on the road over cellular (without rooting):
   * Plug the tablet into your local PC once and run:
     ```bash
     adb tcpip 5555
     ```
   * The ADB daemon will now stay open on port 5555, allowing you to connect securely via NetBird (`adb connect [Tablet_NetBird_IP]:5555`) while away from home.
