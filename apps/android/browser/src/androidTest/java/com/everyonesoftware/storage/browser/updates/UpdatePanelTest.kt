package com.everyonesoftware.storage.browser.updates

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class UpdatePanelTest {
 @get:Rule val compose = createComposeRule()
 private fun screenshot(name: String) {
  compose.waitForIdle()
  val instrumentation = androidx.test.platform.app.InstrumentationRegistry.getInstrumentation()
  val bitmap = requireNotNull(instrumentation.uiAutomation.takeScreenshot())
  try {
   java.io.File(instrumentation.targetContext.getExternalFilesDir(null), name).outputStream().use {
    assertTrue(bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it))
   }
  } finally { bitmap.recycle() }
 }
 private val base = "a".repeat(40)
 private val target = "b".repeat(40)
 private val offer = AndroidRelease(11, emptyList(), target)
 private fun available() = UpdateState(release = offer, browserVersion = "0.1.7", nextPackage = SERVICE_PACKAGE, message = "")
 private fun history(original: String = "[verified] feat(android): Preserve shared updates") = ChangeHistory(base, target, listOf(SourceChange(target, original)))
 private fun show(state: State<UpdateState>, scale: Float = 1f, onNotes: () -> Unit = {}, onDownload: () -> Unit = {}) {
  compose.setContent {
   val density = LocalDensity.current
   CompositionLocalProvider(LocalDensity provides Density(density.density, scale)) {
    MaterialTheme { Column(Modifier.width(360.dp).verticalScroll(rememberScrollState()).padding(16.dp)) {
     UpdatePanel(state.value, onNotes = onNotes, onDownload = onDownload)
    } }
   }
  }
 }
 @Test fun disclosureStartsCollapsedLoadsOnDemandAndReopensWithoutLosingOriginals() {
  var requests = 0
  val state = mutableStateOf(available().copy(notes = ChangelogState(history = history())))
  show(state, onNotes = { requests++ })
  screenshot("settings-update-available.png")
  compose.onNodeWithText("Improvements").assertDoesNotExist()
  compose.onNodeWithText("What’s new").performClick()
  compose.onNodeWithText("Improvements").assertExists()
  screenshot("settings-update-expanded.png")
  compose.onNodeWithText("Fixes").assertDoesNotExist()
  compose.onNodeWithText("Commit details").performScrollTo().performClick()
  compose.onNodeWithText("Source range: $base..$target").assertExists()
  compose.onNodeWithText("$target · [verified] feat(android): Preserve shared updates").assertExists()
  compose.onNodeWithText("What’s new").performScrollTo().performClick()
  compose.onNodeWithText("Improvements").assertDoesNotExist()
  compose.onNodeWithText("What’s new").performClick()
  compose.onNodeWithText("Improvements").assertExists()
  assertEquals(0, requests)
 }
 @Test fun unopenedNotesAreFetchedOnlyWhenExpanded() {
  var requests = 0
  show(mutableStateOf(available()), onNotes = { requests++ })
  assertEquals(0, requests)
  compose.onNodeWithText("What’s new").performClick()
  assertEquals(1, requests)
 }
 @Test fun unavailableChangelogRetryDoesNotDisableDownload() {
  var retries = 0; var downloads = 0
  show(mutableStateOf(available().copy(notes = ChangelogState(unavailable = "Installed source identity is unavailable."))), onNotes = { retries++ }, onDownload = { downloads++ })
  compose.onNodeWithText("What’s new").performClick()
  compose.onNodeWithText("Retry changelog").performScrollTo().performClick()
  compose.onNodeWithText("Download update").performScrollTo().assertIsEnabled().performClick()
  assertEquals(1, retries); assertEquals(1, downloads)
  compose.onNodeWithText("0 changes").assertDoesNotExist()
 }
 @Test fun transitionsKeepCompactLayoutAndExplicitServiceThenBrowserActions() {
  val state = mutableStateOf(available())
  show(state)
  compose.onNodeWithText("Storage 0.1.11").assertExists()
  compose.runOnIdle { state.value = state.value.copy(busy = true, received = 30, total = 100, message = "Downloading…") }
  compose.onNodeWithText("30 / 100 bytes").assertExists()
  compose.onNodeWithText("Check now").assertIsNotEnabled()
  compose.runOnIdle { state.value = state.value.copy(busy = false, ready = true, permission = false) }
  compose.onNodeWithText("Install Service update").performScrollTo().assertIsNotEnabled()
  compose.onNodeWithText("Allow installs from Storage…").assertExists()
  compose.runOnIdle { state.value = state.value.copy(permission = true, nextPackage = BROWSER_PACKAGE) }
  compose.onNodeWithText("Install Browser update").performScrollTo().assertIsEnabled()
  compose.onNodeWithText("Install Service update").assertDoesNotExist()
  for (removed in listOf("Updates", "Appearance", "Package versions", "About Storage")) compose.onNodeWithText(removed).assertDoesNotExist()
 }
 @Test fun currentInitialCheckingAndErrorMessagesNeverInventHistory() {
  val state = mutableStateOf(UpdateState(browserVersion = "0.1.7"))
  show(state)
  compose.onNodeWithText("Not checked yet").assertExists()
  compose.onNodeWithText("Check now for a newer Android version.").assertExists()
  compose.runOnIdle { state.value = state.value.copy(busy = true, checkStatus = UpdateCheckStatus.Checking, message = "Looking for a newer Android version.") }
  compose.onNodeWithText("Checking for updates…").assertExists()
  compose.runOnIdle { state.value = state.value.copy(busy = false, checkStatus = UpdateCheckStatus.Current, message = "No newer Android version is available.") }
  compose.onNodeWithText("Up to date").assertExists()
  compose.onNodeWithText("No newer Android version is available.").assertExists()
  screenshot("settings-update-current.png")
  compose.onNodeWithText("Download update").assertDoesNotExist()
  compose.runOnIdle { state.value = state.value.copy(checkStatus = UpdateCheckStatus.Failed, message = "Try again when you’re connected.") }
  compose.onNodeWithText("Couldn’t check for updates").assertExists()
  compose.onNodeWithText("Try again when you’re connected.").assertExists()
  compose.onNodeWithText("What’s new").assertDoesNotExist()
 }
 @Test fun cleanupWithoutSessionKeepsCancelAndBlocksEveryNewAction() {
  val state = mutableStateOf(available().copy(cleanupPending = true, session = false))
  show(state)
  compose.onNodeWithText("Cancel update").assertExists().assertIsEnabled()
  compose.onNodeWithText("Check now").assertIsNotEnabled()
  compose.onNodeWithText("Download update").assertDoesNotExist()
  compose.runOnIdle { state.value = state.value.copy(ready = true, permission = true) }
  compose.onNodeWithText("Install Service update").assertDoesNotExist()
  compose.onNodeWithText("Cancel update").assertIsEnabled()
 }
 @Test fun offerAndVersionHeadersWrapAtAccessibilityFontSizeWithoutOverlap() {
  show(mutableStateOf(available()), scale = 2f)
  val left = compose.onNodeWithText("Update available").fetchSemanticsNode().boundsInRoot
  val right = compose.onNodeWithText("Storage 0.1.11").fetchSemanticsNode().boundsInRoot
  assertTrue(left.right <= right.left || left.bottom <= right.top || right.bottom <= left.top)
  compose.onNodeWithText("Check now").assertExists()
  compose.onNodeWithText("Current Version: 0.1.7").assertExists()
 }
 @Test fun emptyGroupsAreOmittedAndNewOfferResetsDisclosure() {
  val docs = ChangeHistory(base, target, listOf(SourceChange(target, "docs: revise guide", listOf("docs/guide.md"))))
  val state = mutableStateOf(available().copy(notes = ChangelogState(history = docs)))
  show(state)
  compose.onNodeWithText("What’s new").performClick()
  compose.onNodeWithText("Improvements").assertDoesNotExist()
  compose.onNodeWithText("Fixes").assertDoesNotExist()
  compose.onNodeWithText("Commit details").assertExists()
  compose.runOnIdle { state.value = state.value.withOffer(AndroidRelease(12, emptyList(), "c".repeat(40))) }
  compose.onNodeWithText("Commit details").assertDoesNotExist()
  compose.onNodeWithText("What’s new").assertExists()
 }
}
