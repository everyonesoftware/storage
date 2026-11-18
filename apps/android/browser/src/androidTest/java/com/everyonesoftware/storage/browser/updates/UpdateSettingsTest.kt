package com.everyonesoftware.storage.browser.updates

import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import com.everyonesoftware.storage.browser.MainActivity
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class UpdateSettingsTest {
 @get:Rule val compose = createAndroidComposeRule<MainActivity>()
 @Test fun verifiedDebugFixtureRequiresExplicitUnknownSourcePermission() {
  val instrumentation = InstrumentationRegistry.getInstrumentation()
  val context = instrumentation.targetContext
  val installer = AndroidUpdateInstaller(context)
  val prefs = installer.preferences
  try {
   installer.directory.mkdirs()
   val files = listOf("Service", "Browser").map { label ->
    val file = java.io.File(installer.directory, "Storage-$label-0.1.2.apk")
    instrumentation.context.assets.open("updates/$label.apk").use { input -> file.outputStream().use { input.copyTo(it) } }
    org.json.JSONObject().put("name", file.name).put("bytes", file.length()).put("sha256", ReleaseClient.hex(java.security.MessageDigest.getInstance("SHA-256").digest(file.readBytes())))
   } + org.json.JSONObject().put("name", "Storage-android-SHA256SUMS").put("bytes", 1).put("sha256", "a".repeat(64))
   val raw = org.json.JSONObject().put("schemaVersion", 1).put("platform", "android").put("signing", "release-key").put("count", 2)
    .put("version", "0.1.2").put("tag", "v0.1.2").put("sourceCommit", "a".repeat(40)).put("files", org.json.JSONArray(files)).toString()
   prefs.edit().putString("release", raw).remove("result").commit()
   assertFalse("Test never grants unknown-source approval", installer.canInstall())
   compose.onNodeWithText("Settings").performClick()
   compose.waitUntil(30000) { compose.onAllNodesWithText("Allow installs from Storage…").fetchSemanticsNodes().isNotEmpty() }
   compose.onNodeWithText("Install Service update").assertIsNotEnabled()
   compose.onNodeWithText("Allow installs from Storage…").performScrollTo().performClick()
   // Open the real Android per-source permission page, then Back WITHOUT approving anything.
   Thread.sleep(2000)
   assertTrue(instrumentation.uiAutomation.performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_BACK))
   compose.waitForIdle()
   assertFalse(installer.canInstall())
   assertEquals(-1, installer.sessionId)
  } finally { installer.directory.deleteRecursively(); prefs.edit().clear().commit() }
 }
 @Test fun realSettingsShowsInstalledProductVersionsAndCheckAction() {
  compose.onNodeWithText("Settings").performClick()
  compose.waitUntil(10000) { compose.onAllNodes(hasText("Browser 0.1.0 (1)", substring = true)).fetchSemanticsNodes().isNotEmpty() }
  compose.onNodeWithText("Browser 0.1.0 (1)").assertIsDisplayed()
  compose.onNodeWithText("Service 0.1.0 (1)").assertIsDisplayed()
  compose.onNodeWithText("Check for updates").assertIsEnabled()
 }
 @Test fun alreadyInstalledPublicationCannotStartDownloadOrEraseStaging() {
  val context = InstrumentationRegistry.getInstrumentation().targetContext
  val installer = AndroidUpdateInstaller(context)
  installer.directory.mkdirs()
  val marker = java.io.File(installer.directory, "preserve-existing-stage").apply { writeText("untouched") }
  val prefs = installer.preferences
  val raw = """{"schemaVersion":1,"platform":"android","count":1,"version":"0.1.1","tag":"v0.1.1","sourceCommit":"${"a".repeat(40)}","signing":"release-key","files":[{"name":"Storage-Service-0.1.1.apk","bytes":1,"sha256":"${"b".repeat(64)}"},{"name":"Storage-Browser-0.1.1.apk","bytes":1,"sha256":"${"b".repeat(64)}"},{"name":"Storage-android-SHA256SUMS","bytes":1,"sha256":"${"b".repeat(64)}"}]}"""
  prefs.edit().putString("release", raw).commit()
  val controller = UpdateController(context)
  fun waitIdle() {
   val deadline = System.nanoTime() + 10_000_000_000L
   while (controller.state.value.busy && System.nanoTime() < deadline) Thread.sleep(20)
   assertFalse(controller.state.value.busy)
  }
  try {
   controller.refresh(); waitIdle()
   assertFalse(controller.state.value.ready)
   assertNull(controller.state.value.nextPackage)
   controller.download()
   Thread.sleep(500)
   controller.cancel(); waitIdle()
   assertTrue("An equal-version publication must be refused before staging or network work", marker.exists())
   assertFalse(controller.state.value.ready)
   assertEquals(-1, installer.sessionId)
  } finally { controller.cancel(); installer.directory.deleteRecursively(); prefs.edit().clear().commit() }
 }
 @Test fun recreatedControllerRetainsReleaseButNeverTrustsMissingDownloads() {
  val context = InstrumentationRegistry.getInstrumentation().targetContext
  val prefs = context.getSharedPreferences("application-updates", 0)
  val raw = """{"schemaVersion":1,"platform":"android","count":2,"version":"0.1.2","tag":"v0.1.2","sourceCommit":"${"a".repeat(40)}","signing":"release-key","files":[{"name":"Storage-Service-0.1.2.apk","bytes":1,"sha256":"${"b".repeat(64)}"},{"name":"Storage-Browser-0.1.2.apk","bytes":1,"sha256":"${"b".repeat(64)}"},{"name":"Storage-android-SHA256SUMS","bytes":1,"sha256":"${"b".repeat(64)}"}]}"""
  prefs.edit().putString("release", raw).putBoolean("ready", true).commit()
  val controller = UpdateController(context)
  controller.refresh()
  val deadline = System.nanoTime() + 10_000_000_000L
  while (controller.state.value.busy && System.nanoTime() < deadline) Thread.sleep(20)
  assertFalse(controller.state.value.busy)
  assertEquals(2, controller.state.value.release?.count)
  assertFalse(controller.state.value.ready)
  prefs.edit().clear().commit()
 }
}
