package com.everyonesoftware.storage.client

import android.content.*
import android.os.IBinder
import java.util.concurrent.CompletableFuture

/** Only logical commands and descriptors cross this API. Never load the native engine here. */
class StorageConnection(private val context: Context) : AutoCloseable {
 private val lock = Any()
 private var current: Binding? = null
 private inner class Binding : ServiceConnection {
  var pending = CompletableFuture<IStorage>()
  var bound = false
  override fun onServiceConnected(name: ComponentName, binder: IBinder) = synchronized(lock) {
   if (current === this) pending.complete(IStorage.Stub.asInterface(binder))
   Unit
  }
  override fun onServiceDisconnected(name: ComponentName) = synchronized(lock) {
   if (current === this) {
    pending.completeExceptionally(IllegalStateException("Storage service disconnected"))
    pending = CompletableFuture()
   }
  }
  override fun onNullBinding(name: ComponentName) = synchronized(lock) {
   if (current === this) release(this, "Storage service rejected binding")
  }
  override fun onBindingDied(name: ComponentName) = synchronized(lock) {
   if (current === this) release(this, "Storage service stopped; reconnect")
  }
 }
 private fun release(binding: Binding, message: String) {
  current = null
  binding.pending.completeExceptionally(IllegalStateException(message))
  if (binding.bound) {
   binding.bound = false
   context.unbindService(binding)
  }
 }
 fun connect(): CompletableFuture<IStorage> = synchronized(lock) {
  current?.let { return@synchronized it.pending }
  val binding = Binding()
  current = binding
  try {
   binding.bound = context.bindService(Intent().setComponent(ComponentName("com.everyonesoftware.storage.service", "com.everyonesoftware.storage.service.StorageService")), binding, Context.BIND_AUTO_CREATE)
   if (!binding.bound) release(binding, "Install and enable Storage Service first")
  } catch (e: Exception) {
   current = null
   binding.pending.completeExceptionally(e)
  }
  binding.pending
 }
 override fun close() = synchronized(lock) {
  current?.let { release(it, "Storage connection closed") }
  Unit
 }
}
