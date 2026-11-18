package com.everyonesoftware.storage.client;
import android.os.ParcelFileDescriptor;
interface IStorage {
 String query(String operation, String arguments);
 String importFile(String name, String contentType, in ParcelFileDescriptor source);
 String addVersion(String itemId, long expectedRevision, in ParcelFileDescriptor source);
 ParcelFileDescriptor readVersion(String versionId);
}
