package com.everyonesoftware.storage.browser.updates

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import java.io.File
import java.security.MessageDigest
import java.util.UUID

/** Only application binaries. Never binds to, opens, deletes or migrates the Service repository. */
internal class AndroidUpdateInstaller(context: Context, private val boundary: (String, Int) -> Unit = { _, _ -> }) {
 private val context = context.applicationContext
 private val pm = this.context.packageManager
 private val sessions get() = pm.packageInstaller
 internal val preferences = this.context.getSharedPreferences("application-updates", Context.MODE_PRIVATE)
 val directory = File(this.context.filesDir.canonicalFile, "application-updates")
 val sessionId get() = preferences.getInt("session", -1)
 fun installed(packageName: String) = identity(packageName, null)
 @Suppress("DEPRECATION")
 private fun identity(packageName: String, file: File?): ApkIdentity {
  check(packageName == SERVICE_PACKAGE || packageName == BROWSER_PACKAGE)
  // Android 9/10's archive parser only collects certificates when GET_SIGNATURES is also
  // requested. Still compare apkContentsSigners (never certificate history) on API 28+.
  val flags = PackageManager.GET_SIGNATURES or if (Build.VERSION.SDK_INT >= 28) PackageManager.GET_SIGNING_CERTIFICATES else 0
  val info = requireNotNull(if (file == null) pm.getPackageInfo(packageName, flags) else pm.getPackageArchiveInfo(file.path, flags))
  val signers = if (Build.VERSION.SDK_INT >= 28) info.signingInfo?.apkContentsSigners else info.signatures
  return ApkIdentity(info.packageName, if (Build.VERSION.SDK_INT >= 28) info.longVersionCode else info.versionCode.toLong(), info.versionName,
   requireNotNull(signers).map { ReleaseClient.hex(it.toByteArray()) }, requireNotNull(info.applicationInfo).flags and ApplicationInfo.FLAG_DEBUGGABLE != 0)
 }
 private fun file(release: AndroidRelease, packageName: String): Pair<ReleaseFile, File> {
  val label = if (packageName == SERVICE_PACKAGE) "Service" else "Browser"
  val entry = release.apks.single { it.name == "Storage-$label-${release.version}.apk" }
  val expected = File(directory, entry.name)
  check(expected.canonicalFile == expected && directory.canonicalFile == directory)
  return entry to expected
 }
 fun verify(release: AndroidRelease) {
  check(release.count in 1..65534 && release.apks.size == 2)
  val service = installed(SERVICE_PACKAGE); val browser = installed(BROWSER_PACKAGE)
  UpdatePolicy.plan(Build.VERSION.SDK_INT, release.count, service, browser)
  for (pkg in listOf(SERVICE_PACKAGE, BROWSER_PACKAGE)) {
   val (entry, apk) = file(release, pkg)
   check(apk.isFile && apk.length() == entry.bytes && entry.bytes in 1..512L * 1024 * 1024)
   val digest = MessageDigest.getInstance("SHA-256")
   apk.inputStream().use { input -> val buffer = ByteArray(32 * 1024); while (true) { val n = input.read(buffer); if (n < 0) break; digest.update(buffer, 0, n) } }
   check(ReleaseClient.hex(digest.digest()) == entry.sha256)
   UpdatePolicy.verify(release.count, pkg, if (pkg == SERVICE_PACKAGE) service else browser, identity(pkg, apk))
  }
  // PackageManager reports current certificate identity, not a replacement for the OS's final
  // cryptographic APK verification. Hashes bind bytes; Android verifies signatures at commit.
 }
 fun canInstall() = pm.canRequestPackageInstalls()
 fun permissionIntent() = Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}"))
 fun confirmationIntent(): Intent? = preferences.getString("confirmation", null)?.let { Intent.parseUri(it, Intent.URI_INTENT_SCHEME) }
 fun prepare(release: AndroidRelease): Int {
  check(sessionId == -1) { "An install is already in progress" }
  verify(release)
  val plan = UpdatePolicy.plan(Build.VERSION.SDK_INT, release.count, installed(SERVICE_PACKAGE), installed(BROWSER_PACKAGE))
  check(plan.isNotEmpty()) { "Both products are already installed" }
  val token = UUID.randomUUID().toString()
  val marker = "Storage update $token"
  // Persist ownership BEFORE creation. The marker closes createSession -> ID journal's crash gap;
  // only our installer package AND this random attempt marker can discover an unjournaled ID.
  check(preferences.edit().putString("token", token).putString("phase", "preparing")
   .putStringSet("sessions", emptySet()).remove("confirmation").remove("result").commit())
  val created = mutableSetOf<String>()
  try {
   fun params() = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
    setAppLabel(marker)
    if (Build.VERSION.SDK_INT >= 31) setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_REQUIRED)
   }
   fun createRecorded(params: PackageInstaller.SessionParams): Int {
    val id = sessions.createSession(params)
    boundary("created", id)
    created.add(id.toString())
    val edit = preferences.edit().putStringSet("sessions", created.toSet())
    if (sessionId == -1) edit.putInt("session", id)
    check(edit.commit()) // durable exact ID before opening/writing/linking this session
    boundary("journaled", id)
    return id
   }
   for (pkg in plan) {
    val (entry, apk) = file(release, pkg)
    val id = createRecorded(params().apply { setAppPackageName(pkg); setSize(entry.bytes) })
    sessions.openSession(id).use { session ->
     session.openWrite("base.apk", 0, entry.bytes).use { output ->
      apk.inputStream().use { it.copyTo(output) }
      boundary("writing", id)
      session.fsync(output)
     }
    }
    boundary("written", id)
   }
   val root = created.single().toInt()
   check(preferences.edit().putString("phase", "prepared").commit())
   boundary("prepared", root)
   return root
  } catch (e: Exception) { cancel(); throw e }
 }
 fun commit() {
  check(canInstall()) { "Allow installs from Storage in Android Settings first" }
  val id = sessionId; check(id != -1 && sessions.getSessionInfo(id) != null)
  val callback = Intent(context, UpdateResultReceiver::class.java).setAction("${context.packageName}.UPDATE_RESULT")
   .setData(Uri.parse("storage-update:$id/${preferences.getString("token", "")}"))
   .putExtra("ownedSession", id).putExtra("token", preferences.getString("token", ""))
  val flags = PendingIntent.FLAG_UPDATE_CURRENT or if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0
  val result = PendingIntent.getBroadcast(context, id, callback, flags)
  check(preferences.getString("phase", null) == "prepared")
  check(preferences.edit().putString("phase", "committing").commit())
  boundary("committing", id)
  sessions.openSession(id).use { it.commit(result.intentSender) }
  boundary("committed", id)
  // A pending-user-action callback can arrive before commit returns. Do not overwrite its data.
  if (sessionId == id) check(preferences.edit().putString("phase", "committed").commit())
 }
 private fun ownedSessions(): List<PackageInstaller.SessionInfo> {
  val ids = preferences.getStringSet("sessions", emptySet()).orEmpty().mapNotNull { it.toIntOrNull() }.toSet() + sessionId
  val marker = preferences.getString("token", null)?.let { "Storage update $it" }
  // getMySessions omits attached children on API29. Resolve every journaled ID explicitly.
  val found = sessions.mySessions + ids.filter { it != -1 }.mapNotNull { sessions.getSessionInfo(it) }
  return found.distinctBy { it.sessionId }.filter {
   it.installerPackageName == context.packageName &&
    (it.sessionId in ids || marker != null && it.appLabel?.toString() == marker)
  }
 }
 fun cancel() {
  val owned = ownedSessions().sortedBy { if (it.sessionId == sessionId) 0 else 1 }
  // Android10's non-staged abandon does NOT cascade to children (despite the API contract).
  // Detach our exact recorded children while the root still exists, then abandon each one.
  if (Build.VERSION.SDK_INT == 29) {
   owned.filter { it.isMultiPackage }.forEach { root ->
    sessions.openSession(root.sessionId).use { parent ->
     root.childSessionIds.filter { child -> owned.any { it.sessionId == child } }.forEach { parent.removeChildSessionId(it) }
    }
   }
  }
  owned.forEach { if (sessions.getSessionInfo(it.sessionId) != null) runCatching { sessions.abandonSession(it.sessionId) } }
  // Never lose the journal on cleanup failure: a later explicit retry must still find our sessions.
  val deadline = System.nanoTime() + 3_000_000_000L
  while (ownedSessions().isNotEmpty() && System.nanoTime() < deadline) Thread.sleep(25)
  check(ownedSessions().isEmpty()) { "Android has not released this update's installer sessions. Retry cancellation." }
  check(preferences.edit().remove("session").remove("sessions").remove("phase").remove("token").remove("confirmation")
   .putString("result", "Installation cancelled or interrupted. Downloaded files can be retried.").commit())
 }
 fun reconcile() {
  if (sessionId == -1 && preferences.getString("phase", null) == null && preferences.getString("token", null) == null) return
  val info = sessions.getSessionInfo(sessionId)
  val keep = UpdateRecovery.keep(preferences.getString("phase", null), info != null,
   info?.isSealed == true, preferences.contains("confirmation"))
  if (!keep) cancel()
 }
}

/** Explicit, non-exported mutable callback. Never launches or accepts the system consent screen. */
class UpdateResultReceiver : BroadcastReceiver() {
 @Suppress("DEPRECATION")
 override fun onReceive(context: Context, intent: Intent) {
  val installer = AndroidUpdateInstaller(context)
  val prefs = installer.preferences
  val id = installer.sessionId
  if (id == -1 || intent.getIntExtra("ownedSession", -2) != id || intent.getStringExtra("token") != prefs.getString("token", null)) return
  val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
  if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
   val confirmation = intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT) ?: return
   prefs.edit().putString("confirmation", confirmation.toUri(Intent.URI_INTENT_SCHEME)).putString("result", "Android confirmation required. Tap Continue installation.").commit()
  } else {
   val text = when (status) {
    PackageInstaller.STATUS_SUCCESS -> "Android reports installation complete. Installed versions have been refreshed."
    PackageInstaller.STATUS_FAILURE_ABORTED -> "Installation cancelled. You can retry."
    else -> "Android could not install this update (status $status). You can retry."
   }
   // Some Android10 builds remove a failed atomic root but strand its attached children.
   // Keep the exact journal on cleanup failure; do not turn an OS failure into a receiver crash.
   val cleaned = runCatching { installer.cancel() }.isSuccess
   prefs.edit().putInt("status", status).remove("confirmation").putString("statusMessage", intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE))
    .putString("result", text + if (cleaned) "" else " Android has not released the recorded installer sessions; cancellation cleanup is still pending.").commit()
  }
 }
}
