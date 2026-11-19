package com.everyonesoftware.storage.browser.updates

internal data class SourceChange(val sha: String, val original: String, val paths: List<String> = emptyList())
internal data class ChangeHistory(val base: String, val target: String, val commits: List<SourceChange>) {
 val improvements get() = summaries.filter { !it.first }.map { it.second }
 val fixes get() = summaries.filter { it.first }.map { it.second }
 val changeCount get() = summaries.size
 private val summaries get() = commits.mapNotNull { change ->
  // Unknown/shared paths are relevant; subjects never establish platform relevance.
  if (change.paths.isNotEmpty() && change.paths.all(::androidIrrelevant)) return@mapNotNull null
  val subject = change.original.lineSequence().first().trim().replace(Regex("^(\\[verified]\\s*)+", RegexOption.IGNORE_CASE), "")
  val prefix = Regex("^([a-z]+)(?:\\(([^)]+)\\))?!?:\\s*(.+)$", RegexOption.IGNORE_CASE).matchEntire(subject)
  val type = prefix?.groupValues?.get(1)?.lowercase()
  val text = prefix?.groupValues?.get(3) ?: subject
  (type == "fix" || type == "bugfix") to text
 }
 private fun androidIrrelevant(path: String): Boolean = path.startsWith("apps/windows/", ignoreCase = true) ||
  path.startsWith("docs/", ignoreCase = true) || path.startsWith(".github/", ignoreCase = true) ||
  path.endsWith(".md", ignoreCase = true) ||
  Regex("^(?:scripts|tests)/[^/]*windows[^/]*$", RegexOption.IGNORE_CASE).matches(path) && !path.contains("android", ignoreCase = true)
}
