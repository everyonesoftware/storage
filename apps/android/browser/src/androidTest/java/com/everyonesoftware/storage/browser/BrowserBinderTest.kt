package com.everyonesoftware.storage.browser

import android.os.ParcelFileDescriptor
import androidx.compose.ui.test.*
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import com.everyonesoftware.storage.client.StorageConnection
import org.json.JSONObject
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import java.io.File
import java.util.concurrent.TimeUnit

class BrowserBinderTest {
 @get:Rule val compose = createAndroidComposeRule<MainActivity>()
 @Test fun realImportedItemOpensDetailsAndHistory() {
  val context = InstrumentationRegistry.getInstrumentation().targetContext
  val name = "Browser fixture ${System.nanoTime()}.txt"
  val file = File(context.cacheDir, "fixture.txt").apply { writeText("real service bytes") }
  StorageConnection(context).use { connection ->
   val api = connection.connect().get(30, TimeUnit.SECONDS)
   ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use {
    val response = JSONObject(api.importFile(name, "text/plain", it)); assertTrue(response.toString(), response.getBoolean("ok"))
   }
  }
  compose.waitUntil(30_000) { !compose.onNodeWithContentDescription("Refresh").fetchSemanticsNode().config.contains(SemanticsProperties.Disabled) }
  compose.onNodeWithContentDescription("Refresh").performClick()
  compose.onNode(hasSetTextAction()).performTextInput(name)
  val itemCard = hasText(name) and hasClickAction() and !hasSetTextAction()
  compose.waitUntil(30_000) { compose.onAllNodes(itemCard).fetchSemanticsNodes().size == 1 }
  fun snapshot(name: String) {
   val bitmap = requireNotNull(InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot())
   File(context.getExternalFilesDir(null), "$name.png").outputStream().use { bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it) }
   bitmap.recycle()
  }
  compose.onNode(itemCard).assertIsDisplayed()
  assertTrue(InstrumentationRegistry.getInstrumentation().uiAutomation.performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_BACK))
  compose.waitForIdle()
  snapshot("items-grid")
  compose.onNode(itemCard).performClick()
  compose.waitUntil(30_000) { compose.onAllNodesWithText("Attributes").fetchSemanticsNodes().isNotEmpty() }
  compose.onNodeWithText("Attributes").assertIsDisplayed()
  compose.onAllNodesWithText("18 bytes").onFirst().assertExists()
  compose.onNodeWithText("Export current version").assertExists()
  snapshot("item-details")
  compose.onNodeWithTag("details").performScrollToNode(hasText("Version history"))
  compose.onNodeWithText("Version history").assertIsDisplayed()
  file.delete()
 }
 @Test fun deferredFeaturesAreHonest() {
  compose.onNodeWithText("Queries").performClick()
  compose.onNodeWithText("Saved queries are not implemented in this local slice.").assertIsDisplayed()
  compose.onNodeWithText("Settings").performClick()
  compose.waitUntil(10_000) { compose.onAllNodesWithText("Service 0.1.0 (1)").fetchSemanticsNodes().isNotEmpty() }
  compose.onNodeWithText("Service 0.1.0 (1)").assertIsDisplayed()
  compose.onNodeWithText("Check for updates").assertIsEnabled()
 }
}
