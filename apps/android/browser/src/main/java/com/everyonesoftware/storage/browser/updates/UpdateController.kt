package com.everyonesoftware.storage.browser.updates

import android.content.Context
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

internal data class UpdateState(
 val release: AndroidRelease? = null, val busy: Boolean = false, val ready: Boolean = false,
 val received: Long = 0, val total: Long = 0, val message: String = "Check public Android releases from GitHub.",
 val browser: String = "Browser version unavailable", val service: String = "Service version unavailable",
 val nextPackage: String? = null, val session: Boolean = false, val confirmation: Boolean = false, val permission: Boolean = false,
 val cleanupPending: Boolean = false
)

/** Finalization is separate so the real cancellation boundary can be exercised without Android. */
internal fun finishUpdateOperation(gate: UpdateGate, state: MutableStateFlow<UpdateState>, cancel: () -> Unit) {
 try {
  if (gate.cancelled) {
   cancel()
   state.update { it.copy(session = false, confirmation = false, ready = false, cleanupPending = false, message = "Update cancelled. Installed versions are unchanged unless Android already completed an approved install.") }
  }
 } catch (_: Exception) {
  // cancel() owns the durable journal. Do not clear it or claim sessions were released.
  state.update { it.copy(confirmation = false, ready = false, cleanupPending = true,
   message = "Update cancellation cleanup is pending. Tap Cancel update to retry. Recorded installer ownership is retained; installed versions may already have changed after an approved install.") }
 } finally {
  try { state.update { it.copy(busy = false) } } finally { gate.leave() }
 }
}

/** Process-owned work survives Activity recreation. Private preferences/session IDs survive process death.
 * Interrupted downloads are never resumed as trusted bytes: retry discards partials and revalidates both APKs.
 */
internal class UpdateController(context: Context, private val client: ReleaseClient = ReleaseClient()) {
 val installer = AndroidUpdateInstaller(context)
 private val prefs = installer.preferences
 private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
 private val gate = UpdateGate()
 private val mutable = MutableStateFlow(UpdateState())
 val state = mutable.asStateFlow()
 private fun operation(showBusy: Boolean = true, cleanupRetry: Boolean = false, action: () -> Unit) {
  if (!gate.enter()) return
  if (mutable.value.cleanupPending && !cleanupRetry) { gate.leave(); return }
  if (showBusy) mutable.update { it.copy(busy = true) }
  scope.launch {
   try { action() }
   catch (_: java.util.concurrent.CancellationException) { mutable.update { it.copy(message = "Update cancelled. You can retry.", ready = false) } }
   catch (e: SigningIdentityMismatch) { mutable.update { it.copy(message = e.message.orEmpty(), ready = false) } }
   catch (_: Exception) { mutable.update { it.copy(message = if (gate.cancelled) "Update cancelled. You can retry." else "Update failed. Check your connection, available space and package signing identity, then retry. No repository data was changed.", ready = false) } }
   finally { finishUpdateOperation(gate, mutable, installer::cancel) }
  }
 }
 fun refresh(showBusy: Boolean = true) = operation(showBusy) {
  installer.reconcile()
  val browser = installer.installed(BROWSER_PACKAGE); val service = installer.installed(SERVICE_PACKAGE)
  val raw = prefs.getString("release", null)
  val release = raw?.let { ReleaseClient.parse(it) }
  val ready = installer.sessionId == -1 && release != null && runCatching { installer.verify(release) }.isSuccess &&
   (release.count > browser.code || release.count > service.code)
  mutable.update { it.copy(release = release, ready = ready, browser = "Browser ${browser.version ?: "unknown"} (${browser.code})", service = "Service ${service.version ?: "unknown"} (${service.code})",
   nextPackage = release?.let { runCatching { UpdatePolicy.plan(android.os.Build.VERSION.SDK_INT, it.count, service, browser).firstOrNull() }.getOrNull() },
   permission = installer.canInstall(), session = installer.sessionId != -1, confirmation = installer.confirmationIntent() != null,
   message = prefs.getString("result", null) ?: if (release != null && release.count.toLong() == browser.code && release.count.toLong() == service.code) "Both installed products match the selected release." else it.message) }
 }
 fun check() = operation {
  check(installer.sessionId == -1)
  mutable.update { it.copy(message = "Checking public Android releases…", received = 0, total = 0) }
  val release = client.latest(installer.installed(SERVICE_PACKAGE).code, installer.installed(BROWSER_PACKAGE).code) { gate.cancelled }
  gate.checkActive()
  if (release != null) check(prefs.edit().putString("release", encode(release)).remove("result").commit())
  else check(prefs.edit().remove("release").remove("result").commit())
  val next = release?.let { UpdatePolicy.plan(android.os.Build.VERSION.SDK_INT, it.count, installer.installed(SERVICE_PACKAGE), installer.installed(BROWSER_PACKAGE)).firstOrNull() }
  mutable.update { it.copy(release = release, ready = false, nextPackage = next, message = if (release == null) "No newer compatible Android publication found." else "Android ${release.version} available: Service and Browser.") }
 }
 fun download() = operation {
  check(installer.sessionId == -1)
  val release = requireNotNull(mutable.value.release)
  check(UpdatePolicy.plan(android.os.Build.VERSION.SDK_INT, release.count, installer.installed(SERVICE_PACKAGE), installer.installed(BROWSER_PACKAGE)).isNotEmpty()) { "Both products already match this release" }
  installer.directory.mkdirs()
  // Only this fixed private staging directory is cleared; no Service data is accessible here.
  installer.directory.listFiles()?.forEach { check(it.delete()) }
  val total = release.apks.sumOf { it.bytes }; var completed = 0L
  mutable.update { it.copy(ready = false, received = 0, total = total, message = "Downloading verified product APKs…") }
  for (file in release.apks) {
   client.download(release, file, File(installer.directory, file.name), { gate.cancelled }) { received, _ ->
    mutable.update { it.copy(received = completed + received) }
   }
   completed += file.bytes
  }
  gate.checkActive()
  installer.verify(release)
  val next = UpdatePolicy.plan(android.os.Build.VERSION.SDK_INT, release.count, installer.installed(SERVICE_PACKAGE), installer.installed(BROWSER_PACKAGE)).firstOrNull()
  mutable.update { it.copy(ready = next != null, nextPackage = next, message = "Both APKs verified. Install Service first, then return to install Browser. Each package needs Android confirmation.") }
 }
 fun install() = operation {
  check(installer.canInstall() && installer.sessionId == -1 && mutable.value.ready)
  val release = requireNotNull(mutable.value.release)
  // Re-read installed identities and every byte immediately before handing off to OS-owned sessions.
  installer.prepare(release)
  try { gate.checkActive(); installer.commit(); gate.checkActive() } catch (e: Exception) { installer.cancel(); throw e }
  mutable.update { it.copy(session = true, ready = false, message = "Waiting for Android. Continue installation when confirmation is ready.") }
 }
 fun cancel() {
  gate.cancel()
  client.cancel()
  if (!mutable.value.busy) operation(cleanupRetry = true) { gate.cancel() }
 }
 private fun encode(release: AndroidRelease): String = JSONObject().put("schemaVersion", 1).put("platform", "android").put("signing", "release-key")
  .put("count", release.count).put("version", release.version).put("tag", "v${release.version}").put("sourceCommit", release.sourceCommit)
  .put("files", JSONArray(release.files.map { JSONObject().put("name", it.name).put("bytes", it.bytes).put("sha256", it.sha256) })).toString()
 companion object {
  @Volatile private var instance: UpdateController? = null
  fun get(context: Context): UpdateController = instance ?: synchronized(this) { instance ?: UpdateController(context.applicationContext).also { instance = it } }
 }
}
