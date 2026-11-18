package com.everyonesoftware.storage.service
object CallerPolicy {
 fun allowed(packages: Set<String>, sameSigner: Boolean, permission: Boolean, ownUid: Boolean): Boolean = ownUid || (packages == setOf("com.everyonesoftware.storage.browser") && sameSigner && permission)
}
