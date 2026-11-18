plugins { id("com.android.application"); id("org.jetbrains.kotlin.android") }
android {
 namespace = "com.everyonesoftware.storage.denied"; compileSdk = 36
 defaultConfig { applicationId = "com.everyonesoftware.storage.denied"; minSdk = 26; targetSdk = 36; versionCode = 1; versionName = "0.1.0"; testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner" }
 compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
 kotlinOptions { jvmTarget = "17" }
}
dependencies { implementation(project(":client")); androidTestImplementation("androidx.test:runner:1.6.2"); androidTestImplementation("androidx.test.ext:junit:1.2.1") }
