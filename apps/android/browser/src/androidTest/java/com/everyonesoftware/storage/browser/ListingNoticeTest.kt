package com.everyonesoftware.storage.browser

import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import org.junit.Rule
import org.junit.Test

class ListingNoticeTest {
 @get:Rule val compose = createComposeRule()
 @Test fun truncatedListsNeverClaimCompleteResults() {
  compose.setContent { ListingNotice(true, "items") }
  compose.onNodeWithText("Showing a partial list of items. More results exist; search applies only to loaded results.").assertIsDisplayed()
 }
 @Test fun completeListsHaveNoTruncationWarning() {
  compose.setContent { ListingNotice(false, "versions") }
  compose.onAllNodes(hasText("Showing a partial list", substring = true)).assertCountEquals(0)
 }
}
