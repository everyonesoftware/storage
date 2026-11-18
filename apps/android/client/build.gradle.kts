plugins { id("com.android.library"); id("org.jetbrains.kotlin.android") }
android {
 namespace = "com.everyonesoftware.storage.client"; compileSdk = 36
 defaultConfig { minSdk = 26 }
 buildFeatures { aidl = true }
 compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
 kotlinOptions { jvmTarget = "17" }
}
