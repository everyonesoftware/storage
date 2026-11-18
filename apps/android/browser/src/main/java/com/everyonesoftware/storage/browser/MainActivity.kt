package com.everyonesoftware.storage.browser

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.automirrored.outlined.ViewList
import androidx.compose.material.icons.automirrored.outlined.InsertDriveFile
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.everyonesoftware.storage.client.IStorage
import com.everyonesoftware.storage.client.StorageConnection
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.TimeUnit

class MainActivity : ComponentActivity() {
 private lateinit var connection: StorageConnection
 override fun onCreate(savedInstanceState: Bundle?) {
  super.onCreate(savedInstanceState)
  connection = StorageConnection(this)
  setContent {
   val dark = isSystemInDarkTheme()
   val colors = if (dark) darkColorScheme(primary = Color(0xFF69D8CE), secondary = Color(0xFF99C9FF), background = Color(0xFF101719), surface = Color(0xFF101719)) else lightColorScheme(primary = Color(0xFF006B64), secondary = Color(0xFF245D97), background = Color(0xFFF5F8F8), surface = Color(0xFFF5F8F8))
   MaterialTheme(colorScheme = colors) { StorageApp() }
  }
 }
 override fun onDestroy() { connection.close(); super.onDestroy() }
 private fun unwrap(raw: String): JSONObject {
  val result = JSONObject(raw)
  if (!result.getBoolean("ok")) throw IllegalStateException(result.getJSONObject("error").getString("message"))
  return result.getJSONObject("result")
 }
 private fun JSONArray.objects() = (0 until length()).map { getJSONObject(it) }

 @OptIn(ExperimentalMaterial3Api::class)
 @Composable private fun StorageApp() {
  val scope = rememberCoroutineScope()
  var api by remember { mutableStateOf<IStorage?>(null) }
  var page by remember { mutableStateOf("Items") }
  var items by remember { mutableStateOf(emptyList<JSONObject>()) }
  var collections by remember { mutableStateOf(emptyList<JSONObject>()) }
  var collection by remember { mutableStateOf<JSONObject?>(null) }
  var selected by remember { mutableStateOf<JSONObject?>(null) }
  var versions by remember { mutableStateOf(emptyList<JSONObject>()) }
  var itemsHaveMore by remember { mutableStateOf(false) }
  var collectionsHaveMore by remember { mutableStateOf(false) }
  var versionsHaveMore by remember { mutableStateOf(false) }
  var health by remember { mutableStateOf<JSONObject?>(null) }
  var busy by remember { mutableStateOf(true) }
  var error by remember { mutableStateOf<String?>(null) }
  var notice by remember { mutableStateOf<String?>(null) }
  var search by remember { mutableStateOf("") }
  var grid by remember { mutableStateOf(true) }
  var newCollection by remember { mutableStateOf(false) }
  var collectionName by remember { mutableStateOf("") }
  var exportVersion by remember { mutableStateOf("") }
  var openExport by remember { mutableStateOf(false) }
  var versionImport by remember { mutableStateOf(false) }
  fun work(action: suspend () -> Unit) { scope.launch { busy = true; error = null; try { action() } catch (e: Exception) { error = e.cause?.message ?: e.message ?: "Storage operation failed" } finally { busy = false } } }
  suspend fun query(op: String, args: JSONObject = JSONObject()): JSONObject = withContext(Dispatchers.IO) { unwrap(requireNotNull(api) { "Storage Service is not connected" }.query(op, args.toString())) }
  suspend fun refresh() {
   health = query("health")
   val itemPage = if (collection == null) query("listItems") else query("listCollectionItems", JSONObject().put("collectionId", collection!!.getString("id")))
   items = itemPage.getJSONArray("items").objects()
   itemsHaveMore = itemPage.optBoolean("hasMore", false)
   val collectionPage = query("listCollections")
   collections = collectionPage.getJSONArray("collections").objects()
   collectionsHaveMore = collectionPage.optBoolean("hasMore", false)
  }
  suspend fun loadVersions(item: JSONObject) {
   val versionPage = query("listVersions", JSONObject().put("itemId", item.getString("id")))
   versions = versionPage.getJSONArray("versions").objects()
   versionsHaveMore = versionPage.optBoolean("hasMore", false)
  }
  fun detail(item: JSONObject) { work { selected = query("getItem", JSONObject().put("itemId", item.getString("id"))); loadVersions(item) } }
  val importer = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
   if (uri != null) work {
    val current = selected
    val imported = withContext(Dispatchers.IO) {
     var name = uri.lastPathSegment ?: "Imported file"
     contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { if (it.moveToFirst()) name = it.getString(0) }
     contentResolver.openFileDescriptor(uri, "r").use { descriptor ->
      requireNotNull(descriptor) { "Unable to read selected file" }
      val service = requireNotNull(api)
      unwrap(if (versionImport && current != null) service.addVersion(current.getString("id"), current.getLong("revision"), descriptor) else service.importFile(name, contentResolver.getType(uri) ?: "application/octet-stream", descriptor))
     }
    }
    refresh(); if (versionImport) { selected = imported; loadVersions(imported) }
    notice = "Imported ${imported.getString("name")}"; versionImport = false
   }
  }
  val exporter = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
   if (uri != null) work {
    withContext(Dispatchers.IO) {
     ParcelFileDescriptor.AutoCloseInputStream(requireNotNull(api).readVersion(exportVersion)).use { input ->
      requireNotNull(contentResolver.openOutputStream(uri, "wt")) { "Unable to open export destination" }.use { output -> input.copyTo(output) }
     }
    }
    notice = "Export complete"
    if (openExport) {
     val intent = Intent(Intent.ACTION_VIEW).setDataAndType(uri, selected?.optString("contentType") ?: "application/octet-stream").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
     try { startActivity(Intent.createChooser(intent, "Open exported file")) } catch (_: android.content.ActivityNotFoundException) { notice = "Export complete; no compatible viewer installed" }
    }
   }
  }
  LaunchedEffect(Unit) { work { api = withContext(Dispatchers.IO) { connection.connect().get(20, TimeUnit.SECONDS) }; refresh() } }
  Scaffold(
   topBar = { TopAppBar(title = { Column { Text(selected?.optString("name") ?: collection?.optString("name") ?: "Storage", maxLines = 1, overflow = TextOverflow.Ellipsis, fontWeight = FontWeight.SemiBold); if (selected == null) Text("Your files. Your local store.", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant) } }, navigationIcon = { if (selected != null || collection != null) IconButton(onClick = { if (selected != null) { selected = null; versions = emptyList() } else { collection = null; work { refresh() } } }) { Icon(Icons.AutoMirrored.Outlined.ArrowBack, "Back") } }, actions = { IconButton(enabled = !busy, onClick = { work { if (api == null) api = withContext(Dispatchers.IO) { connection.connect().get(20, TimeUnit.SECONDS) }; refresh() } }) { Icon(Icons.Outlined.Refresh, "Refresh") } }) },
   bottomBar = { NavigationBar { listOf("Items" to Icons.Outlined.Folder, "Collections" to Icons.Outlined.CollectionsBookmark, "Queries" to Icons.Outlined.Search, "Devices" to Icons.Outlined.Devices, "Settings" to Icons.Outlined.Settings).forEach { (label, icon) -> NavigationBarItem(selected = page == label, onClick = { page = label; selected = null; collection = null; if (label == "Items") work { refresh() } }, icon = { Icon(icon, label) }, label = { Text(label, maxLines = 1) }, alwaysShowLabel = true) } } },
   floatingActionButton = { if (page == "Items" && selected == null) ExtendedFloatingActionButton(onClick = { versionImport = false; importer.launch(arrayOf("*/*")) }, icon = { Icon(Icons.Outlined.Add, null) }, text = { Text("Import file") }) }
  ) { padding ->
   Column(Modifier.fillMaxSize().padding(padding)) {
    if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
    error?.let { message -> Surface(color = MaterialTheme.colorScheme.errorContainer, modifier = Modifier.fillMaxWidth()) { Text(message, Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onErrorContainer) } }
    notice?.let { message -> Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) { Text(message, Modifier.weight(1f), style = MaterialTheme.typography.bodySmall); TextButton(onClick = { notice = null }) { Text("Dismiss") } } }
    when {
     selected != null -> {
      val item = selected!!
      LazyColumn(Modifier.fillMaxSize().testTag("details"), contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
       item { Text("Item details", style = MaterialTheme.typography.headlineSmall) }
       item { Section("Attributes") {
        Text("${item.getJSONObject("attributes").getLong("byteLength")} bytes", style = MaterialTheme.typography.titleMedium)
        Text(item.getString("contentType")); LabelValue("SHA-256", item.getJSONObject("attributes").getString("sha256"))
        LabelValue("Item ID", item.getString("id")); LabelValue("Revision", item.getLong("revision").toString()); LabelValue("Modified", item.getString("modifiedAt"))
        LabelValue("Access policy ID", item.getString("accessPolicyId")); LabelValue("Policy revision", item.getLong("accessPolicyRevision").toString())
       } }
       item { Button(onClick = { exportVersion = item.getString("currentVersionId"); openExport = false; exporter.launch(item.getString("name")) }, enabled = !busy, modifier = Modifier.fillMaxWidth()) { Text("Export current version") } }
       item { OutlinedButton(onClick = { exportVersion = item.getString("currentVersionId"); openExport = true; exporter.launch(item.getString("name")) }, enabled = !busy, modifier = Modifier.fillMaxWidth()) { Text("Export and open…") } }
       item { OutlinedButton(onClick = { versionImport = true; importer.launch(arrayOf("*/*")) }, enabled = !busy, modifier = Modifier.fillMaxWidth()) { Text("Import new version") } }
       if (collections.isNotEmpty()) item { Section("Add to collection") { collections.forEach { group -> TextButton(onClick = { work { query("addToCollection", JSONObject().put("collectionId", group.getString("id")).put("itemId", item.getString("id")).put("expectedRevision", group.getLong("revision"))); refresh(); notice = "Added to ${group.getString("name")}" } }) { Text(group.getString("name")) } } } }
       item { ListingNotice(collectionsHaveMore, "collections") }
       item { Text("Version history", style = MaterialTheme.typography.titleLarge); ListingNotice(versionsHaveMore, "versions") }
       items(versions, key = { it.getString("id") }) { version -> Section(if (version.getString("id") == item.getString("currentVersionId")) "Current version" else "Retained version") {
        Text(version.getString("createdAt")); Text("${version.getLong("byteLength")} bytes"); LabelValue("Version ID", version.getString("id")); LabelValue("SHA-256", version.getString("sha256"))
        TextButton(onClick = { exportVersion = version.getString("id"); openExport = false; exporter.launch(item.getString("name")) }) { Text("Export this version") }
       } }
      }
     }
     page == "Items" -> {
      Row(Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically) { OutlinedTextField(value = search, onValueChange = { search = it }, placeholder = { Text("Search items") }, singleLine = true, leadingIcon = { Icon(Icons.Outlined.Search, null) }, modifier = Modifier.weight(1f), shape = RoundedCornerShape(16.dp)); IconButton(onClick = { grid = !grid }) { Icon(if (grid) Icons.AutoMirrored.Outlined.ViewList else Icons.Outlined.GridView, if (grid) "List view" else "Grid view") } }
      val filtered = items.filter { it.getString("name").contains(search, ignoreCase = true) }
      Text("${filtered.size} ${if (itemsHaveMore) "loaded items" else "items"} · Local store", Modifier.padding(horizontal = 20.dp, vertical = 4.dp), style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
      ListingNotice(itemsHaveMore, "items")
      if (filtered.isEmpty() && !busy) EmptyState(if (search.isEmpty()) "A place for what matters" else "No matching items", if (search.isEmpty()) "Import your first file. Content stays in Storage Service, separate from this browser." else "Try another name.")
      else if (grid) LazyVerticalGrid(GridCells.Adaptive(150.dp), contentPadding = PaddingValues(16.dp, 12.dp, 16.dp, 100.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) { items(filtered, key = { it.getString("id") }) { ItemCard(it, true) { detail(it) } } }
      else LazyColumn(contentPadding = PaddingValues(16.dp, 12.dp, 16.dp, 100.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) { items(filtered, key = { it.getString("id") }) { ItemCard(it, false) { detail(it) } } }
     }
     page == "Collections" -> LazyColumn(contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
      item { Text("Collections", style = MaterialTheme.typography.headlineMedium); Text("Organize references without duplicating content.", color = MaterialTheme.colorScheme.onSurfaceVariant); Spacer(Modifier.height(16.dp)); Button(onClick = { newCollection = true }) { Text("New collection") } }
      item { ListingNotice(collectionsHaveMore, "collections") }
      if (collections.isEmpty()) item { Text("No collections yet.") }
      items(collections, key = { it.getString("id") }) { group -> ElevatedCard(onClick = { collection = group; page = "Items"; work { refresh() } }, modifier = Modifier.fillMaxWidth()) { Row(Modifier.padding(20.dp), verticalAlignment = Alignment.CenterVertically) { Icon(Icons.Outlined.CollectionsBookmark, null); Spacer(Modifier.width(14.dp)); Column { Text(group.getString("name"), style = MaterialTheme.typography.titleMedium); Text("Revision ${group.getLong("revision")}", style = MaterialTheme.typography.bodySmall) } } } }
     }
     page == "Queries" -> EmptyState("Queries", "Saved queries are not implemented in this local slice.")
     page == "Devices" -> LazyColumn(contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
      item { Text("This device", style = MaterialTheme.typography.headlineMedium) }
      item { Section("Storage Service diagnostics") { health?.let { h -> LabelValue("Status", h.getString("status")); LabelValue("Device ID", h.getString("deviceId")); LabelValue("Data store ID", h.getString("dataStoreId")); LabelValue("Items", h.getLong("itemCount").toString()); LabelValue("Versions", h.getLong("versionCount").toString()) } ?: Text("Service not connected") } }
      item { Section("Networking") { Text("Pairing and replication are not implemented. No paired devices are reported.") } }
     }
     page == "Settings" -> LazyColumn(contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
      item { Text("Settings", style = MaterialTheme.typography.headlineMedium) }
      item { Section("About Storage") { Text("Browser 0.1.0 · local vertical slice"); Text("The browser has no repository access. All content operations are authenticated by Storage Service.") } }
      item { Section("Updates") { Text("No release feed configured."); Button(onClick = { notice = "Update checking is not configured: no published Android release feed." }) { Text("Check for updates") } } }
      item { Section("Appearance") { Text("Follows your device’s light or dark theme.") } }
     }
    }
   }
  }
  if (newCollection) AlertDialog(onDismissRequest = { newCollection = false }, title = { Text("New collection") }, text = { OutlinedTextField(value = collectionName, onValueChange = { collectionName = it }, label = { Text("Name") }, singleLine = true) }, confirmButton = { TextButton(enabled = collectionName.isNotBlank(), onClick = { newCollection = false; work { query("createCollection", JSONObject().put("name", collectionName)); collectionName = ""; refresh() } }) { Text("Create") } }, dismissButton = { TextButton(onClick = { newCollection = false }) { Text("Cancel") } })
 }
}

@Composable internal fun ListingNotice(hasMore: Boolean, records: String) {
 if (hasMore) Text("Showing a partial list of $records. More results exist; ${if (records == "items") "search applies only to loaded results." else "this local slice does not yet support paging."}", Modifier.padding(horizontal = 20.dp, vertical = 8.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

@Composable private fun Section(title: String, content: @Composable ColumnScope.() -> Unit) {
 Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surfaceContainer, modifier = Modifier.fillMaxWidth()) { Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) { Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold); content() } }
}
@Composable private fun LabelValue(label: String, value: String) { Column { Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant); Text(value, style = MaterialTheme.typography.bodySmall) } }
@Composable private fun EmptyState(title: String, description: String) { Column(Modifier.fillMaxWidth().padding(horizontal = 36.dp, vertical = 50.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) { Icon(Icons.Outlined.FolderOpen, null, Modifier.size(54.dp), tint = MaterialTheme.colorScheme.primary); Text(title, style = MaterialTheme.typography.headlineSmall); Text(description, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant) } }
@Composable private fun ItemCard(item: JSONObject, grid: Boolean, onClick: () -> Unit) {
 ElevatedCard(onClick = onClick, modifier = Modifier.fillMaxWidth(), shape = RoundedCornerShape(20.dp)) {
  Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
   Icon(Icons.AutoMirrored.Outlined.InsertDriveFile, null, Modifier.size(if (grid) 36.dp else 24.dp), tint = MaterialTheme.colorScheme.primary)
   Text(item.getString("name"), maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleSmall)
   Text("${item.getJSONObject("attributes").getLong("byteLength")} bytes · v${item.getLong("revision")}", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
  }
 }
}
