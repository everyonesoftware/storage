package com.everyonesoftware.storage.service

/** OS descriptor flags only; this is not a second repository engine. */
internal object DescriptorIo {
 init { System.loadLibrary("storage_descriptor_io") }
 @JvmStatic external fun makeNonblocking(fd: Int): Int
}
