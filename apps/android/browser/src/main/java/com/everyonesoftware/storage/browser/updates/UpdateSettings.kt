package com.everyonesoftware.storage.browser.updates

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.ExpandLess
import androidx.compose.material.icons.outlined.ExpandMore
import androidx.compose.material.icons.outlined.ChevronRight
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.LifecycleOwner
import kotlinx.coroutines.delay

@Composable internal fun UpdateSettings() {
 val context = LocalContext.current
 val controller = remember { UpdateController.get(context) }
 val state by controller.state.collectAsState()
 val launcher = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { controller.refresh() }
 LaunchedEffect(Unit) { controller.refresh() }
 DisposableEffect(context) {
  val lifecycle = (context as? LifecycleOwner)?.lifecycle
  val observer = LifecycleEventObserver { _, event -> if (event == Lifecycle.Event.ON_RESUME) controller.refresh() }
  lifecycle?.addObserver(observer)
  onDispose { lifecycle?.removeObserver(observer) }
 }
 LaunchedEffect(state.session) {
  while (state.session) { delay(1000); controller.refresh(false) }
 }
 UpdatePanel(state, onCheck = controller::check, onDownload = controller::download, onInstall = controller::install,
  onPermission = { launcher.launch(controller.installer.permissionIntent()) }, onConfirm = {
   runCatching { controller.installer.confirmationIntent()?.let { launcher.launch(it) } }.onFailure { controller.cancel() }
  }, onCancel = controller::cancel, onNotes = controller::loadNotes)
}

@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun UpdatePanel(state: UpdateState, onCheck: () -> Unit = {}, onDownload: () -> Unit = {},
 onInstall: () -> Unit = {}, onPermission: () -> Unit = {}, onConfirm: () -> Unit = {}, onCancel: () -> Unit = {}, onNotes: () -> Unit = {}) {
 val enabled = !state.busy && !state.session && !state.cleanupPending
 val offered = state.release != null && state.nextPackage != null
 Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
  FlowRow(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalArrangement = Arrangement.spacedBy(4.dp)) {
   Text("Current Version: ${state.browserVersion}", Modifier.align(Alignment.CenterVertically), style = MaterialTheme.typography.titleMedium)
   TextButton(onClick = onCheck, enabled = enabled) { Text("Check now") }
  }
  Surface(shape = RoundedCornerShape(18.dp), color = if (isSystemInDarkTheme()) Color(0xFF1C292F) else Color(0xFFE7EFF2), modifier = Modifier.fillMaxWidth()) {
   Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
    if (offered) {
     FlowRow(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalArrangement = Arrangement.spacedBy(4.dp)) {
      Text("Update available", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
      Text("Storage ${state.release!!.version}", color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.titleMedium)
     }
    } else {
     when (state.checkStatus) {
      UpdateCheckStatus.Unknown -> Text("Not checked yet", style = MaterialTheme.typography.titleLarge)
      UpdateCheckStatus.Checking -> Text("Checking for updates…", style = MaterialTheme.typography.titleLarge)
      UpdateCheckStatus.Current -> Text("Up to date", style = MaterialTheme.typography.titleLarge)
      UpdateCheckStatus.Failed -> Text("Couldn’t check for updates", style = MaterialTheme.typography.titleLarge)
     }
    }
    if (state.message.isNotBlank()) Text(state.message, Modifier.semantics { liveRegion = LiveRegionMode.Polite }, style = MaterialTheme.typography.bodyMedium)
    if (offered) ChangelogDisclosure(state, onNotes)
    if (state.total > 0) {
     LinearProgressIndicator(progress = { (state.received.toFloat() / state.total).coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth())
     Text("${state.received} / ${state.total} bytes", style = MaterialTheme.typography.bodySmall)
    } else if (state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
    if (offered && !state.ready && !state.session && !state.cleanupPending) {
     Button(onClick = onDownload, enabled = enabled, modifier = Modifier.heightIn(min = 48.dp)) { Text("Download update") }
    }
    if (state.ready && state.nextPackage != null && !state.session && !state.cleanupPending) {
     if (!state.permission) OutlinedButton(onClick = onPermission, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) { Text("Allow installs from Storage…") }
     Button(onClick = onInstall, enabled = !state.busy && state.permission, modifier = Modifier.fillMaxWidth()) {
      Text(if (state.nextPackage == SERVICE_PACKAGE) "Install Service update" else "Install Browser update")
     }
    }
    if (state.confirmation) Button(onClick = onConfirm, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) { Text("Continue installation…") }
    if (state.busy || state.session || state.cleanupPending) TextButton(onClick = onCancel) { Text("Cancel update") }
   }
  }
 }
}

@Composable private fun ChangelogDisclosure(state: UpdateState, onNotes: () -> Unit) {
 var expanded by rememberSaveable(state.release) { mutableStateOf(false) }
 var details by rememberSaveable(state.release) { mutableStateOf(false) }
 val notes = state.notes
 val history = notes.history
 val count = history?.changeCount
 TextButton(onClick = {
  expanded = !expanded
  if (expanded && history == null && !notes.loading && notes.unavailable == null) onNotes()
 }, modifier = Modifier.fillMaxWidth().semantics { stateDescription = if (expanded) "Expanded" else "Collapsed" }, contentPadding = PaddingValues(vertical = 4.dp)) {
  Icon(if (expanded) Icons.Outlined.ExpandMore else Icons.Outlined.ChevronRight, null)
  Spacer(Modifier.width(4.dp))
  Text("What’s new", Modifier.weight(1f), textAlign = androidx.compose.ui.text.style.TextAlign.Start)
  if (count != null) Text("$count ${if (count == 1) "change" else "changes"}", style = MaterialTheme.typography.bodySmall)
 }
 if (expanded) {
  when {
   notes.loading -> Text("Loading what’s new…", Modifier.semantics { liveRegion = LiveRegionMode.Polite })
   notes.unavailable != null -> {
    Text("${notes.unavailable} You can still download this update.", style = MaterialTheme.typography.bodyMedium)
    TextButton(onClick = onNotes) { Text("Retry changelog") }
   }
   history != null -> {
    if (history.improvements.isNotEmpty()) ChangeGroup("Improvements", history.improvements)
    if (history.fixes.isNotEmpty()) ChangeGroup("Fixes", history.fixes)
    TextButton(onClick = { details = !details }, modifier = Modifier.semantics { stateDescription = if (details) "Expanded" else "Collapsed" }) {
     Text("Commit details"); Icon(if (details) Icons.Outlined.ExpandLess else Icons.Outlined.ExpandMore, null)
    }
    if (details) {
     Text("Source range: ${history.base}..${history.target}", style = MaterialTheme.typography.bodySmall)
     history.commits.forEach { Text("${it.sha} · ${it.original}", style = MaterialTheme.typography.bodySmall) }
    }
   }
  }
 }
}

@Composable private fun ChangeGroup(title: String, entries: List<String>) {
 Text(title, style = MaterialTheme.typography.labelLarge)
 entries.forEach { Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) { Text("•"); Text(it, style = MaterialTheme.typography.bodyMedium) } }
}
