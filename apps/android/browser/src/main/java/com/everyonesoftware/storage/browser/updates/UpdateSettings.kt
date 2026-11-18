package com.everyonesoftware.storage.browser.updates

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
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
 Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
  Text(state.browser); Text(state.service)
  Text("Updates replace both application packages in place. Service data and application grants are kept; never uninstall to update.")
  Text(state.message)
  if (state.total > 0) {
   LinearProgressIndicator(progress = { (state.received.toFloat() / state.total).coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth())
   Text("${state.received} / ${state.total} bytes")
  } else if (state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
  Button(onClick = controller::check, enabled = !state.busy && !state.session && !state.cleanupPending) { Text("Check for updates") }
  if (state.release != null && state.nextPackage != null && !state.session && !state.cleanupPending) {
   OutlinedButton(onClick = controller::download, enabled = !state.busy) { Text(if (state.ready) "Download again" else "Download update") }
  }
  if (state.ready && state.nextPackage != null && !state.session && !state.cleanupPending) {
   if (!state.permission) OutlinedButton(onClick = { launcher.launch(controller.installer.permissionIntent()) }, enabled = !state.busy) { Text("Allow installs from Storage…") }
   Button(onClick = controller::install, enabled = !state.busy && state.permission) {
    Text(if (state.nextPackage == SERVICE_PACKAGE) "Install Service update" else "Install Browser update")
   }
  }
  if (state.confirmation) Button(onClick = {
   runCatching { controller.installer.confirmationIntent()?.let { launcher.launch(it) } }.onFailure { controller.cancel() }
  }, enabled = !state.busy) { Text("Continue installation…") }
  if (state.busy || state.session || state.cleanupPending) TextButton(onClick = controller::cancel) { Text("Cancel update") }
  Text("Android asks separately for each package: approve Service first, then return here to install Browser on all Android versions. Cancellation can be retried without uninstalling either app.", style = MaterialTheme.typography.bodySmall)
 }
}
