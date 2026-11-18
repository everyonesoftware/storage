package com.everyonesoftware.storage.browser.updates

internal const val SERVICE_PACKAGE = "com.everyonesoftware.storage.service"
internal const val BROWSER_PACKAGE = "com.everyonesoftware.storage.browser"
internal data class ApkIdentity(val packageName: String, val code: Long, val version: String?, val signers: List<String>, val debuggable: Boolean)
internal class SigningIdentityMismatch : IllegalStateException("This update uses a different signing key. Use APKs signed with the installed identity; do not uninstall Storage Service or clear its data.")
internal object UpdatePolicy {
 fun plan(api: Int, count: Int, service: ApkIdentity, browser: ApkIdentity): List<String> {
  check(service.packageName == SERVICE_PACKAGE && browser.packageName == BROWSER_PACKAGE)
  check(service.signers.isNotEmpty() && service.signers.all { it.isNotEmpty() } && service.signers.sorted() == browser.signers.sorted())
  check(count >= service.code && count >= browser.code) { "A newer product is already installed" }
  val pending = listOf(service, browser).filter { it.code < count }.map { it.packageName }
  check(api >= 26)
  // Public user-consent sessions are deliberately sequential on every supported Android.
  // Browser becomes eligible only after PackageManager observes Service at the selected code.
  return pending.take(1)
 }
 fun verify(count: Int, expectedPackage: String, installed: ApkIdentity, archive: ApkIdentity) {
  check(installed.packageName == expectedPackage && archive.packageName == expectedPackage)
  check(archive.code == count.toLong() && archive.code >= installed.code && archive.version == "0.1.$count")
  check(!archive.debuggable || installed.debuggable)
  check(archive.signers.isNotEmpty() && archive.signers.all { it.isNotEmpty() })
  if (archive.signers.sorted() != installed.signers.sorted()) throw SigningIdentityMismatch()
 }
}
/** isSealed is available on all supported APIs (26+) and becomes true at OS commit, not at prepare.
 * The OS bit resolves death after durable 'committing' but before/after the Binder commit call.
 */
internal object UpdateRecovery {
 fun keep(phase: String?, exists: Boolean, sealed: Boolean, confirmation: Boolean) =
  exists && (sealed || phase == "committed" || confirmation)
}
internal class UpdateGate {
 private val active = java.util.concurrent.atomic.AtomicBoolean(false)
 @Volatile var cancelled = false
  private set
 fun enter(): Boolean {
  if (!active.compareAndSet(false, true)) return false
  cancelled = false
  return true
 }
 fun cancel() { cancelled = true }
 fun checkActive() { if (cancelled) throw java.util.concurrent.CancellationException() }
 fun leave() { active.set(false) }
}
