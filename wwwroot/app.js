// State Variables
let selectedDeviceId = null;
let devicesCache = [];
let allApps = [];
let activeTab = "control";
let appFilterType = "user";

// DOM Elements
const deviceList = document.getElementById("device-list");
const selectedDeviceName = document.getElementById("selected-device-name");
const onlineBadge = document.getElementById("online-badge");
const dashboardTabs = document.getElementById("dashboard-tabs");
const valModel = document.getElementById("val-model");
const valVersion = document.getElementById("val-version");
const valBattery = document.getElementById("val-battery");
const valBatteryFill = document.getElementById("val-battery-fill");
const valRamUsed = document.getElementById("val-ram-used");
const valRamTotal = document.getElementById("val-ram-total");
const valRamBar = document.getElementById("val-ram-bar");
const storageVolumesContainer = document.getElementById("storage-volumes-container");
const valIp = document.getElementById("val-ip");
const valConnection = document.getElementById("val-connection");
const valUptime = document.getElementById("val-uptime");
const commandLogBody = document.getElementById("command-log-body");
const appsTableBody = document.getElementById("apps-table-body");
const serverTime = document.getElementById("server-time");

// Modal Elements
const resultModal = document.getElementById("result-modal");
const modalTitle = document.getElementById("modal-title");
const modalText = document.getElementById("modal-text");

// ── Clock ───────────────────────────────────────────────────────────────────
setInterval(() => {
    const now = new Date();
    serverTime.textContent = now.toTimeString().split(" ")[0];
}, 1000);

// ── Tabs Switching ──────────────────────────────────────────────────────────
function switchTab(tabId) {
    activeTab = tabId;
    
    // Update active button state
    const buttons = document.querySelectorAll(".tab-btn");
    buttons.forEach(btn => {
        if (btn.getAttribute("onclick").includes(tabId)) {
            btn.classList.add("active");
        } else {
            btn.classList.remove("active");
        }
    });

    // Update active pane
    const panes = document.querySelectorAll(".tab-pane");
    panes.forEach(pane => {
        if (pane.id === `tab-${tabId}`) {
            pane.classList.add("active");
        } else {
            pane.classList.remove("active");
        }
    });
}

// ── Polling & Fetching Devices ──────────────────────────────────────────────
async function fetchDevices() {
    try {
        const response = await fetch("/api/devices");
        if (!response.ok) throw new Error("Network response not ok");
        const devices = await response.json();
        devicesCache = devices;
        
        renderDeviceList(devices);
        if (selectedDeviceId) {
            const activeDev = devices.find(d => d.Id === selectedDeviceId);
            if (activeDev) {
                updateTelemetry(activeDev);
            }
        }
    } catch (e) {
        console.error("Error fetching devices:", e);
    }
}

function renderDeviceList(devices) {
    if (devices.length === 0) {
        deviceList.innerHTML = `<li class="loading-item">No devices registered</li>`;
        return;
    }
    
    deviceList.innerHTML = "";
    devices.forEach(device => {
        const li = document.createElement("li");
        li.className = `device-item ${device.Id === selectedDeviceId ? 'active' : ''}`;
        li.onclick = () => selectDevice(device.Id);
        
        li.innerHTML = `
            <div>
                <span class="device-name">${device.Name}</span>
                <span class="device-model">${device.Model}</span>
            </div>
            <span class="status-dot ${device.IsOnline ? 'status-online' : 'status-offline'}"></span>
        `;
        deviceList.appendChild(li);
    });
}

function selectDevice(id) {
    selectedDeviceId = id;
    dashboardTabs.style.display = "flex";
    
    // Highlight active element in list
    const items = deviceList.querySelectorAll(".device-item");
    items.forEach((item, index) => {
        const dev = devicesCache[index];
        if (dev && dev.Id === id) {
            item.classList.add("active");
        } else {
            item.classList.remove("active");
        }
    });

    const activeDev = devicesCache.find(d => d.Id === id);
    if (activeDev) {
        updateTelemetry(activeDev);
        fetchCommandLogs(id);
    }
}

function updateTelemetry(device) {
    selectedDeviceName.textContent = device.Name;
    
    // Online state
    if (device.IsOnline) {
        onlineBadge.textContent = "ONLINE";
        onlineBadge.className = "badge badge-online";
    } else {
        onlineBadge.textContent = "OFFLINE";
        onlineBadge.className = "badge badge-offline";
    }
    
    // Model & CPU Arch
    if (device.SystemInfo && device.SystemInfo.CpuArch && device.SystemInfo.CpuArch !== "unknown") {
        valModel.textContent = `${device.Model} (${device.SystemInfo.CpuArch})`;
    } else {
        valModel.textContent = device.Model;
    }
    
    // Android Version & Resolution Specs
    if (device.SystemInfo && device.SystemInfo.Resolution) {
        valVersion.textContent = `Android ${device.AndroidVersion} (${device.SystemInfo.Resolution})`;
    } else {
        valVersion.textContent = `Android ${device.AndroidVersion}`;
    }
    
    // Battery level
    valBattery.textContent = `${device.Battery}%`;
    valBatteryFill.style.width = `${device.Battery}%`;
    if (device.Battery < 20) {
        valBatteryFill.style.background = "linear-gradient(90deg, #ff3b30, #ff453a)"; // Red
    } else if (device.Battery < 50) {
        valBatteryFill.style.background = "linear-gradient(90deg, #ffcc00, #ffd60a)"; // Yellow
    } else {
        valBatteryFill.style.background = "linear-gradient(90deg, #00ff66, #39ff14)"; // Green
    }
    
    // RAM details
    if (device.SystemInfo && device.SystemInfo.TotalRam > 0) {
        const usedGB = ((device.SystemInfo.TotalRam - device.SystemInfo.AvailableRam) / (1024 * 1024 * 1024)).toFixed(2);
        const totalGB = (device.SystemInfo.TotalRam / (1024 * 1024 * 1024)).toFixed(2);
        const pct = ((device.SystemInfo.TotalRam - device.SystemInfo.AvailableRam) / device.SystemInfo.TotalRam) * 100;
        valRamUsed.textContent = `${usedGB} GB`;
        valRamTotal.textContent = `${totalGB} GB`;
        valRamBar.style.width = `${pct}%`;
    } else {
        valRamUsed.textContent = "-";
        valRamTotal.textContent = "-";
        valRamBar.style.width = "0%";
    }
    
    // Storage Info (Multi-volume support)
    if (device.StorageVolumes && device.StorageVolumes.length > 0) {
        storageVolumesContainer.innerHTML = "";
        device.StorageVolumes.forEach(vol => {
            const usedGB = (vol.Used / (1024 * 1024 * 1024)).toFixed(2);
            const totalGB = (vol.Total / (1024 * 1024 * 1024)).toFixed(2);
            const pct = vol.Total > 0 ? (vol.Used / vol.Total) * 100 : 0;
            
            const group = document.createElement("div");
            group.className = "storage-group";
            group.innerHTML = `
                <span class="storage-volume-name"><i class="fa-solid fa-hard-drive"></i> ${vol.Name}</span>
                <div class="storage-text">
                    <span>${usedGB} GB</span> / <span>${totalGB} GB</span>
                </div>
                <div class="progress-track">
                    <div class="progress-bar" style="width: ${pct}%;"></div>
                </div>
            `;
            storageVolumesContainer.appendChild(group);
        });
    } else {
        const usedGB = (device.StorageUsed / (1024 * 1024 * 1024)).toFixed(2);
        const totalGB = (device.StorageTotal / (1024 * 1024 * 1024)).toFixed(2);
        const pct = device.StorageTotal > 0 ? (device.StorageUsed / device.StorageTotal) * 100 : 0;
        
        storageVolumesContainer.innerHTML = `
            <div class="storage-group">
                <span class="storage-volume-name"><i class="fa-solid fa-hard-drive"></i> Internal Storage</span>
                <div class="storage-text">
                    <span>${usedGB} GB</span> / <span>${totalGB} GB</span>
                </div>
                <div class="progress-track">
                    <div class="progress-bar" style="width: ${pct}%;"></div>
                </div>
            </div>
        `;
    }
    
    // Network Specs & Diagnostics
    if (device.SystemInfo) {
        valIp.textContent = device.SystemInfo.IpAddress || "-";
        
        if (device.SystemInfo.ConnectionType && device.SystemInfo.ConnectionType !== "None") {
            const sig = device.SystemInfo.WifiSignal >= 0 ? ` (${device.SystemInfo.WifiSignal}%)` : "";
            valConnection.textContent = `${device.SystemInfo.ConnectionType}${sig}`;
        } else {
            valConnection.textContent = "-";
        }
        
        if (device.SystemInfo.Uptime > 0) {
            const hrs = Math.floor(device.SystemInfo.Uptime / 3600);
            const mins = Math.floor((device.SystemInfo.Uptime % 3600) / 60);
            valUptime.textContent = `${hrs}h ${mins}m`;
        } else {
            valUptime.textContent = "-";
        }
        
        // Wake Lock Remote Button state update
        const btnWake = document.getElementById("btn-toggle-wake");
        if (device.SystemInfo.KeepScreenAwake) {
            btnWake.classList.add("active");
            btnWake.innerHTML = `<i class="fa-solid fa-sun" style="color: var(--color-warning);"></i> Keep Awake: ON`;
        } else {
            btnWake.classList.remove("active");
            btnWake.innerHTML = `<i class="fa-solid fa-sun"></i> Keep Awake: OFF`;
        }
    }
    
    // Render apps list
    allApps = device.AppList || [];
    filterAppList();
}

// ── App Search and Filtering ────────────────────────────────────────────────
function renderAppList(apps) {
    // Apply tab categorization filter
    let filtered = apps;
    if (appFilterType === "user") {
        filtered = apps.filter(app => !app.startsWith("[SYS]"));
    } else if (appFilterType === "system") {
        filtered = apps.filter(app => app.startsWith("[SYS]"));
    }

    if (filtered.length === 0) {
        appsTableBody.innerHTML = `<tr><td colspan="2" class="empty-row">No packages found for this filter</td></tr>`;
        return;
    }
    
    appsTableBody.innerHTML = "";
    filtered.forEach(app => {
        const isSystem = app.startsWith("[SYS]");
        const cleanApp = isSystem ? app.substring(5) : app;
        const tr = document.createElement("tr");
        tr.innerHTML = `
            <td class="package-name-cell">
                <i class="fa-solid fa-cube" style="color: ${isSystem ? 'var(--color-accent)' : 'var(--color-primary)'};"></i> 
                ${cleanApp} ${isSystem ? '<span class="status-badge status-badge-sent" style="font-size: 8px; padding: 2px 4px; margin-left: 6px;">SYSTEM</span>' : ''}
            </td>
            <td style="text-align: right;">
                <button class="btn btn-outline btn-neon btn-sm" onclick="launchApp('${cleanApp}')" style="margin-right: 6px;">
                    <i class="fa-solid fa-play"></i> Launch
                </button>
                <button class="btn btn-outline btn-danger btn-sm" onclick="uninstallPackage('${cleanApp}')">
                    <i class="fa-solid fa-trash-can"></i> Uninstall
                </button>
            </td>
        `;
        appsTableBody.appendChild(tr);
    });
}

function filterAppList() {
    const query = document.getElementById("app-search-input").value.toLowerCase();
    const filtered = allApps.filter(app => {
        const cleanApp = app.startsWith("[SYS]") ? app.substring(5) : app;
        return cleanApp.toLowerCase().includes(query);
    });
    renderAppList(filtered);
}

function setAppFilterType(type) {
    appFilterType = type;
    
    document.getElementById("filter-btn-user").classList.toggle("active", type === "user");
    document.getElementById("filter-btn-system").classList.toggle("active", type === "system");
    document.getElementById("filter-btn-all").classList.toggle("active", type === "all");
    
    filterAppList();
}

function uninstallPackage(packageName) {
    if (confirm(`Are you sure you want to silently uninstall package: ${packageName}?`)) {
        sendDirectCommand("UninstallPackage", packageName);
        alert(`Uninstall command enqueued for: ${packageName}`);
    }
}

// ── Device Admin Specific Confirmation ──────────────────────────────────────
function confirmWipeDevice() {
    if (!selectedDeviceId) {
        alert("Select a device first.");
        return;
    }
    const check1 = confirm("WARNING: You are about to initiate a remote Factory Reset (Wipe Data) on the target device. This will completely erase all files, apps, settings, and local storage. Do you wish to proceed?");
    if (check1) {
        const check2 = prompt("To confirm this destructive action, please type WIPE in all capitals:");
        if (check2 === "WIPE") {
            sendDirectCommand("WipeDevice", "");
            alert("Factory Reset command successfully enqueued to the target device.");
        } else {
            alert("Confirmation cancelled: mismatch code.");
        }
    }
}

// ── Fetch Command History Logs ──────────────────────────────────────────────
async function fetchCommandLogs(deviceId) {
    try {
        const response = await fetch(`/api/devices/${deviceId}/commands`);
        if (!response.ok) return;
        const list = await response.json();
        
        renderCommandLogs(list);
    } catch (e) {
        console.error("Error fetching logs:", e);
    }
}

function renderCommandLogs(logs) {
    if (logs.length === 0) {
        commandLogBody.innerHTML = `<tr><td colspan="6" class="empty-row">No commands issued yet</td></tr>`;
        return;
    }
    
    commandLogBody.innerHTML = "";
    logs.forEach(log => {
        const tr = document.createElement("tr");
        
        let badgeClass = "status-badge-pending";
        if (log.Status === "Sent") badgeClass = "status-badge-sent";
        if (log.Status === "Success") badgeClass = "status-badge-success";
        if (log.Status === "Failed") badgeClass = "status-badge-failed";
        
        let statusContent = `<span class="status-badge ${badgeClass}">${log.Status.toUpperCase()}</span>`;
        if (log.Status === "Failed") {
            statusContent += `<button class="btn-retry-cmd" onclick="retryCommand('${log.Id}')" title="Retry command"><i class="fa-solid fa-rotate-right"></i> Retry</button>`;
        }
        
        const payloadText = log.Payload.length > 30 ? log.Payload.substring(0, 30) + "..." : log.Payload;
        
        let resultCell = "-";
        if (log.Result && log.Result.trim().length > 0) {
            resultCell = `<span class="click-output" onclick="showOutputModal('${log.CommandType}', \`${encodeURIComponent(log.Result)}\`)">View Output</span>`;
        }
        
        tr.innerHTML = `
            <td><strong>${log.CommandType}</strong></td>
            <td title="${log.Payload}">${payloadText}</td>
            <td>${statusContent}</td>
            <td>${log.CreatedAt.split(" ")[1] || log.CreatedAt}</td>
            <td>${log.CompletedAt ? (log.CompletedAt.split(" ")[1] || log.CompletedAt) : "-"}</td>
            <td>${resultCell}</td>
        `;
        commandLogBody.appendChild(tr);
    });
}

async function retryCommand(commandId) {
    if (!selectedDeviceId) return;
    try {
        const response = await fetch(`/api/devices/${selectedDeviceId}/commands/${commandId}/retry`, {
            method: "POST"
        });
        if (response.ok) {
            fetchCommandLogs(selectedDeviceId);
        }
    } catch (e) {
        console.error("Error retrying command:", e);
    }
}

async function clearCommandHistory() {
    if (!selectedDeviceId) return;
    if (confirm("Are you sure you want to clear all command logs for this device?")) {
        try {
            const response = await fetch(`/api/devices/${selectedDeviceId}/commands`, {
                method: "DELETE"
            });
            if (response.ok) {
                fetchCommandLogs(selectedDeviceId);
            }
        } catch (e) {
            console.error("Error clearing logs:", e);
        }
    }
}

// ── Send Commands ───────────────────────────────────────────────────────────
async function sendCommand(type, inputId) {
    if (!selectedDeviceId) {
        alert("Please select a device from the sidebar first.");
        return;
    }
    
    const input = document.getElementById(inputId);
    const payload = input.value.trim();
    if (!payload) return;
    
    try {
        const response = await fetch(`/api/devices/${selectedDeviceId}/commands`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ CommandType: type, Payload: payload })
        });
        
        if (response.ok) {
            input.value = "";
            fetchCommandLogs(selectedDeviceId);
        }
    } catch (e) {
        console.error("Error sending command:", e);
    }
}

async function sendDirectCommand(type, payload) {
    if (!selectedDeviceId) {
        alert("Please select a device from the sidebar first.");
        return;
    }
    
    try {
        const response = await fetch(`/api/devices/${selectedDeviceId}/commands`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ CommandType: type, Payload: payload })
        });
        
        if (response.ok) {
            fetchCommandLogs(selectedDeviceId);
        }
    } catch (e) {
        console.error("Error sending command:", e);
    }
}

// ── Modal Outputs ───────────────────────────────────────────────────────────
function showOutputModal(type, encodedResult) {
    const rawText = decodeURIComponent(encodedResult);
    modalTitle.textContent = `${type} Result Output`;
    modalText.textContent = rawText;
    resultModal.style.display = "block";
}

function closeModal() {
    resultModal.style.display = "none";
}

window.onclick = function(event) {
    if (event.target === resultModal) {
        closeModal();
    }
}

// ── File Drag & Drop + APK Upload ───────────────────────────────────────────
const uploadZone = document.getElementById("upload-zone");
const fileInput = document.getElementById("apk-file-input");
const progressContainer = document.getElementById("upload-progress-container");
const progressBar = document.getElementById("upload-progress-bar");
const statusText = document.getElementById("upload-status-text");

uploadZone.addEventListener("click", () => fileInput.click());

uploadZone.addEventListener("dragover", (e) => {
    e.preventDefault();
    uploadZone.classList.add("dragover");
});

uploadZone.addEventListener("dragleave", () => {
    uploadZone.classList.remove("dragover");
});

uploadZone.addEventListener("drop", (e) => {
    e.preventDefault();
    uploadZone.classList.remove("dragover");
    const files = e.dataTransfer.files;
    if (files.length > 0) {
        handleApkUpload(files[0]);
    }
});

fileInput.addEventListener("change", (e) => {
    if (e.target.files.length > 0) {
        handleApkUpload(e.target.files[0]);
    }
});

function handleApkUpload(file) {
    if (!selectedDeviceId) {
        alert("Please select a device from the sidebar first.");
        return;
    }
    if (!file.name.endsWith(".apk")) {
        alert("Please select a valid Android package file (.apk).");
        return;
    }
    
    const formData = new FormData();
    formData.append("file", file);
    
    // Show progress UI
    progressContainer.style.display = "block";
    progressBar.style.width = "0%";
    statusText.textContent = "Uploading: 0%";
    
    const xhr = new XMLHttpRequest();
    xhr.open("POST", `/api/devices/${selectedDeviceId}/upload-apk`, true);
    
    xhr.upload.onprogress = (e) => {
        if (e.lengthComputable) {
            const pct = Math.round((e.loaded / e.total) * 100);
            progressBar.style.width = `${pct}%`;
            statusText.textContent = `Uploading: ${pct}%`;
        }
    };
    
    xhr.onload = () => {
        if (xhr.status === 200) {
            progressBar.style.width = "100%";
            statusText.textContent = "Upload successful! Enqueued InstallApk command.";
            setTimeout(() => {
                progressContainer.style.display = "none";
            }, 3000);
            fetchCommandLogs(selectedDeviceId);
        } else {
            statusText.textContent = `Upload failed: Status ${xhr.status}`;
        }
    };
    
    xhr.onerror = () => {
        statusText.textContent = "Upload encountered connection error.";
    };
    
    xhr.send(formData);
}

// ── Remote Controls ─────────────────────────────────────────────────────────
function changeBrightness(val) {
    document.getElementById("label-brightness").textContent = val;
    sendCommandDirectPayload("SetBrightness", val);
}

function changeVolume(val) {
    document.getElementById("label-volume").textContent = val;
    sendCommandDirectPayload("SetVolume", `media:${val}`);
}

function toggleWakeLockState() {
    const btn = document.getElementById("btn-toggle-wake");
    const active = btn.classList.contains("active");
    sendDirectCommand("KeepScreenAwake", active ? "false" : "true");
}

function launchApp(packageName) {
    sendDirectCommand("LaunchApp", packageName);
}

async function sendCommandDirectPayload(type, payload) {
    if (!selectedDeviceId) return;
    try {
        await fetch(`/api/devices/${selectedDeviceId}/commands`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ CommandType: type, Payload: payload })
        });
    } catch (e) {
        console.error("Error sending direct command:", e);
    }
}

async function handleLogout() {
    try {
        const response = await fetch("/api/logout", {
            method: "POST"
        });
        if (response.ok) {
            window.location.href = "/login.html";
        }
    } catch (e) {
        console.error("Error logging out:", e);
    }
}

// ── Startup Initialization ──────────────────────────────────────────────────
fetchDevices();
// Poll devices list every 2 seconds
setInterval(fetchDevices, 2000);
// Poll commands status logs every 3 seconds if device is selected
setInterval(() => {
    if (selectedDeviceId) {
        fetchCommandLogs(selectedDeviceId);
    }
}, 3000);
