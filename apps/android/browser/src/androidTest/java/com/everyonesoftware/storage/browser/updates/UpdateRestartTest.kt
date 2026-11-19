package com.everyonesoftware.storage.browser.updates

import android.content.pm.PackageInstaller
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.After
import org.junit.Assert.*
import org.junit.Test
import java.io.File

/** Faults bypass Exception cleanup exactly where process death loses in-memory preparation state.
 * A fresh installer reads only durable preferences and Android's real sessions. Never approves consent.
 */
class UpdateRestartTest {
 private val instrumentation = InstrumentationRegistry.getInstrumentation()
 private val context = instrumentation.targetContext
 private val installer = AndroidUpdateInstaller(context)
 private class ProcessDeath : Error()
 private val restartPhase get() = InstrumentationRegistry.getArguments().getString("restartPhase")
 @After fun clean() {
  if (restartPhase?.startsWith("seed") == true) return // external harness force-stops between seed/recover runs
  installer.cancel()
  installer.directory.deleteRecursively()
  installer.preferences.edit().clear().commit()
 }
 private fun fixture(): AndroidRelease {
  installer.directory.mkdirs()
  val release = AndroidRelease(2, listOf("Service", "Browser").map { name ->
   val file = File(installer.directory, "Storage-$name-0.1.2.apk")
   instrumentation.context.assets.open("updates/$name.apk").use { input -> file.outputStream().use { input.copyTo(it) } }
   ReleaseFile(file.name, file.length(), fixtureSha256(file))
  } + ReleaseFile("Storage-android-SHA256SUMS", 1, "a".repeat(64)))
  val raw = org.json.JSONObject().put("schemaVersion", 1).put("platform", "android").put("signing", "release-key")
   .put("count", 2).put("version", "0.1.2").put("tag", "v0.1.2").put("sourceCommit", "a".repeat(40))
   .put("files", org.json.JSONArray(release.files.map { org.json.JSONObject().put("name", it.name).put("bytes", it.bytes).put("sha256", it.sha256) })).toString()
  assertTrue(installer.preferences.edit().putString("release", raw).commit())
  return release
 }
 @Test fun restartAtEveryCreateJournalWriteBoundaryOnlyAbandonsThisAttempt() {
  val release = fixture()
  val sessions = context.packageManager.packageInstaller
  val foreign = sessions.createSession(PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL))
  try {
   val width = 1
   for (boundary in listOf("created", "journaled", "writing", "written")) {
    val count = width
    for (ordinal in 1..count) {
     var seen = 0
     val attemptIds = mutableSetOf<Int>()
     val faulty = AndroidUpdateInstaller(context) { point, id ->
      if (point == "created") attemptIds.add(id)
      if (point == boundary && ++seen == ordinal) throw ProcessDeath()
     }
     assertThrows(ProcessDeath::class.java) { faulty.prepare(release) }
     val restarted = AndroidUpdateInstaller(context)
     restarted.reconcile()
     assertEquals("$boundary/$ordinal", -1, restarted.sessionId)
     assertTrue(attemptIds.isNotEmpty())
     attemptIds.forEach { assertNull("$boundary/$ordinal left session $it", sessions.getSessionInfo(it)) }
     assertNotNull("Unrelated Browser-owned session must survive", sessions.getSessionInfo(foreign))
     restarted.verify(release) // verified downloads survive; explicit retry does not redownload.
    }
   }
  } finally { sessions.abandonSession(foreign) }
 }
 @Test fun restartAfterPreparedAndBeforeCommitReturnsToExplicitRetry() {
  val release = if (restartPhase == "recover") ReleaseClient.parse(installer.preferences.getString("release", null)!!) else fixture()
  if (restartPhase == "seedWriting") {
   val faulty = AndroidUpdateInstaller(context) { point, _ -> if (point == "writing") throw ProcessDeath() }
   assertThrows(ProcessDeath::class.java) { faulty.prepare(release) }
   assertEquals("preparing", installer.preferences.getString("phase", null))
   return
  }
  if (restartPhase != "recover") {
   installer.prepare(release)
   assertEquals("prepared", installer.preferences.getString("phase", null))
   if (restartPhase == "seed") return
  }
  val ids = installer.preferences.getStringSet("sessions", emptySet())!!.map { it.toInt() }
  val controller = UpdateController(context)
  controller.refresh()
  val deadline = System.nanoTime() + 15_000_000_000L
  while (controller.state.value.busy && System.nanoTime() < deadline) Thread.sleep(25)
  assertFalse(controller.state.value.busy)
  assertFalse(controller.state.value.session)
  assertTrue("Verified files must yield the explicit Install retry action", controller.state.value.ready)
  assertEquals(-1, installer.sessionId)
  ids.forEach { assertNull(context.packageManager.packageInstaller.getSessionInfo(it)) }
  installer.verify(release)
  val retry = installer.prepare(release)
  assertNotNull(context.packageManager.packageInstaller.getSessionInfo(retry))
 }
 private fun withInstallPermission(action: () -> Unit) {
  fun shell(command: String) = android.os.ParcelFileDescriptor.AutoCloseInputStream(
   instrumentation.uiAutomation.executeShellCommand(command)).bufferedReader().use { it.readText() }
  val prefix = "appops set ${context.packageName} REQUEST_INSTALL_PACKAGES"
  val before = shell("appops get ${context.packageName} REQUEST_INSTALL_PACKAGES")
  val mode = Regex("REQUEST_INSTALL_PACKAGES: ([a-z]+)").find(before)?.groupValues?.get(1) ?: "default"
  try {
   shell("$prefix allow")
   assertTrue("Test must be able to reach OS commit", installer.canInstall())
   action()
  } finally { shell("$prefix $mode") }
 }
 @Test fun restartBeforeCommitCallAbandonsUnsealedSessionButPreservesFiles() = withInstallPermission {
  val release = fixture()
  val faulty = AndroidUpdateInstaller(context) { point, _ -> if (point == "committing") throw ProcessDeath() }
  faulty.prepare(release)
  assertThrows(ProcessDeath::class.java) { faulty.commit() }
  assertEquals("committing", installer.preferences.getString("phase", null))
  AndroidUpdateInstaller(context).reconcile()
  assertEquals(-1, installer.sessionId)
  installer.verify(release)
 }
 @Test fun realSequentialServiceCommitReachesUserConsentWithoutFinalFailure() = withInstallPermission {
  val id = installer.prepare(fixture())
  val prepared = context.packageManager.packageInstaller.getSessionInfo(id)!!
  if (android.os.Build.VERSION.SDK_INT >= 29) assertFalse(prepared.isMultiPackage)
  assertEquals(SERVICE_PACKAGE, prepared.appPackageName)
  installer.commit()
  val deadline = System.nanoTime() + 15_000_000_000L
  while (installer.confirmationIntent() == null && installer.sessionId == id && System.nanoTime() < deadline) Thread.sleep(25)
  assertEquals("Real commit failed before consent: ${installer.preferences.getString("result", null)}", id, installer.sessionId)
  assertNotNull("Real OS commit must offer user confirmation", installer.confirmationIntent())
  // Keep observing past the first child callback; an immediate final failure must not be hidden.
  Thread.sleep(1500)
  assertEquals("OS destroyed the root after emitting child consent", id, installer.sessionId)
  assertNotNull(context.packageManager.packageInstaller.getSessionInfo(id))
 }
 @Test fun restartAfterOsCommitKeepsSealedSessionAndPendingConfirmation() = withInstallPermission {
  val release = fixture()
  val faulty = AndroidUpdateInstaller(context) { point, _ -> if (point == "committed") throw ProcessDeath() }
  val sessions = context.packageManager.packageInstaller
  val id = faulty.prepare(release)
  assertThrows(ProcessDeath::class.java) { faulty.commit() }
  assertTrue(sessions.getSessionInfo(id)!!.isSealed)
  val restarted = AndroidUpdateInstaller(context)
  restarted.reconcile()
  assertEquals(id, restarted.sessionId)
  val deadline = System.nanoTime() + 15_000_000_000L
  while (installer.confirmationIntent() == null && System.nanoTime() < deadline) Thread.sleep(25)
  assertNotNull("Real committed Service session must retain OS consent", installer.confirmationIntent())
  AndroidUpdateInstaller(context).reconcile()
  assertEquals(id, installer.sessionId)
  assertEquals(id, installer.confirmationIntent()!!.getIntExtra(PackageInstaller.EXTRA_SESSION_ID, -1))
 }
}
