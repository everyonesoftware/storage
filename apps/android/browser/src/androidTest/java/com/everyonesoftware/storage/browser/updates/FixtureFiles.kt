package com.everyonesoftware.storage.browser.updates

import java.io.File
import java.security.MessageDigest

/** Real APK fixtures exceed small emulator heaps; retain bounded memory just like the installer. */
internal fun fixtureSha256(file: File): String {
 val digest = MessageDigest.getInstance("SHA-256")
 file.inputStream().use { input ->
  val buffer = ByteArray(32 * 1024)
  while (true) {
   val count = input.read(buffer)
   if (count < 0) break
   digest.update(buffer, 0, count)
  }
 }
 return digest.digest().joinToString("") { "%02x".format(it) }
}
