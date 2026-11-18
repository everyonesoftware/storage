#include <errno.h>
#include <fcntl.h>
#include <jni.h>

/* Public Os.fcntlInt is API 30+, but descriptor imports also run on API 26–29.
 * No paths or repository operations cross this helper. */
JNIEXPORT jint JNICALL
Java_com_everyonesoftware_storage_service_DescriptorIo_makeNonblocking(
    JNIEnv *env, jclass klass, jint fd) {
    (void)env;
    (void)klass;
    int flags = fcntl(fd, F_GETFL);
    if (flags == -1) return errno;
    return fcntl(fd, F_SETFL, flags | O_NONBLOCK) == -1 ? errno : 0;
}
