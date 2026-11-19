package com.everyonesoftware.storage.browser.updates

import okhttp3.Authenticator
import okhttp3.CookieJar
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.concurrent.TimeUnit

internal data class ReleaseFile(val name: String, val bytes: Long, val sha256: String)
internal data class AndroidRelease(val count: Int, val files: List<ReleaseFile>, val sourceCommit: String = "") {
 val version get() = "0.1.$count"
 val apks get() = files.filter { it.name.endsWith(".apk") }
 fun url(name: String) = "https://github.com/everyonesoftware/storage/releases/download/v$version/$name"
}

/** Public, credential-free transport, isolated from Storage's service and all account state. */
internal class ReleaseClient(private val testOrigin: HttpUrl? = null, private val afterFetch: () -> Unit = {}) {
 private val http = OkHttpClient.Builder().authenticator(Authenticator.NONE).proxyAuthenticator(Authenticator.NONE)
  .cookieJar(CookieJar.NO_COOKIES).followRedirects(false).followSslRedirects(false)
  .connectTimeout(15, TimeUnit.SECONDS).readTimeout(15, TimeUnit.SECONDS).callTimeout(120, TimeUnit.SECONDS).build()
 init { require(testOrigin == null || testOrigin.host in setOf("127.0.0.1", "localhost", "::1") && testOrigin.scheme == "http") }
 fun changelog(base: String, target: String, cancelled: () -> Boolean = { false }): ChangeHistory {
  check(Regex("[0-9a-f]{40}").matches(base) && Regex("[0-9a-f]{40}").matches(target))
  val commits = mutableListOf<SourceChange>()
  var expected: Long? = null
  for (page in 1..20) {
   var hasNext = false
   val response = fetch("https://api.github.com/repos/everyonesoftware/storage/compare/$base...$target?per_page=100&page=$page", true, cancelled) {
    hasNext = Regex("rel\\s*=\\s*\"?next\\b", RegexOption.IGNORE_CASE).containsMatchIn(it.header("Link").orEmpty())
    JSONObject(text(it, 4 * 1024 * 1024, cancelled))
   }
   val total = number(response, "total_commits")
   check(total in 0..2000 && (expected == null || expected == total)) { "Incomplete comparison" }
   expected = total
   check(response.getJSONObject("base_commit").getString("sha") == base && response.getJSONObject("merge_base_commit").getString("sha") == base)
   check(response.getString("status") == if (base == target) "identical" else "ahead")
   val entries = response.getJSONArray("commits").objects()
   check(entries.size.toLong() == minOf(100L, total - commits.size)) { "Incomplete comparison page" }
   for (entry in entries) {
    val sha = entry.getString("sha"); val original = entry.getJSONObject("commit").getString("message")
    check(Regex("[0-9a-f]{40}").matches(sha) && original.isNotBlank() && commits.none { it.sha == sha })
    commits.add(SourceChange(sha, original))
   }
   if (commits.size.toLong() == total) {
    check(!hasNext && (base == target && total == 0L || commits.lastOrNull()?.sha == target)) { "Comparison target missing" }
    val detailed = commits.map { it.copy(paths = changedPaths(it.sha, cancelled)) }
    if (cancelled()) throw java.util.concurrent.CancellationException()
    return ChangeHistory(base, target, detailed)
   }
  }
  error("Comparison history limit reached")
 }
 private fun changedPaths(sha: String, cancelled: () -> Boolean): List<String> {
  val paths = mutableListOf<String>()
  val seen = mutableSetOf<String>()
  var metadataBytes = 0
  for (page in 1..10) {
   var hasNext = false
   val metadata = fetch("https://api.github.com/repos/everyonesoftware/storage/commits/$sha?per_page=100&page=$page", true, cancelled) { response ->
    hasNext = Regex("rel\\s*=\\s*\"?next\\b", RegexOption.IGNORE_CASE).containsMatchIn(response.header("Link").orEmpty())
    val raw = text(response, minOf(4 * 1024 * 1024, 8 * 1024 * 1024 - metadataBytes), cancelled)
    metadataBytes += raw.toByteArray(Charsets.UTF_8).size
    JSONObject(raw)
   }
   if (cancelled()) throw java.util.concurrent.CancellationException()
   check(metadata.getString("sha") == sha) { "Changed commit identity" }
   val entries = metadata.getJSONArray("files").objects()
   check(entries.size <= 100) { "Unexpected commit file page size" }
   for (entry in entries) {
    if (cancelled()) throw java.util.concurrent.CancellationException()
    val filename = entry.getString("filename")
    check(validChangedPath(filename) && seen.add(filename)) { "Invalid or repeated commit file" }
    paths.add(filename)
    if (entry.has("previous_filename")) {
     val previous = entry.getString("previous_filename")
     check(validChangedPath(previous)) { "Invalid rename source" }
     paths.add(previous)
    }
   }
   if (entries.size < 100) {
    check(!hasNext) { "Incomplete commit file page" }
    if (cancelled()) throw java.util.concurrent.CancellationException()
    return paths.toList()
   }
  }
  error("Commit file history limit reached")
 }
 private fun validChangedPath(path: String) = path.isNotBlank() && path.length <= 4096 &&
  !path.startsWith("/") && !path.contains('\\') && path.none { it.isISOControl() } &&
  path.split('/').none { it.isEmpty() || it == "." || it == ".." }
 fun installedSource(code: Long, version: String?, embedded: String, debuggable: Boolean, cancelled: () -> Boolean = { false }): String {
  check(!debuggable) { "Installed source unavailable for a development build" }
  if (Regex("[0-9a-f]{40}").matches(embedded)) return embedded
  check(embedded.isEmpty() && code in 1..65534 && version == "0.1.$code") { "Installed source identity unavailable" }
  // This tag locates the contract-defined publication only. Its manifest, not the tag, supplies the source SHA.
  val published = fetch("https://api.github.com/repos/everyonesoftware/storage/releases/tags/v$version", true, cancelled) {
   JSONObject(text(it, 4 * 1024 * 1024, cancelled))
  }
  check(published.get("draft") == false && published.get("prerelease") == false && published.optString("published_at").isNotBlank())
  check(published.getString("tag_name") == "v$version")
  return publication(published, code.toInt(), cancelled).sourceCommit
 }
 private fun publication(published: JSONObject, count: Int, cancelled: () -> Boolean): AndroidRelease {
  // Validate the selected publication even when no newer pair can be offered.
  val assets = published.getJSONArray("assets").objects()
  check(assets.map { it.getString("name") }.distinct().size == assets.size)
  check(assets.filter { it.getString("name").endsWith(".apk", ignoreCase = true) }.map { it.getString("name") }.toSet() ==
   setOf("Storage-Service-0.1.$count.apk", "Storage-Browser-0.1.$count.apk"))
  val manifestAsset = assets.single { it.getString("name") == "storage-android-release.json" }
  val url = AndroidRelease(count, emptyList()).url("storage-android-release.json")
  check(manifestAsset.getString("browser_download_url") == url)
  val raw = fetch(url, false, cancelled) { text(it, 64 * 1024, cancelled) }
  check(raw.toByteArray().size.toLong() == number(manifestAsset, "size"))
  val release = parse(raw)
  check(release.count == count)
  for (f in release.files) {
   val asset = assets.single { it.getString("name") == f.name }
   check(asset.getString("browser_download_url") == release.url(f.name) && number(asset, "size") == f.bytes)
  }
  return release
 }

 fun cancel() { http.dispatcher.cancelAll() }
 fun latest(serviceCode: Long, browserCode: Long, cancelled: () -> Boolean = { false }): AndroidRelease? {
  val candidates = mutableListOf<JSONObject>()
  var exhausted = false
  for (page in 1..20) {
   val entries = fetch("https://api.github.com/repos/everyonesoftware/storage/releases?per_page=100&page=$page", true, cancelled) { response ->
    val a = JSONArray(text(response, 4 * 1024 * 1024, cancelled))
    check(a.length() <= 100)
    check(a.length() == 100 || !Regex("rel\\s*=\\s*\"?next\\b", RegexOption.IGNORE_CASE).containsMatchIn(response.header("Link").orEmpty()))
    a
   }
   for (i in 0 until entries.length()) {
    val r = entries.getJSONObject(i)
    check(r.get("draft") is Boolean && r.get("prerelease") is Boolean)
    if (!r.getBoolean("draft") && !r.getBoolean("prerelease") && r.optString("published_at").isNotBlank() &&
     r.getJSONArray("assets").objects().any { it.getString("name") == "storage-android-release.json" }) candidates.add(r)
   }
   if (entries.length() < 100) { exhausted = true; break }
  }
  check(exhausted) { "Release history limit reached; cannot determine current Android release" }
  val releases = candidates.map { r ->
   val tag = r.getString("tag_name")
   check(Regex("v0\\.1\\.[1-9][0-9]{0,4}").matches(tag))
   val count = tag.removePrefix("v0.1.").toInt(); check(count in 1..65534)
   count to r
  }
  check(releases.map { it.first }.distinct().size == releases.size)
  val (count, published) = releases.maxByOrNull { it.first } ?: return null
  val release = publication(published, count, cancelled)
  if (count < serviceCode || count < browserCode || count.toLong() == serviceCode && count.toLong() == browserCode) return null
  return release
 }
 fun download(release: AndroidRelease, file: ReleaseFile, target: File, cancelled: () -> Boolean, progress: (Long, Long) -> Unit) {
  check(file in release.apks && release.count in 1..65534 && file.bytes in 1..512L * 1024 * 1024)
  check(file.name in setOf("Storage-Service-${release.version}.apk", "Storage-Browser-${release.version}.apk"))
  var owned = false
  try {
   check(target.createNewFile()); owned = true
   fetch(release.url(file.name), false, cancelled) { response ->
    val body = requireNotNull(response.body)
    check(body.contentLength() == -1L || body.contentLength() == file.bytes)
    val digest = java.security.MessageDigest.getInstance("SHA-256")
    var received = 0L
    body.byteStream().use { input -> target.outputStream().use { output ->
     val buffer = ByteArray(32 * 1024); progress(0, file.bytes)
     while (true) {
      if (cancelled()) throw java.util.concurrent.CancellationException()
      val n = input.read(buffer); if (n < 0) break
      check(received + n <= file.bytes)
      output.write(buffer, 0, n); digest.update(buffer, 0, n); received += n; progress(received, file.bytes)
     }
     check(received == file.bytes && hex(digest.digest()) == file.sha256)
    } }
   }
  } catch (e: Exception) { if (owned) target.delete(); throw e }
 }
 private fun text(response: Response, limit: Int, cancelled: () -> Boolean): String {
  val body = requireNotNull(response.body); check(body.contentLength() <= limit)
  return body.byteStream().use { input ->
   val output = java.io.ByteArrayOutputStream()
   val buffer = ByteArray(8192)
   while (true) {
    if (cancelled()) throw java.util.concurrent.CancellationException()
    val n = input.read(buffer); if (n < 0) break
    check(output.size() + n <= limit); output.write(buffer, 0, n)
   }
   output.toString("UTF-8")
  }
 }
 private fun <T> fetch(initial: String, api: Boolean, cancelled: () -> Boolean = { false }, consume: (Response) -> T): T {
  var url = initial.toHttpUrl()
  try {
  repeat(6) { hop ->
   if (cancelled()) throw java.util.concurrent.CancellationException()
   check(url.scheme == "https" && url.port == 443 && url.username.isEmpty() && url.password.isEmpty() && url.fragment == null)
   check(if (api) url.host == "api.github.com" && url.toString() == initial else
    url.host == "github.com" && url.encodedPath.startsWith("/everyonesoftware/storage/releases/download/") || url.host in setOf("release-assets.githubusercontent.com", "objects.githubusercontent.com"))
   val transport = testOrigin?.newBuilder()?.encodedPath(url.encodedPath)?.encodedQuery(url.encodedQuery)?.build() ?: url
   val request = Request.Builder().url(transport).header("User-Agent", "Storage-Android-Updater").header("Accept-Encoding", "identity").header("Accept", if (api) "application/vnd.github+json" else "application/octet-stream").build()
   val call = http.newCall(request)
   if (cancelled()) throw java.util.concurrent.CancellationException()
   call.execute().use { response ->
    if (response.code in setOf(301, 302, 303, 307, 308)) {
     check(hop < 5 && !api); url = requireNotNull(url.resolve(requireNotNull(response.header("Location"))))
    } else { check(response.isSuccessful) { "Public release request failed (${response.code})" }; return consume(response) }
   }
  }
  error("Redirect limit")
  } finally { afterFetch() }
 }
 companion object {
  fun hex(bytes: ByteArray) = bytes.joinToString("") { "%02x".format(it.toInt() and 255) }
  private fun JSONArray.objects() = (0 until length()).map { getJSONObject(it) }
  private fun number(obj: JSONObject, key: String): Long {
   val n = obj.get(key); check(n is Int || n is Long); return (n as Number).toLong()
  }
  fun parse(raw: String): AndroidRelease {
   val m = JSONObject(raw); check(number(m, "schemaVersion") == 1L && m.getString("platform") == "android" && m.getString("signing") == "release-key")
   val n = number(m, "count"); check(n in 1..65534)
   check(m.getString("version") == "0.1.$n" && m.getString("tag") == "v0.1.$n" && Regex("[0-9a-f]{40}").matches(m.getString("sourceCommit")))
   val files = m.getJSONArray("files").objects().map { f ->
    ReleaseFile(f.getString("name"), number(f, "bytes"), f.getString("sha256")).also { check(it.bytes in 1..(512L * 1024 * 1024) && Regex("[0-9a-f]{64}").matches(it.sha256)) }
   }
   check(files.size == 3 && files.map { it.name }.toSet() == setOf("Storage-Service-0.1.$n.apk", "Storage-Browser-0.1.$n.apk", "Storage-android-SHA256SUMS"))
   return AndroidRelease(n.toInt(), files, m.getString("sourceCommit"))
  }
 }
}
