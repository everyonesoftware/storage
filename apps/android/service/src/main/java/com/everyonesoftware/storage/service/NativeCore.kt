package com.everyonesoftware.storage.service

object NativeCore {
 init { System.loadLibrary("storage_core") }
 @JvmStatic external fun execute(root: String, actorJson: String, commandJson: String): String
}
