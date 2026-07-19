package net.barrer.devicemanager

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import java.io.File
import java.io.FileInputStream

object PackageInstallerHelper {

    fun installApk(context: Context, file: File): Boolean {
        // Try root shell installation first (instant silent install if rooted)
        if (installApkWithRoot(file)) {
            return true
        }

        // Try Device Owner Session silent install (standard MDM silent install)
        try {
            val packageInstaller = context.packageManager.packageInstaller
            val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
            val sessionId = packageInstaller.createSession(params)
            val session = packageInstaller.openSession(sessionId)

            val out = session.openWrite("package_install_session", 0, -1)
            val input = FileInputStream(file)
            val buffer = ByteArray(65536)
            var n: Int
            while (input.read(buffer).also { n = it } != -1) {
                out.write(buffer, 0, n)
            }
            session.fsync(out)
            input.close()
            out.close()

            // Create receiver intent for status callback
            val intent = Intent(context, PackageInstallRcvr::class.java).apply {
                action = "${context.packageName}.ACTION_INSTALL_STATUS"
            }
            val pendingIntent = PendingIntent.getBroadcast(
                context,
                sessionId,
                intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE
            )
            session.commit(pendingIntent.intentSender)
            session.close()
            return true
        } catch (e: Exception) {
            e.printStackTrace()
        }

        // Fall back to standard User-Prompt Intent installation (requires user manual click)
        return installApkWithIntent(context, file)
    }

    private fun installApkWithRoot(file: File): Boolean {
        try {
            val process = Runtime.getRuntime().exec(arrayOf("su", "-c", "pm install -r \"${file.absolutePath}\""))
            val exitCode = process.waitFor()
            return exitCode == 0
        } catch (e: Exception) {
            return false
        }
    }

    private fun installApkWithIntent(context: Context, file: File): Boolean {
        try {
            val authority = "${context.packageName}.fileprovider"
            val uri = androidx.core.content.FileProvider.getUriForFile(context, authority, file)
            val intent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, "application/vnd.android.package-archive")
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            context.startActivity(intent)
            return true
        } catch (e: Exception) {
            e.printStackTrace()
            return false
        }
    }

    fun uninstallPackage(context: Context, packageName: String): Boolean {
        // Try root shell uninstallation first (instant silent uninstall if rooted)
        if (uninstallPackageWithRoot(packageName)) {
            return true
        }

        // Try Device Owner silent uninstall (standard MDM silent uninstall)
        try {
            val packageInstaller = context.packageManager.packageInstaller
            val intent = Intent(context, PackageInstallRcvr::class.java).apply {
                action = "${context.packageName}.ACTION_INSTALL_STATUS"
            }
            val pendingIntent = PendingIntent.getBroadcast(
                context,
                0,
                intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE
            )
            packageInstaller.uninstall(packageName, pendingIntent.intentSender)
            return true
        } catch (e: Exception) {
            e.printStackTrace()
        }

        // Fall back to standard User-Prompt intent uninstallation (requires manual confirmation)
        try {
            val intent = Intent(Intent.ACTION_DELETE).apply {
                data = android.net.Uri.parse("package:$packageName")
                putExtra(Intent.EXTRA_RETURN_RESULT, true)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(intent)
            return true
        } catch (e: Exception) {
            e.printStackTrace()
            return false
        }
    }

    private fun uninstallPackageWithRoot(packageName: String): Boolean {
        try {
            val process = Runtime.getRuntime().exec(arrayOf("su", "-c", "pm uninstall \"$packageName\""))
            val exitCode = process.waitFor()
            return exitCode == 0
        } catch (e: Exception) {
            return false
        }
    }
}
