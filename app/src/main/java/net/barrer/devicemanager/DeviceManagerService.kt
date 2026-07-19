package net.barrer.devicemanager

import android.app.*
import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.media.AudioManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.os.*
import android.provider.Settings
import android.speech.tts.TextToSpeech
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.HttpURLConnection
import java.net.URL
import java.util.*

class DeviceManagerService : Service(), TextToSpeech.OnInitListener {

    private var isRunning = false
    private var serviceThread: Thread? = null
    private var tts: TextToSpeech? = null
    private var ttsReady = false
    private lateinit var deviceId: String
    private lateinit var serverUrl: String

    companion object {
        const val CHANNEL_ID = "DeviceManagerServiceChannel"
        const val NOTIFICATION_ID = 9999
        private const val TAG = "DeviceManagerService"

        fun startService(context: Context, serverUrl: String) {
            val intent = Intent(context, DeviceManagerService::class.java).apply {
                putExtra("server_url", serverUrl)
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }

        fun stopService(context: Context) {
            val intent = Intent(context, DeviceManagerService::class.java)
            context.stopService(intent)
        }
    }

    override fun onCreate() {
        super.onCreate()
        tts = TextToSpeech(this, this)
        
        // Retrieve or generate unique device ID
        deviceId = Settings.Secure.getString(contentResolver, Settings.Secure.ANDROID_ID) ?: UUID.randomUUID().toString()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        serverUrl = intent?.getStringExtra("server_url") ?: "http://10.251.0.1:18800"
        
        // Save to preferences for auto-restart
        val prefs = getSharedPreferences("dev_manager_prefs", Context.MODE_PRIVATE)
        prefs.edit().putString("server_url", serverUrl).putBoolean("service_running", true).apply()

        createNotificationChannel()
        val notification = createNotification()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(
                NOTIFICATION_ID,
                notification,
                android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
            )
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }

        if (!isRunning) {
            isRunning = true
            serviceThread = Thread { runLoop() }.apply { start() }
        }

        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onInit(status: Int) {
        if (status == TextToSpeech.SUCCESS) {
            tts?.language = Locale.US
            ttsReady = true
        }
    }

    private fun areNotificationsFullyEnabled(): Boolean {
        val manager = NotificationManagerCompat.from(this)
        if (!manager.areNotificationsEnabled()) return false
        
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = manager.getNotificationChannel(CHANNEL_ID)
            if (channel != null && channel.importance == NotificationManager.IMPORTANCE_NONE) {
                return false
            }
        }
        return true
    }

    private fun runLoop() {
        var lastStatusTime = 0L

        while (isRunning) {
            // Guardrail: Shut down if the administrator notification is disabled/hidden
            if (!areNotificationsFullyEnabled()) {
                Log.e(TAG, "Notification permission is disabled! Aborting sync loop.")
                stopSelf()
                break
            }

            val now = System.currentTimeMillis()
            
            // 1. Heartbeat & Registration (every 10s)
            if (now - lastStatusTime >= 10000) {
                registerDevice()
                reportStatus()
                lastStatusTime = now
            }

            // 2. Poll Pending Commands (every 3s)
            pollCommands()

            try {
                Thread.sleep(3000)
            } catch (e: InterruptedException) {
                break
            }
        }
    }

    // ── HTTP API Actions ────────────────────────────────────────────────────────

    private fun registerDevice() {
        val json = JSONObject().apply {
            put("Id", deviceId)
            put("Name", Build.MANUFACTURER + " " + Build.MODEL)
            put("Model", Build.MODEL)
            put("AndroidVersion", Build.VERSION.RELEASE)
        }.toString()
        sendJsonPost("$serverUrl/api/devices/register", json)
    }

    private fun getLocalIpAddress(): String {
        try {
            val interfaces = java.net.NetworkInterface.getNetworkInterfaces()
            while (interfaces.hasMoreElements()) {
                val networkInterface = interfaces.nextElement()
                val addresses = networkInterface.inetAddresses
                while (addresses.hasMoreElements()) {
                    val address = addresses.nextElement()
                    if (!address.isLoopbackAddress && address is java.net.Inet4Address) {
                        return address.hostAddress ?: "None"
                    }
                }
            }
        } catch (e: Exception) {}
        return "None"
    }

    private fun getConnectionDetails(): Pair<String, Int> {
        try {
            val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                val activeNetwork = cm.activeNetwork ?: return Pair("None", -1)
                val caps = cm.getNetworkCapabilities(activeNetwork) ?: return Pair("None", -1)
                
                val connType = when {
                    caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "WiFi"
                    caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "Cellular"
                    caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "Ethernet"
                    else -> "None"
                }

                var signalStrength = -1
                if (connType == "WiFi") {
                    val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
                    @Suppress("DEPRECATION")
                    val wifiInfo = wm.connectionInfo
                    if (wifiInfo != null) {
                        @Suppress("DEPRECATION")
                        signalStrength = WifiManager.calculateSignalLevel(wifiInfo.rssi, 100)
                    }
                }
                return Pair(connType, signalStrength)
            } else {
                @Suppress("DEPRECATION")
                val activeInfo = cm.activeNetworkInfo
                if (activeInfo == null || !activeInfo.isConnected) return Pair("None", -1)
                @Suppress("DEPRECATION")
                val connType = when (activeInfo.type) {
                    ConnectivityManager.TYPE_WIFI -> "WiFi"
                    ConnectivityManager.TYPE_MOBILE -> "Cellular"
                    ConnectivityManager.TYPE_ETHERNET -> "Ethernet"
                    else -> "None"
                }
                
                var signalStrength = -1
                if (connType == "WiFi") {
                    val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
                    @Suppress("DEPRECATION")
                    val wifiInfo = wm.connectionInfo
                    if (wifiInfo != null) {
                        @Suppress("DEPRECATION")
                        signalStrength = WifiManager.calculateSignalLevel(wifiInfo.rssi, 100)
                    }
                }
                return Pair(connType, signalStrength)
            }
        } catch (e: Exception) {
            return Pair("None", -1)
        }
    }

    private fun reportStatus() {
        // Battery
        val batteryManager = getSystemService(Context.BATTERY_SERVICE) as BatteryManager
        val battery = batteryManager.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)

        // Gather all storage volumes
        val storageVolumes = JSONArray()
        val dirs = getExternalFilesDirs(null)
        var primaryUsed = 0L
        var primaryTotal = 0L

        for (dir in dirs) {
            if (dir != null) {
                try {
                    val stat = StatFs(dir.path)
                    val total = stat.blockCountLong * stat.blockSizeLong
                    val available = stat.availableBlocksLong * stat.blockSizeLong
                    val used = total - available
                    
                    val pathLower = dir.path.lowercase()
                    val name = when {
                        pathLower.contains("emulated") || pathLower.contains("primary") -> "Internal Storage"
                        pathLower.contains("sd") -> "SD Card"
                        pathLower.contains("usb") || pathLower.contains("otg") -> "USB Storage"
                        else -> "External Storage"
                    }
                    
                    if (name == "Internal Storage") {
                        primaryUsed = used
                        primaryTotal = total
                    }

                    val vol = JSONObject().apply {
                        put("Name", name)
                        put("Used", used)
                        put("Total", total)
                    }
                    storageVolumes.put(vol)
                } catch (e: Exception) {
                    // Ignore transient StatFs failures for detached volumes
                }
            }
        }

        // Fallback for primary used/total if not found via emulated path
        if (primaryTotal == 0L) {
            val stat = StatFs(Environment.getDataDirectory().path)
            primaryTotal = stat.blockCountLong * stat.blockSizeLong
            val available = stat.availableBlocksLong * stat.blockSizeLong
            primaryUsed = primaryTotal - available
            
            // Add internal volume to list if empty
            if (storageVolumes.length() == 0) {
                storageVolumes.put(JSONObject().apply {
                    put("Name", "Internal Storage")
                    put("Used", primaryUsed)
                    put("Total", primaryTotal)
                })
            }
        }

        // Installed apps package names
        val pm = packageManager
        val packages = pm.getInstalledPackages(0)
        val appList = JSONArray()
        for (pkg in packages) {
            // Filter to user apps mainly
            val isSystem = pkg.applicationInfo?.let { (it.flags and android.content.pm.ApplicationInfo.FLAG_SYSTEM) != 0 } ?: false
            if (!isSystem || pkg.packageName.contains("google") || pkg.packageName.contains("android")) {
                val prefix = if (isSystem) "[SYS]" else ""
                appList.put(prefix + pkg.packageName)
            }
        }

        // RAM Info
        val actManager = getSystemService(Context.ACTIVITY_SERVICE) as ActivityManager
        val memInfo = ActivityManager.MemoryInfo()
        actManager.getMemoryInfo(memInfo)
        val totalRam = memInfo.totalMem
        val availRam = memInfo.availMem

        // CPU Arch
        val cpuArch = Build.SUPPORTED_ABIS.firstOrNull() ?: "unknown"

        // Uptime
        val uptime = SystemClock.elapsedRealtime() / 1000

        // Display Resolution
        val metrics = resources.displayMetrics
        val resolution = "${metrics.widthPixels}x${metrics.heightPixels}"

        // Connection Details
        val (connType, wifiSignal) = getConnectionDetails()
        val ipAddress = getLocalIpAddress()

        val systemInfo = JSONObject().apply {
            put("IpAddress", ipAddress)
            put("ConnectionType", connType)
            put("WifiSignal", wifiSignal)
            put("TotalRam", totalRam)
            put("AvailableRam", availRam)
            put("CpuArch", cpuArch)
            put("Uptime", uptime)
            put("Resolution", resolution)
            put("KeepScreenAwake", isWakeLockActive)
        }

        val json = JSONObject().apply {
            put("Battery", battery)
            put("StorageUsed", primaryUsed)
            put("StorageTotal", primaryTotal)
            put("AppList", appList)
            put("StorageVolumes", storageVolumes)
            put("SystemInfo", systemInfo)
        }.toString()

        sendJsonPost("$serverUrl/api/devices/$deviceId/status", json)
    }

    private fun pollCommands() {
        var conn: HttpURLConnection? = null
        try {
            val url = URL("$serverUrl/api/devices/$deviceId/commands/pending")
            conn = url.openConnection() as HttpURLConnection
            conn.requestMethod = "GET"
            conn.connectTimeout = 5000
            conn.readTimeout = 5000

            if (conn.responseCode == HttpURLConnection.HTTP_OK) {
                val responseText = conn.inputStream.bufferedReader().use { it.readText() }
                val json = JSONObject(responseText)
                val commandId = json.getString("commandId")
                val commandType = json.getString("commandType")
                val payload = json.getString("payload")

                Log.d(TAG, "Executing command: $commandType ($commandId)")
                val success = executeCommand(commandType, payload)
                reportCommandCompletion(commandId, success)
            }
        } catch (e: Exception) {
            // Silence noise
        } finally {
            conn?.disconnect()
        }
    }

    private fun reportCommandCompletion(commandId: String, success: Boolean) {
        val json = JSONObject().apply {
            put("Success", success)
        }.toString()
        sendJsonPost("$serverUrl/api/devices/$deviceId/commands/$commandId/complete", json)
    }

    private fun sendJsonPost(urlStr: String, json: String): Boolean {
        var conn: HttpURLConnection? = null
        try {
            val url = URL(urlStr)
            conn = url.openConnection() as HttpURLConnection
            conn.requestMethod = "POST"
            conn.connectTimeout = 5000
            conn.readTimeout = 5000
            conn.doOutput = true
            conn.setRequestProperty("Content-Type", "application/json")

            OutputStreamWriter(conn.outputStream).use { it.write(json) }
            val responseCode = conn.responseCode
            
            // Drain response stream to allow clean TCP reuse/close and avoid Kestrel connection reset warnings
            if (responseCode in 200..299) {
                conn.inputStream.bufferedReader().use { it.readText() }
            } else {
                conn.errorStream?.bufferedReader()?.use { it.readText() }
            }
            return responseCode in 200..299
        } catch (e: Exception) {
            Log.e(TAG, "POST error to $urlStr: ${e.message}")
            return false
        } finally {
            conn?.disconnect()
        }
    }

    // ── Command Executor ────────────────────────────────────────────────────────

    private fun executeCommand(type: String, payload: String): Boolean {
        return when (type) {
            "SendNotification" -> {
                showStatusBarNotification(payload)
                true
            }
            "SpeakText" -> {
                speakOut(payload)
                true
            }
            "Vibrate" -> {
                val duration = payload.toLongOrNull() ?: 1000
                vibrateDevice(duration)
                true
            }
            "Ping" -> {
                true
            }
            "InstallApk" -> {
                downloadAndInstallApk(payload)
            }
            "LockDevice" -> {
                lockDevice()
            }
            "WipeDevice" -> {
                wipeDevice()
            }
            "UninstallPackage" -> {
                PackageInstallerHelper.uninstallPackage(this, payload)
            }
            "SetVolume" -> {
                setVolume(payload)
            }
            "SetBrightness" -> {
                setBrightness(payload)
            }
            "KeepScreenAwake" -> {
                toggleWakeLock(payload.lowercase() == "true")
            }
            "LaunchApp" -> {
                launchApp(payload)
            }
            else -> {
                Log.w(TAG, "Unknown command type: $type")
                false
            }
        }
    }

    private fun setVolume(payload: String): Boolean {
        return try {
            val parts = payload.split(":")
            val streamTypeStr = parts.getOrNull(0)?.trim()?.lowercase() ?: "media"
            val level = parts.getOrNull(1)?.trim()?.toIntOrNull() ?: 0
            
            val audioManager = getSystemService(Context.AUDIO_SERVICE) as AudioManager
            val streamType = when (streamTypeStr) {
                "media" -> AudioManager.STREAM_MUSIC
                "alarm" -> AudioManager.STREAM_ALARM
                "ring" -> AudioManager.STREAM_RING
                "notification" -> AudioManager.STREAM_NOTIFICATION
                "system" -> AudioManager.STREAM_SYSTEM
                else -> AudioManager.STREAM_MUSIC
            }
            
            val maxVolume = audioManager.getStreamMaxVolume(streamType)
            val targetLevel = level.coerceIn(0, maxVolume)
            
            audioManager.setStreamVolume(streamType, targetLevel, AudioManager.FLAG_SHOW_UI)
            Log.d(TAG, "Volume set for stream $streamTypeStr to $targetLevel/$maxVolume")
            true
        } catch (e: Exception) {
            Log.e(TAG, "Error setting volume: ${e.message}")
            false
        }
    }

    private fun setBrightness(payload: String): Boolean {
        return try {
            val brightnessVal = payload.toIntOrNull() ?: 128
            val targetBrightness = brightnessVal.coerceIn(0, 255)
            
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                if (Settings.System.canWrite(this)) {
                    Settings.System.putInt(contentResolver, Settings.System.SCREEN_BRIGHTNESS, targetBrightness)
                    Log.d(TAG, "Screen brightness set to $targetBrightness")
                    true
                } else {
                    Log.w(TAG, "Cannot write settings: Permission not granted.")
                    val intent = Intent(Settings.ACTION_MANAGE_WRITE_SETTINGS).apply {
                        data = android.net.Uri.parse("package:$packageName")
                        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    }
                    startActivity(intent)
                    false
                }
            } else {
                Settings.System.putInt(contentResolver, Settings.System.SCREEN_BRIGHTNESS, targetBrightness)
                true
            }
        } catch (e: Exception) {
            Log.e(TAG, "Error setting brightness: ${e.message}")
            false
        }
    }

    private var wakeLock: PowerManager.WakeLock? = null
    private var isWakeLockActive = false

    private fun toggleWakeLock(enable: Boolean): Boolean {
        return try {
            val powerManager = getSystemService(Context.POWER_SERVICE) as PowerManager
            if (enable) {
                if (wakeLock == null) {
                    @Suppress("DEPRECATION")
                    wakeLock = powerManager.newWakeLock(
                        PowerManager.SCREEN_BRIGHT_WAKE_LOCK or PowerManager.ACQUIRE_CAUSES_WAKEUP,
                        "DeviceManager:WakeLock"
                    )
                }
                if (wakeLock?.isHeld == false) {
                    wakeLock?.acquire()
                }
                isWakeLockActive = true
                Log.d(TAG, "Wake lock acquired.")
            } else {
                if (wakeLock?.isHeld == true) {
                    wakeLock?.release()
                }
                wakeLock = null
                isWakeLockActive = false
                Log.d(TAG, "Wake lock released.")
            }
            true
        } catch (e: Exception) {
            Log.e(TAG, "Error toggling wake lock: ${e.message}")
            false
        }
    }

    private fun launchApp(packageName: String): Boolean {
        return try {
            val launchIntent = packageManager.getLaunchIntentForPackage(packageName)
            if (launchIntent != null) {
                launchIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                startActivity(launchIntent)
                Log.d(TAG, "Launched application: $packageName")
                true
            } else {
                Log.w(TAG, "No launch intent found for: $packageName")
                false
            }
        } catch (e: Exception) {
            Log.e(TAG, "Error launching app $packageName: ${e.message}")
            false
        }
    }

    private fun showStatusBarNotification(text: String) {
        val notificationManager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        val notif = NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_notify_chat)
            .setContentTitle("Device Manager Host Alert")
            .setContentText(text)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .build()
        notificationManager.notify(Random().nextInt(), notif)
    }

    private fun speakOut(text: String) {
        if (ttsReady && tts != null) {
            tts?.speak(text, TextToSpeech.QUEUE_FLUSH, null, null)
        }
    }

    private fun vibrateDevice(duration: Long) {
        try {
            @Suppress("DEPRECATION")
            val vibrator = getSystemService(Context.VIBRATOR_SERVICE) as Vibrator
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                val audioAttributes = android.media.AudioAttributes.Builder()
                    .setContentType(android.media.AudioAttributes.CONTENT_TYPE_SONIFICATION)
                    .setUsage(android.media.AudioAttributes.USAGE_ALARM)
                    .build()
                @Suppress("DEPRECATION")
                vibrator.vibrate(
                    VibrationEffect.createOneShot(duration, VibrationEffect.DEFAULT_AMPLITUDE),
                    audioAttributes
                )
            } else {
                @Suppress("DEPRECATION")
                vibrator.vibrate(duration)
            }
            Log.d(TAG, "Vibration triggered for ${duration}ms")
        } catch (e: Exception) {
            Log.e(TAG, "Vibrate exception: ${e.message}")
        }
    }

    private fun downloadAndInstallApk(urlStr: String): Boolean {
        try {
            val outputDir = getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS) ?: cacheDir
            val tempFile = File(outputDir, "downloaded_pkg.apk")
            if (tempFile.exists()) tempFile.delete()

            Log.d(TAG, "Downloading APK from: $urlStr")
            val downloadUrl = URL(urlStr)
            val connection = downloadUrl.openConnection() as HttpURLConnection
            connection.connectTimeout = 30000
            connection.readTimeout = 30000
            connection.connect()

            if (connection.responseCode != HttpURLConnection.HTTP_OK) {
                return false
            }

            connection.inputStream.use { input ->
                tempFile.outputStream().use { output ->
                    input.copyTo(output)
                }
            }

            Log.d(TAG, "APK Download completed. Launching silent installer...")
            return PackageInstallerHelper.installApk(this, tempFile)
        } catch (e: Exception) {
            e.printStackTrace()
            return false
        }
    }

    private fun lockDevice(): Boolean {
        try {
            val dpm = getSystemService(Context.DEVICE_POLICY_SERVICE) as DevicePolicyManager
            val adminComponent = ComponentName(this, DeviceAdminRcvr::class.java)
            if (dpm.isAdminActive(adminComponent)) {
                dpm.lockNow()
                return true
            }
            Log.w(TAG, "Cannot lock: Device Admin is not active.")
            return false
        } catch (e: Exception) {
            e.printStackTrace()
            return false
        }
    }

    private fun wipeDevice(): Boolean {
        try {
            val dpm = getSystemService(Context.DEVICE_POLICY_SERVICE) as DevicePolicyManager
            val adminComponent = ComponentName(this, DeviceAdminRcvr::class.java)
            if (dpm.isAdminActive(adminComponent)) {
                dpm.wipeData(0)
                return true
            }
            Log.w(TAG, "Cannot wipe: Device Admin is not active.")
            return false
        } catch (e: Exception) {
            e.printStackTrace()
            return false
        }
    }

    // ── Service Lifecycles ──────────────────────────────────────────────────────

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "Device Manager Service Channel",
                NotificationManager.IMPORTANCE_DEFAULT
            )
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(channel)
        }
    }

    private fun createNotification(): Notification {
        val title = "Device Manager Client Active"
        val subtitle = "Connected to host: $serverUrl"
        
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle(title)
            .setContentText(subtitle)
            .setSmallIcon(android.R.drawable.ic_menu_compass)
            .setPriority(NotificationCompat.PRIORITY_DEFAULT)
            .setOngoing(true)
            .build()
    }

    override fun onDestroy() {
        super.onDestroy()
        isRunning = false
        serviceThread?.interrupt()
        
        tts?.stop()
        tts?.shutdown()
        
        val prefs = getSharedPreferences("dev_manager_prefs", Context.MODE_PRIVATE)
        prefs.edit().putBoolean("service_running", false).apply()
    }
}
