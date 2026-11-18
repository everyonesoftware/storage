package com.everyonesoftware.storage.browser.updates

import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class ReleaseClientTest {
 private fun inventory(n: Int) = JSONObject().put("schemaVersion", 1).put("platform", "android")
  .put("count", n).put("version", "0.1.$n").put("tag", "v0.1.$n").put("sourceCommit", "a".repeat(40)).put("signing", "release-key")
  .put("files", JSONArray(listOf("Storage-Service-0.1.$n.apk", "Storage-Browser-0.1.$n.apk", "Storage-android-SHA256SUMS").map { JSONObject().put("name", it).put("bytes", 3).put("sha256", "b".repeat(64)) }))
 private fun release(n: Int, android: Boolean) = JSONObject().put("tag_name", "v0.1.$n").put("draft", false).put("prerelease", false).put("published_at", "2026-11-18T00:00:00Z")
  .put("assets", if (!android) JSONArray() else JSONArray((inventory(n).getJSONArray("files").let { a -> (0 until a.length()).map { a.getJSONObject(it).getString("name") } } + "storage-android-release.json").map { name -> JSONObject().put("name", name).put("size", if (name.endsWith(".json")) inventory(n).toString().toByteArray().size else 3).put("browser_download_url", "https://github.com/everyonesoftware/storage/releases/download/v0.1.$n/$name") }))
 @Test fun cancellationBetweenDiscoveryCallsStopsBeforeNextPageOrManifest() {
  for (fullPage in listOf(false, true)) {
   MockWebServer().use { server ->
    val entries = if (fullPage) (101..200).map { release(it, false) } else listOf(release(4, true))
    server.enqueue(MockResponse().setBody(JSONArray(entries).toString()))
    // Without the check, this response would be consumed by page 2 or the manifest request.
    server.enqueue(MockResponse().setResponseCode(500))
    var cancelled = false
    lateinit var client: ReleaseClient
    client = ReleaseClient(server.url("/"), afterFetch = { cancelled = true; client.cancel() })
    assertThrows(java.util.concurrent.CancellationException::class.java) {
     client.latest(1, 1) { cancelled }
    }
    assertEquals(1, server.requestCount)
   }
  }
 }
 @Test fun cancellationBeforeRedirectHopStopsWithoutSecondRequest() {
  MockWebServer().use { server ->
   server.enqueue(MockResponse().setResponseCode(302).addHeader("Location", "https://release-assets.githubusercontent.com/test.apk"))
   server.enqueue(MockResponse().setResponseCode(500))
   val dir = kotlin.io.path.createTempDirectory().toFile()
   try {
    val f = ReleaseFile("Storage-Service-0.1.4.apk", 3, "b".repeat(64))
    assertThrows(java.util.concurrent.CancellationException::class.java) {
     ReleaseClient(server.url("/")).download(AndroidRelease(4, listOf(f)), f, java.io.File(dir, "update.apk"), { server.requestCount > 0 }) { _, _ -> }
    }
    assertEquals(1, server.requestCount)
    assertFalse(java.io.File(dir, "update.apk").exists())
   } finally { dir.deleteRecursively() }
  }
 }
 @Test fun cancelledDownloadNeverStartsARequest() {
  MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody("abc"))
   val dir = kotlin.io.path.createTempDirectory().toFile()
   try {
    val f = ReleaseFile("Storage-Service-0.1.4.apk", 3, "b".repeat(64))
    assertThrows(java.util.concurrent.CancellationException::class.java) {
     ReleaseClient(server.url("/")).download(AndroidRelease(4, listOf(f)), f, java.io.File(dir, "update.apk"), { true }) { _, _ -> }
    }
    assertEquals(0, server.requestCount)
   } finally { dir.deleteRecursively() }
  }
 }
 @Test fun malformedGitHubBooleanIsNotAStableRelease() {
  MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody(JSONArray(listOf(release(4, true).put("draft", "false"))).toString()))
   server.enqueue(MockResponse().setBody(inventory(4).toString()))
   assertThrows(IllegalStateException::class.java) { ReleaseClient(server.url("/")).latest(1, 1) }
  }
 }
 @Test fun rejectsExtraApkInMarkedPublication() {
  MockWebServer().use { server ->
   val r = release(4, true); r.getJSONArray("assets").put(JSONObject().put("name", "other.apk").put("size", 3).put("browser_download_url", "https://github.com/everyonesoftware/storage/releases/download/v0.1.4/other.apk"))
   server.enqueue(MockResponse().setBody(JSONArray(listOf(r)).toString())); server.enqueue(MockResponse().setBody(inventory(4).toString()))
   assertThrows(IllegalStateException::class.java) { ReleaseClient(server.url("/")).latest(1, 1) }
  }
 }
 @Test fun noUpdateAndMixedInstalledVersionsNeverDowngrade() {
  for ((service, browser) in listOf(4L to 4L, 5L to 1L, 1L to 5L)) {
   MockWebServer().use { server ->
    server.enqueue(MockResponse().setBody(JSONArray(listOf(release(4, true))).toString())); server.enqueue(MockResponse().setBody(inventory(4).toString()))
    assertNull(ReleaseClient(server.url("/")).latest(service, browser))
   }
  }
 }
 @Test fun downloadReportsExactProgressAndRejectsOverrunTruncationCancellation() {
  val digest = ReleaseClient.hex(java.security.MessageDigest.getInstance("SHA-256").digest("abc".toByteArray()))
  for (body in listOf("abc", "ab", "abcd")) {
   MockWebServer().use { server ->
    server.enqueue(MockResponse().setChunkedBody(body, 1))
    val dir = kotlin.io.path.createTempDirectory().toFile(); val file = java.io.File(dir, "update.apk")
    val f = ReleaseFile("Storage-Browser-0.1.4.apk", 3, digest); val values = mutableListOf<Long>()
    try {
     val action = { ReleaseClient(server.url("/")).download(AndroidRelease(4, listOf(f)), f, file, { false }) { n, total -> values.add(n); assertEquals(3L, total) } }
     if (body == "abc") { action(); assertEquals("abc", file.readText()); assertEquals(0L, values.first()); assertEquals(3L, values.last()) }
     else { assertThrows(IllegalStateException::class.java, action); assertFalse(file.exists()) }
    } finally { dir.deleteRecursively() }
   }
  }
 }
 @Test fun rateLimitUnsafeRedirectAndFalseExhaustionAreErrors() {
  for (response in listOf(MockResponse().setResponseCode(403), MockResponse().setResponseCode(302).addHeader("Location", "http://evil.invalid/"), MockResponse().setBody("[]").addHeader("Link", "<https://api.github.com/next>; rel=\"next\""))) {
   MockWebServer().use { server -> server.enqueue(response); assertThrows(IllegalStateException::class.java) { ReleaseClient(server.url("/")).latest(1, 1) } }
  }
 }
 @Test fun cancelInterruptsBlockedDownloadAndRemovesPartial() {
  MockWebServer().use { server ->
   server.enqueue(MockResponse().setSocketPolicy(okhttp3.mockwebserver.SocketPolicy.NO_RESPONSE))
   val dir = kotlin.io.path.createTempDirectory().toFile(); val file = java.io.File(dir, "update.apk")
   val client = ReleaseClient(server.url("/")); val f = ReleaseFile("Storage-Service-0.1.4.apk", 3, "b".repeat(64))
   val executor = java.util.concurrent.Executors.newSingleThreadExecutor()
   try {
    val task = executor.submit<Boolean> { try { client.download(AndroidRelease(4, listOf(f)), f, file, { false }) { _, _ -> }; false } catch (_: java.io.IOException) { true } }
    server.takeRequest(); client.cancel()
    assertTrue(task.get(3, java.util.concurrent.TimeUnit.SECONDS)); assertFalse(file.exists())
   } finally { executor.shutdownNow(); dir.deleteRecursively() }
  }
 }
 @Test fun rejectsCorruptManifestEvenWhenInstalledIsNewer() {
  MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody(JSONArray(listOf(release(4, true))).toString()))
   server.enqueue(MockResponse().setBody(inventory(4).put("signing", "unsigned").toString()))
   assertThrows(IllegalStateException::class.java) { ReleaseClient(server.url("/")).latest(6, 6) }
  }
 }
 @Test fun downloadChecksHashAndRemovesPartialOnFailure() {
  MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody("bad"))
   val dir = kotlin.io.path.createTempDirectory().toFile()
   val file = java.io.File(dir, "update.apk")
   try {
    val f = ReleaseFile("Storage-Service-0.1.4.apk", 3, "b".repeat(64))
    assertThrows(IllegalStateException::class.java) { ReleaseClient(server.url("/")).download(AndroidRelease(4, listOf(f)), f, file, { false }) { _, _ -> } }
    assertFalse(file.exists())
   } finally { dir.deleteRecursively() }
  }
 }
 @Test fun discoversAndroidOnLaterPageNotLatestWindows() {
  MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody(JSONArray((101..200).map { release(it, false) }).toString()))
   server.enqueue(MockResponse().setBody(JSONArray(listOf(release(4, true))).toString()))
   server.enqueue(MockResponse().setBody(inventory(4).toString()))
   val result = ReleaseClient(server.url("/")).latest(1, 1)!!
   assertEquals(4, result.count); assertEquals(2, result.apks.size)
   assertEquals("/repos/everyonesoftware/storage/releases?per_page=100&page=1", server.takeRequest().path)
   assertEquals("/repos/everyonesoftware/storage/releases?per_page=100&page=2", server.takeRequest().path)
   assertNull(server.takeRequest().getHeader("Authorization"))
  }
 }
}
