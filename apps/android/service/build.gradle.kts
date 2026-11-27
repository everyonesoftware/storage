plugins { id("com.android.application"); id("org.jetbrains.kotlin.android") }
android {
 namespace = "com.everyonesoftware.storage.service"
 compileSdk = 36
 ndkVersion = "29.0.14206865"
 defaultConfig { applicationId = "com.everyonesoftware.storage.service"; minSdk = 26; targetSdk = 36; versionCode = 1; versionName = "0.1.0"; testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"; ndk { abiFilters += listOf("x86_64", "arm64-v8a") } }
 compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
 kotlinOptions { jvmTarget = "17" }
 sourceSets["main"].jniLibs.srcDir(layout.buildDirectory.dir("generated/nativeLibs"))
 sourceSets["main"].jniLibs.srcDir(layout.buildDirectory.dir("generated/descriptorLibs"))
}
val verifyNativeLibs by tasks.registering {
 doLast {
  if (!providers.gradleProperty("allowMissingNative").isPresent) listOf("x86_64", "arm64-v8a").forEach { abi -> require(rootProject.file("../../artifacts/native/android/$abi/libstorage_core.so").isFile) { "Missing native core for $abi; run repository scripts/build-native.sh first" } }
 }
}
val copyNativeLibs by tasks.registering(Sync::class) {
 dependsOn(verifyNativeLibs)
 val nativeRoot = rootProject.file("../../artifacts/native/android")
 from(nativeRoot) { include("x86_64/libstorage_core.so", "arm64-v8a/libstorage_core.so") }
 into(layout.buildDirectory.dir("generated/nativeLibs"))
}
// This tiny OS shim is independent of the prebuilt Rust engine. API 26–29 have no
// public Java fcntl setter; native O_NONBLOCK also closes poll/read races safely.
val compileDescriptorIo by tasks.registering {
 val source = layout.projectDirectory.file("src/main/c/descriptor_io.c")
 val destination = layout.buildDirectory.dir("generated/descriptorLibs")
 inputs.file(source)
 inputs.property("ndkVersion", android.ndkVersion ?: "")
 outputs.dir(destination)
 doLast {
  val os = System.getProperty("os.name").lowercase()
  val host = if (os.contains("windows")) "windows-x86_64" else if (os.contains("mac")) "darwin-x86_64" else "linux-x86_64"
  val suffix = if (os.contains("windows")) ".cmd" else ""
  val compilerRoot = android.ndkDirectory.resolve("toolchains/llvm/prebuilt/$host/bin")
  mapOf("x86_64" to "x86_64-linux-android26", "arm64-v8a" to "aarch64-linux-android26").forEach { (abi, target) ->
   val output = destination.get().file("$abi/libstorage_descriptor_io.so").asFile
   output.parentFile.mkdirs()
   providers.exec {
    commandLine(compilerRoot.resolve("$target-clang$suffix"), "-shared", "-fPIC", "-O2", "-Wall", "-Wextra", "-Werror", "-Wl,-z,max-page-size=16384", source.asFile, "-o", output)
   }.result.get().assertNormalExitValue()
  }
 }
}
tasks.matching { it.name.startsWith("merge") && it.name.endsWith("JniLibFolders") && !it.name.contains("AndroidTest") }.configureEach { dependsOn(copyNativeLibs, compileDescriptorIo) }
apply(from = rootProject.file("release.gradle"))
dependencies {
 implementation(project(":client"))
 implementation("androidx.activity:activity:1.11.0")
 val cameraVersion = "1.5.1"
 implementation("androidx.camera:camera-camera2:$cameraVersion")
 implementation("androidx.camera:camera-lifecycle:$cameraVersion")
 implementation("androidx.camera:camera-view:$cameraVersion")
 implementation("com.google.zxing:core:3.5.3")
 testImplementation("junit:junit:4.13.2")
 androidTestImplementation("androidx.test:runner:1.6.2")
 androidTestImplementation("androidx.test:rules:1.6.1")
 androidTestImplementation("androidx.test.ext:junit:1.2.1")
}
