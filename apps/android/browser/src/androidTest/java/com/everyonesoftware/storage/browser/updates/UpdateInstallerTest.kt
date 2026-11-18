package com.everyonesoftware.storage.browser.updates

import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.After
import org.junit.Test
import java.io.File
import java.security.MessageDigest

/** Disposable debug APKs generated locally; not published releases or production Items. Never approves OS prompts. */
class UpdateInstallerTest {
 private val instrumentation = InstrumentationRegistry.getInstrumentation()
 private val context = instrumentation.targetContext
 private val installer = AndroidUpdateInstaller(context)
 @After fun clean() { installer.cancel(); installer.directory.deleteRecursively(); context.getSharedPreferences("application-updates", Context.MODE_PRIVATE).edit().clear().commit() }
 private fun fixture(): AndroidRelease {
  installer.directory.mkdirs()
  val files = listOf("Service", "Browser").map { name ->
   val file = File(installer.directory, "Storage-$name-0.1.2.apk")
   instrumentation.context.assets.open("updates/$name.apk").use { input -> file.outputStream().use { input.copyTo(it) } }
   ReleaseFile(file.name, file.length(), ReleaseClient.hex(MessageDigest.getInstance("SHA-256").digest(file.readBytes())))
  } + ReleaseFile("Storage-android-SHA256SUMS", 1, "a".repeat(64))
  return AndroidRelease(2, files)
 }
 @Test fun verifiesActualDebugPairAndRejectsTamperedBytes() {
  val release = fixture(); installer.verify(release)
  File(installer.directory, release.apks.first().name).appendText("tamper")
  assertThrows(Exception::class.java) { installer.verify(release) }
 }
 @Test fun rejectsSwappedActualPackages() {
  val release = fixture()
  val first = File(installer.directory, release.apks[0].name)
  val second = File(installer.directory, release.apks[1].name)
  first.writeBytes(second.readBytes())
  val changed = release.copy(files = release.files.map { if (it.name == first.name) it.copy(bytes = first.length(), sha256 = ReleaseClient.hex(MessageDigest.getInstance("SHA-256").digest(first.readBytes()))) else it })
  assertThrows(Exception::class.java) { installer.verify(changed) }
 }
 @Test fun preparesRealServiceSessionThenCancelsWithoutInstalling() {
  val release = fixture()
  val id = installer.prepare(release)
  val info = context.packageManager.packageInstaller.getSessionInfo(id)!!
  assertEquals(SERVICE_PACKAGE, info.appPackageName)
  if (android.os.Build.VERSION.SDK_INT >= 29) assertFalse(info.isMultiPackage)
  val controller = UpdateController(context)
  controller.refresh(false)
  controller.cancel() // Cancellation must not be dropped while a silent refresh owns the gate.
  val deadline = System.nanoTime() + 10_000_000_000L
  while (context.packageManager.packageInstaller.getSessionInfo(id) != null && System.nanoTime() < deadline) Thread.sleep(25)
  assertNull(context.packageManager.packageInstaller.getSessionInfo(id))
  assertEquals(1L, installer.installed(SERVICE_PACKAGE).code)
  assertEquals(1L, installer.installed(BROWSER_PACKAGE).code)
 }
 @Test fun installerResultSurvivesNewInstallerAndRejectsForeignCallback() {
  val release = fixture(); val id = installer.prepare(release)
  val store = context.getSharedPreferences("application-updates", Context.MODE_PRIVATE)
  val callback = Intent().putExtra("ownedSession", id).putExtra("token", store.getString("token", ""))
   .putExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE_ABORTED)
  UpdateResultReceiver().onReceive(context, Intent(callback).putExtra("token", "foreign"))
  assertEquals(id, AndroidUpdateInstaller(context).sessionId)
  val confirmation = Intent("android.content.pm.action.CONFIRM_INSTALL").putExtra(PackageInstaller.EXTRA_SESSION_ID, id)
  UpdateResultReceiver().onReceive(context, Intent(callback).putExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_PENDING_USER_ACTION).putExtra(Intent.EXTRA_INTENT, confirmation))
  assertEquals(id, AndroidUpdateInstaller(context).confirmationIntent()!!.getIntExtra(PackageInstaller.EXTRA_SESSION_ID, -1))
  UpdateResultReceiver().onReceive(context, callback)
  assertEquals(-1, AndroidUpdateInstaller(context).sessionId)
  assertTrue(store.getString("result", "")!!.contains("cancel", true))
 }
}
