package com.everyonesoftware.storage.browser.updates

import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class ChangelogTest {
 private val base = "a".repeat(40)
 private val target = "b".repeat(40)
 private fun commit(sha: String, message: String) = JSONObject().put("sha", sha).put("commit", JSONObject().put("message", message))
 private fun page(commits: List<JSONObject>, total: Int) = JSONObject().put("total_commits", total).put("status", "ahead")
  .put("base_commit", JSONObject().put("sha", base)).put("merge_base_commit", JSONObject().put("sha", base)).put("commits", JSONArray(commits)).toString()
 @Test fun classifiesRealSubjectsRetainingExactOriginalsAndSharedChanges() {
  val originals = listOf("[verified] feat(android): improve update layout", "fix: retain shared content", "docs: update guide", "ci: build packages", "fix(windows): taskbar icon", "feat: add Windows and Android updates", "refactor: simplify reads", "fix: repair Windows taskbar icons", "fix(core): handle Windows-compatible shared paths", "fix: normalize shared Windows paths")
  val history = ChangeHistory(base, target, originals.mapIndexed { i, text ->
   val path = when (i) { 2 -> "docs/guide.md"; 3 -> ".github/workflows/ci.yml"; 4, 7 -> "apps/windows/Storage.Browser/App.cs"; else -> "core/src/lib.rs" }
   SourceChange(i.toString().padStart(40, '0'), text, listOf(path))
  })
  assertEquals(listOf("improve update layout", "add Windows and Android updates", "simplify reads"), history.improvements)
  assertEquals(listOf("retain shared content", "handle Windows-compatible shared paths", "normalize shared Windows paths"), history.fixes)
  assertEquals(originals, history.commits.map { it.original })
 }
 private fun files(sha: String, paths: List<String>) = JSONObject().put("sha", sha)
  .put("files", JSONArray(paths.map { JSONObject().put("filename", it) })).toString()
 @Test fun neutralSubjectWithOnlyWindowsFilesIsExcludedButOriginalIsRetained() {
  MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody(page(listOf(commit(target, "fix: restore startup")), 1)))
   server.enqueue(MockResponse().setBody(files(target, listOf("apps/windows/Storage.Browser/App.cs"))))
   val history = ReleaseClient(server.url("/")).changelog(base, target)
   assertEquals(0, history.changeCount)
   assertEquals("fix: restore startup", history.commits.single().original)
   assertEquals(2, server.requestCount)
  }
 }
 @Test fun scopedSubjectsCannotExcludeSharedImplementationAndUnknownPaths() {
  val originals = listOf("fix(windows): retain data", "docs(core): explain behavior\n\nOriginal body", "ci: improve shared implementation", "fix: handle unknown files")
  val history = ChangeHistory(base, target, originals.mapIndexed { i, original ->
   SourceChange(i.toString().padStart(40, '0'), original, if (i == 3) emptyList() else listOf("core/src/lib.rs"))
  })
  assertEquals(listOf("retain data", "handle unknown files"), history.fixes)
  assertEquals(listOf("explain behavior", "improve shared implementation"), history.improvements)
  assertEquals(4, history.changeCount)
  assertEquals(originals, history.commits.map { it.original })
 }
 @Test fun collectsEveryFilePageAndBothSidesOfRenamesBeforeSummarizing() {
  MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody(page(listOf(commit(target, "fix(windows): preserve shared behavior")), 1)))
   server.enqueue(MockResponse().setBody(files(target, (1..100).map { "docs/$it.md" })))
   val last = JSONObject(files(target, listOf("apps/windows/new.cs")))
   last.getJSONArray("files").getJSONObject(0).put("previous_filename", "core/src/old.rs")
   server.enqueue(MockResponse().setBody(last.toString()))
   // Unexpected requests fail immediately, never wait for MockWebServer's default timeout.
   server.enqueue(MockResponse().setResponseCode(404))
   val history = ReleaseClient(server.url("/")).changelog(base, target)
   assertEquals(listOf("preserve shared behavior"), history.fixes)
   assertEquals(102, history.commits.single().paths.size)
   assertTrue(history.commits.single().paths.contains("core/src/old.rs"))
   assertEquals(3, server.requestCount)
   server.takeRequest(); server.takeRequest()
   val request = server.takeRequest(1, java.util.concurrent.TimeUnit.SECONDS)!!
   assertEquals("/repos/everyonesoftware/storage/commits/$target?per_page=100&page=2", request.path)
   assertNull(request.getHeader("Authorization")); assertNull(request.getHeader("Cookie"))
  }
 }
 @Test fun malformedOrIncompleteFileMetadataNeverProducesAFalseZero() {
  val invalid = listOf(
   JSONObject(files(base, listOf("docs/guide.md"))).toString(),
   JSONObject().put("sha", target).toString(),
   files(target, listOf("")),
   files(target, listOf("docs/a.md", "docs/a.md")),
   JSONObject(files(target, listOf("docs/new.md"))).also { it.getJSONArray("files").getJSONObject(0).put("previous_filename", "") }.toString(),
   files(target, (1..101).map { "docs/$it.md" })
  )
  for (raw in invalid) MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody(page(listOf(commit(target, "docs: guide")), 1)))
   server.enqueue(MockResponse().setBody(raw))
   server.enqueue(MockResponse().setResponseCode(404))
   assertThrows(Exception::class.java) { ReleaseClient(server.url("/")).changelog(base, target) }
  }
  MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody(page(listOf(commit(target, "docs: guide")), 1)))
   server.enqueue(MockResponse().setBody(files(target, listOf("docs/a.md"))).addHeader("Link", "<next>; rel=\"next\""))
   server.enqueue(MockResponse().setResponseCode(404))
   assertThrows(IllegalStateException::class.java) { ReleaseClient(server.url("/")).changelog(base, target) }
  }
 }
 @Test fun cancellationAfterLastMetadataResponseCannotPublishNotes() {
  MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody(page(listOf(commit(target, "fix: retain data")), 1)))
   server.enqueue(MockResponse().setBody(files(target, listOf("core/src/lib.rs"))))
   var responses = 0
   val client = ReleaseClient(server.url("/"), afterFetch = { responses++ })
   assertThrows(java.util.concurrent.CancellationException::class.java) { client.changelog(base, target) { responses >= 2 } }
   assertEquals(2, server.requestCount)
  }
 }
 @Test fun filePageAndMetadataBudgetsFailUnavailableRatherThanReturningPartialNotes() {
  for (oversizedMetadata in listOf(false, true)) MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody(page(listOf(commit(target, "docs: guide")), 1)))
   for (number in 1..10) {
    val raw = JSONObject(files(target, (1..100).map { "docs/$number-$it.md" }))
    if (oversizedMetadata) raw.put("padding", "x".repeat(3 * 1024 * 1024))
    server.enqueue(MockResponse().setBody(raw.toString()).addHeader("Link", "<next>; rel=\"next\""))
   }
   server.enqueue(MockResponse().setResponseCode(404))
   assertThrows(IllegalStateException::class.java) { ReleaseClient(server.url("/")).changelog(base, target) }
   assertEquals(if (oversizedMetadata) 4 else 11, server.requestCount)
  }
 }
 @Test fun cancellationBetweenFilePagesStopsBeforeRequestingMoreMetadata() {
  MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody(page(listOf(commit(target, "fix: preserve data")), 1)))
   server.enqueue(MockResponse().setBody(files(target, (1..100).map { "docs/$it.md" })).addHeader("Link", "<next>; rel=\"next\""))
   server.enqueue(MockResponse().setResponseCode(404))
   var responses = 0
   val client = ReleaseClient(server.url("/"), afterFetch = { responses++ })
   assertThrows(java.util.concurrent.CancellationException::class.java) { client.changelog(base, target) { responses >= 2 } }
   assertEquals(2, server.requestCount)
  }
 }
 @Test fun unknownFilesAreConservativeAndRenamesIntoSharedPathsAreRelevant() {
  for (paths in listOf(emptyList(), listOf("core/src/new.rs"))) MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody(page(listOf(commit(target, "fix(windows): retain data")), 1)))
   val metadata = JSONObject(files(target, paths))
   if (paths.isNotEmpty()) metadata.getJSONArray("files").getJSONObject(0).put("previous_filename", "apps/windows/old.cs")
   server.enqueue(MockResponse().setBody(metadata.toString()))
   server.enqueue(MockResponse().setResponseCode(404))
   val history = ReleaseClient(server.url("/")).changelog(base, target)
   assertEquals(listOf("retain data"), history.fixes)
   assertEquals(1, history.changeCount)
  }
 }
 @Test fun installedSourceUsesEmbeddedIdentityWithoutTreatingVersionsAsTags() {
  MockWebServer().use { server ->
   assertEquals(base, ReleaseClient(server.url("/")).installedSource(7, "0.1.7", base, false))
   assertEquals(0, server.requestCount)
  }
 }
 @Test fun debugAndUnmappedInstalledVersionsAreHonestlyUnavailableWithoutNetwork() {
  MockWebServer().use { server ->
   val client = ReleaseClient(server.url("/"))
   assertThrows(IllegalStateException::class.java) { client.installedSource(1, "0.1.0", "", true) }
   assertThrows(IllegalStateException::class.java) { client.installedSource(7, "unknown", "", false) }
   assertEquals(0, server.requestCount)
  }
 }
 @Test fun installedPublicationResolvesItsManifestSourceNotTheTag() {
  MockWebServer().use { server ->
   val n = 7
   val manifest = JSONObject().put("schemaVersion", 1).put("platform", "android").put("signing", "release-key")
    .put("count", n).put("version", "0.1.$n").put("tag", "v0.1.$n").put("sourceCommit", base)
    .put("files", JSONArray(listOf("Storage-Service-0.1.$n.apk", "Storage-Browser-0.1.$n.apk", "Storage-android-SHA256SUMS").map { JSONObject().put("name", it).put("bytes", 3).put("sha256", "c".repeat(64)) }))
   val assets = (0 until 3).map { manifest.getJSONArray("files").getJSONObject(it).getString("name") } + "storage-android-release.json"
   val publication = JSONObject().put("tag_name", "v0.1.$n").put("draft", false).put("prerelease", false).put("published_at", "2026-11-18T00:00:00Z")
    .put("assets", JSONArray(assets.map { name -> JSONObject().put("name", name).put("size", if (name.endsWith(".json")) manifest.toString().toByteArray().size else 3).put("browser_download_url", "https://github.com/everyonesoftware/storage/releases/download/v0.1.$n/$name") }))
   server.enqueue(MockResponse().setBody(publication.toString()))
   server.enqueue(MockResponse().setBody(manifest.toString()))
   assertEquals(base, ReleaseClient(server.url("/")).installedSource(7, "0.1.7", "", false))
   assertEquals("/repos/everyonesoftware/storage/releases/tags/v0.1.7", server.takeRequest().path)
   assertEquals("/everyonesoftware/storage/releases/download/v0.1.7/storage-android-release.json", server.takeRequest().path)
  }
 }
 @Test fun staleNotesCannotDescribeADifferentOfferOrOverwriteARetry() {
  val first = AndroidRelease(7, emptyList(), target)
  val second = AndroidRelease(8, emptyList(), "c".repeat(40))
  val history = ChangeHistory(base, target, listOf(SourceChange(target, "fix: retain data")))
  val state = UpdateState(release = first).beginNotes(1)
  assertSame(state, state.completeNotes(second, 1, history, null))
  val retry = state.beginNotes(2)
  assertSame(retry, retry.completeNotes(first, 1, history, null))
  assertEquals(history, retry.completeNotes(first, 2, history, null).notes.history)
 }
 @Test fun unavailableNotesKeepAValidDownloadAndRetryIndependent() {
  val offer = AndroidRelease(7, emptyList(), target)
  val state = UpdateState(release = offer, nextPackage = SERVICE_PACKAGE).beginNotes(1)
   .completeNotes(offer, 1, null, "Installed source identity is unavailable.")
  assertEquals(offer, state.release)
  assertEquals(SERVICE_PACKAGE, state.nextPackage)
  assertFalse(state.busy)
  assertEquals("Installed source identity is unavailable.", state.notes.unavailable)
  assertNull(state.notes.history)
  assertTrue(state.beginNotes(2).notes.loading)
 }
 @Test fun incompleteDivergedDuplicateWrongBaseAndWrongTargetHistoriesAreUnavailable() {
  val valid = JSONObject(page(listOf(commit(target, "fix: retain data")), 1))
  val invalid = listOf(
   JSONObject(valid.toString()).put("status", "diverged"),
   JSONObject(valid.toString()).put("total_commits", 2),
   JSONObject(valid.toString()).put("total_commits", 2001),
   JSONObject(valid.toString()).put("merge_base_commit", JSONObject().put("sha", target)),
   JSONObject(valid.toString()).put("base_commit", JSONObject().put("sha", target)),
   JSONObject(valid.toString()).put("commits", JSONArray(listOf(commit(base, "fix: wrong endpoint")))),
   JSONObject(valid.toString()).put("total_commits", 2).put("commits", JSONArray(listOf(commit(target, "one"), commit(target, "two"))))
  )
  for (response in invalid) MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody(response.toString()))
   assertThrows(IllegalStateException::class.java) { ReleaseClient(server.url("/")).changelog(base, target) }
  }
 }
 @Test fun rateLimitFalseExhaustionAndOversizedHistoryAreNotEmptySummaries() {
  for (response in listOf(MockResponse().setResponseCode(403), MockResponse().setBody(page(listOf(commit(target, "fix: preserve data")), 1)).addHeader("Link", "<next>; rel=\"next\""), MockResponse().setBody("x".repeat(4 * 1024 * 1024 + 1)))) {
   MockWebServer().use { server ->
    server.enqueue(response)
    assertThrows(IllegalStateException::class.java) { ReleaseClient(server.url("/")).changelog(base, target) }
   }
  }
 }
 @Test fun cancellationBetweenComparePagesStopsBeforeTheNextRequest() {
  MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody(page((1..100).map { commit(it.toString(16).padStart(40, '0'), "feat: shared $it") }, 101)))
   var cancelled = false
   val client = ReleaseClient(server.url("/"), afterFetch = { cancelled = true })
   assertThrows(java.util.concurrent.CancellationException::class.java) { client.changelog(base, target) { cancelled } }
   assertEquals(1, server.requestCount)
  }
 }
 @Test fun sameSourceProducesAnHonestCompleteEmptyHistory() {
  MockWebServer().use { server ->
   server.enqueue(MockResponse().setBody(JSONObject(page(emptyList(), 0)).put("status", "identical").toString()))
   val history = ReleaseClient(server.url("/")).changelog(base, base)
   assertEquals(0, history.changeCount)
   assertTrue(history.improvements.isEmpty())
   assertTrue(history.fixes.isEmpty())
  }
 }
 @Test fun malformedAndExplicitlyUnknownSourceNeverFallBackToVersionTags() {
  MockWebServer().use { server ->
   val client = ReleaseClient(server.url("/"))
   for (source in listOf("unknown", "v0.1.7", "HEAD", "abc", "A".repeat(40))) {
    assertThrows(IllegalStateException::class.java) { client.installedSource(7, "0.1.7", source, false) }
   }
   assertEquals(0, server.requestCount)
  }
 }
 @Test fun offerReplacementClearsNotesEvenWhenTheVersionNumberIsTheSame() {
  val old = AndroidRelease(7, emptyList(), base)
  val state = UpdateState(release = old).beginNotes(4)
  val replaced = state.withOffer(old.copy(sourceCommit = target))
  assertEquals(ChangelogState(), replaced.notes)
  assertSame(replaced, replaced.completeNotes(old, 4, ChangeHistory(base, target, emptyList()), null))
 }
 @Test fun comparesExactSourceRevisionsAcrossEveryPageAndSkippedRelease() {
  MockWebServer().use { server ->
   val first = (1..300).map { commit(it.toString(16).padStart(40, '0'), "feat: shared improvement $it") }
   first.chunked(100).forEach { server.enqueue(MockResponse().setBody(page(it, 301)).addHeader("Link", "<next>; rel=\"next\"")) }
   server.enqueue(MockResponse().setBody(page(listOf(commit(target, "fix(android): retain downloads")), 301)))
   (first + commit(target, "fix(android): retain downloads")).forEach {
    server.enqueue(MockResponse().setBody(files(it.getString("sha"), listOf("core/src/lib.rs"))))
   }
   server.enqueue(MockResponse().setResponseCode(404))
   val notes = ReleaseClient(server.url("/")).changelog(base, target)
   assertEquals(301, notes.changeCount)
   assertEquals(305, server.requestCount)
   assertEquals(301, notes.commits.size)
   assertEquals(base, notes.base)
   assertEquals(target, notes.target)
   assertEquals("fix(android): retain downloads", notes.commits.last().original)
   for (number in 1..4) {
    val request = server.takeRequest(1, java.util.concurrent.TimeUnit.SECONDS)!!
    assertEquals("/repos/everyonesoftware/storage/compare/$base...$target?per_page=100&page=$number", request.path)
    assertNull(request.getHeader("Authorization"))
    assertNull(request.getHeader("Cookie"))
   }
   for (change in notes.commits) {
    val request = server.takeRequest(1, java.util.concurrent.TimeUnit.SECONDS)!!
    assertEquals("/repos/everyonesoftware/storage/commits/${change.sha}?per_page=100&page=1", request.path)
    assertNull(request.getHeader("Authorization"))
    assertNull(request.getHeader("Cookie"))
   }
  }
 }
}
